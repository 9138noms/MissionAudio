# MissionAudio

Nuclear Option BepInEx plugin that adds a **"Play Audio"** outcome type to the mission editor. Ship custom `.mp3` / `.wav` / `.ogg` / `.aiff` files from a shared plugin library and trigger them on objective completion.

![outcome-menu-screenshot-placeholder](.github/outcome-menu.png)

## Features
- New **"Play Audio"** option in the Outcomes dropdown (injected under the vanilla types)
- Dropdown picker for audio files from the plugin's shared library (`BepInEx/plugins/MissionAudio/audio/`)
- Per-outcome volume (0.0–2.0)
- Sidecar `audio.json` stored next to the mission JSON — no vanilla schema changes
- Auto-stops on mission unload / main menu return
- Graceful fallback: vanilla clients without the mod see a no-op `ShowMessage` outcome (no audio, no text glitches)

## Requirements
- Nuclear Option **0.34** — v1.0.1 is built and tested on 0.34.6. v1.0.0 does not work on this version: it was built against the game's old `SavedMission.ObjectiveV2` API, which no longer exists.
- **BepInEx** 5.x

## Installation
1. Requires **BepInEx** (5.x or compatible) already set up for Nuclear Option.
2. Extract the release zip into `<Nuclear Option>/`. Resulting layout:
   ```
   BepInEx/plugins/MissionAudio/
   ├── MissionAudio.dll
   └── audio/                   (auto-created on first launch)
   ```
3. Drop any `.mp3` / `.wav` / `.ogg` / `.aiff` files into `BepInEx/plugins/MissionAudio/audio/`.
4. Launch the game.

## Usage (mission authors)
1. Open the mission editor.
2. Edit an objective → click **+** in the Outcomes list.
3. In the "새 결과 / New Outcome" type dropdown, pick **Play Audio** (appears below the vanilla types).
4. Give it a name → click Create.
5. In the Edit Outcome panel:
   - **Audio File** dropdown — pick a file from your shared library
   - **Volume (0~2)** — optional, defaults to 1.0
6. Save the mission.

When the objective completes at runtime, the selected audio plays. Map/config is stored as `audio.json` alongside the mission JSON.

## How it works
- New outcomes can't be added to the `OutcomeType` enum (it's baked into the game DLL), so MissionAudio reuses `ShowMessage` as a carrier: Play Audio outcomes are stored as `ShowMessage` with an empty `Message`, `PlaySound=false`, and a `__MA__`-prefixed `UniqueName`.
- Runtime Harmony Prefix on `ShowMessageOutcome.Complete` detects the `__MA__` prefix and plays the mapped audio instead of showing a message.
- The `audio.json` sidecar maps each `__MA__` outcome UniqueName to its audio file + volume.

## Sharing missions
- The `audio.json` sidecar travels with the mission folder. Include it when zipping / sharing a mission.
- Audio files live in the **player's** plugin folder, not the mission folder. Share your `audio/` folder contents separately (or bundle them with the mission archive and tell players where to put them).
- Players without the mod can still open and play the mission — they just won't hear the custom audio (the carrier outcome is a no-op).

## Troubleshooting
BepInEx log (`BepInEx/LogOutput.log`) contains:
- `MissionAudio v1.0.1 loaded. Library: ... (N files)` — plugin initialized
- `MissionAudio: played 'X' vol=Y` — audio fired
- `MissionAudio: auto-created N entry/entries ...` — mission had Play Audio outcomes but audio.json was missing/stale; entries defaulted to the first library file
- `MissionAudio: load failed ...` — audio file format issue (try re-encoding to 16-bit PCM WAV or 128kbps MP3)

For verbose tracing, enable `Debug` level in `BepInEx/config/BepInEx.cfg`:
```
[Logging.Console]
LogLevels = All
```

## Limitations
- No network sync — each client plays locally. Since objective completion is server-synced, audio fires at (roughly) the same time on all clients who have the mod installed.
- Only one audio stream at a time per client (newer plays cut off the older).
- Vanilla `OutcomeType` enum can't be extended, so this piggybacks on `ShowMessage`. If a future game update breaks this mapping, the mod will need patching.

## Build from source
```
dotnet build -c Release -p:NuclearOptionDir="C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option"
```
Output: `bin/Release/net472/MissionAudio.dll`. The `-p:NuclearOptionDir=` argument can be omitted if the game is at that default Steam path.

The game's assemblies and BepInEx are referenced from your install, never copied into this repository.

## License
MIT. See [LICENSE](LICENSE).
