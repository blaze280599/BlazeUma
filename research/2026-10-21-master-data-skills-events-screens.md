# Master data, goals, skills, events, screens (2026-10-21)

Continuation of `2026-10-21-root-chain-and-turn-data.md`; same process (PID 3944).
Turn 3 confirmed after the user's second Speed training.

## 1. Master data (static game data)

`<Steam>\UmamusumePrettyDerby\UmamusumePrettyDerby_Data\Persistent\master\master.mdb`
is plain SQLite 3 (416 tables, 17.3 MB). Read a copy, never the live file (the
game updates it). English text is in `text_data(category, index, text)`.

Text categories confirmed by sampling:

| category | content |
|---|---|
| 6 / 170 | character names (by chara_id) |
| 28 | race names by race_instance id |
| 32 / 33 | race names by race id |
| 47 | skill names (by skill id) |
| 75 | support card titles |
| 181 | story / event titles (by story_id) |
| 189 | scenario-ish titles (unverified) |

## 2. Career goals (route races): fully resolved

Character `_routeRaceIdArray` (decoded 22, 23, 24…) = `single_mode_route_race.id`.
Route set = `single_mode_route(chara_id → race_set_id)` (Daiwa Scarlet 1009 → 1009).
`condition_id` = `single_mode_program.id` → `race_instance_id` → `race_instance.race_id`
→ `race(grade, course_set)` → `race_course_set(distance, ground, inout, turn …)`.
`condition_value_1` = required finishing place (0 = just run). Rows sharing a
`sort_id` are alternatives (Oaks / Derby, `determine_race` 1/2).

Daiwa Scarlet route: T12 Make Debut (2000 turf, run) → T29 Tulip Sho G2 (top 5)
→ T31 Oka Sho (top 5) → T34 Oaks or Derby 2400 (top 5) → T44 Shuka Sho (top 3)
→ T45 QE II Cup (top 3) → T54 Osaka Hai (top 3) → T68 Tenno Sho Autumn (1st)
→ T72 Arima Kinen 2500 (1st) → T74/76/78 URA (10001/2/3, 1st).
Grade codes: 100 = G1, 200 = G2, 900 = debut. Ground 1 = turf.
`single_mode_program.need_fan_count` gives fan gates; `single_mode_turn` maps turn
→ year/month/half; optional races per turn = programs matching month/half and
`race_permission` (to be verified against the race list screen).

### 2a. Race list prediction (to verify after debut)

`scripts/predict_races.py TURN` lists `single_mode_program` rows (program_group 0)
for the turn's month/half (`single_mode_turn`, turn_set_id 1 = URA). Hypotheses:
`race_permission` 1 = junior, 2 = classic, 3 = senior, 4/5 = combinations
(Arima Kinen has rows with 2, 3 and 4; Osaka Hai program 3 has 4); `race.grade`
100 G1, 200 G2, 300 G3, 400 OP, 700 Pre-OP, 800 Maiden(?), 900 Debut, 999/1000
unknown (special/URA?). Predicted: T13 maidens only (1150 dirt, 1200 / 1800 turf);
T14 Hakodate Junior Stakes G3 1200, Chukyo Junior Stakes OP 1600 (fans >= 350) +
maidens; T15 Phoenix Sho / Dahlia Sho / Cosmos Sho OP; T16 Niigata Junior Stakes G3
1600, Clover Sho OP. Maidens should disappear once the debut is won.

### 2b. Race entry screen (turn 12, debut race day)

View controller `SingleModeRaceEntryViewController` (view id 0x4BA):
`_itemList` (+0x30) List<DialogSingleModeRaceListItem>, `_displayTurn` (+0x70),
`_nextReservedTurn`/`_prevReservedTurn`, `_currentSelectedProgram` (+0x80).
Item: `<Program>` (+0x140) = SingleModeProgram master row (Id, ProgramGroup,
RaceInstanceId, RacePermission, Month, Half, NeedFanCount, FanSetId, RewardSetId…),
`_programEntryState` (+0x148), `IsRouteRace` (+0x14C), `IsReservedProgram`
(+0x14D), `_character`. Script: `scripts/race_list.py`.
Observed: one item, program 1067 (group 1006, perm 1, debut, 2000 m turf) = the
route goal; screen: "Junior Make Debut, Kyoto Turf 2000m (Med) Right / Inner,
+700 fans, Goal". Goal programs use their own ProgramGroup (1006), so predictions
must include route programs, not only program_group 0. Optional-race list still
to verify on turn 13+.
**Optional race list verified (turn 13, after the debut win).** Screenshots of the
schedule list: T13 "There are no races to compete in" (maidens drop out after a
debut win); T14 Hakodate Junior Stakes G3 1200 + Chukyo Junior Stakes OP 1600;
T15 Dahlia Sho 1400 + Phoenix Sho 1200 (+ more below the fold); T16 Niigata Junior
Stakes G3 1600 + Clover Sho OP 1500; T17 Sapporo Junior Stakes G3 1800 + Kokura
Junior Stakes G3 1200; T18 Kikyo Stakes OP 1400 + Fuyo Stakes OP 2000. All match
`predict_races.py` (non-maiden rows). The user had scrolled to turn 43 when memory
was read: the 11 items in `_itemList` equal the corrected prediction for T43
exactly. `_displayTurn` follows the scrolled month, not the current turn.
`race_permission` corrected: 1 junior, 2 classic only, 3 classic + senior,
4 senior only (5 unverified). `_programEntryState`: 0 = enterable, 3 = locked
(fan requirement not met: 1,800–12,000 needed, 800 owned).
Fans for 1st place = `single_mode_fan_count(fan_set_id, order = 1)`: G3 3,100,
OP 1,600, debut 700, all matching the screen. The debut actually gave +799
(1 → 800), so some bonus applies on top of the base; check after the next race.

Mood enum completed: screen AWFUL with `_motivation` = 1 (so 1 Awful … 3 Normal,
4 Good; 2 Bad and 5 Great by elimination). Energy 69.

## 3. Skills

- Base learnable list: `available_skill_set` where `available_skill_set_id = card_id`
  (100901 → Competitive Spirit ○ 90, Up-Tempo 160, Preferred Position 180,
  Stamina to Spare 180 [rank 2], Killer Tunes 160 [3], Prepared to Pass 180 [4],
  Race Planner 180 [5]). `need_rank` is presumably the awakening level gate
  (character TalentLevel = 1 now). Verify against the in-game skill screen.
- Cost: `single_mode_skill_need_point(id = skill_id).need_skill_point`. Hint discount
  formula still to verify in game.
- Owned: character `_acquiredSkillList` (AcquiredSkill `_masterId`, `_level`);
  10091 Red Ace (unique) level 2 = `card_rarity_data.skill_set`.
- Hints: character `_skillTipsList` → `SkillTips{GroupId, Rarity, Level}` (ObscuredInt).
  Resolve with `skill_data where group_id = GroupId and rarity = Rarity`. Turn 7:
  {20071, 1, 1} = Rosy Outlook (200712, base 160); same group rarity 2 =
  Trackblazer (200711, 160). Discounted cost on screen still to confirm.
- **Skill learning screen (turn 7, confirmed).** SceneManager view controller =
  `SingleModeSkillLearningViewController` (view id 0x578). `RemainingPoint` (+0x58) =
  154 = screen. `_skillInfoList` (+0x40) List<SkillInfo>; `SkillInfo._skillList`
  List<Info>; Info fields (plain): SkillId, Level, IsAcquired, ChangeLevel, IsGet,
  IsLock, StatusType, StatusValue, NeedPoint, CalcNeedPoint, OriginNeedPoint,
  DiscountRateForDisplay, HintLv, IsSelected, MasterData, NeedPointMasterData,
  IsSkillUpgrade. Script: `scripts/skill_shop.py`.
  Observed: Red Ace (IsGet, "Obtained"), Competitive Spirit ○ 90 (+ ◎ 110 in the
  same group = upgrade tier), Preferred Position 180, Rosy Outlook 144 (origin 160,
  hint lv1, 10% discount), Up-Tempo 160. All prices confirmed by user screenshots
  (Rosy Outlook shows "Hint Lvl 1 10% OFF!" 144). Exactly the need_rank 0 skills of
  `available_skill_set` plus the hint: talent gating confirmed. The game computes
  the discounted price, so the bot reads NeedPoint instead of a formula. The list
  only exists while the screen is open; outside it, predict from master data +
  `_skillTipsList` (10% per hint level observed at lv1).
- Turn 7 after Wit training: Wit 95→107, energy 43→48 (Wit +5 energy confirmed).
- Skill effects/conditions: `skill_data` (ability types/values, condition strings,
  rarity, grade_value, tag_id) for the scorer.
- Aptitude encoding observed 1..8 (card base 7 = A; char shows inheritance changes).

## 4. Events

Types (runtime field tables):

- `Gallop.SingleModeEventInfo`: event_id, chara_id, story_id, play_timing (int),
  event_contents_info → EventContentsInfo, succession_event_info, minigame_result.
- `Gallop.EventContentsInfo`: support_card_id, show_clear, show_clear_sort_id,
  choice_array (ChoiceArray[]), is_effected_multi_chara, tips_training_partner_id.
- `Gallop.ChoiceArray`: select_index, receive_item_id, target_race_id,
  gain_select_id_index, select_icon.
- `Gallop.SingleModeEventAccesor`: wraps `_eventInfo`.
- `WorkSingleModeData.EventChoiceRewardDict` (+0x168), values
  `EventChoiceReward{select_index, gain_param_array[EventChoiceRewardGainParam{display_id,
  effect_value_0..2}]}`. Likely the server-provided per-choice effect preview.
  Empty at the training home screen.
- Master: `single_mode_story_data` (story_id → card/support card, event_category),
  `single_mode_event_choice_reward`, titles in text category 181.

Pending: catch a live event with choices to locate the holder of the current
SingleModeEventInfo and confirm EventChoiceRewardDict contents.

### 4a. Live event with choices (turn 5)

With a two-choice event on screen, the SceneManager still reported
`SingleModeMainViewController` (events play as an overlay; prev scene was a
StoryScene). Choice previews were populated:

`WorkSingleModeData.EventChoiceRewardDict` = Dictionary<ObscuredInt choice, EventChoiceReward>;
entries are 40 bytes (hash, next, 20-byte ObscuredInt key at +8, pad, value at +32).
`EventChoiceReward._branchRewardArray` → `EventChoiceBranchReward[]` (one per
possible outcome) → `_gainParamArray` → `EventChoiceRewardGainParam{_displayId,
_effectValue0..2}` (ObscuredInt). Decoder: `scripts/event_rewards.py`.

Display formats: `text_data` category 394, indexed by display id; value meaning
per slot in `single_mode_event_choice_reward(effect_value_type_0..2)`. Examples:
1/2 = "{param} +/−{n}" (param ids as in training gains: 1–5 stats, 10 energy,
30 skill pts; 20 inferred = mood), 4 = friendship, 6 = hint lvl, 8 = random skill,
9/37 = become condition, 10 = cures, 13 = all stats, 19/20 = previously trained
attribute ±. Conditions: `single_mode_chara_effect`, names text 142, descriptions 143
(6 = Practice Poor, 10 = Practice Perfect ○).

Observed preview:

| Choice | Branch | Effects |
|---|---|---|
| 1 | 0 | Mood −1, last-trained stat −5 |
| 1 | 1 | Mood −1, Practice Poor, last-trained stat −5 |
| 2 | 0 | Mood −1, last-trained stat −10 |
| 2 | 1 | last-trained stat −10, Practice Poor, Mood −1 |
| 2 | 2 | Practice Perfect ○ |

Confirmed by the user's screenshot: Trainee Event "Get Well Soon!" (the
training-failure event; energy ~15%, mood GOOD, 7 turns before debut). Choice 1
(top) = "Let's call it a day before it gets worse.", choice 2 (bottom) = "Just be
careful from now on." Effects match the community reference exactly, including
branch order. Branch probabilities are NOT in the preview data; umareference.com
(guide/percentages-of-events) gives top 92/8 and bottom 30/55/15 (PP 15%), and
"Don't Overdo It!" 50/50 and 97/3. The engine therefore needs a per-event odds
table (external reference) with a fallback (e.g. uniform) for unknown events.
Event identity is needed for that lookup, so identifying the event is not optional.

**Current event identity (solved).** `Gallop.StoryManager` is a
`Singleton<StoryManager>` (resolve like WorkDataManager: klass+0x58 → +0xB8 →
+0x0). Instance fields: `<StoryId>` (+0x10, plain int), `_storyScene` (+0x18),
`_storySceneController` (+0x20), `_view` (+0x28, StoryView), `_viewController`
(+0x30, StoryViewController), `<LatestLog>`/`<AllLogList>`, `_storyData` (+0x68) →
`SingleModeStoryData` (cached master row: Id, StoryId, CardCharaId, SupportCardId,
SupportCharaId, EventCategory …, plain ints). Observed: StoryId 501009713,
_storyData.Id 1363, CardCharaId 1009, EventCategory 3 → text 181 "Get Well Soon!".
Found by scanning memory for the story id (`scripts/find_int.py`); other hits were
headerless (native/text buffers). Story ids follow `501` + chara (3 digits) +
event (3 digits); 713 = Get Well Soon! for every trainee. Odds-table key should
be the event suffix / title for common events, story id for unique ones.
Open: StoryId probably persists after the event ends; use the view state
(StoryManager._view / SceneManager) to decide whether an event is active.

**Second event, turn 6 (Support Card Event "Embrace Those Emotions!", Light Hello,
story 809008004).** StoryManager switched to it before the user reported it.
`StoryViewController._eventId` (+0x160) = 10002 = `_cachedRewardEventId`, so that
field is the current event's id in a separate id space (not story_data.id).
Preview decoded: top = friendship(9008)+5, param10 +24, param20 +1, param1 +9,
param4 +9, unlock recreation with 9008; bottom = friendship −5, param5 +40,
param30 +40, hint Maverick ○ (200272) +5. The user took top; afterwards memory and
screen showed Speed 170→179, Guts 136→145, energy 19→43, mood 3→4 (NORMAL→GOOD),
SP unchanged, Recreation showing the Light Hello icon. This confirms param 10 =
energy, 20 = mood, 1 = speed, 4 = guts, and display 1 = "+", 11 = unlock
recreation. Get Well Soon! (top) result: mood 4→3, no condition added (92% branch).
Mood enum: 3 = NORMAL, 4 = GOOD (screens at turns 1, 5, 6).

**Event-active indicator (candidate, two samples).** StoryManager.StoryId,
`_storyData`, `_view` and `EventChoiceRewardDict` all keep the last event after it
ends, so they cannot tell "showing" from "finished". StoryViewController flags
differ: during the event `_needsSingleModeSave` (+0x150) and
`_needsUpdateSelectIndex` (+0x151) were true and `_typewriteFrameCount` was 786;
back on the home screen both flags were false and the count was 1. Re-check on
more events (including no-choice events) before relying on them.

`_cachedRewardEventId` = 7014, which resolves to `single_mode_story_data.id`
7014 = Yaeno Muteki "Self-Introduction", not plausible for this career; treat it
as a different id space or stale. No live `SingleModeEventInfo` objects existed
(heap scan: 3 class-metadata false positives only); the response object is not
kept. Event title identification still open; choice scoring does not need it.

## 5. Screen detection

`Gallop.SceneManager` is a `MonoSingleton<SceneManager>`; static `_instance` on the
parent generic class (resolve via SceneManager klass+0x58 → +0xB8 → +0x8).

Fields: `_currentSceneController` (+0x20), `_currentSceneBase` (+0x28),
`_currentViewController` (+0x38), `_currentViewBase` (+0x40), `_prevSceneId`
(+0x48), `_prevViewId` (+0x4C), `_nextViewId` (+0x50), `_isRunChangeView` (+0x60),
`IsRunChangeScene` (+0x70).

Training home observed: scene controller `SingleModeSceneController`, scene
`SingleModeScene`, view controller `SingleModeMainViewController`, view
`SingleModeMainView`, view id 0x44D (1101), prev view 0x44C, scene id 5.
The controller class name is a usable screen identifier; ViewId enum names can be
read from metadata literals later.

Related controllers present in metadata: `SingleModeSkillLearningViewController`,
`SingleModeRaceEntryViewController`.

Dialogs/popups: `Gallop.DialogManager` (`_instance` static at +0x0 of its own
static block in this build) with `_dialogList` (+0x40) and `_dialogBgList`.

## 6. Open

- Live event with choices: holder object, choice previews.
- Skill learning screen: confirm list, costs, hint discounts.
- Race entry screen: confirm optional-race list against master data.
- Command types (confirmed against the turn-6 home screen and
  `single_mode_training` / `single_mode_outing`): 1 = training (101 Spd, 105 Sta,
  102 Pow, 103 Gut, 106 Wit), 3 = recreation (301 normal outing; 390 outing variant,
  probably the friend-card outing, unconfirmed), 4 = races (401, disabled
  pre-debut, screen shows locked), 7 = rest (701), 8 = infirmary (801, disabled
  with no condition). Turn 6 failure rates at 43 energy: 11–14% for trainings that
  cost energy, 0% for Wit.
