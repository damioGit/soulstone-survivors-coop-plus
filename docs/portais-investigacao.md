# Portais Endless/BossRush e Reiniciar em co-op (investigação)

Investigação estática, só leitura: `dump.cs`, ApiDump no interop e Ghidra headless (`-readOnly`, numa cópia do projeto). RVAs relativos a `0x180000000`. As saídas descompiladas ficaram fora do repo. Base: `docs/fase4a-investigacao.md` (portais, `RunEnd`) e `docs/fase6-investigacao.md` (`RunStart`, `ChangeScene`, afixos, barreira).

Notação: `Classe.Metodo (RVA)`, `Classe.Campo (offset)`.

---

## 1. O que cada portal faz

### Quais portais aparecem

`GameManagerUtil.OpenPortalAndObjective` (MoveNext 0x705120) roda no ramo de vitória. Ele cria `GameData.EnterPortalObjective` (`SimpleObjective`, `Completed` 0x20) e liga os objetos:

| Portal | Objeto | Quando aparece |
|---|---|---|
| **Home (azul)** | `HomePortalObject` | sempre |
| **Endless (infinito)** | `EndlessPortalObject` | só no modo **Survivors**; na 1ª fase exige `GameStats.MatchTime ≥ EndlessPropertiesSO.MinTimeForEndlessRush` (0x20); numa fase seguinte, só se a corrente já é Endless (`EndlessType` 0 ou None); e o passo 1 do `PlayerProfileProgressCheckpoint` do **perfil local** |
| **BossRush (desafio)** | `BossRushPortalObject` | só no **Survivors**; 1ª fase com `MatchTime ≥ BossRushPropertiesSO.MinTimeForBossRush` (0x20); depois, só numa corrente BossRush; passo 2 do checkpoint local |

Consequências:
- No **Mastery** só existe o portal Home. A **Cruzada** tem fluxo próprio (seção 4).
- O portal roxo/vermelho depende do perfil e do tempo de partida **de cada máquina**. Um pode ver o portal e o outro não. Isso não impede de seguir o parceiro: a entrada por código (`SurvivorsGameManager.OnPlayerEnter*Portal`) não olha se o objeto está ativo.
- O **F10** da 4A força a vitória cedo, então na 1ª fase o tempo mínimo provavelmente não foi atingido e os portais Endless/BossRush **não aparecem**. Para testar é preciso um atalho que entre no portal por código.

### Entrada

Os três seguem o mesmo começo. O trigger do portal (`HomePortal.OnTriggerStay`, com o jogador **registrado** dentro por `TimeToEnter`) dispara uma `UnityEvent` → `SurvivorsGameManager.OnPlayerEnterPortal` (0x438CF0), `OnPlayerEnterEndlessPortal` (0x438CC0) ou `OnPlayerEnterBossRushPortal` (0x438C80). Os três: `if (GameManagerUtil.ProcessOnPlayerEnterPortal()) GameManagerUtil.OnEnter*Portal(idle, close)`.

- **`ProcessOnPlayerEnterPortal`** (0x6EE750): se `EnterPortalObjective.Completed` já está ligado, devolve false (só entra uma vez por fase). Senão liga `Completed`, `SetMovementMode(None)` no `PlayerEntities[0]`, esconde o modelo e desliga partículas, recicla buffs e devolve true. **Estoura NRE se `EnterPortalObjective` for nulo** (portal ainda não aberto naquela máquina).
- **Home** → `OnEnterHomePortal` (0x6EE160): VFX, `SaveFinalGameStats` se preciso, tela final com atraso 2 s, `SaveToProfile`. É fim de run (já sincronizado pela 4A).
- **Endless** → `OnEnterEndlessPortal` (0x6EDF20):
  1. VFX;
  2. `endless = GameManagerUtil.SetEndlessData(Endless, GameData.PowerUpData, PlayerEntities[0])` (0x6F3610);
  3. `map = Global.MapCollection.GetRandomMapExcluding(endless.MapsToExclude)` (0x500E20). **Este é o único sorteio**;
  4. `GameManagerUtil.LoadNextScene(map.MapId, 2f, false)`;
  5. copia o inventário para o painel de moedas.
- **BossRush** → `OnEnterBossRushPortal` (0x6EDD00): igual, com `SetEndlessData(BossRush, …)`. O mapa é **fixo**: `Global.BossRushProperties.BossRushMapId` (0x30). Nada é sorteado.

### A cena do BossRush tem outro game manager

O `BossRushMapId` carrega uma cena com **`SurvivorsBossRoomGameManager`**, não `SurvivorsGameManager`. Lá `GameData.SurvivorsGameManager` é nulo e não existe `MainObjective`. O que muda:
- **Portais:** os três portais são métodos dele (`OnPlayerEnterPortal` 0x437120, `OnPlayerEnterEndlessPortal` 0x4370E0, `OnPlayerEnterBossRushPortal` 0x4370A0), com o mesmo corpo (`ProcessOnPlayerEnterPortal` + `OnEnter*Portal`).
- **Bosses:** `UpdateAll` (0x437930) chama `SpawnNextBoss` (0x437150) por timer (`_delayUntilFirstBoss`, depois `DelayBetweenBosses` a cada boss morto). Ele carrega de `Resources`, faz `Entity.Setup` e cria um `EliminateSpecificEntitiesObjective` por boss. Não passa por `BossSpawnProcessor` nem respeita `StopSpawning`. **No cliente isso gera bosses próprios.**
- **Vitória:** `GameManagerUtil.GetMapProgress()` (0x6ED540) = `(Progression.CompletedObjectiveProgression + CurrentObjectiveProgression) / TotalObjectiveProgression` (`GameData.Progression` 0x1C8) chega a 1 → `StopSpawning`, `Result = Victory`, apocalipse, coleta e `OpenPortalAndObjective(6 s)`. O `Completed` só cresce quando um objetivo de boss termina.

O mod não cobria nada disso: o portal Home e a vitória da 4A olham só o `SurvivorsGameManager`. Sem isso o portal de desafio levaria a uma fase quebrada.
- **Plano:** bloquear `SpawnNextBoss` no cliente; espelhar Home e Endless/BossRush também nessa cena; a vitória do cliente faz `Completed = Total`.
- O `GoblinCaveGameManager` é da Cruzada (fora).

### O que passa de uma cena para a outra

`SetEndlessData(tipo, powerUps, jogador)`:
- chama `SaveToProfile` (grava o progresso da partida no perfil local);
- reaproveita o `EndlessData` da corrente (`GameData`+0x150) ou cria um novo. No novo: modo, personagem, arma, runas do jogador, **afixos copiados do `AffixGameplayData` atual** (`SelectedAffixes`) e `ExtraMapDifficultyRating` do mapa atual;
- `EndlessLevel + 1`, `MatchTime +=`, a cena atual entra em `MapsToExclude`, contadores de kills e dano somados;
- `SetDataBasedOnPlayerEntity` (0x4FF600): poderes coletados, nível, XP e XP necessário (`ExperienceComponent.GetLevelExperienceAndRequirement`), inventário (`InventoryComponent.SaveMatchInventoryData`), prestígio inicial;
- guarda tudo no estático **`GameManagerUtil.InitialEndlessData`** (0x38) e em `GameData`+0x150.

`LoadNextScene(mapId, delay, clearLoad)` (0x6EDA70): liga `GameManagerLoadingScene`, para a corrotina pendente (com `clearLoad`, apaga também `InitialEndlessData`), para as vozes e inicia `LoadNextStageAfterDelay` (MoveNext 0x704F30). Essa corrotina espera `delay` com **`WaitForSeconds` (tempo escalado: com `timeScale` 0 ela não anda)** e chama `MainUIManager.ChangeScene(mapId, 0, 0)`, o mesmo caminho da fase 6.

Na cena nova, `SurvivorsGameManager.Start` (MoveNext 0x442A80) roda `SetupGameData` **logo no primeiro passo**. Depois:
- `GetAndRemoveInitialData()` devolve o `EndlessData`;
- `EnemyPrefabsUtil.Setup(cena, isEndless)`;
- **os afixos vêm de `EndlessData.SelectedAffixes`, não do perfil** (o perfil só é lido numa run nova). A escala por nível fica em `AffixGameplayData`+0x54 = `(EndlessLevel − 1) × +0x50`;
- `SetupPlayerForMatch(…, endlessData, …)` devolve nível, XP, poderes e inventário ao jogador;
- `StartMatch`.

**Conclusão:** a progressão de cada jogador já passa de uma fase para a outra pelo próprio jogo, em cada máquina. O cliente entrou na 1ª fase com os afixos do host (fase 6), e esses afixos seguem na corrente. Nível da corrente e dificuldade do mapa também são iguais nos dois, porque os dois partem do mesmo mapa e do mesmo `EndlessLevel`.

---

## 2. O que precisa ser igual nas duas máquinas

| Item | Situação |
|---|---|
| **Mapa da próxima fase (Endless)** | **Sorteado em cada máquina** (`RandomUtils.Range` em `GetRandomMapExcluding`). Não há seed: `UnityEngine.Random.InitState` só aparece em `WeaponCollectionSO.GetWeaponUpgradeCost` (fase 6). **O host precisa mandar o resultado.** |
| Mapa BossRush | fixo (`BossRushMapId`): igual nos dois |
| Modo, tier/nível do Mastery | `Global`, inalterados no meio da run: iguais |
| Afixos | seguem no `EndlessData` (iguais desde a fase 6) |
| `EndlessLevel`, `ExtraMapDifficultyRating`, `MapsToExclude` | calculados localmente com as mesmas entradas: iguais (os dois vão para o mesmo mapa) |
| Variante do mapa e mapa de loot aleatório | sorteios locais na entrada (fase 6, mesmo caso da run nova): ignorados |
| Nível, skills, itens, XP | de cada jogador, locais, como no solo |

**Mapa que o cliente não tem** (DLC): `GetRandomMapExcluding` filtra `Unavailable` e `IgnoreInRandomMapSelection` com dados de cada máquina. O cliente confere `MapCollection.GetMapById` e `Application.CanStreamedLevelBeLoaded` antes de seguir.

**Como forçar o mapa no cliente sem patch de retorno:** depois que o fluxo nativo do portal rodou no cliente (e sorteou um mapa qualquer), chamar de novo `GameManagerUtil.LoadNextScene(mapaDoHost, 2f, false)`. A função cancela a corrotina pendente e agenda a do mapa do host. Com `clearLoad = false` o `InitialEndlessData` fica. No host, um postfix só de leitura em `GetRandomMapExcluding` (instância + `List<string>`, sem struct por valor) captura o `MapId` sorteado.

---

## 3. O que do mod precisa resetar na troca de cena no meio da run

### O problema: talvez não exista nenhum frame "sem game manager"

A fase 6 detecta o fim da run quando `GameManagerUtil.HasGameManager` fica falso, ou pela cena do menu, olhando no tick. Os módulos fazem o mesmo: `ClientPuppets`, `RemoteProxy`, `RemoteCaster`, `CoopLife`, `EnemyCasts`, `LootSync`.

Numa troca de fase:
- a cena velha é destruída na ativação do `LoadSceneAsync` (`SurvivorsGameManager.OnDestroy` → `GameManagerUtil.UnsetGameManager` 0x6F8510: `GameData = null`, `EntityRegistry.ClearAll`);
- o `Start` do game manager novo, que roda `SetupGameData` no primeiro passo, é chamado antes de qualquer `Update` do frame seguinte.

Então o `Update` do mod pode **nunca** ver `HasGameManager == false`. Se isso acontecer:
- `CoopSession.LocalInRun` não cai e o `LobbyState` nunca passa por Loading, então não há barreira;
- `GameState.LocalPlayerEntity` aponta para o jogador destruído;
- o `_matchStarted` das marionetes continua ligado, e spawns da cena nova seriam criados antes do `StartMatch`;
- os caches do `EnemyCasts` ficam com ponteiros velhos.

**Conserto:** postfix em `GameManagerUtil.UnsetGameManager` (estático, sem argumentos), que faz no momento exato o que o tick faria. Serve também para a volta ao menu.

### Estados por módulo

| Estado | O que já acontece | O que falta |
|---|---|---|
| Fantasma | some quando um lado sai de InRun, nasce com os dois InRun | Sair de InRun na hora do portal (estado novo `Transition`) e não recriar enquanto um lado está em transição |
| Proxy do cliente (host), lançador remoto | `OnMatchStarted` zera; sem fantasma não recriam | nada |
| Marionetes e netIds | netIds do host só crescem (sem colisão). `ResetForNewRun` só no `RunStart` do menu | **Spawns velhos:** tudo que o host mandou da cena velha chega antes da marca "host saiu da cena" (canal ordenado). Quando o `LobbyState` do host vira Loading, o cliente descarta marionetes e pendentes. `_matchStarted` desliga no `UnsetGameManager` local |
| Loot pendente do cliente | descartado no tick sem game manager | descartar na mesma marca do host |
| `EnemyCasts` | limpa no tick sem game manager | limpar no `UnsetGameManager` |
| Objetivos espelhados, vitória, caído | `CoopLife.OnMatchStarted` → `Reset` | durante os 2 s antes da troca: reviver quem está caído (como no portal Home) e ligar `RunEnding` (ninguém cai nem perde) |
| Pausa | `PauseSync` só aplica com os dois InRun | `ResetForRun` na transição (como no `RunStart`) |
| XP de equipe a caminho (`_shareIn`) | sobrevive se não houver frame sem game manager; senão é descartado | aceitável. O XP já entrou no `EndlessData` na hora do portal |
| Barreira (`HoldForPartner`) | trava quem chega ao `StartMatch` com o parceiro em Loading | contar também `Transition`; **nunca** travar a cena velha durante a espera de 2 s (com `timeScale` 0 o `WaitForSeconds` não anda) |
| Reenvio de inimigos (host) | quando o cliente vira InRun | não reenviar se o host ainda está na cena velha |
| Afixos do cliente (fase 6) | `_clientRun` + prefix do construtor | Endless não precisa (vêm do `EndlessData`). **Reiniciar precisa**: é run nova lida do perfil, então manter `_clientRun` através do `UnsetGameManager` |

---

## 4. Reiniciar e Cruzada

### Reiniciar

`EndGamePanelManager.OnRestartPressed` (0x5F5070):
- trava com `RequestedGoToMenu` (0x300) e limpa a seleção da UI;
- na Cruzada: `ResetProgress(Crusade)` + `LoadNextScene(lobby, 0, true)`;
- senão: `LoadNextScene(MatchStats.MapId, 0, true)` (`clearLoad` apaga o `InitialEndlessData`: run nova do zero, afixos do perfil).

**Dá para cobrir com o mesmo mecanismo:**
- o host reinicia normalmente e manda os parâmetros da run (os mesmos do `RunStart`);
- se o cliente já voltou ao menu, vale o `RunStart` da fase 6;
- se o cliente ainda está na tela final, ele grava os mesmos estáticos de `Global`, prepara a troca dos afixos e chama `LoadNextScene(mapa, 0, true)`, como o próprio botão faz;
- o Reiniciar do cliente continua bloqueado (o host decide).

### Cruzada

**Não é barata.** A Cruzada começa num lobby (`SurvivorsCrusadesLobby`, `CrusadeLobbyGameManager`). Cada mapa é escolhido pelo jogador numa tela (`CrusadeGameManager.ShowMapSelectionUI` → `CrusadeMapSelectionPanel.Open(crusadeData, infinityReroll)`), com opções sorteadas, rerrolagem, sangue de titã, portal primordial, caverna goblin e bosses de cruzada (`SpawnCrusadeBoss`). Sincronizar exige:
- espelhar as opções sorteadas;
- deixar só o host escolher;
- sincronizar o lobby e as runas;
- cobrir os bosses e o Void King da Cruzada, que a 4D não cobre.

Também tem o seu próprio `RunStart`, porque o Lutar da aba Cruzada está bloqueado em co-op desde a fase 6. Fica como fase própria. A troca de mapa da Cruzada continua fora do co-op.

---

## 5. Plano

Pacotes 90–99.

| # | Nome | Sentido | Conteúdo |
|---|---|---|---|
| 90 | `PortalTransition` | host → cliente | tipo (Endless, BossRush, Restart), mapa; no Restart também modo, Mastery e afixos |
| 91 | `PortalRequest` | cliente → host | tipo (Endless, BossRush): o cliente entrou num portal |

1. **Host entra** (trigger real, atalho ou pedido do cliente):
   - prefix: anota `Completed` e liga a captura;
   - o fluxo nativo sorteia e chama `LoadNextScene`;
   - postfix: se o objetivo virou `Completed`, manda `PortalTransition(tipo, mapa)` e marca a transição local.
2. **Cliente entra:** o prefix bloqueia e manda `PortalRequest`. O host entra por código (passo 1).
3. **Cliente recebe `PortalTransition`:**
   - revive se caído e liga `RunEnding`;
   - se `EnterPortalObjective` ainda não existe, tenta de novo por até 30 s;
   - confere se tem o mapa;
   - chama o mesmo `OnPlayerEnter*Portal` com flag de rede (o jogo guarda a progressão local) e depois `LoadNextScene(mapaDoHost, 2f, false)`.
   - Sem o mapa: aviso, recusa com `RunStartRefused` e portal Home local.
4. **Transição local:** novo `LobbyPlace.Transition` (portal tomado, cena velha ainda viva). Depois Loading (no `UnsetGameManager`) e InRun (`StartMatch`). A barreira trava quem chega primeiro.
5. **Reiniciar:** como na seção 4.
6. Tirar os bloqueios da 4A (Endless/BossRush) e da fase 6 (Reiniciar). A Cruzada continua bloqueada.
7. **Atalho de teste (host):** F6 entra no portal Endless e Shift+F6 no BossRush, por código, depois que o `EnterPortalObjective` existe.
