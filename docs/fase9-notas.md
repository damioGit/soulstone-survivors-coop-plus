# Fase 9: até 8 jogadores

**Implementado sem teste em jogo.** Só compilado (`-p:SkipDeploy=true`, 0 erros, 0 avisos). A investigação e o desenho estão em `docs/fase9-investigacao.md`. O `PluginVersion` não mudou (a sessão principal publica a 0.9.0).

> **Formato de rede mudou.** A chave do IP (`soulstonecoop-v2`) e o protocolo da Steam (`scoop_proto = 2`) subiram: um build de dois jogadores é recusado na conexão, mesmo com o mesmo `PluginVersion`.

## Commits (branch `fase9-oito`)

| Etapa | Commit | O que entrou |
|---|---|---|
| Investigação | `17a6b7b` | `docs/fase9-investigacao.md` |
| Merge do master | `dacbb9a` | fase 5 completa, painel de level-up |
| 1 | `b80090c` | slots, transportes com vários peers, origem dos pacotes, `--coop-name` |
| 2 | `b986967` | estado por jogador em todos os módulos e as regras com N |
| 3 | `3b5d337` | o host repassa os pacotes de cada cliente aos outros; bots de teste |
| 4 | `2f1c4be` | lobby da Steam e painel para 8 (`MaxPlayers` padrão 8, `LobbyType`) |
| 6 | `d1d33bc` | `EnemyScalePerPlayer`, corte de réplicas longe, telemetria `[net]` |
| Merge do master | `88b4be3` | dash do fantasma (`DashState` = 54), por jogador e repassado |

A etapa 5 (regras com N) entrou junto com a 2: ao trocar cada módulo para "por jogador", a regra já foi escrita para N. Com 2 jogadores ela dá o mesmo resultado de antes, com as exceções listadas em "O que muda com 2".

## Como funciona

### Ids e topologia

- **Estrela, com o host como servidor.** O host continua dono do mundo: inimigos, loot, objetivos, revive e fim de run.
- **Slots:**
  - 0 é o host; 1 a 7 são os clientes (`Net/Players.cs`).
  - O host dá ao cliente o menor slot livre e manda primeiro o `Welcome` (102): o slot dele, o tamanho da sessão e quem já está dentro.
  - Quando alguém sai, o host avisa os outros com `PlayerLeft` (103), no canal ordenado, antes de entregar o slot de novo.
- **De quem veio cada pacote:**
  - o cliente nunca escreve o próprio slot. O host carimba a origem pela conexão de onde o pacote chegou (`PacketRouter.From`);
  - num cliente, `From` é 0 (o host) ou a origem que vem no envelope `Relayed` (100: `origem u8 | pacote original`).
- **Repasse** (`PacketRouter.RelayMethod`). Esses pacotes de um cliente o host aplica e também repassa aos outros:
  - `Hello`, `LobbyState`, `PauseState`, `PartnerHealth`, `XpShare` e `DashState` (o dash do master `c8876dc`, um por fantasma);
  - `SkillCast` e `SkillChannelEnd`;
  - `SummonSpawn`, `SummonDespawn` e `SummonStates`.

  Quem chega recebe na hora o `Hello`, o `LobbyState` e a pausa de quem já estava.
- **Posições:** o cliente manda o `PlayerState` (2) ao host. O host manda a todos um `PlayersState` (101) por tick (20 Hz) com a posição dele e a última de cada cliente. Assim cada cliente recebe um pacote só por tick, em vez de um repasse por jogador.
- **Unicast** (só para um cliente): `DamageToClient`, `ProxyStatus`, o reenvio dos inimigos para quem entra na run e o `RunStart` da reentrada.

### Por jogador

- **Sessão:** `Lobby/RemotePlayer.cs`, um registro por outro jogador, com identidade (`Hello`), lugar (`LobbyState`) e fantasma. `CoopSession.Remotes`/`Get(slot)` substituem o `Remote`/`RemotePlace` de antes.
- **Fantasmas:**
  - um por jogador que está na run com você;
  - cada um com nome, barra de vida e ícone no minimapa;
  - cada jogador tem uma cor (anel do chão e nome): host verde, depois azul, amarelo, rosa, laranja, lilás, água e branco.
- **Host:**
  - um proxy por cliente (`RemoteProxy`);
  - um lançador remoto por jogador (`RemoteCaster`, nos dois lados);
  - status do proxy encaminhados só ao cliente dono (`ProxyStatusForward`);
  - summons separados por dono (`SummonSync`).

### Regras com N jogadores

- **Revive:** qualquer aliado vivo a ≤ 2,5 u de um caído enche a barra dele em 3 s. Cada caído tem a própria barra. Longe de todos, a barra cai em dobro.
- **Derrota:** só com **todos** os jogadores da run fora (caído ou morto) por 0,5 s.
  - Quem está no menu não conta.
  - O cliente que desistiu conta como fora.
- **Espectador:** o caído assiste o aliado vivo mais perto. A escolha é refeita a cada 1 s e troca quando esse aliado cai.
- **Desistir:**
  - o cliente que desiste sai **sozinho** (tela de fim só para ele) e os outros seguem;
  - a desistência do host encerra a run de todos.
- **Portal Home:** qualquer um que entra leva todos.
- **Portais Endless/BossRush:** o host decide e todos seguem. Quem não tem o mapa vai pelo Home; o host vê "X não entrou".
- **Pausa** (regra do usuário):
  - o Esc de qualquer um congela todo mundo;
  - **o host despausa** fechando a própria pausa (Esc duas vezes) ou com **F4**. Quem estava pausando continua com o menu aberto, mas deixa de congelar os outros até pausar de novo (`PauseOverride`, 104);
  - a faixa do host mostra "X pausou o jogo (Esc duas vezes ou F4 despausa)".
- **Level-up:** pela `LevelUpPolicy`. O padrão `Overlay` não pausa ninguém. Com `Always`:
  - com 2 jogadores, pausa os dois, como antes;
  - com 3 ou mais, pausa só o próprio jogo.
- **Barreira de carregamento:** segura até **todos** que aceitaram o `RunStart` saírem do carregamento (limite de 45 s). A faixa lista quem falta.
- **Lutar:** bloqueado enquanto alguém não voltou ao menu. A faixa diz quem.
- **Host:**
  - spawn em rodízio entre os jogadores vivos (com 2 é a alternância de antes);
  - o reciclador poupa o que está perto de qualquer cliente vivo;
  - os seekers vão atrás do jogador vivo mais perto.
- **XP:** cada orbe cai no mundo do jogador vivo mais perto (`LootDrop` com `ownerSlot`). Quem coleta repassa a todos (XP de equipe para N).
- **Reentrada** (`AllowRejoin`): por jogador. O `RunStart` vai só para quem chegou.

### Steam

- **Tamanho:** o lobby é criado com `[Coop] MaxPlayers` vagas (padrão **8**).
- **Quem entra:** `[Coop] LobbyType`.
  - `FriendsOnly` (padrão). Pela documentação da Steam, "joinable by friends and invitees". Como **qualquer membro do lobby pode convidar** (o botão "Convidar amigo" agora funciona também no cliente), o amigo de um cliente entra pelo convite desse cliente.
  - `Public`: qualquer um com o código.
  - `Private`: só convidados.
- **Não confirmado:** se o amigo de um cliente consegue entrar **só pelo código**, sem convite, num lobby `FriendsOnly`. Se não der, use `LobbyType = Public` ou o convite.
- **Botão:** mostra as vagas ("Convidar amigo (3/8)") e desliga com o lobby cheio. "Copiar código" funciona para qualquer membro.
- **Transporte:** uma sessão `SteamNetworkingMessages` por cliente, cada uma com ping, timeout (15 s, ou 45 s carregando) e sequência do canal 1 próprios. O `Accept` (41) leva o slot.

### Balanceamento e desempenho

- **`[Coop] EnemyScalePerPlayer`** (host, **0 = desligado**, padrão):
  - multiplica o `MaxTargetEnemiesAlive` e o `TargetEnemiesAliveDifficultyFactor` do spawner regular e o `AliveUnitLimit` das filas por `1 + k × (jogadores na run − 1)`, com teto de 200;
  - é reaplicado quando o número de jogadores muda e nunca multiplica duas vezes (`Enemies/EnemyScaling.cs`, log `[balance]`);
  - **sugestão: 0,25** (8 jogadores = ×2,75);
  - elites, bosses e vida dos inimigos não mudam.
- **`[Coop] ReplicaCastRadius`** (60 u): magia de quem está mais longe que isso não é reproduzida (é só visual e estaria fora da tela). A linha `[casts]` mostra `culledFar=`. 0 desliga.
- **Telemetria:**
  - `[net]` a cada 5 s: payload de subida e descida por slot (sem cabeçalho de transporte);
  - `[sync]`: slot, peers, cada jogador com lugar, ping, posição e quantas posições chegaram, e quantos pacotes foram repassados.

## O que muda com 2 jogadores (em relação à 0.8.x)

- **Desistir no cliente** só tira o cliente da run (antes encerrava a dos dois). Decisão do usuário.
- **Pausa:** o host pode despausar (Esc duas vezes ou F4).
- **Host caído com o cliente no menu (fora da run):** antes ficava caído para sempre (ninguém podia reviver). Agora é derrota, como no solo.
- **Cor do fantasma:**
  - o host vê o cliente (slot 1) em azul;
  - o cliente vê o host em verde, como antes.
- **Painel:** maior, com a lista de jogadores.

## Riscos (sem teste)

1. **Transporte Steam reescrito para várias sessões.** É a parte mais delicada e só dá para testar com contas diferentes. Logs: `[steam] Client ... connected over Steam -> slot N`, `Peer ... gone`.
2. **Refactor grande** (16 módulos de "um parceiro" para "por jogador"). Um erro de cleanup aparece como fantasma, proxy ou lançador que não some quando alguém sai, ou um NRE com `[players] Left(N) handler` no log.
3. **Pausa com N:** um cliente com a pausa liberada pelo host fica com o mundo dele parado (o próprio Esc), enquanto os outros seguem. Quando ele voltar, as marionetes se ajustam pelo snapshot.
4. **Lobby `FriendsOnly`:** entrada por código de um amigo de cliente (ver acima).
5. **Ordem entre origens:** um pacote Sequenced (posição de summon) de um jogador pode descartar o de outro se chegar fora de ordem no canal 1. Só afeta o próximo quadro.
6. **Desempenho com 8:** VFX de 8 builds e 14 Dummies no host. O corte por distância ajuda; medir com `[net]` e o FPS.
7. **Steam, reconexão rápida com a mesma conta:** se um cliente cai sem `Bye` (fechou o jogo) e volta antes dos 15 s em que o host percebe, o host só reenvia o `Accept`, sem `Welcome`. O cliente fica sem slot: não é revivido e não recebe orbes de XP. Saída: o cliente desconecta, espera uns 15 s e entra de novo. (Pendência da revisão, não corrigida.)

## Correções da revisão (commit da revisão)

- Cliente que desistiu sozinho ignora a vitória, o portal Home e os portais Endless/BossRush que o host toma depois (`CoopLife.LeftAlone`).
- Host que aperta Desconectar no meio da run recicla todos os proxies (a limpeza roda com o papel ainda de host).
- Com `AllowRejoin`, quem já estava na run e voltou ao menu (desistiu) não é puxado de volta.
- Cliente que saiu da run com um `ClientState` antigo de "vivo" não impede mais a derrota.
- `EnemyScalePerPlayer` nunca baixa o limite original do jogo.
- Fechar a pausa do host só libera pausas de Esc; o F4 libera também um level-up que pausa.

## Ferramentas de teste

- **`--coop-name <nome>`:** o nome no `Hello` (várias instâncias com a mesma conta Steam ficariam todas com o mesmo nome).
- **Bots** (`tools/TestClient`): clientes falsos por IP que seguem o host na run e andam em volta dele.

  ```
  cd tools\TestClient
  dotnet run -c Release -- --host 127.0.0.1 --bots 5 --version 0.8.0
  ```

  - `--version` tem que ser o `PluginVersion` do mod;
  - o host precisa de `MaxPlayers` ≥ bots + clientes de verdade + 1;
  - cada bot imprime a cada 5 s o que recebe do host (pacotes e KB/s), que é a subida do host por cliente;
  - os bots não levam dano nem lançam magia.

## Roteiro de teste

### A. Três instâncias no mesmo PC (por IP)

- **Preparação:**
  - `BepInEx/config/com.kolber.soulstonecoop.cfg`: `[Coop] MaxPlayers = 8`, `[Debug] AutoHost = false`, simulação em 0;
  - três atalhos do `Soulstone Survivors.exe` (janela pequena, gráfico no mínimo):
    - **A (host):** `-screen-fullscreen 0 -screen-width 960 -screen-height 540 --coop-name A`
    - **B:** `-screen-fullscreen 0 -screen-width 960 -screen-height 540 --coop-name B --coop-connect 127.0.0.1`
    - **C:** igual ao B com `--coop-name C`
  - A hospeda pela aba Co-op (**Hospedar por IP**). B e C conectam sozinhos pelo argumento (ou pelo painel: Entrar).
- **Logs:** `LogOutput.log`, `LogOutput.1.log` e `LogOutput.2.log`, na ordem de abertura.

1. **Conexão:**
   - painel de A: "Hospedando (IP): 3/8 jogadores", com B e C, ping e "no menu";
   - painel de B: "Conectado com A", e "No grupo (3/8): C".
   - Log: `[players] Welcome: I'm slot 1` (B) e `slot 2` (C).
2. **Início:**
   - A aperta Lutar e os três carregam o mesmo mapa;
   - quem chega primeiro vê "Aguardando B, C carregar...".
3. **Fantasmas:**
   - cada um vê os outros dois, com o personagem, o nome e a barra certos e anéis de cores diferentes;
   - B vê C andando liso (posição repassada pelo host);
   - dois ícones extras no minimapa.
4. **Inimigos e dano:**
   - os inimigos nascem em volta dos três (`[proxy] spawnRedirects=` no A);
   - B e C apanham e matam;
   - a linha `[proxy]` de A lista os slots 1 e 2 com `hits` e `dmg`.
5. **Magias:**
   - cada um vê as magias dos outros dois, inclusive B vendo as de C;
   - nenhuma réplica dá dano (`blockedDamage`).
6. **XP de equipe:** depois de 1 min, os três com o mesmo nível/XP. `[loot]` de A mostra drops com dono 1 e 2.
7. **Pausa:**
   - Esc em B: os três congelam. A vê "B pausou o jogo (Esc duas vezes ou F4 despausa)";
   - A aperta F4: A e C voltam a andar; B continua no menu e vê "O host despausou: os outros continuam jogando". A e C veem "B no menu de pausa (o jogo segue)";
   - B fecha o menu e depois aperta Esc de novo: os três congelam outra vez;
   - A aperta Esc duas vezes (abre e fecha a própria pausa): todos voltam.
8. **Caído e revive:**
   - F9 em B; C fica perto 3 s; B levanta ("REVIVEU!" em cima de B nas três telas);
   - com B caído, a câmera dele segue o aliado vivo mais perto.
9. **Derrota parcial e total:**
   - F9 em A e B: a run continua com C vivo; C fica perto de A, que levanta;
   - F9 nos três: derrota nas três telas.
10. **Desistir:** numa run nova, B desiste pela pausa.
    - Só B vai pra tela final; A e C continuam.
    - Em A, o fantasma de B some quando B volta ao menu.
11. **Saída:** feche a janela de C no meio da run.
    - A: "C desconectou";
    - B: "C saiu", e o fantasma de C some;
    - os inimigos que perseguiam C vão para outro jogador.
12. **Portal Home:** F10 em A e B entra no portal azul. Os três vão para a tela final.
13. **Portal Endless:** F10 e F5 em A. Os três vão para o mesmo mapa.
14. **Reentrada (opcional):** `AllowRejoin = true`, A numa run, reabra C: ele entra na mesma partida.
15. **Balanceamento:** `EnemyScalePerPlayer = 0.25` e uma run nova.
    - Log de A: `[balance] players=3 k=0.25 mult=1.5: MaxTarget 40->60 ...`;
    - mais inimigos vivos.
16. **Bots (carga):** com A, B e C numa run, rode `dotnet run -c Release -- --bots 5`.
    - Os bots entram na próxima run do A.
    - Anote a linha `[net]` de A (subida total) e o FPS de A e de B.

### B. Três ou mais contas Steam

1. Host: aba Co-op → **Convidar amigo** e convide o amigo 1. O botão mostra "(2/8)" quando ele entra.
2. Amigo 1, já no lobby: **Convidar amigo** e convide um amigo dele (amigo 2), que não precisa ser amigo do host. O amigo 2 entra. Se não entrar, anote a mensagem e teste com `LobbyType = Public` no host.
3. **Copiar código** em qualquer um e um terceiro entra pelo código.
4. Repita os passos 2 a 13 do roteiro A com rede real e anote o ping de cada um no painel do host.
5. **Quedas:**
   - um cliente fecha o jogo e os outros continuam;
   - o host fecha o jogo e todos os clientes encerram em 4 s.
6. Mandem todos os `LogOutput.log` (linhas `[steam]`, `[players]`, `[net]`, `[sync]`, `[life]`, `[pause]`).

## Pacotes

**Novos:**

| # | Nome | Sentido | Conteúdo |
|---|---|---|---|
| 100 | `Relayed` | host → cliente | origem u8, pacote original |
| 101 | `PlayersState` | host → clientes, Sequenced | count u8, count × (slot u8, x, y, z, yaw f32, sentMs u32) |
| 102 | `Welcome` | host → cliente novo | seuSlot u8, maxPlayers u8, n u8, n × slot u8 |
| 103 | `PlayerLeft` | host → clientes | slot u8 |
| 104 | `PauseOverride` | host → clientes | máscara u8 dos slots com a pausa liberada |

Livres: 105–119.

**Formatos que mudaram:**

| Pacote | Mudança |
|---|---|
| `PlayerState` (2) | só cliente → host |
| `Revive` (5) | `target` = slot |
| `ClientState` (13) | o primeiro byte é 0 vivo, 1 caído, 2 morto |
| `SkillCast` (14) | `TargetPlayer` (3) com o slot |
| `LootDrop` (16) | `xpOwner` u8 antes do XP |
| `CoopLife` (18) | lista de (slot, caído, progresso) |
| `EnemyCast` (21) | `TargetPlayer` (3) com o slot no lugar de host/cliente |
| `Accept` da Steam (41) | leva o slot |

## Opções novas no `.cfg`

| Chave | Padrão | O que faz |
|---|---|---|
| `[Coop] MaxPlayers` | 8 | tamanho da sessão do host (2–8) |
| `[Coop] LobbyType` | FriendsOnly | quem entra no lobby da Steam (FriendsOnly, Public, Private) |
| `[Coop] EnemyScalePerPlayer` | 0 | mais inimigos por jogador extra (sugestão 0,25) |
| `[Coop] ReplicaCastRadius` | 60 | distância máxima para reproduzir a magia de outro jogador (0 = sempre) |
