"""Read-only: list the race entry screen's races (only while that screen is open).

SceneManager._currentViewController = SingleModeRaceEntryViewController
  _itemList: List<DialogSingleModeRaceListItem>; item.<Program> = SingleModeProgram (master row)
"""
import sys, io, contextlib, sqlite3
sys.argv = [sys.argv[0]]
with contextlib.redirect_stdout(io.StringIO()):
    import inspect_obj as I
r = I.r
MASTER = r'C:\Users\blazi\AppData\Local\Temp\claude\C--Users-blazi--claude-spares-spare-2L4CvK\34df2e32-e8a7-4d16-9e4c-80d9e044c23b\scratchpad\master.mdb'
db = sqlite3.connect(MASTER)

k = I.rt.cached_class(I.md.find('Gallop.SceneManager'))
scene = r.ptr(r.ptr(r.ptr(k + 0x58) + 0xb8) + 8)
vc = r.ptr(scene + 0x38)
if I.cname(r.ptr(vc)) != 'Gallop.SingleModeRaceEntryViewController':
    sys.exit(f'race entry screen not open (current: {I.cname(r.ptr(vc))})')
N = lambda o, f: r.unpack(I.field_addr(o, f), '<i')
B = lambda o, f: bool(r.read(I.field_addr(o, f), 1)[0])
print('display turn', N(vc, '_displayTurn'), 'selected program', N(r.ptr(I.field_addr(vc, '_currentSelectedProgram')), 'Id'))
for item in I.list_items(r.ptr(I.field_addr(vc, '_itemList'))):
    p = r.ptr(I.field_addr(item, 'Program'))
    pid, ri = N(p, 'Id'), N(p, 'RaceInstanceId')
    row = db.execute('''select r.grade, cs.distance, cs.ground, t.text from race_instance i join race r on r.id = i.race_id
        join race_course_set cs on cs.id = r.course_set left join text_data t on t.category = 28 and t."index" = i.id
        where i.id = ?''', (ri,)).fetchone()
    print(f"program {pid} group {N(p,'ProgramGroup')} perm {N(p,'RacePermission')} fans>={N(p,'NeedFanCount')} "
          f"entryState {N(item,'_programEntryState')} route {B(item,'IsRouteRace')} reserved {B(item,'IsReservedProgram')} | {row}")
