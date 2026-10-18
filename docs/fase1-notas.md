# Fase 1: notas para a fase 2

Testado com duas instâncias no mesmo PC (mapa SurvivorsDesert, level-up no host incluído). O usuário confirmou que funcionou.

## O que entrou

- **Cliente sem inimigos próprios** (`ClientSpawnBlock.cs`):
  - Postfix em `GameManagerUtil.StartMatch` que chama `StopSpawning()`. O cliente reaplica a chamada a cada 2 s enquanto há game manager, porque alguns `Setup` de spawner (que religam `_isActive`) chegam por chamada virtual e a ordem em relação ao StartMatch não é garantida.
  - Bloqueio de `ResumeSpawning`, `BossSpawnProcessor.SpawnBosses` / `MasterySpawnBosses`, e de `SpawnSpecificBoss`, que passa a devolver null. O único chamador de `SpawnSpecificBoss` é o modo Mastery.
  - Diagnóstico de "LEAK" num postfix de `Entity.Setup`. A God-Entity do mapa (`EntityNameGod`, time Creeps, posição 0,0,0) é parte da cena e está na allowlist.
  - `ClientSpawnBlock.NetworkSpawnInProgress` existe para a fase 2 ligar enquanto cria as marionetes, assim o diagnóstico não as acusa.
- **Telemetria no host** (`WorldTelemetry.cs`): log `[volume]` a cada 5 s.
- **Pausa sincronizada** (`Net/PauseSync.cs`, `PacketType.PauseState = 6`, ReliableOrdered):
  - Um postfix em `TimeUtil.SetTime` lê o estado **local** pelos fatores `TimeUtil.LevelUpTime` / `TimeUtil.PauseTime` (≈ 0 = pausado), manda o estado só quando ele muda e, se o remoto estiver pausado, força `Time.timeScale = 0`.
  - Ao desconectar, o estado remoto é limpo.
  - Aviso via OnGUI ("Parceiro pausou o jogo" / "Parceiro escolhendo upgrade..."). IMGUI funciona neste build.

## Descobertas que a fase 2 precisa saber

- **Pausa por perda de foco:** com `GamePreferences.PauseOnFocus` ligado, `GameplayUIManager.OnApplicationFocus(false)` (RVA 0x605490) chama `GameplayPanelManager.OnPauseButtonPressed`. É a mesma pausa do Esc e liga o mesmo `TimeUtil.PauseTime`, então não dá pra distinguir pelo fator. Em co-op, um prefix pula essa chamada (`GameplayUIFocusPausePatch`). Sem isso, com duas janelas, um clique numa delas pausava a outra, que propagava a pausa de volta. `GameLoop.OnApplicationFocus` e `RuntimePlatformUtil.OnApplicationFocus` não pausam nada.
- **Volume medido no host:** pico de **51 inimigos vivos**, em regime ~45 (`allEntities=46`, `activeCount=45`, 1 aliado = o jogador) nos primeiros minutos do Desert. É coerente com os defaults (30–40 + elites). Ainda falta medir no fim da run e em endless. Para o snapshot da fase 2 dá pra assumir ≤ 100 no início e ≤ 300 no pior caso (≈ 12 B por inimigo ⇒ ≤ 1,2 KB por snapshot no começo).
- **Spawn no cliente:** o único log de spawn foi `StartMatch(SurvivorsDesert) -> StopSpawning() applied`. Nenhum LEAK de inimigo real e nenhuma exceção nos dois logs. Os caminhos que contornam o `StopSpawning` (MapTasks, guardiões de recompensa, quests) não apareceram nesse teste. O diagnóstico continua ativo para pegá-los.
- **Pausa de level-up:** `TimeUtil.LevelUpTime` vai a ~0 enquanto a tela de upgrade está aberta. O jogo multiplica todos os fatores no `timeScale` a cada frame dentro do `GameLoop.Update`, então forçar `timeScale = 0` no postfix basta. Não é preciso mexer em `Time.timeScale` fora do `SetTime`.
- Para a fase 2: enquanto o remoto está pausado, o `timeScale` local é 0. A sincronização de marionetes e de snapshots deve usar `Time.unscaledDeltaTime`, como o `PlayerSync` já faz, para continuar processando pacotes. A simulação de inimigos do host para sozinha nesse período.
