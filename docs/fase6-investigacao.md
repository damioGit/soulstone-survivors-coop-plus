# Fase 6: UI de co-op e início de run sincronizado (investigação)

Investigação estática, só leitura (dump.cs, stringliteral.json, ApiDump no interop e Ghidra headless com `-readOnly` numa cópia do projeto). RVAs relativos a `0x180000000`. As saídas descompiladas ficaram fora do repo. Nenhum código do mod foi alterado.

---

## 1. Do menu até a run

### Classes

- **`MenuManager`** (cena `Menu-Survivors`, `Instances` estático): dono de todos os painéis do menu (`MainMenuPanel` 0x50, `FightScreenPanel` 0x60, `CharacterScreenPanel` 0x68, `MapScreenPanel` 0x98, `MailingPanel` 0xB0…). `UpdateAll` (0x66BEA0) faz o tick de tudo, inclusive `MapSelectionScreenPanel.OnUpdate`.
- **`MainMenuPanel`**: botões da esquerda (Lutar, Personagem, Forja, Talentos…). Estado em `MainMenuState` (Fight 0, Map 1, Character 2…).
- **`FightScreenPanel`**: tela "Lutar", com o personagem e as runas. O botão só liga a flag `FightPressed` (0x70) e o `MenuManager` abre o estado `Map`.
- **`MapSelectionScreenPanel`**: abas de modo (`CurrentSelectedGameMode` 0x78) e o botão Lutar final. `OnFightPressed` (0x667820) só faz `FightPressed = true`; quem age é o `OnUpdate` (0x667830), que chama o `OnFightPressed` da aba ativa e guarda o resultado em `StartingFight` (0x81, trava o duplo clique):
  - **Survivors** → `MapSelectionSurvivorsScreenPanel.OnFightPressed` (0x668C40)
  - **Mastery** → `MapSelectionMasteryScreenPanel.OnFightPressed` (0x6651E0)
  - **Crusade** → `MapSelectionCrusadeScreenPanel.OnFightPressed` (0x65E130)

### A função que começa a partida

As três fazem a mesma coisa: checam se o modo está liberado, tocam som e **trocam de cena** pelo `MainUIManager`, e depois gravam o modo e a dificuldade em estáticos de `Global`:

| Aba | Cena carregada | Estáticos gravados |
|---|---|---|
| Survivors | `LevelSelectionModule.SelectedMapItem.MapId` (o id do mapa **é** o nome da cena) | `Global.SelectedGameMode = Survivor (0)`, `Global.MasteryDifficultyTier = 0` |
| Mastery | `MasteryConfigSO.MasteryMapId` (0x30) | `SelectedGameMode = Mastery (1)`, `MasteryDifficultyTier` (= `_selectedDifficulty`), `MasteryDynamicLevel` (= `_selectedDynamicLevel`) |
| Crusade | `CrusadeConfigSO.MapId` (0x60), a cena de lobby `SurvivorsCrusadesLobby` | `SelectedGameMode = Crusade (2)`, `CrusadeDifficultyTier` (byte), `CrusadeDynamicLevel` |

- **`MainUIManager.ChangeScene(sceneName, delay, mode)`** (0x65BF40) só inicia a corrotina `DoChangeScene` (MoveNext 0x65FC60): limpa a seleção do `EventSystem` e os tooltips, espera `delay`, mostra a tela de loading (`LoadingScreenManager.PushAndWaitUntilVisible`), faz `SceneManager.LoadSceneAsync(sceneName)` (modo Single), limpa o XP do chão, tira a tela de loading e volta todos os fatores de `TimeUtil` para 1. O Crusade cria a mesma corrotina inline, sem passar por `ChangeScene`.
- `MainUIManager` é um singleton (`Instance`) que existe no menu e na partida (o `EndGamePanelManager` também chama `ChangeScene`).
- Na cena carregada, o `SurvivorsGameManager.Start` (MoveNext 0x442A80) monta tudo sozinho: `SetupGameData`, `GetAndRemoveInitialData` (nulo numa run nova; só tem valor nas trocas de fase do Endless), o mapa por `SceneManager.GetActiveScene().name` → `Global.MapCollection.GetMapById`, `EnemyPrefabsUtil.Setup`, os afixos (abaixo), os componentes de spawn e por fim `GameManagerUtil.StartMatch(mapId)` e `SetupPlayerForMatch`.

**Dá para chamar por código no cliente, pulando os menus?** Sim, e é o que o próprio jogo faz: gravar os estáticos de `Global` e chamar `MainUIManager.Instance.ChangeScene(cena, 0, 0)`. Nada do que os `OnFightPressed` fazem além disso é necessário (som, vibração do controle e a trava `StartingFight`). O personagem, a arma, a skin, as runas e a árvore de talentos saem do **perfil local** na hora do `SetupPlayerForMatch`, então o cliente entra com o personagem dele.

Cuidados:
- Mapa de DLC que o cliente não tem: conferir `Global.MapCollection.GetMapById(cena)` e `Application.CanStreamedLevelBeLoaded(cena)` antes, e recusar com aviso.
- Só chamar no menu (`MenuManager.Instances.Count > 0` e cena ativa `Menu-Survivors`). No fim de run o jogo faz `GameManagerUtil.ResetProgress(Global.SelectedGameMode)` antes de voltar (`EndGamePanelManager.OnBackToMenuPressed` 0x5F4D50 → `ChangeScene("Menu-Survivors")`); pular isso deixaria progresso de Endless/Mastery pendurado.

### O que define a run

| Parâmetro | Onde está | Igual nos dois? |
|---|---|---|
| Mapa / cena | argumento de `ChangeScene` | **Precisa** (é o que hoje cada um escolhe na mão) |
| Modo | `Global.SelectedGameMode` | **Precisa** |
| Dificuldade Mastery | `Global.MasteryDifficultyTier`, `MasteryDynamicLevel` | **Precisa** (os afixos do Mastery saem disso, via `MasteryConfigSO.FillListWithSelectedAffixes`) |
| Dificuldade Crusade | `Global.CrusadeDifficultyTier`, `CrusadeDynamicLevel` | Precisa, mas ver Crusade abaixo |
| **Afixos (maldições/modificadores) no Survivors** | **perfil local**: `PlayerProfile.MapProgression[mapa].SelectedAffixes` (0x28), ignorado se `AffixesDisabled` (0x24). Vira `new AffixGameplayData(lista, MapDefinition+0x38)` no `Start` | **Não são iguais hoje**: cada máquina usa a seleção salva no próprio perfil. Precisa vir do host |
| Personagem, arma, skin, runas, talentos | perfil local | Não: cada um com o seu |
| Seed | não existe | `UnityEngine.Random.InitState` só é chamado em `WeaponCollectionSO.GetWeaponUpgradeCost`. Não há seed de run |

**Sorteios locais na entrada da run:** o mapa de loot quando `MapDefinition+0x25` está ligado (`MapCollectionSO.GetAvailableLootFromRandomMap`) e uma rolagem `RandomUtils.value` contra `Global.GlobalProperties+0x18C` que escolhe uma variante do mapa. O resto do que é aleatório (spawn, drops, baús, minérios) já é só do host desde as fases 1–4B. Se aparecer diferença visual por essa variante, dá para mandar o resultado do host depois.

**Afixos no cliente:** o host é dono dos inimigos, então os stats extras dos inimigos não importam no cliente. Mas os afixos também ligam coisas locais: redução de cura do jogador (`AffixHealingDampening`), meteoros na morte de inimigo, pilares, bônus de recompensa (`AffixBonusData`, usado no `ProcessLoot` e no prestígio) e o `CurseLevel` que aparece na UI. Plano: o host manda a lista dele no `RunStart` e o cliente troca o argumento `selectedAffixes` num prefix de `AffixGameplayData..ctor(List<string>, int)` (0x3FC7B0; argumentos lista e int, sem struct pequena por valor), só enquanto a run de co-op estiver começando. Assim o perfil salvo do cliente não é tocado.

### Crusade, Endless e Reiniciar

- **Crusade** começa num lobby (`SurvivorsCrusadesLobby`) e escolhe cada mapa lá dentro (`CrusadeLobbyGameManager.<ShowMapSelectionUI>` → `GameManagerUtil.LoadNextScene`). É a mesma "troca de cena no meio da run" dos portais Endless/BossRush da 4A. **Fora da fase 6:** no co-op, o botão Lutar da aba Crusade fica bloqueado com aviso.
- **Reiniciar** na tela final (`EndGamePanelManager.OnRestartPressed` 0x5F5070) faz `LoadNextScene(MatchStats.MapId)` local. Em co-op: bloquear e voltar pelo menu. Dá para virar "o host reinicia e manda `RunStart`" depois.

---

## 2. UI

### O que existe

- O IMGUI do mod não serve (a 4A viu `Method unstripping failed` nas chamadas `GUI.*` feitas pelo interop). O menu do jogo é **uGUI + TextMeshPro**, com `EventSystem` do Input System. O interop tem `UnityEngine.UI.dll`, `Unity.TextMeshPro.dll` e `UnityEngine.UIModule.dll` (Canvas); hoje o csproj só referencia `UnityEngine.UI`.
- Campo de texto pronto no menu: **`MenuManager.MailingPanel.EmailInput`** (`MailingFormPanel`+0x38, um `TMP_InputField` com viewport, texto e placeholder já montados). O jogo digita nele, então o caminho nativo de teclado do `TMP_InputField` funciona neste build (o processamento de tecla roda no IL2CPP, não passa pelo unstripping).
- Fontes: qualquer `TextMeshProUGUI` do menu tem um `TMP_FontAsset` válido para copiar.

### Como montar

1. Um `GameObject` nosso com `DontDestroyOnLoad`, `Canvas` (`ScreenSpaceOverlay`, `sortingOrder` alto), `CanvasScaler` (referência 1920×1080) e `GraphicRaycaster`. Por ficar fora das cenas, ele também serve de "aviso na tela" durante a run (substituto do OnGUI quebrado da fase 1).
2. Botões feitos do zero: `Image` (fundo) + `Button` + filho `TextMeshProUGUI` com a fonte copiada. Não clonar botões do jogo: eles carregam listeners persistentes (`onClick` chamando `MainMenuPanel.OnQuitPressed` etc.), `LocalizedLabel` que reescreve o texto e navegação de controle.
3. Campo de IP: `Object.Instantiate(EmailInput.gameObject, painel)`. No clone, destruir os `LocalizedLabel`, trocar `onEndEdit`/`onSubmit`/`onValueChanged`/`onSelect`/`onDeselect` por eventos novos (`RemoveAllListeners` não remove os persistentes), `contentType = Standard`, `characterLimit = 64` e escrever o placeholder. Plano B, se o clone der problema: montar o `TMP_InputField` do zero (viewport com `RectMask2D`, `textComponent`, `placeholder`, ligar o componente por último).
4. Clique: `button.onClick.AddListener((UnityAction)OnHostClicked)`. O Il2CppInterop gera a conversão implícita de `System.Action` para `UnityAction` (o `DelegateSupport` cria o delegate IL2CPP). Mesmo jeito para `TMP_InputField.SubmitEvent` (`UnityAction<string>`).
5. Riscos: a navegação de teclado/controle do menu (`MenuManager` seleciona o botão padrão quando o `EventSystem` fica sem seleção) pode roubar o foco do campo, e Esc fecha painéis do jogo. O IP só tem dígitos e ponto, que o menu não usa. Validar no teste.

### Onde aparece

- Botão "Co-op" no canto superior direito, só na cena `Menu-Survivors`.
- Painel: Hospedar / Entrar (campo de IP, porta 7777 fixa) / Desconectar, e uma linha de status: "Aguardando parceiro…", "Conectado com <nome> (<personagem>)", "Versão diferente: …", "Aguardando o host começar".
- Durante a run, só a faixa de aviso (carregando parceiro, parceiro desconectou).

---

## 3. Personagem e skin

- `Global.PlayerProfile` (getter 0x450780):
  - `SelectedCharacter` (0x20): `CharacterIdString` (campo string 0x18) e `CharacterIdAsEnum` (`CharactersType`, enum de **1 byte**: Barbarian 1 … Blacksmith 23).
  - `SelectedWeaponPerCharacter` (0x28): `GetWeaponIdByCharacterId(CharactersType)` → id da arma.
  - `SelectedSkinPerCharacter` (0x30): `GetCharacterSkinIdByCharacterId(CharactersType)` → id da skin, `GetWeaponSkinIdByCharacterId` → skin da arma, `GetSkinDataByCharacterId` → `SkinData`.
- Chamar esses métodos é seguro (são chamadas, não patches). O getter `CharacterIdAsEnum` lê um `Nullable` internamente, mas é código nativo; se der NRE pelo interop, ler o campo string 0x18 e converter.
- É o que o handshake manda: `characterId` (byte), `weaponId`, `skinId`, `weaponSkinId`. A fase 5 usa isso para montar o fantasma (`CharacterModelHolder.SpawnCharacter`/`PreloadCharacter(CharacterSkinData)` são os caminhos do jogo).
- O personagem pode mudar no menu depois de conectar: mandar de novo quando mudar (checar a cada ~1 s no menu) e, de novo, no `RunStart`.

### Nome do jogador

`Steamworks.SteamFriends.GetPersonaName` foi stripado do interop (sobraram dois métodos). Mas a API flat está no `steam_api64.dll` do jogo: `SteamAPI_SteamFriends_v017()` e `SteamAPI_ISteamFriends_GetPersonaName(ptr)` (UTF-8). P/Invoke direto, só se `SteamManager.Initialized`, com try/catch. Sem Steam: "Host"/"Cliente". No mesmo PC as duas instâncias têm o mesmo nome, então o status mostra o papel junto.

---

## 4. Rede

### Handshake e versão

- Hoje o host faz `AcceptIfKey("soulstonecoop-v1")`. Novo: o cliente conecta com um `NetDataWriter` com chave, versão do protocolo (u16) e versão do mod (string). O host lê no `ConnectionRequestEvent` e, se a versão for diferente (ou não vier, cliente antigo), faz `request.Reject(motivo)`. O cliente recebe `PeerDisconnected` com `DisconnectReason.ConnectionRejected` e o motivo em `AdditionalData`: mostra "Versão diferente: host X, você Y" e para de tentar (o retry atual só vale para `ConnectionFailed`). O host mostra "Recusei um cliente com a versão Y".
- Depois de conectar, cada lado manda `Hello` (nome, personagem, arma, skin) e de novo quando mudar. Fica num estado de sessão (`CoopSession`) que as outras fases leem: papel, nome e personagem de cada lado, se o parceiro está no menu ou na run.

### Pacotes novos (a 4A usa até o 20, a 4D fica com 21–29; a fase 6 usa 30–39)

| # | Nome | Sentido | Conteúdo |
|---|---|---|---|
| 30 | `Hello` | os dois | nome, `characterId` u8, `weaponId`, `skinId`, `weaponSkinId` |
| 31 | `LobbyState` | os dois | onde estou: menu / carregando / na run |
| 32 | `RunStart` | host → cliente | cena, modo, `MasteryDifficultyTier`, `MasteryDynamicLevel`, afixos desligados u8, lista de afixos |
| 33 | `RunStartRefused` | cliente → host | motivo (mapa ausente/DLC, não está no menu) |

### Início sincronizado

1. O host aperta Lutar normalmente. Um postfix nos três `OnFightPressed` das abas (sem argumentos, devolvem bool): se `__result` e houver parceiro, manda `RunStart` com os valores que acabaram de ser gravados em `Global`, a cena e os afixos (`PlayerProfile.MapProgression[cena]`). No Crusade, um prefix bloqueia com aviso.
2. Bloqueio do host: se o cliente não estiver no menu (`LobbyState`), o prefix devolve `false` e mostra "Parceiro ainda não voltou ao menu".
3. No cliente com host conectado, o prefix dos três `OnFightPressed` devolve `false` e mostra "Aguardando o host começar".
4. O cliente recebe `RunStart`: confere a cena e chama `Global.SelectedGameMode = …`, os campos de dificuldade e `MainUIManager.Instance.ChangeScene(cena, 0, 0)`. Os afixos do host ficam guardados para o prefix do `AffixGameplayData..ctor`.
5. **Barreira de carregamento:** cada um carrega no seu ritmo. Os inimigos do host que nascem antes de o cliente chegar já vão para a fila `Pending` das marionetes, mas o host joga sozinho uns segundos. Plano: um `PauseReason.Loading` no `PauseSync`, que cada lado declara do `RunStart` até o próprio `StartMatch`. Assim o primeiro a chegar fica congelado até o outro entrar (com limite de ~45 s).

### Fim de run e volta ao menu

O fim já é sincronizado pela 4A (`RunEnd`). Depois cada um aperta "Voltar ao menu" na própria tela. A conexão continua, porque o `CoopBehaviour` é `DontDestroyOnLoad`. O host só começa outra run quando os dois estiverem no menu. O que cada módulo precisa resetar está na seção 5.

---

## 5. Estado das fases anteriores entre runs

Hoje a conexão nasce **dentro** da run (`GameplayUIManager.Setup` → `CoopBehaviour.EnsureCreated`), e vários módulos tratam o `PeerConnected` como "o parceiro entrou na partida". Com a conexão feita no menu e mantida entre runs, isso quebra assim:

| Problema | Onde | Conserto |
|---|---|---|
| **O fantasma só nasce no `PeerConnected`**, clonado do jogador local. No menu não há jogador, então ele não nasce, e na run 2 ninguém o recria. Proxy do host, lançador remoto (4C), dono do XP (4B), redirecionamento de spawn, revive por proximidade e câmera de espectador dependem dele (`RemoteProxy.TryGetRemotePosition`). | `CoopBehaviour.cs:66-82` | Criar o fantasma no início de cada run local (fim do `StartMatch`/`SetupPlayerForMatch`) se houver parceiro, e destruí-lo ao sair da run. Zerar `_ghostRebuilds` por run. |
| **O reenvio de todos os inimigos só roda no `PeerConnected`** do host. | `HostEnemySync.cs:165-201` | Rodar o mesmo reenvio quando o cliente avisar que a run dele começou (`LobbyState = InRun`). |
| Spawns do host que chegam com o cliente ainda na tela final da run anterior viram marionetes da cena velha e somem com ela. | `ClientPuppets` (`_matchStarted` ainda ligado) | Resolvido pela regra "o host só começa quando o cliente está no menu", mais o reenvio do item acima. Desligar `_matchStarted` das marionetes ao receber `RunStart`. |
| `PauseSync`: o último estado (pausa da tela de derrota) fica preso no parceiro e pode congelar a próxima run. `RemotePaused` não olha se há partida. | `PauseSync.cs:23-62` | Mandar `None` ao sair da run, zerar `RemoteReason` no `RunStart`, e só aplicar a pausa remota com os dois em partida (`LobbyState`). |
| `PlayerSync`: `ReceivedCount` e o buffer de posições nunca zeram, o que enfraquece a checagem do proxy. | `PlayerSync.cs:44-61` | Zerar no início de cada run. |
| `CoopLife.OnRunEnd`/`OnRunEndRequest` não checam se há partida: um `RunEnd` atrasado no menu chamaria `Surrender()`/vitória sobre um `GameData` velho. | `CoopLife.cs:593-613` | Ignorar fora da partida. |
| `NetworkManager`: `PeerDisconnected` não zera `Role` (o `IsClientSide` continua true e a próxima run solo fica sem inimigos); `Stop()` não dispara a limpeza dos módulos. | `NetworkManager.cs:44-96` | Desconectar = `Stop()` + evento de limpeza + `Role = None`. `IsClientSide` passa a exigir conexão (o argumento `--coop-connect` só vale enquanto tenta conectar). |
| `EnsureCreated` também liga os ganchos e o `PollEvents`; hoje só roda na primeira run. | `CoopBehaviour.cs:23-63` | Criar o `CoopBehaviour` no menu (postfix de `MenuManager.Awake`), separado de conectar. O auto-connect dos argumentos e do `AutoHost` passa a acontecer no menu. |
| Pacotes da run anterior chegando atrasados (`LootDrop`, `XpShare`, `EnemySpawn`). | `LootSync`, `ClientPuppets` | Coberto pela ordem: o cliente só sai do menu depois do `RunStart`, e o canal é ReliableOrdered. Um id de run nos pacotes fica como melhoria se aparecer no teste. |

O resto já se limpa sozinho: `RemoteProxy`, `RemoteCaster`, `ClientPuppets`, `CoopLife` e `LootSync` resetam no `StartMatch` ou no tick quando `GameManagerUtil.HasGameManager` fica falso. **Conferir no jogo:** se `HasGameManager` é mesmo falso na cena `Menu-Survivors` (`UnsetGameManager` no `OnDestroy` dos game managers). Os patches que podem rodar no menu (`Entity.Setup`, `Entity.Recycle`, `TimeUtil.SetTime`, `PlayerCharacter.UpdateAll` da prévia do personagem, `EntityRegistry.DefaultContinuousRecycler`) já têm guarda de partida ou de papel. Só o `CoopLifeInputBlockPatch` depende de `LocalDowned`, que é zerado no `Reset`.

---

## 6. Plano de implementação (ordem)

1. **Sessão e rede:** `CoopSession` (papel, nome e personagem dos dois, `LobbyState` do parceiro), handshake com versão no `ConnectionRequest` e recusa com motivo, `Hello`/`LobbyState`, desconectar limpo (`Role = None` + evento). `CoopBehaviour` criado no menu. Os argumentos `--coop-host`/`--coop-connect` e o `AutoHost` continuam, agora conectando já no menu.
2. **Resets entre runs** (seção 5): fantasma por run, reenvio de inimigos quando o cliente entra, `PauseSync`, `PlayerSync`, `CoopLife` fora da partida.
3. **RunStart:** postfix/prefix nos três `OnFightPressed`, bloqueio do cliente e do Crusade, `ChangeScene` no cliente, afixos do host via prefix do `AffixGameplayData..ctor`, barreira `PauseReason.Loading`. Bloquear Reiniciar e os portais Endless/BossRush (estes a 4A já bloqueia).
4. **UI** (`src/SoulstoneCoop/Ui/`): canvas próprio, botão Co-op, painel com Hospedar/Entrar/Desconectar, campo de IP clonado do formulário de e-mail, status, e a faixa de aviso durante a run.
5. Teste: sem argumentos, hospedar, entrar por 127.0.0.1, run, menu, run de novo; versões diferentes (`PluginVersion` trocada só num dos dois).

Pacotes 30–39 (a 4D usa 21–29). Fora da fase 6: Crusade, Reiniciar sincronizado, portais Endless/BossRush, reconexão no meio da run (fase 8).
