# Level-up sem pausa: notas

Investigação em `docs/levelup-investigacao.md`.

- **0.8.0:** level-up sem pausa depois da build completa, com o painel do jogo em tela cheia. Feedback da 0.7.0.
- **0.8.1:** **painel compacto (overlay)**. Feedback da 0.8.0: "fica paia sem pausar". Com o painel cobrindo a tela e o personagem parado, não dá pra jogar. **Compilado, não testado em jogo.**

## Opções no `.cfg` (`BepInEx/config/com.kolber.soulstonecoop.cfg`, seção `[Coop]`)

- `LevelUpPause`:
  - `Always`: o level-up pausa os dois, como na 0.7.0.
  - `NotWhenFullBuild` (**padrão**): pausa até todos os slots de skill estarem ocupados. Daí em diante, cada level-up abre no **painel compacto**, sem pausar ninguém.
  - `Overlay`: nunca pausa e sempre usa o painel compacto, desde o primeiro nível.
  - `Never`: nunca pausa, com o painel do jogo em tela cheia e o personagem parado (o comportamento da 0.8.0, pra quem preferir).
- `LevelUpOverlayScale` (padrão `0.6`): tamanho do painel compacto. Ele ainda encolhe se não couber na largura da tela.
- `LevelUpOverlayOpacity` (padrão `0.8`): opacidade com o mouse fora do painel. Com o mouse em cima, fica opaco.
- `LevelUpOverlayPosition` (padrão `Bottom`): `Bottom`, `BottomLeft`, `BottomRight` ou `Top`.
- Só vale em co-op (parceiro conectado e os dois na run). Sozinho, o jogo pausa como sempre. Cada jogador decide os próprios level-ups.
- **Por que o padrão continua `NotWhenFullBuild`:** as primeiras escolhas montam a build (skill nova, descrição longa) e pausar ali ajuda. As escolhas frequentes do fim, que eram o incômodo, viram overlay. Quem quiser overlay desde o começo usa `Overlay`.

## Painel compacto (overlay)

- **Visual** (`Ui/LevelUpOverlay.cs`): é o painel do próprio jogo, sem recriar nada. A cada frame, depois do `LevelUpPanelManager.OnUpdate`:
  - o grupo do popup (`PowerUpAnimation`) é reduzido, levado pra borda escolhida com 2% de margem (posição calculada pelos cards e botões visíveis) e fica semitransparente por um `CanvasGroup`;
  - as imagens em tela cheia dentro do popup (o fundo escuro) são desligadas;
  - a tela de troca de skill (`ReplaceSkillsAnimation`) recebe o mesmo tratamento, se for um grupo separado.
  - As animações de UI do jogo só escrevem quando o tempo delas muda. Então a escala e a posição "base" são as últimas que o jogo escreveu, e o fade de abrir e fechar continua (a opacidade é multiplicada pela visibilidade da animação).
- **HUD visível:** `GameplayUIManager.HasOverlayPanel` (0x605150) fica verdadeiro com o popup aberto, e os 23 painéis do HUD (vida, mana, fúria...) se escondem com ele. Com o overlay, um postfix responde como se o popup estivesse fechado (as mesmas checagens do original, menos a do popup).
- **Movimento:** com o popup aberto, o jogo chama `InputManager.SwitchToUIMap` (liga o mapa UI e desliga o de gameplay). O `PlayerCharacter.UpdateAll` (0x7A9650) lê o movimento por `InputManager.GetMovementValue` e não olha painel nenhum, então basta religar o mapa de gameplay. Os conflitos são resolvidos por ação, olhando as teclas em que cada uma está ligada:
  - ação de UI na mesma tecla de uma ação de gameplay (navegação no WASD, confirmar na tecla do dash...) fica desligada enquanto o overlay está aberto;
  - **exceções**, que ganham do gameplay: as teclas de opção do jogo (`SelectPowerup1..7`), o menu de pausa e o cancelar. Nesses casos, quem é desligada é a ação de gameplay da mesma tecla;
  - as teclas do overlay (1-4, numpad 1-4 e Tab) também ganham do gameplay;
  - os botões do mouse do gameplay (skill manual, clique pra andar, dash do mouse) ficam desligados **só com o mouse em cima do painel**, pra um clique escolher a opção sem lançar skill.
  - Ao fechar, tudo volta e o mod chama `SwitchToGameplayMap`, porque o jogo só volta sozinho quando o mapa de gameplay está desligado.
  - O log `[levelup] Overlay input:` lista o que foi desligado.
- **Teclas:** **1, 2, 3, 4** (ou o numpad) escolhem a opção, ou o slot na tela de troca de skill. Elas marcam o mesmo `LevelUpPanelItem.Selected` que as teclas do próprio jogo, então o tempo mínimo, os modos lock/banish e a troca de skill continuam valendo. Se as teclas de opção do jogo já estiverem nesses números, quem escolhe é o jogo, e o mod não escolhe de novo. Reroll, banish, lock e skip usam as teclas do próprio jogo, se não baterem com o gameplay, e os botões do painel funcionam com o mouse. **Tab** esconde e mostra o painel. Escondido, as teclas de opção ficam desligadas e a faixa de avisos mostra "Level up! (N) - Tab mostra". O estado escondido continua nos próximos level-ups, até apertar Tab de novo ou começar outra run.
- **Parceiro:** com o overlay, o estado mandado é `None`. Não congela e não mostra aviso nenhum. Em `Never`, continua o "escolhendo upgrade (sem pausa)".
- **Controle (gamepad):** com o gamepad como input ativo, o painel fica compacto, mas o input continua como o jogo faz: o stick navega nas opções e o personagem fica parado.
- **Diagnóstico:** na primeira abertura da sessão, o log mostra a hierarquia do popup (`Popup hierarchy:`, até 60 linhas) e onde o painel foi posto (`Overlay on the popup …`). Se o layout ficar estranho, esse trecho mostra o que ajustar.

## Base (0.8.0, continua valendo)

- **"Build completa"** = `LevelUpPanelManager.IsFullOfSkills()`, o teste do próprio jogo (slots ativos + slots extras do inventário), reavaliado a cada 0,5 s com o popup aberto. Se der erro, conta como "não completa".
- **Sem pausa:** o postfix de `TimeUtil.SetTime` recalcula o `timeScale` sem o fator `LevelUpTime`. Esc, death guard, câmera lenta etc. continuam valendo.
- **Rede:** sem pacote novo. O `PauseState` (6) tem `Choosing = 3` (só em `Never`). Esc continua pausando os dois.
- **Telemetria `[levelup]`:** `Popup opened … -> pause | NO PAUSE (overlay) | NO PAUSE (full panel)`, `Policy changed…`, `Popup closed after X s …`, além das linhas do overlay acima.
- **Atalho de debug F6** (co-op, na partida): +1 nível no jogador local; **Shift+F6** dá +5. Não é XP de equipe.

## Riscos

1. **Layout às cegas:** não deu pra ver a hierarquia do painel sem abrir o jogo. Se o fundo escuro não for uma imagem em tela cheia dentro do popup, ou se o popup tiver um layout que reposiciona os filhos, o painel pode ficar mal posicionado ou com o fundo ainda escuro. O log da hierarquia existe pra isso.
2. **Teclas:** a resolução é por ação inteira. Se a navegação usa setas e WASD, ela some inteira, setas incluídas. Se o jogador remapeou alguma tecla de UI pra uma de movimento, aquela ação de UI some no overlay (fica no log). O mouse sempre funciona.
3. **Esc com o overlay:** a pausa de gameplay na mesma tecla do menu de pausa da UI fica desligada, então o Esc vai pela UI, como no popup normal. Vale conferir que ele abre e fecha a pausa uma vez só.
4. **Clique no painel:** com o mouse em cima, o botão do mouse do gameplay fica desligado. Quem joga com "clique pra andar" para de andar enquanto o cursor está no painel.
5. **XP chegando com o painel aberto** enfileira mais escolhas (o jogo trata isso), e dá pra cair escolhendo. Agora dá pra andar, o que ajuda.
6. Quem escolhe com o overlay **anda e luta**. Se algo no meio travar o input (um painel do jogo que não previ), o log mostra `Overlay input` e o Esc deve destravar.

## Roteiro de teste (0.8.1)

Os dois com a 0.8.1. Abra o jogo uma vez para o `.cfg` ganhar as linhas novas. Pra testar rápido, use `LevelUpPause = Overlay` nos dois (reinicie o jogo depois de editar o `.cfg`).

1. **Overlay no CLIENTE:** em co-op, na partida, aperte **F6** no cliente. O painel aparece menor, semitransparente, na borda de baixo; o centro da tela fica livre e o HUD continua visível. Ande com WASD: o personagem anda e o autocast segue. No host, nada congela e não aparece aviso.
2. **Escolher pelo teclado:** com o painel aberto, aperte **1**, **2** ou **3**. A opção é escolhida uma vez só (confira no log uma linha `Key N` ou nenhuma, se o jogo já usa essas teclas), e o painel fecha ou vai pra próxima da fila.
3. **Escolher pelo mouse:** abra outro (F6) e clique numa opção. Escolhe sem lançar skill no clique. Com o mouse em cima, o painel fica opaco; fora, semitransparente.
4. **Fila:** aperte **Shift+F6** (5 níveis). Escolha as 5 seguidas pelo teclado enquanto anda.
5. **Tab:** com o painel aberto, aperte **Tab**. O painel some e a faixa mostra "Level up! (N) - Tab mostra". Os números não escolhem nada escondido. Tab de novo traz o painel de volta.
6. **Esc:** com o painel aberto, aperte Esc. Os dois pausam ("pausou o jogo"). Feche a pausa: o painel continua lá e dá pra andar de novo.
7. **Overlay no HOST:** repita o 1 e o 2 no host. O mundo do cliente não para.
8. **Troca de skill:** com os slots cheios, quando aparecer skill nova, escolha e veja a tela de troca (também compacta). Escolha o slot com 1-4 ou clicando.
9. **Padrão (`NotWhenFullBuild`):** volte o `.cfg` pro padrão. Antes da build completa, o level-up pausa os dois com o painel grande, como sempre. Encha os slots (Shift+F6, pegando skill nova) e veja os próximos virarem overlay (`NO PAUSE (overlay)` no log).
10. **`Never` e `Always`:** `Never` = painel grande sem pausa (personagem parado). `Always` = pausa sempre.
11. **Gamepad (se tiverem):** com o controle, o painel fica compacto e o stick navega nas opções (personagem parado).
12. Mande o `LogOutput.log` dos dois (linhas `[levelup]`, principalmente `Popup hierarchy`, `Overlay on the popup` e `Overlay input`).
