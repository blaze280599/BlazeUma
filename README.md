# BlazeUma

Career helper for Umamusume: Pretty Derby (Steam). It reads game state from memory
(read-only) and will drive a URA Finale career in advisor → supervised → auto modes.

Status: the memory layer, the game-data layer and an advisor (recommendations only)
work against the live game. Supervised automation (`auto run`) taps through trainings, events and races
through an in-game agent; there is no UI yet.

## Layout

| Path | What it is |
|---|---|
| `src/BlazeUma.Memory` | Read-only game access: Win32 reads, IL2CPP metadata/runtime, ACTk Obscured decoders, career snapshot |
| `src/BlazeUma.Data` | Game data from the client's `master.mdb` (SQLite): calendar, goals, races, skills, cards, text |
| `src/BlazeUma.Advisor` | Decision engine: targets, value model, turn / event / race / skill advice |
| `src/BlazeUma.Automation` | Injects the in-game agent and talks to it over a named pipe |
| `src/BlazeUma.Cli` | Snapshot JSON, `--advise`, and the `agent` / `buttons` / `inspect` / `auto` commands |
| `agent/` | C++ DLL loaded into the game; runs taps and calls on Unity's main thread |
| `tests/` | Decoder, layout, data and advisor tests |
| `research/` | Verified findings and the Python scripts used to find them |

## Run

Requires the .NET 10 SDK, Windows x64, and the game running.

```powershell
dotnet build BlazeUma.slnx
dotnet run --project src/BlazeUma.Cli                 # one snapshot
dotnet run --project src/BlazeUma.Cli -- --watch 1    # one JSON line per second
dotnet run --project src/BlazeUma.Cli -- --advise --watch 1   # live advice, redrawn when it changes;
                                                               # waits for the game and reconnects after restarts
dotnet run --project src/BlazeUma.Cli -- --advise --verbose    # reasons for every option + stat weights
dotnet test BlazeUma.slnx
```

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

`--advise --watch` appends to `logs/career-<cardId>-<time>.jsonl` (change with `--log DIR`, disable
with `--no-log`). Each completed turn writes a `turn` record: the start-of-turn snapshot, the advice,
and the measured outcome at the start of the next turn (stat/energy/mood/SP/fan/bond changes,
conditions, skills learned, and a best-guess action such as `training:101`, `outing:9008`, `race`,
`rest`). Each event shown writes an `event` record. The log is what the estimates get tuned from.

## In-game agent (automation, in progress)

`agent/` is a small C++ DLL loaded into the game; `src/BlazeUma.Automation` injects it and talks to it
over a local named pipe. It answers `ping` and runs `tap` / `call` on Unity's main thread. The memory reader
stays read-only; the injector is the only part that opens the game with write access.

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" -S agent -B agent/build -G "Visual Studio 18 2026" -A x64
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" --build agent/build --config Release
dotnet run --project src/BlazeUma.Cli -- agent inject    # load + ping
dotnet run --project src/BlazeUma.Cli -- agent ping
dotnet run --project src/BlazeUma.Cli -- agent unload    # agent removes itself

dotnet run --project src/BlazeUma.Cli -- buttons         # list tappable buttons on the current screen
dotnet run --project src/BlazeUma.Cli -- buttons --all   # include non-tappable
dotnet run --project src/BlazeUma.Cli -- buttons --active  # only buttons really on screen (agent checks the GameObject)
dotnet run --project src/BlazeUma.Cli -- buttons --watch 0.5   # trace screen changes to logs/trace-*.txt
dotnet run --project src/BlazeUma.Cli -- agent tap 0x<addr>   # invoke a button's click on Unity's main thread
dotnet run --project src/BlazeUma.Cli -- agent tap-path Details   # tap by field-path substring (aliases: Rest, Training, Close, Back, …)

dotnet run --project src/BlazeUma.Cli -- auto plan                 # taps the advisor's pick would need (dry run)
dotnet run --project src/BlazeUma.Cli -- auto run                  # supervised: Enter confirms each tap, q stops, a = rest of the action unasked
dotnet run --project src/BlazeUma.Cli -- auto run --action "Train Wit"   # a specific action instead of the advisor's pick
dotnet run --project src/BlazeUma.Cli -- auto run --action "Race: Mile Cup"   # optional race; race day runs the goal race
```

Events with choices: `auto run` closes the reward preview and takes the advisor's choice.
Races run end to end: entry confirmation, View Results, the result scenes, goal popups and the new-month
screen, until home on the next turn. An unmapped screen stops the run and hands back to you.

The game loads the agent from a copy in `agent/build/Release/loaded/<hash>/`, so the agent can be rebuilt
while the game runs; `auto run` swaps an older loaded agent for the current one. (The game refuses DLLs
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
