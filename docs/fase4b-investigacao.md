# Fase 4B: XP e loot no cliente (investigação)

Investigação estática, só leitura (dump.cs, script.json, Ghidra headless com `-readOnly` numa cópia do projeto e ApiDump no interop). RVAs relativos a `0x180000000`. As saídas brutas ficaram em `tools/decompiled_4b_a_{raw,named}.txt` e `tools/decompiled_4b_b_{raw,named}.txt` (ignoradas no git). Nenhum código do mod foi alterado.

Notação: `Entity+0x370/0x378` é `Entity.Stats` (`StatsComponentReference`: `HasReference` em +0, `Reference` em +8). Do mesmo jeito: `+0x3B0/0x3B8` é `Inventory`, `+0x430/0x438` é `Runes`, `+0x350/0x358` é `Loot`, `+0x340/0x348` é `Experience` e `+0x2C0/0x2C8` é `Health`.

---

## 1. `LootUtil.ProcessLoot` (0x6FA180) por dentro

Assinatura: `ProcessLoot(int experience, float prestige, PlayerProfileCurrencies currencies, List<ItemDropRuntimeData> possibleItems, List<PowerUpRequest> powerUps, Vector3 sourcePosition, bool instant, int experienceInterval = 100)`.

### Quem chama, e com quais argumentos

Xrefs de 0x6FA180: só 5 funções.

| Chamador | RVA | Argumentos |
|---|---|---|
| `UnitUtil.ChangeEntityHealth` (morte) | 0x73BE30, call em 0x73DED6 | `Loot.Experience` (+0x20), `Loot.Prestige` (+0x24), `Loot.Currencies` (+0x28), `Loot.PossibleItems` (+0x48), `Loot.PowerUpRequests` (+0x38), posição do morto, `instant = false`, `interval = 100` (lido no assembly). Só roda se `HasBeenKilled && yieldLoot && Entity.Loot.HasReference`. |
| `InteractableRewardObject.UpdateAll` (baús) | 0x456A50 | Ao consumir o trigger: (1) `ProcessLoot(0, _prestigeToYield, _currenciesToYield, null, _powerUpsToYield, pos, instant = true, 100)`. (2) `_experienceToYield` em pedaços de ≤100, cada um com `ProcessLoot(xp, 0, null, null, null, pontoAleatório, false, 5)`. (3) Para cada `_mineralsEquivalentToYield`: `GameManagerMineralSpawnComponent.GetMineralPrefabAccordingToTier`, depois `LootComponent.Setup` no prefab e por fim `ProcessLoot(loot…, instant = true)`. |
| `LootDropperByHealthPercentComponent.UpdateAll` | 0x7C3150 | Para cada `HealthPercentInterval` de vida perdida: `ProcessLoot(Loot.Experience × DropPercentPerInterval × n, …)`. É o drop contínuo de XP dos bosses. |
| `SoulstoneItem.<RunBreakdown>d__20.MoveNext` | 0x4835A0 | Ao coletar uma soulstone grande: credita as moedas no `Inventory` de quem coletou e, se a runa `SoulstonesToMineralsSpawnAmountRange` (Runes+0xE0) existir, gera minerais com `ProcessLoot(…)`. **É loot pessoal de quem coletou.** |
| `GameCommands.SpawnSoulstone` | 0x7A1D40 | Cheat. |

`XpDropper.Drop` só tem mais dois chamadores diretos, ambos cheats: `CheatManager.DropXp` 0x6D3AB0 e `GameCommands…DropXPLoop` 0x7ABF30. `SpawnPowerUpRequestLoot` tem `ProcessLoot` e `CrusadeGameManager.UpdateAll` (0x4117E0). Ou seja, **todo o loot normal do modo Survivors passa por `ProcessLoot`**.

### O que ele faz, em ordem

1. **XP:** só se `experience > 0 && !GameManagerUtil.ReachedMaximumLevel` (static +0x8). Limpa `LootUtil._queryList` e chama `AIUtil.GetAllEntitiesInSpecificTeam(Players, _queryList)` (0x804170). Para **cada** entidade da lista, lê `Runes.Reference` (+0x438) **sem olhar `HasReference`** e, se `RunesComponent.ExtraExperienceMultiplier` (+0xB8) > 0, faz `experience *= ExtraExperienceMultiplier` (o multiplicador acumula por jogador). No fim faz **um** `XpDropper.Drop(sourcePosition, experience, experienceInterval)`.
2. **Prestige:** se > 0, `prestige *= (1 + GameData.AffixGameplayData(+0x130)→+0x98→+0x14)`. Depois, para cada entidade de time Players: lê `Runes.Reference` (sem checar) e aplica `prestige *= 1 + ExtraPrestigePercentage` (+0x84). Em seguida lê `Inventory.Reference`→`InventoryData` e faz `Prestige += prestige`, **direto no inventário, sem pickup**.
3. **Moedas** (se `currencies != null`):
   - `MinorSoulstones > 0`:
     - `instant`: para cada jogador com `Inventory.HasReference`, lê `Stats.Reference` (sem checar) e calcula o multiplicador `(stat+0x5C da base × stat+0x5C dos multiplicativos + 1 + afixo+0x10)`. O resultado vai somado em `InventoryData.MinorSoulstones`.
     - Senão: `MinorSoulstoneProjectile.FireProjectileAtEntities(_queryList, pos, minor)` (0x54F780), que dispara projéteis **para cada jogador** da lista: o total `minor × multiplicador` é dividido em ~minor/3 projéteis por jogador. O projétil credita no `Inventory` do alvo quando chega (`DoOnReachedTarget` 0x54F6C0).
   - Soulstone grande: escolhe **um** prefab, o primeiro tipo com valor > 0 na ordem Corrupted, Vile, Wicked, Hateful, Rogue (estáticos `LootUtil._corruptedSoulstone`…`_rogueSoulstone`, carregados em `PreloadAndSetup` de `Soulstones/*-Soulstone`). Instancia no chão (`ResourcesUtil.GetPrefabInstance<SoulstoneItem>` em `NavMesh.SamplePosition`) e roda `SoulstoneItem.Setup(currencies)` (0x480540) como corrotina no `JobRunner`. A coleta é feita por `SoulstoneItem.OnTriggerEnter` (0x4802B0), que só aceita `Entity.IsPlayer()`, e `OnCollect` (0x480050), que credita em quem coletou e também dispara `GameManagerUtil.CollectAllCollectablesAfterDelay`, o "ímã" de tudo.
4. **Itens** (`possibleItems`): para cada `ItemDropRuntimeData {ItemDefinition, Chance, QuantityMin, QuantityMax}`:
   - Rola `RandomUtils.Range(0..1) ≤ Chance` e `qty = RandomUtils.Range(QuantityMin, QuantityMax)`. `RandomUtils` usa o `Random` global da Unity, sem seed por drop.
   - Calcula um multiplicador por raridade a partir de `AffixGameplayData` (+0x98/+0xA0, campos 0x18–0x2C, conforme `ItemDefinition.ItemType`/`Rarity`).
   - Para **cada** jogador de time Players aplica `(1 + Runes.ExtraMaterialsPercentage +0x80)`, este sim checando `HasReference`, e `(1 + stat +0x60 ou +0x64)`, também checando.
   - `instant`: `LootUtil.AddItemToEntity(jogador, ItemId, qty × mult)` (0x6F9F00). Senão: `qty` vezes `ItemProjectile.FireProjectile(pos, jogador, ItemId, mult)` (0x54BCC0), e o projétil soma em `InventoryData.Items` quando chega (`DoOnReachedTarget` 0x54B920).
5. **Power-ups:** para cada `PowerUpRequest`: `SpawnPowerUpRequestLoot(req, pos)` (0x6FB110). O prefab sai de `Global.PowerUpCollection` (`PowerUpCollectionSO`):
   - `PlayerLevel > 0`: `SkillItem` (+0x40) se o nível for múltiplo de 3, senão `RandomPassiveItem` (+0x38).
   - Senão, pela `SpecialSource`: bosses (`& 0xFCF0000`) dão `BossItem` (+0x48); `VoidFields` (0x400) dá `SurvivorsBossItem` (+0x50); `VoidKing` dá +0x60; `CorruptedVoidHunters` dá +0x58; `GuardedReward` (2) dá `BonusTaskItem` (+0x68). Qualquer outro valor cai em +0x38 e loga um erro.
   - O prefab é um `CollectableObject` cujo tipo exato deve ser `CollectablePowerUpRequest`; o jogo chama `SetupPowerUpRequest(req)` (virtual, slot 8).

### O que ele lê do jogador sem checar (complemento ao hack do time Summons, fase 3)

- O caminho do **XP** lê `Runes.Reference` sem `HasReference`. É **esse** o primeiro NRE com o proxy Dummy (que não tem `RunesComponent`), antes mesmo de chegar ao Inventory.
- O caminho do **prestige** lê `Runes.Reference` e `Inventory.Reference`, os dois sem checar.
- As **moedas instant** checam `Inventory.HasReference`, mas leem `Stats.Reference` sem checar.
- Os itens e o `MinorSoulstoneProjectile` checam tudo.

Conclusão: qualquer Entity de time Players sem Runes, Inventory ou Stats derruba o `ProcessLoot`. Isso vale nos **dois** mundos. No cliente hoje não existe nenhuma (o fantasma não é Entity), mas se alguma fase criar um proxy do host no cliente, o hack do `HostLootProxyTeamPatch` precisa ser espelhado lá.

### De onde vêm os valores

- `EnemyPrefabData` (struct): `Experience` (+0x8), `Prestige` (+0xC), `Currencies` (+0x18), com overrides em `DataPerTier[]` (`EnemyPrefabDataPerTier.OverrideExperience/Experience`, etc.).
- `UnitUtil.SetEntityBasedOnPrefabData` (0x749BF0) copia o registro de runtime (`_prefabDatas[PrefabDataIndex]`, entradas de 0x40 bytes) para o `LootComponent`: `Experience` (+0x20), `Prestige` (+0x24) e `Currencies.Copy` (+0x28).
- `ApplyGenericEnemyScaling` (0x7387C0) **não** mexe no loot, só nos stats.
- `LootComponent.Setup` (0x7C2F60), chamado por `Entity.Setup`: `TierAtSpawnTime = GameManagerUtil.GetCurrentTierRating()` (0x6ED380), depois `CalculatePossibleItems` (0x7C2AD0). Este escolhe o `ItemDropByTier` com `Tier == TierAtSpawnTime` e converte cada `ItemDropData {ItemId +0x10, Chance +0x14, Min +0x18, Max +0x1C}` em `ItemDropRuntimeData`, com `ItemDefinition = Global.ItemCollection.GetItemDefinitionById(ItemId)` (0x5005E0).
- `PowerUpRequests` vem serializada no prefab (elites e bosses).
- `LootComponent.CopyLoot` (0x7C2D50) é usado pelo `GameManagerLootEnemySpawnerComponent` (0x80F2E0), o goblin de loot, que copia o loot de outro inimigo.

---

## 2. Replicar no cliente

### Chamar `ProcessLoot` local com os mesmos argumentos: é seguro?

Sim, desde que valham estas condições:
1. Chamar na main thread, depois do `StartMatch` local (os prefabs de soulstone e o pool de XP já carregados) e fora de qualquer `UpdateAll`. Basta processar a fila no tick do mod, como os Spawn da fase 2.
2. Não pode existir Entity de time Players sem Runes, Inventory ou Stats no mundo do cliente (ver acima).
3. Usar uma flag de reentrância `ReplayingNetworkLoot`, para os patches do cliente (seção 7) saberem que essa chamada é legítima.

### É fiel?

Na parte determinística, sim: total de XP, prestige, moedas, tipo de soulstone e power-ups.

Os **multiplicadores** saem do jogador **local** (runas de XP, prestige e materiais, stats de soulstone). Isso é o correto para progressão por jogador, igual a jogar solo. O que é **re-sorteado** em cada mundo é o espalhamento das orbs (`RandomUtils`), quais itens caem e em que quantidade.

Detalhe importante: o próprio jogo já foi escrito para N jogadores. Itens, `MinorSoulstone` e prestige são entregues **a cada** Entity de time Players. Então "cada um recebe o seu" é o comportamento nativo, não uma duplicação. O XP é a exceção: o jogo faz um drop só, coletado por quem passar.

### Quais argumentos são objetos e como serializar

| Arg | Tipo no interop | Chave estável |
|---|---|---|
| `experience`, `prestige`, `instant`, `experienceInterval`, `sourcePosition` | escalares | valor direto (posição absoluta em f32) |
| `currencies` | `PlayerProfileCurrencies` (classe, 6 × int32) | os 6 ints. No cliente: `new PlayerProfileCurrencies()` e setar as propriedades (`Copy` também existe). |
| `possibleItems` | `List<ItemDropRuntimeData>`. No interop, `ItemDropRuntimeData : ValueType` **com propriedades**, porque é não-blittable por causa da referência `ItemDefinition`. | `ItemId` (int32, enum) + `Chance` + `QuantityMin/Max`. No cliente: `Global.ItemCollection.GetItemDefinitionById(itemId)`. É o mesmo build e o mesmo `ItemCollectionSO`. |
| `powerUps` | `List<PowerUpRequest>`. No interop, `PowerUpRequest : ValueType` **com campos** (blittable: int, enum, bool, bool). | `PlayerLevel`, `SpecialSource` (int32), `CanBeLocked`, `CanBeBanished`. Não tem referência a objeto. |

Risco a validar: montar `new Il2CppSystem.Collections.Generic.List<ItemDropRuntimeData>()` e dar `Add` num struct não-blittable pelo Il2CppInterop. A assinatura existe no interop, então deve funcionar, mas é o ponto mais frágil. Há dois fallbacks:
- (a) Mandar o `netId` da origem e, no cliente, usar a `Loot.PossibleItems` **da própria marionete**, que já é uma lista Il2Cpp calculada pelo `LootComponent.Setup` local.
- (b) Mandar `null` (sem itens) numa primeira versão.

### Alternativa mínima: só `XpDropper.Drop(pos, amount)` e moedas

Funciona, porque `Drop` é estático e não depende de quem chama, mas perde muita coisa:
- o multiplicador de XP das runas do cliente (teria que ser recalculado à mão);
- o prestige;
- os itens;
- os power-ups;
- as soulstones grandes.

Só vale como passo de depuração. **Recomendo replicar o `ProcessLoot` inteiro.**

### Formato sugerido: `LootDrop`, host → cliente, ReliableOrdered no mesmo canal do `EnemyDied`

```
u16 sourceNetId (0 = sem entidade) | f32 x,y,z | i32 xp | f32 prestige | u8 flags (instant, temCurrencies)
| u16 interval | [6 × i32 currencies] | u8 nItems × (i32 itemId, f32 chance, u16 min, u16 max)
| u8 nPowerUps × (u16 playerLevel, i32 specialSource, u8 lock|banish)
```

Fica em ~30–60 B por morte. Com ~10–30 mortes por segundo no fim da run, dá ≤ 2 KB/s, e dá pra agrupar por tick se precisar.

**Ordem:** o `ProcessLoot` do host roda *dentro* do `ChangeEntityHealth`, enquanto o `EnemyDied` sai do polling de `HasBeenKilled` no tick (`HostEnemySync`). Mandando o `LootDrop` na hora, pelo mesmo canal ordenado, ele chega **antes** do `Died` da mesma entidade.

---

## 3. `XpDropper`: os dois modos funcionam chamados de fora?

Sim, nos dois modos. `XpDropper.Drop(position, amount, interval)` (0x763590) quebra `amount` em pedaços de `interval` (100 por padrão; o baú usa 5). Para cada pedaço:

- **Modo objeto** (`UseInstancedXpRender == false`, static +0x18A): `ResourcesUtil.GetPrefabInstance<CollectableObject>` (o pool `Collectables/Experience-Item`) seguido de `CollectableExperience.Setup(pedaço)` (0x463660). A coleta é física:
  - `CollectObjectsAreaTrigger.OnTriggerEnter` (0x55BC60), no colisor do jogador;
  - `PlayerCollectItemComponent.UpdateAll` (0x7D6C80), chamado pelo `GameLoop.Update`, que chama `CollectableObject.OnCollect(entity)` (0x463DE0);
  - `CollectableExperience.DoOnReachedTarget` (0x463560): `_collectingEntity.Experience.AddExperience(ExperienceAmount)`.
- **Modo instanciado:** só enfileira `(pos, pedaço)` em `XpDropper._addItemRequests` (+0x180).
  - `XpDropper.OnUpdate` (0x7640B0) esvazia a fila em `XpCpuState.AddXp` (0x762E50), com jitter `RandomUtils.Range(±0.2)` e `NavMesh.SamplePosition`. Atrai e coleta pela posição de `GameManagerUtil.PlayerEntities[0]` e `CollectionDistanceSqrdThreshold`, num job.
  - `XpDropper.OnLateUpdate` (0x763DA0) completa o job, desconta o coletado, pega `TryGetPlayer` (0x764C80, também `PlayerEntities[0]`) e chama `ExperienceComponent.AddExperience(coletado)`.

Nenhum dos dois modos olha quem chamou o `Drop`. O pré-requisito é o `XpDropper.Init/Load` da partida, que o cliente faz porque joga uma partida normal. **`PlayerEntities[0]` é o jogador local em cada máquina**, então a coleta no cliente vai para o jogador do cliente. Confirma o patch #15 da fase 0: não precisa de patch no `XpDropper`.

Observação de gameplay: a orb aparece onde o inimigo morreu. As orbs das kills do host aparecem no mundo do cliente perto do fantasma do host, e vice-versa. Como os spawns já alternam entre os dois (patch 7), na prática cada um pega as orbs da própria região e anda pra pegar as do outro. Se isso incomodar, existe a opção "crédito direto para drops longe": no replay, se `dist(pos, jogadorLocal) > R` e o drop estiver mais perto do remoto, chamar `ExperienceComponent.AddExperience(xp × multRunas)` em vez de `Drop`. Fica como config, não como padrão.

---

## 4. Itens e power-ups: duplicam? Como evitar, se quiser?

O que acontece hoje no host:
- **Os baús de recompensa guardada só existem no host:** o `GameManagerComponent` de Interactable-Guarded-Reward é desligado pelo `StopSpawning` no cliente. Não verifiquei se há outros `InteractableRewardObject` vindos da cena ou de MapTasks no cliente; o diagnóstico de LEAK da fase 1 não pega esse tipo.
- O proxy do cliente está em `Summons` durante o loot, então o host recebe sozinho o prestige, as moedas e os itens.
- O cliente provavelmente não consegue abrir baús no mundo do host, porque o `InteractableTriggerObject` deve ser acionado pelo jogador. Não descompilei isso. Abrir baú pelo cliente exigiria um pedido de interação, fora do escopo.

Com o replay do `ProcessLoot`, cada categoria fica assim:

| Drop | Nativo | Com replay | Duplicar é problema? |
|---|---|---|---|
| XP | 1 drop, quem passar pega | orbs nos 2 mundos | Não. É o "XP compartilhado". |
| Prestige, `MinorSoulstone`, itens de material | 1 por jogador (nativo) | 1 pro host, 1 pro cliente | Não. É o comportamento nativo para N jogadores. |
| Soulstone grande (`SoulstoneItem`) | 1 no chão, vai pra quem coletar | 1 em cada mundo | É moeda de meta-progressão de cada perfil. Recomendo deixar duplicar, porque cada jogador recebe o que receberia solo. |
| Power-up no chão (`CollectablePowerUpRequest`) | 1 no chão. `DoOnReachedTarget` (0x464140) põe o `PowerUpRequest` em `GameData.GameplayUIManager.LevelUpPanel.PowerUpRequests` (+0x150) **local** | 1 em cada mundo, cada um ganha uma escolha | Não, cada um monta a própria build. É o desejável em co-op. |
| **Power-up de boss** (`CollectableSurvivorsBossPowerUp.DoOnReachedTarget` 0x4650D0) | faz o mesmo e **chama `SurvivorsGameManager.OnPowerUpCollected` (0x438D20), que roda `GameManagerUtil.OpenPortalAndObjective(6 s)`** (0x6EE6F0) | o cliente abriria o portal **no mundo dele** | **Sim.** Mexe no fluxo da partida, e o portal precisa ser do host. |

Para evitar o problema do portal: um prefix em `SurvivorsGameManager.OnPowerUpCollected` devolve `false` no cliente. A escolha de power-up continua, porque ela já foi enfileirada antes pela base `CollectablePowerUpRequest.DoOnReachedTarget`. Portal e objetivo vêm por evento do host, no item de fluxo de partida.

Falta confirmar em runtime qual prefab tem o componente `CollectableSurvivorsBossPowerUp`. A hipótese é `SurvivorsBossItem`, escolhido para `SpecialSource & VoidFields`. Dá pra logar o tipo num postfix de `CollectablePowerUpRequest.SetupPowerUpRequest` (0x464360).

**"Quem pegar primeiro"**, se quisermos para soulstone grande ou power-up:
- O `LootDrop` ganha um `lootId`.
- No host (durante o `ProcessLoot` original) e no cliente (durante o replay), um postfix em `CollectablePowerUpRequest.SetupPowerUpRequest` e em `SoulstoneItem.Setup` (0x480540) guarda `lootId → instância`, usando um contexto `[ThreadStatic]` aberto pelo prefix do `ProcessLoot`.
- Um prefix em `CollectableObject.OnCollect` (0x463DE0, virtual) e em `SoulstoneItem.OnCollect` manda `LootClaimed(lootId)`.
- Quem recebe e ainda não coletou devolve a cópia dele ao pool. O caminho de despawn (`CollectableObject.OnDespawn`, slot 5, ou `ProcessDespawn`) ainda não foi descompilado e precisa ser checado.
- Corrida dentro de um RTT: os dois ganham, o que é aceitável.

Não recomendo isso como padrão.

---

## 5. Crédito de kill e procs

### O que o jogo faz na morte (`ChangeEntityHealth`, ramo `killed`)

- Percorre `HealthComponent.AttackerList` (+0x180, `List<AttackerReference {InstanceId, Entity, TimeInterestExpires}>`). Para **cada** atacante:
  - sem gate de `yieldLoot`: `SkillTreeComponent.ProcessOnKilledEntity(alvo)` (0x7F7C60), `RunesComponent.ProcessOnKilledEntity(alvo, GameStatsHealthType)` (0x7E7A00) e `PassiveSkillsComponent.ProcessOnKilledEntity(alvo, tipo)` (0x7D1B80);
  - **com gate** `HasBeenKilled && yieldLoot`: os recursos de classe, `CompanionHunger/ArcanePower/SoulHarvest/ElementalOverload/ChaosEnergy/Reanimation/Mechatron/DemonicBinding/GhostShip.GenerateLoot`, `RageComponent.GenerateRage` e `ConcentrationComponent.GenerateConcentration` / `MyrmidonFavorComponent.GenerateFavor`.
- Depois limpa a lista e soma 1 em `GameStats` +0x20, +0x24 ou +0x28, conforme `HealthComponent.GameStatsHealthType` (+0x38: 1 inimigo, 2 elite, 3 boss). **Isso não depende de atacante nem de `yieldLoot`.**
- Os procs usam o alvo morto:
  - Runas: cura a cada N kills (`KillCount` +0x1E0), `ChanceToCastSkillAfterKill` com `CastSkillMultipleTimesAtTarget` no morto, e a versão por posição.
  - SkillTree: `ChanceToGainHealthOnKill` e `TemporaryStatsOnKill`.
  - Passivas: cast com filtro de abnormal na posição do morto.
- `HealthComponent.RegisterAttacker` (0x7BF3F0) é chamado pelo `ChangeEntityHealth` quando `attacker != null` (e pelo `AbnormalStatusComponent.ApplyEffects` 0x4F1E40, para DoTs).
  - Não duplica entradas: se o `InstanceId` já está na lista, retorna **sem renovar**.
  - O prazo é `Time.time + GameProperties.DefaultAttackInterestExpirationTime` (+0x28).
  - Se o atacante tem `Entity.Summoner` (+0x628), também registra o dono. Assim, a kill de um summon dá crédito ao jogador.
- `HealthComponent.UpdateAll` (0x7C0270) poda a lista a cada frame: tira atacantes mortos e entradas vencidas.

### Situação no co-op hoje

- No **host**, o dano do cliente é aplicado com `attacker = null`, então o host só tem crédito se ele mesmo acertou o inimigo. Isso já é o modelo de "assistência" do jogo.
- No **cliente**, os hits nas marionetes passam pelo `ChangeEntityHealth` com `attacker = jogador do cliente` (o clamp só reduz o valor, o original roda). Ou seja, **a `AttackerList` da marionete já registra o jogador do cliente e os summons dele.** Quem apaga isso é o `ClientPuppets.KillPuppet`, que dá `AttackerList.Clear()` e aplica a morte com `yieldLoot: false`.
- O `GameStats` de kills do cliente **já anda** em toda morte de marionete, porque o incremento não depende de atacante. Isso ainda precisa ser confirmado com log de `GameStats.EnemiesKilled` nos dois lados. O contador fica "kills do time" nos dois, e o bloqueio de boss no cliente (fase 1) continua necessário.

### Recomendação: deixar o próprio jogo dar o crédito, sem `KillCredit`

No `KillPuppet`:
- não limpar a `AttackerList`;
- chamar `ChangeEntityHealth(null, marionete, -(HP+1), yieldLoot: **true**)`;
- no cliente, um prefix em `LootUtil.ProcessLoot` devolve `false` quando `ApplyingNetworkDeath && !ReplayingNetworkLoot`, para a marionete não dropar o loot dela (o loot vem do `LootDrop` do host).

Resultado: o cliente ganha **todos** os procs on-kill e os recursos de classe exatamente nas kills em que participou dentro da janela de interesse, sem nada extra na rede. O host continua igual. Não há proc duplo, porque cada máquina só tem o próprio jogador na lista da sua cópia.

O host sabe quem deu o último hit? Para dano do cliente, sim: em `HostEnemySync.OnClientDamage`, se `health.HasBeenKilled` virou true naquele lote, a kill foi do cliente (é o contador `_clientKills`). Se algum dia precisarmos de "kill de quem" (estatística por jogador ou quest), basta um byte `killer` no `EnemyDied`. O evento `KillCredit(netId)`, chamando os três `ProcessOnKilledEntity` à mão, é a alternativa simples, mas **perde os `GenerateLoot`/`GenerateRage`** e repete uma lógica que o jogo já tem.

---

## 6. Level-up do cliente

Não precisa de patch. O fluxo no cliente é este:

- `ExperienceComponent.AddExperience` (0x6CDEB0) exige `Stats.HasReference`. Calcula `xp × stat+0x54 (base) × stat+0x54 (mult) × HiddenExperienceMultiplier`, menos a redução da runa `ExtraDamageWithExperienceReduction`, e soma em `_experienceToAdd` (+0x40). Não olha rede nem host.
- `ExperienceComponent.UpdateAll` (0x6CE760), a cada frame e se `!ReachedMaximumLevel`, passa 10% de `_experienceToAdd` para `_experience` e chama `LevelUp` (0x6CE090) quando `_experience ≥ _requiredExperience`.
- `LevelUp`:
  - `Level++`;
  - em Endless, trava no máximo e liga `ReachedMaximumLevel`;
  - `required *= GameProperties.ExperienceProperties +0x14`;
  - aplica as runas de reroll (`ExtraRerollLevelFrequency/ExtraRerollsToEarn` em `InventoryData.Rerolls`);
  - **enfileira `PowerUpRequest(Level, None, true, true)` em `GameplayUIManager.LevelUpPanel.PowerUpRequests` (+0x150)**;
  - cura de level-up e som.
- `LevelUpPanelManager.OnUpdate` (0x60A7B0): com a fila não vazia e o popup fechado, abre `OpenPopup` (0x60E680) sozinho se `GamePreferences.AutoLevelUpEnabled` (+0x12), ou quando a ação de input é apertada. Ele escreve **`TimeUtil.LevelUpTime = 0` enquanto o popup está visível** e 1 quando fecha.
- A pausa sincronizada da fase 1 (postfix de `TimeUtil.SetTime`, que lê `LevelUpTime`) cobre isso: o cliente manda `PauseState` "level-up" e o host congela com "Parceiro escolhendo upgrade...", igual ao caminho testado com o host subindo de nível.
- Os power-ups coletados (seção 4) entram na mesma fila e no mesmo popup, também com pausa.
- Efeito colateral esperado: o número de pausas dobra, porque os dois sobem de nível. Se incomodar, é política de pausa, não bug.

---

## 7. Proposta

### Patches Harmony

| # | Lado | Alvo (RVA) | Tipo | O que fazer |
|---|---|---|---|---|
| B1 | Host | `LootUtil.ProcessLoot` (0x6FA180) | Prefix (classe nova, convive com `HostLootProxyTeamPatch`) | Se há peer e não estamos dentro do `RunBreakdown` (B2): serializar os args num `LootDrop` e mandar na hora (ReliableOrdered). `sourceNetId` vem de um contexto aberto por B3. No modo "XP dividido", escalar `experience` por `ref`. |
| B2 | Host | `SoulstoneItem.<RunBreakdown>d__20.MoveNext` (0x4835A0) | Prefix/Finalizer (flag) | Marcar o loot pessoal (minerais da runa de quem coletou) para B1 **não** replicar. O cliente gera o dele quando coleta a cópia dele. Alternativa sem patchar o `MoveNext`: só replicar dentro de contextos permitidos (`ChangeEntityHealth`, `InteractableRewardObject.UpdateAll`, `LootDropperByHealthPercentComponent.UpdateAll`). |
| B3 | Host | `UnitUtil.ChangeEntityHealth` (0x73BE30) | Prefix/Finalizer | Guardar o `targetEntity` corrente num `[ThreadStatic]`, para B1 achar o netId (`HostEnemySync.ByPtr`). Opcional, porque só serve para o fallback de itens e a depuração. |
| B4 | Cliente | handler do `LootDrop` | — | Enfileirar até o `StartMatch` local e o jogador local existir. Depois montar `PlayerProfileCurrencies`, `List<ItemDropRuntimeData>` (via `GetItemDefinitionById`) e `List<PowerUpRequest>`, e chamar `LootUtil.ProcessLoot` com `ReplayingNetworkLoot = true`, num try/catch. |
| B5 | Cliente | `LootUtil.ProcessLoot` (0x6FA180) | Prefix → false | Bloquear quando `ApplyingNetworkDeath && !ReplayingNetworkLoot` (o loot próprio da marionete). |
| B6 | Cliente | `LootDropperByHealthPercentComponent.UpdateAll` (0x7C3150) | Prefix → false | No cliente, toda instância é marionete, e o HP vindo do snapshot faria o boss dropar XP local **além** do replay. Hoje bosses não têm marionete, mas vão ter. |
| B7 | Cliente | `ClientPuppets.KillPuppet` (código nosso) | — | Não limpar a `AttackerList` e passar `yieldLoot: true`. Com B5 isso dá procs on-kill, `GenerateLoot`/`Rage`/`Concentration` e `GameStats` nativos. |
| B8 | Cliente | `SurvivorsGameManager.OnPowerUpCollected` (0x438D20) | Prefix → false | O portal é do host. A escolha de power-up continua normal. |
| B9 | Ambos (opcional) | `CollectablePowerUpRequest.SetupPowerUpRequest` (0x464360), `SoulstoneItem.Setup` (0x480540), `CollectableObject.OnCollect` (0x463DE0), `SoulstoneItem.OnCollect` (0x480050) | Postfix/Prefix | "Quem pegar primeiro" (seção 4). |

Nada no `XpDropper`, no `ExperienceComponent` nem no `LevelUpPanelManager`.

### Pacotes novos

- `LootDrop` (host → cliente, ReliableOrdered), no formato da seção 2.
- `LootClaimed` (os dois sentidos, ReliableOrdered), só se B9 entrar.
- Opcional: um byte `killer` no `EnemyDied`.

O `PacketType` commitado vai até 13. A 4C (magias), ainda sem commit no checkout, já usa `SkillCast = 14` e `SkillChannelEnd = 15`. Então o candidato é `LootDrop = 16` e `LootClaimed = 17`, mas confira os próximos livres **na hora de implementar**, porque a 4A (revive) também pode pegar algum.

Sobre o `RemoteCaster` da 4C: é um `Creeps/Dummy` em time **Summons** nos dois lados. Fica fora da varredura de Players do `ProcessLoot`, então não cai no NRE da seção 1. Com B7 ele pode entrar na `AttackerList` da marionete se as magias dele acertarem, mas o Dummy não tem SkillTree, Runes nem PassiveSkills, então não gera proc. Isso é inofensivo.

### Ordem de implementação

1. **XP primeiro:** B1, B4 (só XP, prestige, moedas e power-ups, com `possibleItems = null`) e B6. Log `[loot]` nos dois lados.
2. **Itens:** a lista `ItemDropRuntimeData` no B4. Se a construção pelo interop falhar, usar o fallback por `sourceNetId`, com a `Loot.PossibleItems` da marionete.
3. **Crédito de kill:** B5 e B7.
4. **Portal:** B8, e depois o portal e os objetivos por evento do host (fluxo de partida).
5. B2, quando houver runa de minerais em teste, e o modo "XP dividido" como config.
6. B9 só se o usuário quiser exclusividade.

### Riscos

- **NRE no `ProcessLoot`** se aparecer alguma Entity de time Players sem Runes, Inventory ou Stats no mundo do cliente (seção 1). Todo patch precisa de try/catch, porque um NRE dentro do `ChangeEntityHealth` corta o resto da morte (fase 3).
- **`List<ItemDropRuntimeData>` no Il2CppInterop**, porque é struct não-blittable (seção 2).
- **Drop dobrado:** se esquecermos B5 ou B6, o cliente recebe o replay **e** o loot local da marionete.
- **Divergência de valores:** RNG por mundo, multiplicadores locais e `ReachedMaximumLevel` local são intencionais. Mesmo assim, vale logar a `Loot.Experience` da marionete contra o `xp` recebido para o mesmo netId, para pegar diferenças de tier (`GetCurrentTierRating` no cliente).
- **Janela de interesse:** se `DefaultAttackInterestExpirationTime` for curta e o `Died` atrasar (RTT, mais 1/15 s do lote de dano, mais o polling), o cliente pode perder procs de kills no limite. Ler o valor em runtime.
- **Pausas mais frequentes**, porque os dois sobem de nível.
- **O cliente (provavelmente) não abre baús**, que só existem no host. O cliente recebe a parte dele só quando o host abre.
- **Volume** baixo (≤ 2 KB/s). Agrupar por tick se precisar.

### Como testar (duas instâncias, Desert)

1. Host mata sozinho. No cliente devem aparecer orbs no ponto da morte (perto do fantasma) e `[loot] replay xp=…` no log. O cliente coleta, a barra de XP sobe e o level-up abre a tela. No host deve aparecer "Parceiro escolhendo upgrade...".
2. Cliente mata. O XP aparece nos dois mundos (o host já tinha).
3. Comparar o `InventoryData` (prestige, `MinorSoulstones`, `Items`) dos dois numa tela de fim, ou com log a cada 30 s.
4. Elite ou boss com power-up: cada um coleta o seu e ganha uma escolha. No cliente o portal **não** pode abrir sozinho (B8).
5. Procs: personagem ou runa com efeito on-kill (`RunesComponent.KillCount`, cura após kills, `ChanceToCastSkillAfterKill`) e um personagem com recurso de kill (Rage, Concentration, SoulHarvest). Logar `KillCount` do cliente subindo só nas kills em que ele acertou.
6. Comparar `GameStats.EnemiesKilled` nos dois lados.
7. Contar NRE e erros `[loot]` nos dois logs: o esperado é zero.

---

## Resumo

- `ProcessLoot` (0x6FA180) é o funil de todo loot do Survivors: morte (`ChangeEntityHealth`, `instant = false`, `interval = 100`), baús (`instant = true`), drop de boss por % de vida, minerais da runa de soulstone, e cheats.
- Ele gera XP (um `XpDropper.Drop`), prestige e soulstone menor (direto ou por projétil, **um por jogador**), uma soulstone grande no chão, itens (um projétil por jogador) e power-ups no chão.
- Os valores vêm de `EnemyPrefabData` → `SetEntityBasedOnPrefabData` → `LootComponent` (itens por tier no `LootComponent.Setup`).
- Ele lê Runes, Inventory e Stats de toda Entity de time Players, às vezes sem checar: o XP já quebra em `Runes`.
- Plano: o host replica os args (`LootDrop`) e o cliente chama `ProcessLoot` local. É seguro e fiel nos totais, com multiplicadores e RNG locais. Itens viram `ItemId` e power-ups viram structs simples.
- O `XpDropper` funciona chamado de fora nos dois modos e coleta pelo `PlayerEntities[0]` (o local). Nada a patchar.
- Duplicar é o comportamento nativo para N jogadores (itens, moedas, prestige). O único perigo é o power-up de boss, que abre o portal: bloquear no cliente.
- Crédito de kill: a `AttackerList` da marionete já tem o cliente. Basta não limpar, usar `yieldLoot: true` e bloquear o `ProcessLoot` da marionete. Isso dá procs, recursos de classe e `GameStats` nativos, sem `KillCredit`.
- Level-up do cliente: `AddExperience` → `LevelUp` → fila do `LevelUpPanel`; `LevelUpTime = 0` com o popup aberto, já coberto pela pausa da fase 1.
- Ordem: XP (B1, B4, B6), itens, crédito de kill (B5, B7), portal (B8). Exclusividade (B9) é opcional.
- Maiores riscos: `List<ItemDropRuntimeData>` no interop, drop dobrado sem B5/B6, e Entity de time Players sem Inventory no cliente.
