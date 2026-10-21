# BlazeUma

Career helper for Umamusume: Pretty Derby (Steam). It reads game state from memory
(read-only) and will drive a URA Finale career in advisor → supervised → auto modes.

Status: the memory layer and a snapshot CLI work against the live game. The decision
engine, game-data (master.mdb) layer, input automation and UI are not built yet.

## Layout

| Path | What it is |
|---|---|
| `src/BlazeUma.Memory` | Read-only game access: Win32 reads, IL2CPP metadata/runtime, ACTk Obscured decoders, career snapshot |
| `src/BlazeUma.Cli` | Prints a live `CareerSnapshot` as JSON |
| `tests/BlazeUma.Memory.Tests` | Decoder and collection-layout tests (real captured bytes) |
| `research/` | Verified findings and the Python scripts used to find them |

## Run

Requires the .NET 10 SDK, Windows x64, and the game running.

```powershell
dotnet build BlazeUma.slnx
dotnet run --project src/BlazeUma.Cli                 # one snapshot
dotnet run --project src/BlazeUma.Cli -- --watch 1    # one JSON line per second
dotnet test BlazeUma.slnx
```

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
