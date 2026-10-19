# Fase 5: identidade do jogador (investigação)

Investigação estática, só leitura: `dump.cs`/`script.json`, ApiDump no interop, Ghidra headless (`-readOnly`, cópia do projeto numa pasta de rascunho) e os caminhos de `Resources` listados no `globalgamemanagers`. RVAs relativos a `0x180000000`. As saídas descompiladas ficaram fora do repo.

## Como o jogo monta o modelo do jogador

`GameManagerUtil.SetupPlayerForMatch` (MoveNext 0x705A20) lê do perfil local o personagem (`SelectedCharacter.CharacterIdAsEnum`), a arma (`GetWeaponIdByCharacterId` → `WeaponCollection.TryGetWeaponDefinitionById`) e a `CharacterDefinition`. Depois roda o `CharacterModelHolder.SpawnCharacter(entity, characterId, characterDef, weaponDef)` (MoveNext 0x5AD130), que faz:

1. `SkinData skin = PlayerProfile.SelectedSkinPerCharacter.GetSkinDataByCharacterId(id)`: struct `{CharacterSkinId, WeaponSkinId}`, 16 B.
2. `CharacterSkinData data = Global.CharacterSkinCollection.GetSkinById(skin.CharacterSkinId)`.
3. Skin de mod (`data.ModId.HasValue`, byte em +0x30): `ModManager.InstantiateCharacterSkinRoutine`, assíncrono, com o modelo vindo do disco do jogador.
4. Skin normal: `data.LoadCharacterSkin(false)` (0x407070) = `ResourcesUtil.Load<PlayerPrefabReferences>("Characters/" + CharacterSkinID)`. Com `isMenu`, tenta antes `"Characters/<id>Menu"`. O resultado vai para `Instantiate(prefab, entity.transform)` e `localScale = Vector3.one * data.CharacterSkinSize`.
5. No `PlayerPrefabReferences` instanciado: `SpawnedCharacterId`, `SpawnedCharacterSkinId` e `WeaponDefinition`. Depois **`Setup(entity, weaponDef, skin)`** (0x812FA0), `UpdateDashEffect` e os companheiros da arma (`CompanionComponent` no Entity).

O `Setup`/`FirstTimeSetup` (0x812600) **escreve no Entity**: lista de Animators do `AnimatorComponent` (+0x2B8), renderers de dano (+0x2C8), placeholders (+0x368), ragdoll (+0x2F8), `PowerUpComponent` (+0x3C8), `Entity.Setup` dos `ChildEntitiesToSetup`. Também dispara a corrotina `SpawnWeaponsRoutine` no `JobRunner`. Isso não pode rodar para o fantasma.

As armas vêm de `CharacterModelHolder.SpawnWeaponsAsyncRoutine(SkinData, isMenu, PlayerPrefabReferences, playSpawnAnimation)` (MoveNext 0x5C4BF0), que **só mexe no modelo**:
- `ResetFollowersAndOffsets(refs)`, com guarda de nulo no `ItemFollowBone`;
- `WeaponDefinition.TryLoadWeaponReferences(Main/Offhand)` (`Resources "Weapons/..."`) e `Instantiate` sob `MainWeaponAttachment`/`OffhandWeaponAttachment`;
- `SetHandOffsetToMatchDefaultRig`, offsets da `WeaponDefinition` e escala `BaseWeaponScaler`;
- os renderers da arma entram em `TakeDamageShaderRenderers`.

No caminho sem mod ela termina no primeiro passo. O `runtimeAnimatorController` só é trocado com `isMenu` ou skin de mod, então **o controller de gameplay é o do próprio prefab**. Cuidado: com skin de arma de mod, ela vai pelo `ModManager`, e o ramo de falha grava `SetWeaponSkinIdForCharacterId` no **perfil local**.

O `globalgamemanagers` confirma os prefabs: `characters/<personagem><regular|veil|dlc|ascended>` (e `...menu`). Os `...ascension` são modelos de transformação no meio da run, trocados por `InstantiateAscensionModelRoutine`.

## O que o handshake já manda

O `Hello` da fase 6 (`Lobby/CoopSession.cs`) leva `CharacterId` (byte de `CharactersType`), `WeaponId`, `SkinId` (`GetCharacterSkinIdByCharacterId`) e `WeaponSkinId`. Isso basta:
- skin: `CharacterSkinCollection.TryGetCharacterSkinById(SkinId, out data, out personagem)`;
- reserva: `GetDefaultCharacterSkinByCharacterId`;
- arma: `WeaponCollection.TryGetWeaponDefinitionById` (reserva: `GetDefaultWeaponDefinitionByCharacterId`).

Não precisa de pacote novo.

## Plano

- Continuar clonando o Entity local para o fantasma (o anel e os efeitos do corpo continuam iguais), mas trocar o filho `PlayerPrefabReferences` pelo modelo do parceiro, montado como no passo 4: `LoadCharacterSkin(false)`, `Instantiate` sob um pai **inativo** (sem `Awake` em nada), escala, os três campos e as armas pela `SpawnWeaponsAsyncRoutine` rodada na hora com `WeaponSkinId` vazio. Depois despir o modelo, desligar `applyRootMotion` e ativar o modelo como filho do fantasma.
- Skin de mod, 2D (`IsSprite2D`, precisa do `CharacterSkin2DController.Setup(entity)`), de outro personagem ou desconhecida: usar a skin padrão do personagem. Sem `Hello`, ou com o personagem sem skin padrão: usar o clone local (o fantasma de antes) e logar o motivo.
- **Lançador da 4C:** ele já carrega as skills por `Resources "Skill-Prefabs/<chave>"`, então serve para qualquer personagem. O `UnitUtil.SetupExtraSkillForEntity`, tentado antes (descompilado em 0x749F30), busca o id no `MatchPowerUpData` **desta** partida, ou seja, nos power-ups do personagem local:
  - para skill de outro personagem, devolve nulo;
  - para skill em comum, instancia a cópia e dá NRE no laço do `PowerUpComponent` (Entity+0x3C8, que o Dummy não tem), vazando a cópia. É o NRE de Buckshot/PowerBlast da 4C.

  Sai esse passo e a cópia vem direto do prefab.
- **Animator:** `ForwardMovement`, `SidewaysMovement` e `OnGround` são os hashes estáticos do `PlayerAnimator` (o mesmo componente em todo personagem). Os nomes de estado do cast vêm da `SkillAnimationTrait` da própria skill. Com o modelo certo, os dois devem existir. Mesmo assim, dá para conferir em runtime pela lista `Animator.parameters` e pelo `HasState`.
