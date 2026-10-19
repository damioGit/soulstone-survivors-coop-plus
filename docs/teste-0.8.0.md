# Teste da 0.8.0

Roteiro único pra testar com o amigo tudo o que entrou depois da 0.7.0. Nada disso foi testado em jogo ainda. Os detalhes de cada item estão nas notas indicadas.

## Antes de começar

- Os dois com o jogo **1.5.3.2** e o mod **0.8.0** (`dist/SoulstoneCoop-0.8.0.zip`). A 0.7.0 não conecta com a 0.8.0.
- Os dois abrem o jogo **pela Steam**.
- **Personagens diferentes** (por exemplo, host com Hound Master e amigo com Arcane Weaver). É o que testa a fase 5.
- Se algo der errado, mandem os dois `BepInEx\LogOutput.log` logo depois, antes de abrir o jogo de novo.

## Teclas de debug

| Tecla | Quem | O que faz |
|---|---|---|
| F5 / Shift+F5 | host | Usa o portal Endless / o de desafio (BossRush) |
| F6 / Shift+F6 | qualquer um | +1 / +5 níveis |
| F7 | host | Spawna VoidPresence, void hunters, minério e pilar |
| F8 | host | Completa o objetivo de kills; o boss nasce |
| F9 | qualquer um | Cai (estado "caído") |
| F10 | host | Força a vitória (apocalipse e portais) |

## Roteiro

1. **Conexão:** o host clica em **Co-op → Convidar amigo**, e o amigo aceita pela Steam. Os dois painéis mostram "Conectado com <nome> (Steam, x ms)".
2. **Início junto:** o host escolhe um mapa e aperta Lutar. O amigo entra sozinho no mesmo mapa.
3. **Parceiro com a cara certa** (fase 5, `fase5-notas.md`): cada um vê o outro com o **próprio personagem, a skin e a arma**, o **nome da Steam** em cima, uma **barra de vida** de aliado e o anel colorido no chão. O fantasma anda animado, e o ataque dele anima também. Também aparece um marcador no minimapa.
4. **Barra de vida:** um apanha de propósito; a barra dele desce na tela do outro.
5. **Level-up sem pausar** (`levelup-notas.md`): os dois apertam **Shift+F6** algumas vezes até encher a build. Com a build completa, a tela de upgrade de um **não congela** o jogo do outro. Antes de completar a build, ainda congela os dois, como na 0.7.0.
6. **Bosses** (`fase4d-notas.md`): o host aperta **F8**. No amigo, o contador de bosses **anda** (não fica mais em 0/N), aparecem a barra grande do boss e os avisos de área no chão. Matem o boss: a barra some nas duas telas, e o contador avança.
7. **God Meteors** (`pendencias-notas.md`): onde houver a estátua do deus, o amigo vê os meteoros caindo, e o dano dele vem uma vez só, sem golpe duplo.
8. **Status** (`pendencias-notas.md`): quando um inimigo aplica slow ou stun no amigo, ele sente (fica lento ou atordoado). Os números de dano das magias replicadas não aparecem mais em dobro.
9. **Cair e reviver:** o host aperta **F9** e o amigo fica perto uns 3 s. Aparecem "CAIDO", "REVIVE xx%" e "REVIVEU!", e o host levanta. Esse é o caso da 4A que ainda não tinha sido visto.
10. **Portal infinito** (`portais-notas.md`): o host aperta **F10** e depois **F5**. Os dois vão juntos pra próxima fase, mantendo nível e skills. O fantasma, os inimigos e o contador funcionam na fase nova.
11. **Portal de desafio:** **F10** de novo e depois **Shift+F5**. Os dois entram na sala do BossRush. **F10** lá dentro dá a vitória nos dois.
12. **Reiniciar:** na tela final, o host aperta Reiniciar. Os dois recomeçam a mesma run juntos.
13. **Quedas** (`fase8-notas.md`): numa run, o **amigo fecha o jogo**. No host os inimigos largam o amigo e a run continua sozinha. Depois, numa run nova, o **host fecha o jogo**. O amigo vê "O host desconectou. Encerrando a partida..." e volta ao menu uns 4 s depois.
14. **Conexão instável:** se a rede piorar, aparece "Conexão instável" antes de cair.

## Opções novas no `.cfg` (`BepInEx\config\com.kolber.soulstonecoop.cfg`)

| Opção | Padrão | Pra quê |
|---|---|---|
| `[Coop] LevelUpPause` | `NotWhenFullBuild` | `Always` volta ao comportamento da 0.7.0; `Never` nunca pausa a sessão |
| `[Coop] EndRunWhenHostLeaves` | `true` | Com `false`, o cliente não desiste sozinho quando o host cai |
| `[Coop] AllowRejoin` | `false` | Experimental: o cliente volta pra run em andamento como personagem novo (nível 1) |
| `[Coop] SenderClock` | `false` | Experimental: suaviza o fantasma e os inimigos quando a rede tem jitter |
| `[Debug] SimulateLatencyMs` / `SimulateJitterMs` / `SimulatePacketLossPercent` | `0` | Simula rede ruim pra testes locais |

## Se algo quebrar

Cada item entrou num commit separado. Dá pra reverter só a parte com problema:

- Status no cliente: `0989994`
- God Meteors: `9b8f910`
- Números de dano: `e225e80`
- Desistência automática quando o host cai: `[Coop] EndRunWhenHostLeaves = false`, sem precisar reverter.
