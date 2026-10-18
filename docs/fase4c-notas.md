# Fase 4C: magias do outro jogador (só visual)

Testado com duas instâncias no mesmo PC (Desert, mesmo personagem nos dois lados). O usuário confirmou que as magias do outro jogador aparecem nas duas janelas. A investigação está em `docs/fase4c-investigacao.md`.

## O que entrou (`src/SoulstoneCoop/Skills/`)

- **Captura (`CastSync`):** postfix em `SkillCasterComponent.CastSkill` filtrado por ponteiro (caster = `SkillCasterComponent` do jogador local, `__result == true`). Cada cast vira um `SkillCast = 14` com estes campos:
  - chave do prefab (`Buckshot-Skill`), `SkillType`, nível;
  - quantidades (`ProjectilesToFire` / `AreasToSpawn` / `Min/MaxProjectilesQuantity`);
  - escala de área do remetente (`UnitUtil.GetAdjustedScale`) e yaw;
  - os quatro vetores dos parâmetros, flags dos delegates (channeling, front position);
  - o alvo: si mesmo, inimigo por netId (`HostEnemySync` / `ClientPuppets.TryGetNetId`) ou o outro jogador (o proxy no host).

  Fica em torno de 35 B por cast. Channeling manda `SkillChannelEnd = 15` quando o func do auto-cast (ou a própria skill) para.
- **Entrega:** os dois pacotes vão **ReliableOrdered**, no mesmo canal, porque o fim de um channel não pode chegar antes do início. Com casts de ~35 B e poucos por segundo, o head-of-line blocking não pesa.
- **Lançador remoto (`RemoteCaster`):** é um `Creeps/Dummy` por lado, pela receita do `RemoteProxy`, configurado assim:
  - time **Summons**;
  - `IgnoredByEntitySearchQueries`, sem colisor, sem renderers, sem IA;
  - segue o fantasma a cada frame.

  Cada chave nova vira uma cópia da skill como filha dele, com `Skill.Setup(lançador, …)` + `FillPools(3)` e sem `AddSkill`. O cast recebido roda `CastSkill(cópia, params, SkillActivationOptions.IgnoreAll)`: sem cooldown, sem procs, sem chain e sem multicast extra, porque esses chegam como casts separados. A escala de área é resolvida nos `Modifiers` da cópia, para bater com a do remetente.
- **Bônus:** depois do cast, `CrossFade` da `AnimationTrait` da skill no Animator do fantasma, se o estado existir.
- **Bloqueio (as réplicas não fazem nada no jogo):**
  - prefix `Priority.First` em `UnitUtil.ChangeEntityHealth`: atacante ou alvo = lançador → false;
  - prefix em `AbnormalStatusComponent.ApplyEffects(applyingEntity, …)`: status e DoTs;
  - postfix em `UnitUtil.CalculatePushbackForceMultiplier`: empurrão zerado quando os stats são do lançador;
  - os dois patches de `ChangeEntityHealth` da fase 3 (clamp das marionetes e proxy) ignoram o lançador, então nada dele entra no `ClientDamage`.
- **Telemetria `[casts]` a cada 5 s:** sent (bytes), recv, played, failed (noCaster/noSkill/rejected/error), skippedSummon, captureErrors, channelEnds, openChannels, blockedEffects, blockedDamage, caster e copies.

## Medido no teste

- `played` de 2 a 5 por janela de 5 s nos dois lados, `failed` = 0 (um `noCaster` no início, antes de o lançador existir), `captureErrors=0` e nenhum erro de trampoline.
- `blockedDamage` = `blockedEffects` (2 a 35 por janela): os hits das réplicas acertam e são barrados.
- Sem dano em dobro: o `ClientDamage` do cliente (29 300 + 40 074) bate com o que o host aplicou (21 522 + 49 292; as janelas de 5 s cortam em pontos diferentes). O host teve 32 mortes, 20 creditadas ao cliente, e o resto foi das magias do próprio host.
- `SetupExtraSkillForEntity` deu NRE dentro do jogo para `Buckshot` e `PowerBlast` (avisa uma vez por chave). O fallback manual (`Instantiate` + `Skill.Setup` com `Nullable<float>`/`Nullable<bool>` vazios) funcionou nos dois lados.

## Regra do Il2CppInterop (vale para qualquer patch novo)

**Não patchar método que recebe struct não-blittable de 1, 2, 4 ou 8 bytes por valor** (`Nullable<int>`, `Nullable<float>`, `Nullable<bool>`, struct com um campo de referência…). A ABI x64 passa esses structs no registrador, mas o trampoline native→managed do interop lê como ponteiro. O resultado é um NRE em `Buffer.Memmove`, e o **original não roda**. O primeiro patch em `Skill.ApplyEffects`, que termina num `Nullable<int>`, desligou todo efeito de toda skill (`died=0`). Structs maiores vão por ponteiro e funcionam: `SkillActivationParameters`, `SkillActivationOptions` (9 B), `EntityByEnemyPrefabData`, `AbnormalStatusApplyData`, `Vector3`. Antes de patchar, confira o tamanho de cada struct no `dump.cs`.

Correlato: os getters e setters de `Nullable<Vector3>` dentro de `SkillActivationParameters` dão NRE em `CreateGCHandle`, porque o class pointer do genérico não é resolvido. Por isso a leitura e a escrita são feitas direto no struct (`Skills/ActivationParamsRaw.cs`, offsets do `dump.cs`).

## O que ainda não aparece

- **Summons:** skills com `SkillType.Summon` não são replicadas, porque o summon seria uma Entity de verdade causando dano. Um `SummonEntityEffect` dentro de uma skill que não seja Summon passaria, mas não vi nenhum.
- **Auras e escudos giratórios** que ficam ligados sem passar por `CastSkill`.
- **Mira contínua de channeling:** o `GetTargetFrontPosition` do receptor devolve o ponto do início do cast.
- **Buffs em aliados** aplicados por efeito de skill (se existirem) não são bloqueados. Só dano, status e empurrão são.
- Os números de dano das réplicas podem aparecer (o hit roda até o `ChangeEntityHealth`, que devolve 0).
- O fantasma continua sendo um clone do jogador **local**, então a animação de cast só toca quando os dois usam o mesmo personagem. (Resolvido na fase 5: `docs/fase5-notas.md`.)

## Pacotes

`PacketType` vai até **15**: `SkillCast = 14` e `SkillChannelEnd = 15`. As próximas fases começam no **16**.
