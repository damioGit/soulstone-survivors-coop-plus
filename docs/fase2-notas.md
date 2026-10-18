# Fase 2: notas para a fase 3

Testado com duas instâncias no mesmo PC (SurvivorsDesert, Hound Master nos dois lados). O usuário confirmou: o cliente vê os inimigos do host nas mesmas posições, andando com animação e morrendo quando morrem no host. A pausa sincronizada continua funcionando, e o fantasma do host aparece no cliente.

## O que entrou

Código em `src/SoulstoneCoop/Enemies/`.

- **Host (`HostEnemySync.cs`)**
  - Postfix em `Entity.Setup`: toda Entity de time `Monsters` que não é jogador (fora a God-Entity) ganha um `netId` u16 próprio. `Entity.Id` não é usado. O host manda `EnemySpawn` com chave do prefab, tier, elite, posição, yaw, HP máximo e HP%.
  - Prefix em `Entity.Setup` guarda `!HasBeenSetUp`. Se um Setup "real" acontece numa instância ainda rastreada (veio do pool sem passar pelo nosso Recycle), o host manda Despawn e um Spawn novo. Isso não apareceu no teste; é só proteção.
  - Patch manual (em `Plugin.Load`, com try/catch próprio) em `EnemyPrefabsUtil.SpawnEnemyEntity`: o prefix captura `EntityByEnemyPrefabData.Prefab`/`Tier` e `isElite`, e o `Entity.Setup` que roda dentro dele consome esse contexto. O patch aplicou sem problema, apesar do parâmetro struct por valor.
  - Prefix em `Entity.Recycle`: manda `EnemyDespawn`. Confirmado no teste (`despawns=3`, `despawns=18`).
  - A morte é detectada por polling de `HealthComponent.HasBeenKilled` a cada frame nas entidades rastreadas, e vira `EnemyDied`. Não há patch no `ChangeEntityHealth` do host.
  - Snapshot a 15 Hz (unscaled), `Unreliable`, formato em `EnemyMessages.cs`. São 16 B de cabeçalho mais 10 B por inimigo: netId, dx/dz em int16 a 1/32 u, dy a 1/64 u relativos à posição do host, yaw em u8 e HP% em u8. É fatiado em até 118 inimigos por pacote (≤ 1,2 KB). Cada fatia é independente, e o cliente descarta entradas velhas por sequência de cada marionete.
  - Quando o peer conecta, o host reenvia o Spawn de tudo o que está vivo e adota entidades inimigas que ainda não estavam rastreadas.
- **Cliente (`ClientPuppets.cs`)**
  - As marionetes são criadas pelo pipeline do `SpawnEnemyEntity`: `ResourcesUtil.GetPrefabInstance<Entity>`, depois `SetEntityBasedOnPrefabData` (só quando há PrefabData e tier válido), `ApplyGenericEnemyScaling(isElite)`, `ApplyGenericGameModeEnemyScaling`, `Entity.Setup` e por fim `OverheadHealthBarPanelManager.RequestHealthBar`. Tudo isso roda com `ClientSpawnBlock.NetworkSpawnInProgress` ligado.
  - Antes do Setup: `CanBeRecycledByDistance = false` e `IgnoredByUnitLimit = true`. Depois: `BasicAIComponent.StopRefreshingTarget()` + `enabled = false` (isso cobre o `PlayerSeekerAIComponent`, que herda dele) e `StaticAIComponent.enabled = false`.
  - Movimento: `MoveToLocation` até a última posição do host, extrapolada pela velocidade (com 0,1 s de lead e no máximo 0,35 s). Se o erro horizontal passa de 3 u, `Teleport`. Parado, gira pro yaw do host. Inimigos sem `_hasPathFollowingBehaviour` usam `SetPosition`. Nada se move com `timeScale = 0`.
  - HP: `CurrentHealth = max(1, HP% × max local)` a cada snapshot.
  - Died: limpa a `AttackerList`, zera `InvulnerabilityTime` e chama `ChangeEntityHealth(null, marionete, -(HP+1), yieldLoot: false)` com uma flag de reentrância. Morte, som, ragdoll e dissolve seguem o caminho normal, e o próprio jogo recicla depois. Se a marionete sobreviver (escudo), ela é reciclada.
  - Despawn: `Entity.Recycle()`. É o mesmo que o recycler de distância faz: marca killed + instant dissolve, e o `HealthComponent.UpdateAll` devolve a instância ao pool. O Despawn de uma marionete que está morrendo é ignorado, pra não cortar a animação de morte.
  - Prefix em `Entity.Recycle` no cliente: tira a marionete do mapa antes de a instância do pool voltar como outra.
  - Prefix em `UnitUtil.ChangeEntityHealth`: dano numa marionete é limitado pra vida ficar ≥ 1. O hit continua rodando (número, flash, som). No log isso aparece como `clampedHits`.
- **Chave de prefab (`EnemyPrefabKeys.cs`)**: é o `PrefabAddress` das listas do `EnemyPrefabsUtil`, por exemplo `Creeps/Beetle` ou `Creeps/Lizard`. Os dois lados rodam `EnemyPrefabsUtil.Setup` no mesmo mapa, então as listas batem. O PrefabData vem de `_prefabDatas[PrefabDataIndex]`. Quando o spawn não passou pelas listas, a chave vira `@NomeDoPrefab`, e o cliente procura pelo nome nas listas e depois em `Resources.FindObjectsOfTypeAll<Entity>` (só assets, cena inválida). No teste, a vida máxima ficou igual nos dois lados.
- **Fantasma do jogador remoto (`CoopBehaviour.RebuildGhostIfModelInactive`)**: o fantasma é reconstruído quando o modelo clonado está inativo. Veja abaixo.

## Descobertas que a fase 3 precisa saber

- **Ordem no cliente:** `GameplayUIManager.Setup` (o cliente conecta e recebe os Spawn) acontece **antes** do `GameManagerUtil.StartMatch` local. Os Spawn ficam numa fila e só viram marionete depois do `StartMatch`. O `StartMatch` **não** pode limpar o rastreio: isso deixou 18 marionetes órfãs, paradas, no primeiro teste. Entidades de cena antiga caem pelo check de `null`/inativo que roda a cada frame.
- **Fantasma invisível no cliente:** quando o cliente clona o jogador, o filho do modelo (`HoundMasterRegular(Clone)`, o que tem o Animator) ainda está inativo, porque o jogo só liga ele depois. O clone ficava sem corpo, só com o círculo azul no chão. A suspeita do dissolve do `HealthComponent.AppearEffectRoutine` (0x7CB270) foi descartada: o material já estava em −2, visível. A correção reclona o fantasma quando o modelo do jogador real está ativo e o do fantasma não. **Para o proxy do cliente na fase 3, vale a mesma lição: não clone nem configure nada a partir do jogador antes do `StartMatch`.**
- **`despawns=0` com o jogo pausado é normal:** o dissolve usa `Time.deltaTime`, então com `timeScale = 0` nada recicla e `tracked` fica maior que os vivos até despausar.
- **Volume medido:** 40 a 74 inimigos rastreados (pico de 74 no Desert), 360 a 500 B por snapshot em 1 fatia, ~4 a 7 KB/s. O erro médio de posição das marionetes foi de 0,7 a 1,1 u (máximo de ~3 u, com 0 a 3 teleportes a cada 5 s). Dá pra melhorar o erro com mais lead ou interpolação, mas visualmente ficou bom.
- **Chave não resolvida:** `@VoidPresence-Survivors` (entidade especial do Void, fora das listas de tier) aparece como `unresolved` no log do cliente e não vira marionete. O host continua mandando o snapshot dela (`unknownNetIds` ~60 a cada 5 s). Bosses e Void Hunters provavelmente caem no mesmo caso, porque são carregados por endereço fora das listas. Na fase 4, mandar o endereço real (`BossSpawnProcessor`/`GetVoidHunterPrefabAddresses`) ou carregar por `ResourcesUtil.Load<Entity>(endereço)`.
- **HP da marionete "volta":** o cliente acerta, a barra desce e o snapshot seguinte restaura a vida do host. Isso some quando a fase 3 mandar o dano pro host.
- **Ataques não são replicados:** o host não vê as magias do cliente, nem o contrário. Isso não faz parte da fase 3 (dano). Se quisermos o efeito visual da magia do outro jogador, precisa de uma etapa própria (eventos de cast replicados no fantasma).

## O que a fase 3 deve fazer em cima disto

- **Dano do cliente → host:** no `ClientPuppetHealthClampPatch` (prefix de `ChangeEntityHealth`), acumular `(netId, -healthChange)` antes do clamp e mandar em lote. O host aplica com `UnitUtil.ChangeEntityHealth(null, entidade, -dano, yieldLoot: true, ...)`, e a morte volta pelo `EnemyDied`, que já existe. O mapa netId → Entity do host está em `HostEnemySync` (`All`/`ByPtr`); falta só um `TryGet(netId)`.
- **Proxy aliado no host + inimigos atacando o cliente:** patches 7, 8, 9 e 13 da fase 0. O proxy precisa ser criado depois do `StartMatch` (veja a ordem acima). O `HostEnemySync.IsTrackable` só olha o time `Monsters`, então um proxy de time `Players` não é replicado como marionete por engano.
- **Procs de on-kill no cliente:** hoje a `AttackerList` da marionete é limpa antes da morte de rede. Pra dar crédito de kill (fase 4), basta **não** limpar a lista quando o cliente tiver acertado a marionete, ou mandar um `KillCredit`.
- **Pacotes livres:** `PacketType` usa até 10 (`EnemySnapshot`). Os tipos 1, 3, 4 e 5 continuam reservados e sem uso.
