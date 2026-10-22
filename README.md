# BlazeUma

Career helper for Umamusume: Pretty Derby (Steam). It reads game state from memory
(read-only) and will drive a URA Finale career in advisor → supervised → auto modes.

Status: the memory layer, the game-data layer and the advisor work against the live game. The auto bot
plays trainings, rest, recreation, infirmary, events and races through an in-game agent, and pauses to ask
when it meets a screen or popup it does not handle yet.

## Layout

| Path | What it is |
|---|---|
| `src/BlazeUma.Memory` | Read-only game access: Win32 reads, IL2CPP metadata/runtime, ACTk Obscured decoders, career snapshot |
| `src/BlazeUma.Data` | Game data from the client's `master.mdb` (SQLite): calendar, goals, races, skills, cards, text |
| `src/BlazeUma.Advisor` | Decision engine: targets, value model, turn / event / race / skill advice |
| `src/BlazeUma.Automation` | Injects the in-game agent and talks to it over a named pipe |
| `src/BlazeUma.Cli` | `BlazeUma.exe`: the interactive console app (main menu, Advisor, Auto bot) |
| `agent/` | C++ DLL loaded into the game; runs taps and calls on Unity's main thread |
| `tests/` | Decoder, layout, data and advisor tests |
| `research/` | Verified findings and the Python scripts used to find them |

## Run

Requires Windows x64 and the game running. Build the agent first (see below), then:

```powershell
dotnet publish src/BlazeUma.Cli -c Release    # publish/BlazeUma.exe (self-contained) + BlazeUma.Agent.dll
.\publish\BlazeUma.exe                        # or double-click it
dotnet test BlazeUma.slnx
```

Without arguments it opens on a main menu:

1. **Advisor**: you play; BlazeUma reads the game once a second and shows the recommended action.
   It waits for the game and reconnects after restarts. Esc goes back to the menu, V shows the reasons for
   every option and the stat weights.
2. **Auto bot**: BlazeUma plays the career. **Insert** pauses, **Page Down** stops; both work while the game
   has focus and take effect at the next safe point (before the next action, or the next tap in a race).
   Paused, you can resume or stop; stopped, you can start again, switch to Advisor, go to the main menu or exit.
   A screen the bot does not handle yet, or a popup with a real choice (the Rest confirmation), pauses it and
   says what is needed; you can play by hand meanwhile and resume from any screen.

## How the advisor decides

`CareerAdvisor` looks at the current screen and answers for it:

- **Training turn**: every enabled action is scored in one unit ("one early Speed point"):
  stat gains × per-stat weight, skill points, bond/hint value of partners, the energy
  change (energy is worth more the lower it is), and failure risk
  (`fail% × (gains + failure loss)`). Rest, recreation (mood), infirmary (bad conditions)
  and the best predicted optional race are scored on the same scale.
- **Targets**: derived from the remaining goals. Stamina follows the longest goal
  distance; stat weights shrink as a stat approaches its target and drop to zero at the cap.
- **Events**: expected value over the game's own choice previews. Known trainee events use
  measured odds; others weight outcomes equally.
- **Races**: aptitude must be B or better; fan shortfall for the next goal dominates.
- **Skills**: rating × fit (goal distances, running style, surface) ÷ price; skill points
  are saved until a goal race is today or next turn.

All constants are heuristics in `ValueModel` and the advisors, meant to be tuned.

## Turn log

Advisor mode appends to `logs/career-<cardId>-<time>.jsonl` next to the exe. Each completed turn writes a `turn` record: the start-of-turn snapshot, the advice,
and the measured outcome at the start of the next turn (stat/energy/mood/SP/fan/bond changes,
conditions, skills learned, and a best-guess action such as `training:101`, `outing:9008`, `race`,
`rest`). Each event shown writes an `event` record. The log is what the estimates get tuned from.

### Replay

`replay` re-runs the current advisor on logged snapshots, to see which picks a change flips. No game needed;
game data comes from the `master.mdb` copy in `%LOCALAPPDATA%\BlazeUma` (or `--db <path>`).

```powershell
.\publish\BlazeUma.exe replay logs publish\logs          # one row per turn/event; FLIP marks a changed pick
.\publish\BlazeUma.exe replay logs\career-....jsonl --all  # also the top 3 options with scores, old vs new
```

It ends with counts of records replayed, flips, and records that could not be read or advised (with the reason).

### Event odds

`event-odds` counts what each event choice did (the `event-result` records) and writes `data/event-odds.json`,
which the event advisor loads from beside the exe. A choice with at least 5 logged results uses those odds
(Laplace-smoothed, "odds from N logged results"); hard-coded trainee odds still win, and the rest weight outcomes
equally. Trainee events pool across trainees. The repo copy is `data/event-odds.json`, copied on build.

```powershell
.\publish\BlazeUma.exe event-odds logs publish\logs                          # writes publish\data\event-odds.json
dotnet run --project src\BlazeUma.Cli -- event-odds logs publish\logs --out data\event-odds.json  # the repo copy
```

## In-game agent (automation, in progress)

`agent/` is a small C++ DLL loaded into the game; `src/BlazeUma.Automation` injects it and talks to it
over a local named pipe. It answers `ping` and runs `tap` / `call` on Unity's main thread. The memory reader
stays read-only; the injector is the only part that opens the game with write access.

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" -S agent -B agent/build -G "Visual Studio 18 2026" -A x64
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" --build agent/build --config Release
```

`dotnet publish` copies the built agent next to the exe; the auto bot injects it when it starts.
Events with choices: the bot closes the reward preview and takes the advisor's choice.
Races run end to end: entry confirmation, View Results, the result scenes, goal popups and the new-month
screen, until home on the next turn.

The game loads the agent from a copy in `loaded/<hash>/` beside the DLL, so the agent can be rebuilt
while the game runs; the auto bot swaps an older loaded agent for the current one. (The game refuses DLLs
from `%LOCALAPPDATA%`.)

## How the reader finds things

No heap scanning and no hardcoded addresses. Each launch it:

1. Finds `GameAssembly.dll`, parses its exports and checks the IL2CPP accessor bytes
   the layouts depend on (refuses to run if they changed).
2. Resolves classes by name through `global-metadata.dat` and the runtime class cache,
   and takes every field offset from the runtime field tables.
3. Follows static roots: `Singleton<WorkDataManager>` → `SingleMode` → `Character`,
   `MonoSingleton<SceneManager>` (current screen), `Singleton<StoryManager>` (events).
4. Decodes ObscuredInt/Bool/Long locally after verifying their layouts and keys.

The process is opened with `PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION` only.
A section that cannot be read is reported in `Errors` and left null; values are never
filled with zeros. See `research/*.md` for the evidence behind every field.

## Known gaps

- `Story.LooksActive` is a heuristic from two samples.
- Event outcome odds are not in memory; they need an external table.
- Not yet verified across a game restart.
