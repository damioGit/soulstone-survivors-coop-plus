# Fase 6: UI de co-op e início de run sincronizado

**Commitada sem teste completo.** O usuário trocou para a fase 7 (convite pela Steam), que é construída em cima deste código; o teste da fase 6 vai ser feito junto com o da 7. A investigação está em `docs/fase6-investigacao.md`.

## O que entrou

- **Conexão no menu, mantida entre runs.**
  - O `CoopBehaviour` nasce no `MenuManager.Awake` (postfix), e não mais na primeira run.
  - Os argumentos `--coop-host`/`--coop-connect <ip>` e o `AutoHost` do `.cfg` conectam uma vez, já no menu.
  - Os atalhos Insert/Delete continuam, mas ficam desligados enquanto o campo de IP tem foco.
- **Handshake com versão (`Net/NetworkManager.cs`).**
  - O cliente conecta mandando a chave e a versão do mod (`Plugin.PluginVersion`, agora `0.6.0`).
  - O host recusa versão diferente, versão ausente (mod antigo) ou um segundo parceiro, com `request.Reject(motivo)`.
  - O cliente recebe o motivo e mostra, sem tentar de novo. O host mostra "Recusei um cliente com a versão X".
  - Para testar no mesmo PC: `--coop-fake-version 0.5.0` num dos jogos.
  - Desconectar (`Stop()`) agora dispara a limpeza dos módulos e zera o `Role`. Antes o cliente desconectado continuava `IsClientSide` e ficava sem inimigos numa run solo.
- **Sessão (`Lobby/CoopSession.cs`).**
  - `Hello` (30): nome Steam (P/Invoke em `steam_api64.dll`; o wrapper foi stripado), `CharactersType`, arma, skin, skin da arma e versão. É reenviado quando o personagem muda no menu.
  - `LobbyState` (31): Menu / Loading / InRun de cada lado.
  - `CoopSession.Remote` é o que a fase 5 deve usar para montar o fantasma certo.
  - `ClientRunLatched` mantém `CoopRole.IsClientSide` até o fim de uma run começada como cliente, mesmo se a conexão cair.
- **Início sincronizado (`Lobby/RunSync.cs`).**
  - Postfix nos `OnFightPressed` das abas Survivors e Mastery: o host manda `RunStart` (32) com a cena, o modo, o tier e o nível do Mastery e os afixos do perfil dele.
  - O cliente grava os mesmos estáticos de `Global` e chama `MainUIManager.Instance.ChangeScene(cena, 0, Full)`, depois de conferir que está no menu e tem a cena. Se não der, responde `RunStartRefused` (33) com o motivo.
  - **Bloqueios:**
    - o Lutar do cliente ("Aguardando o host começar a partida");
    - o do host enquanto o parceiro não está no menu;
    - Cruzada em co-op;
    - Reiniciar na tela final.
  - **Afixos do host no cliente:** prefix/postfix no `AffixGameplayData..ctor(List<string>, int)`, patch manual em `Plugin.Load`. O conteúdo da lista é trocado só durante o construtor, que copia a lista; o perfil salvo não muda. Se o mapa estiver com afixos desligados no cliente, o flag é ligado só durante o carregamento.
  - **Barreira de carregamento:** `RunSync.HoldForPartner` deixa o `timeScale` em 0 (via `PauseSync`) em quem chegou ao `StartMatch` primeiro, até o parceiro sair de Loading. Limite de 45 s.
- **Resets entre runs.**
  - O fantasma nasce quando os dois estão InRun e some quando um sai (antes nascia no `PeerConnected`).
  - O host reenvia todos os inimigos quando o cliente entra na run.
  - O cliente zera as marionetes ao receber `RunStart`.
  - `PlayerSync` zera o buffer e o `ReceivedCount` com fantasma novo.
  - `PauseSync` só aplica a pausa remota com os dois na run e zera no `RunStart`.
  - `CoopLife` ignora `RunEnd`/`RunEndRequest` fora de partida.
  - A saída da run é detectada pela cena `Menu-Survivors`, além do `HasGameManager`.
- **UI (`Ui/CoopUi.cs`).**
  - **Aba "Co-op"** no fim da barra de cima do menu, clonada da aba Perfil (`History-Button`). Do clone saem `LocalizedLabel`, `EventTrigger` e as `UIAnimation`, e o `onClick` persistente é trocado. A cada frame ela copia a visibilidade e o alpha da Perfil, e a posição é calculada à mão: o layout da barra não posiciona as abas depois da animação de entrada.
  - **Painel** com a moldura e o título do `Confirmation-Panel` e o sprite das abas nos botões: Hospedar, campo de IP + Colar + Entrar, Desconectar e uma linha de status. O IP usado fica salvo em `[Coop] LastAddress` no `.cfg`. Colar lê a área de transferência pelo user32.
  - **Faixa de avisos** num canvas próprio que sobrevive às cenas: parceiro carregando, pausado ou escolhendo upgrade, desconectou, ações bloqueadas. Ela substitui o aviso de pausa por OnGUI da fase 1, que nunca aparecia.

## Visto funcionando

- Duas instâncias com os argumentos: handshake nos dois sentidos (`0.6.0`, nome Steam, personagem do host). O personagem do cliente só chega quando o perfil carrega.
- A aba Co-op aparece na barra do menu (depois de duas correções: alpha travado em 0 e posição fora da tela).
- **Campo de IP que não digitava:** no `GameLoop.Update` (0x6EAA00), com `Global.ForceCleanEventSystemSelectedObjectKeyboard` ligado e entrada de teclado/mouse, o jogo faz `EventSystem.SetSelectedGameObject(null)` a cada frame, então o campo perdia o foco na hora. Correção: `GameLoopKeepSelectionPatch` (prefix/postfix sem argumentos) desliga o flag só durante esse `Update` enquanto o painel está aberto e restaura logo depois. Compilado, mas **não confirmado em jogo**. Os logs "Panel opened (ForceClean…=…)" e "IP field got keyboard focus" confirmam.

## Não testado

- Conectar pelo painel (Hospedar / Entrar), status e Desconectar.
- Lutar bloqueado no cliente, e no host com o cliente fora do menu.
- `RunStart`: o cliente entrar sozinho no mesmo mapa/modo, inclusive Mastery.
- Afixos do host no cliente (log `host affixes applied … curse=`).
- A barreira de carregamento.
- Voltar ao menu e jogar a segunda run conectado (fantasma, inimigos, pausa, vida e loot de novo).
- Reiniciar bloqueado e Cruzada bloqueada.
- Recusa por versão com `--coop-fake-version`.
- Se `HasGameManager` fica falso no menu (há um aviso no log se não ficar).

## Roteiro de teste

`AutoHost` está `false` no `.cfg` do usuário (desligado para este teste). Abra os dois jogos pelo exe, sem argumentos de co-op.

1. Nos dois jogos, Start game até o menu principal.
2. Jogo A: aba **Co-op** → **Hospedar**.
3. Jogo B: aba **Co-op** → o IP já vem `127.0.0.1` (digitar e Colar devem funcionar) → **Entrar**. Os dois mostram "Conectado com <nome> (<personagem>)".
4. B: Lutar na tela de mapa mostra "Aguardando o host começar a partida".
5. A: escolha um mapa com afixos e aperte Lutar. B carrega sozinho o mesmo mapa; quem chegar primeiro espera ("Aguardando X carregar...").
6. Na run: fantasma, inimigos e pausa. Pendência da 4A: F9 no host e o cliente perto por 3 s pra reviver.
7. Fim da run (desistir ou F10 no host). Reiniciar bloqueado; os dois voltam ao menu.
8. Segunda run pelo A: tudo de novo.
9. B: **Desconectar**; A mostra "<nome> desconectou".
10. B reaberto com `--coop-fake-version 0.5.0`: Entrar mostra a versão diferente nas duas telas.

Logs úteis: `[session]`, `[run]`, `[ui]`, `[sync] place=`.

## Pacotes

`Hello = 30`, `LobbyState = 31`, `RunStart = 32`, `RunStartRefused = 33`. Livres até 39.

## Fora da fase 6

Cruzada (lobby e troca de mapa no meio da run), Reiniciar sincronizado, portais Endless/BossRush e reconexão no meio da run (fase 8).
