# Soulstone Coop

Online co-op for [Soulstone Survivors](https://store.steampowered.com/app/2066020/Soulstone_Survivors/): two players in the same run, invited through Steam.

An unofficial fan mod, not affiliated with or endorsed by the game's developers. Built on [BepInEx 6](https://github.com/BepInEx/BepInEx) (IL2CPP).

> **Status: alpha.** Tested with two players over Steam up to 0.7.0. See [What works](#what-works) and [Known limitations](#known-limitations).

## Install

1. Download the latest `SoulstoneCoop-<version>.zip` from [Releases](../../releases).
2. Close the game. In Steam: right-click **Soulstone Survivors → Manage → Browse local files**.
3. Extract the zip into that folder. `winhttp.dll` and the `BepInEx` folder must end up **next to** `Soulstone Survivors.exe`, not inside a subfolder.
4. Launch the game from Steam. A black BepInEx console opens too; don't close it. **The first launch takes a few minutes** while BepInEx sets up; later launches are fast.
5. A **Co-op** tab in the main menu means the mod is loaded.

Requirements: Windows, the game on version **1.5.3.2**, and **both players on the same mod version** (different versions refuse to connect).

To uninstall, delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, the `dotnet` folder and the `BepInEx` folder from the game folder.

## How to play

1. Both players launch the game from Steam and stay in the main menu.
2. **Host:** Co-op tab → **Invite friend**, and pick the friend in the Steam overlay. Without the overlay, use the Steam friends list → right-click the friend → *Invite to game*.
3. **Guest:** accept the invite in Steam chat. The Co-op panel shows *Connected with <name> (Steam, x ms)*. If the invite doesn't arrive, the host clicks **Copy code** and the guest pastes it into the panel and clicks **Join**.
4. Each player picks their own character; the **host** picks the map and presses Fight, and the guest's run starts by itself on the same map.

Direct IP is also available (**Host by IP** / type the host's IP → **Join**, UDP port 7777). Over the internet it needs a gaming VPN (Tailscale, ZeroTier, Radmin) or a forwarded port.

## What works

| Area | Status |
|---|---|
| Connection | Steam invite (P2P through Steam's relay, no port forwarding), invite code, direct IP; mod version check |
| Lobby | Co-op tab in the menu; the host starts the run and the guest follows on the same map, mode, difficulty and curses |
| Partner | Shown with their own character, skin and weapon, Steam name, health bar, ally ring and minimap marker; smooth movement and animations |
| Enemies | The host owns the world; the guest sees the same enemies, bosses, void hunters and specials, including boss area warnings |
| Combat | Both players damage the same enemies; enemies chase and hit whoever is closest; statuses (slow, stun) reach the guest |
| Skills | Each player sees the other's skills (visual only; damage is synced separately) |
| Progression | Team XP (whoever picks up an orb, both get it), loot and kill credit on the guest; each player keeps their own level and upgrades |
| Pause | Pause and level-up screens freeze both games; with a complete build, picking an upgrade no longer pauses the session |
| Death | Downed state, revive by standing next to your partner, defeat only when both are down |
| End of run | Victory, Home portal, Endless and challenge portals, Restart and surrender happen for both players |
| Disconnects | Host leaving ends the guest's run with a notice; guest leaving lets the host continue alone |

## Known limitations

- Two players only.
- Rejoining a run in progress keeps nothing (behind `[Coop] AllowRejoin`, off by default).
- Chests only exist for the host; the guest gets their share but can't open them.
- Summons, always-on auras and channel aim of the partner's skills are not shown.
- Regular enemies don't animate their attacks on the guest (bosses, elites and specials do).
- Crusade mode is not supported in co-op.

The full list and design notes are in [`docs/roadmap.md`](docs/roadmap.md).

## Roadmap

- Full rejoin keeping level, skills and items.
- Chests and other interactables for the guest.
- Partner summons and auras.
- Crusade mode.

## Settings

`BepInEx/config/com.kolber.soulstonecoop.cfg` (created on first launch):

| Setting | Default | Meaning |
|---|---|---|
| `[Coop] LevelUpPause` | `Overlay` | `Overlay`: compact see-through panel, you keep playing (1-4 pick, Tab hides); `NotWhenFullBuild`: pause until the build is full; `Always`: always pause; `Never`: full-screen panel without pause |
| `[Coop] EndRunWhenHostLeaves` | `true` | End the guest's run when the host disconnects |
| `[Coop] AllowRejoin` | `false` | Experimental: let the guest rejoin a run in progress (as a fresh character) |
| `[Coop] SenderClock` | `false` | Experimental: smoother remote movement on jittery connections |
| `[Debug] SimulateLatencyMs`, `SimulateJitterMs`, `SimulatePacketLossPercent` | `0` | Simulate a bad network for testing |

## Building from source

The plugin compiles against the interop assemblies BepInEx generates from the installed game, so a build needs the game installed with BepInEx 6 be.789 (IL2CPP) launched at least once. Those generated assemblies and anything else derived from the game are never committed.

```bash
dotnet build src/SoulstoneCoop
```

The build copies the plugin into the game's `BepInEx/plugins/SoulstoneCoop` (skip it with `-p:SkipDeploy=true`). If the game is installed elsewhere, pass `-p:GameDir=<path>`.

- `tools/package.ps1` builds the install zip into `dist/`.
- `tools/release.ps1` publishes a GitHub release: it checks the tree, makes a Release build, packages it, tags `v<version>` and uploads the zip. Bump `PluginVersion` in `src/SoulstoneCoop/Plugin.cs` and put the notes in `docs/releases/<version>.md` first. `-DryRun` runs everything except the tag and the upload.

Design notes for each phase (in Portuguese) are in [`docs/`](docs/), starting with [`docs/roadmap.md`](docs/roadmap.md).

## Credits

- [BepInEx](https://github.com/BepInEx/BepInEx) and [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop): mod loader and IL2CPP interop, bundled in the release zip.
- [HarmonyX](https://github.com/BepInEx/HarmonyX): runtime patching.
- [LiteNetLib](https://github.com/RevenantX/LiteNetLib): direct-IP transport.
