"""Predict the race entry list for a career turn from master.mdb (no game access).

usage: predict_races.py TURN [--all-permissions]
race_permission: 1 junior, 2 classic, 3 classic+senior, 4 senior (5 unverified, treated as any year).
"""
import sqlite3, sys

MASTER = r'C:\Users\blazi\AppData\Local\Temp\claude\C--Users-blazi--claude-spares-spare-2L4CvK\34df2e32-e8a7-4d16-9e4c-80d9e044c23b\scratchpad\master.mdb'
GRADE = {100: 'G1', 200: 'G2', 300: 'G3', 400: 'OP', 700: 'Pre-OP', 800: 'Maiden', 900: 'Debut'}
GROUND = {1: 'Turf', 2: 'Dirt'}

turn = int(sys.argv[1])
db = sqlite3.connect(MASTER)
year, month, half = db.execute(
    'select year, month, half from single_mode_turn where turn_set_id = 1 and turn = ?', (turn,)).fetchone()
print(f'turn {turn}: year {year}, month {month}, half {half}')
rows = db.execute('''
    select p.id, p.race_permission, p.need_fan_count, r.grade, cs.distance, cs.ground, t.text
    from single_mode_program p
    join race_instance ri on ri.id = p.race_instance_id
    join race r on r.id = ri.race_id
    join race_course_set cs on cs.id = r.course_set
    left join text_data t on t.category = 28 and t."index" = p.race_instance_id
    where p.month = ? and p.half = ? and p.program_group = 0
    order by r.grade, cs.distance''', (month, half)).fetchall()
for pid, perm, fans, grade, dist, ground, name in rows:
    allowed = {1: {1}, 2: {2}, 3: {2, 3}, 4: {3}}.get(perm, {1, 2, 3})
    if year not in allowed and '--all-permissions' not in sys.argv:
        continue
    print(f'  program {pid:>5} perm {perm} {GRADE.get(grade, grade):>6} {dist:>4}m {GROUND.get(ground, ground):<4} fans>={fans:<6} {name}')
