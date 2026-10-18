# Fase 5: identidade do jogador (personagem, skin e nome certos)

**Commitada sem teste em jogo.** O usuário estava jogando quando foi feita. Só foi compilada (0 erros, 0 avisos). A investigação está em `docs/fase5-investigacao.md`.

## O que entrou

- **Fantasma com o personagem do parceiro (`VisualGhostFactory`).**
  - O Entity local continua sendo clonado, como antes, para manter o anel do chão e os efeitos do corpo. O filho `PlayerPrefabReferences` do clone (o modelo local) é destruído e trocado pelo do parceiro, montado a partir do `Hello` (`CoopSession.Remote`) pelos mesmos passos do `CharacterModelHolder.SpawnCharacter`:
    - `CharacterSkinCollection.TryGetCharacterSkinById`;
    - `CharacterSkinData.LoadCharacterSkin(false)` (`Resources "Characters/<skin>"`);
    - `Instantiate` com escala `CharacterSkinSize`;
    - `SpawnedCharacterId`, `SpawnedCharacterSkinId` e `WeaponDefinition`.
  - **Armas:** a rotina do jogo `CharacterModelHolder.SpawnWeaponsAsyncRoutine`, rodada na hora. No caminho sem mod ela termina no primeiro passo e só mexe no modelo. O `PlayerPrefabReferences.Setup` **não** é chamado, porque escreve no Entity.
  - O modelo é instanciado sob um objeto **inativo** e despido (só Transform, Animator e renderers, com `TryCast`) antes de ser ativado. Nenhum script de jogo dele chega a rodar, nem os `ChildEntitiesToSetup`. `applyRootMotion` fica desligado.
  - **Reservas:**
    - skin de mod, 2D, de outro personagem ou desconhecida: a skin padrão do personagem;
    - arma desconhecida: a arma padrão;
    - sem `Hello`, sem personagem ou se algo der exceção: o clone local de antes, com um aviso `uses the LOCAL character as fallback (motivo)`.
  - O fantasma guarda para qual identidade foi montado (`personagem|skin|arma`). Se o `Hello` chegar ou mudar depois, ele é remontado, no máximo 3 vezes por run. O `RebuildGhostIfModelInactive` continua e agora olha o Animator do modelo. Nada é clonado antes do `StartMatch` local (o ciclo de vida do fantasma da fase 6 não mudou).
- **Nome em cima do fantasma (`GhostNameTag`).** Um `TextMeshPro` 3D filho do fantasma, na altura do topo do modelo, na fonte do menu do jogo (a mesma que a UI de co-op acha), azul-claro com contorno preto. Ele vira para a câmera a cada frame e se atualiza se o nome mudar. Nomes acima de 24 caracteres são cortados com "...".
- **Locomoção (`PlayerSync`).** Na primeira vez, cada Animator do fantasma tem os parâmetros lidos (`Animator.parameters`). Só são escritos `ForwardMovement`, `SidewaysMovement` e `OnGround` que existem naquele controller. Isso inclui os Animators das armas e evita o aviso de "parâmetro não existe" a cada frame. Se a lista não puder ser lida, escreve os três, como antes. Log: `[sync] Ghost animator '<nome>' (<controller>): ForwardMovement=… SidewaysMovement=… OnGround=…`.
- **Lançador remoto (`RemoteCaster`).**
  - A cópia da skill vem direto de `Resources "Skill-Prefabs/<chave>"` + `Skill.Setup`, para qualquer personagem. Saiu a tentativa do `UnitUtil.SetupExtraSkillForEntity`: ela procurava a skill nos power-ups do personagem **local** e, para skill em comum, vazava uma cópia com NRE no Dummy (os NREs de Buckshot/PowerBlast da 4C).
  - Ao criar o lançador, as skills da arma do parceiro (`MainSkillId`, `OffhandSkillId`, `ArtifactPowerSkillId`) são pré-carregadas e logadas: `[casts] Partner plays <personagem> with '<arma>': X=ok, Y=ok`.
  - **Animação de cast:** o `CrossFade` da `AnimationTrait` vai para todo Animator do fantasma que tem o estado, como o `AnimatorComponent` do jogo faz. Estado ausente é logado uma vez por nome, e a telemetria `[casts]` ganhou `ghostAnim=tocadas/total`.

Nenhum pacote novo. A versão do mod e o `roadmap.md` não foram mexidos.

## Riscos (sem teste)

1. **Rotina de armas fora do contexto normal.** Ela roda sobre um modelo inativo que nunca passou pelo `FirstTimeSetup`. Um NRE ali é pego e o fantasma fica sem arma (log `weapon spawn failed`). Também chama `ModManager.CancelModelLoadingProcessing()`, que no meio da run não deve ter nada para cancelar.
2. **`Instantiate` sob pai inativo.** Se algum renderer depender de um script que despimos (por exemplo, combinar malhas no `Awake`), o modelo pode aparecer incompleto. O clone local antigo passava pelo `Awake` antes de ser despido.
3. **Altura e tamanho do nome.** O tamanho da fonte (5) e a altura (topo dos renderers + 0,4) são estimativas. O texto pode sair pequeno, grande ou baixo demais.
4. **Shader do TextMeshPro em 3D.** O material da fonte do menu é o mesmo shader de distance field. Deve renderizar fora do canvas, mas não foi visto.
5. **Velocidade da locomoção.** A animação de andar é normalizada pela velocidade máxima do jogador **local**. Com personagens de velocidades diferentes, o passo do fantasma pode ficar um pouco rápido ou lento.
6. **Fora do escopo:** o modelo de transformação (Ascension, por exemplo o do Demon Hunter) e os companheiros visuais da arma não aparecem no fantasma. Skins de mod do parceiro viram a skin padrão.

## Roteiro de teste

Duas máquinas (ou duas instâncias), conectadas pela aba Co-op ou pelo convite da Steam.

1. Host escolhe **Hound Master** e cliente escolhe **Arcane Weaver**, de preferência com uma skin que não seja a padrão em pelo menos um deles. Os dois no menu, conectados.
2. Host aperta Lutar. Nos dois lados, assim que a run começar:
   - **modelo:** o fantasma é o personagem do outro (o host vê um Arcane Weaver, o cliente vê um Hound Master), com a arma na mão;
   - **skin:** a mesma que o outro escolheu;
   - **nome:** o nome Steam do outro aparece em cima do fantasma, legível e virado para a câmera.
3. **Andar:** cada um anda em várias direções. As pernas do fantasma animam (frente, lado, parado) e ele não escorrega para fora da posição.
4. **Cast:** cada um deixa as skills dispararem. As magias do outro aparecem como na 4C e o fantasma faz a animação de ataque.
5. **Revive:** host aperta F9 e o cliente fica perto por 3 s. Depois o contrário. Os textos de revive aparecem sobre o personagem certo e o nome continua no fantasma.
6. **Segunda run:** voltem ao menu, o cliente troca de personagem e skin, e joguem de novo. O fantasma novo é o personagem novo.
7. **Mesmo personagem nos dois lados** (por exemplo, os dois Hound Master com skins diferentes): cada um vê a skin do outro.

**Logs para mandar** (`BepInEx/LogOutput.log` dos dois lados):
- `VisualGhostFactory: ... built as the partner: <personagem> skin '<skin>' ..., weapon '<arma>' (main=True offhand=...)`, ou o aviso `uses the LOCAL character as fallback (...)`;
- `Name tag '<nome>' attached`;
- `[sync] Ghost animator ...: ForwardMovement=True ...`;
- `[casts] Partner plays ...` e o `ghostAnim=` da telemetria;
- qualquer `Ghost has no animator state`.
