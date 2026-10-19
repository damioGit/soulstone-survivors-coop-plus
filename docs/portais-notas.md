# Portais Endless/BossRush e Reiniciar em co-op

**Implementado sem teste.** O usuário testa depois. A investigação está em `docs/portais-investigacao.md`.

Feedback que originou: "o avanço das fases, modo infinito e desafio não funcionam, só o portal azul" (0.7.0 com um amigo). Era o bloqueio deliberado da 4A: esses portais sorteiam o próximo mapa em cada máquina.

## O que entrou

### Portais Endless (infinito) e BossRush (desafio): o host decide, os dois vão juntos

O jogo já leva a progressão de cada jogador para a próxima fase (nível, XP, poderes, itens, afixos), em cada máquina (`SetEndlessData` → `InitialEndlessData`). O único sorteio é o mapa do Endless. O BossRush tem mapa fixo.

**Host entra** (trigger do portal, F6 ou pedido do cliente):
- `Lobby/PortalSync.cs`: prefix/postfix em `OnPlayerEnterEndlessPortal` e `OnPlayerEnterBossRushPortal` de `SurvivorsGameManager` **e** de `SurvivorsBossRoomGameManager` (a cena do BossRush);
- o fluxo nativo roda e um postfix só de leitura em `MapCollectionSO.GetRandomMapExcluding` captura o mapa sorteado;
- se o portal realmente abriu (`EnterPortalObjective.Completed`), o host manda `PortalTransition` (90) com o tipo e o mapa.

**Cliente entra no portal:** o fluxo nativo é bloqueado e o cliente manda `PortalRequest` (91). O host entra por código e cai no caso acima. O pedido se repete no máximo a cada 1,5 s enquanto ele estiver no portal.

**Cliente recebe `PortalTransition`:**
1. Marca a transição e revive se estiver caído.
2. Espera o próprio portal abrir, por até 30 s (a vitória chega pelo `RunEnd` com atraso).
3. Confere se tem o mapa.
4. Chama o mesmo `OnPlayerEnter*Portal` (o jogo guarda a progressão local) e depois `GameManagerUtil.LoadNextScene(mapaDoHost, 2 s, false)`, que cancela a carga que o jogo sorteou e agenda a do host.

**Sem o mapa (DLC):** aviso nas duas telas e o cliente vai pelo portal Home.

**Novo estado `LobbyPlace.Transition`:** o portal foi tomado e a cena velha ainda está viva. A sequência é Transition → Loading (cena velha destruída) → InRun (`StartMatch`). A barreira da fase 6 (`HoldForPartner`) agora conta Transition também. Ela nunca trava a cena velha, porque o `LoadNextStageAfterDelay` do jogo espera em tempo escalado.

### Troca de cena dentro da run (vale para portal e Reiniciar)

**Postfix em `GameManagerUtil.UnsetGameManager`** (o `OnDestroy` de todo game manager). Na troca direta de uma cena de jogo para outra, o `Start` do game manager novo roda `SetupGameData` antes do próximo `Update`. Então o tick do mod talvez nunca veja `HasGameManager == false`. O postfix faz na hora exata:
- `CoopSession`: sai da run (`LocalPlayerEntity = null`, fantasma, `LobbyState`);
- `ClientPuppets._matchStarted = false`;
- limpa os caches do `EnemyCasts`.

**Marionetes velhas:** quando o `LobbyState` do host vira Loading, a cena velha dele acabou. Tudo o que ele mandou dela chegou antes, pelo canal ordenado. O cliente descarta as marionetes e o loot pendente nesse momento (`PortalSync.OnHostLeftScene`).

**Outros ajustes:**
- o host não reenvia inimigos ao cliente enquanto ele mesmo ainda está na cena velha;
- o fantasma não nasce enquanto um lado está em transição;
- `PauseSync.ResetForRun()` na transição;
- `CoopLife.PrepareForStageTransition()`: revive e liga `RunEnding` nos últimos segundos da cena velha.

### Reiniciar (tela final)

`Lobby/RunSync.cs`, `CoopRestartPatch` no lugar do bloqueio da fase 6:
- **Host aperta Reiniciar:** o jogo reinicia normalmente.
  - Parceiro já no menu: recebe o `RunStart` de sempre.
  - Parceiro ainda na tela final: recebe `PortalTransition(Restart)` com os parâmetros da run (modo, Mastery, afixos do host). Ele grava os mesmos `Global` e chama `LoadNextScene(mapa, 0, true)`, como o próprio botão faz. A troca de afixos da fase 6 vale de novo, porque `_clientRun` sobrevive ao `UnsetGameManager`.
- **Cliente aperta Reiniciar:** bloqueado, com aviso "quem reinicia é o host".

### Cena do BossRush (`SurvivorsBossRoomGameManager`)

O portal de desafio leva a uma cena com outro game manager: não tem `SurvivorsGameManager` nem `MainObjective`, os bosses nascem por timer (`SpawnNextBoss`) e a vitória vem de `GameManagerUtil.GetMapProgress() ≥ 1`. Antes disso nada do mod cobria essa cena. Agora:
- **cliente:** `SpawnNextBoss` bloqueado (`ClientSpawnBlock.cs`); os bosses do host chegam como marionetes;
- **vitória do host** mirrorada como sempre (`OpenPortalAndObjective`). No cliente, `CoopLife.ForceLocalVictory` completa `GameData.Progression` (`Completed = Total`) quando não existe `SurvivorsGameManager`. O **F10** faz o mesmo no host;
- **portal Home** espelhado também nessa cena (`CoopLifeBossRoomHomePortalPatch`, e `CoopLife.TryEnterPortalLocally` via `PortalSync.EnterHomePortal`).

### Atalho de teste (host)

- **F6:** entra no portal **Endless** por código, depois que os portais abriram (F10 antes).
- **Shift+F6:** entra no **BossRush**.

Motivo: com F10 cedo o jogo **não mostra** os portais roxo/vermelho (`MinTimeForEndlessRush`/`MinTimeForBossRush` contra o tempo de partida). Eles também dependem do progresso do perfil local.

## Pacotes

| # | Nome | Sentido | Conteúdo |
|---|---|---|---|
| 90 | `PortalTransition` | host → cliente | tipo u8 (Endless 0, BossRush 1, Restart 2), mapa; no Restart também cena, modo, Mastery, afixos (mesmo formato do `RunStart`) |
| 91 | `PortalRequest` | cliente → host | tipo u8 (Endless, BossRush) |

A recusa do cliente reaproveita o `RunStartRefused` (33), e o host mostra o motivo. Livres: 92–99.

## Arquivos

- **Novo:** `Lobby/PortalSync.cs`.
- **Editados:**
  - `Lobby/CoopSession.cs` (`Transition`, `BeginStageTransition`/`CancelStageTransition`, `OnGameManagerUnset`, marca do host);
  - `Lobby/RunSync.cs` (Reiniciar, barreira, parâmetros reaproveitados);
  - `Life/CoopLife.cs` (bloqueios da 4A removidos, sala do BossRush, `PrepareForStageTransition`);
  - `ClientSpawnBlock.cs`, `CoopBehaviour.cs`, `Net/PacketRouter.cs`;
  - `Enemies/ClientPuppets.cs`, `Enemies/EnemyCasts.cs` e `Loot/LootSync.cs` (métodos pequenos de reset).

## Riscos

1. **Troca de cena direta**, a hipótese central. Se o postfix de `UnsetGameManager` não bastar, os sintomas na fase nova são:
   - fantasma que não aparece;
   - cliente sem inimigos;
   - barreira que não segura.

   Logs: `[session] Left the run's scene (stage transition…)`, `Local run started`, `[portal] Client: host left its scene`.
2. **Sala do BossRush nunca rodou em co-op.** Pontos a observar:
   - chave das marionetes dos bosses (`res:Bosses/…`, da 4D);
   - o cliente não tem o objetivo "Eliminate X" nem a barra de progresso dessa sala (só as marionetes);
   - vitória forçada pelo `Progression`.
3. **Level-up aberto no cliente quando o host entra:** a carga dele só anda quando ele escolher (a espera do jogo é em tempo escalado). O host fica na barreira por até 45 s.
4. **Mapa de DLC que o cliente não tem:** ele volta pra casa e o host segue sozinho.
5. **Portais diferentes nos dois perfis:** um pode ver o portal roxo e o outro não. Seguir funciona do mesmo jeito, porque a entrada por código não depende do objeto ativo.
6. XP de equipe em trânsito na hora do portal pode se perder (o `EndlessData` já foi gravado).
7. Os dois precisam deste build: o `PluginVersion` não mudou e o estado `Transition` é novo.
8. **A Cruzada continua bloqueada** (seleção de mapa com opções sorteadas, lobby, sangue de titã e bosses próprios). Fica como fase própria.

## Roteiro de teste

Dois jogos conectados pela aba Co-op (ou convite Steam), host = A, cliente = B. Mapa do modo **Survivors**.

1. A começa a run. B entra junto (fase 6).
2. A aperta **F10**: vitória nos dois. Espere uns 6 s até aparecer o objetivo "entre no portal".
3. **Endless pelo host:** A aperta **F6** (ou entra no portal roxo, se aparecer).
   - A: "Portal infinito: indo com B pra <mapa>". B: "Seguindo o host pelo portal infinito".
   - Os dois carregam **o mesmo mapa**; quem chegar primeiro vê "Aguardando … carregar…".
   - Na fase nova cada um mantém nível, skills e itens; o fantasma aparece; B vê os inimigos do host.
4. Na fase 2: **F10** de novo e depois **F6**. Os dois vão de novo pro mesmo mapa (a dificuldade sobe).
5. **Endless pelo cliente:** numa fase com o portal roxo visível em B, B entra nele. B: "chamando o host…", e os dois vão juntos.
6. **BossRush:** volte ao menu, comece outra run, **F10** e depois **Shift+F6**.
   - Os dois vão para a sala do desafio.
   - B **não** deve ter bosses próprios (log `blocked SurvivorsBossRoomGameManager.SpawnNextBoss`); vê os do host.
   - Mate os bosses ou aperte **F10** em A: vitória nos dois (log de B: `Victory (BossRush room)`).
7. Na sala do BossRush, B entra no **portal azul**: os dois vão para a tela final.
8. **Reiniciar com B na tela final:** A aperta Reiniciar. Os dois recomeçam o mesmo mapa, com os afixos de A (log de B `host affixes applied`).
9. **Reiniciar com B no menu:** B volta ao menu, A aperta Reiniciar. B entra pelo `RunStart` normal.
10. B aperta Reiniciar: aviso "Em co-op quem reinicia é o host".
11. **Regressão:** portal azul numa run comum continua levando os dois para a tela final.

Logs úteis: `[portal]`, `[session]`, `[run]`, `[puppets]`, `[spawn]`, `[life] Victory`.
