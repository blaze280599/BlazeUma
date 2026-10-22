"""Measure advisor constants from career logs (JSONL LogRecords written by TurnRecorder). No game access.

usage: measure_logs.py [PATH ...]   files or folders of career-*.jsonl; default the shared log folder
(BLAZEUMA_LOGS, else ~/BlazeUma/logs) that the bot and Advisor mode write to
Race programs are looked up in the master.mdb cache MasterDatabase keeps (%LOCALAPPDATA%/BlazeUma/master.mdb);
without it races are grouped by turn only. Each estimate prints the C# constant it should replace.
"""
import glob, json, os, re, sqlite3, sys
from collections import Counter, defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
LOGS = os.environ.get('BLAZEUMA_LOGS') or os.path.join(os.path.expanduser('~'), 'BlazeUma', 'logs')
MASTER = os.path.join(os.environ.get('LOCALAPPDATA', ''), 'BlazeUma', 'master.mdb')
MOOD = {'Awful': 1, 'Bad': 2, 'Normal': 3, 'Good': 4, 'Great': 5}
GRADE = {100: 'G1', 200: 'G2', 300: 'G3', 400: 'OP', 700: 'Pre-OP', 800: 'Maiden', 900: 'Debut'}
STATS = ['speed', 'stamina', 'power', 'guts', 'wit']
HAPPY_MEEK = 2001
MIN_SAMPLES = 3

def records(args):
    paths = args or [LOGS]
    files = []
    for p in paths:
        files += sorted(glob.glob(os.path.join(p, 'career-*.jsonl'))) if os.path.isdir(p) else [p]
    out = []
    for f in files:
        with open(f, encoding='utf-8') as fh:
            out += [json.loads(line) for line in fh if line.strip()]
    return files, out

def summary(xs):
    """'n=…, mean …, range …' plus the small-sample warning."""
    if not xs: return 'n=0'
    s = f'n={len(xs)}, mean {sum(xs) / len(xs):+.1f}, range {min(xs):+d}..{max(xs):+d}'
    return s + ('  (too few samples)' if len(xs) < MIN_SAMPLES else '')

def replaces(*names):
    for n in names: print(f'    replaces {n}')

def energy(o): return o['EnergyAfter'] - o['EnergyBefore']
def mood(o): return MOOD[o['MoodAfter']] - MOOD[o['MoodBefore']]
def stat_total(o): return sum(o['StatChanges'].get(s, 0) for s in STATS)

def shown_gain(cmd, key):
    g = cmd['Gains'].get(key)
    return g['Value'] + g['Bonus'] if g else 0

def chosen_command(rec):
    """The command the outcome says was taken (training:ID), from the start-of-turn snapshot."""
    m = re.fullmatch(r'training:(\d+)', rec['Outcome']['InferredAction'])
    if not m: return None
    return next((c for c in rec['Snapshot']['Commands'] if c['Type'] == 'Training' and c['Id'] == int(m[1])), None)

class Master:
    def __init__(self, path):
        self.db = sqlite3.connect(f'file:{path}?mode=ro', uri=True) if os.path.exists(path) else None

    def race(self, rec):
        """(program id, grade, name) of the race run on this turn, or None."""
        if not self.db: return None
        snap, turn = rec['Snapshot'], rec['Turn']
        ids = snap['Character']['RouteRaceIds']
        if ids:
            row = self.db.execute(f'select condition_id from single_mode_route_race where turn = ? and id in ({",".join("?" * len(ids))})',
                                  (turn, *ids)).fetchone()
            if row: return self.program(row[0])
        name = rec['Summary'].removeprefix('Race: ')
        year, month, half = self.db.execute('select year, month, half from single_mode_turn where turn_set_id = 1 and turn = ?', (turn,)).fetchone()
        for pid, perm, rname in self.db.execute('''
                select p.id, p.race_permission, t.text from single_mode_program p
                left join text_data t on t.category = 28 and t."index" = p.race_instance_id
                where p.month = ? and p.half = ? and p.program_group = 0''', (month, half)):
            if year in {1: {1}, 2: {2}, 3: {2, 3}, 4: {3}}.get(perm, {1, 2, 3}) and (rname or '').replace('\\n', ' ') == name:
                return self.program(pid)
        return None

    def program(self, pid):
        row = self.db.execute('''select p.id, r.grade, coalesce(t.text, '') from single_mode_program p
            join race_instance ri on ri.id = p.race_instance_id join race r on r.id = ri.race_id
            left join text_data t on t.category = 28 and t."index" = p.race_instance_id where p.id = ?''', (pid,)).fetchone()
        return (row[0], row[1], row[2].replace('\\n', ' ')) if row else (pid, None, f'program {pid} (URA finale?)')

files, recs = records(sys.argv[1:])
turns = [r for r in recs if r['Kind'] == 'turn' and r.get('Outcome')]
print(f'{len(files)} log file(s), {len(recs)} records, {len(turns)} measured turns')
print(f'  kinds {dict(Counter(r["Kind"] for r in recs))}, actions {dict(Counter(r["Outcome"]["InferredAction"].split(":")[0] for r in turns))}')

print('\n== Rest')
rest_turns = [r for r in turns if r['Outcome']['InferredAction'] == 'rest']
rest = [r['Outcome'] for r in rest_turns]
print(f'  energy {summary([energy(o) for o in rest])}')
print(f'  capped at max energy (gain understated): {sum(r["Outcome"]["EnergyAfter"] >= r["Snapshot"]["Character"]["MaxEnergy"] for r in rest_turns)}')
print(f'  mood {summary([mood(o) for o in rest])}')
print(f'  conditions added {Counter(c for o in rest for c in o["ConditionsAdded"])} (Night Owl)')
replaces('TurnAdvisor.RestOutcomes (src/BlazeUma.Advisor/TurnAdvisor.cs)')

print('\n== Recreation')
solo = [r['Outcome'] for r in turns if r['Outcome']['InferredAction'] == 'recreation']
print(f'  trainee outing: energy {summary([energy(o) for o in solo])}; mood {summary([mood(o) for o in solo])}')
replaces('TurnAdvisor.SoloOutings (src/BlazeUma.Advisor/TurnAdvisor.cs)')
friend = [r for r in turns if r['Outcome']['InferredAction'].startswith('outing:')]
print(f'  friend outing: energy {summary([energy(r["Outcome"]) for r in friend])}; mood {summary([mood(r["Outcome"]) for r in friend])}')
print(f'    stats {summary([stat_total(r["Outcome"]) for r in friend])}; SP {summary([r["Outcome"]["SkillPointChange"] for r in friend])}')
by_step = defaultdict(list)
for r in friend:
    chara = r['Outcome']['InferredAction'].split(':')[1]
    bond = [int(b.split(':')[1]) for b in r['Outcome']['BondChanges'] if b.startswith(chara + ':') and 'outing' not in b]
    step = next((b.split('outing ')[1] for b in r['Outcome']['BondChanges'] if b.startswith(chara + ':outing')), '?')
    by_step[step].append(r['Outcome'])
    print(f'    turn {r["Turn"]:>2} chara {chara} step {step}: energy {energy(r["Outcome"]):+d}, mood {mood(r["Outcome"]):+d}, '
          f'stats {r["Outcome"]["StatChanges"]}, SP {r["Outcome"]["SkillPointChange"]:+d}, bond {bond}, skills {r["Outcome"]["SkillsLearned"]}')
for step, os_ in sorted(by_step.items()):
    print(f'    chain step {step}: stats {summary([stat_total(o) for o in os_])}')
replaces('TurnAdvisor.OutingEnergy, TurnAdvisor.OutingBond, TurnAdvisor.OutingChainStepValue (src/BlazeUma.Advisor/TurnAdvisor.cs)')

print('\n== Races')
master = Master(MASTER)
if not master.db: print(f'  {MASTER} not found; grouping by turn')
by_grade = defaultdict(list)
for r in (r for r in turns if r['Outcome']['InferredAction'] == 'race'):
    o, race = r['Outcome'], master.race(r)
    grade = GRADE.get(race[1], race[1]) if race else f'turn {r["Turn"]}'
    by_grade[grade].append(o)
    print(f'  turn {r["Turn"]:>2} {race[2] if race else r["Summary"]} (program {race[0] if race else "?"}, {grade}): '
          f'fans {o["FanChange"]:+d}, SP {o["SkillPointChange"]:+d}, stats {o["StatChanges"]}, energy {energy(o):+d}')
for grade, os_ in sorted(by_grade.items(), key=lambda kv: str(kv[0])):
    print(f'  {grade}: SP {summary([o["SkillPointChange"] for o in os_])}')
    print(f'      stats {summary([stat_total(o) for o in os_])}; fans {summary([o["FanChange"] for o in os_])}; energy {summary([energy(o) for o in os_])}')
replaces('RaceEvaluator grade table (sp, stats) in Evaluate, RaceEvaluator.RaceEnergyCost (src/BlazeUma.Advisor/RaceEvaluator.cs)')

print('\n== Happy Meek duels (training with partner 2001: extra over the shown gain)')
meek, other = [], []
for r in turns:
    cmd = chosen_command(r)
    if not cmd: continue
    o = r['Outcome']
    extra_stat = stat_total(o) - sum(shown_gain(cmd, s) for s in STATS)
    extra_sp = o['SkillPointChange'] - shown_gain(cmd, 'skillPoints')
    with_meek = any(p['Position'] == HAPPY_MEEK for p in cmd['Partners'])
    (meek if with_meek else other).append((extra_stat, extra_sp))
    if with_meek: print(f'  turn {r["Turn"]:>2} training {cmd["Id"]}: extra stats {extra_stat:+d}, extra SP {extra_sp:+d}')
duels = [x for x in meek if x[1] >= 15]
print(f'  with Meek: extra stats {summary([x[0] for x in meek])}; extra SP {summary([x[1] for x in meek])}')
print(f'  without Meek (baseline): extra stats {summary([x[0] for x in other])}; extra SP {summary([x[1] for x in other])}')
print(f'  duel seen (extra SP >= 15): {len(duels)}/{len(meek)}' + ('  (too few samples)' if len(meek) < MIN_SAMPLES else ''))
print(f'  duel won (extra SP >= 30): {sum(x[1] >= 30 for x in duels)}/{len(duels)}' + ('  (too few samples)' if len(duels) < MIN_SAMPLES else ''))
replaces('TurnAdvisor.HappyMeek DuelChance, WinChance (src/BlazeUma.Advisor/TurnAdvisor.cs)')

print('\n== Training failure (shown failure rate vs failures seen)')
buckets = defaultdict(lambda: [0, 0])
for r in turns:
    cmd = chosen_command(r)
    if not cmd: continue
    shown = sum(shown_gain(cmd, s) for s in STATS)
    failed = stat_total(r['Outcome']) < shown / 2 or any(v < 0 for v in r['Outcome']['StatChanges'].values())
    b = buckets[cmd['FailureRate'] // 10 * 10]
    b[0] += 1; b[1] += failed
for lo, (n, failed) in sorted(buckets.items()):
    print(f'  shown {lo:>2}-{lo + 9}%: {failed}/{n} failed' + ('  (too few samples)' if n < MIN_SAMPLES else ''))
replaces('TurnAdvisor.Training failure chance p = FailureRate / 100, ValueModel.FailureLoss (src/BlazeUma.Advisor/TurnAdvisor.cs, ValueModel.cs)')

print('\n== Event outcomes (event-result: story, matched choice/outcome)')
shown_events = Counter(r['StoryId'] for r in recs if r['Kind'] == 'event')
results = Counter()
for r in (r for r in recs if r['Kind'] == 'event-result'):
    res = r['Result']
    results[(res['StoryId'], res['Choice'], res['Outcome'])] += 1
    print(f'  turn {r["Turn"]:>2} story {res["StoryId"]}: choice {res["Choice"]}, outcome {res["Outcome"]}, error {res["MatchError"]}, '
          f'stats {res["Change"]["StatChanges"]}, energy {energy(res["Change"]):+d}, SP {res["Change"]["SkillPointChange"]:+d}')
for (story, choice, outcome), n in sorted(results.items(), key=lambda kv: (kv[0][0], str(kv[0][1:]))):
    total = sum(v for k, v in results.items() if k[0] == story and k[1] == choice)
    print(f'  story {story} choice {choice} -> outcome {outcome}: {n}/{total}' + ('  (too few samples)' if total < MIN_SAMPLES else ''))
print(f'  events shown (any): {dict(shown_events)}')
replaces('EventAdvisor.TraineeEventOdds (src/BlazeUma.Advisor/EventAdvisor.cs)')
