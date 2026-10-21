"""Read-only: list the skill learning screen entries (only while that screen is open).

SceneManager._currentViewController = SingleModeSkillLearningViewController
  _skillInfoList: List<SkillInfo>; SkillInfo._skillList: List<Info>
"""
import sys, io, contextlib, sqlite3
sys.argv = [sys.argv[0]]
with contextlib.redirect_stdout(io.StringIO()):
    import inspect_obj as I
r = I.r
MASTER = r'C:\Users\blazi\AppData\Local\Temp\claude\C--Users-blazi--claude-spares-spare-2L4CvK\34df2e32-e8a7-4d16-9e4c-80d9e044c23b\scratchpad\master.mdb'
db = sqlite3.connect(MASTER)
name = lambda s: (db.execute('select text from text_data where category=47 and "index"=?', (s,)).fetchone() or ['?'])[0]

k = I.rt.cached_class(I.md.find('Gallop.SceneManager'))
scene = r.ptr(r.ptr(r.ptr(k + 0x58) + 0xb8) + 8)
vc = r.ptr(scene + 0x38)
if I.cname(r.ptr(vc)) != 'Gallop.SingleModeSkillLearningViewController':
    sys.exit(f'skill screen not open (current: {I.cname(r.ptr(vc))})')
print('remaining SP', r.unpack(I.field_addr(vc, 'RemainingPoint'), '<i'))
B = lambda o, f: bool(r.read(I.field_addr(o, f), 1)[0])
N = lambda o, f: r.unpack(I.field_addr(o, f), '<i')
for g, info in enumerate(I.list_items(r.ptr(I.field_addr(vc, '_skillInfoList')))):
    for e in I.list_items(r.ptr(I.field_addr(info, '_skillList'))):
        sid = N(e, 'SkillId')
        print(f"group {g}: {sid} {name(sid):<28} need={N(e,'NeedPoint'):>4} origin={N(e,'OriginNeedPoint'):>4} "
              f"discount={N(e,'DiscountRateForDisplay')}% hintLv={N(e,'HintLv')} lvl={N(e,'Level')} "
              f"acquired={B(e,'IsAcquired')} lock={B(e,'IsLock')} get={B(e,'IsGet')} selected={B(e,'IsSelected')}")
