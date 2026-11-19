# Tales of Berseria Remastered — Archipelago Setup

## Requirements

- Tales of Berseria Remastered for PC (version 1.5.1.0)
- [BepInEx 6 (IL2CPP, bleeding edge build 793 or newer)](https://builds.bepinex.dev/projects/bepinex_be)
- The BerseriaAP mod (`BerseriaAP` folder from the release zip)

## Installing

1. Extract the BepInEx `BepInEx-Unity.IL2CPP-win-x64` zip into the game folder (the folder that contains
   `Tales of Berseria Remastered.exe`).
2. Start the game once and wait for the title screen (the first start takes longer while BepInEx prepares
   itself), then quit.
3. Copy the `BerseriaAP` folder from the release into `BepInEx/plugins/`.

## Connecting

1. Start the game. The **Archipelago window** appears on top of the game (press **F1** while the game is
   in front to show/hide it).
2. Enter the server (for example `archipelago.gg:38281`), your slot name and the room password if any,
   then press **Connect** (or Enter). The window shows the connection status and the server's messages.
3. Load a save (or start a new game). An in-game message box confirms the connection.

The settings are saved to `BepInEx/config/berseria.archipelago.cfg` and the mod connects automatically
on the next start. Editing that file while the game runs also reconnects.

## Notes

- Use a **fresh save** for each seed. The first time a save connects it is bound to that seed; the mod
  will not deliver items into a save that belongs to a different seed.
- Received items are counted inside the save file, so loading an older save re-delivers exactly the
  items that save is missing.
- When a region is locked, walking into a map exit that leads there shows a message naming the
  Region Key you need.
