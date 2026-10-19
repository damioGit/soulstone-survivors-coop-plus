# Fase 9: até 8 jogadores (investigação e desenho)

Só leitura do código do master `ffcd97b` (0.8.0) e das notas das fases. Nada foi implementado. Pedido do usuário: "Vamos fazer com limite de 8 pessoas para dar uma zoada mesmo!!!" — 1 host + até 7 clientes na mesma run.

**Atenção para o PASSO 2:** duas sessões ainda mexem em `VisualGhostFactory`, `PlayerSync`, `RemoteCaster`/`CastSync` (fase 5: fantasma, magias, companheiros) e no painel de level-up (`LevelUpPolicy`, `PauseSync`, `CoopUi`). Os números de linha abaixo são do `ffcd97b`; o desenho não depende deles.

---

## 0. Resumo

- O mod inteiro assume **um** parceiro: estáticos de módulo (`_proxy`, `_caster`, `Remote`, `RemotePlace`, `Buffer`, `RemoteReason`, `PartnerHealth.*`, `GameState.RemoteGhostObject`) e booleanos "o outro está…". São ~44 usos de `HasPeer` e 31 chamadas de `NetworkManager.Send` em 16 arquivos.
- **Topologia: estrela.** O host continua sendo o único dono do mundo. O que um cliente manda e os outros precisam ver, o host **repassa** com o slot de origem.
- **Id do jogador = slot (u8):** 0 = host, 1–7 = clientes, dado pelo host no `Welcome` (pacote novo). O cliente nunca escreve o próprio slot: o host carimba a origem pela conexão de onde o pacote veio (não dá pra falsificar).
- **Como o slot entra nos pacotes:**
  - Um envelope `Relayed = 100` (`origem u8 | pacote original`) para tudo o que é repassado. O formato dos pacotes de hoje não muda, e o `PacketRouter` só ganha um "de quem veio".
  - Um campo de slot explícito só nos pacotes do host que falam **sobre** um jogador (alvo de cast, dono do XP, revive, lista de caídos).
  - A regra de struct ≤ 8 bytes vale só pra assinaturas de patch Harmony, não pra pacote.
- **Banda do host com 7 clientes:** perto de 70–90 KB/s de subida (~0,7 Mbps), dominada pelos snapshots de inimigos (o mesmo snapshot vai pra cada cliente). Cabe numa internet doméstica e muito abaixo dos limites por conexão da Steam e do LiteNetLib.
- **Entidades extras:**
  - no host, 14 Dummies (7 proxies + 7 lançadores), todos `IgnoredByUnitLimit`;
  - em cada cliente, 7 fantasmas + 7 lançadores.
  - O custo real é o **VFX das magias de 8 builds** em toda máquina. Proposta: não reproduzir casts de jogadores longe da câmera, o que não muda nada no jogo, porque as réplicas são só visuais.
- **Balanceamento:** o limite de inimigos vivos é **global** (30–40 por padrão, `AliveUnitLimit`/`MaxTargetEnemiesAlive`), e o spawn alterna entre os jogadores. Com 8 jogadores espalhados, cada um veria uns 5 inimigos. Proposta: `[Coop] EnemyScalePerPlayer` (0 = desligado no primeiro teste; sugestão de 0,25 depois), aplicado nos limites dos spawners no `StartMatch` do host, com teto.
- **Plano em 6 etapas** (§6), cada uma compilando e jogável com 2:
  1. ids e transporte multi-peer;
  2. estado por jogador;
  3. repasse;
  4. lobby e UI com vagas;
  5. regras com N;
  6. balanceamento e desempenho.

  O teste com 3 ou mais é por IP, com várias instâncias no mesmo PC e `--coop-name` (§8).

---

## 1. Inventário: tudo que é "um parceiro só"

Legenda do que vira: **P** = vira um por jogador remoto (lista indexada por slot); **R** = o host passa a repassar com a origem; **U** = o host manda só para um cliente (unicast); **N** = regra nova com N jogadores.

### 1.1 Rede e transporte

| Onde | O que é hoje | Com N |
|---|---|---|
| `Net/ITransport.cs` `HasPeer`, `PingMs`, `SilenceSeconds`, `Send(data, len, method)` | Um parceiro só. `Send` vai "pro outro" | `HasPeer` passa a ser "tem alguém". Entram `PeerSlots`, `PingMs(slot)`, `SilenceSeconds(slot)`, `Send` (host: todos; cliente: o host), `SendTo(slot)` e `SendExcept(slot)`. Callbacks: `Connected(slot)`, `Disconnected(slot)` e `Data(reader, fromSlot)` |
| `Net/NetworkManager.cs` `PeerConnected`/`PeerDisconnected` (eventos sem argumento), `Unstable`, `LogStability` | Disparam uma vez para "o" parceiro. Os módulos limpam estado **global** no disconnect | Os eventos levam o slot. No cliente, a queda do host vira "todos saíram". `Unstable` passa a ser por slot (o banner diz quem). Entram `Broadcast`, `SendTo`, `SendExcept` e `Relay` |
| `Net/LiteNetTransport.cs` `_peer` (um `NetPeer`), `OnConnectionRequest` → "O host já tem um parceiro", `PeerDisconnectedEvent` ignora quem não é `_peer` | 1 cliente | `Dictionary<int peerId, slot>`. Recusa com "O host está cheio (N/N)" acima de `MaxPlayers − 1`. `Send` de broadcast com `SendToAll`/laço. MTU de 1024 por peer, como hoje |
| `Net/SteamTransport.cs` `_peer` (um SteamID), `_connected`, `_seqIn`/`_seqOut`, `_lastHeard`, `PingMs`, `_sessionTimer`, `OnHello` → "O host já tem um parceiro", `PeerGone`, `OnMemberLeft`, `OnSessionFailed` | Uma sessão `SteamNetworkingMessages` | Um `Peer {steamId, slot, connected, lastHeard, ping, pingTimer, seqIn, seqOut}` por cliente. Ping, timeout (15 s/45 s), estado da sessão (1×/s) e sequência do canal 1 passam a ser por peer. O `Accept` (41) leva o slot. `Hello` repetido de quem já está dentro reenvia o `Accept`. `OnMemberLeft(id)` derruba só aquele peer. O cliente continua com um peer só (o host) |
| `Steam/SteamLobby.cs:188` `CreateLobby(FriendsOnly, 2)` e `:214` `SetLobbyMemberLimit(lobby, 2)` | 2 vagas | `[Coop] MaxPlayers` (2–8, padrão 8 na etapa 4). `IsMember` não muda. Ver o risco do FriendsOnly em §7 |
| `Net/PacketRouter.cs` `Handle(NetDataReader)` | Não sabe de quem veio | `Handle(reader, fromSlot)`, com `PacketRouter.From` legível pelos handlers durante o despacho. O envelope `Relayed` (100) desembrulha e despacha de novo com a origem |
| `Plugin.cs` argumentos `--coop-*` | — | `--coop-name <nome>`, para testar várias instâncias com a mesma conta Steam (§8) |

### 1.2 Sessão, identidade e fantasma

| Onde | O que é hoje | Com N |
|---|---|---|
| `Lobby/CoopSession.cs` `Remote` (`PlayerInfo`), `RemotePlace`, `RemoteInRun`, `RemoteChangingScene`, `PartnerName`, `OnHello`, `OnLobbyState` (resync dos inimigos quando o parceiro entra InRun) | Um parceiro | **P+R.** `Players[slot] = RemotePlayer {Info, Place, …}`. Agregados no lugar dos booleanos: `AnyRemoteInRun`, `AnyRemoteChangingScene`, `RemotesNotInMenu`, `NamesOf(…)`. O `Hello` e o `LobbyState` dos clientes são repassados aos outros. O host manda o `Hello`/`LobbyState` de todos ao recém-chegado logo depois do `Welcome`. O `ResyncToPeer` vira **U**, só para quem entrou InRun |
| `GameState.RemoteGhostObject` | Um `GameObject` | **P:** `RemotePlayer.Ghost` |
| `CoopBehaviour.UpdateGhostLifecycle`/`DestroyGhost`/`RebuildGhostIfModelInactive`/`RebuildGhost`, `_ghostRebuilds`, `_identityRebuilds` | Um fantasma, criado com os dois InRun | **P:** um laço por slot. O fantasma de X existe enquanto eu e X estamos InRun (e ninguém em transição). Os contadores de rebuild vão por jogador. O `PageDown` de debug continua igual |
| `VisualGhostFactory` `BuiltForKey`, `BuiltFromRemote`, `ModelAnimator` (estáticos), `Create(source, …)` lê `CoopSession.Remote` | Um fantasma | `Create(source, PlayerInfo info, …)` devolve um `GhostBuild {Go, BuiltForKey, BuiltFromRemote, ModelAnimator}` guardado no `RemotePlayer`. O `_buildHolder` pode continuar estático (é síncrono) |
| `Net/PlayerSync.cs` `Buffer`, `Clock`, `ReceivedCount`, `LastReceivedPosition`, cache de Animators (`_animGhostPtr`, `_ghostAnimators`, `_ghostParams`, `_lastGhostPos`) | Uma interpolação | **P:** buffer, relógio e cache por jogador. **Envio:** o cliente continua mandando `PlayerState` (2) ao host. O host junta todos num `PlayersState` (101) por tick (20 Hz), com o slot de cada um e o `SentMs` de cada origem, em vez de repassar 7 pacotes. Motivo: o canal Sequenced (Steam canal 1 e LiteNetLib) tem **uma** sequência por conexão, e pacotes de origens diferentes nele descartariam uns aos outros |
| `GhostOverhead` (nome, barra, ícone do minimapa; tudo estático: `_root`, `_panel`, `_name`, `_fill`, `_ghostPtr`, `_iconId`…) | Um overlay e um ícone | **P:** um painel (nome + barra) e um ícone por fantasma, no mesmo canvas. Para distinguir 7 aliados, o anel e o nome ganham uma cor por slot, de uma paleta fixa de 8 cores |
| `Life/PartnerHealth.cs` `Known`, `Health`, `MaxHealth`, `Down`, `Dead` (estáticos) | A vida do parceiro | **P+R:** a tabela por slot. Cada um manda a própria vida ao host, que repassa |

### 1.3 Host: stand-ins, dano e status

| Onde | O que é hoje | Com N |
|---|---|---|
| `Enemies/RemoteProxy.cs` `_proxy`, `_proxyPtr`, `_proxyCharacter`, `_remoteState`, `_appliedDead`, `_lootTeamSwapped`, `Targetable`/`RemoteAlive`/`RemoteDownOrDead`, `TryGetRemotePosition` (lê o fantasma e `PlayerSync.ReceivedCount`) | Um proxy | **P:** `ProxyState` por cliente, criado quando o fantasma daquele slot existe e já chegou posição. `IsProxy(e)` vira `TryGetProxySlot(ptr, out slot)` (dicionário por ponteiro). `BeforeLoot`/`AfterLoot` trocam o time de **todos** os proxies |
| `RemoteProxy.OnClientState` (`ClientState` 13) | Aplica no único proxy | Aplica no proxy de `PacketRouter.From`. Não é repassado |
| `RemoteProxy.ForwardDamage` → `DamageToClient` (12) | Broadcast implícito (1 peer) | **U:** `SendTo(slot do proxy atingido)`. O formato não muda |
| `ClientIncomingDamage` (cliente) | Recebe do host | Não muda |
| Patch 9 `HostProxyDamagePatch` | `IsProxy`, `IsCaster`, `CountHostHit` | Busca por ponteiro em conjunto/dicionário. Atalho: sem stand-ins, `return true` antes de qualquer busca (roda em todo hit do jogo) |
| `HostEnemySync.OnClientDamage` (`ClientDamage` 11) | Aplica com atacante null | Não muda: vale para qualquer cliente. A telemetria passa a ser por slot |
| `ProxyStatusForward` (`ProxyStatus` 71) filtra pelos ponteiros dos componentes do proxy (`AbnormalStatus`, `Stunnable`), com limite de 40/s | Um proxy | **U:** mapa de ponteiro de componente → slot, e envio para aquele slot. O limite de 40/s vale por slot. No cliente não muda |
| `Enemies/TeamQueryGuard.cs` | Tira **o** proxy e **o** lançador da lista | Tira todos os stand-ins (um `HashSet<IntPtr>`) |
| `RemoteProxy.ReleaseTargets`/`DropAsTarget` | Inimigos que miravam o proxy | Por proxy. O seeker volta pro jogador vivo mais perto, não necessariamente o host |

### 1.4 Magias

| Onde | O que é hoje | Com N |
|---|---|---|
| `Skills/RemoteCaster.cs` `_caster`, `_casterPtr`, `_casterStatsPtr`, `Copies`, `FailedKeys`, `Channeling`, `_animGhostPtr`/`_ghostAnimators`, `PreloadPartnerWeaponSkills` (lê `CoopSession.Remote`), `CasterEntity` | Um lançador que segue o fantasma | **P:** um lançador por jogador remoto, nos dois lados (o host tem 7; cada cliente tem 7: o do host e os dos outros 6). `IsCaster(e)`/`IsCasterStats(s)` viram buscas em conjunto. `Play(m)` recebe o slot de origem. Os channels são por slot (dois jogadores podem canalizar a mesma chave) |
| `CastSync.OnLocalCast` → `SkillCast` (14) / `SkillChannelEnd` (15) | Vai pro outro | **R:** o host aplica no lançador do slot de origem e repassa no envelope aos outros clientes. O próprio cast do host vai em broadcast. `TargetOtherPlayer` vira `TargetPlayer + slot u8` (ver §3) |
| Patches `RemoteCasterDamageBlockPatch`, `RemoteCasterStatusBlockPatch`, `RemoteCasterPushbackBlockPatch`, `ClientPuppetEffectDamageBlockPatch`, `ReplicaCombatText` | Um lançador | Testam "é algum lançador" (conjunto de ponteiros de Entity e de `StatsComponent`) |
| `Enemies/EnemyCasts.cs:98-99`, `:154-155` `TargetHostPlayer`/`TargetClientPlayer` | Alvo = host ou o cliente | Vira `TargetPlayer + slot`. No cliente: slot = o meu → jogador local; outro slot → o lançador daquele slot (stand-in que segue o fantasma, como hoje com o host) |
| `Enemies/GodCasts.cs:150` toca o meteoro em `RemoteCaster.CasterEntity` | O único lançador | O lançador do slot 0 (host), que sempre existe no cliente durante a run |

### 1.5 Loot e XP

| Onde | O que é hoje | Com N |
|---|---|---|
| `Loot/LootSync.ClientOwnsXp` (`:160`) | Host ou cliente, o vivo mais perto | `OwnerOfXp(pos) → slot`: o jogador **vivo** mais perto entre o host e os proxies vivos. Se ninguém estiver vivo, o host |
| `LootSync.CaptureOnHost` → `LootDrop` (16) com `xp` | O XP vai pro cliente se ele for o dono | Broadcast com `ownerSlot u8` antes do `xp`. Só o dono aplica o XP; os outros replayam o mesmo drop com XP 0. Moedas, itens e power-ups continuam indo para todos (cada um recebe o seu, como hoje) |
| `XpShare` (17), `_shareOut`/`_shareIn`/`xpShareHeld`/`ownXpHeldWhileDown` | Vai pro outro | **R:** o host aplica e repassa aos outros com a origem. Todo mundo ganha o XP que qualquer um coletou (a regra do usuário vale para N). A retenção com o jogador caído continua por jogador local, sem mudança |

### 1.6 Vida, caído, fim de run

| Onde | O que é hoje | Com N |
|---|---|---|
| `Life/CoopLife.HostTick` (`:371`) | `hostDown`/`clientDown`; revive se um está caído e o outro a ≤ 2,5 u; derrota com os dois caídos por 0,5 s | **N:** progresso por jogador caído (`_progress[slot]`). Qualquer aliado vivo a ≤ 2,5 u enche a barra. A queda **em dobro** (fora do raio) continua. Derrota só com **todos** os jogadores da run fora (caído ou morto) por 0,5 s. Jogador desconectado sai da conta |
| `SendRevive` → `Revive` (5) `target u8 (1 = cliente)` | — | O campo já existe: vira o slot. Broadcast (todos mostram "REVIVEU!" em cima do fantasma certo) |
| `SendLife` → `CoopLife` (18) `hostDown | clientDown | progress` | 2 jogadores | `count u8 | count × (slot u8, down u8, progress f32)`, com todos os slots da run, na mudança e a 5 Hz enquanto alguém está caído |
| `_remoteDownedFromHost`, `_clientDownedFromHost`, `RemoteDowned` (`:104`) | "O outro está caído" | Tabela `Down[slot]` vinda do `CoopLife` |
| `UpdateSpectator` (`:329`), segue `GameState.RemoteGhostObject` | O fantasma | Segue o fantasma do aliado **vivo** mais perto, reavaliado a cada 1 s. Quando ele cai, troca. Opcional: Tab cicla entre os vivos |
| Marcadores "CAIDO"/"CAIDOS"/"REVIVE xx%" (`:794-830`) | Sobre o local ou o fantasma | Sobre cada corpo caído (local ou fantasma do slot). "CAIDOS" some (vira "CAIDO" em cada um) |
| `OnRunEndRequest` (`:628`): `Surrender` do cliente → `EndRun(Surrender)` para os dois | O cliente desistir encerra a run do host | **N:** com 3 ou mais, o cliente que desiste sai **sozinho**: roda o `Surrender` local, aparece como fora/Menu, e o host recicla o proxy dele. Com 2, a regra antiga continua (decisão do usuário? ver §7). A desistência do host encerra a run de todos |
| Portal Home (`OnPlayerEnterPortal` espelhado) | Um entra, o outro vai junto | Qualquer um entra → o host manda `RunEnd(PortalHome)` a todos (o host já é o meio do caminho) |
| `OnPeerDisconnected` (`:642`): "caído quando o parceiro sai morre de verdade" | — | No host: só quando **não sobra nenhum** cliente. No cliente: na queda do host (todos somem juntos, porque não há migração de host). A saída de outro cliente só tira aquele slot da conta |
| `BeginHostLost` (fase 8) | Cliente encerra quando o host cai | Não muda: sem host não há mundo |

### 1.7 Pausa e level-up

| Onde | O que é hoje | Com N |
|---|---|---|
| `Net/PauseSync.cs` `RemoteReason`, `RemotePaused`, `RemoteChoosing` | Pausa de qualquer um congela os dois | **R + N:** `Reason[slot]`, repassado. Regra proposta em `[Coop] PauseRule` (ver §6, etapa 5): `Anyone` (hoje), `HostOnly` (o Esc de um cliente abre o menu dele sem congelar os outros; aparece "X no menu") ou `Auto` (**padrão**: `Anyone` com 2 jogadores, `HostOnly` com 3 ou mais). O level-up que pausa (`LevelUp`) continua congelando todos em qualquer regra (mesma lógica de hoje) |
| `Net/LevelUpPolicy.cs:63` `coop = HasPeer && LocalInRun && RemoteInRun` | Um parceiro na run | `AnyRemoteInRun`. Nada mais muda. Com XP de equipe todo mundo sobe de nível quase junto, então as pausas se sobrepõem. O risco é esperar o mais lento escolher (§7) |
| `RunSync.HoldForPartner` (barreira de carregamento, `:487`) | Segura enquanto o parceiro carrega | **N:** segura enquanto **algum** jogador que recebeu o `RunStart` (e não recusou) está em Loading/Transition, com o mesmo limite de 45 s. O banner lista quem falta |

### 1.8 Início de run, portais, reentrada

| Onde | O que é hoje | Com N |
|---|---|---|
| `RunSync.AllowFight` (`:42`) "parceiro ainda não voltou ao menu" | 1 | Bloqueia se **algum** conectado está fora do menu, listando os nomes. Os que estiverem no menu entram juntos |
| `RunStart` (32), `RunStartRefused` (33) | Um cliente | Broadcast. A recusa é por cliente: ele fica fora e a run segue com os outros ("X não entrou: motivo") |
| `RunSync.HostRejoinTick` (`_rejoinSent`, `AllowRejoin`) | Um parceiro | **U:** um flag por slot. O `RunStart` vai só pro recém-chegado. Com `AllowRejoin` desligado, quem conecta no meio da run espera no menu (IP) ou não acha o lobby joinable (Steam), como hoje |
| `RunSync.AllowRestart`/`AfterHostRestart` | `RemoteChangingScene`, `RemotePlace == Menu` | Restart: `PortalTransition(Restart)` para quem está na tela final e `RunStart` para quem está no menu. Bloqueia se algum ainda carrega |
| `Lobby/PortalSync.cs` `PortalTransition` (90) / `PortalRequest` (91), avisos "não deu pra levar {PartnerName}" | Um cliente | `PortalTransition` em broadcast. O `PortalRequest` pode vir de qualquer cliente. "Sem o mapa" é por cliente: ele vai pelo Home e os outros seguem. Os avisos listam nomes |
| `CoopSession.OnLobbyState` → `PortalSync.OnHostLeftScene` | Lê a troca do host | Só quando o `LobbyState` é do slot 0 (com o envelope, `From == 0`) |
| `Enemies/ObjectiveSync.cs` | Host → cliente | Broadcast, sem mudança. A checagem `RemoteInRun` no cliente passa a ser "o host está InRun" |

### 1.9 Spawn, recycler e IA (patches da fase 0)

| Onde | O que é hoje | Com N |
|---|---|---|
| Patch 7 `HostSpawnPointPatch` → `MaybeRedirectSpawn` (`_spawnAlternator & 1`) | Um ponto a cada dois vai pro cliente; com o host caído, todos | **N:** rodízio entre os jogadores **vivos**: o índice `contador % vivos` escolhe a referência (host incluído se vivo). Com o host caído, só os clientes vivos. Sem mudança na chamada recursiva com guarda |
| Patch 8 `HostRecyclerPatch` (`RecyclerProtected`, raio de 35 u) | Protege perto do cliente | Protege perto de **qualquer** proxy vivo (laço `entidades × proxies`, ~300 × 7, ok a cada chamada do recycler) |
| Patch 13 `HostPlayerSeekerPatch` → `RetargetSeeker` | O mais perto entre o host e o proxy | O mais perto entre todos os vivos (host + proxies). Ainda nunca exercitado (`seekerRetargets=0`) |
| `BasicAIComponent.RefreshTarget` | Já escolhe a entidade Allies mais perto | Não muda (7 proxies registrados = 7 alvos a mais na busca, desprezível) |
| `ClientSpawnBlock` | Só no cliente | Não muda |
| `HostEnemySync` (Spawn/Died/Despawn/Snapshot) | Broadcast implícito | Broadcast explícito. O snapshot é o **mesmo** bytes para todos (ver §4) |

### 1.10 UI

| Onde | O que é hoje | Com N |
|---|---|---|
| `Ui/CoopUi.cs` status "Conectado com X (Steam, 45 ms)", `Remote`, `RemotePlace` | Uma linha | Lista de jogadores (até 7 linhas: nome, personagem, ping, Menu/Carregando/Na run) e "Jogadores: 3/8". O botão vira "Convidar amigo (3/8)" e desabilita com o lobby cheio |
| Banner (`:303-310`): instável, aguardando carregar, pausou, escolhendo | `PartnerName` | Nomes de quem estiver no estado, até 2 nomes + "e mais N" (o banner é uma linha) |

---

## 2. Topologia e id do jogador

**Estrela com o host como servidor.** Os clientes só falam com o host, que é o dono do mundo (inimigos, loot, objetivos, revive, fim de run) como hoje. O que um cliente gera e os outros precisam ver, o host repassa.

**Slot:**
- 0 = host; 1–7 = clientes. O host dá o menor slot livre quando aceita a conexão e manda primeiro de tudo, no canal ordenado, o `Welcome` (102) com `seuSlot | maxPlayers | presentes`.
- O slot de quem sai só é reusado depois do `PlayerLeft` (103) dele, no mesmo canal ordenado: todo mundo limpa aquele slot antes de alguém novo ocupá-lo.
- O cliente não precisa do próprio slot para mandar nada: o host carimba a origem pela conexão. Ele só precisa saber o próprio slot para responder "esse alvo sou eu?" (casts de inimigo, dono do XP, revive, lista de caídos).
- Até o `Welcome` chegar, `MySlot = 255`, e os pacotes que dependem dele são ignorados. Na prática, o `Welcome` chega antes de qualquer run.

**Por que não deixar o cliente escrever o slot:** o host já sabe de quem é cada conexão (`NetPeer.Id` / SteamID). Carimbar no host evita um campo que poderia mentir e deixa o formato dos pacotes do cliente igual ao de hoje.

**Como a origem viaja:**

| Caso | Mecanismo |
|---|---|
| Cliente → host | O transporte entrega `fromSlot`; `PacketRouter.Handle(reader, fromSlot)` |
| Host repassa o pacote do cliente k aos outros | Envelope `Relayed = 100`: `100 | k u8 | bytes originais`. O cliente desembrulha e despacha com `From = k`. Custa 2 bytes por pacote repassado, e os 30 tipos de hoje não mudam de formato |
| Pacote do próprio host | Sem envelope: `From = 0` |
| Pacote do host que fala de um jogador (alvo, dono do XP, revive, caídos) | Campo `slot u8` no formato daquele pacote (lista em §3) |
| Posições | `PlayersState` (101), agregado pelo host (§1.2), porque o canal Sequenced tem uma sequência só por conexão |

**Ordem:** o host repassa na ordem em que recebe, no mesmo canal ordenado, então a ordem por origem se mantém (o `SkillChannelEnd` nunca passa o `SkillCast` do mesmo jogador). A ordem entre origens diferentes não é garantida, e nada depende dela.

**Regra do struct:** "não patchar método com struct de 1/2/4/8 bytes por valor" é sobre o trampoline do Il2CppInterop em patches Harmony. Pacote é `NetDataWriter`, sem relação. Nenhum patch novo precisa de assinatura nova para os slots: os patches continuam os mesmos e só a busca dentro deles muda.

---

## 3. Pacotes

**Novos (faixa 100–119):**

| # | Nome | Sentido | Conteúdo |
|---|---|---|---|
| 100 | `Relayed` | host → cliente | `origem u8 | pacote original (tipo + corpo)` |
| 101 | `PlayersState` | host → clientes, Sequenced 20 Hz | `count u8 | count × (slot u8, x, y, z, yaw f32, sentMs u32)`: o host e todos os clientes; o receptor pula o próprio slot |
| 102 | `Welcome` | host → cliente novo, primeiro pacote | `seuSlot u8 | maxPlayers u8 | n u8 | n × slot u8` |
| 103 | `PlayerLeft` | host → clientes | `slot u8 | motivo u8` (saiu, caiu a conexão, recusado) |
| 104–119 | reservados | | (por exemplo: `RosterUpdate`, `Spectate`, algo da etapa 6) |

**Formatos que mudam** (a versão sobe para 0.9.0 e o handshake recusa a 0.8.0):

| # | Mudança |
|---|---|
| 2 `PlayerState` | Só cliente → host (o host passa a mandar o `PlayersState`) |
| 5 `Revive` | `target u8` passa a ser o slot (o campo já existe) |
| 14 `SkillCast` | `TargetOtherPlayer (3)` vira `TargetPlayer`, com `slot u8` depois |
| 16 `LootDrop` | `ownerSlot u8` antes do `xp` |
| 18 `CoopLife` | Lista `count × (slot, down, progress)` |
| 21 `EnemyCast` | `TargetHostPlayer`/`TargetClientPlayer` viram `TargetPlayer + slot u8` |

**Repassados pelo host (envelope 100):** `Hello` 30, `LobbyState` 31, `PauseState` 6, `PartnerHealth` 50, `SkillCast` 14, `SkillChannelEnd` 15 e `XpShare` 17.

**Só para o host (não repassados):** `ClientDamage` 11, `ClientState` 13, `RunEndRequest` 20, `RunStartRefused` 33 e `PortalRequest` 91.

**Unicast do host:** `DamageToClient` 12, `ProxyStatus` 71, o resync de `EnemySpawn` para quem entrou na run, e o `RunStart` da reentrada.

**Broadcast do host:** o resto (inimigos, snapshot, loot, objetivos, god casts, run start/end, portais).

---

## 4. Banda e desempenho com 7 clientes

### 4.1 Banda no host (estimativa a partir das medições das fases 3, 4B, 4C e 8)

| Fluxo | Por cliente | ×7 |
|---|---|---|
| Snapshot de inimigos (15 Hz, 11 B por inimigo, fatias de 1000 B), hoje 4–7 KB/s | 4–7 KB/s | 28–49 KB/s |
| `PlayersState` (20 Hz, ~8 + 8 × 21 B = 176 B) | 3,5 KB/s | 25 KB/s |
| Casts repassados (7 origens × ~3 casts/s × ~40 B) | ~0,9 KB/s | 6 KB/s |
| Spawn/Died/Loot (~10 mortes/s × ~60 B) | ~0,6 KB/s | 4 KB/s |
| Vida (7 × ≤ 5 Hz × 12 B), pausa, `CoopLife`, objetivos, casts de inimigo | ~0,6 KB/s | 4 KB/s |
| Cabeçalhos UDP/Steam (~60 pacotes/s × ~30 B) | ~1,8 KB/s | 13 KB/s |
| **Subida do host** | **~11–14 KB/s** | **~80–100 KB/s (~0,7–0,8 Mbps)** |

- **Descida do host:** 7 × (`PlayerState` 20 Hz × 27 B + casts + `ClientDamage` 15 Hz + `ClientState` 2 Hz + vida) ≈ 7 × 1,5 KB/s ≈ 10 KB/s.
- **Cada cliente:** ~12–14 KB/s de descida e ~1,5 KB/s de subida.
- **Com `EnemyScalePerPlayer` dobrando os inimigos:** o snapshot dobra e a subida do host vai a uns 130–150 KB/s (~1,2 Mbps). Ainda ok na maioria das conexões, mas já pesa num upload de 2 Mbps.
- **Botões de segurança (etapa 6, só se a medição pedir):**
  - `[Coop] SnapshotHz` (15 → 10 com 5 ou mais jogadores corta um terço);
  - interesse por distância (cada cliente recebe só os inimigos a menos de ~60 u dele ou de quem ele vê). O segundo é bem mais trabalho: o snapshot deixa de ser o mesmo para todos. Não recomendo no começo.

### 4.2 Limites

**Steam (ISteamNetworkingMessages, SDK 1.53a)**
- **Mensagens por segundo:** não há limite de contagem. O que existe é por conexão (uma sessão por cliente):
  - a fila de envio (`SendBufferSize`, 512 KB por padrão: acima disso o `SendMessageToUser` devolve `k_EResultLimitExceeded`, que o nosso log já mostra);
  - o controle de banda (`SendRateMin`/`SendRateMax`).
- **Tamanho:** mensagem até 512 KB. Unreliable acima do MTU (~1,1 KB de carga) é fragmentada e se perde inteira se um fragmento cair. Por isso as fatias de 1000 B da fase 8 continuam.
- **Nossa taxa:** ~12 KB/s por conexão, muito abaixo de qualquer padrão de banda da Steam. Mesmo assim, o autoteste do transporte pode logar o valor real de `SendRateMax` com `GetConfigValue` (eu **não confirmei** o padrão do 1.53a: anotar como verificação da etapa 1).
- **Recepção:** o laço lê 64 mensagens × 8 rodadas por canal por frame. Com 7 clientes são ~5 mensagens por frame, folgado.
- **Lobby:** aceita muito mais que 8 membros.

**LiteNetLib 1.3.1**
- Multi-peer nativo (`ConnectedPeerList`, `SendToAll`).
- MTU fixo de 1024 por peer (fase 8). Reliable com janela de 64 pacotes por canal e por peer: só enche numa rajada grande, como o resync de 100+ inimigos pra quem entra, e mesmo assim só enfileira.
- Os clientes usam porta aleatória (`Start()`), então várias instâncias no mesmo PC funcionam.

### 4.3 Entidades e CPU

**Host**
- 7 proxies + 7 lançadores = 14 `Creeps/Dummy`, todos `IgnoredByUnitLimit` e fora do recycler.
- Os proxies entram nas buscas da IA (7 alvos a mais).
- Os lançadores carregam as cópias de skill de 7 builds (~8–12 chaves × `FillPools(3)` cada). Memória ok.
- Os patches que rodam em todo hit (`ChangeEntityHealth`, `ApplyEffects`) passam de comparar um ponteiro para uma busca em `HashSet`, com um atalho quando não há stand-in.

**Cliente**
- 7 fantasmas com modelo de personagem e arma completos e Animator, mais 7 lançadores.
- Equivale a 7 personagens a mais na tela (sem física nem IA).

**O custo grande, nas duas pontas, é o VFX das réplicas:** 8 builds completas disparando em todo PC. Proposta (etapa 6), em `RemoteCaster.Play`:
- não tocar o cast se o lançador estiver a mais de ~45 u do jogador local (fora da câmera);
- continuar atualizando o estado de channel;
- contar na telemetria (`culledFar`).

Não muda nada no jogo, porque as réplicas são só visuais e o dano chega pelo host. Pode virar `[Coop] ReplicaCastRadius` (0 = sem limite).

---

## 5. Balanceamento

**O jogo não conhece "número de jogadores":**
- O spawn regular só sai se `GetCurrentActiveEntitiesCount()` estiver abaixo de `min(MaxTargetEnemiesAlive, difficulty × TargetEnemiesAliveDifficultyFactor)` (fase 0, seção 1; defaults do código: 40 e ×8).
- As filas só desenfileiram com `ActiveEntities ≤ AliveUnitLimit` (default 30).
- Os dois limites são **globais**.

**Com N jogadores**
- O mesmo número de inimigos vivos é dividido entre todos.
- O patch 7 espalha os spawns em rodízio, então com 8 jogadores longe uns dos outros cada um vê uns 4–5 inimigos.
- Os kills saem 8 vezes mais rápido, os spawns repõem até o teto e o XP de equipe faz todo mundo subir de nível mais rápido.
- **Bosses e elites:**
  - os bosses são disparados por objetivos de kill, então chegam antes;
  - a vida dos bosses não muda;
  - os elites vêm por tempo.

**Proposta: `[Coop] EnemyScalePerPlayer` (float, padrão `0` = desligado).**
- **Fórmula:** `mult = 1 + k × (N − 1)`, com N = jogadores conectados na run. Por exemplo, k = 0,25 com 8 jogadores dá ×2,75.
- **Onde:** host, no postfix de `GameManagerUtil.StartMatch` (o `HostEnemySync` já tem um). Percorre `GameManagerUtil.GameData.GameManagerComponents` e, com `TryCast`, ajusta:
  - `GameManagerRegularEnemyContinuousSpawnComponent.MaxTargetEnemiesAlive` (0x20) e `TargetEnemiesAliveDifficultyFactor` (0x24): mais inimigos vivos por wave;
  - `GameManagerSpawnQueuesBaseComponent.AliveUnitLimit` (0x24): a fila deixa de segurar no teto antigo.
- **Detalhes:**
  - guardar os valores originais e reaplicar `base × mult` quando N muda no meio da run (alguém sai ou entra), nunca multiplicando duas vezes;
  - tetos: no máximo 200 vivos (o `ModifiedCharacter.kMaxEntities = 300` é o limite físico, e o snapshot cresce linear);
  - campos simples (int/float) lidos e escritos por propriedade, sem patch novo e sem a regra do struct;
  - log `[balance] N=… mult=… MaxTarget a→b AliveUnitLimit c→d`.
- **Fica de fora no começo:**
  - `DifficultySpawnMultiplier` (0x28, a "potência" de cada wave);
  - o `Overlord` e as filas de elite;
  - vida de inimigo e de boss (`ApplyGenericEnemyScaling`, em `SpawnEnemyEntity`: dá pra fazer um postfix depois, se os bosses derreterem rápido demais).
- **Primeiro teste:** `0`, pra medir; sugestão de `0,25` depois. Com 8 jogadores e k = 0,25, o snapshot vai de ~40 para ~110 inimigos (2 fatias), ainda dentro da banda de §4.

---

## 6. Plano em etapas

Cada etapa compila (`-p:SkipDeploy=true`), vira commits pequenos e **continua jogável com 2** (host + 1 cliente). A partir da etapa 3 também dá pra jogar com 3 ou mais por IP.

### Etapa 1: ids, transporte multi-peer e roteador
- `Net/PlayerId.cs` (ou `Lobby/Players.cs`): `MySlot`, `const MaxSlots = 8`, `[Coop] MaxPlayers` (2–8, **padrão 2** até a etapa 4).
- `ITransport`/`NetworkManager`: `Send` (broadcast), `SendTo`, `SendExcept`, eventos com slot, `PingMs(slot)`/`SilenceSeconds(slot)`. Os eventos sem argumento continuam existindo enquanto a etapa 2 não troca os assinantes: no cliente, por exemplo, `PeerDisconnected` = o host saiu.
- `LiteNetTransport` e `SteamTransport` com dicionário de peers. O `Accept` do Steam leva o slot.
- `Welcome` (102) e `PlayerLeft` (103); `PacketRouter.Handle(reader, from)` e o envelope `Relayed` (100), ainda sem uso.
- **Com 2:** comportamento idêntico (um cliente só, nada a repassar).
- **Risco:** a reescrita do `SteamTransport`. É a parte com mais estado (handshake, ping, timeout, sequência), e só dá pra testar com duas contas.

### Etapa 2: estado por jogador (refactor sem mudar o comportamento)
- `RemotePlayer` por slot: `Info` (Hello), `Place`, `Ghost` (com `GhostBuild`), estado de interpolação e Animator, vida, pausa, overhead e ícone, lançador remoto. No host, também `ProxyState`.
- `CoopSession.Remote`/`RemotePlace`/`PartnerName` viram agregados ou `Players[slot]`.
- Os módulos da tabela do §1 passam a iterar a lista (com 2 jogadores ela tem um item).
- Os patches 7/8/13, o `TeamQueryGuard` e os bloqueios do lançador passam a usar conjuntos.
- **Com 2:** igual a hoje (rodízio entre 2 vivos = alternância de hoje).
- **Risco:** o tamanho do refactor, ~15 arquivos. Fazer um módulo por commit, e conflitos com as sessões da fase 5 e do level-up (por isso só depois do aviso).

### Etapa 3: repasse
- Host: repassa `Hello`, `LobbyState`, `PauseState`, `PartnerHealth`, `SkillCast`, `SkillChannelEnd` e `XpShare` no envelope.
- `PlayersState` (101) no lugar do `PlayerState` do host.
- Slot no alvo dos casts (14, 21), no dono do XP (16), no `Revive` (5) e no `CoopLife` (18).
- Unicast do `DamageToClient`, do `ProxyStatus` e do resync. O `Hello`/`LobbyState` de todos vai pro recém-chegado.
- `--coop-name` para testar no mesmo PC.
- **Com 3 (IP, `MaxPlayers = 3` no `.cfg`):** cada um vê os outros dois andando e lançando magia, e todo mundo apanha dos inimigos do host.
- **Opcional (vale muito para o teste):** transformar o `tools/TestClient` num **bot** que fala o protocolo novo (handshake LiteNetLib, `Hello`, `LobbyState` espelhando o host, `PlayerState` orbitando, `ClientState` vivo). Com 5 bots + 2 jogos de verdade dá pra medir banda e CPU do host com 7 clientes sem 8 cópias do jogo.

### Etapa 4: lobby e UI com 8 vagas
- Steam: `CreateLobby(FriendsOnly, MaxPlayers)`, `SetLobbyMemberLimit` e recusa "cheio". Padrão de `MaxPlayers` vai para **8**.
- Painel: lista de jogadores (nome, personagem, ping, lugar) e "Convidar amigo (n/8)".
- Banner com nomes.
- Overhead por fantasma, com nome e barra, e cor de anel por slot. Ícone do minimapa por fantasma.

### Etapa 5: regras com N
- **Revive:** qualquer aliado perto revive, com barra por caído.
- **Derrota:** só com todos fora. Espectador no aliado vivo mais perto.
- **Desistência:** a do cliente é solo com 3 ou mais.
- **Portal Home:** qualquer um leva todos.
- **Pausa:** `[Coop] PauseRule`, padrão `Auto`.
- **Barreira** espera todos; `AllowFight` lista quem falta; `RunStartRefused` e portal sem mapa são por cliente.
- **Host:** spawn em rodízio entre os vivos, recycler, seeker e dono do XP com N.
- **Saídas:** saída de cliente no meio da run limpa só aquele slot, nas duas pontas (`PlayerLeft`). Reentrada (`AllowRejoin`) por slot.

### Etapa 6: balanceamento e desempenho
- `EnemyScalePerPlayer` (§5).
- Corte de réplicas longe (`ReplicaCastRadius`).
- Telemetria por slot nas linhas `[sync]`, `[proxy]`, `[casts]` e `[loot]` (contagem e bytes por slot) e uma linha `[net] host up=… KB/s down=… KB/s peers=n`.
- Se a medição pedir: `SnapshotHz`.
- `docs/fase9-notas.md` com o roteiro final.

---

## 7. Riscos e decisões em aberto

1. **Desistência do cliente com 3 ou mais.** Proposta: sai só ele (com 8, um desistindo não derruba 7). Com 2 jogadores, manter a regra de hoje (desistir encerra os dois)? Proponho a mesma regra para qualquer N (desistir = sair sozinho), mais simples, mas com 2 isso muda o que existe. **Decisão do usuário.**
2. **Pausa com 8.** Com `Anyone`, alguém sempre vai estar no menu. O `Auto` (`HostOnly` a partir de 3) parece certo, mas é escolha de gosto.
3. **Level-up com 8.** Com o XP de equipe, todos sobem juntos. Com `LevelUpPause = NotWhenFullBuild` (padrão), o começo da run congela até o mais lento escolher. Pode valer sugerir `Never` em grupos grandes. Por enquanto fica como está e é observado no teste.
4. **Lobby FriendsOnly.** Um amigo de um cliente que não é amigo do host provavelmente não consegue entrar, nem pelo código. **Não confirmei** como a Steam trata a entrada por id num lobby FriendsOnly. Se travar, `[Coop] LobbyType = Invisible` (só por convite/código) resolve.
5. **Reescrita do `SteamTransport`** (multi-sessão): a parte de rede mais delicada. Só dá pra testar com contas diferentes.
6. **Conflitos de merge** com as sessões da fase 5 (`VisualGhostFactory`, `PlayerSync`, `RemoteCaster`) e do level-up (`PauseSync`, `CoopUi`). Por isso o PASSO 2 só começa depois do aviso, e a etapa 2 vai um módulo por commit.
7. **Inimigos espalhados.** Com o teto global, `EnemyScalePerPlayer = 0` deixa o jogo fácil e vazio com muitos jogadores longe uns dos outros. Isso é esperado no primeiro teste.
8. **Latência de quem é repassado:** um cliente vê outro cliente com duas idas (cliente → host → cliente) + 100 ms de interpolação. Para fantasma e réplica visual, ok.
9. **XP em trânsito** de quem cai, sai ou troca de cena continua se perdendo (como na fase 8 e nos portais). Com N, acontece mais vezes.
10. **Não há migração de host.** O host cai e a run de todos acaba (regra da fase 8, sem mudança).
11. **Desempenho do PC do host,** que roda o mundo, 14 stand-ins e o VFX de 8 builds: medir com o bot da etapa 3 e com o corte de réplicas da etapa 6.

---

## 8. Roteiro de teste

### 8.1 Três ou mais instâncias no mesmo PC (por IP)

A Steam exige uma conta por jogador, então no mesmo PC só dá por IP (LiteNetLib, 127.0.0.1:7777). Todas as instâncias usam a mesma conta e o mesmo `.cfg`.

- **Preparação:**
  - no `.cfg`, `[Coop] MaxPlayers = 4` (a partir da etapa 4 o padrão é 8), `AutoHost = false` e simulação de rede em 0;
  - atalhos do `Soulstone Survivors.exe` com os argumentos do Unity para janela pequena, mais os do mod:
    - A (host): `-screen-fullscreen 0 -screen-width 960 -screen-height 540 --coop-host --coop-name A`
    - B, C, D: `-screen-fullscreen 0 -screen-width 960 -screen-height 540 --coop-connect 127.0.0.1 --coop-name B` (C, D)
  - gráfico no mínimo nas quatro. Cada instância pesa uns 2–3 GB de RAM; com 4, o PC precisa de folga.
- **Logs:** o BepInEx numera quem abriu depois: `LogOutput.log`, `LogOutput.1.log`, `.2.log`… Conferir na primeira abertura.
- **Sem uma cópia do jogo por jogador:** o bot da etapa 3 (`tools/TestClient`), várias cópias num console, mais 1 ou 2 jogos de verdade.

### 8.2 Roteiro (3 ou 4 instâncias, A = host)

1. **Conexão:** todos no menu. O painel de A lista B, C e D com ping. B vê A, C e D. Uma quinta instância com `MaxPlayers = 4` é recusada ("cheio").
2. **Início:** A aperta Lutar com todos no menu e todos carregam o mesmo mapa. A barreira segura quem chegou primeiro até o último ("Aguardando C, D carregar...").
3. **Fantasmas:**
   - cada um vê os outros 3, com o personagem, o nome e a barra certos e o anel de cor diferente;
   - os fantasmas andam lisos, inclusive os de cliente vistos por outro cliente;
   - 3 ícones no minimapa.
4. **Inimigos e dano:**
   - os inimigos nascem em volta de todos (rodízio: `[proxy] spawnRedirects`);
   - cada cliente apanha e mata;
   - o host aplica o `ClientDamage` dos três (telemetria por slot).
5. **Magias:**
   - cada um vê as magias dos outros 3;
   - nenhuma réplica causa dano (`blockedDamage`) nem número.
6. **XP de equipe:** depois de 1 min, os 4 têm o **mesmo** XP. A linha `[loot]` de A mostra drops para os 4 donos.
7. **Pausa:**
   - Esc em A congela todos;
   - com `PauseRule = Auto`, Esc em B não congela A, C e D (banner "B no menu").
8. **Caído e revive:**
   - F9 em B; C fica perto 3 s e B levanta. Em D aparece "CAIDO"/"REVIVE xx%" em cima do fantasma de B;
   - espectador: B caído segue o aliado vivo mais perto.
9. **Derrota parcial:** F9 em A, B e C. A run continua com D vivo. D revive um deles.
10. **Derrota:** F9 nos 4: derrota em todas as telas.
11. **Saída de cliente:** fechar C no meio da run.
    - A: "C desconectou";
    - B e D perdem o fantasma de C;
    - os inimigos que perseguiam C voltam para os outros.
12. **Desistência:** B desiste pela pausa. Só B vai para a tela final; A e D continuam.
13. **Portal Home:** F10 em A e D entra no portal. Todos vão para a tela final.
14. **Portal Endless:** F10 e depois F5 em A. Todos vão para o mesmo mapa.
15. **Segunda run** com todos de volta ao menu, e reentrada com `AllowRejoin = true`: C reabre no meio da run e entra como personagem novo.
16. **Balanceamento:** `EnemyScalePerPlayer = 0.25`. O log `[balance]` mostra o multiplicador, e o número de inimigos vivos sobe.
17. **Banda:** a linha `[net]` do host com 3 clientes, comparada à estimativa do §4 (×3/7).

### 8.3 Pela Steam (contas diferentes)

Os passos do 8.2 com amigos de verdade (3 ou mais contas), mais:
- convite pelo overlay e pelo código;
- lobby cheio;
- um amigo de um cliente que não é amigo do host (risco 4);
- queda de um cliente com a sessão Steam aberta;
- queda do host.

Anotar o ping de cada um e a linha `[net]`.
