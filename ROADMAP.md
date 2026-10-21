# BlazeUma roadmap

Work through these in order, one at a time. Each item says how we know it is done.
"Live" items need the game open on a specific screen; the rest are code only.

## Now: make the advisor trustworthy

1. [x] **NPC training partners** (code)
   Director Akikawa, Etsuko Otonashi, Happy Meek etc. appear on trainings with bond ids above 100.
   Show every partner by name and bond in the reasons, value NPC bond at half a support card's
   (they never give friendship trainings), and flag friendship trainings (card at 80+ bond on its
   specialty training).
   *Done when:* reasons read like "Silence Suzuka 29, Director Akikawa 16" and tests cover NPC value.

2. [x] **Race list advice, live** (live: Races screen on a turn with races, e.g. turn 14+)
   Check the ranking and reasons against what a sensible player would enter (aptitude, fans for
   Tulip Sho, distance).
   *Done when:* the advice matches the screen's list and the pick makes sense; fix anything that doesn't.
   Result (turn 14): list matched the screen (Hakodate Junior Stakes, Chukyo Junior Stakes). Fixed: mood now
   scales the chance to place, fan need only counts as urgent when few suitable race turns remain before the
   goal, and the race screen compares racing with the best non-race action ("Go back: …").

3. [x] **Turn log** (code)
   Append every snapshot + advice + what actually happened next turn to a JSONL file per career.
   Needed to measure estimates (outings, rest, race rewards) and to tune `ValueModel` later.
   *Done when:* `--advise --watch` writes `logs/career-<date>.jsonl` with one entry per turn.
   Result (turns 16–17): two turn records and two event records; inferred actions right
   (`outing:9008`, `training:101`). Both events' predicted effects matched the measured outcome exactly
   (Regolith bottom: Spd +20, Gut +20, mood +1, bond +5; Scoop top: Sta +10, Etsuko +5).

4. [ ] **Event advice, live** (live: next event with choices on screen)
   Confirm `LooksActive` turns on, the title is right, and the effects match the in-game Effects
   popup. Also check an event with no choices does not produce stale advice.
   Progress: `LooksActive` was true for both logged events on turns 16–17 and false back on the home
   screen; titles and effects were right. Still to check: a no-choice event.
   Built: an active event with no choices now says "no choice to make, continue" instead of showing
   training advice, and choices whose `_cachedRewardEventId` differs from the showing event's id get a
   warning. On the home screen (turn 18) the previews were empty and the id was 0, so the game clears
   them after each event. Next: confirm on a live no-choice event (the log records it).
   Found while playing (turn 26): the home screen offered "URA Finale Qualifier" in Classic January
   because `race_permission` 5 was treated as "any year"; every permission-5 program is a URA Finale race,
   so 5 now means the finale period only. Fan requirements for goals are now urgent when two or fewer
   race turns remain before the deadline (missing one fails the goal), with a warning note.
   Turn 26: after winning the Wakagoma Stakes, Reporter Otonashi's scene (+500 fans, +15 bond) played and
   the log has no event record for it, so `LooksActive` (StoryViewController save/select flags) seems to
   be true only for events with choices. Fine for advice; automation (item 8) needs another "a story is
   playing" signal, e.g. SceneManager/StoryManager view state.
   *Done when:* two or more events checked, `LooksActive` confirmed or replaced.

5. [x] **Skill screen advice, live** (live: Skills screen, ideally the turn before Tulip Sho)
   Check fit/price ranking and that it buys on the turn before a goal race and saves otherwise.
   *Done when:* the buy list is something you'd agree with; hint discounts above level 1 confirmed.
   Turn 28 fixes (before the Tulip Sho): skills needing other skills to fire (`activate_count_*`,
   e.g. Racing Spirit: Wit) count 25% unless the trainee owns enough skills; situational clauses
   (overtaking, boxed in) count 60%; before a goal race only skills that work at that race's distance and
   surface are bought (Rosy Outlook / Up-Tempo wait for a medium goal). Also fixed: fans expected from
   goal races in between (~40% of 1st-place fans) now count toward a later goal's fan requirement, and
   goal turns no longer count as optional race turns, so turn 28 stopped recommending a race at 31 energy.
   Live shop (turn 28): prices and discounts read from memory; hint level 2 = 20% confirmed (Steadfast
   160 → 128); gold skills are priced with their white prerequisite (Sturm und Drang 342 = 162 + 180).
   Buy list: Preferred Position + Pace Chaser Savvy ◎ (297 of 341 SP). Also found and fixed: the stamina
   urgency boost stacked once per upcoming goal (weight 2.84); it now applies once (1.26).

6. [ ] **Measure estimates** (live, uses the turn log)
   Friend outing energy/mood, rest energy, race skill points and stats per grade. Replace the
   guesses in `TurnAdvisor` / `RaceEvaluator` with measured values.
   *Done when:* each estimate in the code cites a measurement.
   Measured so far: Light Hello outing = mood +1 and bond +5 (twice). Energy not measurable yet (was
   full). The chain event at step 3 was worth far more than the +8 estimate (Spd +20 / Gut +20, or
   energy +80), so `OutingChainStepValue` is likely too low.
   OP race, 1st place (Wakagoma Stakes, turn 26): energy −15 (estimate −15), Wit +5 (estimate 5),
   skill points +32 (estimate 25), fans +2,419 from the race (base 2,000, so about +21% bonus) plus
   +500 from the reporter's post-race scene.

7. [x] **Game restart** (live: close and reopen the game mid-career)
   The reader should reattach with no changes.
   *Done when:* snapshot after restart matches the screen; note the result in research/.
   Result: after closing the game, restarting it and resuming the career, the advisor worked with no
   code or layout changes (static roots and class lookup survived the new process). Watch mode now also
   detects the game closing, shows "Umamusume was closed" and reconnects by itself when it restarts;
   the main menu shows "not in a career" instead of a retry error (the turn re-check ignored `_isPlaying`).

## Next: act on the advice

8. [ ] **Automation, design** (code + live)
   User chose in-process actions (agent DLL calling the game's own button handlers) over clicks; never
   writing game values. Design and findings: `research/2026-10-21-automation-design.md` (action entry
   points found, anti-cheat state checked: only the ObscuredCheatingDetector honeypot runs).
   IDA pass done: handlers either only preview/open UI or commit inside coroutines, so the agent will use
   virtual taps on `Gallop.ButtonCommon` (same flow as a real tap) found by name in the scene.
   Next: agent step 1 (injector + `ping`), then `buttons`, then `tap` (design doc §4b).
   Step 1 done: `agent/` (C++ DLL, static CRT, pipe `BlazeUma.Agent.<pid>`) and `BlazeUma.Automation`
   (injector, pipe client); `BlazeUma.Cli agent inject|ping|unload`. Live: injected into the running
   game twice, ping answered, unload removed the module both times, game and reader unaffected.
   Step 2 done (as a read-only C# walk, not in the agent — the agent would gain nothing from
   duplicating this): `ButtonFinder` walks `SceneManager._currentViewBase`/`_currentViewController`
   recursively for `ButtonCommon`-derived fields. `BlazeUma.Cli buttons` prints the paths + address +
   enable/interactable. Turn 28 live: all 5 home command buttons found by name (`_trainingButton`,
   `_outingButton`, `_raceRegistButton`, `_skillGetButton`, `_nextMonthButton`=Rest), with
   `_hospitalButton` correctly non-interactable.
   Step 3 done (first in-game action from BlazeUma): `BlazeUma.Cli agent tap <addr>` resolves the
   button's `m_OnClick` UnityEvent and `UnityEngine.Events.UnityEvent.Invoke`'s native function +
   MethodInfo, then asks the agent to call `fn(event_obj, mi)` on Unity's main thread via a
   `WH_GETMESSAGE` hook (no code patching). Live: tapped `DetailButton` on turn 28, Umamusume Details
   panel opened as if the user had tapped it, game and reader unaffected afterwards, `agent unload`
   removed the hook and module cleanly.
   Step 4e: races up to entry. Race-day home (turn 29) disables Training/Rest/Recreation/Races and shows
   `_targetRaceRegistButton` (Race!) and `_targetRaceSkillGetButton`. The race list
   (`SingleModeRaceEntryViewController`) has rows `Controller._itemList[i]._entryButton` (select),
   `View.EntryButton` (green Race), Reserve/arrows, and `_currentSelectedProgram`; the goal row was
   pre-selected (Tulip Sho, program 185). New entry state 5 = blocked because a goal race is due today.
   `RaceEntryScreen` reads rows / selection / Race button; plans "Goal race" (race day) and
   "Race: <name>" (optional race) go open list → select row (skipped if selected) → Race.
   Screens after entry are not mapped yet: `buttons --watch` traces each screen change to
   `logs/trace-*.txt` so one hand-played race can map them.

   First supervised action (turn 28 → 29): `auto run` skipped the Recreation tap (picker already open),
   tapped Light Hello's row after confirmation, and saw the turn advance in 2.5 s. Measured outing (final
   chain step, 4 → 5): energy 31 → 63 (+32, estimate was +20), bond +5, Speed and Guts +6 each, mood
   already Great. Race day: the closed-shop skill prediction priced gold skills without their white
   version (See Ya Later! shown as 144; real ≈ 304 with Playtime's Over!); fixed and tested.

   Step 4d: Recreation picker. First live `auto run` (turn 28) tapped Recreation and detected the dialog
   in 0.3 s, but offered 13 meaningless buttons: DialogCommon keeps 4 layout templates in
   `_dialogObjArray` (now skipped; only `_currentDialogObj` is shown) and the rows themselves live in the
   dialog's contents GameObject. The picker is `Gallop.DialogSingleModeOutingPartnerSelect`; its
   `_itemList` rows have `_decideButton` and `OutingInfo` (`_masterOuting.CommandGroupId` 301 = trainee,
   390 = friend card, plus `_equipSupportCard` with deck slot / card id). Nothing managed points at it, so
   `OutingPicker` finds it with a bounded heap scan (`HeapScanner`, ~0.4 s) and accepts only the one whose
   `_dialog` is open. Plan for "Recreation: X" taps the matching row; live: Light Hello → row 0 (390),
   trainee → row 1 (301). `_inputBlock` is never offered (it can act as cancel).

   Step 4c: supervised runner. `TrainingMenu` reads each training tile's command id (from its TurnInfo)
   and `_isSelect` (live turn 28: 0 Speed 101 selected, 1 Stamina 105, 2 Power 102, 3 Guts 103, 4 Wit 106;
   a selected tile executes on the first tap). `ButtonTapper` (Automation) is the shared tap path and
   auto-injects the agent. `ActionPlanner` maps advisor picks to steps (Training: open menu, select tile
   unless selected, tap selected tile; Rest / Recreation / Infirmary: one tap, then dialogs).
   `auto plan` prints the steps; `auto run` asks before every tap, waits for the expected result
   (menu open, tile selected, dialog, turn advance or event), and lists any dialog's buttons so the user
   picks. Races, skills and events are reported as "do by hand". Dry runs checked live on turn 28.
   Also: UIManager footer walked (Back/Skip/Quick/Log/Menu), `Back` alias.

   Step 4b: ButtonFinder walks List<T> and reference arrays too, with defensive catches so a bogus
   pointer in one field doesn't abort the whole walk (parent chain capped at 10, child reads guarded).
   Buttons command no longer calls full Snapshot() (which can race during screen transitions).
   Live on turn 28 training menu: found all 5 training tiles at `View.TrainingFooter._items[0..4]._button`
   and the 5 partner-card icons at `View.TrainingHorse._iconArray[0..4]._myButton`. Back/Skip/Quick
   buttons live deeper than depth 5 or outside the walked roots; to be found when needed.

   Step 4a done (open/close dialog cycles end-to-end):
   - `SingletonInstance` now checks the class itself (not just its parents), so DialogManager resolves.
   - `ButtonFinder` also walks `DialogManager._dialogList`; dialog buttons appear as `Dialog[i]...`.
   - `BlazeUma.Cli agent tap-path <query>` taps a button whose field path contains the query,
     with aliases: Rest/Training/Recreation/Races/Skills/Infirmary/Hint/Details/Close/OK/Confirm/Cancel.
   - Live on turn 28: open Details via `tap-path Details`, closed via `tap-path Close` (routes to
     `Dialog[0]._currentDialogObj._centerButton`), opened Hint via `tap-path Hint`. Game and reader
     unaffected, agent unloaded cleanly. In-view overlays like the Tazuna hint are not DialogManager
     dialogs; they usually self-dismiss or need their own button, to be handled per-case later.
9. [ ] **Supervised mode**: the bot performs the pick after you press a key; stops on anything unexpected.
10. [ ] **Auto mode**: full career loop from turn 1 to the results screen, with a stop hotkey.

## Later

11. [ ] **Event odds table**: more measured odds beyond Get Well Soon! / Don't Overdo It!
12. [ ] **Tuning pass**: use logged careers to adjust targets and weights.
13. [ ] **Trackblazer scenario module**: shop, items (charms, megaphones, energy drinks), coins.
14. [ ] **Optional LLM presets**: build target/skill presets from guides via OpenRouter, reviewed before use.
15. [ ] **Injected actions** (only after confirming ACTk detectors are inactive in IDA).
