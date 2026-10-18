# Fase 7: convite pela Steam e rede P2P

A investigação está em `docs/fase7-investigacao.md`. Versão do mod: **0.7.0** (o handshake recusa versões diferentes; o amigo precisa do pacote novo).

## O que entrou

- **Interface de transporte (`Net/ITransport.cs`).**
  - O `NetworkManager` virou uma fachada: `Role`, `Status`, `LastError`, `PeerConnected`/`PeerDisconnected` e `Send(writer, método)` continuam iguais pras fases 1–6.
  - `NetworkManager.RemotePeer != null` virou `NetworkManager.HasPeer` (troca mecânica em 26 lugares).
  - `PacketRouter.Handle(NetDataReader)`; `PlayerSync` e `PauseSync` recebem `NetDataReader`.
  - `Net/LiteNetTransport.cs`: o IP direto da fase 6, com o handshake de versão, sem mudança de comportamento.
- **Bombeamento de callbacks da Steam (`Steam/SteamCallbacks.cs`).**
  - Prefix em `Steamworks.SteamAPI.RunCallbacks()`, aplicado à mão em `Plugin.Load` com try/catch.
  - Roda o mesmo laço do `CallbackDispatcher.RunFrame` do jogo (manual dispatch) com os nossos ids na frente, e repassa ao jogo o resto: call results (o login do mod.io) e callbacks.
  - Se o P/Invoke falhar, devolve o bombeamento ao jogo.
- **Flat API (`Steam/SteamNative.cs`).** `DllImport` no `steam_api64.dll` do jogo (SDK 1.53a) e os offsets das structs no Windows x64.
- **Lobby (`Steam/SteamLobby.cs`).**
  - "Convidar amigo" cria um lobby FriendsOnly de 2, com `scoop_ver`, `scoop_host` e `scoop_proto`. Também liga o rich presence `connect = +connect_lobby <id>` e abre o diálogo de convite do overlay.
  - O amigo entra por três caminhos:
    - `GameLobbyJoinRequested_t` (convite aceito ou "Entrar no jogo" com o jogo aberto);
    - `GameRichPresenceJoinRequested_t`;
    - `+connect_lobby <id>` na linha de comando (jogo fechado), ou o código colado no campo do painel.
  - **Versão diferente:** o cliente lê `scoop_ver` e sai do lobby antes de conectar, com a mensagem "Versão diferente: host X, você Y".
  - O lobby fecha durante a run e reabre no menu. Um convite aceito no meio da run fica guardado até o menu.
  - `InitRelayNetworkAccess` no início; o status do relay vai pro log.
- **Transporte Steam (`Net/SteamTransport.cs`, ISteamNetworkingMessages, relay da Steam).**
  - Canal 0: Reliable/ReliableUnordered viram `Reliable`, Unreliable vira `UnreliableNoNagle`.
  - Canal 1: Sequenced (posição do jogador) vai com um u16 de sequência na frente; o receptor descarta o velho.
  - Pacotes 40–45, consumidos dentro do transporte: `Hello` (chave, versão, protocolo), `Accept`, `Reject` (motivo), `Bye`, `Ping`, `Pong` (ping no painel). 46–49 ficam reservados.
  - **Queda:** detectada pelo `Bye`, pela saída do lobby (`LobbyChatUpdate_t`), pelo estado da sessão (1×/s) e por 15 s sem tráfego.
  - O host só aceita `Hello` de quem está no lobby.
  - **Autoteste:** uma vez por sessão do jogo, um par de sockets local (`CreateSocketPair`) manda 16 B, 1200 B e 4000 B pela pilha da Steam e confere o conteúdo. Isso valida os offsets do `SteamNetworkingMessage_t`.
- **UI (`Ui/CoopUi.cs`).**
  - "Convidar amigo" e "Copiar código" no topo; depois "Hospedar por IP"; o campo aceita IP ou código do lobby, com Colar e Entrar; Desconectar.
  - O status mostra o tipo de conexão e o ping ("Conectado com X (Steam, 45 ms)").
  - `WindowsClipboard.TrySetText` pro "Copiar código".
- Nenhum arquivo novo no pacote de instalação: o P/Invoke usa o `steam_api64.dll` do jogo, e o `com.rlabrecque.steamworks.net` vem do interop gerado.

## Visto funcionando (uma instância, aberta pelo exe)

- `Callback pump taken over (pipe 1)` e os callbacks normais da Steam passando pelo nosso laço, sem erro.
- `Steam co-op ready: me=<SteamID>`.
- `Relay network: pronto (100)`.
- `Self-test OK: 3/3 messages through the Steam stack`.
- `IsOverlayEnabled` = false com o jogo aberto pelo exe. Por isso o botão pede o overlay mesmo assim e indica o "Copiar código".

## Não testado

- O repasse do call result do mod.io (log `Forwarded call result`): só aparece se o jogo autenticar no mod.io.
- O overlay de convite abrindo (o overlay está desligado no PC do usuário; ele liga se precisar).
- Tudo que exige duas contas: entrar no lobby, sessão P2P, partida, queda.

## Teste local (um PC)

Cobre a fase 6 inteira (o roteiro do `fase6-notas.md`) e o que a fase 7 permite testar sozinho. `AutoHost=false` no `.cfg`.

Os dois jogos podem ser abertos pelo exe (`Soulstone Survivors.exe` na pasta do jogo). O overlay da Steam está desligado no PC do usuário: o diálogo de convite não abre, e o caminho testado é o código do lobby.

Fase 7, sozinho (jogo A):
1. Abra o jogo A e vá até o menu.
2. Aba **Co-op** → **Convidar amigo**. O status mostra "Lobby aberto na Steam" e "Se o overlay da Steam não abriu, use Copiar código".
3. **Copiar código** → aparece "Código do lobby copiado". Cole no Bloco de Notas: é um número de 17 dígitos.
4. **Desconectar**. O status volta pra "Fora do co-op".

Fase 6 por IP (os dois jogos):
5. Abra o jogo B pelo exe e vá até o menu.
6. A: **Hospedar por IP**.
7. B: o campo já vem `127.0.0.1` → **Entrar**. Os dois mostram "Conectado com <nome> (<personagem>) (IP, x ms)".
8. B: o Lutar na tela de mapa mostra "Aguardando o host começar a partida".
9. A: escolha um mapa **com afixos** e aperte Lutar. B carrega sozinho o mesmo mapa; quem chegar primeiro espera ("Aguardando X carregar...").
10. Na run: fantasma, inimigos, dano, magias, pausa. F9 no A e o B perto por 3 s pra reviver.
11. Fim da run (desistir ou F10 no A). O Reiniciar fica bloqueado; os dois voltam ao menu.
12. Segunda run pelo A: tudo de novo.
13. B: **Desconectar** → A mostra "<nome> desconectou".
14. Feche o B. Abra o B pelo exe de novo, com o atalho com `--coop-fake-version 0.5.0`, e tente Entrar: as duas telas mostram a versão diferente.
15. Feche os dois e me mande `BepInEx/LogOutput.log` (A) e `LogOutput.1.log` (B).

Logs úteis: `[steam]`, `[session]`, `[run]`, `[ui]`, `[sync] place=`.

## Teste com o amigo (duas contas, duas máquinas)

Antes:
- Os dois com o jogo na versão **1.5.3.2** e o mesmo pacote do mod (**0.7.0**, o zip do `tools/package.ps1`). Versões diferentes são recusadas.
- Os dois abrem o jogo **pela Steam** (o amigo não precisa do `steam_appid.txt`).
- Overlay da Steam: opcional. Sem ele, o convite sai pelo código ou pela janela de amigos da Steam (passo 1).

Roteiro:
1. Host: menu → **Co-op** → **Convidar amigo** (cria o lobby). Com overlay: escolha o amigo no diálogo. Sem overlay: na janela de amigos da Steam, fora do jogo, clique com o botão direito no amigo → "Convidar para o jogo" (aparece enquanto o host está no lobby). Se não aparecer, use o passo 8 (código).
2. Amigo, **com o jogo aberto** no menu: aceita o convite no chat da Steam. O painel dele mostra "Conectado com <host> (Steam, x ms)".
3. Uma run inteira: marionetes, dano nos dois sentidos, magias, XP, loot, cair e reviver, boss. Anote o ping.
4. Voltem ao menu e façam uma segunda run.
5. Amigo: **Desconectar**. O host mostra "<nome> desconectou" e continua com o lobby aberto.
6. Amigo **fecha o jogo**. Host: **Convidar amigo** de novo → amigo aceita → a Steam abre o jogo e ele entra sozinho quando chegar no menu.
7. Amigo pelo **"Entrar no jogo"** na lista de amigos da Steam, sem convite.
8. Pelo código: host **Copiar código**, manda no chat, o amigo cola no campo do painel → **Entrar**.
9. Queda: o amigo fecha o jogo no meio da run → o host vê "desconectou" e continua sozinho. Depois o contrário (o host fecha).
10. Os dois mandam `BepInEx/LogOutput.log`.

## Pacotes

40 `Hello`, 41 `Accept`, 42 `Reject`, 43 `Bye`, 44 `Ping`, 45 `Pong` (internos do `SteamTransport`). 46–49 reservados.

## Riscos conhecidos

- A fatia de snapshot (1200 B) fica no limite de um pacote do relay. Se o amigo vir inimigos "pulando", baixar `EnemyMessages.MaxPacketBytes` para 1100.
- Tudo até aqui rodou com 0 ms de latência. Com ping real, a fase 8 pode encontrar problemas de sincronia.
