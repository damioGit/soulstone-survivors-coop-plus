# Textos por idioma (i18n)

Todo texto que o mod mostra ao jogador sai no idioma do jogo **daquele** jogador. Os logs (`[SoulstoneCoop]...`) continuam em inglês.

## Como funciona

| Peça | Onde |
|---|---|
| Tabela de textos (chave → texto por idioma) | `src/SoulstoneCoop/Ui/LocTable.cs` |
| Idioma, renderização, mensagens codificadas, fontes, checagens | `src/SoulstoneCoop/Ui/Loc.cs` |

### Idioma

1. `[Coop] Language` no `.cfg` (padrão `Auto`). Qualquer código da lista abaixo força o idioma, útil pra testar sem mexer no jogo.
2. O idioma escolhido nas Opções do jogo: `LocalizationManager.CurrentLanguageCode` (estático, Assembly-CSharp). O mod lê a cada 0,5 s, então trocar o idioma no menu vale na hora.
3. Antes do jogo ter um idioma carregado: o idioma do jogo na Steam (`ISteamApps::GetCurrentGameLanguage`, pela API flat do `steam_api64.dll`, como o `SteamName`).
4. Senão, inglês.

O jogo tem 8 idiomas (`Localization-Config` no `resources.assets`): `en`, `pt-BR`, `ja`, `zh`, `ru`, `de`, `fr`, `es`. Todos estão na tabela:

- **en e pt-BR**: escritos à mão, completos.
- **es, fr, de, ru, zh, ja**: **tradução automática**, marcada no topo do `LocTable.cs`. Precisam de revisão de quem fala o idioma.

O log mostra a escolha: `[i18n] Language: pt-BR (from game 'pt-BR')`.

### Chamadas

- `Loc.T("chave", ("name", x), ...)`: o texto no idioma atual, já com os parâmetros. Pra texto montado a cada frame (painel, faixa).
- `Loc.Msg("chave", ...)`: a chave e os parâmetros guardados numa string, renderizada só onde aparece (`Loc.Show`). Usada nos avisos (`CoopSession.Notice`), nos erros (`LastError`) e em tudo que viaja pela rede. Assim o aviso acompanha uma troca de idioma e quem recebe vê no próprio idioma.
- `Loc.Show(s)`: mostra uma `Msg` no idioma atual; qualquer outra string passa como está (nome de jogador, texto de um mod antigo).
- `Loc.Plain(s)`: a mesma `Msg` em inglês, pros logs.
- `Loc.Float("float.x", max, ...)`: texto flutuante (veja abaixo).

Parâmetros podem ser outras `Msg` (por exemplo o motivo dentro de "X não entrou: {reason}"); cada lado renderiza tudo no seu idioma.

### Formato na rede

Uma `Msg` é `<link="ssc:chave|nome=valor|...">Texto em inglês</link>` (valores com `Uri.EscapeDataString`). O TextMeshPro desenha a tag `link` só como o texto de dentro. Por isso **o formato dos pacotes não mudou** (continua uma string), e um mod mais antigo que receba uma recusa do handshake mostra o inglês legível. Uma chave que esta versão não conhece (de um mod mais novo) também mostra o inglês de dentro.

Viajam como `Msg`:

- recusas do handshake (versão, cheio, chave, protocolo, "entre pelo convite"), nos dois transportes;
- o motivo do `RunStartRefused` (cliente → host: "não tenho o mapa X", "ainda estou numa partida"...).

O que vem do outro jogador continua como está: nome da Steam e nome do personagem. Sem nome (sem Steam e sem `--coop-name`), o `Hello` agora vai com o nome vazio e cada um mostra "Host"/"Jogador N" no próprio idioma (antes ia "Host"/"Cliente" em português).

### Texto flutuante (16 caracteres)

`OverheadCombatTextPanelManager.RequestCombatTextWithString` tem buffer fixo de 16 caracteres por texto (`fase4a-notas.md`). As chaves `float.*` (`float.down`, `float.revive`, `float.revived`) passam por `Loc.Float`:

- no startup, `Loc.Validate` renderiza cada uma em cada idioma com o parâmetro mais largo (`{pct}` = 100) e loga um aviso pra qualquer uma acima de 16 (`[i18n] Floating text ... has N characters`). Hoje a maior tem 13 (`WIEDERBELEBT!`);
- em jogo, se o texto não couber ou a fonte do texto de combate (`GPUIndirectInstancedCombatTextManager._currentFont`, sem fallback) não tiver algum glifo, usa o mesmo texto sem acento (`CAÍDO` → `CAIDO`) e, se ainda não der, o inglês. Loga uma vez: `[i18n] Floating text '...' doesn't fit/draw`.

### Fontes

O jogo troca as fontes TMP por idioma (`LanguageFontConfig`, `LocalizationManager._fontDatas`; cada label do jogo tem um `LabelReferences.Type`).

- O painel Co-op usa a fonte do botão Lutar (no log: `tanseek-modern-pro-arabic` em inglês) e guarda o `FontType` dele. Quando o idioma (ou a fonte do idioma) muda, troca a fonte de todos os textos do painel e da faixa pela fonte do jogo pro idioma novo (`Loc.GameFont`), e chama `LabelReferences.UpdateFont` na aba Co-op clonada. O nome sobre o fantasma é refeito com a mesma fonte.
- Diagnóstico: ao montar a UI e a cada troca, `Loc.CheckGlyphs` loga se a fonte desenha todos os caracteres do idioma atual (`[i18n] co-op UI: font X draws every pt-BR character` ou a lista do que falta). Não deu pra conferir isso fora do jogo: **ver no log do teste**.
- Botões, título, faixa e status têm auto-size (até 55% do tamanho), porque as traduções têm tamanhos diferentes.

## Adicionar um texto

1. Uma linha no `LocTable.cs`: `["area.nome"] = R("English {x}", "Português {x}", es, fr, de, ru, zh, ja)`. `null` numa coluna = cai pro inglês. Os `{placeholders}` têm que ser os mesmos em todas as colunas (o startup avisa se não forem).
2. No código: `Loc.T(...)` se o texto é montado na hora; `Loc.Msg(...)` se vai pra um `Notice`, um `LastError` ou pela rede.
3. Texto flutuante: chave `float.*`, no máximo 16 caracteres, e a chave entra na lista `FloatKeys` do `Loc.cs` com o parâmetro mais largo.
4. Nunca mandar texto já traduzido pela rede: mande a `Msg`.

## Adicionar um idioma

Só faz sentido se o jogo ganhar o idioma (o mod segue o idioma do jogo).

1. Novo valor no fim do enum `Lang` e o código em `Loc.Codes` (mesma ordem).
2. `Loc.TryParseCode` (código do jogo) e `Loc.TryParseSteam` (nome da Steam).
3. Uma coluna nova em cada linha do `LocTable.cs` (o startup acusa linha com número errado de colunas).
4. Conferir no log a checagem de glifos e dos flutuantes.

## Roteiro de teste

Antes: nos dois jogos, `BepInEx/LogOutput.log` deve ter `[i18n] 132 texts OK (en 132, pt-BR 132, ...)` e nenhum `[i18n] ... problem`.

Pra cada idioma (pelo menos **inglês** e **português**; se der, um de fonte diferente, como russo ou chinês), trocando o idioma nas Opções do jogo, sem reiniciar:

1. **Log:** `[i18n] Language: xx (from game 'xx')` logo depois da troca, e a checagem de glifos (`draws every ... character`, ou o que falta).
2. **Aba Co-op e painel:** nome da aba, título, Convidar amigo, Copiar código, Hospedar por IP, Colar, Entrar, Desconectar, "IP ou código". Nada cortado ou saindo do botão.
3. **Status do painel:** fora do co-op; hospedando por IP; lobby da Steam aberto; conectando; conectado (cliente: "Conectado com ... / No grupo / Aguardando o host"); host com a lista de jogadores (`no menu`, `carregando`, `na partida`, `instável`).
4. **Erros de conexão:** recusa por versão (`--coop-fake-version` no cliente), lobby cheio, host que sai. Testar também com o host num idioma e o cliente em outro: cada um vê a recusa no seu.
5. **Faixa do topo:** pausa do parceiro, "escolhendo upgrade", "Aguardando X carregar", conexão instável (simulação de rede), avisos de Copiar código e Colar com a área vazia.
6. **Em jogo:** caído/revive (os flutuantes `CAÍDO`, `REVIVE xx%`, `REVIVEU!`), o nome com "(caído)" sobre o fantasma, o indicador do level-up compacto ("Level up! (N) - Tab mostra"), avisos de portal (F5/F10 no host), host desconectando no meio da run.
7. **Simulação de rede:** com `SimulateLatencyMs` ligado, a linha laranja no painel.
8. **Troca no meio:** com o painel aberto e com um aviso na tela, trocar o idioma: os textos mudam sem fechar nada.
9. **Mistura:** host em inglês, cliente em português: o aviso "X não entrou: motivo" aparece inteiro no idioma de quem lê.
