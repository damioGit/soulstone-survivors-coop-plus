# Roadmap do Soulstone Coop

Fonte única das próximas fases e pendências. Detalhes técnicos de cada fase ficam nos `docs/faseN-*.md`.

## Feito

| Fase | O que entrega | Commit |
|---|---|---|
| Base | Dois jogos (host/cliente) conectam via LiteNetLib e sincronizam a posição dos jogadores; o remoto é um fantasma só visual | `b4d8ba2` |
| 0 | Mapa de spawn, IA, dano e XP com pontos de patch | `fd9fb6a` |
| 1 | Cliente sem inimigos próprios, pausa e level-up sincronizados | `2b06a95` |
| 2 | Inimigos do host replicados como marionetes no cliente | `e56ab67` |
| 3 | Dano nos dois sentidos, com proxy aliado do cliente no host | `15b0d45`, `0cf1274` |
| 4C | Cada jogador vê as magias do outro (só visual) | `e7a0261` |
| 4B | Loot e XP no cliente, crédito de kill, XP de equipe | `a25e89e`, `47c4287` |
| 4A | Caído, reviver, derrota só quando os dois caem, vitória/portal Home pros dois; fantasma suavizado e animado, câmera de espectador, textos de revive | `ad9a096` |
| 4D | Bosses (barra grande, objetivo "Eliminate X"), VoidPresence, void hunters e pilares como marionetes; avisos de área dos ataques de boss, elite e especiais no cliente | `91b4851` |

## Próximas fases, em ordem

### 6: UI de co-op e início de run sincronizado (EM ANDAMENTO, adiantada pelo usuário)
Detalhes mais abaixo. Inclui o handshake (versão do mod, nome, personagem, skin) que a fase 5 reaproveita.

### 5: identidade do jogador (personagem e skin certos)
**Bug:** o cliente vê o host com a skin do próprio cliente (e vice-versa). Causa: o `VisualGhostFactory` clona o jogador **local** pra representar o remoto (`docs/fase4c-notas.md`, "O que ainda não aparece"). Isso também faz a animação de cast replicada só funcionar com os dois no mesmo personagem.
- Handshake ao conectar: cada lado manda personagem, skin e cosméticos (o `PlayerPrefabReference` e o que o jogo usa pra montar o visual).
- O fantasma é montado a partir do prefab do personagem **remoto**, pelo pipeline do jogo, e despido como hoje.
- O lançador remoto da 4C copia as skills do personagem certo.
- Nome do jogador em cima do fantasma.

### 6: UI de co-op e início de run sincronizado
Hoje entra-se em co-op por argumento de linha de comando e cada um começa a run na mão, torcendo pra escolher o mesmo mapa.
- Menu no jogo: Hospedar / Entrar (IP ou convite), status da conexão, botão de desconectar.
- Lobby: os dois veem quem está conectado e o personagem de cada um, e marcam "pronto".
- O host escolhe mapa, modo e dificuldade; o cliente segue automaticamente e as duas runs começam juntas.
- Versão do mod no handshake: recusa a conexão com versões diferentes, com mensagem clara.
- Sair da partida, voltar ao menu e jogar de novo sem reiniciar o jogo.

### 7: jogar em redes diferentes
Hoje só funciona na mesma máquina ou LAN (IP direto, porta 7777 UDP).
- **Agora, sem código:** VPN de jogo (Tailscale, ZeroTier, Radmin) + `--coop-connect <IP da VPN>`. Serve pra testar com um amigo já.
- **Definitivo: rede P2P da Steam.** O jogo já traz o Steamworks.NET (`com.rlabrecque.steamworks.net.dll`), mas a investigação da fase 6 achou que o wrapper foi removido do build. Talvez seja preciso chamar o `steam_api64.dll` por P/Invoke, como a fase 6 faz pro nome do jogador. Confirmar antes de escolher a abordagem. Usar `SteamNetworkingSockets`/`SteamNetworkingMessages` como transporte (relay da Steam, sem abrir porta, sem servidor nosso) e `SteamMatchmaking` pra lobby e convite de amigo. Trocar o LiteNetLib por uma camada de transporte com as duas implementações (LiteNetLib pra testes locais, Steam pra jogar).
- Teste exige duas contas Steam em máquinas diferentes (no mesmo PC as duas instâncias usam a mesma conta).

### 8: robustez em rede real
Tudo até agora foi testado com 0 ms de latência.
- Testar com latência e perda simuladas (`NetManager.SimulateLatency` / `SimulatePacketLoss` do LiteNetLib) antes da fase 7 ir pro ar: marionetes, dano, pausa, revive.
- Queda e reconexão no meio da run (hoje o fantasma e o proxy somem, mas não há reentrada).
- Host caindo: o que acontece com o cliente.

## Pendências e bugs conhecidos

| Item | Origem |
|---|---|
| 4D não visto em jogo: barra de boss sumindo ao matar, invocações de boss (`sum:`), objetivo "Eliminate X" e o seguinte, minérios e cristais de cura (neutros, rastreados por `IsNeutralSpecial`), vida u16 no snapshot | `fase4d-notas.md` |
| God Meteors só aparecem no host; inimigos comuns não animam o ataque no cliente (o replay é só de boss, elite e especial) | `fase4d-notas.md` |
| Não vistos em jogo: o cliente revivendo o HOST e o efeito ao levantar (o cliente revivido pelo host foi validado). Teste de 1 min: host F9, cliente fica perto 3 s | `fase4a-notas.md` |
| Portais Endless/BossRush e "Reiniciar" sorteiam o mapa em cada máquina; em co-op só o portal Home funciona | `fase4a-investigacao.md` |
| Magias replicadas: summons, auras sempre ligadas e mira contínua de channeling não aparecem; números de dano das réplicas podem aparecer | `fase4c-notas.md` |
| Ataques dos inimigos não animam nas marionetes do cliente (só o dano chega) | `fase3-notas.md` |
| Stats do proxy incompletos (esquiva, resistências e afixos vêm do Dummy); status negativos (stun, slow) aplicados ao proxy não chegam no cliente | `fase3-notas.md` |
| Host morto com cliente vivo: proxy e spawn não foram pensados pra isso (rever com a 4A) | `fase3-notas.md` |
| Patch 13 (PlayerSeekerAI) nunca exercitado | `fase3-notas.md` |
| Baús só existem no host; o cliente recebe a parte dele mas não consegue abrir | `fase4b-notas.md` |
| Não testados em jogo: itens/power-ups via `ProcessLoot`, bloqueio do portal do boss no cliente, procs on-kill com runa de kill, XP retido de jogador morto | `fase4b-notas.md` |
| Ações de time de boss/cutscene (`KillAllEntitiesAction` etc.) podem enxergar o proxy (Entity Players sem Inventory) | `fase3-notas.md` |
| `GhostFactory.cs` e `FieldDiff.cs` são código morto da primeira tentativa (clonar o Entity do player) | base |

## Regras que valem pra qualquer fase

- Nunca criar um segundo Entity de jogador. Toda Entity nova passa pelo pipeline do jogo (pool + `Entity.Setup`), e nada é clonado do jogador antes do `StartMatch` local.
- Todo patch com try/catch: um NRE dentro de um `UpdateAll` derruba o frame inteiro.
- Não patchar método com struct de 1/2/4/8 bytes por valor na assinatura (`fase4c-notas.md`).
- Texto flutuante do jogo (`OverheadCombatTextPanelManager.RequestCombatTextWithString`) aceita no máximo 16 caracteres; acima disso dá erro e o texto não aparece.
- Pacotes de rede: 1–4 reservados (sem uso), 5–21 em uso até a 4D, 22–29 reservados pra 4D, 30–39 pra fase 6. Fases novas começam no 40.
