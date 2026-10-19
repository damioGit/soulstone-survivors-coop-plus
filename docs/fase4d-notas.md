# Fase 4D: chefes e inimigos especiais no cliente

Testado com duas instâncias no mesmo PC (Desert). A investigação está em `docs/fase4d-investigacao.md`. O usuário confirmou que funcionou e pediu pra deixar o resto como pendência.

## O que entrou

- **Chave `res:<caminho>`** (`EnemyPrefabKeys`): o host monta um índice nome → caminho de `Resources` a partir dos dados do jogo:
  - `Global.BossPrefabs` (`Bosses/` e `Bosses/Ethereals/`);
  - `Global.VoidHunterPrefabs` (`Creeps/` + endereço);
  - os endereços do affix de entidades especiais;
  - o `_prefabAddress` do loot goblin.

  Se o nome não estiver no índice, o host sonda as pastas `Creeps/`, `Bosses/`, `Bosses/Ethereals/`, `Minerals/` e `Pillars/` e só aceita o mesmo prefab. O cliente carrega com `ResourcesUtil.Load<Entity>`.
- **Chave `sum:<netId do dono>:<nome>`:** um prefix em `UnitUtil.SetupSummonStats` (`HostSummonCapturePatch`) captura o dono e o prefab original de cada invocação antes do `Entity.Setup`. O cliente procura o prefab nos `PossiblePrefabs` das skills de summon da marionete dona. Se o dono ainda não existe, o spawn espera até 3 s em `Pending`.
- **Receita dos especiais no cliente:** `CacheReferences`, `CopyStats` do prefab e `SetToOne`, como os spawners do jogo. Depois a vida máxima local é escalada para a do host (`MatchHostMaxHealth`), porque cada spawner aplica um scaling diferente. No primeiro teste, o VoidPresence tinha 4,25 M no host e 1 M no cliente; no segundo, 4,25 M nos dois.
- **Boss:**
  - O host captura os tiers num prefix de `SetupWithTier` (Plaguebringer, MasteryBoss e BossAIBasedOnCurseLevel) e lê `MultiplicativeStats.MaxHealth` e o `_pendingBosses` do `BossSpawnProcessor`. Tudo vai num bloco opcional do `EnemySpawn`.
  - O cliente chama `SetupWithTier` e põe o multiplicador de vida antes do `Setup`. A barra grande entra sozinha (`PlaguebringerComponent.Setup`), sem barra overhead.
  - O cliente espelha o contador de bosses pendentes e cria o objetivo "Eliminate X". O boss **não** entra no `MainObjective`: o fim da run continua com o `RunEnd` da 4A.
  - `ReleaseBoss` chama `GameManagerUtil.DeregisterBossEntity` na morte, no despawn e no recycle, uma vez só.
  - `BossSpawnProcessor.SpawnEtherealEntity` é bloqueado no cliente.
- **Replay do ataque (`EnemyCasts`, `EnemyCast = 21`, ReliableUnordered):**
  - No host, um postfix em `SkillCasterComponent.CastSkill` pega bosses, elites e especiais (`Tracked.ReplayCasts`), com no máximo 10 casts/s por inimigo e sem skills de summon. A mensagem leva o nome da skill, o yaw, os vetores e o alvo (o próprio inimigo, outro inimigo, o host ou o cliente).
  - No cliente, `CastSkill(skill, params, IgnoreAll)` roda na marionete, então aparece o aviso de carga (`SkillChargingTimeTrait`). Quando o alvo é o host, usa o `RemoteCaster` como alvo.
  - Bloqueios no cliente: `ChangeEntityHealth` com atacante marionete (exceto o `DamageToClient` do host), status e empurrão. Isso também tira o dano local das skills de morte das marionetes.
- **Snapshot:** a vida passou de u8 para u16 (11 B por entrada, 107 por fatia). Com u8 os números das barras de boss diferiam em cerca de 1/255: host 425.124 contra cliente 424.746.
- **Hotkeys de debug, só na janela do host:** **F7** spawna perto do host VoidPresence, void hunters, um minério e um pilar. **F8** completa o objetivo de kills (soma no `GameStats`) e o jogo spawna o boss pelo caminho normal.
- **Telemetria:** `[enemycasts]`; `[puppets] bosses created/released/errors bossBars summonRetries maxHealthMatched`; e no host o log `special '<chave>'` para cada especial novo.

## Medido no teste

- VoidPresence (`res:Creeps/VoidPresence-Survivors`), void hunter (`res:Creeps/Barbarian-NPC`, elite), pilar (`res:Pillars/Pillar-Protection-Survivors`) e os bosses `res:Bosses/Slayer`, `Medusa`, `Plaguebringer` e `Avatar-of-Ice` viraram marionetes, com `unresolved=-`. Os bosses tinham a mesma vida máxima nos dois lados, e a barra grande aparece no cliente.
- O replay funcionou com `charging=True` em todas as skills de boss vistas: Medusa LightningStrike/LightningExplosion, Slayer Cleave/ShieldBash, AvatarOfIce Cleave/IceBomb e TormentedSoul-Attack do VoidPresence. Ficou com `rejected=0`, `errors=0`, e os hits das réplicas foram barrados (`blockedDamage`/`blockedPush`).
- Nenhuma exceção nova e nenhum erro de trampoline. Só os avisos conhecidos de `SetupExtraSkillForEntity` da 4C.

## Bug da 0.7.0: "0/15 bosses" no cliente a partida inteira

Visto pelo usuário jogando com um amigo (Steam). O fim da run chegou nos dois (RunEnd da 4A), mas o contador de bosses do cliente ficou em 0/15.

- **Qual objetivo:** é o `SurvivorsGameManager.MainObjective`, um `EliminateSpecificEntitiesObjective` com Total = número de bosses da `BossList` (`<CreateObjectives>d__37`, 0x43FE80).
- **Causa:**
  - O `Current` dele só sobe em `ObjectivesPanelReferences.RunEliminateSpecificEntitiesObjective` (0x618670): +1 para cada entidade da **própria lista** (`EntitiesToEliminate`) que morreu, que sai da lista em seguida. `IsComplete` (0x612B60) é `Total <= Current`.
  - No host, o `SpawnBossRoutine` põe cada boss nessa lista. No cliente, a 4D deixou as marionetes de boss **fora** dela de propósito, para o fim da run continuar com o host. Resultado: a lista do cliente ficava vazia e o contador parado.
  - O descompilado mostra que nada recalcula o `Current`: um valor escrito por fora fica.
- **Correção (`Enemies/ObjectiveSync.cs`, `ObjectiveSync = 22`, host → cliente, ReliableOrdered):** quando algo muda (checado a cada 0,25 s) e a cada 2 s, o host manda as kills do `GameStats` (inimigos, elites e bosses), o `GameData.Tier`, o `Progression.CompletedProgressObjectiveProgression`, o `MainObjective` Current/Total e se ele tem um objetivo de kills ativo. O cliente:
  - copia as kills para o `GameStats` dele. O `UpdateAll` do próprio cliente calcula o objetivo de kills a partir delas (`EliminateEnemiesNonCumulativeObjective`: `Current` = kills, completo quando `Current - Starting >= Total`), então o contador e a conclusão seguem o host;
  - sobe o Tier e o número de objetivos completos até os do host (nunca baixa);
  - sobe `MainObjective.Current` **limitado a Total - 1**. O espelho nunca completa o objetivo, porque isso rodaria a vitória local; o fim continua só pelo `RunEnd`;
  - **rede de segurança do "próximo objetivo":** normalmente o `BossSpawnProcessor.OnUpdate` do cliente cria o próximo objetivo de kills quando o `_pendingBosses` espelhado chega a 0. Se o host já tem um, o cliente não tem e nenhuma marionete de boss está viva por 3 s, o cliente zera o contador de bosses pendentes e chama `CreateNextProgressionObjective`.
- **Telemetria `[objectives]` a cada 10 s:** sent/recv/applied, mainRaised, catchUps, errors, o estado do host e, no cliente, o `main` local, a quantidade de objetivos de kills e os bosses vivos.
- **Não testado em jogo** (compilado sem deploy). Passo de teste: host e cliente na mesma run. No host, F8 e matem os bosses. O painel do cliente deve mostrar o mesmo número de kills do host durante a run, o contador de bosses subindo a cada boss morto (até Total - 1; o último fecha pelo RunEnd) e um novo objetivo de kills logo depois de cada leva de bosses. No log do cliente, `[objectives] ... local main=X/15` deve bater com o `main=` do host.

## Correção: skills de boss que lançam pela God-Entity (ProxyGodSkill)

- **Risco** (achado pela sessão de pendências, confirmado no descompilado): `ProxyGodSkill.DoActivate` (0x55C5B0) não ataca sozinho. Ele chama `GameManagerUtil.TryCastGodSkill(GodSkillName, params, Options)`, e quem lança é a God-Entity do mapa. No replay do `EnemyCasts`, isso faria a God-Entity **local** do cliente lançar. Ela não é marionete e nenhum bloqueio da 4D se aplica a ela, então o cliente tomaria o golpe duas vezes: uma de verdade, vinda do host pelo proxy (`DamageToClient`), e outra local.
- **Correção:** um prefix em `ProxyGodSkill.DoActivate` no cliente (`ClientPuppetProxyGodSkillBlockPatch`) devolve false quando o `CasterEntity` é marionete. O bloqueio fica no `DoActivate`, e não em volta do `CastSkill` replayado, porque com carga ele roda atrasado, pela corrotina `Skill.ActivateChargedAttack`. A marionete continua mostrando a carga, o aviso e a animação; só a parte da God-Entity é pulada. O dano real continua chegando pelo `DamageToClient`. Contador: `blockedGodProxy` no log `[enemycasts]`.
- **Fica de fora (a sessão de pendências já trata):** mostrar no cliente o efeito que a God-Entity do host lança. O `GodCast = 70` dela replica os God Meteors pelo `RemoteCaster`. O que a God-Entity do host lança por um `ProxyGodSkill` de boss só aparece no cliente se esse caminho também for coberto.
- **Não testado em jogo** (compilado sem deploy). Passo de teste: num boss com `ProxyGodSkill`, o cliente tem que tomar só o dano que chega pelo host (um número por golpe), e `blockedGodProxy` deve subir no log do cliente.

## Pendências (o usuário pediu pra voltar depois)

- **Não testados (entraram no commit sem rodar em jogo):**
  - a vida em u16 no snapshot;
  - minérios e cristais de cura. Eles **não são do time inimigo**: no F7 o minério nasceu no host e não foi rastreado. Agora o `HostEnemySync.IsNeutralSpecial` rastreia entidades fora de Players/Summons cuja chave seja `res:Minerals/` ou `res:Pillars/`, e loga uma vez cada entidade não rastreada com o time dela.
- **Não vistos em jogo:**
  - a barra de boss sumindo ao matar (`released` ficou em 0 porque os bosses não foram mortos nos testes);
  - as invocações de boss (`sum:`);
  - o objetivo "Eliminate X" e o próximo objetivo de kills no cliente (agora com a rede de segurança do `ObjectiveSync`, também não testada).
- **Pendência da 4A, ainda aberta:** o cliente revivendo o host (F9 + 3 s perto) não foi testado.
- **God Meteors:** a God-Entity lança em volta do host e o cliente não vê. A ideia é um evento `GodCast(posição)` com cast local na God-Entity do cliente.
- **Baús e MapTasks** continuam só no host (interativos, não `Entity`).
- **Volume do replay:** só bosses, elites e especiais. Os inimigos comuns continuam sem animar o ataque.

## Pacotes

`EnemyCast = 21` e `ObjectiveSync = 22`. De 23 a 29 continua livre.
