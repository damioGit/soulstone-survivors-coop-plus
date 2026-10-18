# Fase 4A: caído, revive e fim de run em co-op

Testado com duas instâncias no mesmo PC (Desert). A investigação está em `docs/fase4a-investigacao.md`. O código fica em `src/SoulstoneCoop/Life/CoopLife.cs`, com ganchos em `RemoteProxy`, `LootSync`, `PlayerSync`, `PacketRouter`, `HostEnemySync` (StartMatch) e `CoopBehaviour`.

## O que entrou

- **Caído = o estado nativo `HealthComponent.ReviveOnDeath`.** O `CoopLifeDownPatch` (prefix + finalizer em `UnitUtil.ChangeEntityHealth`) liga `ReviveOnDeath` (e `ReviveDelay = 1e6`) **só durante** as chamadas em que o alvo é o jogador local. As condições são: conectado, partida começada, sem fim em andamento e o jogador ainda não caído.
  - Num golpe letal, o próprio jogo deixa 1 de vida, tira o jogador do `EntityRegistry` e não liga `HasBeenKilled`, então não há tela de morte nem game over. O death guard continua tendo prioridade.
  - O finalizer sempre devolve os valores originais. Se viu `DelayToRevive > 0`, marca o jogador como caído e põe invulnerabilidade de 1e6. Fora dessas chamadas, o `HealthComponent.UpdateAll` nunca vê o flag ligado (ele animaria o "Cowering" todo frame).
  - No tick seguinte entram o visual e o bloqueio: `RagdollComponent.SetToRagdoll(-1, true)`, `SetMovementMode(None)` e `ForceStopAutoCasting`. No host, os inimigos que tinham o host como alvo recebem `RemoveTarget`.
- **Input travado:** `PlayerCharacter.UpdateAll` devolve false enquanto o jogador local está caído. A lista só tem o jogador local.
- **Câmera de espectador:** um GameObject âncora do mod (`SoulstoneCoop_SpectatorAnchor`) segue o fantasma, e o `PlayerCameraController.Target` aponta para a âncora. Nunca aponta para o fantasma, que é recriado. Ao reviver, a câmera volta para o `Target` original e a âncora é destruída.
- **Revive com o host como autoridade:**
  - O host compara a posição dele com a do cliente (fantasma/proxy). Se um dos dois está caído e o outro está a ≤ 2,5 u, o progresso enche em 3 s; fora do raio, cai em dobro.
  - Quando o caído é o host, ele levanta sozinho. Quando é o cliente, o host manda `Revive = 5`.
  - Ao levantar: `DelayToRevive = 0`, vida a 50%, 2 s de invulnerabilidade, `AttackerList` limpa, `EntityRegistry.RegisterEntity` (só se não estiver registrado), `RagdollComponent.SetToAnimator()` (animação de levantar, depois `Walking`), autocast religado e câmera de volta.
- **Integração com o `ClientState` da fase 3:** o `dead` do cliente agora é `HasBeenKilled || caído`. O proxy sai e volta da mira e do colisor sozinho. Ao cair, o XP passa a ser do host (`ClientOwnsXp`).
- **Host caído:**
  - todo spawn vai para perto do cliente (patch 7);
  - o PlayerSeeker vai atrás do proxy (patch 13);
  - o XP vai para o cliente;
  - os inimigos param de achar o host pelo `EntityRegistry`.
- **XP retido (gancho da 4B):**
  - O XP compartilhado que chega com o jogador caído fica em `xpShareHeld`.
  - O XP que o **próprio** caído pega (orbs perto do corpo) não é aplicado. O prefix do `AddExperience` devolve false, o XP vai para o parceiro normalmente e também fica retido para o caído (`ownXpHeldWhileDown`).
  - Tudo entra quando ele levanta. Isso evita o level-up com o jogador caído, que pausaria os dois.
- **Derrota:** com os dois caídos por 0,5 s, o host manda `RunEnd(Defeat)`. Cada lado aplica a morte real pelo caminho do jogo: `InvulnerabilityTime = 0`, `ChangeEntityHealth(null, jogador, -1e9)`, e `HasBeenKilled = true` se o death guard segurar. O `SurvivorsGameManager.UpdateAll` mostra o "You died" e a tela final em cada máquina.
- **Vitória:** um postfix em `GameManagerUtil.OpenPortalAndObjective` (o ramo nativo de vitória do host) manda `RunEnd(Victory)` e revive quem estiver caído. O cliente faz `MainObjective.Current = Total`, e o próprio `UpdateAll` dele roda a vitória: apocalipse, coleta e portais.
- **Portal Home:** um prefix em `SurvivorsGameManager.OnPlayerEnterPortal` avisa o outro lado (o cliente via `RunEndRequest`, o host via `RunEnd`). Quem recebe chama o mesmo método com `FromNetwork`, só se `GameData.EnterPortalObjective` já existir; senão tenta de novo por até 30 s.
- **Portais Endless e BossRush:** bloqueados em co-op (prefix → false), porque cada máquina sorteia um mapa.
- **Textos curtos e VFX do revive:** a versão com textos de no máximo 16 caracteres e o efeito do death guard ao levantar foi só carregada (sem erro), não vista em jogo. Conferir `texts`, `textErrors` e `vfx` no log `[life]` do próximo teste.
- **Desistir:** o cliente pede ao host (`RunEndRequest`), e o host manda `RunEnd(Surrender)`. Os dois rodam o `GameManagerUtil.Surrender` original com `RunEnding` ligado, então não há caído.
- **Desconexão:** quem estiver caído quando o parceiro sai morre de verdade (regra solo).
- **Feedback:** o IMGUI deste build está parcialmente stripado. As chamadas `GUI.*` lançam "Method unstripping failed", o que desligou o `OnGUI` inteiro; até o aviso de pausa da fase 1 cai nisso. Por isso o feedback usa o próprio jogo:
  - texto flutuante pelo `OverheadCombatTextPanelManager.RequestCombatTextWithString`, nas duas telas: "CAIDO" (ou "CAIDOS"), "REVIVE xx%" e "REVIVEU!". **Limite de 16 caracteres por texto:** o `GPUIndirectInstancedCombatTextManager.CreateCombatTextRequest` (0x601550) tem buffer fixo (`MaxCharacterCountPerText`, 16 neste build). Acima disso ele só loga "Combat Text is bigger than the buffer capacity 16" e estoura `IndexOutOfRange`, e o texto não aparece (a primeira versão dos textos falhou assim). O `FloatText` corta tudo no menor valor entre o limite lido em runtime e 16, e conta as falhas em `textErrors`;
  - o VFX do death guard (`InventoryComponent.DeathGuardVfx` via `SkillUtil.RunVfx`) ao levantar.
- **Fantasma suave e animado (resolvido e validado pelo usuário):**
  - **Causa do tremor:** o `PlayerSync` escrevia a posição crua de cada pacote de 20 Hz no fantasma, e a âncora da câmera de espectador herdava os saltos. Agora o fantasma é interpolado com um buffer de snapshots, desenhado 0,1 s no passado, com extrapolação curta se o pacote atrasa e snap acima de 8 u.
  - **Causa do "sem animação":** o clone perde o `PlayerAnimator` do jogo, que é quem escreve os parâmetros de locomoção. O `PlayerSync` escreve os mesmos no Animator do fantasma, com a velocidade da interpolação: `ForwardMovement`, `SidewaysMovement` e `OnGround` (`PlayerAnimator..cctor` 0x7A82E0, `OnUpdate` 0x7A7A20), normalizados pela velocidade máxima do jogador e com amortecimento de 0,1 s. Ele volta a ler os Animators quando o fantasma é recriado.
- **Por que o aviso de tela não aparecia:** o IMGUI do jogo vem sem parte do sistema de interface (as chamadas `GUI.*` dão "Method unstripping failed"). Daí o texto flutuante do próprio jogo.
- **Hotkeys de debug:** F9 dá um golpe letal no jogador local. Só no host: F10 força a vitória (`MainObjective.Current = Total`) e F11 revive todos.

## Medido no teste

- O cliente caiu duas vezes por dano real dos inimigos: `hp=1`, `ragdoll=True`, `stillRegistered=False`. O host reviveu por proximidade, e o cliente voltou com 35 de vida (50% de 70).
- XP retido: `xpShareHeld` chegou a 480 com o cliente caído, e `xpShareApplied=480` no revive. `ownXpHeldWhileDown=180` (orbs que o caído pegou) também entrou.
- A vitória com F10 aconteceu nas duas janelas. O cliente, caído, foi revivido junto e o objetivo ficou em 20/20.
- A derrota com F9 nos dois saiu nas duas janelas (`DEAD for real (both players down)`).
- O usuário confirmou o portal e o resto do fluxo. Nenhuma exceção nova e nenhum erro de trampoline; só os avisos já conhecidos de `SetupExtraSkillForEntity` da 4C.

## Pacotes

`Revive = 5` (host → cliente), `CoopLife = 18` (host → cliente: hostDown, clientDown, progress), `RunEnd = 19` (host → cliente) e `RunEndRequest = 20` (cliente → host). As próximas fases começam no **21**.

## Pendências

- **Portais Endless e BossRush, e o botão Reiniciar:** cada máquina sorteia o próximo mapa (`MapCollectionSO.GetRandomMapExcluding`, `LoadNextScene`). Precisa de sincronização de troca de cena com o host escolhendo o `mapId`. Hoje esses portais ficam bloqueados em co-op.
- **Aviso de pausa (fase 1) e IMGUI:** o `OnGUI` do mod é desligado no primeiro erro de unstripping. O aviso "Parceiro pausou" deve ser migrado para o texto flutuante ou para a UI do jogo.
- **O fantasma de um parceiro caído fica de pé:** só o texto flutuante indica a queda. Dá para tocar um estado do Animator ou deitar o modelo.
- **Desistir:** está implementado, mas o teste com o menu de pausa do cliente não foi confirmado nos logs.
- **Level-up com o jogador caído:** evitado pela retenção de XP. Se outra fonte de XP aparecer fora do `AddExperience`, revisar.
