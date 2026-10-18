# Distribuição: mandando o mod pra alguém testar

O pacote é um zip que a pessoa extrai **na pasta do jogo**. Ele leva o BepInEx 6 (be.789, IL2CPP)
inteiro + o mod + configs padrão + um `LEIA-ME.txt` em português pra jogador.

## Gerar o pacote

```
powershell -ExecutionPolicy Bypass -File tools\package.ps1
```

Sai em `dist/SoulstoneCoop-<versão>.zip` (o `dist/` está no `.gitignore`). A versão é o
`PluginVersion` de `src/SoulstoneCoop/Plugin.cs`. Hoje dá uns 33 MB (72 MB extraído; quase tudo é o
runtime .NET em `dotnet/`).

O script não compila nada e não escreve na pasta do jogo: só lê. Dá pra rodar com o jogo aberto.

Parâmetros:

| Parâmetro       | Padrão                                         | Pra quê |
|-----------------|------------------------------------------------|---------|
| `-GameDir`      | `C:\Program Files (x86)\Steam\...\Soulstone Survivors` | de onde vem o BepInEx e as configs |
| `-PluginSource` | `<jogo>\BepInEx\plugins\SoulstoneCoop`         | de onde vêm as DLLs do mod |
| `-OutDir`       | `<repo>\dist`                                  | onde sai o zip |

### De onde vem a DLL do mod

Por padrão o script pega as DLLs que o target `DeployPlugin` já copiou pro jogo, ou seja, **o último
build**. Antes de empacotar, confirme que esse build é o que você quer mandar (por exemplo, que
não é um build no meio do trabalho de alguma fase). O script mostra o `ProductVersion` da DLL (o hash
do commit do build) e a data.

O script **para com erro** se a string do `PluginVersion` do `Plugin.cs` não aparece dentro da
`SoulstoneCoop.dll` (o atributo `[BepInPlugin]` guarda ela), que é o sinal de que a DLL instalada é de
outro build. Se mudar a versão, recompile antes de empacotar.

Pra empacotar um build feito em outra pasta (opção "b"):

1. Hoje o `DeployPlugin` roda sempre depois do `Build` e copia de `$(OutputPath)`, então
   `-p:OutDir=...` **não** desliga o deploy (ele continua tentando copiar `bin\...` pro jogo, e falha se
   o jogo estiver aberto). Quem for dono do csproj precisa pôr uma condição no target:
   ```xml
   <Target Name="DeployPlugin" AfterTargets="Build" Condition="'$(SkipDeploy)' != 'true'">
   ```
2. Aí:
   ```
   dotnet build src\SoulstoneCoop -c Release -p:SkipDeploy=true -p:OutDir=C:\temp\coop-build\
   powershell -ExecutionPolicy Bypass -File tools\package.ps1 -PluginSource C:\temp\coop-build
   ```
   (O build continua precisando do `BepInEx\interop` gerado no jogo desta máquina.)

## O que vai no zip

| Caminho no zip | De onde | Por quê |
|---|---|---|
| `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` | jogo instalado | Doorstop: o proxy que faz o jogo carregar o BepInEx |
| `dotnet/` | jogo instalado | runtime CoreCLR (.NET 6) que o BepInEx IL2CPP usa |
| `BepInEx/core/` | jogo instalado | BepInEx, HarmonyX, Il2CppInterop, Cpp2IL etc. |
| `BepInEx/plugins/SoulstoneCoop/SoulstoneCoop.dll`, `LiteNetLib.dll` | `-PluginSource` | o mod e a única dependência dele que não vem do jogo nem do BepInEx |
| `BepInEx/config/BepInEx.cfg` | jogo, com tudo voltando pro padrão | aqui o `[Logging.Disk] LogLevels` está em `All` pra depurar; no pacote volta pro padrão (`Fatal, Error, Warning, Message, Info`) |
| `BepInEx/config/com.kolber.soulstonecoop.cfg` | jogo, com tudo voltando pro padrão | `AutoHost = false`, `LastAddress = 127.0.0.1` |
| `LEIA-ME.txt` | texto dentro do `tools/package.ps1` | instruções pro jogador |

Os arquivos do loader são cópia exata do que está instalado (conferido por hash; a lista bate com o
`tools/bepinex_il2cpp.zip` original). O `changelog.txt` do BepInEx fica de fora pra não aparecer solto
na pasta do jogo.

As configs: o script troca cada `chave = valor` pelo `# Default value:` que o BepInEx escreve logo
acima. Então qualquer config nova que as fases adicionarem já sai no padrão, sem mexer no script.
Se o cfg do mod não existir no jogo, o script usa um padrão embutido.

Referências: o script lê as referências de assembly das DLLs do mod e para com erro se alguma não
existir em `dotnet/`, `BepInEx/core/`, `BepInEx/interop/` ou na própria pasta do plugin. Hoje o mod
referencia só `System.*` (runtime), BepInEx/Harmony/Il2CppInterop (core), assemblies do jogo e da Unity
(interop) e `LiteNetLib`.

## O que NÃO vai, e por quê

O script confere o zip no fim contra uma lista branca e apaga o zip se tiver qualquer coisa a mais.

- **`BepInEx/interop/`, `BepInEx/unity-libs/`**: o interop é gerado a partir do `GameAssembly.dll` e
  do metadata do jogo, é conteúdo derivado do jogo (não é nosso pra distribuir). O BepInEx gera tudo
  na primeira execução (por isso a primeira abertura demora e precisa de internet: ele baixa as
  bibliotecas base da Unity de `unity.bepinex.dev`).
- **`BepInEx/cache/`**: cache local, recriado sozinho.
- **`BepInEx/patchers/`** e outras pastas vazias: o BepInEx cria.
- **Logs** (`LogOutput*.log`, `ErrorLog.log`): são da nossa máquina.
- **`steam_appid.txt`**: faz o jogo rodar sem passar pelo launcher da Steam e muda o comportamento da
  Steam. É só pro nosso teste local (abrir duas instâncias). O amigo abre pela Steam normal.
- **Plugins de dev** (`BepInEx/plugins/UnityExplorer/`): ferramenta nossa, não vai.
- **Qualquer arquivo do jogo** (`GameAssembly.dll`, `*_Data/`, `.exe`...).

## Checklist do teste com o amigo

Antes:

- [ ] Os dois com Soulstone Survivors **1.5.3.2** (ver `build_info.txt` na pasta do jogo). Se a
      Steam atualizou o jogo, o mod pode quebrar: teste aqui antes de mandar.
- [ ] Os dois com o **mesmo zip** (mesmo nome de versão). O handshake recusa versões diferentes do mod.
- [ ] Amigo instalou seguindo o `LEIA-ME.txt`, abriu o jogo uma vez, esperou a primeira carga e viu a
      aba **Co-op** no menu.
- [ ] Decidir quem hospeda. Sugestão: **você** hospeda (o host é a autoridade dos inimigos e você
      conhece o mod); o amigo entra como cliente.
- [ ] Decidir a conexão:
  - VPN de jogo (Tailscale / ZeroTier / Radmin VPN): os dois na mesma rede; o amigo usa o IP da VPN
        do host. É o caminho mais fácil, não precisa mexer no roteador.
  - Ou porta **7777 UDP** redirecionada no roteador do host pro PC dele; o amigo usa o IP público do host.
  - No host, permitir o jogo no Firewall do Windows quando o aviso aparecer ao clicar em Hospedar
        (se não apareceu e não conecta, liberar o "Soulstone Survivors.exe" no firewall, rede privada
        **e** pública, porque a interface da VPN às vezes fica como pública).

Durante:

1. Host: aba Co-op > **Hospedar** (status "Hospedando na porta 7777. Aguardando parceiro...").
2. Cliente: aba Co-op > IP do host > **Entrar** (status "Conectado com ...").
3. Host escolhe personagem e começa a partida; o cliente é levado junto.
4. Observar:
   - conecta de primeira? quanto tempo leva?
   - os dois veem os dois personagens; os inimigos andam liso no cliente (sem teleporte/engasgo);
   - dano e morte de inimigos batendo dos dois lados; XP e loot;
   - cair e reviver o parceiro; chefes e inimigos especiais;
   - pausa / menu de upgrade nos dois lados;
   - fim da partida e voltar pro menu; jogar uma segunda partida sem reiniciar o jogo;
   - lag com a latência real de internet (diferente do nosso teste em localhost).
5. Anotar o horário de qualquer coisa estranha (ajuda a achar no log).

Depois (logs dos dois lados):

- Cada um fecha o jogo e manda `<pasta do jogo>\BepInEx\LogOutput.log` (e `LogOutput.1.log` se
  existir, que é de uma segunda instância). **Antes de abrir o jogo de novo**: o log é reescrito a
  cada abertura.
- Se o jogo crashou de vez: também `%USERPROFILE%\AppData\LocalLow\Game Smithing\Soulstone Survivors\Player.log`
  (log da Unity) e a pasta de crash, se a Unity criar uma.
- No pacote o log do disco está no nível padrão (sem `Debug`). Se precisar de mais detalhe numa
  segunda rodada, peça pro amigo mudar em `BepInEx\config\BepInEx.cfg`, seção `[Logging.Disk]`:
  `LogLevels = All`.

## Quando o convite Steam (fase 7) entrar

- Se a fase 7 trouxer arquivos novos (por exemplo um wrapper Steamworks gerenciado e/ou
  `steam_api64.dll` / outra DLL nativa), eles precisam entrar no pacote: adicione na lista
  `$PluginFiles` do `tools/package.ps1` (e na lista branca, que já é montada a partir dela). Se o
  arquivo tiver que ficar fora de `BepInEx/plugins/SoulstoneCoop/` (por exemplo do lado do `.exe`),
  ponha a cópia no staging e uma regra nova em `$allowed`.
- A checagem de referências pega dependência gerenciada faltando, mas **não** pega DLL nativa
  carregada por P/Invoke: confira na mão.
- Não use `steam_appid.txt` no pacote: com convite pela Steam o jogo tem que abrir pela Steam.
- Atualizar o `LEIA-ME.txt` (seção "COMO JOGAR") pro fluxo do convite, deixando o IP como alternativa.
- Atualizar o checklist acima (convite, overlay da Steam, os dois logados em contas diferentes).
