# Fase 4D: investigação (chefes e inimigos especiais no cliente)

Investigação estática, só leitura: `dump.cs`/`stringliteral.json` do Il2CppDumper, Ghidra headless (cópia do projeto numa pasta de rascunho, `-readOnly`) e os caminhos de `Resources` listados no `globalgamemanagers` do jogo. RVAs relativos a `0x180000000`. As saídas anotadas do Ghidra ficaram em `tools/decompiled_4d_batch1.txt` e `tools/decompiled_4d_batch2.txt` (fora do git). Nada foi compilado nem testado em jogo; o que está marcado como "a confirmar" precisa de log em runtime.

---

## Resumo executivo

- **Não existe Addressables no gameplay.** `ResourcesUtil.Load<T>` (0xA40610), `LoadAsync<T>` (0xA40190) e `GetPrefabInstance<T>` (pool) são `Resources` puro (`ResourceRequest`). O `Unity.Addressables.dll` só é usado pelo banner da home (`HomeBannerConfig`, um `AssetReference`). Todo boss, void hunter, VoidPresence, minério e pilar tem um **caminho de `Resources`** estável, igual nas duas máquinas. Esse caminho é a chave que falta.
- **O boss é sorteado em cada máquina.** `BossSpawnProcessor.Setup` (0x8061B0) chama `EnemyPrefabsUtil.FillBossPools` (0x6E0C70), que embaralha `Global.BossPrefabs` com `RandomUtils.Range`. Host e cliente têm `BossList` diferentes. Por isso a chave tem que levar o **endereço real** (`Bosses/<Nome>`), nunca um índice.
- **O pipeline do boss é o mesmo da fase 2, com dois passos a mais:** `PlaguebringerComponent.SetupWithTier` (skills e stats por tier) e o multiplicador de vida. A barra de vida de boss aparece **sozinha** no cliente, porque `PlaguebringerComponent.Setup` (chamado dentro do `Entity.Setup`) registra o boss. O problema é o contrário: quem tira a barra é o `DoOnUpdate` da IA, que a marionete desliga. O cliente precisa chamar `GameManagerUtil.DeregisterBossEntity` na morte/despawn.
- **As invocações do boss já passam pelo `Entity.Setup` no host** (`SummonEntityEffect.<SpawnUnits>` e variantes) e, por isso, já viram `EnemySpawn`. Só que o prefab vem de uma referência direta na skill (`PossiblePrefabs`), não de um endereço, e a chave cai em `@Nome`.
- **Os avisos de área (telegrafia) são parte da skill do lançador:** `Skill.Activate` (0x5619B0) liga o `SkillChargingTimeTrait` (decal + animação) e a corrotina `ActivateChargedAttack` (0x56BE20) espera `ChargingDuration` antes de disparar. Pro cliente ver e desviar, é preciso **replicar o cast do inimigo** e rodar `CastSkill` na marionete com os efeitos bloqueados. É o mesmo mecanismo que a fase 4C montou para as magias do jogador.
- **A God-Entity, os God Meteors, os baús guardados e as MapTasks não são inimigos-marionete:** são objetos de cena, casts ou interativos (`MonoBehaviour`, sem `Entity`). Precisam de eventos próprios.

---

## 1. Bosses

### 1.1 De onde vem o prefab

- `Global.BossPrefabs` (`BossPrefabsSO`, estático 0x100) tem `BossAddresses[]`: `Addresses: List<string>` (nomes curtos), `BossNameId`, `DropItem`, `Active`, `UnavailableInDemo`.
- `EnemyPrefabsUtil.FillBossPools(bossListToFill, bossQuantity, specificBossNames)` (0x6E0C70) cria um `BossPrefabs` por entrada ativa, com três listas montadas por concatenação de literais:
  - `Prefabs[i] = "Bosses/" + nome`
  - `Statues[i] = "Bosses/Statues/" + nome + "-Statue"`
  - `Ethereals[i] = "Bosses/Ethereals/" + nome + "-Ethereal"`
  
  Depois pré-carrega com `Entity.PreloadAndFillPools(path, 2, 2)` (0x80A410) e `ResourcesUtil.PreloadPool<StatueComponent>`, embaralha a lista (`RandomUtils.Range`), evita repetir os últimos e preenche `BossSpawnProcessor.BossList` (0x80). Quantidade: `(AffixGameplayData+0x30 + 1) × (número de objetivos)`, calculada em `BossSpawnProcessor.Setup` (0x8061B0).
- Os caminhos existem no container de `Resources` (`globalgamemanagers`): `bosses/plaguebringer`, `bosses/medusa`, `bosses/demon-elite`, `bosses/statues/*-statue`, `bosses/ethereals/*-ethereal`, mais os bosses de personagem (`bosses/houndmaster-boss` etc.).
- **Chave estável:** `Bosses/<Nome>`. O nome do prefab é o último segmento do caminho (por exemplo `Plaguebringer`), então dá pra reconstruir a chave a partir do nome da instância. `Resources.Load` não diferencia maiúsculas.

### 1.2 Quem dispara e como nasce

1. `SurvivorsGameManager.UpdateAll` (0x438DC0): quando um `EliminateEnemiesNonCumulativeObjective` de `ProgressObjectives` (0xB0) se completa, chama `BossSpawnProcessor.SpawnBosses(player, tier, tier, tier, team, useGlobalStats=false, additional=AffixGameplayData+0x30, spawnPoint=null, canRefreshTargets=true)`. O tier vem de `GameData+0x1D4`. Veja a seção 5.
2. `BossSpawnProcessor.SpawnBosses` (0x806510): pega `BossList[min(BossesSpawned, Count-1)]`. Para cada prefab da entrada: escolhe um ponto (`GetRandomSpawnPointOutsideOfView(player, -0.4)` + `NavMesh.SamplePosition`, a pelo menos 6 u dos já usados), instancia a **estátua** (`GetPrefabInstance<StatueComponent>(Statues[i])`), toca `MusicStateProcessor.PostBossAppear` (0x436C20, só um evento de áudio), faz `ResourcesUtil.Load<Entity>(Prefabs[i])` e enfileira um `BossQueueItem` em `HighPrioritySpawnQueue` (0x88). O item leva Prefab, DesiredTier/SkillsTier/LootTier, StatueInstance/posição/rotação, `HealthMultiplier = TotalBossStatsMultiplier × HealthMultiplierPerBoss + 1`, Team, UseGlobalStats e CanRefreshTargets. Com o affix de etéreos, também enfileira um `EtherealBossQueueItem` com `Load(Ethereals[i])`. No fim, `_pendingBosses++` e `_hasPendingBosses = true`.
3. `BossSpawnProcessor.OnUpdate` (0x8057F0, chamado no fim do `SurvivorsGameManager.UpdateAll`) ou `ProcessBossSpawnQueue` (0x805A70): enquanto `BossEntities.Count < BossAllowedAtTheSameTime`, inicia a corrotina `<SpawnBossRoutine>d__39` no `JobRunner`.
4. `<SpawnBossRoutine>d__39.MoveNext` (0x813BC0):
   - VFX de spawn (`FirstStatueSpawnVfx`, `SoulTransferVfx` em linha até a estátua), espera `TimeToBreakStatue` (0x30), VFX `StatueBreakVfx` e `StoreInstance` da estátua.
   - `GetPrefabInstance<Entity>(prefab, posição da estátua, rotação)`, `Entity.CacheReferences`, `Stats.CopyStats(prefab._stats.Stats)`, `MultiplicativeStats.SetToOne`, limpa a lista de loot e chama `UnitUtil.FillBossLootTable(DesiredLootTier)`.
   - Se a IA (`Entity._basicAI`, 0x88) for `MasteryBossComponent`: `SetupWithTier(entity, tier, skillsTier, lootTier)` (0x7C9590), `MultiplicativeStats.MaxHealth × HealthMultiplier`, `Entity.Setup`, `GameManagerUtil.RegisterBossEntity` e `BossRegistered = true`. Senão (`PlaguebringerComponent`, o caso do modo Survivors): `SetupWithTier` virtual (slot 14, 0x7D6270), multiplicador de vida, `Entity.Setup`, time (`TeamComponent.Team`, se veio ≠ 0), `Global...+0x48/+0x50` se `UseGlobalStats`, e `StopRefreshingTarget` se `!CanRefreshTargets`.
   - Objetivo: cria `EliminateSpecificEntitiesObjective(id = "Eliminate" + EntityName + "Objective", 1)`, adiciona o boss nele, adiciona o boss na lista de entidades do `MainObjective` (0xC0) e adiciona o objetivo em `EliminationObjectives` (0xB8).
   - Etéreos: se o affix está ativo, chama `SpawnEtherealEntity` (0x806E20).

Outros criadores de boss: `MapKillBossesInAreaTask.<SpawnBossRoutine>` (0x45F540, MapTask "KillRandomBosses", mesma receita com estátua), `CheatManager.SpawnBoss` (0x6D8E20), `BossSpawnProcessor.MasterySpawnBosses` (0x804FC0) e `SpawnSpecificBoss` (0x807350, modo Mastery), `CrusadeGameManager.SpawnCrusadeBoss` (0x410ED0) e `SetupCustomEntityAction` (0x493770). Fora do modo Survivors, fica pra depois.

### 1.3 Setup extra além do `Entity.Setup`

| O quê | Onde | Precisa no cliente? |
|---|---|---|
| Stats por tier, skills por tier, loot | `PlaguebringerComponent.SetupWithTier` (0x7D6270): `SetStatsBasedOnDifficultyAndTier` (0x7D5FE0), contador por tier, `SetSkillsBasedOnCurrentTier` (0x7D54D0, liga/desliga listas `Tier2Skills…Tier5Skills`) | Sim, antes do `Setup`: deixa a vida máxima e o conjunto de skills iguais aos do host (importa para o replay de cast, item 4). |
| Multiplicador de vida | `_stats.MultiplicativeStats.MaxHealth` (Stats+0x14) `*= HealthMultiplier` | Sim (ou mandar o valor pronto). O HP% do snapshot já corrige o resto. |
| Registro de boss (barra de vida) | `PlaguebringerComponent.Setup` (0x7D63B0) → `GameManagerUtil.RegisterBossEntity` (0x6EEA40) → `BossSpawnProcessor.RegisterBossEntity` (0x805C00), que só aceita `HealthComponent.GameStatsHealthType == Boss (3)` | Acontece sozinho dentro do `Entity.Setup`. |
| Tirar a barra | `PlaguebringerComponent.DoOnUpdate` (0x7D4DD0) / `MasteryBossComponent.DoOnUpdate` (0x7C7360): ao ver `HasBeenKilled`, chamam `GameManagerUtil.DeregisterBossEntity` (0x6ECD70) | **O cliente tem que chamar à mão** (IA desligada = `DoOnUpdate` nunca roda). |
| Apresentação | `PlaguebringerComponent.ApresentationTransition` (`PhaseTransition.Setup`/`StartAction`), no `DoOnUpdate` | Não no MVP (é animação/ação de entrada). |
| Estátua + VFX | `SpawnBossRoutine` (estado 0 e 1) | Opcional: evento "boss chegando" (seção 6). |
| Música | `MusicStateProcessor.PostBossAppear` (evento de áudio) e `MusicStateController.OnUpdate(_hasPendingBosses, …)` no `BossSpawnProcessor.OnUpdate` | Opcional (veja o risco 6). |
| Objetivo "eliminar boss" | `SpawnBossRoutine` cria `EliminateSpecificEntitiesObjective` + `MainObjective` | Sim, se o painel de objetivos do cliente tiver que mostrar o boss (seção 5). |
| Arena | Não há arena no Survivors. `CrusadePlayerBarrier` só existe nas MapTasks/Crusade (`UsePlayerBarrier`). | Não. |

---

## 2. Entidades especiais: quem é marionete, quem é outra coisa

| Entidade | Quem cria (RVA) | Prefab / endereço | Tratamento | Chave proposta |
|---|---|---|---|---|
| **Void Hunters** | `GameManagerVoidHunterComponent.OnUpdate` (0x41BA30) → `SpawnVoidHunter` (0x41BEB0), por passo de progresso (`TimeUtil.CheckProgressStepHasBeenReached`) | `Setup` (0x41BD80) chama `EnemyPrefabsUtil.GetVoidHunterPrefabsExcludingId(id do personagem do jogador)` (0x6E2E10): `ResourcesUtil.Load<Entity>("Creeps/" + VoidHunterPrefabData.PrefabAddresses[j])` de `Global.VoidHunterPrefabs` (0x108). O grupo nasce junto (±3 u em volta de `GetRandomSpawnPointOutsideOfView(player, 0.5)`), com `CacheReferences`, `CopyStats`, `SetToOne`, `Entity.Setup` e `RequestHealthBar(elite)`. | **Marionete.** Atenção: a exclusão é pelo personagem **local**, então o cliente pré-carrega um conjunto diferente do host. | `res:Creeps/<addr>`. O valor exato de `<addr>` está no SO; os candidatos no container são `creeps/*-npc` (`houndmaster-npc`, `barbarian-npc`…). Confirmar com log. |
| **VoidPresence-Survivors** (o `@VoidPresence-Survivors` da fase 2) | `GameManagerAffixSpecialEntitiesComponent.OnUpdate` (0x80BE90) → `SpawnAffixSpecialEntities` (0x80C0C0), depois de `AffixSpecialEntityToSpawnDefinitionSO.Delay` (0x70). Também `MapSurviveForSecondsAgainstEnemiesTask.SpawnSpecialEntities` (0x45BAB0), na MapTask `MapTask-SurviveVoidPresences` (`SpecialEntityOverTime.Addresses`). | `Load<Entity>(Addresses[i])` (0x68). Os textos `Creeps/VoidPresence-Survivors` e `Creeps/VoidPresenceFast-Survivors` aparecem serializados no `resources.assets` (affix `Affixes/EliteAffix-VoidPresence`). Mesmo pipeline + `ApplyGenericEnemyScaling(elite)` + `ApplyGenericGameModeEnemyScaling` + `Setup` + barra + lista de elites. | **Marionete, prioridade alta:** é o que matou o cliente parado na fase 3, e o cliente precisa ver pra fugir. | `res:Creeps/VoidPresence-Survivors` |
| **Minérios** | `GameManagerMineralSpawnComponent.OnUpdate` (0x811250) → `SpawnMineral` (0x811BF0) | `Setup` (0x811960) carrega `Minerals/{ItemId}-Ore` por tier (`String.Format`); `GetMineralPrefabAccordingToTier` (0x810FC0) sorteia. Ponto: `GetRandomSpawnPointOutsideOfView(player, 0.1)`, yaw aleatório, `Entity.Setup` e barra. | **Marionete estática** (sem IA; o jogador quebra pra ganhar loot). Hoje o host provavelmente já manda `@CommonCopper-Ore`, e o cliente resolve por `FindObjectsOfTypeAll` porque o `Setup` do componente dele pré-carregou os mesmos prefabs (mesmo mapa). Funciona por sorte. O loot é da 4B. | `res:Minerals/<Nome>` |
| **Cristal de cura** | `GameManagerHealingCrystalSpawnComponent.OnUpdate` (0x80DED0) → `SpawnHealingCrystal` (0x80E1E0) | `_healingMineralPrefab` (0x30), de `Minerals/Heal-Crystal` (literal) ou `Minerals/Heal-Crystal_Destroyer`. Mesmo esquema do minério. | **Marionete estática.** O efeito de cura ao quebrar (loot/collectable?) fica a confirmar na 4B. | `res:Minerals/Heal-Crystal` |
| **Pilares (Despair/Protection)** | `GameManagerPillarsOfDespairComponent.OnUpdate` (0x417F60) → `SpawnPillar` (0x4189B0); Protection herda | `_pillarPrefabs` (0x58), carregados no `Setup` (corrotina, `LoadAsync` de `Pillars/…`: `Pillar-Chaos-Survivors`, `Pillar-Shadow-Survivors`, `Pillar-Protection-Survivors`). Nascem nos `_pillarSpawnPoints` da cena e passam por `BasePillar.Setup(creepSpawnPoints, tier, difficulty)` (virtual), `CopyStats`, `SetToOne` e barra. `IncrementalPillar` invoca creeps (corrotina `SpawnUnits`); `AutoCastPillar` lança skills. | **Marionete estática.** Os creeps do `IncrementalPillar` já viram marionete normal. Os casts do `AutoCastPillar` precisam do replay de cast (item 4). | `res:Pillars/<Nome>` |
| **Loot Goblin** | `GameManagerLootEnemySpawnerComponent.OnUpdate` (0x80F2E0) | `Load<Entity>(_prefabAddress)` (0xB0) + `EnemyPrefabsUtil.SpawnEnemyEntity` com `_enemyPrefabData`, `StopRefreshingTarget`, `LootComponent.CopyLoot`, VFX de portal. Expira (`ExpireEntity`, 0x80F020). | **Marionete.** O despawn por expiração já chega como `EnemyDespawn`. | `res:Creeps/LootGoblin` (hoje sai `@LootGoblin`: passa pelo `SpawnEnemyEntity`, mas não está nas listas). |
| **Guardiões de recompensa** | `InteractableGuardedRewardObject.TrySpawnGuardians` (0x455660) → `SpawnEnemyEntity` | Grupos de elite do próprio baú (`FillGroupTierList` com o `EnemyPrefabsGroupsSO` do objeto, em `InteractableGuardedRewardObject.Setup`, 0x455390), **não** as listas globais. | **Marionete.** A chave pode cair em `@Nome` se o grupo não estiver nas listas do mapa. | `res:` se o nome bater em `Creeps/`; senão `@Nome`. |
| **Baú guardado (o objeto)** | `GameManagerInteractableGuardedRewardSpawnerComponent.<SpawnReward>d__16` (0x814830) | `MapCollectionSO.GetRandomRewardObject` → `LoadAsync<InteractableRewardObject>` (`Interactables/Interactable-Guarded-Chest-<Mapa>`), num ponto de cena aleatório. É `MonoBehaviour`, não `Entity`. | **Interativo, não marionete.** Precisa de evento de spawn (endereço + índice do ponto de cena) e de abrir/coletar. Itens A/B. | — |
| **MapTasks** | `GameManagerMapTaskSpawnerComponent.<SpawnTask>d__17` (0x814D60) | `LoadAsync<MapTask>(MapTaskMetadata.TaskAddress)` (`MapTasks/Tasks/MapTask-…`), `MapTask.Setup` + `StartTaskAtPosition` (0x45CDA0: trigger, ícone de minimapa, texto). | **Interativo.** As entidades que a task cria (bosses, elites, VoidPresence) viram marionete pelas chaves acima. | — |
| **God Meteors** | `GameManagerGodMeteorsComponent.OnUpdate` (0x80DA10) / `LaunchGodMeteorAroundPlayer` (0x80D820) | Não cria Entity: a God-Entity da cena (`SurvivorsGameManager.GodEntity` 0x30 = `GameData+0x28`) faz `SkillCasterComponent.CastSkill(_godMeteorSkill, chão a ±16 u do _playerEntity, SkillActivationOptions.IgnoreAll)`. Dispara por kills (`SetCurrentEnemiesKilled`) e por affix. | **Cast, não marionete.** A God-Entity existe igual nas duas cenas (já está na allowlist). Evento `GodCast(posição)` e cast local na God-Entity do cliente (item 4). Hoje os meteoros só caem em volta do host. | — |
| **Etéreos** | `BossSpawnProcessor.SpawnEtherealEntity` (0x806E20), affix `EliteAffix-EtherealBosses` | `Bosses/Ethereals/<Nome>-Ethereal`, `MasteryBossComponent.SetStatsBasedOnDifficultyAndTier` e `Setup`. | **Marionete.** | `res:Bosses/Ethereals/<Nome>-Ethereal` |
| **Estátua de boss** | `SpawnBosses` / `SpawnBossRoutine` | `GetPrefabInstance<StatueComponent>("Bosses/Statues/<Nome>-Statue")`. Só visual (`MiddleBodyTransform`). | **Visual por evento**, opcional. | — |
| **Invocações de inimigo/boss** | `SummonEntityEffect.<SpawnUnits>d__29` (0x541750), `SummonEntityPositionalEffect.<SpawnUnits>d__31` (0x542C20), `SummonEntityAndForgetPositionalEffect.<SpawnUnits>d__16` (0x52F5B0) | `GetPrefabInstance(PossiblePrefabs[i])` (referência direta na skill), `CacheReferences`, `UnitUtil.SetupSummonStats`, barra, `Entity.Setup`. | **Marionete.** Já chegam como `EnemySpawn` (time Monsters). | `sum:<netId do dono>:<Nome>` (seção 6) |
| Elites, Twitch, Overlord, regulares | `SpawnEnemyEntity` com listas | Listas do `EnemyPrefabsUtil` | Já funciona (fase 2). | Endereço das listas (sem mudança) |

`GameManagerDiscord/Steam/EpicGames/GogGalaxyActivityComponent` são só presença social. `GameManagerProgressBasedWeightedEliteSpawnComponent` e `GameManagerRegularEnemyIgnoreProgression…` usam as filas normais.

---

## 3. Carregar no cliente

- **Sim, dá pra usar `ResourcesUtil.Load<Entity>(endereço)` + `GetPrefabInstance` + `Entity.Setup`.** É exatamente o que os próprios spawners fazem (`SpawnAffixSpecialEntities`, `SpawnVoidHunter`, `SpawnBossRoutine`, `CheatManager.SpawnDummy`). A receita comum dos especiais é: `Load<Entity>` → `GetPrefabInstance<Entity>(prefab, pos, rot)` → `Entity.CacheReferences` → `Stats.CopyStats(inst._stats.Stats, prefab._stats.Stats)` → `MultiplicativeStats.SetToOne` → (opcional `ApplyGenericEnemyScaling`/`ApplyGenericGameModeEnemyScaling`) → `Entity.Setup` → `RequestHealthBar`. O `EnemyPrefabKeys` já usa `ResourcesUtil.Load<Entity>(address)` para os grupos de elite.
- **Não precisa de Addressables.** `Load` é síncrono (`Resources.Load`). `LoadAsync` devolve um `ResourceRequest` normal (o jogo usa nas MapTasks, nos baús e nos pilares).
- **O custo é o hitch.** Prefab de boss é pesado; o primeiro `Load` no meio da luta pode travar um frame. Três saídas, da melhor pra pior:
  1. **Pré-carregar no início da partida:** o host manda a `BossList` dele (os nomes) logo depois do `StartMatch`, e o cliente chama `Entity.PreloadAndFillPools("Bosses/"+nome, 2, 2)` (0x80A410), exatamente como `FillBossPools` faz. Dá pra fazer o mesmo com os void hunters do host (`res:Creeps/…`) e com os endereços do affix ativo.
  2. **Pré-carregar no aviso:** o boss leva `TimeToBreakStatue` segundos entre `SpawnBosses` (estátua) e o `Entity.Setup`. Um evento `BossIncoming` nesse momento dá tempo pra um `ResourcesUtil.LoadAsync`.
  3. `Load` síncrono na hora (MVP aceitável; o snapshot espera).
- **Prefabs que só existem depois de uma cena/bundle:** no Survivors não tem. Os prefabs de gameplay estão todos em `Resources`. O que vem da cena (God-Entity, `HomePortalObject` 0x40, portais Endless/BossRush, pontos de spawn de pilar/baú/task em `SurvivorsSceneReferences`) existe igual nas duas máquinas porque a cena é a mesma. Se um dia precisar referenciar um objeto de cena pela rede, use o índice no array da cena, não um id de runtime. As invocações são o único caso "carregado por dependência": o prefab só está na memória do cliente depois que o dono (boss) foi carregado.

---

## 4. Fases, invocações, telegrafia, barras e UI

### Fases
- **Survivors (`PlaguebringerComponent`):** não há fase por vida. O "nível" do boss é por tier (`SetupWithTier` liga/desliga listas de skills). A única "fase" é a `ApresentationTransition` de entrada, que segura o boss parado até acabar (`DoOnUpdate` só chama o comportamento quando a transição chega no estado 2).
- **Mastery (`MasteryBossComponent`):** fases por % de vida (`InitialPhase`, `IntermediatePhases[]`, `FinalPhase`, `SetSkillsBasedOnPhase` 0x7C8200, `PhaseTransition.StartAction`, voice line de provocação). Só no modo Mastery.
- Na marionete, a IA desligada não roda nada disso, o que está certo (o host decide). Se alguma transição tiver efeito visual importante, a evolução é um evento `BossPhase(netId, índice)` que chama `PhaseTransition.StartAction` na marionete. Fora do MVP.

### Invocações
- Quem cria no host: as skills de summon do boss (`SummonEntityEffect`/`SummonEntityPositionalEffect`/`SummonEntityAndForgetPositionalEffect`). As três terminam em `Entity.Setup` depois de `SetupSummonStats` (que acerta o time). **O postfix de `Entity.Setup` do `HostEnemySync` já pega**, e o `EnemySpawn` sai.
- Por que falha hoje: sem `SpawnEnemyEntity`, não há prefab capturado, e `HostKeyFor` cai em `@Nome`. No cliente, `ResolveByName` só acha o prefab se ele já estiver na memória. Ele **provavelmente vai estar** depois que a marionete do boss for criada (o prefab do summon é dependência do prefab do boss), mas isso não é garantido (o summon pode chegar antes do boss na fila, ou o nome pode ser ambíguo).
- Cuidado com o replay de cast (abaixo): a marionete **não pode** rodar o efeito de summon, senão o cliente cria invocações locais. Bloquear `Skill.DoApplyEffects`/`ApplyEffects` com caster = marionete já cobre isso (é o mesmo bloqueio da 4C).

### Telegrafia (avisos de área)
- `Skill.Activate` (0x5619B0) chama `SkillChargingTimeTrait.Activate` (0x55F240): toca a animação de carga e liga o `ChargingArea` (`SkillChargingTimeTraitRoundArea.Activate` 0x55EFA0 / `LineArea` 0x55EBE0: escala pelo `GetAdjustedScale`, `Animator.speed = 1/ChargingDuration`, ativa os decals `FillingAreas`/`DecalAnimations`). Depois a corrotina `<ActivateChargedAttack>d__98` (0x56BE20) espera `ChargingDuration` (`SkillChargingTimeTrait` 0x30), chama `FinishCharging` (0x55F420) e dispara o ataque.
- Ou seja, o aviso é a própria skill do inimigo rodando no lançador. **Não dá pra mostrar sem replicar o cast.**
- Proposta: postfix em `SkillCasterComponent.CastSkill` (0x7F0D00) no host, quando `__instance.Entity` está rastreado pelo `HostEnemySync` e `__result == true`. Manda `EnemyCast(netId, índice da skill em SkillCaster.Skills, posição/direção alvo, netId do alvo: proxy = jogador local do cliente, host = fantasma)`. O índice serve porque é o mesmo prefab dos dois lados. Se o tier mudar o conjunto, usar o nome do GameObject da skill. No cliente, `puppet.SkillCaster.CastSkill(skill, params, SkillActivationOptions.IgnoreAll)` (0x7F4510, o mesmo que os God Meteors usam). Efeitos bloqueados (prefix em `DoApplyEffects`/`ApplyEffects` com caster = marionete), e **o dano continua vindo do host** pelo `DamageToClient`. Bônus: a marionete passa a animar o ataque (pendência da fase 3).
- A latência é meia RTT de atraso no aviso. As cargas de boss costumam durar bem mais que isso, mas é preciso medir.
- Evolução (fidelidade): deixar o dano do cast replicado valer no cliente (com os stats reais do jogador) e deixar o proxy do host imune a esse atacante. Fica pra depois do MVP, porque muda a autoridade do dano.

### Barra de vida de boss e UI
- `GameplayPanelManager.OnUpdate` (0x5F69F0) cria/remove um `BossHealthBarItem` por entidade em `GameManagerUtil.BossEntities` (`get_BossEntities` 0x6F8720: `BossSpawnProcessor.BossEntities` 0x68 quando `GameData.BossSpawnDetails` (0x58) existe, senão `GameData.BossEntities` 0x188). Respeita `GamePreferences.BossHealthBarsDisabled` (0x17).
- A marionete entra na lista sozinha (`PlaguebringerComponent.Setup`). Para tirar: chamar `GameManagerUtil.DeregisterBossEntity(marionete)` no `Died`/`Despawn` e zerar `PlaguebringerComponent.HealthBarRegistered` (0x161), pra não desregistrar duas vezes. Se a marionete for reciclada sem isso, a barra fica pendurada e o `BossEntities.Count` do cliente nunca cai.
- Efeito colateral no cliente: `BossSpawnProcessor.UnregisterBossEntity` (0x807A00) faz `_pendingBosses--` (fica negativo, porque o cliente nunca incrementou), enfileira `LastBossKilledData` e, com o affix de etéreos, chama `SpawnEtherealEntity`. Hoje isso não cria nada, porque `EtherealBossesToSpawnList` (0x90) só é preenchida em `SpawnBosses` (bloqueado no cliente). Mesmo assim, por segurança, bloquear `SpawnEtherealEntity` no cliente como já é feito com `SpawnBosses`.
- O apresentador de boss (`BossPresentationPanelReferences`, timelines em `TutorialLobby`/`GenericFinalBoss`) não aparece no fluxo do Survivors normal analisado.

---

## 5. Objetivos e progressão (mapa, sobrepõe o item A)

Fluxo no host (`SurvivorsGameManager`):
1. `<CreateObjectives>d__37` (0x43FE80): cria o `MainObjective` (`EliminateSpecificEntitiesObjective`, contagem = número de bosses de `BossList`) e o primeiro `EliminateEnemiesNonCumulativeObjective` (`GetEnemiesToEliminatePerIndex` 0x438A70, de `EnemiesToEliminatePerBoss` 0x28). Rastreia os dois no `ObjectivesPanelReferences.TrackObjective`.
2. A cada frame, em `UpdateAll` (0x438DC0): `kills = GameStats.EnemiesKilled (0x20) + EliteKilled (0x24) + BossKilled (0x28)` (ou só elites, conforme o `BaseGameData`) vai para cada objetivo de progresso. Ao completar: `GameProgressionData.CompletedProgressObjectiveProgression++` (`GameData+0x1C8`), voice line, `GameData+0x1D4` (tier) sobe, o objetivo sai e **`SpawnBosses`**. Os `GameManagerComponents` recebem `SetCurrentEnemiesKilled` (God Meteors, MapTasks).
3. Quando os bosses do lote morrem e `_pendingBosses` chega a 0, `BossSpawnProcessor.OnUpdate` chama `CreateNextProgressionObjective` (0x438700), que cria o próximo objetivo de kills e toca o voice line de morte do boss.
4. `MainObjective` completo (todos os bosses mortos): primeiro `StopSpawning()` (`GameData.StoppedSpawning` 0x1D9). No frame seguinte, com `StoppedSpawning` ligado: `MatchResult = Vitória` (`GameStats+0x9C`), `ResetAscensions`/`PauseAscension` e a corrotina `GameManagerUtil.OpenPortalAndObjective` (`<OpenPortalAndObjective>d__76`, 0x705120). Essa corrotina recicla a God-Entity (`GameData+0x28`), liga `HomePortalObject` (`+0x30`), Endless (`+0x40`) e BossRush (`+0x50`) conforme o checkpoint de progresso, cria o `SimpleObjective` "entre no portal" (`GameData+0x178`) e dispara `GameManagerUtil.OnPortalsOpened`.
5. Entrar no portal: `SurvivorsGameManager.OnPlayerEnterPortal` (0x438CF0) → `GameManagerUtil.ProcessOnPlayerEnterPortal` (0x6EE750: trava o jogador, esconde VFX, marca o objetivo) → `OnEnterHomePortal` (0x6EE160). Endless e BossRush têm `OnEnterEndlessPortal`/`OnEnterBossRushPortal`.

O que o cliente precisa receber pra objetivo e portal funcionarem:
- **Contagem de kills / progresso.** O `KillPuppet` usa `ChangeEntityHealth`, e o incremento de `GameStats` (0x20/0x24/0x28 conforme `GameStatsHealthType`) fica no ramo de morte **sem depender do atacante** (descompilado da fase 0). Então o contador do cliente provavelmente já anda com as mortes das marionetes. Isso contradiz a nota da fase 3; confirmar com log. Mesmo assim, a fonte da verdade deve ser o host: `ObjectiveProgress(completados, kills atuais, alvo)` ou os três contadores do `GameStats`.
- **Tier atual (`GameData+0x1D4`):** afeta o scaling das marionetes (`ApplyGenericGameModeEnemyScaling`) e o `SetupWithTier`.
- **Boss nasceu:** os campos do `BossQueueItem` (endereço, tiers, multiplicador de vida, time) + netId. O cliente cria o `EliminateSpecificEntitiesObjective` do boss e coloca a marionete no `MainObjective`, igual ao `SpawnBossRoutine`.
- **Boss morreu:** já é o `EnemyDied`; falta o `DeregisterBossEntity`.
- **Vitória/portal:** evento `PortalsOpened` do host (postfix em `GameManagerUtil.OpenPortalAndObjective`, ou assinatura de `OnPortalsOpened`). Risco: se o `MainObjective` do cliente completar sozinho (ele já tem `StoppedSpawning = true` desde a fase 1), o cliente abre o portal por conta própria no frame seguinte. Ou isso é aceito (fica sincronizado se as mortes forem as mesmas), ou é bloqueado com um prefix em `OpenPortalAndObjective` no cliente, liberado só pelo evento.
- **Entrada no portal / fim de run:** regra de co-op (os dois entram? basta um?). Itens A/revive.

---

## 6. Proposta

### 6.1 Chave de prefab (formato)

Mantém o que existe e acrescenta dois prefixos:

| Formato | Uso | Resolução no cliente |
|---|---|---|
| `Some/Prefab/Address` | Listas de tier do `EnemyPrefabsUtil` (sem mudança) | Como hoje (carrega `PrefabData`). |
| `res:<Resources path>` | Bosses, etéreos, void hunters, VoidPresence, loot goblin, minérios, cristais, pilares | `ResourcesUtil.Load<Entity>(path)`, com cache. |
| `sum:<netId dono>:<Nome>` | Invocações (prefab referenciado por skill) | Procura `Nome` nos `PossiblePrefabs` das skills de summon da marionete dona (`SkillCaster.Skills` → efeitos `SummonEntity*Effect`). Se não achar, cai no `@Nome`. |
| `@Nome` | Último recurso (como hoje) | `ResolveByName` (listas + `FindObjectsOfTypeAll`). |

**Como o host descobre o `res:`** (no `EnemyPrefabKeys.HostKeyFor`, antes do `@`):
1. **Dicionário nome → endereço** montado no `StartMatch` só com strings, sem carregar nada: `Global.BossPrefabs` (`Bosses/`, `Bosses/Ethereals/`), `Global.VoidHunterPrefabs` (`Creeps/` + `PrefabAddresses`), `AffixSpecialEntityToSpawnDefinitionSO.Addresses` do affix ativo, `Minerals/Heal-Crystal`, e os endereços `Minerals/{ItemId}-Ore` do mapa. A chave é o último segmento, comparado com `StripClone(nome)`.
2. **Sondagem por convenção** quando o nome não está no dicionário: tentar `Bosses/`, `Bosses/Ethereals/`, `Creeps/`, `Minerals/` e `Pillars/` com `ResourcesUtil.Load<Entity>` e aceitar só se o prefab carregado for o mesmo da instância. A instância sabe o próprio prefab pelo pool: `ResourcesUtil.PrefabIdByInstanceId[inst.GetInstanceID()]` deve ser igual ao `GetInstanceID()` do prefab. Cache por nome. Como o prefab já está carregado no host, o `Load` é barato.
3. Invocação: se `HostEnemySync` souber o dono (capturar o caster num prefix das corrotinas `SpawnUnits`, ou guardar o "skill caster atual" num prefix de `SummonEntityEffect.DoApplyEffects` 0x52B770), mandar `sum:`.

### 6.2 Mudanças no `EnemyPrefabKeys`

- `HostKeyFor`: ordem listas → dicionário `res:` → sondagem `res:` → `sum:` (com dono) → `@`.
- `ClientResolve`: tratar `res:` (`Load<Entity>`, `PrefabData = null`) e `sum:` (busca no dono). Não guardar no cache um resultado nulo de `sum:`, porque o dono pode chegar depois.
- `BuildNameIndex()` chamado no `StartMatch` dos dois lados. `Reset()` limpa.
- Pré-carga: `PreloadAddress(string)` com `Entity.PreloadAndFillPools(path, 2, 2)`.

### 6.3 Mudanças no `ClientPuppets`

- `EnemySpawnMsg` ganha um `Kind` (Regular, Special, Boss, Summon, Static) e, para Boss, `Tier`, `SkillsTier`, `LootTier`, `HealthMultiplier` e `Team`. Dá pra estender o pacote 7 com um byte de versão/flags, ou criar um pacote `BossSpawn`.
- Criação `res:` genérica: `CacheReferences` + `CopyStats(prefab)` + `SetToOne` antes do `Setup` (como os spawners especiais). Minérios, cristais e pilares: `UsePathFollowing = false`, só `SetPosition` (já é o caminho sem `_hasPathFollowingBehaviour`).
- Boss: antes do `Setup`, `PlaguebringerComponent.SetupWithTier(entity, tier, skillsTier, lootTier)` (ou `MasteryBossComponent.SetupWithTier`) e `_stats.MultiplicativeStats.MaxHealth *= HealthMultiplier`. Depois do `Setup` e do `DisableAI`, a barra de boss já está registrada. Criar o objetivo do boss (seção 5). Guardar `IsBoss` na `Puppet`.
- `KillPuppet`/`DespawnPuppet`/`OnEntityRecycle`: se `IsBoss`, chamar `GameManagerUtil.DeregisterBossEntity` e zerar `HealthBarRegistered`. Fazer isso **antes** do `Recycle`.
- `ClientSpawnBlock`: prefix → false em `BossSpawnProcessor.SpawnEtherealEntity` (0x806E20) no cliente. Revisar a allowlist de LEAK para não acusar as estátuas (elas não são Entity, então não devem aparecer).
- Replay de cast: `OnEnemyCast` → `CastSkill` na marionete, com os efeitos bloqueados. Reaproveitar o bloqueio de `DoApplyEffects` da 4C, estendendo o critério "caster = lançador remoto" para "caster é marionete".

### 6.4 Eventos novos (números livres a partir de 14; combinar com as sessões 4A/4B/4C, que também podem estar criando pacotes)

| Pacote | Direção | Conteúdo | Entrega |
|---|---|---|---|
| `BossPlan` | host → cliente | Lista de nomes da `BossList` do host, no início da partida, pra pré-carga (e, opcionalmente, pra sobrescrever a `BossList` do cliente) | ReliableOrdered |
| `BossIncoming` | host → cliente | Endereço do boss, posição/rotação da estátua, `TimeToBreakStatue`. O cliente mostra a estátua e os VFX e pré-carrega. | ReliableOrdered |
| `EnemySpawn` estendido | host → cliente | `Kind` + bloco de boss (6.3) | ReliableOrdered |
| `EnemyCast` | host → cliente | netId, índice/nome da skill, pos/dir alvo, netId alvo (ou "jogador local") | ReliableUnordered, ou Sequenced por netId |
| `GodCast` | host → cliente | posição do meteoro (a God-Entity do cliente lança localmente) | ReliableUnordered |
| `ObjectiveProgress` | host → cliente | `GameStats` 0x20/0x24/0x28, `GameData+0x1D4`, completados | ReliableOrdered (só quando muda) |
| `PortalsOpened` | host → cliente | quais portais abriram | ReliableOrdered |

### 6.5 Ordem de implementação

1. **`res:` + VoidPresence + void hunters + loot goblin** (chave e resolução). Critério: `unresolved=-` no log do cliente com o affix VoidPresence ativo, e o cliente vê e mata o VoidPresence.
2. **Minérios, cristais e pilares com `res:`** (tira a dependência do `FindObjectsOfTypeAll`).
3. **Boss básico:** `EnemySpawn` com bloco de boss, `SetupWithTier`, barra de boss, `DeregisterBossEntity` na morte, bloqueio de `SpawnEtherealEntity`. Critério: barra aparece e some junto com o host, sem exceção.
4. **Invocações com `sum:`.**
5. **Replay de cast de inimigo (`EnemyCast`)**, depois que a 4C estabilizar o bloqueio de efeitos: telegrafia dos bosses, animação de ataque, `AutoCastPillar`. Depois o `GodCast`.
6. **Objetivo do boss + `ObjectiveProgress` + `PortalsOpened`** (alinhar com o item A).
7. Polimento: `BossPlan`/pré-carga, `BossIncoming` com estátua, música de boss.

### 6.6 Riscos

1. **Hitch no `Load` síncrono do boss** no cliente: mitigar com `BossPlan`/`BossIncoming` (seção 3).
2. **Barra de boss pendurada** se algum caminho reciclar a marionete sem `DeregisterBossEntity` (o recycler, a limpeza de `StartMatch`, a desconexão). Centralizar no `OnEntityRecycle`.
3. **`sum:` antes do dono:** o summon pode chegar na fila antes do boss virar marionete. Manter em `Pending` alguns frames antes de desistir (hoje o cliente desiste na primeira tentativa).
4. **Replay de cast criando efeitos reais:** summons locais, dano dobrado, buffs. O bloqueio de `DoApplyEffects`/`ApplyEffects` tem que pegar **todo** caster marionete, e o `ChangeEntityHealth` precisa de rede de segurança (atacante marionete, alvo jogador local, sem `ApplyingNetworkDamage` → ignora).
5. **Objetivo/portal do cliente andando sozinho:** com `StoppedSpawning` já ligado no cliente, um `MainObjective` completo abre o portal localmente no frame seguinte (seção 5). Decidir se é aceito ou se o portal fica preso ao evento do host.
6. **Música de boss:** depende de `_hasPendingBosses` (0xDC), que só `SpawnBosses` liga. Se o cliente ligar o flag pra ter a música, `BossSpawnProcessor.OnUpdate` passa a chamar `CreateNextProgressionObjective` quando `_pendingBosses == 0`, e esse contador fica negativo no cliente. Sem música de boss no MVP, ou ajustar `_pendingBosses` junto.
7. **`LootUtil.ProcessLoot` e ações de time:** os bosses podem rodar `KillAllEntitiesAction`/`StopEntityTeamAction` (fase 3b) e enxergar o proxy Players sem Inventory. Testar boss com o cliente conectado e o log aberto.
8. **Chave dos void hunters:** `"Creeps/" + PrefabAddresses[j]` está confirmado no código, mas os valores do SO não foram lidos. Logar no host na primeira vez.
9. **Ordem de `Setup` virtual:** `PlaguebringerComponent.Setup` lê `GameManagerUtil.PlayerEntities` (vira `_playerEntities` 0x168). No cliente isso é o jogador local, o que está certo. Mas o `Setup` precisa rodar depois do `StartMatch` (lição da fase 2; o `Pending` já garante).

### 6.7 Como testar (forçar boss rápido)

- **Comandos do jogo:** `GameCommands.SpawnBoss(prefabName, tier, quantity, additional)` (0x7A0D20, e as sobrecargas 0x7A0DC0/0x7A0ED0) chama `CheatManager.SpawnBoss` (0x6D8E20), que acha o nome em `Global.BossPrefabs` (ou sorteia com `GetRandomBossPrefabAddress` 0x6E2A70) e chama o `SpawnBosses` normal (estátua, objetivo e tudo). **Só funciona se `CheatManager.Instance` (estático 0x10) existir.** `GameCommands.SpawnBoss` não faz nada quando é nulo, e numa build release isso é provável. Também há `GameCommands.SpawnVoidHunter`, `SpawnMineral` e `KillAllEnemies`.
- **Hotkey de debug no mod (host), recomendado:**
  - Boss pelo fluxo real: `GameManagerUtil.GameData.BossSpawnDetails.SpawnBosses(GameManagerUtil.PlayerEntity, tier, tier, tier, Team.Creeps, false, 0, null, true)`. Pega o próximo da `BossList` do host.
  - Boss pelo objetivo (testa também a seção 5): somar no `GameStats.EnemiesKilled` (0x20) o que falta pro `EliminateEnemiesNonCumulativeObjective` atual. No frame seguinte o `UpdateAll` dispara o boss sozinho.
  - Void hunters: `GameManagerVoidHunterComponent.SpawnVoidHunter()` no componente da lista `GameData.GameManagerComponents` (o Il2CppInterop expõe métodos privados).
  - VoidPresence: replicar a receita do `SpawnAffixSpecialEntities` com `Creeps/VoidPresence-Survivors` (é o caso mais simples pra validar o `res:`).
- **Critérios no log:** no cliente, `unresolved=-` e o primeiro `'res:Bosses/…' -> … hostMaxHP=X localMaxHP≈X`. A barra de boss aparece e some nos dois lados. Zero `LEAK` e zero exceção. No host, `[enemies]` com a chave de cada especial na primeira vez.

---

## Resumo

- No gameplay não tem Addressables: todo especial é `Resources`. O cliente cria com `ResourcesUtil.Load<Entity>(caminho)` + o pipeline da fase 2 (`CacheReferences`, `CopyStats`, `SetToOne`, `Setup`).
- Chave nova `res:<caminho>`, descoberta no host por um índice nome → endereço montado dos SOs (`Global.BossPrefabs`, `VoidHunterPrefabs`, affix) e por sondagem verificada pelo pool. Invocações usam `sum:<dono>:<nome>`.
- Boss: o endereço é `Bosses/<Nome>`, e a lista é sorteada por máquina, então mande o endereço, nunca o índice. Setup extra: `PlaguebringerComponent.SetupWithTier` + multiplicador de vida. A barra entra sozinha no `Setup`, mas o cliente tem que chamar `DeregisterBossEntity` na morte.
- VoidPresence (affix `EliteAffix-VoidPresence`/MapTask), void hunters, loot goblin e etéreos são marionetes. Minérios, cristais e pilares são marionetes estáticas.
- Baús, MapTasks, estátuas e God Meteors não são `Entity`: precisam de eventos (spawn interativo, cast da God-Entity).
- A telegrafia vem do `SkillChargingTimeTrait` dentro do `Skill.Activate` do inimigo. Pra ver, replique o cast (`EnemyCast`) e rode `CastSkill` na marionete com efeitos bloqueados, como na 4C. O dano continua do host.
- Objetivos: kills → `SpawnBosses` → bosses mortos → `StopSpawning` → `OpenPortalAndObjective`. O cliente precisa de progresso, tier, objetivo do boss e `PortalsOpened`. Cuidado: com `StoppedSpawning`, o portal do cliente pode abrir sozinho.
- Ordem: `res:` + VoidPresence → estáticos → boss básico → `sum:` → `EnemyCast` → objetivos/portal → pré-carga e estátua.
- Teste rápido: hotkey no host chamando `BossSpawnDetails.SpawnBosses`, ou somando kills no `GameStats`. `GameCommands.SpawnBoss` depende de `CheatManager.Instance`, provavelmente nulo na build release.
- A confirmar em runtime: valores dos endereços dos void hunters, se o `GameStats` do cliente já anda com o `KillPuppet`, e o hitch do primeiro `Load` de boss.
