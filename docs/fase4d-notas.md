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

## Pendências (o usuário pediu pra voltar depois)

- **Não testados (entraram no commit sem rodar em jogo):**
  - a vida em u16 no snapshot;
  - minérios e cristais de cura. Eles **não são do time inimigo**: no F7 o minério nasceu no host e não foi rastreado. Agora o `HostEnemySync.IsNeutralSpecial` rastreia entidades fora de Players/Summons cuja chave seja `res:Minerals/` ou `res:Pillars/`, e loga uma vez cada entidade não rastreada com o time dela.
- **Não vistos em jogo:**
  - a barra de boss sumindo ao matar (`released` ficou em 0 porque os bosses não foram mortos nos testes);
  - as invocações de boss (`sum:`);
  - o objetivo "Eliminate X" e o próximo objetivo de kills no cliente.
- **Pendência da 4A, ainda aberta:** o cliente revivendo o host (F9 + 3 s perto) não foi testado.
- **God Meteors:** a God-Entity lança em volta do host e o cliente não vê. A ideia é um evento `GodCast(posição)` com cast local na God-Entity do cliente.
- **Baús e MapTasks** continuam só no host (interativos, não `Entity`).
- **Volume do replay:** só bosses, elites e especiais. Os inimigos comuns continuam sem animar o ataque.

## Pacotes

`EnemyCast = 21`. A 4D usa só o 21; de 22 a 29 continua livre.
