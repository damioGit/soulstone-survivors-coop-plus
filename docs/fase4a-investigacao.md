# Fase 4A: reviver o parceiro e game over em co-op (investigação)

Investigação estática, só leitura: `dump.cs`/`script.json` do Il2CppDumper, Ghidra headless (`-readOnly`) sobre `tools/GameAssembly.dll` e o `ApiDump` já compilado. Os RVAs são relativos a `0x180000000`. As saídas brutas ficaram em `tools/decompiled_4a_{raw,named}{1,2,3}.txt` (fora do git). Nada em `src/` foi alterado.

Notação: `Classe.Metodo (RVA)`, `Classe.Campo (offset)`. Offsets de `Entity` usados abaixo: `Health` 0x2C0/0x2C8, `Ragdoll` 0x2F0/0x2F8, `Stunnable` 0x300/0x308, `SkillCaster` 0x280/0x288, `Character` 0x260/0x268, `Animator` 0x2B0/0x2B8, `Inventory` 0x3B0/0x3B8, `PlayerSkillAutoCast` 0x3E0, `PlayerAutoAim` 0x460. O par é o flag `Has*` seguido do ponteiro.

---

## Resumo da descoberta principal

O jogo já tem um estado de **"caído" nativo**: `HealthComponent.ReviveOnDeath` (0x2B) + `ReviveDelay` (0x2C) + `DelayToRevive` (0x164). Quando esse flag está ligado e o golpe é letal, `UnitUtil.ChangeEntityHealth` **não mata**: deixa a vida em 1, tira a entidade do `EntityRegistry` e arma um contador. Com isso o jogador fica fora da mira dos inimigos, sem `HasBeenKilled`, sem tela de morte e sem game over. O próprio `GameManagerUtil.Surrender` já trata jogadores com esse flag ligado, o que indica que o caso é previsto pelo jogo. A proposta (seção 5) usa esse caminho para o "caído", o ragdoll do jogo para o visual e o `RagdollComponent.SetToAnimator` para levantar.

---

## 1. Caminho da morte do jogador

### 1.1 `UnitUtil.ChangeEntityHealth` (0x73BE30), o ramo de vida ≤ 0

Ordem real, conferida no descompilado:

1. `HealthComponent.HasBeenKilled` (0x118) ligado: retorna na hora.
2. `InvulnerabilityTime` (0xF0) > 0: dano vira 0 e `isImmune = true`. Cura passa.
3. Escudos temporários (`TemporaryShields`, 0x178) absorvem, `CurrentHealth` (0xF4) recebe o resto, `RegisterAttacker` só com atacante não nulo, e thresholds.
4. Se `CurrentHealth ≤ 0`:
   - **Death guard** (a "segunda chance" do jogo): se a entidade tem Inventory e `InventoryData.DeathGuards` (`InventoryData` 0x4C) > 0, **e** `EndGamePanelManager.LossBySurrender` (0x340) está desligado, chama `InventoryComponent.ProcDeathGuard` (0x7C15E0), que faz voz, `DeathGuards--`, VFX `DeathGuardVfx` e a corrotina `RunDeathGuard` (0x7CBD20, câmera lenta via `TimeUtil.DeathGuardTime` = 0,1 por 0,6 s reais). Depois `InvulnerabilityTime = max(atual, 2)` e `CurrentHealth = round(maxHP × 0,5)`. Não morre.
   - Senão, se **`ReviveOnDeath` está desligado** (morte de verdade): `CurrentHealth = 0` e `HasBeenKilled = !IgnoreDeathState` (0x34). Para jogador, `Entity.ResetAscensions` (0x80AC50) e `Entity.PauseAscension` (0x80A140), que zeram e pausam os recursos de classe (rage, mana, concentração e afins). Se a skin não é mod, o ragdoll vai para o layer de morto. Depois `SetRotationMode(0)`, procs de on-kill de cada atacante da `AttackerList`, `GameStats` +1 conforme `GameStatsHealthType`, sons de morte, loot (se `yieldLoot`) e:
     - com ragdoll (caso normal do jogador): **`RagdollComponent.SetToRagdoll(-1, false)`** (0x7DDA90). A duração −1 é a constante em `0x1828C3344`, lida do `.rdata`, e significa ragdoll permanente;
     - sem ragdoll: `StunnableComponent.SetDead` (0x801B50), `SkillCasterComponent.CancelExecution`, `SetMovementMode(None)` e a animação de morte.
   - Senão (**`ReviveOnDeath` ligado**): se `DelayToRevive ≤ 0`, faz `DelayToRevive = ReviveDelay`, `CurrentHealth = 1` e `EntityRegistry.UnregisterEntity(entity)` (0x4252C0). Não liga `HasBeenKilled`, não roda procs, stats, loot nem ragdoll, e não reseta ascensões.

### 1.2 Depois do golpe: `HealthComponent.UpdateAll` (0x7C0270)

- `HasBeenKilled`: `CharacterLayerComponent.OnDeath` (0x6C0600) troca o layer do GameObject para `TargetDeadLayer`, remove o ícone do minimapa e, uma vez só (`DegisteredEntity` 0x160), chama `EntityRegistry.UnregisterEntity`. Para **jogador**, o tempo até o dissolve é `FLT_MAX` (0x7F7FFFFF), então o corpo nunca dissolve nem recicla.
- O dissolve marca `TriggeredDissolve` (0x134) e `CurrentDissolveAmount = -2`, e para `AliveParticles`/`AliveVfx`.
- **Estado nativo de caído** (`ReviveOnDeath && DelayToRevive > 0`): decrementa `DelayToRevive` com `Time.deltaTime` e manda `AnimatorComponent.SetFloat(Cowering, 1)`. Quando o contador chega a 0: `TriggeredRevive = false`, `CurrentHealth = MaxHealth` e **`EntityRegistry.RegisterEntity`** (0x424AC0). Esse é o "levantar" do próprio jogo. Enquanto `ReviveOnDeath` está ligado, essa linha roda **todo frame**, mandando `SetFloat(Cowering, 0)` quando não há contador.
- O regen de vida só roda com `!HasBeenKilled`.

### 1.3 O que dispara o fim da partida: `SurvivorsGameManager.UpdateAll` (0x438DC0)

Conferido no descompilado (`tools/decompiled_fase0_batch1_named.txt`):

- `allDead = true`, e o loop percorre `GameData.Players` (0x60): basta um com `Health.HasBeenKilled == false` para virar `false`. No caminho B, `GameData.Players` só tem o jogador local, então **morrer localmente é sempre "todos mortos"**.
- `mainDone = MainObjective.IsComplete()` (`SurvivorsGameManager.MainObjective` 0xC0, slot 4; `EliminateSpecificEntitiesObjective.IsComplete` 0x612B60 é `Total ≤ Current`).
- Se `!GameData.StoppedSpawning` (0x1D9) e (`allDead` ou `mainDone`): para a corrotina de objetivos e chama `StopSpawning()`. O resultado sai no frame seguinte.
- Se já parou de spawnar e `GameStats.Result` (0x9C, `Nullable<MatchResult>`) está vazio:
  - `allDead`: `Result = Defeat`, `GameModeBaseData.SetEndlessCompletionPercent`, **`GameManagerUtil.SaveFinalGameStats`** (0x6EECA0, o cálculo dos resultados). Depois, se não foi desistência: com `GamePreferences.EnableDeathEffect` (0x38), roda `DeathSequenceManager.DeathSequenceRoutine` (0x4241C0 → MoveNext 0x4330B0); sem ele, `EndGamePanelManager.ShowEndingScreen` (0x5F5910) e `GameManagerUtil.SaveToProfile` (0x6F2490) direto.
  - `mainDone`: `Result = Victory`, `SaveFinalGameStats`, `GameManagerUtil.CombatActive = false`, corrotinas `CastApocalypse`, `CollectAllCollectablesAfterDelay`, `CollectAllSoulstonesAfterDelay`, `ResetAscensions`/`PauseAscension` e **`GameManagerUtil.OpenPortalAndObjective`** (0x6EE6F0 → MoveNext 0x705120).
- No **cliente**, `StoppedSpawning` já vem ligado (patch da fase 1), então a derrota sai no mesmo frame da morte.
- O tempo de partida (`GameStats.MatchTime` 0x10) só anda enquanto `Result` está vazio.

### 1.4 A tela "You died": `DeathSequenceRoutine` (0x4330B0)

`AudioUtil.StopAll`, `GraveSpawnAnimation` (túmulo, só em skin 2D), `DeathSlowTime` (mexe em `TimeUtil.SlowMotionTime` 0xC), `DeathZoom` (`PlayerCameraController._forcedZoomFactor` 0xA0), `AdjustPostProcessing`. Esperas com `WaitForSecondsRealtime`. Depois `GameplayPanelManager.FadeAnimationGroupDesiredVisibility` (0x230) vai a 0, `ObjectivesPanelReferences.HideAllObjectives` e **`LoseScreenPanelReference.ShowLoseScreen(true)`** (0x610860, o "You died"). Em seguida `AdjustCameraOffset` (`FollowOffset` 0xA8) e, se `EndGamePanelManager.EndingScreenTriggered` (0x308) ainda está desligado, `ShowEndingScreen` + `SaveToProfile`. O `ShowEndingScreen` (MoveNext 0x5FE5A0) troca o input para o mapa de UI (`InputManager.SwitchToUIMap`).

### 1.5 Onde segurar a run com o jogador local "morto" na partida

Há dois pontos possíveis:

- **(A) Mascarar no game over:** prefix em `SurvivorsGameManager.UpdateAll` que zera `HasBeenKilled` do jogador local quando o parceiro está vivo, mais um finalizer que devolve `true`. O loop de `allDead` passa a ver alguém vivo e nada mais dispara (nem `DeathSequenceRoutine`, nem `SaveFinalGameStats`). Funciona, mas o jogador já passou pela morte real: perdeu as ascensões (`ResetAscensions`), saiu do registry, ganhou layer de morto e ficou com `HasBeenKilled` para o resto do jogo. Reviver depois exige desfazer tudo isso à mão.
- **(B) Não morrer (recomendado):** ligar `ReviveOnDeath` do jogador local **só durante** as chamadas de `ChangeEntityHealth` em que ele é o alvo, enquanto o parceiro está de pé. O golpe letal cai no ramo nativo de caído. `HasBeenKilled` nunca liga, o `UpdateAll` nunca vê "todos mortos" e não há nada a mascarar. Detalhes na seção 5.

---

## 2. Revive: o que o jogo já tem e como restaurar

### 2.1 Mecânicas nativas encontradas

| Mecânica | Onde | Serve para o co-op? |
|---|---|---|
| Death guard ("segunda chance") | `ChangeEntityHealth` + `InventoryComponent.ProcDeathGuard` (0x7C15E0) / `RunDeathGuard` (0x7CBD20); cargas em `InventoryData.DeathGuards`, ganhas por `SkillTreeDeathGuardSO.DeathGuardsPerLevel`; UI `DeathGuardPanelReferences` | Só evita a morte no golpe letal. Não existe função que reviva alguém já morto. Continua funcionando antes do caído, porque o jogo checa o death guard primeiro. |
| `ReviveOnDeath` / `ReviveDelay` / `DelayToRevive` / `TriggeredRevive` | `ChangeEntityHealth` (desce) + `HealthComponent.UpdateAll` (levanta com vida cheia + `RegisterEntity`) | **Sim.** É o estado de caído. A IA trata entidades nesse estado como inativas (`BasicAIComponent.DoBasicBehaviour` 0x5E3100 chama `RemoveTarget` quando o próprio `_health` 0x88 está caído; `RefreshTarget` 0x5EB840 e `StaticAIComponent.UpdateAll` 0x7FB9B0 também pulam). Para o jogador, o que importa é que ele sai do `EntityRegistry`, então nenhuma busca de alvo o encontra. |
| Ragdoll que levanta sozinho | `RagdollComponent.SetToRagdoll(dur>0)` → `ReturnFromRagdoll` (0x7E0750) → `SetToAnimator` (0x7DD660) → `ReEnableMovement` (0x7E0470) | **Sim**, para o visual de cair e levantar (ver 2.3). |
| Cutscene de câmera | `SetEntityCameraTargetAction` (0x4953A0) / `ReleaseEntityCameraTargetAction` (0x491270) chamam `PlayerCameraController.SetEntityTarget(t, null, null)` | Mostra que a câmera aceita seguir outro alvo sem `ModifiedCharacter` nem auto-aim (seção 3). |
| Ascensões | `SetPlayersAscensionsStateAction` (0x491900) usa `ResetAscensions`/`PauseAscension`/`StartAscension` (0x80BBA0) | No caminho A, o revive precisaria de `StartAscension`. No B, nada é resetado. |

Não achei runa nem item de "ressuscitar". `ReanimationComponent` é recurso de classe (gera loot e ascensão), não revive de jogador. `SurvivorsGameManager.ShouldKillPlayer` (0xDC) existe, mas não aparece no `UpdateAll`; não investiguei.

### 2.2 Restaurar, caminho B (o jogador nunca morreu)

O estado a desfazer é só o que o ramo de caído e nós mesmos mudamos:

1. `health.DelayToRevive = 0`, `TriggeredRevive = false`, `CurrentHealth = max(1, round(maxHP × f))` (sugestão: f = 0,5, igual ao death guard; o nativo usa vida cheia) e `InvulnerabilityTime = 2` (o valor do death guard). Limpar `AttackerList`.
2. `EntityRegistry.RegisterEntity(entity)` (0x424AC0), igual ao revive nativo. Ele também registra o rigidbody/colisor de `Physical` (0x270). **Não é idempotente:** se a entidade já está registrada, gera `Debug.LogError` e devolve false. Então só chamar se o caído de fato desregistrou (veja o `TryGetEntity(entity.Id)` antes).
3. Ragdoll: `RagdollComponent.SetToAnimator()`. É privado no jogo, mas o Il2CppInterop expõe como público (o ApiDump lista). Ele religa os animators, desliga o `RagdollRoot`, toca a animação de levantar (frente ou costas) e inicia `ReEnableMovement(RecuperationTime)`, que faz `SetMovementMode(Walking)` e, depois de `RecuperationTime`, `Ragdolled = false`. Alternativa sem método privado: `CurrentDuration = 0` e `SetToRagdoll(0.01f, true)`, que dispara `ReturnFromRagdoll` → `SetToAnimator`. Essa alternativa falha se `ImmuneToRagdollEffect` estiver ligado.
4. `PlayerSkillAutoCastComponent.ForceStopAutoCasting = false`.
5. Câmera: restaurar o `Target`/`TargetCharacter`/`_autoAimComponent` salvos (seção 3).
6. Rede: o cliente volta a mandar `ClientState.dead = false`. O `RemoteProxy` já reverte (colisor, `IgnoredByEntitySearchQueries`).

### 2.3 Restaurar, caminho A (morte real), para referência

Seria preciso também: `HasBeenKilled = false`, `DegisteredEntity = false`, `TriggeredDissolve = false`, `CurrentDissolveAmount = -2`, `CurrentTimeToWaitUntilDissolve = 0`, `CharacterLayerComponent.Setup()` (0x6C0730, volta `TargetAliveLayer`), ícone do minimapa (`MinimapManager.RequestIcon`, como em `HealthComponent.Setup` 0x7BF770), religar `AliveParticles`/`AliveVfx`, `Entity.StartAscension()`, reverter o layer de morto dos ossos do ragdoll e manter a máscara no `UpdateAll`. Rodar `HealthComponent.Setup` de novo zeraria quase tudo, mas também pediria um ícone de minimapa duplicado e tocaria a animação de spawn. É bem mais frágil que o B.

---

## 3. Estado de espectador

- **Câmera:** `PlayerCameraController.UpdateAll` (0x408A80) segue `Target` (0x48), que é um `Transform` puro (`TargetPosition` 0x408A10). `TargetCharacter` (0x50) e `_autoAimComponent` (0xB8) são checados com `!= null` antes de uso, e o próprio jogo passa os dois nulos nas cutscenes. Para seguir o parceiro: salvar os três campos e chamar `SetEntityTarget(âncora, null, null)` (0x4084B0). Para voltar: `SetEntityTarget(salvos...)`.
  - **Não aponte direto para o transform do fantasma.** O `CoopBehaviour.RebuildGhostIfModelInactive` destrói e recria o fantasma. Se a câmera ficar com um `Transform` destruído, o `Transform.get_position` estoura NRE dentro do `PlayerCameraController.UpdateAll` e derruba o resto do frame. Use um GameObject âncora do mod, que nunca é destruído enquanto há espectador, e mova-o a cada frame para a posição do fantasma (ou para `PlayerSync.LastReceivedPosition`).
  - Com auto-aim nulo, a câmera usa `RotationTarget` (0x90). O jogo faz isso na cutscene, mas vale confirmar no teste que não dá NRE. Se der, passe o `_autoAimComponent` original e troque só o `Target`.
- **Input:** `PlayerCharacter.UpdateAll` (0x7A9650) **não checa** `HasBeenKilled`. O que segura o jogador morto no jogo normal é o ragdoll: `SetToRagdoll` faz `CancelExecution`, `SetMovementMode(None)` e `DisableAnimators`. Com `MovementMode = None`, o `CharacterMovement` fica pausado (`SetMovementMode` 0x7A5D20), e o `PlayerCharacter.Dash` (0x7A8450) exige `Walking` ou `Falling`. O input de mover e de clicar para andar continua sendo lido. Em caídos longos, recomendo também um prefix que pule `PlayerCharacter.UpdateAll` (só existe o jogador local nessa lista) enquanto o jogador local está caído.
- **Autocast:** `PlayerSkillAutoCastComponent.UpdateAll` (0x7D82F0) **também não checa morte**. Ele para por `GameManagerUtil.CombatActive == false`, por `RagdollComponent.Ragdolled` (0x70), por `StunnableComponent.Stunned` (0x38) ou por `ForceStopAutoCasting` (0x121). Como o ragdoll pode não ligar (skin mod, `ImmuneToRagdollEffect`), ligue `ForceStopAutoCasting` explicitamente durante o caído.
- **UI de morte:** no B nada aparece, porque não há `HasBeenKilled` nem `DeathSequenceRoutine`. A barra de vida mostra 1. O aviso de caído e o progresso do revive ficam num OnGUI nosso, que funciona neste build (fase 1). No A, sem a máscara, sairiam o `ShowLoseScreen` e a `EndGamePanel`.
- **Coleta/XP:** o caído sai do registry, então o `HomePortal` (que usa `EntityRegistry.TryGetEntityByRigidbody` + `IsPlayer`) não o vê. Já o XP instanciado (`XpDropper.OnUpdate`, `PlayerEntities[0]`) continua coletando no lugar do corpo, e o `ExperienceComponent.AddExperience` (0x6CDEB0) não checa morte. Pode subir de nível caído, e a tela de level-up pausa o parceiro (PauseSync). No MVP isso é aceitável; dá para bloquear depois com um prefix em `XpDropper.OnUpdate`/`OnLateUpdate`.

---

## 4. Fim de run sincronizado

| Fim | Como o jogo faz | Ponto de interceptação/comando |
|---|---|---|
| **Derrota** | `UpdateAll` vê todos de `GameData.Players` com `HasBeenKilled` → `Defeat` → `DeathSequenceRoutine`/`ShowEndingScreen` | O host decide ("os dois caídos") e manda `RunEnd(Defeat)`. Cada lado aplica a morte real no jogador local (como o `Surrender` faz, ver abaixo) e o próprio `UpdateAll` cuida do resto. |
| **Vitória por objetivo (bosses)** | `MainObjective` = `EliminateSpecificEntitiesObjective` criado por `SurvivorsGameManager.<CreateObjectives>d__37` (0x43FE80) depois de `DelayForMainObjective`, com `Total` = soma dos bosses de `BossSpawnDetails`. Existe também no cliente. `Current` só sobe em `ObjectivesPanelReferences.RunEliminateSpecificEntitiesObjective` (0x618670), e só enquanto `Current < Total`, contando entidades de `EntitiesToEliminate` mortas (preenchida via `GameManagerUtil.RegisterBossEntity` 0x6EEA40). | Host: postfix no `UpdateAll` detecta `GameStats.Result` virando `Victory` e manda `RunEnd(Victory)`. Cliente: `sgm.MainObjective.Current = sgm.MainObjective.Total`. No frame seguinte o **ramo nativo** de vitória roda no cliente (apocalipse, coleta, `OpenPortalAndObjective`). O valor não é sobrescrito, porque o contador só sobe enquanto `Current < Total`. |
| **Portal (Home)** | `OpenPortalAndObjective` ativa os portais e cria `GameData.EnterPortalObjective` (0x178). `HomePortal.OnTriggerStay` (0x467E80) conta `TimeToEnter` com o jogador **registrado** dentro e dispara a `UnityEvent` → `SurvivorsGameManager.OnPlayerEnterPortal` (0x438CF0, sem chamador direto). Esse chama `GameManagerUtil.ProcessOnPlayerEnterPortal` (0x6EE750: marca o objetivo, `SetMovementMode(None)`, esconde o modelo) e depois `OnEnterHomePortal` (0x6EE160: `SaveFinalGameStats` se preciso, `ShowEndingScreen(delay 2)`, `SaveToProfile`). | Prefix em `OnPlayerEnterPortal` (e nas versões Endless 0x438CC0 e BossRush 0x438C80): quando não veio da rede, avisa o outro lado (cliente → host → cliente) e deixa rodar. Quem recebe chama o mesmo método localmente, com flag de reentrância e **só se `GameData.EnterPortalObjective != null`**; senão `ProcessOnPlayerEnterPortal` estoura NRE. Se ainda não existir, enfileira até o `OpenPortalAndObjective` local rodar. |
| **Portal Endless/BossRush** | `OnEnterEndlessPortal` (0x6EDF20) chama `SetEndlessData` + `MapCollectionSO.GetRandomMapExcluding` + `LoadNextScene` (0x6EDA70). **O mapa é sorteado em cada máquina.** | Precisa de sincronização de troca de cena (o host escolhe o `mapId` e o cliente força o mesmo no `LoadNextScene`). Fica fora do MVP da 4A: no começo, liberar só o portal Home em co-op. |
| **Desistir** | `PausePanelManager.OnUpdate` (0x61AA50) → `GameManagerUtil.Surrender` (0x6F7D90): `LossBySurrender = true` e, para cada jogador, `InvulnerabilityTime = 0`, `ChangeEntityHealth(null, p, -1e9, true)`, `ReviveOnDeath = false` e `HasBeenKilled = true`. Daí o `UpdateAll` dá derrota. | Prefix em `Surrender`: em co-op, quando não veio da rede, manda `RunEndRequest(Surrender)` ao host e cancela. O host manda `RunEnd(Surrender)` e os dois chamam o original com o flag "fim em andamento" ligado. |
| **Depois da tela final** | `EndGamePanelManager.OnRestartPressed` (0x5F5070) → `LoadNextScene(cena atual)`. `OnBackToMenuPressed` (0x5F4D50) → `ResetProgress` + `MainUIManager.ChangeScene`. | São locais. Reiniciar em co-op precisa da mesma sincronização de cena. Pendência para depois da 4A. |

**Morte final igual à do jogo** (para `RunEnd(Defeat)`): com o flag "fim em andamento" desligando o nosso caído, faça `InvulnerabilityTime = 0` e `ChangeEntityHealth(null, jogador, -1e9f, false, …)`. O ragdoll já está ativo, e `SetToRagdoll` simplesmente não refaz o primeiro bloco. Se o death guard absorver (o jogador ganhou carga enquanto caído, caso raro), ligue `HasBeenKilled = true` à mão, como o `Surrender`. O `UpdateAll` faz o resto: "You died" e tela final, em cada máquina com as próprias estatísticas.

---

## 5. Proposta

### 5.1 Regras

- Golpe letal com o parceiro conectado e de pé: **caído** (death guard primeiro, como no jogo).
- O parceiro revive ficando a ≤ 2,5 u do corpo por 3 s acumulados. O progresso cai em dobro fora do raio e zera se o reviver cair. Os números ficam em config.
- Os dois caídos (estável por ~0,5 s, para absorver um revive no mesmo instante): derrota para os dois.
- Vitória, portal Home e desistência acontecem para os dois. Na vitória, o host revive quem estiver caído.
- Parceiro desconectado: volta a regra solo. Se o jogador local estiver caído nessa hora, morte final local.
- **O host é a autoridade** de caído, revive e fim de run. Ele sabe a posição dele e a do cliente (fantasma/proxy) e recebe o estado do cliente pelo `ClientState`.

### 5.2 Patches Harmony

| # | Lado | Alvo (RVA) | Tipo | O que fazer |
|---|---|---|---|---|
| 1 | Ambos | `UnitUtil.ChangeEntityHealth` (0x73BE30) | Prefix + **Finalizer** | Se `target` é o jogador local, há co-op, o parceiro está de pé e não há "fim em andamento": guardar `ReviveOnDeath`/`ReviveDelay`, ligar `ReviveOnDeath = true` e `ReviveDelay = 1e6`. No finalizer, sempre restaurar os valores originais, e se `DelayToRevive > 0` e ainda não estava caído, chamar `EnterDowned()`. **Não deixar `ReviveOnDeath` ligado fora da chamada**: o `HealthComponent.UpdateAll` mandaria `SetFloat(Cowering)` todo frame em um Animator que provavelmente não tem esse parâmetro (risco de log). Esse patch convive com o prefix do cliente que já existe (`ClientPuppetHealthClampPatch`, que só age em marionetes) e com o patch 9 do host (proxy). |
| 2 | Ambos | `EnterDowned()` (nosso, sem patch) | — | `InvulnerabilityTime = 1e6` (DoTs e AoE ficam imunes, e a vida não volta a ≤ 0), `Ragdoll.SetToRagdoll(-1, true)` (se houver), `ForceStopAutoCasting = true`, `Character.SetMovementMode(None)` (cobre skin sem ragdoll), câmera para a âncora, estado de rede. No host: `BasicAIComponent.RemoveTarget()` em quem tinha o host como `_entityToFollow`, igual ao `RemoteProxy.DropAsTarget`. |
| 3 | Ambos | `PlayerCharacter.UpdateAll` (0x7A9650) | Prefix → false enquanto o local está caído | Sem input de movimento ou clique enquanto caído. A lista só tem o jogador local. |
| 4 | Host | `SurvivorsGameManager.UpdateAll` (0x438DC0) | Postfix | Detecta `GameStats.Result` ganhando valor: `Victory` manda `RunEnd(Victory)`; `Defeat` serve só de log, porque a derrota é comandada por nós. Roda a máquina de revive (progresso, `Revive`, `RunEnd(Defeat)`). Tudo em try/catch. |
| 5 | Cliente | Ao receber `RunEnd(Victory)` (sem patch) | — | `sgm.MainObjective.Current = sgm.MainObjective.Total` (checar null). Revive local se estiver caído. |
| 6 | Ambos | `SurvivorsGameManager.OnPlayerEnterPortal` (0x438CF0) | Prefix | Quando não veio da rede: mandar `RunEndRequest(PortalHome)` (cliente) ou `RunEnd(PortalHome)` (host). Ao receber: chamar o método local com o flag de rede, só se `GameData.EnterPortalObjective != null`; senão enfileirar. `OnPlayerEnterEndlessPortal` (0x438CC0) e `OnPlayerEnterBossRushPortal` (0x438C80): em co-op, bloquear (prefix → false) até existir a sincronização de cena. |
| 7 | Ambos | `GameManagerUtil.Surrender` (0x6F7D90) | Prefix | Em co-op e sem vir da rede: pedir ao host e cancelar. Ao receber `RunEnd(Surrender)`: ligar "fim em andamento" e chamar o original. |
| 8 | Host | `PlayerSeekerAIComponent.DoOnUpdate` (0x7D6F20, patch 13 da fase 3) | Ajuste | Ignorar o host quando ele está caído (hoje só ignora o cliente morto). |
| 9 | Host | `GameManagerUtil.GetRandomSpawnPointOutsideOfView` (patch 7 da fase 3) | Ajuste | Com o host caído, sortear todos os pontos em volta do cliente. Com o cliente caído, já fica só no host. |
| 10 | Ambos | Âncora da câmera (sem patch) | — | GameObject do mod criado no `EnterDowned` e movido a cada frame para o fantasma. Ao reviver: `SetEntityTarget(salvos)` e só então destruir. |
| 11 | Opcional | `XpDropper.OnUpdate`/`OnLateUpdate` (0x7640B0/0x763DA0) | Prefix → false enquanto caído | Evita level-up caído (que pausa o parceiro). Deixar para depois do MVP. |

**Revive** (host decide e manda `Revive(alvo)`, e cada lado aplica no próprio jogador): passos de 2.2.

### 5.3 Pacotes de rede

- **Reusar `ClientState = 13`** (cliente → host): o `dead u8` passa a significar "caído **ou** morto" (o proxy já reage). Opcional: um byte `state` (0 de pé, 1 caído, 2 morto final).
- **`Revive = 5`** (já reservado no `PacketRouter` e sem uso), host → cliente, ReliableOrdered: `target u8` (0 host, 1 cliente). O cliente só aplica quando `target == 1`. Ele serve também para o cliente atualizar o próprio OnGUI quando o host levanta.
- **`CoopLife`** (novo), host → cliente, ReliableOrdered, ao mudar e a ~5 Hz enquanto alguém está caído: `hostDowned u8 | clientDowned u8 | reviveTarget u8 | progress f32`. Alimenta o OnGUI do cliente ("revivendo 40%") e a câmera de espectador.
- **`RunEnd`** (novo), host → cliente, ReliableOrdered: `kind u8` (Defeat, Victory, Surrender, PortalHome, mais tarde PortalEndless/BossRush) e `mapId string` opcional para a troca de cena futura.
- **`RunEndRequest`** (novo), cliente → host, ReliableOrdered: `kind u8` (Surrender, PortalHome). O host valida (a run ainda está viva) e manda `RunEnd`.
- Opcional, para a UI: **`ObjectiveSync`**, host → cliente (`MainObjective.Current/Total`), assim a barra de boss do cliente anda junto.
- Números: hoje o último usado é 13. A fase 4C está mexendo no mesmo `PacketRouter` (pasta `src/SoulstoneCoop/Skills/` ainda não versionada), então pegue os próximos livres na hora de implementar, não os fixe agora.

### 5.4 Ordem de implementação

1. **Caído local + câmera** (patches 1, 2, 3 e 10), com uma tecla de debug "forçar golpe letal" em cada janela. Critério: cai, fica ragdoll, inimigos largam, a câmera segue o parceiro e não sai "You died".
2. **Revive por proximidade no host** (máquina de estados no patch 4), mais `Revive` e `CoopLife`, mais o OnGUI dos dois lados. Critério: os dois revivem um ao outro e as magias voltam.
3. **Derrota conjunta**: os dois caídos → `RunEnd(Defeat)` → morte final igual à do jogo → "You died" e tela final nas duas janelas.
4. **Vitória** (patch 4 + item 5) e **portal Home** (patch 6).
5. **Desistir** (patch 7) e regra de desconexão.
6. Ajustes no host para quando ele está caído (patches 8 e 9).
7. Depois: sincronização de cena (Endless/BossRush/Reiniciar) e patch 11.

### 5.5 Riscos

1. **NRE dentro de `UpdateAll`.** O patch 1 roda dentro de praticamente todo sistema de dano (skills, DoTs, `HealthComponent.UpdateAll` no regen). Prefix e finalizer com try/catch próprio, sem alocar, e o finalizer **sempre** restaura `ReviveOnDeath`. `EnterDowned` não pode lançar exceção: chame-o dentro do finalizer protegido, ou adie para o tick do mod (flag pendente). Prefira adiar o que não é crítico (câmera, OnGUI, rede).
2. **Câmera com Transform destruído**: NRE no `PlayerCameraController.UpdateAll`. Use a âncora (item 10).
3. **`RegisterEntity` duplicado** gera `LogError` e falha. Cheque `EntityRegistry.TryGetEntity(entity.Id)` antes.
4. **Unregister no meio de um golpe:** o ramo nativo tira a entidade do `AllEntities` dentro do `ChangeEntityHealth`, que pode estar sendo chamado por alguém iterando a lista (AoE). É o comportamento do jogo para quem tem `ReviveOnDeath`, mas vale observar se algum golpe "pula" um inimigo no frame do caído.
5. **Ragdoll ausente** (skin mod ou `ImmuneToRagdollEffect`): sem visual de queda e com autocast vivo. Por isso `ForceStopAutoCasting` e `SetMovementMode(None)` explícitos.
6. **Corrida revive × derrota:** só o host decide, e com histerese de 0,5 s.
7. **Inimigos fixos no `PlayerEntities[0]`** (PlayerSeekerAI, `EnvironmentalDamageOnTriggerEnterFollowPlayerSkill`, meteoros) continuam mirando o host caído. Ele está invulnerável, então só desperdiçam ataque (patch 8 corrige o seeker).
8. **Level-up caído** pausa o parceiro (patch 11 opcional).
9. **Câmera lenta local** do death guard (`TimeUtil.DeathGuardTime`) e da `DeathSlowTime` só afeta o `timeScale` de quem a disparou, como já acontece hoje.
10. **Hack do proxy em Summons durante o loot** (fase 3): continua necessário. Qualquer código novo que percorra "jogadores por time" precisa lembrar do proxy sem Inventory.
11. **Mapa sorteado** no portal Endless/BossRush e no Reiniciar: dessincroniza a próxima fase até existir sincronização de cena.
12. **Contador de kills no cliente:** as mortes das marionetes passam por `ChangeEntityHealth` e somam `GameStats` no cliente. Por isso os `ProgressObjectives` do cliente andam e tentam `SpawnBosses`, que já está bloqueado pela fase 1. Não atrapalha a vitória por comando do host.

### 5.6 Como testar com duas janelas

- Teclas de debug (só em build de teste): **F9** = golpe letal no jogador local (`ChangeEntityHealth(null, local, -1e9)`, passando pelo patch 1); **F10** = forçar `RunEnd(Victory)` no host; **F11** = revive imediato.
- Roteiro:
  1. Cliente aperta F9: o cliente cai, a câmera do cliente segue o host, sem "You died". No host, o log do proxy mostra `dead=true` e os inimigos largam o cliente.
  2. Host anda até o corpo: a barra sobe nas duas janelas, o cliente levanta com a animação de levantar, as magias voltam e o proxy volta a ser alvo.
  3. Repetir com o host caindo: o cliente revive o host, e no host os inimigos vão para o cliente.
  4. Os dois apertam F9: "You died" e tela final nas duas janelas, com tempos de partida iguais.
  5. F10 no host: o apocalipse e os portais aparecem nas duas janelas. Um entra no Home, e a tela final aparece nas duas.
  6. Desistir pelo menu de pausa do cliente: tela de derrota por desistência nas duas.
  7. Fechar o cliente com o host caído: o host morre de verdade (regra solo).
- Logs a acompanhar: `[coop][life] downed/revived/runEnd`, exceções no `BepInEx/LogOutput.log` dos dois lados, e avisos de Animator (para confirmar que o `Cowering` não está sendo chamado fora da janela do patch 1).

---

## Resumo

- A morte do jogador é decidida em `UnitUtil.ChangeEntityHealth` (0x73BE30): death guard primeiro, depois `HasBeenKilled` + ragdoll permanente (`SetToRagdoll(-1)`) + reset de ascensões.
- O fim da run é o loop de `SurvivorsGameManager.UpdateAll` (0x438DC0). No caminho B, `GameData.Players` só tem o jogador local, então morrer é sempre "todos mortos": `SaveFinalGameStats`, depois `DeathSequenceRoutine` ("You died" via `LoseScreenPanelReference.ShowLoseScreen`), depois `ShowEndingScreen`.
- O jogo já tem um estado de caído nativo, `HealthComponent.ReviveOnDeath`: vida em 1, fora do `EntityRegistry`, sem `HasBeenKilled` e sem game over. O `Surrender` já trata esse caso.
- Proposta: ligar `ReviveOnDeath` só durante o `ChangeEntityHealth` do jogador local enquanto o parceiro está de pé, e no caído usar ragdoll, invulnerabilidade e `ForceStopAutoCasting`.
- Revive: `DelayToRevive = 0`, vida a 50%, 2 s de invulnerabilidade, `RegisterEntity` e `RagdollComponent.SetToAnimator` (levanta e devolve o movimento).
- Espectador: `PlayerCameraController.SetEntityTarget(âncora, null, null)`, como nas cutscenes do jogo, com uma âncora própria (nunca o transform do fantasma).
- Autocast e input não checam morte: quem segura é o ragdoll e o `MovementMode`. Ligar `ForceStopAutoCasting` e pular o `PlayerCharacter.UpdateAll` enquanto caído.
- Fim sincronizado com o host como autoridade:
  - Derrota: os dois caídos geram `RunEnd(Defeat)` e a morte final igual à do jogo.
  - Vitória: o cliente faz `MainObjective.Current = Total`.
  - Portal Home: prefix em `OnPlayerEnterPortal`, chamando local só com `EnterPortalObjective` pronto.
  - Desistir: prefix em `Surrender`.
- Pacotes: reusar `ClientState` e `Revive = 5`, e criar `CoopLife`, `RunEnd` e `RunEndRequest` com números livres na hora (a 4C mexe no mesmo router).
- Fora do MVP: portais Endless/BossRush e Reiniciar sorteiam o mapa em cada máquina e precisam de sincronização de cena. O maior risco técnico é qualquer exceção nos patches que rodam dentro do `ChangeEntityHealth` e da câmera.
