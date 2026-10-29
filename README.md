# Soulstone Coop

> **This is a modified copy of [gskolber/soulstone-survivors-coop](https://github.com/gskolber/soulstone-survivors-coop).** It's the same mod by the same author, with new features and fixes made with the help of AI (Claude) so it supports more of the game: the Co-op panel restyled to match the game, Mastery and Titan Hunt in co-op, the guest's crowd control, chests and map tasks, boss statues, smoother enemies, more synced effects, tougher enemies in co-op, one shared level for the group, a zoomed-out camera in co-op, partners' characters in the menu, map quests and their rewards for guests, reconnecting to a run after a dropped link, co-op options in the panel and Polish texts. It also includes the author's own 1.1.0 additions (party panel, upgrade panel above the HUD with gamepad support, guests seeing every enemy). All credit for the original mod goes to its author. What's new: [docs/releases/0.13.0.md](docs/releases/0.13.0.md).
>
> **Download:** the [Releases](../../releases) page, file `SoulstoneCoop-0.13.0.zip` (unpack it into the game's folder; everyone in the group needs this same version).

Online co-op for [Soulstone Survivors](https://store.steampowered.com/app/2066020/Soulstone_Survivors/): up to 8 players in the same run, invited through Steam.

An unofficial fan mod, not affiliated with or endorsed by the game's developers. Built on [BepInEx 6](https://github.com/BepInEx/BepInEx) (IL2CPP).

> **Status: alpha.** Played by two people over Steam up to 0.8.0; 3+ players and the 0.9.0 changes are untested. See [What works](#what-works) and [Known limitations](#known-limitations).

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
| Modes | Survivors (with Endless and challenge portals), Mastery (cycles, Lords, pillars, final boss and its arena) and Titan Hunt (titan choice, maps, key fragments, boss room, titan blood, sigils, Primordial Portal, Void King) |
| Options | The Co-op panel's **Co-op options**: the host's (enemy health and numbers, joining mid-run, lobby type and size) and your own (pause on level-up, dash dodge, ending when the host leaves) |
| Players | Up to 8 (1 host + 7 guests); the host relays each player to the others |
| Partners | Shown with their own character, skin and weapon, Steam name, health bar, ally ring and minimap marker; smooth movement, dash and animations; their pets and summons (e.g. the Sentinel's panther) are visible |
| Enemies | The host owns the world; the guest sees the same enemies, bosses, void hunters and specials, including boss area warnings |
| Combat | Both players damage the same enemies; enemies chase and hit whoever is closest; statuses (slow, stun) reach the guest |
| Skills | Each player sees the other's skills (visual only; damage is synced separately), including rotating shields and the live aim of channels |
| Chests | Every player sees the reward chests and can open them; the loot is shared as usual |
| Progression | One shared XP pool: everyone levels up at the same moment, each player's XP bonus counts 1/N for the team; loot and kill credit on the guest; each player picks their own upgrades |
| Pause | Anyone's pause freezes everyone and the host can unpause for all; upgrade choices pause the group until everyone has picked (or the compact panel, with `PauseOnLevelUp = false`) |
| Death | Downed state (the partner lies down on your screen), any ally standing next to you revives you, the host included; defeat only when everyone is down. Heal orbs reach every player; guests dodge hits by dashing |
| End of run | Victory, Home portal, Endless and challenge portals and Restart happen for everyone; the host surrendering ends the run for all |
| Disconnects | Host leaving ends everyone's run with a notice; a guest leaving (or surrendering) lets the others continue |

## Known limitations

- Rejoining a run in progress keeps nothing (behind `[Coop] AllowRejoin`, off by default).
- Titan Hunt: only the host picks the titan (the guest sees the same three on its own panel, and a press there is a proposal the host sees); the boss cutscenes play on the host's screen, the others wait with a banner.
- Partners' mod skins, Ascension transformation and weapon companions are not shown on their character.

The full list and design notes are in [`docs/roadmap.md`](docs/roadmap.md).

## Roadmap

- Full rejoin keeping level, skills and items.

## Settings

`BepInEx/config/com.kolber.soulstonecoop.cfg` (created on first launch):

| Setting | Default | Meaning |
|---|---|---|
| `[Debug] LevelUpFullPanel` | `false` | Use the game's full-screen upgrade panel instead of the compact one (co-op upgrade choices never pause either way) |
| `[Coop] EndRunWhenHostLeaves` | `true` | End the guest's run when the host disconnects |
| `[Coop] AllowRejoin` | `false` | Experimental: let the guest rejoin a run in progress (as a fresh character) |
| `[Coop] MaxPlayers` | `8` | Lobby size (2-8) |
| `[Coop] EnemyScalePerPlayer` | `0` | Extra enemies per additional player (try `0.25`) |
| `[Coop] ReplicaCastRadius` | `60` | Don't draw skills of partners farther than this |
| `[Coop] DashDodge` | `true` | Guests dodge a host-computed hit while dashing (or within their ping after) |
| `[Coop] EnemyHealthBonus` | `100` | Enemy health +% per extra player (2 players: +100 %); 0 = off |
| `[Coop] CameraZoomOut` | `25` | In co-op runs, the camera sits this many % farther (0 = the game's camera); the fog moves back with it |
| `[Coop] LobbyPartners` | `true` | Experimental: the connected players' chosen characters (with their names) stand next to yours in the main menu |
| `[Coop] PartyHud` | `true` | In a run, a panel at the top left with each partner's health, level, XP, revives left and state |
| `[Coop] LevelUpPanelPosition` | `Top` | Where the compact upgrade panel sits: `Top` (above your character) or `Bottom` (just above the health bar) |
| `[Coop] PauseOnLevelUp` | `true` | Upgrade choices pause everyone until all have picked (30 s cap); `false` = small panel, no pause |
| `[Coop] LobbyType` | `FriendsOnly` | `Public` if friends of guests can't join |
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
