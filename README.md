# Tales of Berseria Remastered — Archipelago

An [Archipelago](https://archipelago.gg) multiworld integration for Tales of Berseria Remastered (PC, v1.5.1.0).

- **Checks:** 437 treasure chests, 57 Katz boxes, 32 story battles, and optionally 4,303 sparkle points.
- **Region Keys:** every region after the prologue is locked until its key arrives; map exits into locked
  regions are refused in-game.
- **Goal:** defeat the final boss.

## For players

1. Install BepInEx 6 IL2CPP (bleeding edge build 793+) into the game folder and start the game once.
2. Copy `BepInEx/plugins/BerseriaAP` from the release zip into the game folder.
3. Put `tales_of_berseria.apworld` into your Archipelago `custom_worlds` folder to generate seeds.
4. Start the game. A small **Archipelago window** opens (toggle it with **F1** while the game is in front):
   enter server, slot name and password and press Connect. Settings are remembered.

See `SETUP.md` for details. Use a fresh save per seed.

### YAML options

| Option | Default | Meaning |
|---|---|---|
| `region_keys` | `true` | Lock regions behind Region Keys |
| `sparklesanity` | `false` | First pickup of each sparkle point is a check |
| `starting_region_keys` | `0` | Start with the first N keys in story order |
| `death_link` | `false` | A party wipe (game over) kills other DeathLink players, and vice versa |
| `death_link_mode` | `immediate` | `immediate`: a received death starts a battle on the spot and knocks out the party; `next_battle`: it waits for your next battle |

## How it works (for developers)

The game is a Unity IL2CPP shell around Bandai Namco's native engine (`TLSampleViewerTO12.dll`), whose
gameplay is written in an embedded Lua 5.1 VM.

- `src/BerseriaAP.Plugin` — BepInEx plugin (C#). Installs a native detour on `luaD_pcall` the moment the engine
  DLL is mapped, and uses it as a safe point to run Lua inside the game. `Lua/ap_runtime.lua` patches the game's
  own script modules (treasure boxes, Katz boxes, sparkle points, map links) and exposes a small API that the
  C# side drives. Networking uses Archipelago.MultiClient.Net.
- `src/BerseriaAP.DataTool` — reads the encrypted TLDAT archives (via TalesOfTools, MIT) and generates
  `data/game_data.json`, the single source of truth for ids, names, regions and story order.
- `apworld/tales_of_berseria` — the Archipelago world (Python, required by Archipelago's generator).
- `data/raw` — item/box/name dumps captured from the running game.

Build a release with `./build_release.ps1` (needs the .NET SDK and the game installed).

## Known limitations

- Region locks apply to map exits, warp points, map shortcuts and ship voyages. Story events that move the
  party into a new region by cutscene are not blocked. The warp-point and voyage menus were verified by direct
  calls only; their full in-game flow still needs a check once those features unlock in the story.
- Chests in areas that can only be visited once may be missable; logic assumes regions stay revisitable.
- Boss-check and goal detection use the game's scenario counter and still need a full playthrough to verify.
- DeathLink in `next_battle` mode: scripted story battles may revive the party instead of a game over, so a
  death received just before such a battle has no lasting effect (`immediate` mode always uses a plain battle).
