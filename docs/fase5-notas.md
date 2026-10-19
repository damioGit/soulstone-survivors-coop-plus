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
- **Destaque do aliado (`GhostOverhead`, segundo commit, a partir do feedback do teste da 0.7.0).**
  - **Nome Steam e barra de vida** em cima do fantasma, nas duas telas. O nome vem do `Hello`, que manda o nome Steam (P/Invoke em `steam_api64.dll`) tanto no convite pela Steam quanto por IP. Sem Steam, vem "Host"/"Cliente", e "Parceiro" antes do `Hello`.
  - Fica num canvas de tela próprio (overlay, `sortingOrder -100`, abaixo dos painéis do jogo), posicionado a cada frame onde a câmera do jogo (`OverheadHealthBarPanelManager.GameplayCamera`, ou `Camera.main`) vê o topo do fantasma. Por isso não some no meio dos efeitos nem muda de tamanho com a distância.
  - **Nome:** fonte do menu do jogo, negrito, 26 px de referência, verde-aliado, com contorno preto e sombra. Com o parceiro caído, fica vermelho e ganha "(caído)".
  - **Barra:** 130×14 px, fundo preto e preenchimento com o sprite de aliado das barras do próprio jogo (`AllyHealthBarSprite`) ou, se não achar, verde liso. Fica vazia com o parceiro caído.
  - Tudo some com o jogo congelado (pausa, level-up, barreira de carregamento, tela final: `timeScale` 0).
  - **Por que não a barra overhead do jogo:** `OverheadHealthBarPanelManager.RequestHealthBar` precisa de um `HealthComponent`, que o fantasma não tem. Dá para revisitar usando o `HealthComponent` do lançador remoto (4C), mas isso mexe na vida de uma Entity de verdade.
  - **Vida (`Life/PartnerHealth.cs`, pacote novo `PartnerHealth = 50`):** os **dois** lados mandam a própria vida com o mesmo formato (`hp f32 | maxHp f32 | flags u8`, 1 = caído e 2 = morto), ReliableOrdered, só quando muda (no máximo ~5 Hz) e mais uma vez por segundo de refresco. Assim o cliente recebe a vida do host, que antes não chegava, sem mexer no `PlayerState`, no snapshot nem no `ClientState`.
  - **Anel do chão:** os renderers do corpo que o clone herdou do Entity local (o anel e afins, tudo fora do modelo do personagem) recebem a cor de aliado (`_BaseColor`/`_Color`/`_TintColor`/`_MainColor` em instâncias de material). O log lista quais foram pintados.
  - **Minimapa:** `MinimapManager.RequestIcon(Player, transform do fantasma)`, um ícone de jogador a mais, removido quando o fantasma sai. O `OnUpdate` do minimapa confere o transform contra nulo (descompilado em 0x615430) e o `SetupMinimap` limpa a lista a cada mapa. O ícone é igual ao do jogador local, sem cor própria.
- **Locomoção (`PlayerSync`).** Na primeira vez, cada Animator do fantasma tem os parâmetros lidos (`Animator.parameters`). Só são escritos `ForwardMovement`, `SidewaysMovement` e `OnGround` que existem naquele controller. Isso inclui os Animators das armas e evita o aviso de "parâmetro não existe" a cada frame. Se a lista não puder ser lida, escreve os três, como antes. Log: `[sync] Ghost animator '<nome>' (<controller>): ForwardMovement=… SidewaysMovement=… OnGround=…`.
- **Lançador remoto (`RemoteCaster`).**
  - A cópia da skill vem direto de `Resources "Skill-Prefabs/<chave>"` + `Skill.Setup`, para qualquer personagem. Saiu a tentativa do `UnitUtil.SetupExtraSkillForEntity`: ela procurava a skill nos power-ups do personagem **local** e, para skill em comum, vazava uma cópia com NRE no Dummy (os NREs de Buckshot/PowerBlast da 4C).
  - Ao criar o lançador, as skills da arma do parceiro (`MainSkillId`, `OffhandSkillId`, `ArtifactPowerSkillId`) são pré-carregadas e logadas: `[casts] Partner plays <personagem> with '<arma>': X=ok, Y=ok`.
  - **Animação de cast:** o `CrossFade` da `AnimationTrait` vai para todo Animator do fantasma que tem o estado, como o `AnimatorComponent` do jogo faz. Estado ausente é logado uma vez por nome, e a telemetria `[casts]` ganhou `ghostAnim=tocadas/total`.


Pacote novo: `PartnerHealth = 50` (faixa 50–59 da fase 5). `PlayerState`, snapshot e `ClientState` não mudaram. A versão do mod e o `roadmap.md` não foram mexidos.

## Riscos (sem teste)

1. **Rotina de armas fora do contexto normal.** Ela roda sobre um modelo inativo que nunca passou pelo `FirstTimeSetup`. Um NRE ali é pego e o fantasma fica sem arma (log `weapon spawn failed`). Também chama `ModManager.CancelModelLoadingProcessing()`, que no meio da run não deve ter nada para cancelar.
2. **`Instantiate` sob pai inativo.** Se algum renderer depender de um script que despimos (por exemplo, combinar malhas no `Awake`), o modelo pode aparecer incompleto. O clone local antigo passava pelo `Awake` antes de ser despido.
3. **Posição e ordem do overlay.** A altura (topo dos renderers + 0,4) é estimativa. Se os painéis do jogo forem canvas de câmera (e não overlay), o nome e a barra podem ficar por cima de um painel que não congela o jogo. Com `timeScale` 0 eles somem.
4. **Pintura do anel.** Ela depende de o shader do anel ter uma das propriedades de cor. Se o anel for um efeito com cor só no `ParticleSystem`, ele fica como estava. Pode pintar também algum efeito do corpo que não seja o anel (o log lista os nomes).
5. **Ícone do minimapa** igual ao do jogador local. No modo de minimapa centrado no jogador, os dois ícones podem ficar parecidos.
6. **Velocidade da locomoção.** A animação de andar é normalizada pela velocidade máxima do jogador **local**. Com personagens de velocidades diferentes, o passo do fantasma pode ficar um pouco rápido ou lento.
7. **Fora do escopo:** o modelo de transformação (Ascension, por exemplo o do Demon Hunter) e os companheiros visuais da arma não aparecem no fantasma. Skins de mod do parceiro viram a skin padrão.

## Roteiro de teste

Duas máquinas, conectadas pela aba Co-op ou pelo convite da Steam.

1. Host escolhe **Hound Master** e cliente escolhe **Arcane Weaver**, de preferência com uma skin que não seja a padrão em pelo menos um deles. Os dois no menu, conectados.
2. Host aperta Lutar. Nos dois lados, assim que a run começar:
   - **modelo:** o fantasma é o personagem do outro (o host vê um Arcane Weaver, o cliente vê um Hound Master), com a arma na mão;
   - **skin:** a mesma que o outro escolheu;
   - **nome:** o nome Steam do outro aparece em verde em cima do fantasma, com contorno, legível no meio das magias;
   - **barra:** logo abaixo do nome, a barra de vida do outro, cheia;
   - **anel e minimapa:** o anel do chão do fantasma está verde (diferente do seu) e há um segundo ícone de jogador no minimapa, que anda junto com o parceiro.
3. **Vida:** um dos dois toma dano. A barra dele na tela do outro desce em menos de um segundo e volta a subir quando ele se cura. Façam nos dois sentidos: a barra do host na tela do cliente é a novidade.
4. **Andar:** cada um anda em várias direções. As pernas do fantasma animam (frente, lado, parado) e ele não escorrega para fora da posição.
5. **Cast:** cada um deixa as skills dispararem. As magias do outro aparecem como na 4C e o fantasma faz a animação de ataque.
6. **Pausa e level-up:** com o jogo pausado ou na escolha de upgrade, nome e barra somem e voltam depois.
7. **Revive:** host aperta F9 e o cliente fica perto por 3 s. Na tela do cliente, o nome do host fica vermelho com "(caído)" e a barra vazia. Depois do revive, volta ao verde. Façam também o contrário.
8. **Segunda run:** voltem ao menu, o cliente troca de personagem e skin, e joguem de novo. O fantasma novo é o personagem novo, com nome, barra e ícone.
9. **Mesmo personagem nos dois lados** (por exemplo, os dois Hound Master com skins diferentes): cada um vê a skin do outro.

**Logs para mandar** (`BepInEx/LogOutput.log` dos dois lados):
- `VisualGhostFactory: ... built as the partner: <personagem> skin '<skin>' ..., weapon '<arma>' (main=True offhand=...)`, ou o aviso `uses the LOCAL character as fallback (...)`;
- `ally tint on N body renderer(s): ...`;
- `[hpbar] Overhead UI built (...)`, `[hpbar] First partner health: ...`, `[hpbar] Minimap icon ...` e a linha de 5 s `[hpbar] partner hp=... shown=...`;
- `[sync] Ghost animator ...: ForwardMovement=True ...`;
- `[casts] Partner plays ...` e o `ghostAnim=` da telemetria;
- qualquer `Ghost has no animator state` e qualquer erro com `[hpbar]`.
