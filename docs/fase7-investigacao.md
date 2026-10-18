# Fase 7: convite pela Steam e rede P2P (investigação)

Investigação estática, só leitura: ApiDump no interop `com.rlabrecque.steamworks.net.dll`, `dump.cs`, nomes exportados do `steam_api64.dll` do jogo e Ghidra headless (`-readOnly`, cópia do projeto) em `SteamManager`, `SteamAPI` e `CallbackDispatcher`. RVAs relativos a `0x180000000`. Nenhum código do mod foi alterado.

---

## 1. Como o jogo usa a Steam

### Versões

- `Soulstone Survivors_Data/Plugins/Steamworks.NET.txt`: **Steamworks.NET 20.1.0, SDK 1.53a**.
- `Plugins/x86_64/steam_api64.dll` (295 336 bytes) exporta a flat API inteira dessa SDK, inclusive o que vamos usar:
  - `SteamAPI_SteamMatchmaking_v009`, `SteamAPI_SteamFriends_v017`, `SteamAPI_SteamUser_v021`, `SteamAPI_SteamUtils_v010`
  - `SteamAPI_SteamNetworkingMessages_SteamAPI_v002`, `SteamAPI_SteamNetworkingSockets_SteamAPI_v012`, `SteamAPI_SteamNetworkingUtils_SteamAPI_v004`
  - `ISteamMatchmaking_CreateLobby/JoinLobby/LeaveLobby/SetLobbyData/GetLobbyData/GetLobbyOwner/GetNumLobbyMembers/GetLobbyMemberByIndex/SetLobbyJoinable/SetLobbyType/SetLobbyMemberLimit/InviteUserToLobby/SetLobbyMemberData`
  - `ISteamFriends_ActivateGameOverlayInviteDialog`, `…InviteDialogConnectString`, `InviteUserToGame`, `SetRichPresence`, `ClearRichPresence`, `GetPersonaName`, `GetFriendPersonaName`, `GetFriendCount/GetFriendByIndex`
  - `ISteamNetworkingMessages_SendMessageToUser/ReceiveMessagesOnChannel/AcceptSessionWithUser/CloseSessionWithUser/CloseChannelWithUser/GetSessionConnectionInfo`, `SteamNetworkingMessage_t_Release`
  - `ISteamNetworkingSockets_ConnectP2P/CreateListenSocketP2P/AcceptConnection/CloseConnection/SendMessageToConnection/ReceiveMessagesOnConnection/GetConnectionInfo/CreateSocketPair/RunCallbacks`
  - `ISteamNetworkingUtils_InitRelayNetworkAccess/GetRelayNetworkStatus/SetConfigValue`, `SteamNetworkingIdentity_*`
  - `ISteamUtils_IsAPICallCompleted/GetAPICallResult/IsOverlayEnabled`, `ISteamApps_GetLaunchCommandLine`
  - `SteamAPI_ManualDispatch_Init/RunFrame/GetNextCallback/FreeLastCallback/GetAPICallResult`, `SteamAPI_GetHSteamPipe`

### Quem inicializa

`SteamManager.Init` (0x593270, Assembly-CSharp, classe estática):
1. `Packsize.Test()` e `DllCheck.Test()` (só logam erro).
2. `SteamAPI_RestartAppIfNecessary(2066020)`: se der true, loga e `Application.Quit()`. **Com o `steam_appid.txt` na pasta, dá false e o jogo não relança.** Sem o arquivo e aberto pelo exe, a Steam relança o jogo (e os argumentos de linha de comando se perdem).
3. `SteamAPI.Init()` (0x8292C0) = `SteamAPI_Init` nativo → `CSteamAPIContext.Init()` → **`CallbackDispatcher.Initialize()` (liga o manual dispatch)**. O resultado vai para `SteamManager.Initialized`.
4. Gancho de aviso (`SteamClient.SetWarningMessageHook`).

O `steam_appid.txt` não vem com o jogo: foi criado em 2026-10-17 por uma sessão nossa, para os testes locais. Na máquina do amigo ele não existe e o jogo é aberto pela Steam, que é o caso normal.

### Quem bombeia os callbacks

`RuntimePlatformUtil.OnLateUpdate` (0x71CA70) → `SteamManager.OnUpdate` (0x593600, só se `Initialized`) → `SteamAPI.RunCallbacks()` (0x829380) → `CallbackDispatcher.RunFrame(false)` (0x824D40). Uma vez por frame, no `LateUpdate`.

`RunFrame` é o do Steamworks.NET 20.1 sem mudança (confirmado no descompilado):
- `SteamAPI_ManualDispatch_RunFrame(pipe)` e depois um laço de `GetNextCallback` até esvaziar a fila.
- `m_iCallback == 703` (`SteamAPICallCompleted_t`): faz **sempre** `ManualDispatch_GetAPICallResult` para aquela chamada, mesmo que ninguém tenha registrado nada para ela, e só depois procura o handle em `m_registeredCallResults` (estático +0x10). Encontrou: chama `OnRunCallResult` (vtable +0x188) e `SetUnregistered`.
- Outros ids: procura em `m_registeredCallbacks` (estático +0x0) e chama `OnRunCallback` (vtable +0x178).
- Sempre `FreeLastCallback`.

**Consequência: o jogo consome a fila inteira de callbacks.** Qualquer callback que a gente queira (convite aceito, entrar no lobby, sessão P2P) é retirado da fila e descartado pelo jogo antes de chegarmos lá. Os call results também são "lidos" pelo jogo.

### O que sobrou do Steamworks.NET no interop

28 tipos. Nada de `SteamMatchmaking`, `SteamNetworkingSockets`, `SteamNetworkingMessages`, `SteamNetworkingUtils`, `CSteamID` nem **`Callback<T>`** (o genérico foi cortado inteiro).

| Classe | Métodos que sobraram |
|---|---|
| `SteamAPI` | `Init`, `Shutdown`, `RestartAppIfNecessary`, `RunCallbacks`, `IsSteamRunning`, `GetHSteamPipe`, `GetHSteamUser` |
| `SteamFriends` | `ActivateGameOverlayToStore`, `SetRichPresence` |
| `SteamUser` | `RequestEncryptedAppTicket`, `GetEncryptedAppTicket` |
| `SteamApps` | `BIsDlcInstalled` |
| `SteamUtils` | `IsOverlayEnabled` |
| `SteamUserStats` | `SetStat(float)`, `SetAchievement`, `StoreStats`, `ResetAllStats` |
| `SteamClient` | todos os `GetISteam*` (devolvem ponteiro) |
| `CSteamAPIContext` | ponteiros estáticos de todas as interfaces, inclusive `m_pSteamMatchmaking`, `m_pSteamNetworkingSockets`, `m_pSteamNetworkingMessages` |
| `CallbackDispatcher` | `Initialize`, `RunFrame(bool)`, `Register/Unregister(SteamAPICall_t, CallResult)`, dicionários `m_registeredCallbacks`, `m_registeredCallResults` |
| `Callback`, `CallResult` | bases abstratas (`OnRunCallback(IntPtr)`, `OnRunCallResult(IntPtr, bool, ulong)`, `SetUnregistered`) |
| `CallResult<T>` | só instanciado como `CallResult<EncryptedAppTicketResponse_t>` |

O único registro que o jogo faz no dispatcher é esse `CallResult<EncryptedAppTicketResponse_t>`, em `ModIOSteamAuthentication.Authenticate` (login do mod.io). Como `Callback<T>` não existe, `m_registeredCallbacks` fica vazio: hoje o jogo joga fora todo callback que chega.

---

## 2. Opções de acesso à API

| | Como | Prós | Contras / riscos |
|---|---|---|---|
| **(a)** Steamworks.NET do jogo pelo interop | `SteamMatchmaking.CreateLobby` etc. | Nada para embarcar | **Inviável:** os wrappers de Matchmaking/Networking e o `Callback<T>` foram removidos. Só aproveitamos o dispatcher dele e os ponteiros do `CSteamAPIContext` |
| **(b)** Steamworks.NET gerenciado próprio no plugin | DLL do Steamworks.NET 20.1 compilada por nós | API tipada pronta, structs de callback certas | O assembly se chama `com.rlabrecque.steamworks.net`, igual ao interop (conflito de nome e de namespace `Steamworks`: precisaria recompilar renomeado). O `RunFrame` dele disputa a fila com o do jogo, então é preciso tomar o bombeamento do mesmo jeito. ~1 MB para usar uns 30 métodos |
| **(c)** P/Invoke direto na flat API | `[DllImport("steam_api64")]`, como o `Lobby/SteamName.cs` da fase 6 | Pequeno, sem dependência, resolve no mesmo módulo que o jogo já carregou e inicializou | Structs de callback escritas na mão (layout com pack 8 no Windows). Também precisa resolver o bombeamento |

**Recomendação: (c) mais tomar o bombeamento de callbacks.**

### O bombeamento (vale para qualquer opção)

**Plano A: prefix em `Steamworks.SteamAPI.RunCallbacks()`** (estático, sem argumentos, devolve void: assinatura segura para o trampoline). O prefix roda o nosso laço e devolve `false`:

```
pipe = SteamAPI_GetHSteamPipe()
ManualDispatch_RunFrame(pipe)
while ManualDispatch_GetNextCallback(pipe, &msg):
    try:
        if msg.id == 703 (SteamAPICallCompleted_t):
            se o handle é nosso  -> GetAPICallResult no nosso buffer -> nosso handler
            senão -> repassa ao jogo: m_registeredCallResults[handle] (Dictionary<ulong, List<CallResult>> do interop)
                     GetAPICallResult num buffer, cr.OnRunCallResult(buf, failed, handle), cr.SetUnregistered()
        else:
            se o id é um dos nossos -> nosso handler (copiando a struct de msg.pubParam)
            senão, se m_registeredCallbacks tem o id -> cb.OnRunCallback(msg.pubParam) (hoje nunca acontece)
    finally: ManualDispatch_FreeLastCallback(pipe)
```

- É o mesmo algoritmo do `RunFrame` do jogo, com os nossos ids na frente. O repasse mantém o login do mod.io funcionando.
- try/catch por mensagem (uma exceção não pode deixar a fila sem `FreeLastCallback`). Se o nosso laço falhar na entrada (P/Invoke não resolve), o prefix devolve `true` e o jogo segue como hoje.
- Só roda com `SteamManager.Initialized`.

**Plano B (se o repasse der problema):** não tomar nada e registrar objetos nossos no dispatcher do jogo: uma classe injetada (`ClassInjector`) derivada de `Steamworks.Callback`/`CallResult`, sobrescrevendo `OnRunCallback`/`OnRunCallResult`, colocada em `m_registeredCallbacks[id]` e `m_registeredCallResults[handle]`. O `RunFrame` do jogo chamaria a gente pela vtable. Zero patch, mas depende de o `ClassInjector` sobrescrever método abstrato `internal` de uma classe IL2CPP, o que não está provado neste projeto.

**O que não funciona:** consultar `ISteamUtils_IsAPICallCompleted`/`GetAPICallResult` por conta própria (o `RunFrame` do jogo lê todo call result antes de nós) e rodar um segundo laço de `GetNextCallback` além do jogo (quem chegar primeiro leva).

---

## 3. Fluxo do convite

### Host

1. "Convidar amigo" no painel Co-op: `CreateLobby(k_ELobbyTypeFriendsOnly = 1, 2)`. O call result `LobbyCreated_t` (id 513: `EResult` int em 0, `ulSteamIDLobby` u64 em 8) e o callback `LobbyEnter_t` (504) trazem o id do lobby.
2. `SetLobbyData`: `scoop_ver` = `Plugin.HandshakeVersion`, `scoop_host` = SteamID do host, `scoop_proto` = versão do protocolo de transporte. `SetLobbyMemberLimit(2)`.
3. `SetRichPresence("connect", "+connect_lobby <id>")`: liga o "Entrar no jogo" na lista de amigos mesmo que o lobby seja privado. (`steam_display` exige tokens de localização configurados pelo desenvolvedor no Steamworks; não temos, então o amigo vê só "Jogando Soulstone Survivors".)
4. `ActivateGameOverlayInviteDialog(lobby)`: abre o diálogo de convite do overlay. Sem overlay (`ISteamUtils_IsOverlayEnabled` false), plano B: um botão "Copiar código" (o id do lobby no clipboard, o `Ui/WindowsClipboard.cs` já existe) e uma lista de amigos nossa com `GetFriendCount/GetFriendByIndex/GetFriendPersonaName` + `InviteUserToLobby`.
5. Fica escutando `LobbyChatUpdate_t` (506: lobby u64, membro que mudou u64, quem fez u64, estado u32; 1 = entrou, 2 = saiu, 4 = desconectou) e começa a falar com o membro que entrou (seção 4).
6. Ao sair do co-op ou fechar o jogo: `LeaveLobby`, `ClearRichPresence`. Lobby fica `SetLobbyJoinable(false)` durante a run (o cliente só entra pelo menu, regra da fase 6).

### Amigo

| Situação | O que a Steam faz | O que a gente precisa |
|---|---|---|
| Jogo aberto, aceita o convite ou clica "Entrar no jogo" (lobby FriendsOnly) | callback `GameLobbyJoinRequested_t` (333: lobby u64, amigo u64) | **callback próprio** (dispatcher) |
| Jogo aberto, "Entrar no jogo" pelo rich presence `connect` | callback `GameRichPresenceJoinRequested_t` (337: amigo u64, `rgchConnect[256]`) | **callback próprio** |
| Jogo fechado, aceita o convite | a Steam abre o jogo com `+connect_lobby <id>` na linha de comando | ler `Environment.GetCommandLineArgs()` no `Plugin` (sem callback) |
| Jogo fechado, "Entrar no jogo" pelo rich presence | abre com a string do `connect` (`+connect_lobby <id>`) na linha de comando | idem |

Depois: `JoinLobby(id)` → `LobbyEnter_t` (504: lobby u64 em 0, permissões u32 em 8, bloqueado bool em 12, resposta u32 em 16; 1 = sucesso). O cliente lê `scoop_ver` com `GetLobbyData`: versão diferente → sai do lobby e mostra "Versão diferente: host X, você Y" sem nem abrir a conexão. Host = `GetLobbyOwner`.

O `+connect_lobby` só pode ser atendido quando o menu e o `CoopBehaviour` já existem: guardar o id no `Plugin.Load` e fazer o `JoinLobby` no mesmo ponto em que a fase 6 aplica o `--coop-connect`.

**Resumo:** sem callbacks próprios dá para criar e configurar o lobby (mas não saber o id dele, que vem por callback ou call result), abrir o overlay, ligar o rich presence e atender o amigo que abriu o jogo pelo convite. Aceitar convite com o jogo aberto (o caso mais comum), saber quem entrou e saber o id do lobby criado **exigem** o dispatcher.

### steam_appid.txt e a máquina de teste

- Com o arquivo, o exe aberto direto inicializa a Steam como o app 2066020: lobby, rich presence e convite funcionam igual. O overlay em jogo aberto fora da Steam costuma funcionar (o `SteamAPI_Init` carrega o `gameoverlayrenderer64`), mas é o primeiro ponto a validar.
- O amigo abre pela Steam, sem o arquivo: é o caminho normal, e os argumentos `+connect_lobby` chegam porque o `RestartAppIfNecessary` dá false quando quem abriu foi a Steam.
- Se o próprio usuário abrir o exe direto **sem** o arquivo, a Steam relança o jogo e os argumentos `--coop-*` somem. Por isso o arquivo continua na pasta de teste.

---

## 4. Transporte

### Como o código usa a rede hoje

- Tudo sai por `NetworkManager.Send(NetDataWriter, DeliveryMethod)` e chega por `PacketRouter.Handle(NetPeer, NetPacketReader)`, que lê o tipo (u8) e despacha.
- Quase todos os handlers recebem `NetDataReader` (só `PlayerSync.OnPlayerStateReceived` e `PauseSync.OnPauseStateReceived` pedem `NetPacketReader`). `ClientPuppets.OnSnapshot` usa `reader.RawDataSize`, que o `NetDataReader` também tem.
- "Há parceiro?" é `NetworkManager.RemotePeer != null` (26 lugares nas fases 1–6). `NetPeer` não é usado para mais nada fora do `NetworkManager`.
- Eventos: `PeerConnected`/`PeerDisconnected`. Estado para a UI: `Role`, `Status`, `LastError`, `TargetAddress`.
- Métodos de entrega usados:

| LiteNetLib | Onde | Steam |
|---|---|---|
| `ReliableOrdered` | quase tudo: spawn/despawn/morte, dano no cliente, vida, revive, RunEnd, casts, loot, XP, pausa, Hello, LobbyState, RunStart | `k_nSteamNetworkingSend_Reliable` (8), canal 0 |
| `ReliableUnordered` | `ClientDamage` (cliente → host), `EnemyCast` | `Reliable` (8), canal 0. A Steam entrega confiável sempre em ordem; é mais estrito, não muda nada para quem lê |
| `Unreliable` | `EnemySnapshot` (fatias de até 1200 B) | `k_nSteamNetworkingSend_UnreliableNoNagle` (1), canal 0 |
| `Sequenced` | `PlayerState` (posição do jogador) | `UnreliableNoNagle` (1) no **canal 1**, com um u16 de sequência na frente; o receptor descarta o que for mais velho que o último |

### SteamNetworkingMessages ou SteamNetworkingSockets

| | Messages (`SendMessageToUser`) | Sockets (`ConnectP2P` / `CreateListenSocketP2P`) |
|---|---|---|
| Endereço | SteamID do parceiro (vem do lobby) | SteamID + porta virtual, conexão explícita |
| Abrir | implícito: mandar a primeira mensagem já aceita a sessão. Quem recebe antes de mandar ganha `SteamNetworkingMessagesSessionRequest_t` (1251, uma `SteamNetworkingIdentity` de 136 B) e chama `AcceptSessionWithUser` | o host precisa do callback `SteamNetConnectionStatusChangedCallback_t` (1221) para `AcceptConnection`, com uma struct `SteamNetConnectionInfo_t` grande (~700 B) |
| Estado | `GetSessionConnectionInfo` devolve o estado (dá para consultar 1×/s, sem callback); falha também vem em 1252 | callback 1221 a cada mudança, motivo de fechamento com texto |
| Recusa (versão) | já resolvida antes, pelo `scoop_ver` do lobby; para garantir, mensagem de transporte `ConnectReject` | `CloseConnection` com código e texto |
| Receber | `ReceiveMessagesOnChannel(canal)` | `ReceiveMessagesOnConnection` |

**Recomendação: SteamNetworkingMessages.** Com o lobby fazendo a sinalização (quem é o host, quem entrou, versão), sobra pouco para o transporte: os dois lados sabem o SteamID um do outro e mandam primeiro, então a sessão abre sem depender de callback; o 1251 fica só de reserva. Menos structs escritas na mão (a de sessão é só a identidade). Usa o mesmo relay da Steam (SDR). Os Sockets ficam como plano B se precisarmos de estado de conexão mais fino.

### Limites e a fatia de snapshot

- Mensagem máxima: 512 KB (`k_cbMaxSteamNetworkingSocketsMessageSizeSend`). Nenhum pacote nosso chega perto.
- Mensagem não confiável acima de ~1,1–1,2 KB vira vários pacotes e só chega se todos chegarem. A fatia de snapshot (`EnemyMessages.MaxPacketBytes = 1200`, 107 inimigos) está no limite. Logar o tamanho no teste com o amigo e, se aparecer perda alta, baixar para 1100 (uma constante, sem mudar o formato).
- `SteamNetworkingUtils.InitRelayNetworkAccess()` logo no início (os dois lados), para a primeira conexão não esperar o relay. `GetRelayNetworkStatus` mostra se o relay está pronto.
- A Steam copia o buffer no `SendMessageToUser`, então dá para mandar `writer.Data` (0..`writer.Length`) com `fixed` e reaproveitar o writer.
- Recebimento: `ReceiveMessagesOnChannel(0)` e `(1)` a cada frame no `CoopBehaviour.Update` (o mesmo lugar do `PollEvents` de hoje), em lotes de até 64. Cada `SteamNetworkingMessage_t*` (Windows, pack 8: `m_pData` 0, `m_cbSize` 8, `m_conn` 12, `m_identityPeer` 16, `m_usecTimeReceived` 160, `m_nChannel` 192) é copiado para um `byte[]`, liberado com `SteamNetworkingMessage_t_Release` e entregue num `NetDataReader.SetSource`. Só aceitar mensagem do SteamID do parceiro.
- Queda: a sessão sem tráfego cai em ~10 s. Somar um keepalive nosso a 1 Hz (dá o ping para a UI) e `LobbyChatUpdate_t` (saiu/desconectou) para perceber saída limpa na hora.

---

## 5. Teste

**No mesmo PC as duas instâncias são a mesma conta Steam:** não dá para entrar no próprio lobby como outro membro nem abrir sessão P2P consigo mesmo. A partida em co-op pela Steam só se testa com duas contas em duas máquinas.

### Sozinho (uma instância, conta do usuário)

1. Bombeamento: log de que o prefix do `RunCallbacks` está ativo, contagem de callbacks por id, e o call result do mod.io repassado (o jogo segue igual, sem erro no log).
2. Nome Steam (`SteamName` da fase 6) aparecendo no log: primeira prova de que o P/Invoke no `steam_api64` funciona.
3. "Convidar amigo": log com o id do lobby (`LobbyCreated_t`), `scoop_ver` lido de volta com `GetLobbyData`, overlay de convite abrindo (ou o aviso de overlay desligado e o botão "Copiar código").
4. Rich presence: no próprio cliente Steam ou no perfil dá para ver "Jogando…"; o "Entrar no jogo" só aparece para outra conta.
5. Relay: `InitRelayNetworkAccess` e o status chegando em "pronto" no log.
6. Laço de transporte em uma instância: um modo de diagnóstico com `ISteamNetworkingSockets_CreateSocketPair` (par local, mesma struct `SteamNetworkingMessage_t`) manda e recebe pacotes de teste. Valida o layout das structs, os flags e o `Release` sem precisar de outra pessoa.
7. `+connect_lobby`: abrir a segunda instância com `+connect_lobby <id do log>` e ver o mod reconhecer o argumento e tentar o `JoinLobby` (o resultado com a mesma conta não vale como teste).
8. Regressão: IP direto (127.0.0.1) entre as duas instâncias, run inteira, igual à fase 6.

### Com um amigo (duas contas, duas máquinas, as duas com o jogo e a mesma versão do mod)

1. Os dois sem `steam_appid.txt`, jogo aberto pela Steam.
2. Host: Co-op → "Convidar amigo" → escolhe o amigo no overlay.
3. Amigo com o jogo **aberto**: aceita no chat da Steam → entra sozinho, o painel mostra "Conectado com <host>". Conferir no log dos dois o SteamID do parceiro e o ping.
4. Uma run inteira (marionetes, dano, magias, XP, cair/reviver, boss), voltar ao menu, segunda run.
5. Amigo **fecha o jogo**, host convida de novo → amigo aceita → a Steam abre o jogo e ele entra direto (`+connect_lobby`).
6. Amigo pelo "Entrar no jogo" na lista de amigos, sem convite.
7. Versão diferente (`--coop-fake-version` num dos dois): recusa com mensagem clara.
8. Queda: amigo fecha o jogo no meio da run → host vê "parceiro desconectou" e continua solo; idem ao contrário.
9. Mandar ao final os dois `BepInEx/LogOutput.log`.

---

## 6. Proposta de implementação

### Etapa 1: interface de transporte (sem Steam, sem mudar comportamento)

- `Net/ITransport.cs`: `StartHost()`, `StartClient(alvo)`, `Stop()`, `Poll()`, `Send(byte[] dados, int tamanho, DeliveryMethod)`, `Connected`, `Status`, `LastError`, `TargetLabel`, `PingMs`, eventos `Connected`/`Disconnected`/`Data(NetDataReader)`. Reusa o `DeliveryMethod` do LiteNetLib como enum (evita mexer nos 23 `Send`).
- `Net/LiteNetTransport.cs`: o `NetworkManager` de hoje (handshake de versão da fase 6 incluído) movido para dentro.
- `NetworkManager` vira fachada: `Role`, `Status`, `LastError`, eventos e `Send(NetDataWriter, DeliveryMethod)` iguais; `RemotePeer != null` vira `NetworkManager.HasPeer` (troca mecânica nos arquivos das fases 1–6); `PacketRouter.Handle(NetDataReader)`; `PlayerSync`/`PauseSync` passam a receber `NetDataReader`.
- Teste: igual ao da fase 6 por IP. Nada pode mudar.

### Etapa 2: base Steam

- `Steam/SteamNative.cs`: os `DllImport` e as structs (pack 8).
- `Steam/SteamCallbacks.cs`: o prefix em `SteamAPI.RunCallbacks` com repasse ao jogo, registro de handlers por id e por handle de call result, contadores no log.
- `Steam/SteamLobby.cs`: criar, configurar, entrar, sair, rich presence, overlay, `+connect_lobby`, `GameLobbyJoinRequested_t`/`GameRichPresenceJoinRequested_t`, `LobbyChatUpdate_t`.
- `InitRelayNetworkAccess` no início.
- Teste sozinho: itens 1–5 e 7 da seção 5.

### Etapa 3: transporte Steam

- `Net/SteamTransport.cs` (Messages): sessão com o SteamID do parceiro vindo do lobby, canais 0/1, sequência no canal 1, handshake de transporte e keepalive (pacotes abaixo), estado pelo `GetSessionConnectionInfo`.
- Diagnóstico `CreateSocketPair` (item 6 da seção 5).
- O `NetworkManager` escolhe o transporte: Steam quando a sessão nasce de um lobby, LiteNetLib quando nasce de IP ou dos argumentos `--coop-*`.

### Etapa 4: UI no painel Co-op da fase 6

- Botões "Convidar amigo" (host: cria o lobby e abre o overlay) e "Copiar código"; campo "Entrar por código" (o mesmo campo de IP: se for um número de 17 dígitos, é lobby). O IP continua em "Hospedar por IP / Entrar".
- Status: "Lobby criado, convide um amigo", "Entrando no lobby de <nome>…", "Conectado com <nome> (Steam, 45 ms)", "Versão diferente…", "Overlay da Steam desligado: use Copiar código".

### Arquivos no pacote de instalação

O transporte Steam **não acrescenta nenhum arquivo** em `BepInEx/plugins/SoulstoneCoop/`: continua só `SoulstoneCoop.dll` + `LiteNetLib.dll`. O P/Invoke resolve no `steam_api64.dll` que o próprio jogo carrega (`Soulstone Survivors_Data/Plugins/x86_64/`), e os tipos `Steamworks.*` que o patch do dispatcher usa vêm do interop gerado na primeira execução. Não embarcar Steamworks.NET gerenciado (opção b descartada), nem `steam_api64.dll`, nem `steam_appid.txt` (o amigo abre o jogo pela Steam).

### Pacotes novos (40–49)

Consumidos pelo `SteamTransport`, não chegam ao `PacketRouter`, mas usam a mesma faixa de números para não confundir.

| # | Nome | Sentido | Conteúdo |
|---|---|---|---|
| 40 | `TransportHello` | cliente → host | chave `soulstonecoop-v1`, versão do mod (string), versão do protocolo (u16) |
| 41 | `TransportAccept` | host → cliente | vazio. Só depois dele os dois disparam `PeerConnected` |
| 42 | `TransportReject` | host → cliente | motivo (string), mesmo texto do `Reject` da fase 6 |
| 43 | `TransportBye` | os dois | motivo (string): saída limpa |
| 44 | `TransportPing` | os dois | u32 tempo local (1 Hz) |
| 45 | `TransportPong` | os dois | u32 tempo recebido (ping para a UI) |
| 46–49 | reservados | | |

### Riscos

| Risco | Mitigação |
|---|---|
| O prefix no `RunCallbacks` quebrar o repasse ao jogo (login do mod.io) | Repasse fiel ao `RunFrame` original; log do repasse; plano B (classe injetada no dispatcher) |
| Layout de struct errado nos callbacks e no `SteamNetworkingMessage_t` | Poucas structs, offsets fixos documentados acima, teste `CreateSocketPair` sozinho e log em hexa do primeiro callback de cada id |
| Overlay não abrir no jogo aberto pelo exe | "Copiar código" + "Entrar por código" e `InviteUserToLobby` por lista própria |
| Fatia de snapshot fragmentada no relay | Medir; baixar `MaxPacketBytes` para 1100 se preciso |
| Latência real (tudo foi testado com 0 ms) | Fase 8; antes do teste com o amigo, um teste por IP com `SimulateLatency` do LiteNetLib |
| Amigo sem o mod ou com outra versão | `scoop_ver` no lobby, recusa antes de conectar, mensagem clara |
| Só dá para testar a partida com outra pessoa | Etapas 1–3 com tudo o que dá para validar sozinho antes de marcar o teste |
