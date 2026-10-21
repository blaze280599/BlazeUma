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

3. [ ] **Turn log** (code)
   Append every snapshot + advice + what actually happened next turn to a JSONL file per career.
   Needed to measure estimates (outings, rest, race rewards) and to tune `ValueModel` later.
   *Done when:* `--advise --watch` writes `logs/career-<date>.jsonl` with one entry per turn.

4. [ ] **Event advice, live** (live: next event with choices on screen)
   Confirm `LooksActive` turns on, the title is right, and the effects match the in-game Effects
   popup. Also check an event with no choices does not produce stale advice.
   *Done when:* two or more events checked, `LooksActive` confirmed or replaced.

5. [ ] **Skill screen advice, live** (live: Skills screen, ideally the turn before Tulip Sho)
   Check fit/price ranking and that it buys on the turn before a goal race and saves otherwise.
   *Done when:* the buy list is something you'd agree with; hint discounts above level 1 confirmed.

6. [ ] **Measure estimates** (live, uses the turn log)
   Friend outing energy/mood, rest energy, race skill points and stats per grade. Replace the
   guesses in `TurnAdvisor` / `RaceEvaluator` with measured values.
   *Done when:* each estimate in the code cites a measurement.

7. [ ] **Game restart** (live: close and reopen the game mid-career)
   The reader should reattach with no changes.
   *Done when:* snapshot after restart matches the screen; note the result in research/.

## Next: act on the advice

8. [ ] **Click automation, design** (code + live)
   Find the game window, map each action (training buttons, rest, recreation dialog, race list,
   event choices, skills, confirm/next dialogs) to click targets, using the current screen from memory
   to know where we are. Input through SendInput on the game window only.
9. [ ] **Supervised mode**: the bot performs the pick after you press a key; stops on anything unexpected.
10. [ ] **Auto mode**: full career loop from turn 1 to the results screen, with a stop hotkey.

## Later

11. [ ] **Event odds table**: more measured odds beyond Get Well Soon! / Don't Overdo It!
12. [ ] **Tuning pass**: use logged careers to adjust targets and weights.
13. [ ] **Trackblazer scenario module**: shop, items (charms, megaphones, energy drinks), coins.
14. [ ] **Optional LLM presets**: build target/skill presets from guides via OpenRouter, reviewed before use.
15. [ ] **Injected actions** (only after confirming ACTk detectors are inactive in IDA).
