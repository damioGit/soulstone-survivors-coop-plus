# Issue #2: dash do parceiro (notas)

Investigação completa em `docs/issue2-dash-investigacao.md`.

## Causa

O jogo **não tem animação de dash**. O `PlayerCharacter.Dash` não chama nada no Animator, e o bool `Dash` que o
`PlayerAnimator` escreve existe nos controllers, mas nenhum estado o usa: não há `Layer.Dash` em controller nenhum. No
jogador de verdade, o que se vê é o personagem correndo mais o **rastro de after-images do `DashEffect`**: cópias do
modelo na cor de destaque do personagem, que vão sumindo.

O `DashEffect` é um componente do GameObject do **Entity**, não do modelo. O fantasma é um clone do Entity sem os
scripts (`StripToVisualOnly`), então ele sumia junto. Mesmo que ficasse, ele lê `ModifiedCharacter._isDashing` e
`Rigidbody.velocity`, e o fantasma não tem nenhum dos dois. Resultado: no fantasma, o bool `Dash` chegava (e continua
chegando) sem efeito, e o personagem só deslizava.

## Correção

`src/SoulstoneCoop/GhostDashTrail.cs`: o `DashEffect` refeito só com visual, por fantasma.

- **Configuração:** a do `DashEffect` do Entity local (`DashEffectPrefab`, `TimeBetweenImages`, `FadeSpeed`, `PoolSize`,
  `ColorProperty`, `FadeProperty`). O Entity é o mesmo prefab para todos os personagens.
- **Cor:** `CharacterDefinition.GetAccentColor(personagem do parceiro)`. O jogo usa a do personagem do perfil, então
  cada fantasma fica com a cor do seu personagem.
- **Pool (como o `Setup`):** `PoolSize` cópias do prefab, com o mesh combinado, a cor no property block e inativas. Elas
  ficam num holder na cena (`SoulstoneCoop_DashTrail<slot>`). O jogo também instancia sem pai.
- **Mesh (como o `CreateAfterImage`):**
  - os `SkinnedMeshRenderer` do modelo do fantasma passam por `BakeMesh(useScale: true)`;
  - os `MeshRenderer` entram com os seus meshes;
  - tudo vai para o espaço do root do fantasma, num mesh só (índice UInt32);
  - só a subárvore do modelo entra, então o anel do chão e o nome ficam de fora;
  - o mesh é refeito a cada início de dash, então cada dash tem a pose daquele momento. O jogo só refaz quando os
    renderers mudam;
  - mesh sem Read/Write fica de fora, senão o `CombineMeshes` inteiro falha.
- **Ritmo (como o `UpdateAll`):** em dash, a cada `TimeBetweenImages`, se o fantasma se move a mais de 0,1/s, sai uma
  imagem na posição, rotação e escala do root. Cada imagem vive `FadeSpeed` segundos, com `FadeProperty` = restante/vida.
  Os timers usam `Time.deltaTime` como no jogo: na pausa, as imagens congelam.
- **Quando:** é o mesmo evento `DashState` de antes, já aplicado com o `InterpDelay`, então o rastro sai junto com o
  deslocamento do dash.
- **Quando nada é criado:**
  - nada é criado até o primeiro dash do parceiro;
  - fantasma recriado ou jogador que sai: o rastro é destruído;
  - troca de cena: a cena leva as imagens e o pool é refeito no próximo dash;
  - erro: o rastro daquele fantasma desliga com um aviso e o resto continua.
- O bool `Dash` continua sendo escrito no `ModelAnimator`, igual ao jogo, mesmo sem efeito.

**Formato de rede:** não mudou. Nada novo é enviado: a cor sai do `CharacterId` que já vem no `Hello`.

**Telemetria:**
- a linha `[sync]` ganhou `trail=N`, o total de imagens que os fantasmas mostraram;
- `[dash] Dash trail template: ...` sai uma vez, com os valores lidos do `DashEffect` local;
- `[dash] Ghost N dash trail: X skinned + Y mesh renderer(s) ...` sai no primeiro dash de cada fantasma.

**Fora:**
- o som do dash do parceiro (`PlayerDash`);
- o layer `PlayerDash` do Entity durante o dash, que é só física;
- o rastro de skin 2D (`SpriteTrailRenderer`), porque o fantasma nunca usa skin 2D.

## Roteiro de teste

Dois jogadores com personagens **diferentes**, por exemplo um de arma corpo a corpo e um mago.

1. **Dash parado:** A fica parado e dá dash. Na tela de B, o fantasma de A avança deixando 3 a 6 cópias na cor do
   personagem de A, que somem em menos de meio segundo. Compare com o dash de A na tela de A: tem que ser o mesmo rastro.
2. **Dash correndo:** A corre e dá dash em várias direções, inclusive para trás. O rastro segue o caminho e o fantasma
   volta a correr normal no fim.
3. **Trocar os papéis:** B faz o mesmo. Na tela de A, o rastro tem a cor e o modelo do personagem de B, com a arma.
4. **Pausa e troca de mapa:** dê dash, pause logo em seguida (as cópias congelam) e despause. Passe por um portal e dê
   dash no mapa novo: o rastro continua aparecendo.
5. **Log:** confira se `[dash] Dash trail template` e `[dash] Ghost N dash trail` aparecem, se `trail=` sobe junto com
   `dashes=` na linha `[sync]` e se nenhum `dash trail off` aparece.
