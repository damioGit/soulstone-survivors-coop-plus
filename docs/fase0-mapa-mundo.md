# Fase 0: mapa do "mundo" do Soulstone Survivors (v1.5.3.2)

Investigação estática, só leitura (Il2CppDumper `dump.cs`/`script.json`/`il2cpp.h` e Ghidra headless sobre `tools/GameAssembly.dll`). Os RVAs são relativos à image base `0x180000000`. Os offsets de campo vêm do `dump.cs`. No mod, os campos aparecem como propriedades do Il2CppInterop, então os offsets só servem pra conferir o descompilado.

As saídas brutas do Ghidra ficaram em `tools/decompiled_fase0_batch{1,2,3}.txt` e nas versões `_named.txt`, onde cada `FUN_`/`DAT_` vem anotado com o nome resolvido. Esses arquivos estão no `.gitignore` e não são versionados.

---

## Resumo executivo

- **O "porteiro" do `GameLoop.Update` não tem nada a ver com jogador.** `DAT_183395318` é `Global_TypeInfo` e o offset 200 (0xC8) é `Global.GamePreferences`. O check só ajusta `Time.maximumDeltaTime` conforme `GamePreferences.PhysicsCPULimit`. Quando `GamePreferences` é nulo, o jogo lança uma NullReferenceException. O travamento que vimos ao clonar o Entity tem outra explicação: cada `UpdateAll` faz `break → throw NullReferenceException` quando encontra um elemento ou campo nulo. Como `GameLoop.Update` chama uns 150 sistemas em sequência dentro de um único método, uma exceção num sistema anterior pula todos os seguintes naquele frame, inclusive `PlayerSkillAutoCastComponent.UpdateAll`. Ou seja, o caminho A esbarra em inicialização incompleta e em vários `PlayerEntities[0]` fixos no código, não num singleton global. Fica só como contexto: **o caminho B está confirmado** e é a base de todo o plano abaixo.
- **A IA dos inimigos não depende de um singleton "o jogador".** O `BasicAIComponent` escolhe como alvo a entidade mais próxima do time adversário, varrendo `EntityRegistry.AllEntities` (`AIUtil.TryGetEntityByDistanceCondition`). Qualquer Entity do grupo `Allies` (Players=1 ou Summons=2) que esteja registrada vira alvo. Por isso inimigos já perseguem invocações. Pra fazer os inimigos do host perseguirem o cliente, basta um **proxy aliado registrado** (uma entidade leve, que não é jogador) posicionado onde o cliente está.
- **Todo dano passa por um único funil:** `UnitUtil.ChangeEntityHealth` (RVA 0x73BE30). Isso vale para skills, projéteis, áreas, DoTs (burn/bleed/poison) e o ambiente. O valor já chega calculado (crítico, multiplicadores, runas), e a morte, as estatísticas de kill e o loot/XP acontecem dentro dele. É o ponto ideal pra interceptar no cliente e pra aplicar no host.
- **O spawn é centralizado.** Os componentes `GameManager*SpawnComponent` alimentam filas, `GameManagerSpawnQueuesComponent.ProcessSpawnQueues` consome as filas, `EnemyPrefabsUtil.SpawnEnemyEntity` cria o inimigo (com pool), e o ponto de spawn é escolhido em volta da posição de referência do jogador. O jogo já tem um interruptor nativo, `GameManagerUtil.StopSpawning()`.
- **O hit detection depende de colisor + `EntityRegistry`.** Uma magia do cliente só acerta algo que seja um Entity registrado e tenha colisor. Por isso recomendo que, no cliente, os inimigos do host sejam **marionetes**: Entities de inimigo de verdade, criadas pelo próprio pipeline de spawn, com a IA desligada e a posição dirigida pelos snapshots do host. É diferente do fantasma só visual que usamos para o jogador remoto. Inimigos não são jogadores (`IsPlayer()` é falso), então não reabrimos o problema do segundo jogador.
- **Volume:** os limites padrão no código são 30 a 40 inimigos vivos (os prefabs podem mudar isso). Existe um teto físico de `ModifiedCharacter.kMaxEntities = 300`. Um snapshot de 300 inimigos a 12 bytes por inimigo dá ~3,6 KB; a 10–20 Hz isso é ~36–72 KB/s no pior caso, o que é tranquilo. Projéteis não precisam ser sincronizados.

---

## 1. Spawn de inimigos

**Cadeia (modo Survivors):**

1. `SurvivorsGameManager.<Start>d__31.MoveNext` (0x442A80) instancia os componentes a partir de `Resources/Game-Manager-Components/*`: Spawn-Queues, Overlord-Spawn-Queues, Regular-Enemy-Continuous-Spawn, Elite-Enemy-Timed/Continuous-Spawn, Twitch-Enemy-Spawn, Mineral, Healing-Crystal, Pillars-Of-Despair/Protection, VoidHunter, God-Meteors, Interactable-Guarded-Reward, Affix-Special-Entities, Discord/Steam. Todos ficam em `GameData.GameManagerComponents` (0x140). Depois chama `EnemyPrefabsUtil.Setup(mapId, isEndless)` (0x6E3AA0), que preenche as listas por tier (`Tier1..5`, `Tier*Elite`, `Tier*EliteGroups`).
2. A cada frame, `SurvivorsGameManager.UpdateAll` (0x438DC0, chamado pelo `GameLoop.Update`) roda `OnUpdate(tier, difficultyRating, matchTime)` (slot 6) em cada componente.
3. `GameManagerRegularEnemyContinuousSpawnComponent.OnUpdate` (0x4197A0) é um timer (`_unitSpawnTimeUntilNextWave`) que chama `DoSpawnRegularUnits` → `SpawnRegularUnits` (0x4199F0). Esse método sorteia prefabs por "power" (`EnemyPrefabsUtil.TryGetRandomWeightedEnemyPrefabByProgression`) e **enfileira** `EntityByEnemyPrefabData` (Tier, Prefab, PrefabData, ViewerName, ExtraDistanceMultiplier). A wave só sai se `EntityRegistry.GetCurrentActiveEntitiesCount()` estiver abaixo de `min(MaxTargetEnemiesAlive, difficulty × TargetEnemiesAliveDifficultyFactor)`. Os defaults do ctor são 40, ×8, mult 4 e 2 s entre waves.
4. `GameManagerSpawnQueuesBaseComponent.OnUpdate` (0x41A3B0) é outro timer (`DelayBetweenSpawns`, default 0,05 s) que chama `GameManagerSpawnQueuesComponent.ProcessSpawnQueues` (0x41A5E0). Esse método desenfileira na ordem Twitch → Medium → Low (uma por tick, se `ActiveEntities ≤ AliveUnitLimit`, default 30) e chama:
5. `EnemyPrefabsUtil.SpawnEnemyEntity(prefabData, extraDistanceMultiplier, isElite, referenceSpawnPoint)` (0x6E4760), que faz:
   - `GameManagerUtil.GetRandomSpawnPointOutsideOfView(referencePoint, mult)` (0x6ED660): um ponto num retângulo de ±24 × ±18 (×(1+mult)) em volta da referência, com `NavMesh.SamplePosition`. **A referência é a posição de `_playerEntity`**, o jogador host.
   - `ResourcesUtil.GetPrefabInstance<Entity>` (pool), `UnitUtil.SetEntityBasedOnPrefabData`, `ApplyGenericEnemyScaling`, `ApplyGenericGameModeEnemyScaling`, `Entity.Setup()` (que registra no `EntityRegistry`) e, por fim, `OverheadHealthBarPanelManager.RequestHealthBar`.

**Outros chamadores diretos de `SpawnEnemyEntity`:** `GameManagerOverlordSpawnQueuesComponent.ProcessSpawnQueues` (0x417820), `InteractableGuardedRewardObject.TrySpawnGuardians` (0x455660), `MapKillElitesInAreaTask.DoOnUpdate` (0x45AD80) e `GameManagerLootEnemySpawnerComponent.OnUpdate` (0x80F2E0). Os bosses vêm de outro lugar: `BossSpawnProcessor.SpawnBosses/SpawnSpecificBoss` (0x806510/0x807350), disparados pelo `SurvivorsGameManager.UpdateAll` quando objetivos de kill se completam. Há ainda uns 40 chamadores de `Entity.Setup` (summons, pilares, void hunters, quests etc.).

**Como desligar no cliente com segurança:**
- `GameManagerUtil.StopSpawning()` (0x6F7C30) liga `GameData.StoppedSpawning` (0x1D9), limpa a fila de bosses e chama `DisableComponent()` (slot 5) em **todos** os `GameManagerComponents`. `DisableComponent` só zera `_isActive` e limpa as filas. O próprio jogo usa isso na fase de boss e em cheats. **Plano:** um postfix em `GameManagerUtil.StartMatch` (0x6F47A0), ou o fim do `Start` do game manager, chama `StopSpawning()` no cliente. Um prefix em `GameManagerUtil.ResumeSpawning` (0x6EEB80) devolve `false` no cliente (Crusade/Mastery chamam isso).
- Prefix devolvendo `false` no cliente em `BossSpawnProcessor.SpawnBosses` (0x806510) e `SpawnSpecificBoss` (0x807350). Isso importa principalmente se sincronizarmos a contagem de kills, que é o que move os objetivos.
- Rede de segurança e diagnóstico: um postfix em `Entity.Setup` (0x80B060) no cliente que registra em log qualquer Entity com `Team & Monsters` criada fora da rede. Com isso dá pra pegar vazamentos (MapTasks, guardiões, quests). **Não** recomendo um prefix que devolva null em `SpawnEnemyEntity`: vários chamadores desreferenciam o retorno sem checar.

## 2. Identidade

- `Entity.Id` é um `ushort` (0x20), atribuído **uma vez por instância** em `Entity.CacheId` (0x808AF0) a partir do estático `Entity.NextEntityId` (com flag `HasCachedId`). Como as entidades vêm de pool, **o mesmo Id volta quando o objeto é reciclado e reaproveitado**. O Id identifica o GameObject do pool, não o spawn, e é diferente entre as duas máquinas.
- Registro global: `EntityRegistry` (estático):
  - `AllEntities : List<Entity>` (0x8). Usa swap-remove, então a ordem não é estável.
  - `EntityIdToEntityIndexPlusOne : ushort[]` (0x0) dá busca O(1) com `EntityRegistry.TryGetEntity(ushort id, out Entity)` (0x4251D0).
  - `_colliderInstanceIdToEntity` e `_rigidBodyInstanceIdToEntityIndex` (`TryGetEntityByCollider` 0x424EE0 e `TryGetEntityByColliderInstanceId` 0x424E20) são o que o hit detection usa.
  - `RegisterEntity` (0x424AC0) é chamado por `Entity.Setup` e por `HealthComponent.UpdateAll`; `UnregisterEntity` (0x4252C0) por `Entity.Recycle` (0x80A520). O teto é 0xFFFE entidades.
  - `GetCurrentActiveEntitiesCount()` (0x4249C0) é o que os spawners comparam com o limite (desconta `EntitiesThatDoNotCountTowardsLimit`, ligado ao flag `Entity.IgnoredByUnitLimit` 0x232).
- Por componente: `ComponentList<T>.Instances` (`BasicAIComponent`, `HealthComponent`, `PlayerCharacter.Players`, `ModifiedCharacter.ModifiedCharacters`…). `OnEnable` e `OnDisable` adicionam e removem da lista.
- **Recomendação:** um `netId` (u16 ou u32) atribuído pelo host num postfix de `Entity.Setup` para entidades `Team ∈ Monsters` e não-jogador, guardado em `Dictionary<netId, Entity>` dos dois lados. A morte e a reciclagem vêm de um prefix em `Entity.Recycle` e da morte em `ChangeEntityHealth`. Não usar `Entity.Id` como chave de rede.

## 3. Alvo e IA

- `BasicAIComponent` (`Instances`; `UpdateAll` 0x5E3A00 chama `DoOnUpdate` (slot 9, 0x5E33C0), que chama `CheckIfCanMove` e depois `DoBasicBehaviour` 0x5E3100):
  - **Aquisição de alvo**: corrotina `RefreshTarget` (`<RefreshTarget>d__47.MoveNext`, 0x5EB840), iniciada no `Setup` (0x5E36A0) e repetida a cada `RefreshTargetDelay` s. Em ordem de prioridade: se há taunt (alvo forçado em `SkillCaster+0x150`), usa ele. Se `GetTargetFromSkill`, usa `SkillPriorityComponent.GetHighestPrioritySkillIndexAndTargetFromSkill`. Senão usa `AIUtil.TryGetEntityByDistanceCondition(team, TargetAllegiance.Enemies, posição, MaxRangeToGetTarget, LessThan, FavorBossAndEliteTargets)` (0x8042A0), que **percorre `EntityRegistry.AllEntities`**, ignora `IgnoredByEntitySearchQueries` (0x233), filtra pelo grupo de time (`UnitUtil.GetTeamGroupFromTeam`) e devolve a mais próxima. O resultado vai para `_transformToFollow` (0xA8), `_entityToFollow` (0xB8) e `_entityToFollowRadius` (0xC0).
  - **Movimento e ataque**: `BehaviourWhenBestSkillSelected` (0x5E2640). Se `dist² ≥ 0,9·range²`, chama `ModifiedCharacter.MoveToLocation(alvo)` (0x7A5480). Senão para, gira e dispara a corrotina `CastSkillMultipleTimes`, que chama `SkillCasterComponent.CastSkill`. Sem skill disponível, `BehaviourWhenNoBestSkillSelected` só para.
- Times: `Players=1, Summons=2, Creeps=4, Titans=8`. Os grupos são `Allies=3` e `Monsters=12`. Para inimigos, qualquer entidade Players ou Summons registrada é alvo válido.
- Exceções que **usam o jogador fixo**: `PlayerSeekerAIComponent.<RefreshTarget>d__5` (0x7E0600) sempre pega `GameManagerUtil.PlayerEntities[0]`. `EnvironmentalDamageOnTriggerEnterFollowPlayerSkill.OnUpdate` e `GameManagerUtil.get_PlayerEntity` têm uns 55 chamadores (UI, câmera, recycler, spawners, mapas). `StaticAIComponent.GetTarget` (0x7FB220) também usa `TryGetEntityByDistanceCondition`.
- **O que patchar pra mirarem no mais próximo entre o jogador local e o remoto:**
  - Opção recomendada, **proxy aliado no host**: uma Entity leve que não é jogador (sem `Inventory`, então `IsPlayer()` dá falso; `IsPlayer` só lê `Entity+0x3B0`, a referência do Inventory), com `Team = Players`, colisor, `HealthComponent` invulnerável, `IgnoredByUnitLimit = true` e `CanBeRecycledByDistance = false`. Ela é movida a cada tick para a posição do cliente e fica com os renderers desligados (o visual já é o fantasma). A IA e os hits passam a funcionar sem patch nenhum na IA. Candidato a prefab: `Creeps/Dummy`. Confirmado: `CheatManager.SpawnDummy` (0x6D99F0) faz `ResourcesUtil.Load<Entity>("Creeps/Dummy")`, depois `GetPrefabInstance`, `CacheReferences`, `Stats.CopyStats`/`SetToOne`, `Entity.Setup` e `RequestHealthBar`, que é exatamente a receita do proxy (trocando o `TeamComponent.Team` para Players). Falta validar em jogo.
  - Alternativa: postfix em `BasicAIComponent.<RefreshTarget>d__47.MoveNext` que troca `_transformToFollow`/`_entityToFollow` se o remoto estiver mais perto. Mesmo assim precisa de uma Entity (o `_entityToFollow` é lido por `DoBasicBehaviour` e para mirar a skill), então não economiza o proxy.
  - Um prefix em `PlayerSeekerAIComponent.<RefreshTarget>d__5.MoveNext` cobre o caso especial.

## 4. Dano: do hit até a morte

1. **Cast:** `SkillCasterComponent.CastSkill` (0x7F0D00) chama `Skill.Activate` (0x5619B0), que chama `DoActivate` (virtual; cria projétil ou área).
2. **Detecção** (sempre colisor → `EntityRegistry`): `SkillOverlapUtil.OnLateUpdate` / `ProcessSkillFireAndForgetOverlapResults` (0x724880, 0x725750…); `PhysicsUtil.AttemptToCompleteSphereCastJobs` (0x6FD710) → `TryGetEntityByColliderInstanceId`; `LineProjectile.OnTriggerEnter` (0x54DBA0) e `StaticProjectile.OnTriggerEnter` (0x555CA0) → `TryGetEntityByCollider`; raios `Channeling*`; `TargetAreaTrigger.OnTriggerEnter` (0x582270).
3. `Skill.ApplyEffects(targets, …)` (0x562040) chama `Skill.DoApplyEffects` (0x562280), que chama `SkillEffect.ApplyEffect` (virtual). Para dano: `ChangeHealthEffect.ApplyEffect`/`DoApplyEffects` (0x5210E0/0x521460) **calcula o valor final** (`CalculateRandomHealthChange`, crítico, distância, expose, brittle, condemned, runas, extra stats). O cálculo usa os stats do atacante **e do alvo**. Em seguida chama:
4. **`UnitUtil.ChangeEntityHealth(attacker, target, healthChange, yieldLoot, out effective, out absorbed, out isImmune)` (0x73BE30)**, onde `healthChange < 0` é dano. O que acontece dentro, em resumo:
   - Retorna logo se `HealthComponent.HasBeenKilled` (0x118). Fica imune se `InvulnerabilityTime` (0xF0) for maior que 0. Para jogador, aplica a redução de afixo e de modo. Escudos temporários absorvem antes.
   - `CurrentHealth` (0xF4) += valor; `TookDamage`; `HealthComponent.RegisterAttacker` (só se attacker ≠ null); thresholds.
   - Se `CurrentHealth ≤ 0`: o jogador pode gastar um *death guard*. Caso contrário, `CurrentHealth = 0` e `HasBeenKilled = !IgnoreDeathState`. Cada entrada da `AttackerList` recebe `SkillTree`/`Runes`/`PassiveSkills.ProcessOnKilledEntity` (procs de on-kill). `GameStats` +1 em 0x20, 0x24 ou 0x28 conforme `GameStatsHealthType` (Enemy/Elite/Boss). Toca os sons de morte. Se `killed && yieldLoot && tem Loot`, chama `LootUtil.ProcessLoot` (0x6FA180). Por fim ragdoll, `Stunnable.SetDead`, `SkillCaster.CancelExecution`, `SetMovementMode(0)`.
   - O dissolve e a reciclagem acontecem depois, em `HealthComponent.UpdateAll` (0x7C0270).
   - DoTs passam pelo mesmo funil: `AbnormalStatusComponent.UpdateAll`/`ApplyEffects` (0x4F8A30/0x4F1E40) chamam `ChangeEntityHealth`.
5. **Interceptar no cliente** (marionete): prefix em `ChangeEntityHealth` quando `target` é marionete e `healthChange < 0`. O prefix acumula `(netId, -healthChange)` por tick para enviar ao host e **limita** o valor para a vida não passar de 1. O original continua rodando, então número de dano, flash e som aparecem, mas a morte, o loot e o kill nunca acontecem localmente.
6. **Aplicar no host:** `UnitUtil.ChangeEntityHealth(null, inimigo, -dano, yieldLoot: true, …)`. Com attacker nulo não há `RegisterAttacker`, então o host não ganha procs de on-kill pelos kills do cliente. A morte segue o caminho normal no host e é replicada como evento "morreu netId".

## 5. Dano no jogador

- Não encontrei um sistema separado de "dano por contato". Os ataques de inimigo são **skills** do próprio inimigo (lançadas pela IA via `CastSkillMultipleTimes` → `SkillCasterComponent.CastSkill`). O efeito é `ChangeHealthEffect` (ou variantes como `DamageOnTriggerEnterEffect`), e no fim chega em `ChangeEntityHealth(attacker = inimigo, target = jogador)`. Há também DoTs, perigos de ambiente (`EnvironmentalSkillArea`, `EnvironmentalDamageOnTriggerEnterFollowPlayerSkill`, meteoros do `GameManagerGodMeteorsComponent`) e o "apocalipse" (`GameManagerUtil.CastApocalypse`).
- **Ponto de interceptação no host:** prefix em `ChangeEntityHealth` quando `target == proxyDoCliente`. Envia `DanoNoCliente(valor, netIdAtacante)` e devolve `false`, de modo que o proxy nunca morre.
- **Aplicação no cliente:** `ChangeEntityHealth(marioneteAtacante ou null, jogadorLocal, valor, false, …)`. Assim o death guard, a UI e o game over locais (`SurvivorsGameManager` termina quando todos os `GameData.Players` estão mortos) funcionam naturalmente.
- **Limitação:** o valor que o host calcula usa os stats defensivos do **proxy**, não os do jogador do cliente (veja o item 4.3). Para um MVP isso é aceitável, talvez com um fator de correção. A solução fiel é replicar o **cast** do inimigo: o host envia `inimigo X lançou skill i em alvo/posição`, a marionete no cliente executa `SkillCasterComponent.CastSkillAtTarget` (0x7F04B0) localmente, e o dano contra o jogador do cliente é calculado lá, com os stats corretos. Nesse caso o proxy do host fica imune.

## 6. XP, drops e pickups

- A morte com `yieldLoot` chama `LootUtil.ProcessLoot(experience, prestige, currencies, possibleItems, powerUps, sourcePosition, instant, interval)` (0x6FA180). Daí vem `XpDropper.Drop(pos, amount, interval)` (0x763590) e as moedas/itens/power-ups (`SpawnPowerUpRequestLoot`). A experiência e as moedas vêm do `EnemyPrefabData`/`LootComponent` do inimigo.
- `XpDropper` é estático e tem dois modos:
  - **Instanciado** (`UseInstancedXpRender`, 0x18A): as orbs ficam num `XpCpuState` nativo, com jobs. `OnUpdate` (0x7640B0) usa a posição de **`GameManagerUtil.PlayerEntities[0]`** e `CollectionDistanceSqrdThreshold` para atrair e coletar. `OnLateUpdate` (0x763DA0) chama `XpDropper.TryGetPlayer` (0x764C80, também `PlayerEntities[0]`) e depois `ExperienceComponent.AddExperience` (0x6CDEB0).
  - **Objetos**: `CollectableExperience`, que é um `CollectableObject` (pool). Também há moedas, vida etc. O `CollectObjectsAreaTrigger` (colisor do jogador, 0x55BC60) junta os objetos, e `PlayerCollectItemComponent.UpdateAll` (0x7D6C80) chama `CollectableObject.OnCollect(entity)`. O efeito acontece em `DoOnReachedTarget` (por exemplo `CollectableExperience`, que chama `AddExperience`, ou `CollectableHealth`, que cura via `ChangeEntityHealth`).
- O raio de coleta vem de `PlayerCollectItemComponent.SetRadius`, que chama `XpDropper.SetCollectionRadius` (0x764BF0). Está amarrado ao jogador, que em cada máquina é o jogador local.
- **Implicação para o caminho B:** o host replica `ProcessLoot` (prefix em `LootUtil.ProcessLoot` envia os argumentos) e o cliente chama `LootUtil.ProcessLoot` local com os mesmos argumentos. Cada um coleta as orbs no seu mundo, o que na prática vira **XP compartilhado**. Isso é simples e combina com o gênero. Se quisermos XP dividido, é só escalar o `amount`.

## 7. O "porteiro" do `GameLoop.Update` (RVA 0x6EAA00)

> **Só contexto.** O usuário confirmou o caminho B. Esta seção não faz parte do plano. A lição prática para o B está no risco 6: qualquer NullReferenceException dentro de um `UpdateAll` derruba o resto do frame.

- Pelo `script.json`, `0x183395318` é `Global_TypeInfo`. `+0xB8` são os static fields, e o offset 200 = 0xC8 é **`Global.GamePreferences`** (`dump.cs`, classe `Global`, TypeDefIndex 829).
- O bloco só lê `GamePreferences.PhysicsCPULimit` (0x3D) e ajusta `Time.maximumDeltaTime` (`2 × fixedDeltaTime` ou 1,0). Logo depois lê `Global.ForceCleanEventSystemSelectedObjectKeyboard` (0x1F1) para limpar a seleção do EventSystem. O `goto` em `GamePreferences == null` leva a `FUN_180337CA0`, que é o lançador de NullReferenceException do IL2CPP. **Não existe check de jogador ou de entidade aí.**
- O que de fato "quebra o frame": todos os `UpdateAll` seguem o mesmo padrão `ComponentList` e fazem `break → throw NRE` ao achar um elemento nulo ou uma referência nula num campo esperado (veja `PlayerSkillAutoCastComponent.UpdateAll`). Como `GameLoop.Update` é um método só, que chama em sequência `HealthComponent`, `PlayerCharacter`, `PlayerCameraController`, `PlayerCollectItemComponent`… e só depois `PlayerSkillAutoCastComponent.UpdateAll`, uma exceção em qualquer sistema anterior por causa de um clone mal inicializado pula o resto do frame. Isso bate com "o jogador real para de lançar magia". (Isso foi inferido do código. Para confirmar, procurar a NRE no `Player.log`/BepInEx de quando o clone existia.)
- **O que isso diz sobre o caminho A:** não há um singleton bloqueando. `GameData.Players` é uma `List<Entity>` (0x60) com `PlayerIndex` (0x68), e o game over do `SurvivorsGameManager.UpdateAll` já itera todos os jogadores. Mesmo assim, A exigiria (a) inicializar o segundo jogador pelo pipeline completo (`GameManagerUtil.SetupPlayerForMatch`, 0x705A20: CacheReferences, skills, câmera, UI) e (b) tratar dezenas de usos de `PlayerEntities[0]`/`get_PlayerEntity` (XP, PlayerSeekerAI, recycler, referência de spawn, UI). É caro e frágil. **B continua sendo o caminho.** Fica registrado aqui como informação, não como proposta.

## 8. Volume

- Defaults do código (os prefabs em `Resources/Game-Manager-Components/*` podem sobrescrever): `AliveUnitLimit = 30`, `DelayBetweenSpawns = 0,05 s`, `MaxTargetEnemiesAlive = 40` (factor 8 × difficultyRating). `SurvivorsSandboxGameManager` tem os mesmos campos. Elites, bosses, summons e minérios somam por cima.
- Tetos duros: `ModifiedCharacter.kMaxEntities = 300` (buffer dos jobs de física de personagem) e `EntityRegistry` ≤ 0xFFFE.
- **Estimativa de dimensionamento:** típico de 40 a 150 personagens, pior caso 300. Um snapshot por inimigo: netId 2 B, x/z quantizados 4 B, y 2 B, yaw 1 B, HP% 2 B, flags 1 B, ~12 B no total. 300 × 12 = 3,6 KB por snapshot, o que dá 36–72 KB/s a 10–20 Hz sem compressão. Com delta (só quem se mexeu) ou 10 Hz com interpolação, cai bastante. Cabe num `LiteNetLib` unreliable-sequenced, mas acima de ~1,2 KB por pacote é melhor fatiar.
- Projéteis: **não sincronizar**. Os do jogador são simulados por quem lança. Os de inimigo viram eventos de cast (fase 3), se e quando fizermos.
- Fase 1: medir em runtime, logando `EntityRegistry.AllEntities.Count` e `GetCurrentActiveEntitiesCount()` a cada segundo, para confirmar os valores reais dos prefabs.

---

## Dependências (quem depende de quem)

```
GameLoop.Update (0x6EAA00) ── ~150 sistemas em série; um NRE pula o resto do frame
 ├─ SurvivorsGameManager.UpdateAll
 │    ├─ GameManagerComponents[*].OnUpdate ─► filas ─► SpawnQueues.ProcessSpawnQueues
 │    │        └─► EnemyPrefabsUtil.SpawnEnemyEntity ─► GetRandomSpawnPointOutsideOfView(pos do PlayerEntity)
 │    │                 └─► Pool ─► Entity.Setup ─► EntityRegistry.RegisterEntity
 │    ├─ objetivos (GameStats kills) ─► BossSpawnProcessor.SpawnBosses
 │    ├─ game over: todos GameData.Players mortos
 │    └─ EntityRegistry.DefaultContinuousRecycler(PlayerEntity): recicla > 35 u (10/frame)
 ├─ BasicAIComponent.UpdateAll ─► MoveToLocation / CastSkill
 │    └─ RefreshTarget (corrotina) ─► AIUtil.TryGetEntityByDistanceCondition ─► EntityRegistry.AllEntities (time adversário)
 ├─ Skill/SkillProjectile/SkillAreaEffect.UpdateAll, SkillOverlapUtil, PhysicsUtil
 │    └─ colisor ─► EntityRegistry.TryGetEntityByCollider* ─► Skill.ApplyEffects ─► ChangeHealthEffect
 │              └─► UnitUtil.ChangeEntityHealth ◄─ AbnormalStatus (DoT), ambiente, cheats
 │                     └─ morte ─► GameStats, on-kill procs (AttackerList), LootUtil.ProcessLoot ─► XpDropper.Drop
 ├─ XpDropper.OnUpdate/OnLateUpdate ─► PlayerEntities[0] ─► ExperienceComponent.AddExperience
 ├─ PlayerCollectItemComponent.UpdateAll ─► CollectableObject.OnCollect
 └─ TimeUtil.SetTime (PauseTime/LevelUpTime/…) ─► timeScale
```

## Pontos de patch Harmony propostos

| # | Lado | Alvo (RVA) | Tipo | O que fazer |
|---|---|---|---|---|
| 1 | Cliente | `GameManagerUtil.StartMatch` (0x6F47A0) | Postfix | Chamar `GameManagerUtil.StopSpawning()`. |
| 2 | Cliente | `GameManagerUtil.ResumeSpawning` (0x6EEB80) | Prefix → false | Impede religar os spawners. |
| 3 | Cliente | `BossSpawnProcessor.SpawnBosses` / `SpawnSpecificBoss` (0x806510 / 0x807350) | Prefix → false | Bosses só via rede. |
| 4 | Cliente | `Entity.Setup` (0x80B060) | Postfix | Diagnóstico: logar inimigo criado fora da rede (vazamento de spawn). |
| 5 | Host | `Entity.Setup` (0x80B060) | Postfix | Se `Team & Monsters` e não-jogador: atribuir netId e enviar `Spawn(netId, prefabName, pos, tier, elite, maxHP)`. |
| 6 | Host | `Entity.Recycle` (0x80A520) | Prefix | Enviar `Despawn(netId)` e remover do mapa. |
| 7 | Host | `GameManagerUtil.GetRandomSpawnPointOutsideOfView` (0x6ED660) | Prefix (ajusta `referencePoint`) | Alternar a referência entre o host e a posição do cliente, para distribuir os spawns. |
| 8 | Host | `EntityRegistry.DefaultContinuousRecycler` (0x424690) | Prefix → false + reimplementar | Reciclar só se a distância for > 35 u de **todos** os jogadores (host e remoto). |
| 9 | Host | `UnitUtil.ChangeEntityHealth` (0x73BE30) | Prefix | `target == proxyCliente`: enviar dano ao cliente e devolver false. `target` é inimigo e morreu (postfix): enviar `Died(netId)`. |
| 10 | Host | `LootUtil.ProcessLoot` (0x6FA180) | Prefix | Enviar os argumentos para o cliente reproduzir o drop. |
| 11 | Cliente | `UnitUtil.ChangeEntityHealth` (0x73BE30) | Prefix | `target` marionete: acumular dano para o host e limitar para a vida ficar ≥ 1. Dano vindo da rede no jogador local: deixar passar (flag de reentrância). |
| 12 | Cliente | Marionete: `BasicAIComponent`/`StaticAIComponent` | Sem patch: `enabled = false` depois do `Entity.Setup` | O `OnDisable` (0x5E34F0) chama `ComponentList.Remove` (confirmado), então `UpdateAll` ignora. No Unity, desabilitar um MonoBehaviour **não** para as corrotinas dele, então também é preciso chamar `BasicAIComponent.StopRefreshingTarget()` (0x5E3960). Também ligar `CanBeRecycledByDistance = false` e `IgnoredByUnitLimit = true`. |
| 13 | Ambos | `PlayerSeekerAIComponent.<RefreshTarget>d__5.MoveNext` (0x7E0600) | Postfix (host) | Escolher o mais próximo entre o jogador e o proxy. |
| 14 | Ambos | `TimeUtil.SetTime` (0x729F60) | Prefix/Postfix | Política de pausa em co-op (level-up e pause não podem congelar o mundo do outro). |
| 15 | Cliente | `XpDropper` / `LootUtil` | Nenhum | O drop replicado via `ProcessLoot` local já usa `PlayerEntities[0]` = jogador local. |

Movimento das marionetes: `ModifiedCharacter.MoveToLocation(alvoInterpolado)` (0x7A5480) para manter animação e navmesh, com `ModifiedCharacter.Teleport` (0x7A61C0) quando o erro passar de ~3 u. A vida vem do snapshot e é escrita direto em `HealthComponent.CurrentHealth`.

Criação da marionete no cliente: o mesmo pipeline do `SpawnEnemyEntity` (`ResourcesUtil.GetPrefabInstance<Entity>` + `UnitUtil.SetEntityBasedOnPrefabData` + `Entity.Setup`), com o prefab resolvido pelo nome a partir das listas do `EnemyPrefabsUtil` ou de `Global.EnemyPrefabs`/`BossPrefabs`/`VoidHunterPrefabs`. Como o mapa é o mesmo, as listas são as mesmas depois de `EnemyPrefabsUtil.Setup`.

## Riscos

1. **Prefab por nome:** precisamos de uma chave estável de prefab entre as máquinas (nome do prefab ou endereço). Os void hunters usam addresses (`GetVoidHunterPrefabAddresses`). Validar na fase 2.
2. **Stats do proxy ≠ stats do cliente:** o dano que o host calcula contra o proxy ignora a defesa real do cliente (item 5). O MVP aceita isso; a correção é replicar os casts.
3. **Procs de on-kill do cliente:** as passivas e runas que disparam em kill (`ProcessOnKilledEntity` via `AttackerList`) não disparam pro cliente, porque a morte só acontece no host. Mitigação: um evento `KillCredit(netId)` faz o cliente chamar `PassiveSkillsComponent/RunesComponent/SkillTreeComponent.ProcessOnKilledEntity` passando a marionete.
4. **Progressão do cliente:** sem kills locais, os objetivos de `SurvivorsGameManager` (GameStats 0x20/0x24/0x28) não andam no cliente. Opções: sincronizar os contadores (e aí bloquear os bosses locais, patch 3) ou sincronizar o progresso de objetivos e portais via eventos. Vitória e portal (`OpenPortalAndObjective`) precisam de um evento do host.
5. **Pausa e level-up:** `TimeUtil.SetTime` (0x729F60, chamado a cada frame pelo `GameLoop.Update`) faz `Time.timeScale = PauseTime × LevelUpTime × DeathGuardTime × SlowMotionTime × ControllerDisconnectedTime × CreditsTime × PlayerDataTime` (confirmado). Se o host pausar, o mundo inteiro congela para o cliente. Precisa de política (pausa sincronizada ou sem pausa em co-op).
6. **Exceções no `GameLoop.Update`:** qualquer NRE nosso dentro de um patch de `UpdateAll`, ou num Entity incompleto (proxy ou marionete), derruba o resto do frame. Todo patch precisa de try/catch, e o proxy precisa passar por `Entity.Setup` completo, não por clone manual.
7. **Recycler e limite:** marionetes e proxy contam em `GetCurrentActiveEntitiesCount`, a menos que `IgnoredByUnitLimit` esteja ligado, e podem ser recicladas por distância se o flag não for desligado.
8. **Pool e Id:** reutilização de instância. Sempre enviar Despawn antes de reaproveitar e nunca confiar em `Entity.Id` entre máquinas.
9. **Hardcodes de `PlayerEntities[0]`:** UI, câmera, `PlayerSeekerAI`, ambiente. No caminho B são corretos localmente, mas alguns spawns e efeitos de ambiente do host só "veem" o host.
10. **Valores reais dos prefabs:** os limites de spawn ficam serializados nos prefabs. Os defaults do código podem não ser os efetivos; medir na fase 1.

## Recomendação: fases 1 a 4

- **Fase 1, cliente "mudo" + telemetria.** Patches 1–4 no cliente (sem spawn local) e logs de volume (item 8) no host. Critério de pronto: o cliente roda uma partida sem nenhum inimigo próprio e sem exceção.
- **Fase 2, marionetes.** Patches 5–6 no host. Mensagens Spawn/Despawn/Died e snapshot de 10–20 Hz (posição, yaw, HP). No cliente, as marionetes são criadas pelo pipeline do jogo, com a IA desabilitada (12) e movidas por `MoveToLocation`/`Teleport`. Critério de pronto: o cliente vê os inimigos do host se movendo e morrendo, e o auto-aim do cliente mira neles.
- **Fase 3, dano bidirecional + perseguição.** No cliente, o patch 11 manda o dano em lote; o host aplica com `ChangeEntityHealth(null, …)`. No host, o proxy aliado do cliente (`Creeps/Dummy` com time trocado, ou outro prefab leve) e os patches 7, 8, 9 e 13. O dano no cliente é encaminhado (MVP), com replicação de cast como evolução. Critério de pronto: os dois matam inimigos, os inimigos perseguem e machucam os dois.
- **Fase 4, economia e fluxo da partida.** Patch 10 (drops e XP compartilhado), `KillCredit` para procs, sincronização de progresso, bosses, portal e vitória, política de pausa (14) e game over em co-op.

---

**Ferramenta usada:** script Ghidra `DecompAndXrefs.java` (lê `RVA|Label|D/C/DC` e grava a decompilação e/ou a lista de chamadores) mais `annotate.awk` (resolve `FUN_`/`DAT_` via `dump.cs`/`script.json`). As cópias ficaram na pasta de notas compartilhada da sessão. Posso movê-las para `tools/ghidra_scripts/` se for útil.
