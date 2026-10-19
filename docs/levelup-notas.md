# Level-up sem pausa: notas

Pedido do usuário (feedback da 0.7.0): com a build completa, escolher upgrade sem pausar a sessão. Investigação em `docs/levelup-investigacao.md`. **Compilado, não testado em jogo.**

## O que entrou

- **Opção `[Coop] LevelUpPause`** no `.cfg` (`BepInEx/config/com.kolber.soulstonecoop.cfg`), com três valores:
  - `Always`: o level-up pausa os dois, como antes.
  - `NotWhenFullBuild` (**padrão**): pausa até todos os slots de skill estarem ocupados. Daí em diante, escolhe com o jogo rodando.
  - `Never`: nunca pausa.
  - Vale só em co-op (parceiro conectado e os dois na run). Sozinho, o jogo pausa como sempre.
  - Cada jogador decide os próprios level-ups. Se você usa `Never` e o amigo usa `Always`, os seus não pausam e os dele pausam os dois.
- **"Build completa"** = `LevelUpPanelManager.IsFullOfSkills()`, o mesmo teste que o jogo usa para a tela de troca de skill: slots ativos do `GameProperties` mais os slots extras do inventário. É reavaliado a cada 0,5 s com o popup aberto. Se der erro, conta como "não completa" e mantém a pausa.
- **Como funciona** (`Net/LevelUpPolicy.cs` e `Net/PauseSync.cs`): o postfix de `TimeUtil.SetTime` que já existia recalcula o `timeScale` sem o fator `LevelUpTime`, mas com Esc, death guard, câmera lenta, controle desconectado, créditos e PlayerData. O painel do jogo continua igual: abre, sorteia, aceita clique e troca o input como sempre.
- **Rede:** sem pacote novo. O `PauseState` (6) ganhou o valor `Choosing = 3` ("escolhendo sem pausa"), que **não** congela o parceiro. A faixa de avisos mostra "Fulano escolhendo upgrade (sem pausa)". Esc continua pausando os dois, inclusive com o popup aberto (estado `Pause`).
- **Telemetria `[levelup]`** (só em co-op):
  - `Popup opened: mode=… fullBuild=… coop=… -> NO PAUSE|pause (queued=…, level=…)`;
  - `Policy changed with the popup open…`, quando o último slot enche no meio de uma fila de escolhas ou o parceiro sai ou entra;
  - `Popup closed after X s (…). Run so far: N popups, A paused (s), B without pause (s)`.
- **Atalho de debug F6** (co-op, na partida): +1 nível no jogador local. **Shift+F6** dá +5. Usa `ExperienceComponent.LevelUpMultipleTimes` direto, então **não** é XP de equipe: o parceiro não sobe junto (ele aperta F6 na janela dele se quiser).

## Riscos

1. **Quem escolhe fica parado.** Com o popup totalmente aberto, o próprio jogo desliga o input de gameplay (`InputManager.SwitchToUIMap`): o personagem não anda nem dasha, só o autocast continua. Agora o mundo segue, então dá para levar dano e até cair escolhendo. É o preço do pedido. Religar o movimento junto com a UI ficou de fora, porque WASD/stick também navegam nas opções e o confirmar pode bater com o dash.
2. **XP chegando com o popup aberto** (kills do autocast, XP do parceiro) enfileira mais escolhas no meio. O jogo trata isso (`OnOptionSelected` tira a primeira e reabre com a próxima), mas antes isso nunca acontecia em co-op, porque o tempo parava.
3. **Escolher caído:** o popup pode estar aberto quando o jogador cai (CoopLife). A escolha só aplica stats no próprio personagem. Não vi problema no código, mas é caminho novo.
4. **Versões diferentes do mod:** uma versão antiga trataria `Choosing` como pausa e congelaria. O handshake já exige a mesma versão, então não deve acontecer.
5. **A build completa pode não bater com o que o jogador sente:** com os slots cheios, a cada 3 níveis ainda aparece skill nova com a tela de troca, e ela também não pausa mais. Se incomodar, dá para pausar só nesses níveis.

## Roteiro de teste

Os dois com a mesma build do mod e o `.cfg` no padrão (`LevelUpPause = NotWhenFullBuild`). Abra o jogo uma vez para o `.cfg` ganhar a linha nova.

1. **Antes da build completa (pausa como antes):** entrem numa run em co-op. No primeiro level-up do host, os dois congelam e o cliente vê "… escolhendo upgrade...". Repitam com o cliente subindo de nível. No log: `[levelup] Popup opened: … fullBuild=False … -> pause`.
2. **Chegar rápido na build completa:** na janela de quem vai testar, aperte **Shift+F6** várias vezes. Cada vez enfileira 5 escolhas. Sempre que aparecer skill nova, pegue a skill, até os slots encherem. O log mostra `fullBuild=True` quando chega lá, ou `Policy changed…` se encher no meio da fila.
3. **Build completa no CLIENTE:** com o cliente cheio, aperte F6 no cliente. O popup abre e o jogo **não** para: os inimigos continuam vindo, o autocast segue e o personagem fica parado. No host, nada congela e aparece "… escolhendo upgrade (sem pausa)". Escolha e confira que o aviso some.
4. **Build completa no HOST:** mesmo teste com o host cheio e F6 no host. O mundo do cliente continua andando (inimigos se mexendo, dano chegando) enquanto o host escolhe.
5. **Esc no meio:** com o popup aberto sem pausa, aperte Esc. Os dois congelam ("pausou o jogo"). Feche o menu e veja tudo voltar a andar com o popup ainda aberto.
6. **Death guard:** com uma carga de segunda chance, leve um golpe letal (F9) com o popup aberto sem pausa. A câmera lenta curta do death guard ainda acontece.
7. **`Never`:** troque para `LevelUpPause = Never` num dos dois, reinicie o jogo e, logo no começo (sem build completa), suba de nível com F6. Não pausa.
8. **`Always`:** troque para `Always`. Mesmo com a build completa, pausa os dois como na 0.7.0.
9. **Sozinho:** numa run sem co-op, o level-up pausa como no jogo original, com qualquer valor.
10. Mande o `LogOutput.log` dos dois (linhas `[levelup]` e `[pause]`).
