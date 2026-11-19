# Tales of Berseria Remastered — Archipelago Integration Plan

## 1. What we're working with (recon results)

| Fact | Consequence |
|---|---|
| Unity 2022.3.62 **IL2CPP** host (`GameAssembly.dll`) | BepInEx 6 (IL2CPP, bleeding edge) can load a C# plugin. |
| All game logic lives in **native** `Plugins/x86_64/TLSampleViewerTO12.dll` (Bandai "Tales" engine, C++) | Unity scripts do almost nothing. The mod must **hook native functions** and read and write native memory from C#. |
| The native DLL keeps lots of descriptive strings (`TreasureBoxDatabase`, `ScenarioBitFlagDatabase`, `ItemManagerSaveData`, `Scenario::CallbackChangeMap_*`, `SetFlag` …) | Reverse-engineering (RE) is feasible: functions can be found through string cross-references. |
| Data is in `TLDAT` + `TOFHDB` archives (same format as the 2017 PC release) | Chest, Katz box and sparkle-point tables can be pulled straight from the data files, so the location list can be generated rather than typed in by hand. |

## 2. Architecture

```
┌──────────────── Game process ────────────────┐
│ Unity (IL2CPP) ── BepInEx 6 ── BerseriaAP.dll (C#, .NET 6)
│                                  │  ├─ NativeHooks   (detours on TLSampleViewerTO12.dll)
│                                  │  ├─ GameState     (read/write inventory, flags, gald)
│                                  │  ├─ ApClient      (Archipelago.MultiClient.Net, WebSocket)
│                                  │  ├─ ItemQueue     (gives received items safely on the main thread)
│                                  │  └─ Overlay/UI    (connect window + message log, IMGUI)
└──────────────────────────────────────────────┘
            ▲ websocket
            ▼
   Archipelago server  ◄──  tales_of_berseria.apworld (Python, required by AP)
```

* **C# mod (most of the work):** a BepInEx plugin, with native hooks through BepInEx/Il2CppInterop detours (or MinHook), plus pattern scanning so it survives small game patches.
* **APWorld (Python, unavoidable):** Archipelago generates seeds in Python only. It holds the item and location tables (generated from game data by a C# tool), the logic regions, the options and slot data.
* **Tools (C#):** `BerseriaDataDump`, a console app that unpacks TLDAT and exports the item, chest and flag tables to JSON. The apworld and the mod are both generated from this JSON.

## 3. How the randomizer works

1. **Location check:** hook the "open chest / Katz box / pick up sparkle point" path. Look up which location ID it is (by its treasure-box ID / flag), suppress the vanilla item, send `LocationChecks`, and show "Sent X to Player Y".
2. **Item receive:** on `ReceivedItems`, queue the item. When the game is in a safe state (field, no menu, no event), call the game's own add-item function (or write the inventory directly) and show a popup.
3. **Persistence:** store the "items received" index in the save (a spare flag or counter) so reloading doesn't duplicate items. Re-sync checked locations from the save's chest flags on connect.
4. **Goal:** defeat the final boss (detect the scenario flag), then send `ClientGoal`.
5. **DeathLink (optional):** detect a party wipe / game over, and trigger a wipe when one is received.

## 4. Phases

| # | Phase | Output | Needs you? |
|---|---|---|---|
| 0 | Tooling: install BepInEx 6 IL2CPP, Ghidra, .NET SDK (installed); confirm the game boots modded | Game starts with the BepInEx console | **Yes:** launch the game once |
| 1 | Data extraction: TLDAT/TOFHDB unpacker in C#, dump item names and IDs plus the treasure-box, Katz-box and sparkle-point tables | `data/*.json` | No |
| 2 | RE + memory: find the inventory, gald, scenario flags, current map, and the "open chest" and "add item" functions | Signatures + offsets | **Yes:** short in-game tests (open a chest, buy an item…) |
| 3 | Mod core: hooks, game-state API, debug console commands (`/give`, `/flag`) | Testable plugin | Yes: test |
| 4 | AP client: connect UI, send checks, receive items, save-safe dedup, goal | Playable vertical slice | Yes: playtest |
| 5 | APWorld: items, locations, regions/logic by story chapter, options | `.apworld`, generated seed | No |
| 6 | Polish: DeathLink, shops (optional), Katz/sparkles toggles, installer/README | Release zip | Yes: playtest |

## Status (2026-11-19)

Phases 0–5 are done and verified in game against a local server: hooks, chest checks with scouted
messages, item delivery (consumables, gald, keys) with a save-bound counter, region-key gating at map
exits, apworld generation (passes Archipelago's world tests), release packaging (`build_release.ps1`).

The architecture changed from the original plan for the better: instead of reverse-engineering native
functions, the plugin hooks the embedded Lua VM and patches the game's own Lua modules (see README).

Still to verify or do:
- Katz box and sparkle checks in game (code written, not yet triggered)
- Boss checks and goal (scenario counter) over real story progress
- Gating for transitions not done via map links (fast travel/warp points, story cutscenes)
- Missable chests in one-time areas

## 5. Risks

* **Linear story:** Berseria has very little natural gating. Logic will mostly be "location X needs story chapter N", unless we add artificial gates (see the open questions).
* Native hooks can crash if they fire mid-cutscene, so item delivery must wait for a safe state.
* The overlay may not draw over the native renderer. Fallback: a separate window, or the game's own text popups.
