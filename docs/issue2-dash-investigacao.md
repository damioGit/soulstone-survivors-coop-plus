# Issue #2: dash do parceiro sem animação (investigação)

Pergunta: por que o fantasma do parceiro só desliza no dash, e o que o jogo faz no jogador de verdade.
Fontes: `tools/il2cpp_dump/dump.cs`, decompilação no Ghidra (read-only) das RVAs abaixo e as strings dos
AnimatorControllers em `Soulstone Survivors_Data/resources.assets`.

## 1. O dash no jogador de verdade

### `PlayerCharacter.Dash(bool isMouseDash)` (0x7A8450)

- Sai sem fazer nada se `UnableToDash` (0x84), se já está em dash (`_isDashing`, 0x12C), se `_ongoingDashes` (0x120) >= `DashMaxCount`
  ou se o modo de movimento não é 1/2.
- Liga `_isDashing = true`, conta a carga, `_dashRemainingDuration = _dashDuration` (0x7C), toca o som
  `AudioUtil.PostEventFireAndForget("PlayerDash", gameObject)`, guarda o RotationMode e troca para o modo 0.
- Direção: a da entrada de movimento (0xE8) ou `PlayerCameraController.GetMouseDashDirection`; sem entrada, a frente do
  Rigidbody. Velocidade de dash = direção normalizada x `_dashImpulse` (0x78), com bônus de runa.
- Runa `RecoverHealthOnDash`: cura.
- **Não chama nada no Animator**: nem Play/CrossFade, nem SetTrigger.

`ModifiedCharacter.StopDashing` (0x7A6110) desliga `_isDashing`, zera a duração e volta o RotationMode.

### Animator: `PlayerAnimator.OnUpdate` (0x7A7A20) e `.cctor` (0x7A82E0)

Os hashes do `.cctor` saem das strings: `Ground` = "OnGround", `Jump` = "Jump", `Dash` = "Dash",
`ForwardMovement` = "ForwardMovement", `SidewaysMovement` = "SidewaysMovement".

Em `ModifiedCharacter.Animator` (0xE0, que é o `AnimatorComponent.Animators[0]` do modelo), a cada frame:
- `SetFloat(ForwardMovement / SidewaysMovement, ..., 0.1, dt)`: velocidade local / `GetMaxSpeed()`;
- `SetBool(Dash, _isDashing)`;
- `SetBool(OnGround, ...)` e, no modo 2, `SetFloat(Jump, ...)`.

Só isso. O bool `Dash` fica ligado enquanto durar o dash e nada mais é escrito.

### Os controllers não têm estado de dash

Nas strings dos AnimatorControllers do `resources.assets` (a tabela de nomes de cada controller tem os parâmetros e os
caminhos `Layer.Estado` e `A -> B`):
- o parâmetro `Dash` aparece em todos os controllers de personagem, por exemplo o `ArcaneWeaver-Controller`, e nos de
  inimigos humanoides que herdaram a mesma base;
- **não existe nenhum estado nem transição com "Dash" no nome**: zero ocorrências de `Layer.Dash`. Os estados de base
  são `Grounded` (o blend de locomoção), `Airborne`, `Death`, `Knockdown`, `GettingUp`, `Recover-*` e `Ground-Ragdoll-*`,
  mais as camadas `Torso Layer`, `Full Body Layer` e `Cape` com as skills;
- o único `Base Layer.Roll` é de VFX (pedra rolando etc.).

Um bool não move blend tree. Sem estado de dash, ele não tem efeito visível. No jogador de verdade, o personagem
continua no blend `Grounded`: na velocidade do dash ele fica no `ForwardMovement` = 1, ou seja, correndo.

### O efeito visual: `DashEffect` (no Entity)

`PlayerPrefabReferences.BindMeshToDashEffect` (0x811FC0) pega o `DashEffect` com `playerEntity.gameObject.GetComponent<DashEffect>()`.
O componente fica **no GameObject do Entity**, não no modelo. Os renderers vêm do modelo:
`GetComponentsInChildren<SkinnedMeshRenderer>()`, `GetComponentsInChildren<MeshRenderer>()` e o `MeshFilter` de cada
MeshRenderer. Depois ele chama `DashEffect.Setup(CharacterDefinition.GetAccentColor(personagem do perfil local))`.
`UpdateDashEffect` (0x813780) repassa os arrays com `UpdateRenderers` (0x753F10), que marca `_meshDirty`. Isso acontece, por
exemplo, depois que as armas aparecem.

- `Start` (0x753970): `_dashLayer = NameToLayer("PlayerDash")`, `_characterLayer = NameToLayer(...)`, e `Setup` se `AutoSetup`.
- `Setup(Color)` (0x753540): cria o `_combinedMesh` com índice UInt32, o `MaterialPropertyBlock`, `GetComponent<ModifiedCharacter>`
  e `<CharacterLayerComponent>`, os ids de `ColorProperty` e `FadeProperty` e um pool de `PoolSize` instâncias:
  `Instantiate(DashEffectPrefab)` sem pai, `MeshFilter.sharedMesh = _combinedMesh`, `SetColor(ColorProperty, cor)` no
  property block do `MeshRenderer` e `SetActive(false)`.
- `UpdateAll` (0x7539F0), a cada frame e para cada `DashEffect`:
  - `_spawnTimer -= Time.deltaTime`, sempre;
  - em dash: layer do GameObject do Entity = `_dashLayer`; fora dele, volta ao do `CharacterLayerComponent`. Isso é só
    física, porque os renderers ficam nos filhos e não mudam de layer;
  - em dash, com `_spawnTimer < 0` e `|Rigidbody.velocity| > 0.1`: `_spawnTimer = TimeBetweenImages` e `CreateAfterImage()`;
  - cada imagem ativa: `restante -= deltaTime` e `SetFloat(FadeProperty, restante / FadeSpeed)`. Com `restante <= 0`,
    `SetActive(false)` e volta ao pool.
- `CreateAfterImage` (0x752CF0): pool vazio = nada. Se `_meshDirty`: `BakeMesh(mesh novo, useScale: true)` de cada SMR e
  `CombineInstance.transform = entity.worldToLocalMatrix * renderer.localToWorldMatrix`. Os MeshRenderers entram com o
  `sharedMesh` e o `transform.localToWorldMatrix`. Depois `_combinedMesh.Clear()`, `CombineMeshes` e
  `_meshDirty = false`. Em seguida, tira uma instância do pool: `SetFloat(FadeProperty, 1)`, posição, rotação e
  `localScale` iguais aos do Entity, `SetActive(true)`, `restante = FadeSpeed`.

Ou seja, o "FadeSpeed" é a vida da imagem em segundos. A pose só é refeita quando `_meshDirty` liga, então todas as
imagens de um dash têm a mesma pose. Não há partícula, trail nem troca de material no próprio modelo.

Skins 2D (`CharacterSkin2DController`) usam outro rastro, o `SpriteTrailRenderer.PlayDashEffect`. O fantasma nunca usa
skin 2D: `VisualGhostFactory` cai no modelo 3D padrão.

## 2. Por que o fantasma não mostra

1. **O `DashEffect` foi removido.** O fantasma é `Instantiate` do Entity local com tudo que não é visual destruído
   (`StripToVisualOnly`). O `DashEffect`, um MonoBehaviour no root, vai junto. Mesmo que ficasse, ele depende de
   `ModifiedCharacter._isDashing` e de `Rigidbody.velocity`, e o fantasma não tem nenhum dos dois.
2. **O bool `Dash` não faz nada.** O mod já escreve `Dash` no animator certo: `RemotePlayer.ModelAnimator`, que é o
   `Animators[0]` do modelo, o mesmo em que o `PlayerAnimator` escreve. O parâmetro existe no controller. Só que nenhum
   estado usa esse bool, nem no jogador local.
3. A locomoção não atrapalha. Na velocidade do dash, o fantasma também fica com `ForwardMovement` = 1, igual ao jogador
   de verdade.

O evento chega: `DashState = 54`, ReliableOrdered, aplicado com o mesmo `InterpDelay` das posições, e conta em
`dashes=enviados/recebidos` na linha `[sync]`.

## 3. Varia por personagem ou arma?

- **Animator:** cada personagem tem o seu controller, mas todos têm o mesmo `Dash` sem estado. Não há nada a tocar no
  Animator por personagem. Um `AnimatorOverrideController` só troca clips, não acrescenta estados.
- **`DashEffect`:** fica no Entity do jogador, um prefab só para todos os personagens. Os parâmetros (prefab, intervalo,
  vida, propriedades, pool) são os mesmos. Por personagem mudam só os renderers do modelo, incluindo os da arma, e a cor
  (`CharacterDefinition.GetAccentColor(CharactersType)`).

Para cobrir todos, o fantasma lê os parâmetros do `DashEffect` do Entity local, já que todos usam o mesmo prefab. Os
renderers vêm do modelo do próprio fantasma e a cor vem do `CharacterId` do parceiro.
