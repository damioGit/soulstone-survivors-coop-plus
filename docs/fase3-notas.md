# Fase 3: notas para a fase 4

Testado com duas instâncias no mesmo PC (SurvivorsDesert, Hound Master nos dois lados).

## Etapa 3a: magias do cliente machucam os inimigos do host (commit 15b0d45)

Confirmado pelo usuário e pelos logs: o cliente mata inimigos com as próprias magias, eles morrem nas duas janelas e o XP cai no host, no ponto da morte.

- **Cliente (`ClientPuppets.RecordDamage`, chamado no `ClientPuppetHealthClampPatch`)**: antes do clamp (vida ≥ 1), soma `-healthChange` por netId. Hits cujo atacante é do time inimigo (marionetes se acertando, God-Entity do mapa) são ignorados, porque o host já simula esses hits nos inimigos reais. A cada 1/15 s (unscaled) manda `ClientDamage = 11`, ReliableUnordered: `count u8 | count × (netId u16, dano f32)`, até 200 entradas por pacote.
- **Host (`HostEnemySync.OnClientDamage`)**: `TryGet(netId)` (mapa novo `ByNetId`) e `UnitUtil.ChangeEntityHealth(null, inimigo, -dano, yieldLoot: true, …)`. A morte volta pelo `EnemyDied` de sempre. Entradas de inimigo já morto ou desconhecido são descartadas (`unknownOrDead`, 0 a 3 a cada 5 s, normal: hits que chegam depois do Died).
- **Medido:** 4 a 13 kills do cliente a cada 5 s, `immune=0`, nenhuma exceção. Uns 10 a 20 pacotes por segundo com o cliente atacando, cada um com poucos bytes.
- **Dano somado por tick:** vários hits no mesmo inimigo viram uma chamada só no host. Para dano isso é equivalente, mas efeitos por hit no host (som, número de dano, thresholds) acontecem uma vez por tick. Como attacker é null, o host não ganha procs de on-kill pelos kills do cliente, e o cliente também não (ver abaixo).

## Etapa 3b: inimigos perseguem e atacam o cliente

Confirmado pelo usuário e pelos logs: os inimigos do host vão atrás do cliente, o cliente toma dano e morre (tela de "you died" local), e depois da morte os inimigos mudam o aggro para o host. O host segue normal, com XP e loot e sem exceção.

Código em `src/SoulstoneCoop/Enemies/RemoteProxy.cs`.

- **Proxy (host):** é um `Creeps/Dummy` montado pela receita do `CheatManager.SpawnDummy` (0x6D99F0): `ResourcesUtil.Load<Entity>`, `GetPrefabInstance`, time, `CacheReferences`, `MultiplicativeStats.SetToOne`, `Entity.Setup`. Vai com `Team = Players`. Não é jogador: sem Inventory, `isPlayer=False` no log.
  - Componentes do Dummy: Rigidbody, CapsuleCollider, CharacterMovement, NpcCharacter, PhysicalComponent, SkillCaster, NavMeshAgent, Team, Health (100 M de HP), SkillPriority, Loot, Stats, Placeholder, AbnormalStatus, CharacterLayer, StaticAI e Stunnable. Não tem BasicAI.
  - Antes do Setup: `CanBeRecycledByDistance = false`, `IgnoredByUnitLimit = true`, `IgnoredByEntitySearchQueries = false`, `HasSpawnSound = false`, `VfxOnSetup = null`, `AvoidCreatingHealthBar` e `IgnoreMinimapTracking`. Depois do Setup: StaticAI desligado e todos os renderers desligados.
  - Criado no tick quando já houve StartMatch no host, o fantasma existe e já chegou `PlayerState` (lição da fase 2). A cada frame vai pra posição do fantasma com `ModifiedCharacter.SetPosition`.
  - Ao desconectar: o time, o VFX e os renderers são restaurados e o proxy volta ao pool com `Entity.Recycle`.
- **Patch 9, dano no proxy (host):** prefix em `ChangeEntityHealth` quando o alvo é o proxy. O host manda `DamageToClient = 12` (`healthChange f32`, `attackerNetId u16`, ReliableOrdered) e devolve false. `effectiveHealthChange = healthChange` faz o número de dano aparecer no fantasma. Cura no proxy é ignorada.
- **Aplicação (cliente, `ClientIncomingDamage`):** `ChangeEntityHealth(marioneteAtacante ou null, jogadorLocal, valor, false, …)` com a flag `ApplyingNetworkDamage`. Ignora o pacote se o jogador já morreu ou se o match local não começou. Death guard, i-frames (`immune` no log), UI e "you died" seguem o caminho do jogo.
- **`ClientState = 13` (cliente → host, a cada 0,5 s e na hora da morte ou do revive):** `dead u8 | hp, maxHp, armor (ArmorModifier), damageReduction, blockChance f32`. O host copia isso em `proxy._stats.Stats` e na vida, então as contas de dano que leem o alvo usam os números do cliente: % de vida máxima, armadura, bloqueio, execute.
  - Antes disso, o cliente tomava ~6 por hit com a defesa do Dummy. Depois, ~2,6 por hit, perto dos ~3 do host. Stats fora dessa lista (esquiva, resistências, afixos do jogador) ainda vêm do Dummy.
- **Cliente morto:** `IgnoredByEntitySearchQueries = true`, o colisor do proxy é desligado e `BasicAIComponent.RemoveTarget()` roda em quem tinha o proxy como `_entityToFollow`. No teste, 20 inimigos soltaram e voltaram pro host. Spawn e recycler deixam de considerar o cliente. Se ele reviver (`dead=false`), tudo isso é revertido.
- **Patch 7, spawn (host):** postfix em `GameManagerUtil.GetRandomSpawnPointOutsideOfView`. Quando o `referencePoint` é o host (distância < 2 u), um a cada dois pontos é sorteado de novo em volta do cliente, por chamada recursiva com guarda. A troca do `__result` (struct) funcionou no Il2CppInterop: `spawnRedirects=7/14`, `8/16`.
- **Patch 8, recycler (host):** prefix e postfix em `EntityRegistry.DefaultContinuousRecycler`. Durante a chamada, as entidades a menos de 35 u do cliente ficam com `CanBeRecycledByDistance = false`, e o flag é restaurado depois. Não precisou reimplementar nem mexer no `ref int`.
- **Patch 13, PlayerSeekerAI (host):** prefix em `PlayerSeekerAIComponent.DoOnUpdate` (0x7D6F20), que só lê `_transformToFollow`. Troca `_entityToFollow`, `_transformToFollow`, os `_has*` e `_entityToFollowRadius` pelo mais próximo entre o host e o proxy (só o host quando o cliente está morto). **Não foi exercitado no teste** (`seekerRetargets=0`: não apareceu nenhum seeker no Desert).
- Os inimigos comuns não precisaram de patch: `BasicAIComponent.RefreshTarget` já escolhe a entidade Allies mais próxima.

### Hack obrigatório: time Summons durante o loot

`LootUtil.ProcessLoot` (0x6FA180) chama `AIUtil.GetAllEntitiesInSpecificTeam(Players)` e lê o Inventory (`Entity+0x3B8`) de cada entidade **sem checar null**. Com o proxy em Players, toda morte com loot lançava NRE dentro do `ChangeEntityHealth` (131 vezes no primeiro teste), cortando o resto do processamento da morte. Correção (`HostLootProxyTeamPatch`): prefix que põe o proxy em `Team.Summons` e finalizer que devolve pra `Players`, com uma rede de segurança no tick. Os inimigos miram em Allies (Players | Summons), então nada muda pra eles. Os outros chamadores de `GetAllEntitiesInSpecificTeam` são cheats, testes ou ações de cutscene/boss (`KillAllEntitiesAction`, `StopEntityTeamAction`, `MoveEntityTeamToCenterAction`…). **Atenção na fase 4:** essas ações de time podem rodar em bosses ou eventos e enxergar o proxy. Qualquer código novo que percorra "jogadores" por time precisa lembrar que existe uma Entity Players sem Inventory.

### Achados

- **`VoidPresence-Survivors` mata com hit fixo de 1.669.998**, independente da vida máxima do alvo. No teste, ele matou o cliente parado na origem. Isso parece mecânica do mapa contra jogador parado, mas o cliente não vê o atacante, porque essa entidade não tem marionete (chave `@VoidPresence-Survivors` não resolvida, fase 2). Vale confirmar se o mesmo acontece jogando sozinho parado.
- O número de dano no fantasma aparece no host, porque o patch devolve o valor como efetivo.
- O dano chega por hit (não em lote), ReliableOrdered. O volume é baixo, uns 10 hits a cada 5 s com o cliente cercado.

## Pendências para a fase 4

- **Game over e revive em co-op:** hoje cada lado tem o próprio "you died". O cliente morto fica com a tela de morte e o host segue jogando; quando o host morre, o jogo dele acaba sozinho. Falta decidir a regra (acaba quando os dois morrem? revive pelo parceiro?) e replicar. O `ClientState.dead` e a reversão no proxy já existem para o revive. A morte do host não é tratada no cliente.
- **Magias do outro jogador não aparecem:** o host não vê as magias do cliente e vice-versa. Na tela de quem lança, tudo normal, e o dano chega (3a). Precisa replicar eventos de cast e tocar o visual no fantasma, que não é Entity: algo como `SkillUtil.RunFireAndForgetVfxReference` ou instanciar só o VFX da skill na posição e direção recebidas. O mesmo vale para os ataques dos inimigos no cliente: a marionete não anima o ataque, só o dano chega.
- **Crédito de kill e procs on-kill do cliente:** a `AttackerList` da marionete continua sendo limpa antes da morte de rede, e o host aplica o dano com attacker null. Para dar crédito: não limpar a lista quando o cliente acertou a marionete, ou mandar `KillCredit(netId)` e chamar `ProcessOnKilledEntity` no cliente. As estatísticas de kill (`GameStats`) só andam no host.
- **XP/loot no cliente:** só o host recebe (o `ProcessLoot` roda no host com `yieldLoot`). Patch 10 da fase 0. Lembrar do hack do time Summons ao mexer no `ProcessLoot`.
- **Bosses e entidades por endereço:** `@VoidPresence-Survivors`, bosses e Void Hunters continuam sem marionete. Mandar o endereço real (`BossSpawnProcessor`, `GetVoidHunterPrefabAddresses`) e carregar com `ResourcesUtil.Load<Entity>(endereço)`.
- **Stats do proxy incompletos:** só vida, armadura, redução de dano e bloqueio são copiados. A solução fiel continua sendo replicar o cast do inimigo e calcular o dano no cliente (fase 0, item 5).
- **Status negativos no cliente:** stun, slow, burn e afins aplicados ao proxy ficam no host. Só o dano dos DoTs chega, porque passa pelo `ChangeEntityHealth`.
- **Patch 13 sem teste:** validar num mapa com `PlayerSeekerAIComponent`.
- **Host morto:** o proxy continua alvo válido e o spawn continua alternando; nada disso foi pensado para o host morto com o cliente vivo.
