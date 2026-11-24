# Visual history

Stills (and, for videos, file names in the gitignored `media/` folder) of every look the game has had. Oldest first.

| File | Version | Source |
|---|---|---|
| legacy (pending capture) | **Original project by Ray**: the April 2026 build, as built | `Unity Project/Build/Nemesis-ai.exe` (exe dated 2026-03-30, data 2026-04-02), captured unmodified |
| 2026-11-23_1852_v1-light-arena-debug-panel.png | v1: light arena, white boss, elevated camera, first AI debug panel | test build before 73c12f6 |
| 2026-11-23_1913_v2-dark-panels-hud.png | v2: dark arena, effects, energy blades, dark HUD panels | dac002d |
| 2026-11-23_1923_v3-minimal-white-hud-fullres.png | v3: minimal white HUD, no panels | 99083b7 |
| 2026-11-23_1927_v3-minimal-white-hud-4up.png | v3, four moments of one fight | 99083b7 |
| 2026-11-23_1929_v3-minimal-white-hud.png | v3 (README preview at the time) | 99083b7 |
| 2026-11-23_1929_v3-minimal-settings-menu.png | v3 settings menu | 99083b7 |
| 2026-11-23_1954_v4-broadcast-hud.png | v4: broadcast versus HUD, Boss AI card | b775261 |
| 2026-11-23_1954_v4-start-screen.png | v4 start screen | b775261 |
| 2026-11-23_1955_v4-start-and-ai-card.png | v4 start screen and AI card close-up | b775261 |

## Recreating a later version as a clip

Fights are deterministic, so any commit from 73c12f6 on can be checked out in a separate worktree, built, and the same fight (seed 21) recorded in that version's look:

```powershell
git worktree add ..\nemesis-archive <commit>
& "..\nemesis-archive\Unity Project\Builds\Windows\Nemesis.exe" -batchmode -nemesisRecord 78 -fps 60 -seed 21 -sentis cpu -ffmpeg ffmpeg -out media\archive_<commit>
```

The original build has no recorder, so it is captured from its window with ffmpeg screen capture instead.
