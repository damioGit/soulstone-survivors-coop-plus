# Teste da 0.9.0

Nada do que mudou desde a 0.8.0 foi jogado. Todo mundo precisa da 0.9.0, que não conecta com a 0.8.0.

## Com 2 jogadores (tu e teu amigo, pela Steam)

1. **Conexão:** Co-op → Convidar amigo; ele aceita pela Steam. O painel lista os dois.
2. **Fantasma:** o parceiro aparece com o próprio personagem e a **arma**, **anda animado** (não desliza), e o **dash** dele aparece.
3. **Magias do parceiro:** saem dele e vão na direção dos inimigos. Se continuarem invisíveis, mandem o log: agora ele registra onde as réplicas nascem.
4. **Puma do Sentinel** (e bichos de outros personagens): aparece na tela do outro e some quando o Sentinel monta nela.
5. **Level-up:** o painel abre pequeno, semitransparente, embaixo da tela. Dá pra continuar andando e atacando, escolher com 1–4 e esconder com Tab. O parceiro não congela.
6. **Pausa:** o Esc de qualquer um pausa os dois; o host consegue despausar pros dois (fechando a pausa dele ou com F4).
7. **Desistir:** quando o cliente desiste, sai só ele e o host continua. Quando o host desiste, acaba pros dois.
8. **Mudança visual:** na tela do host, o fantasma do cliente aparece azul.

## Com 3 ou mais

- **No mesmo PC, por IP:** o roteiro completo está em `fase9-notas.md` (uma janela pequena por instância, `--coop-name` pra diferenciar os nomes). Dá pra simular clientes com os bots de `tools/TestClient`.
- **Pela Steam:** uma conta Steam por jogador. Qualquer membro do lobby pode convidar. Se o amigo de um cliente não conseguir entrar, o host põe `[Coop] LobbyType = Public` no `.cfg`.
- **O que observar:** todos se veem, qualquer aliado perto levanta quem caiu, a derrota só vem quando todos caem, quem sai some da tela dos outros, e o jogo continua fluido com várias builds ao mesmo tempo.

## Opções novas no `.cfg`

| Opção | Padrão | Pra quê |
|---|---|---|
| `[Coop] MaxPlayers` | `8` | Tamanho do lobby |
| `[Coop] LevelUpPause` | `Overlay` | `Always` volta a pausar; `NotWhenFullBuild` pausa até a build ficar completa |
| `[Coop] EnemyScalePerPlayer` | `0` | Mais inimigos por jogador extra (testar `0.25`) |
| `[Coop] ReplicaCastRadius` | `60` | Não desenha magias de quem está longe (desempenho) |
| `[Coop] LobbyType` | `FriendsOnly` | `Public` se amigos de clientes não entrarem |

Se algo der errado, mandem o `BepInEx\LogOutput.log` de cada um logo depois, antes de abrir o jogo de novo.
