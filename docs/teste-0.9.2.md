# Teste da 0.9.2 (antes da release)

A 0.9.2 já está instalada no PC do host. Teu amigo precisa do zip `dist/SoulstoneCoop-0.9.2.zip` (a 0.9.2 não conecta com a 0.9.1). Nada disso foi jogado ainda.

Roteiros detalhados: `levelup-notas.md`, `issue2-dash-notas.md` e `i18n-notas.md`.

## Antes de começar

- **Deixem a linha antiga `LevelUpPause = NotWhenFullBuild` no `.cfg`** (se ainda estiver lá). É justamente o caso da issue #1: ela tem que ser ignorada.
- **Usem personagens diferentes** (por exemplo, um corpo a corpo e um mago), pra conferir o dash.
- **Pelo menos um de vocês com o jogo em inglês** (Opções → idioma), pra conferir os textos.

## Roteiro

### Level-up sem pausa (issue #1)

1. **Primeiro level-up:** o painel abre pequeno embaixo, ninguém congela, e quem escolhe continua andando e atacando. Escolham com 1-4.
2. **Vários de uma vez:** **Shift+F6** (5 níveis) e escolham os 5 andando. Ninguém pausa.
3. **Elite, boss ou baú:** **F8** no host chama um boss; matem ele e peguem o power-up. A escolha abre no mesmo painel compacto, sem pausa.
4. **Reroll, banish, lock, skip e troca de skill:** usem os botões do painel no mouse. Nada pausa.
5. **Esc continua pausando todos:** o host fecha o menu dele (ou aperta **F4**) e todos voltam.
6. **Controle (se tiverem):** o stick move o personagem; D-pad esquerda/direita troca o card, cima escolhe, baixo esconde/mostra.

### Dash do parceiro (issue #2)

7. **Parado e correndo:** um dá dash; na tela do outro, o personagem dele deixa **3 a 6 cópias na cor dele**, que somem rápido. Tem que parecer igual ao dash na própria tela.
8. **Troquem os papéis:** o rastro tem a cor e o modelo do outro personagem, com a arma.
9. **Depois de um portal** (F10 e F5 no host), o rastro continua aparecendo.

### Textos no idioma de cada um

10. **Aba e painel Co-op:** em inglês num jogo e em português no outro, cada um vê tudo no próprio idioma (botões, status, lista de jogadores). Nada cortado.
11. **Trocar o idioma com o painel aberto:** os textos mudam na hora, sem reiniciar.
12. **Em jogo:** F9 pra cair → `CAÍDO`, `REVIVE xx%`, `REVIVEU!` aparecem no idioma de quem vê, sem erro de "Combat Text" no log; o nome sobre o fantasma ganha "(caído)".
13. **Avisos do topo:** pausa do parceiro, "Aguardando X carregar", e o indicador do level-up minimizado ("Level up! (N) - Tab mostra").
14. **Recusa no idioma de quem lê:** se der, um de vocês abre com `--coop-fake-version 0.5.0` e tenta entrar; cada um vê o motivo no próprio idioma.

## No log (`BepInEx\LogOutput.log`)

- `[i18n] 133 texts OK` e nenhum `[i18n] ... problem`; a linha `[i18n] ... draws every` (ou o que a fonte não desenha).
- `[levelup] Popup opened: coop=True -> NO PAUSE (overlay)`.
- `[dash] Ghost N dash trail` e `trail=` subindo na linha `[sync]`.

Mandem o log de cada um logo depois de testar, antes de abrir o jogo de novo.
