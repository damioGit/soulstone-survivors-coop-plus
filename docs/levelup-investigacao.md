# Level-up sem pausa: investigação

Pedido do usuário depois de jogar a 0.7.0 com um amigo: com a build completa, não pausar a sessão enquanto alguém escolhe upgrade.

Notação: `Classe.Metodo (RVA)`, `Classe.Campo (offset)`. Tudo lido no dump e no Ghidra (cópia própria do projeto, `-readOnly`).

## 1. Como o level-up funciona

- `ExperienceComponent.UpdateAll` (0x6CE760): a cada frame, se `GameManagerUtil` (estático +8, "nível máximo do endless") estiver desligado, passa 10% de `_experienceToAdd` (+0x40) para `_experience` (+0x48) e chama `LevelUp` (0x6CE090) quando `_experience ≥ _requiredExperience` (+0x4C).
- `LevelUp`: `_level++` (+0x44), desconta o XP, no endless trava no nível máximo de `Global` (+0x38/+0x40), multiplica o requisito, dá ponto de skill da runa a cada N níveis e **enfileira `PowerUpRequest(level, None, canBeLocked, canBeBanished)` em `GameplayUIManager.LevelUpPanel.PowerUpRequests` (+0x150)**. Depois, cura de level-up e som. O `LevelUp` não abre tela nenhuma. `LevelUpMultipleTimes(n)` (0x6CE040) chama `LevelUp` n vezes.
- `GameplayUIManager.UpdateAll` (0x605D20) chama `LevelUpPanelManager.OnUpdate(wasPausedLastFrame, isPaused, endGamePanelActive)` (0x60A7B0) a cada frame, quando não há outro painel por cima (crusade, tutorial, aviso de visibilidade). `isPaused` = `PausePanelManager` +0x88, o menu do Esc.
- `LevelUpPanelManager.OnUpdate`:
  - Com a fila cheia e o popup fechado, abre `OpenPopup` (0x60E680) sozinho se `GamePreferences.AutoLevelUpEnabled` (+0x12) estiver ligado. Com ele desligado, só abre quando o jogador aperta a ação de level-up. **Até lá os pontos se acumulam (contador `SkillPointsLabel`) e o jogo NÃO pausa.** Isso já existe no jogo puro.
  - **`TimeUtil.LevelUpTime = (PowerUpAnimation.CurrentTime > 0) ? 0 : 1`** (`UIDeltaTimeAnimationGroup` +0x38), escrito a cada frame. Ou seja, fica em 0 enquanto qualquer parte do popup está visível, inclusive o fade de fechar. Na primeira versão desta nota estava `TargetVisibility`; corrigido na 0.8.1.
  - Com `PowerUpAnimation.TargetVisibility` (+0x30) em 1 (popup aberto ou abrindo), chama `InputManager.SwitchToUIMap` (0x4542F0: liga o mapa UI e desliga os mapas de gameplay +0x40 e +0x50), se o mapa UI estiver desligado. Abaixo de 1, volta com `SwitchToGameplayMap` (0x4541F0), **só se o mapa de gameplay estiver desligado**.
  - Com `endGamePanelActive`, limpa a fila, fecha tudo e põe `LevelUpTime = 1`.
  - Tempo mínimo antes de aceitar clique: `MinTimeToSelectOption` contra `TimeOptionsWereSetUp` (+0x148), que é `Time.realtimeSinceStartup`. Não depende do `timeScale`.
- `SetupPopup(ignoreSkills, reroll, isSkip)` (0x60EDF0) sorteia as opções em `MatchPowerUpData`:
  - em nível de skill (múltiplo de 3, nível 1 ou oferta de runa), `FillListWithRandomSkillPowerUps` com o tipo de `GetNecessarySkillTypePerLevelReached` (0x60A0F0);
  - nos outros níveis, `FillListWithRandomPowerUps` (passivos: stats e modificadores de skill);
  - nas fontes especiais (baú, boss), `FillListWithPowerUpsBySpecialSource`.
  - Se o sorteio de skill vier com menos itens que os slots do painel, completa com passivos. Com `InventoryData` +0xDC > 0 e a build sem slots cheios, escolhe uma skill aleatória sozinho (efeito de runa).
  - Botões Lock/Banish/Skip/Reroll e a tela de troca de skill (`ReplaceSkillsAnimation`, quando oferece skill nova com os slots cheios).
- `OnOptionSelected` (0x60A4C0) aplica o power-up (`PowerUpUtil.Apply` + `MatchPowerUpData.ProcessPowerUpCollected`), tira o pedido da fila e reabre com o próximo, se houver.

## 2. "Build completa"

- Não existe flag de "build completa" no jogo. O mais próximo, e que o próprio painel usa, é **`LevelUpPanelManager.IsFullOfSkills()` (0x60A270)**: `skills ativas (SkillCaster +0x408 → lista +0x20).Count ≥ GameProperties.MaxSkillSlots (Global +0x20 → +0x20) + slots extras do inventário (InventoryData +0x140)`. É público no interop.
- Depois que os slots enchem, as opções continuam vindo. Os passivos (stats e modificadores) não acabam, e a cada 3 níveis ainda aparece skill nova com a tela de troca. Então "build completa" na prática = **todos os slots de skill ocupados**. A partir daí os level-ups viram refinamentos pequenos e frequentes, que é a situação da reclamação.
- `Skill.SkillLevel` (+0xD0) existe, mas não há um "nível máximo de build" confiável para comparar. Usar `IsFullOfSkills()` é o critério mais estável, e é o mesmo que o jogo usa.

## 3. Painel aberto sem parar o tempo

- O único leitor de `TimeUtil.LevelUpTime` além do `TimeUtil.SetTime` (0x729F60) é o próprio `LevelUpPanelManager.OnUpdate`. Os outros 7 pontos que escrevem nele são `Start` de game managers, `OnDestroy` e a troca de cena, sempre pondo 1. Varri as 50 funções que tocam o `TimeUtil`. Nenhum sistema de gameplay olha o `LevelUpTime`: todos param só porque o `timeScale` vai a 0.
- `SetTime` faz `Time.timeScale = Pause × LevelUp × DeathGuard × SlowMotion × ControllerDisconnected × PlayerData × Credits`. Basta recalcular o produto **sem** o `LevelUpTime` no postfix que já existe. O `LevelUpTime` continua valendo como "popup aberto" para o painel, e nada mais muda.
- Só 3 funções do jogo leem `Time.timeScale` (um game manager de teste, `BounceToGroundProjectile.MoveProjectile` e o `AudioSource.PlayClipAtPoint`). Nenhuma tem relação com level-up.
- **Input:** com o popup totalmente aberto, o jogo desliga os mapas de gameplay (`SwitchToUIMap`). **O jogador fica parado:** não anda, não dasha e não usa skill manual. O **autocast continua**, porque o `PlayerSkillAutoCastComponent.UpdateAll` (0x7D82F0) depende de cooldown, `CombatActive`, ragdoll, stun e `ForceStopAutoCasting`, não do input (fase 4C). A mira fica na última direção.
- Religar o mapa de gameplay junto com o de UI é possível, mas arriscado: WASD/stick andaria **e** navegaria entre as opções, e a tecla de confirmar pode coincidir com o dash. Fica fora desta entrega.
- Mesmo parado, a ideia vale: o mundo segue para os dois, e o parceiro não congela. Quem escolhe fica vulnerável alguns segundos, mas com o autocast ativo. É o mesmo comportamento de quem joga com `AutoLevelUpEnabled` desligado e abre o painel no meio da luta.

## 4. Consequências por papel

- **Cliente escolhe sem pausar:** o host e o mundo autoritativo seguem. O cliente fica parado, os inimigos continuam vindo (marionetes) e o dano chega pelo `DamageToClient`. Ele pode cair (CoopLife) com o popup aberto, e escolher caído só aplica stats no próprio personagem.
- **Host escolhe sem pausar:** o mundo autoritativo continua, porque o `timeScale` do host fica em 1. Se o tempo do host parasse, o mundo do cliente pararia junto, então "não pausar a sessão" pro host significa justamente não parar o tempo local.
- **O mod e o `timeScale`:** os usos de `Time.timeScale` no mod (`ClientPuppets` "paused", `CoopLife.HostTick` "nada progride pausado", `EnemyCasts` descartando cast pausado, `PlayerSync` velocidade do fantasma) tratam 0 como "pausado, segura" e > 0 como normal. Com o tempo correndo, todos fazem o normal: o revive progride, as marionetes andam e os casts tocam. Snapshots, sincronização de fantasma e flush de dano usam `unscaledDeltaTime`/`unscaledTime` (fase 1). Dissolve e reciclagem são do próprio jogo e seguem o `deltaTime` normal. **Nada depende de o `timeScale` do host ser 0 ou 1 durante o level-up.**
- **Esc e death guard:** o `PauseTime` (Esc) e o `DeathGuardTime` (câmera lenta 0,1 por 0,6 s) continuam no produto. Esc com o popup aberto ainda pausa os dois (estado `Pause`, sincronizado).

## 5. Alternativas

| Opção | Avaliação |
|---|---|
| (a) Sem pausa só com a build completa | O pedido. Critério `IsFullOfSkills()`. Antes disso, cada escolha monta a build (skill nova, troca), e a pausa ajuda. **Padrão.** |
| (b) Sem pausa sempre (opção) | Mesmo mecanismo com o critério ignorado. Barato. **Entra como `Never`.** |
| (c) Pausa só local | No host congela o mundo inteiro, ou seja, igual a hoje. No cliente, ele congela o próprio mundo enquanto o host segue: o proxy dele no host leva dano parado, o dano chega e é aplicado com o cliente congelado (pode cair sem ver), e as marionetes dão um salto ao voltar. Pior que (a) e (b). **Não entra.** |
| (d) Escolha automática depois de N s | Daria para chamar `OnOptionSelected` com um item sorteado, mas isso mexe na build do jogador sem ele querer e passa pelo fluxo de troca de skill e lock. Com (a)/(b) o mundo já não para, então o ganho é pequeno. Fica como ideia. O jogo já tem `AutoLevelUpEnabled` desligado ("abrir quando eu quiser"), que é parecido. **Não entra.** |

## Plano

- `[Coop] LevelUpPause = Always | NotWhenFullBuild | Never` (padrão `NotWhenFullBuild`). Só vale em co-op (parceiro conectado e os dois na run). Sozinho, o jogo fica igual ao original.
- No postfix de `TimeUtil.SetTime`, com o popup aberto e a política dizendo "sem pausa", o `timeScale` é recalculado sem o `LevelUpTime`.
- Novo estado de rede `PauseReason.Choosing = 3` no `PauseState` (pacote 6, sem pacote novo). O parceiro não congela e mostra "X escolhendo upgrade (sem pausa)".
- Telemetria `[levelup]`: abertura e fechamento do popup com a política, a build completa, se pausou e a duração.
- Atalho de debug F6 (+1 nível no jogador local, Shift+F6 = +5) para chegar rápido na build completa.

## 6. Overlay jogável (0.8.1)

Feedback da 0.8.0: sem pausa, mas com o painel em tela cheia e o personagem parado, não dá pra jogar. O que o descompilado mostra para fazer um painel compacto em que dá pra andar:

- **Movimento:** `PlayerCharacter.UpdateAll` (0x7A9650) lê `InputManager.GetMovementValue` e, no modo clique-pra-andar, `MouseClickMovement.IsPressed` (`GamePreferences` +0x39). A mira lê a posição do mouse direto do dispositivo. Nada disso olha o painel de level-up nem o `HasOverlayPanel`: o personagem só para porque o `SwitchToUIMap` desliga o mapa de gameplay. Basta religar o mapa.
- **Teclas do próprio jogo no popup** (mapa UI, `InputManager`): `SelectPowerup1..7` (+0x280..+0x2B0) marcam `LevelUpPanelItem.Selected` (+0x104), ou `LevelUpPanelReplaceableSkillItem.Selected` (+0x50) na troca de skill, e o `OnUpdate` processa no mesmo frame, respeitando o tempo mínimo (`GamePreferences` +0x40, contra `TimeOptionsWereSetUp`). Também existem `RerollKey`, `BanishKey`, `LockSkillKey`, `SkipKey` e `FilterKey`, além de `NavigationAction` e `SubmitAction` (as que batem com WASD e dash).
- **Conflito de teclas:** dá pra comparar as teclas das ações em tempo de execução (`InputAction.controls[i].path`, um `ReadOnlyArray` que o interop expõe com `Count` e indexador) e desligar só as ações que batem (`InputAction.Disable`).
- **HUD:** `GameplayUIManager.HasOverlayPanel` (0x605150) = fim de jogo visível, pausa (`PausePanelManager.IsPaused`), `LevelUpPanel.PowerUpAnimation.TargetVisibility > 0`, seleção do Crusade ou dica do tutorial. Os 23 chamadores são todos painéis do HUD (vida, fúria, mana, os painéis de cada personagem), que se escondem com isso.
- **Animações do painel:** `UIDeltaTimeAnimationGroup.UpdateAnimation` (0x776A00) só chama `SetTime` nas `UIAnimation` quando o tempo muda. Com o popup aberto, nada é escrito a cada frame, então escala, posição e alpha postos pelo mod ficam.
- **Minimizar:** `ForceClosePopUp` (0x609FF0) **apaga a fila**, então não serve. Minimizar é só visual (alpha 0, sem raycast, teclas de opção desligadas).

## 7. Todas as escolhas de upgrade (0.9.2, issue #1)

- **Fontes de pedido** (chamadores de `PowerUpRequest..ctor` 0x611620 e de `List<PowerUpRequest>.Add`): `ExperienceComponent.LevelUp` (0x6CE090), `CollectablePowerUpRequest.DoOnReachedTarget` (0x464140, os power-ups de elite, boss e baú no chão), `GameManagerUtil.StartMatch` (0x6F47A0, a escolha inicial) e `MasteryGameManager.ResetPlayer` (0x4297A0). Todos vão pra `LevelUpPanelManager.PowerUpRequests`.
- **Quem abre o popup** (`OpenPopup` 0x60E680): o próprio `OnUpdate`, o `OnOpenLevelUpPopUpOneHandMode` e o `StartMatch`. O `SetupPopup` e o `OnOptionSelected` só são chamados de dentro do painel. **Não existe outro popup de escolha na partida.**
- **Outras escritas no `TimeUtil.PauseTime`** (fora pausa e starts): `TutorialHintPanelReferences.OnUpdate` (0x62E100) e `VisibilityInformationPanel.OnUpdate` (0x6A6BC0). São dicas, não escolhas, mas contam como Esc pro `PauseSync`.
- **Controle:** `Gamepad.current.dpad.{up,down,left,right}.path` dá os caminhos que comparar com os `controls` das ações. Pro destaque, o painel tem `SetSelectedLevelUpPanelItemIndex` / `SetSelectedReplaceableItemIndex`, e a navegação do próprio jogo usa `EventSystem.SetSelectedGameObject(item.Button)`.
