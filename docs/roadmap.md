# Roadmap do Soulstone Coop

Fonte única das próximas fases e pendências. Detalhes técnicos de cada fase ficam nos `docs/faseN-*.md`.

## Feito

| Fase | O que entrega | Commit |
|---|---|---|
| Base | Dois jogos (host/cliente) conectam e sincronizam a posição dos jogadores; o remoto é um fantasma só visual | `b4d8ba2` |
| 0 | Mapa de spawn, IA, dano e XP com pontos de patch | `fd9fb6a` |
| 1 | Cliente sem inimigos próprios, pausa e level-up sincronizados | `2b06a95` |
| 2 | Inimigos do host replicados como marionetes no cliente | `e56ab67` |
| 3 | Dano nos dois sentidos, com proxy aliado do cliente no host | `15b0d45`, `0cf1274` |
| 4C | Cada jogador vê as magias do outro (só visual) | `e7a0261` |
| 4B | Loot e XP no cliente, crédito de kill, XP de equipe | `a25e89e`, `47c4287` |
| 4A | Caído, reviver, derrota só quando os dois caem, vitória/portal Home pros dois; fantasma suavizado e animado, câmera de espectador | `ad9a096` |
| 4D | Bosses, VoidPresence, void hunters e pilares como marionetes; avisos de área dos ataques de boss, elite e especiais | `91b4851` |
| 6 | Aba Co-op no menu, conexão por IP, run começando junto, handshake com versão/nome/personagem | `70dff33` |
| 7 | Convite pela Steam, transporte P2P pelo relay da Steam | `a068c13` |
| Pacote | Script que gera o zip pra instalar em outro PC, com LEIA-ME | `5f09811` |
| **0.8.0** | Tudo abaixo junto, ainda sem teste em jogo (roteiro em `teste-0.8.0.md`) | `7452dec` |
| 5 | Fantasma com o personagem, skin e arma do parceiro; nome da Steam, barra de vida, anel e marcador no minimapa | `56b5309` |
| 8 | Simulação de rede, correção do MTU do LiteNetLib, queda do host e do cliente, aviso de conexão instável, reentrada (experimental) | `958d3f9` |
| Pendências | God Meteors no cliente, status do proxy chegando no cliente, números de dano das réplicas, proxy fora das buscas por time, código morto removido | `bb7481e` |
| 4D, correções | Contador de bosses do cliente parado em 0/N; golpe duplo da estátua do deus no replay | `03f5232`, `4625503` |
| Level-up | Com a build completa, o upgrade não congela a sessão (`[Coop] LevelUpPause`) | `6c7ce5f` |
| Portais | Portais Endless e de desafio e o Reiniciar, com os dois indo juntos | `4aec2b3` |

| **0.9.0** | Tudo abaixo junto, ainda sem teste em jogo (roteiro em `teste-0.9.0.md`) | — |
| 5, correções | Fantasma animado de novo, com arma; lançador das réplicas colado no fantasma; dash do parceiro | `568db38`, `c8876dc` |
| 5, item 5 | Summons, companheiros e a puma do Sentinel do parceiro como clones visuais | `568db38` |
| Level-up | Painel compacto e semitransparente pra jogar enquanto escolhe; padrão `Overlay` | `c263e20`, `efd4d89` |
| 9 | Até 8 jogadores: slots, transportes multi-peer, repasse pelo host, lobby de 8, regras com N, escala de inimigos opcional | merge da `fase9-oito` |
| i18n | Textos do mod no idioma do jogo de cada jogador (en e pt-BR à mão; es, fr, de, ru, zh, ja automáticos, a revisar) | branch `i18n-textos` (`i18n-notas.md`) |

## Agora

**Testar a 0.9.0**, pelo roteiro de `teste-0.9.0.md`: primeiro com 2 pela Steam, depois com 3+ por IP no mesmo PC (`fase9-notas.md`). Os bugs que aparecerem entram aqui antes de qualquer fase nova.

## Próximos itens

| Item | Origem |
|---|---|
| Reconexão completa no meio da run, mantendo nível, skills e itens (hoje a reentrada experimental entra como personagem novo) | `fase8-notas.md` |
| Baús só existem no host; o cliente recebe a parte dele mas não consegue abrir | `fase4b-notas.md` |
| Magias replicadas: summons, auras sempre ligadas e mira contínua de channeling não aparecem | `fase4c-notas.md` |
| Inimigos comuns não animam o ataque no cliente (o replay é só de boss, elite e especial) | `fase4d-notas.md` |
| Modo Crusade (começa num lobby e troca de mapa no meio) | `fase6-notas.md`, `portais-notas.md` |
| Esquiva por dash nos hits que o host calcula no proxy (mexe no balanceamento) | `pendencias-notas.md` |
| Host morto com cliente vivo: rever proxy e spawn | `fase3-notas.md` |
| Skin de mod do parceiro vira a skin padrão; modelo de transformação (Ascension) e companheiros visuais da arma não aparecem no fantasma | `fase5-notas.md` |

## Ainda não vistos em jogo (de fases anteriores)

- Barra de boss sumindo ao matar, invocações de boss, minérios e cristais de cura, vida u16 no snapshot (`fase4d-notas.md`).
- O cliente revivendo o host e o efeito ao levantar (`fase4a-notas.md`).
- Itens e power-ups via `ProcessLoot`, bloqueio do portal do boss no cliente, procs on-kill com runa de kill, XP retido de jogador morto (`fase4b-notas.md`).
- Patch 13 (PlayerSeekerAI) nunca exercitado (`fase3-notas.md`).
- Conexão por IP na fase 6: recusa por versão (`--coop-fake-version`), volta ao menu e segunda run (`fase6-notas.md`).

## Regras que valem pra qualquer fase

- Nunca criar um segundo Entity de jogador. Toda Entity nova passa pelo pipeline do jogo (pool + `Entity.Setup`), e nada é clonado do jogador antes do `StartMatch` local.
- Todo patch com try/catch: um NRE dentro de um `UpdateAll` derruba o frame inteiro.
- Não patchar método com struct de 1/2/4/8 bytes por valor na assinatura (`fase4c-notas.md`).
- Texto flutuante do jogo (`OverheadCombatTextPanelManager.RequestCombatTextWithString`) aceita no máximo 16 caracteres; acima disso dá erro e o texto não aparece.
- Ao subir a versão, mudanças de formato de pacote quebram a compatibilidade: o handshake recusa versões diferentes.
- Enquanto o usuário testa uma versão fixa, `%USERPROFILE%\.soulstone-no-deploy` trava o deploy (ou use `-p:SkipDeploy=true`).
- Texto visível ao jogador sai da tabela `Ui/LocTable.cs` (`Loc.T` / `Loc.Msg`), nunca literal no código; pela rede vai a `Loc.Msg` (chave + parâmetros), nunca o texto traduzido (`i18n-notas.md`).
- Pacotes de rede: veja o enum `PacketType` em `src/SoulstoneCoop/Net/PacketRouter.cs` (a fase 9 usa 100–104). Itens novos começam no 120.
