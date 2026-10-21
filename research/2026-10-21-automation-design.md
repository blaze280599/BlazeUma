# Automation design: in-process actions (roadmap item 8)

Goal: let BlazeUma perform the advisor's pick itself. The user chose the in-process route
("memory writes") over simulated clicks. This document records what was found (read-only) and the
proposed design. Nothing has been injected yet.

## 1. What "memory writes" means here

- **Not** writing game values (stats, energy, fans, skill points). Results are computed by the server,
  and ACTk's ObscuredCheatingDetector is running (see §3): editing obscured values is detectable and is
  cheating, not automation. This stays out of scope permanently.
- **Yes**: a small agent DLL inside the game calls the same managed methods the game's own buttons
  call (e.g. the Rest confirm handler). The server receives the same requests a tap would produce.

## 2. Action entry points (runtime MethodInfo, current build)

Found with `scripts/class_methods.py`. RVAs are for this build only; the agent must resolve methods by
class + name at runtime (`il2cpp_class_get_method_from_name`), never by RVA.

| Action | Class | Method(s) |
|---|---|---|
| Open training menu | `Gallop.SingleModeMainViewController` | `OnClickTraining`, `OnClickTrainingMenu`, `BackFromTraining` |
| Pick a training | `Gallop.SingleModeMainTrainingController` | `OnSelectTraining` |
| Rest | `Gallop.SingleModeMainViewController` | `OnClickHolidayDecide` → `SendCommandHoliday` |
| Infirmary | `Gallop.SingleModeMainViewController` | `OnClickHospital`, `OnClickHospitalDecide` → `SendCommandHospital` |
| Recreation (dialog) | `Gallop.SingleModeMainOutingController` | `OnClickOuting`, `OnDecideOuting` → `OutingAsync` |
| Generic command send | `Gallop.SingleModeMainViewController` | `SendCommandAsync`, `CommonSendCommandAsync` |
| Open race list | `Gallop.SingleModeMainViewController` | `OnClickRace` (+ `OpenDialogSingleModeRaceEntryWarning`) |
| Open skills | `Gallop.SingleModeMainViewController` | `OnClickSkillLearning` |
| Event choice | `Gallop.StoryChoiceButton` / `Gallop.StoryChoiceController` | `OnClickChoice`, `ChoiceButton`, `IsWaitSelect` |
| Next turn / continue | `Gallop.SingleModeMainViewController` | `OnClickNextMonth` |
| Network layer (avoid) | `SingleModeExecCommandRequest/Task`, `SingleModeRaceEntryTask` | sending these directly would skip the UI flow and desync screens |

Methods of `SingleModeRaceEntryViewController` and `SingleModeSkillLearningViewController` were not
listed: their method tables were not populated at read time (screen not open).

Calling UI-level handlers keeps the game's own flow (animations, dialogs, cut-ins, state updates).
Parameters and the confirm-dialog sequence for each still need IDA (§5).

## 3. Anti-cheat state (live heap scan, turn 28 after a game restart)

| Detector | Live component | started / running |
|---|---|---|
| InjectionDetector | none (one transient instance seen, `started=false`, gone a minute later) | no |
| SpeedHackDetector, TimeCheatingDetector, WallHackDetector | none | no |
| ObscuredCheatingDetector | yes | **yes** (honeypot for edited obscured values) |

The earlier finding (`detectorsContainer` null, `Instance` null) was taken before the restart; after it
the ObscuredCheatingDetector is running. It only reacts to obscured values being changed outside the
game, which this design never does. ACTk's InjectionDetector targets managed assemblies loaded into a
Mono domain; a native agent in an IL2CPP build is outside what it inspects (from ACTk's design, not
verified in this build's code). Server-side detection of automated play (timing, patterns) is a
separate, unmeasurable risk; that is the same for clicks and in-process calls.

## 4. Proposed architecture

```
BlazeUma.Cli (advisor, read-only reader)  ──named pipe──►  BlazeUma.Agent.dll (inside the game)
        ▲                                                         │
        └────────── verifies every result by reading memory ◄─────┘
```

- **Injector** (new, small, separate from the read-only reader): `OpenProcess` with
  `CREATE_THREAD | VM_OPERATION | VM_WRITE | QUERY_LIMITED`, write the DLL path, `CreateRemoteThread`
  on `LoadLibraryW`, then start the agent's exported entry. The read-only `GameProcess` stays as is.
- **Agent DLL**: native (C++ with MinHook, or C# NativeAOT exporting an `UnmanagedCallersOnly` entry).
  1. Attach to IL2CPP (`il2cpp_domain_get`, `il2cpp_thread_attach`).
  2. Hook one per-frame managed method (e.g. a `Gallop.SceneManager` update) so commands run on Unity's
     main thread; Unity and game objects must not be touched from other threads.
  3. Each frame, take at most one queued command, resolve the target instance (same roots as the
     reader: SceneManager current view controller, StoryManager), check the expected screen, and call the
     handler with `il2cpp_runtime_invoke` (catches managed exceptions instead of crashing the game).
- **Protocol** (pipe, one command at a time): `status`, `train <commandId>`, `rest`, `infirmary`,
  `outing <charaId|trainee>`, `race <programId>`, `choice <n>`, `learn <skillId,...>`, `next`.
  Each reply is `ok` or `refused: <reason>` (wrong screen, button disabled, unknown target).
- **Safety**: a command is only sent when the reader's snapshot shows the expected screen; after
  sending, the CLI waits for the snapshot to change as expected (turn advanced, stats changed) and stops
  on anything unexpected; global stop hotkey; the agent unhooks and unloads on `quit`.

## 4a. IDA findings (GameAssembly.analysis.dll, bytes verified identical to the live build)

50k runtime method names were applied to the IDA database as `bu_<Class>__<Method>`
(`scripts/method_names.py` dumps them; only `sub_` names were replaced).

- `SingleModeMainTrainingController.OnSelectTraining(this, bool)` only **previews** a training: it
  updates the status panel, background, character model, HP gauge and skill-point panel. No request.
- `SingleModeMainViewController.OnClickTraining(this)` only **opens** the training menu (canvas lock,
  play-in animation, back button). The commit runs inside a coroutine (compiler-generated `MoveNext`)
  that calls `SendCommandAsync(int, int, int, int, ptr, ptr)`; its class was not loaded yet this session.
- Rest: `OnClickHolidayDecide(this)` → `SendCommandHoliday(this)` → `CommonSendCommandAsync(this, int, int)`.
  Infirmary: `OnClickHospitalDecide` → `SendCommandHospital` → same. `OnClickNextMonth` also leads to
  `SendCommandHoliday` (the summer/"next month" path).
- `OutingController.OutingAsync(this, ptr)` has no direct callers (called from a coroutine/lambda).

Conclusion: calling handlers directly needs per-action reverse engineering of coroutines and dialog
flows. **Virtual taps** avoid that: every button is a `Gallop.ButtonCommon` (Unity `Selectable`) with
`OnPointerClick` (tap handler), `m_OnClick` (+0x100), `m_Interactable` (+0xD8), `_isEnable` (+0x1A1).
Triggering a button's own click runs exactly the flow a tap runs (locks, dialogs, coroutines, requests),
for training tiles, Rest/Recreation/Races/Skills, confirm dialogs, event choices and "Next".

The command buttons are not direct fields of `SingleModeMainView`, so the agent finds buttons in the
scene by GameObject name/path using Unity's API in-process. A `buttons` diagnostic lists the active,
interactable `ButtonCommon`s and their names so each action can be mapped from the live game.

## 4b. Agent build

- Toolchain present: Visual Studio 2026 Community, MSVC 14.51, bundled CMake. C++ agent, no installs.
- Main-thread dispatch without a code-patching hook library: replace the `methodPointer` of one
  per-frame managed method's `MethodInfo` with the agent's function, which runs queued work and then
  calls the original. To verify first: that Unity invokes that method through its `MethodInfo` each
  frame. Fallback: an inline hook (MinHook, vendored) if it does not.
- Steps, each tested live before the next:
  1. Injector (C#, separate from the read-only reader) + agent DLL that answers `ping` over a pipe.
  2. `buttons`: list active `ButtonCommon`s with names and interactable state (still no game action).
  3. `tap <button>` on the main thread; first used for "Rest" on a turn where resting is fine.
  4. Map every advisor action to taps; CLI `--auto` in supervised mode (confirm each with a key).

## 5. Open questions before writing the agent

1. Exact signatures and call order per action: e.g. does `OnSelectTraining(int commandId)` select or
   also confirm; which dialog methods confirm Rest / outing / race entry. Needs IDA on the
   `GameAssembly.analysis.dll` database (currently another database is open in IDA).
2. Which per-frame method to hook (stable, always running in the career scene).
3. Async handlers (`SendCommandAsync`, `OutingAsync` return UniTask): calling the synchronous click
   handler that starts them is safer than invoking the async method directly.
4. Agent language: C++ + MinHook (smallest, proven) vs C# NativeAOT (one language for the project).

## 6. Fallback

Simulated clicks (SendInput on the game window, positions by screen from memory) remain possible if the
in-process route proves fragile. The advisor/reader side is identical for both.
