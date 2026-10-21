# Career root chain and turn data (2026-10-21)

Read-only research against a live URA Finale career, turn 1, PID 3944,
GameAssembly base 0x7FF96DDC0000. Metadata SHA256 unchanged from the POC
(`27b8f59d…edb5f`); the POC accessor signatures still matched. Addresses below are
from this process only and MUST NOT be reused. Offsets are observed-build values;
code must derive them from runtime FieldInfo by name, as the POC does.

Tools: `research/scripts/` (Python, depends on `..\BlazeUmamusumePOC\umamemory`).
`inspect_obj.py sm|ch|wdm[.field|.[i]|.{i}]...` dumps any object with decoded
Obscured values; `inspect_obj.py turn` prints the turn menu.

## 1. Stable root (replaces heap scan)

Found by reverse pointer search (`backrefs.py`, `staticowner.py`), then confirmed
by resolving from class metadata only (`rootchain.py`):

```
Gallop.WorkDataManager (type-def cache, by name)
  klass+0x58 parent        -> Gallop.Singleton`1  (generic instance Singleton<WorkDataManager>)
  parent+0xB8 static block -> +0x00 static _instance  -> WorkDataManager object
WorkDataManager   +0x58 <SingleMode>k__BackingField -> Gallop.WorkSingleModeData
WorkSingleModeData +0x30 <Character>k__BackingField -> Gallop.WorkSingleModeCharaData
```

The instance resolved this way equalled the object the POC heap scan found
(0x1AF62424000) and the reverse search. No game code is called; the generic
instance class is reached via the concrete class's parent pointer, so no generic
lookup is needed. WorkDataManager also exposes ItemData, SupportCardData,
SupportDeckData, CardData etc. (offsets 0x10–0x1C8) for later use.

Other holders of the character object (not roots): WorkSingleModeRaceData +0x10,
SingleModeMainViewTrainingStatus +0x28 (a view; useful later for screen detection).

## 2. WorkSingleModeData (selected)

| Off | Field | Observed |
|---|---|---|
| 0x18/0x19 | _isExistPlayingData / _isPlaying (bool) | true / true |
| 0x1C | _totalTurnNum (ObscuredInt) | 1 |
| 0x40 | _homeInfo → WorkSingleModeHomeInfo | |
| 0x5C | _state (ObscuredInt) | 0 |
| 0x70 | _playingState (ObscuredInt) | 1 |
| 0x88 | _scenarioIdList (List) | 4 entries |
| 0xA0 | _raceHistoryInfoList | 0 |
| 0xE8 | SelectedTrainingCommandId (ObscuredInt) | 0 |
| 0x168 | EventChoiceRewardDict | 0 |

## 3. Turn menu: WorkSingleModeHomeInfo

`_turnInfoListDic` Dictionary<int commandType, List<TurnInfo>>; entries are
24 bytes (hash, next, int key, pad, ref value) at array+0x20.
`_availableContinueNum`=3 (alarm clocks), `_availableFreeContinueNum`=1,
`_disableCommandIdList`.

TurnInfo: `_commandType`, `_commandId` (ObscuredInt), `IsEnable` (ObscuredBool),
`ParamIncDecInfoDic` Dictionary<int param, ParamsIncDecInfo{Value, BonusValue}>,
`TrainingFailureRate` (plain int), `TrainingHorseList` List<TrainingHorse>.

Observed turn 1 (needs on-screen confirmation of the mapping):

| type | id | gains (param:value) | partners |
|---|---|---|---|
| 1 | 101 | 1:+12 3:+6 30:+4 10:−21 | 0 |
| 1 | 105 | 2:+10 4:+7 30:+4 10:−19 | 1 |
| 1 | 102 | 2:+7 3:+11 30:+5 10:−18 | 3 |
| 1 | 103 | 1:+5 3:+5 4:+10 30:+4 10:−22 | 1 |
| 1 | 106 | 1:+2 5:+10 30:+5 10:+5 | 1 |
| 3 | 301, 390 | — | |
| 4 | 401 (disabled) | — | |
| 7 | 701 | — | |
| 8 | 801 (disabled) | — | |

Inferred, unconfirmed: param 1–5 = Spd/Sta/Pow/Gut/Wit, 30 = skill points,
10 = energy; 101/105/102/103/106 = Speed/Stamina/Power/Guts/Wit training;
type 7 = race, type 8 = infirmary (disabled with no condition). Types 3 and 4
need checking against the screen (rest / outing).

TrainingHorse: PositionId (deck slot), IsTips (hint), IsVersusEvent, IsGuide,
IsSoulExplode, IsSpSoulExplode.

## 4. Character (WorkSingleModeCharaData) additions to the POC

Max stats (1404/1404/1408/1404/1404 here; DefaultMax 1200), aptitudes
(_properDistance*, _properRunningStyle*, _properGround*: observed 1..7, likely
G..A), _runningStyle, _scenarioId=1 (URA), _routeId, _routeRaceIdArray
(ObscuredInt[], 20-byte inline elements; decoded 22..26… = route race ids),
_reservedRaceProgramId, _acquiredSkillList (AcquiredSkill{_masterId,_level}),
_skillTipsList (hints), _trainingLevelDic (10 entries), _evaluationList
(Evaluation{_targetId = deck slot / NPC, _value = bond, _guestCharaId}),
EquipSupportCardArray (EquipSupportCard{Position, SupportCardId, LimitBreakCount,
Exp, RentalType}), WorkScenarioURA (+0x478), Race (+0x468).

## 5. ObscuredBool

Layout from runtime fields; decoded as `(hiddenValue ^ key) != 181` where key is
currentCryptoKey (byte) or the class static fallback. Observed key 191: false
fields hold 10, true fields hold 106. Consistent across all flags seen.

## 6. Anti-cheat detectors

All CodeStage ACTk detector classes are initialized (type loaded) but
`ACTkDetectorBase.detectorsContainer` = null and
`ObscuredCheatingDetector.<Instance>` and `instancesInScene` = null. ACTk creates
the container when StartDetection runs, so no detector appears to have been
started. Not proof for detectors attached to other GameObjects; confirm via IDA
xrefs to InjectionDetector/SpeedHackDetector start methods before any in-process
work.

## 7. Validation across one training action

User's screenshot of the Speed confirm screen (turn 1, before confirming) showed
Speed +12, Power +6, Skill Pts +4, failure 0%, stats 119/82/89/130/95, caps
1404/1404/1408/1404/1404, SP 120, NORMAL mood. Matches the turn-1 read exactly,
confirming command 101 = Speed training and params 1 = Speed, 3 = Power,
30 = skill points.

After the user did Speed training, re-resolving via the static root (same
PID 3944, same object addresses, no rescan):

| Value | Turn 1 | Turn 2 |
|---|---:|---:|
| _totalTurnNum | 1 | 2 |
| Speed | 119 | 131 (+12) |
| Power | 89 | 95 (+6) |
| Skill points | 120 | 124 (+4) |
| Energy (_hp) | 100 | 79 (−21, param 10) |
| SelectedTrainingCommandId | 0 | 101 |

Training menu re-rolled for turn 2 (partners moved: Power 1, Guts 3, Wit 2;
gains changed accordingly). A full heap rescan at turn 1 found only this
character object; the 3 rejected header matches failed wrapper-flag checks
(class metadata, not stale copies).

## 8. Open

- Confirm command types 3/4 (rest / outing) on screen.
- Map route race ids and program ids to races (master data).
- Skill shop list and costs; event choice data; screen/scene identification.
