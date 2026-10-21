"""Print runtime fields (incl. statics) of classes by qualified name, walking parents. Read-only.

usage: class_fields.py Gallop.SceneManager Gallop.SingleModeEventInfo ...
"""
import sys
sys.path.insert(0, r'C:\Users\blazi\Desktop\AI Projects\BlazeUmamusumePOC')
from umamemory.win32 import Reader
from umamemory.runtime import Runtime
from umamemory.metadata import Metadata

md = Metadata()
with Reader(None) as r:
    rt = Runtime(r)
    def cname(k):
        return f"{r.string(r.ptr(k+0x18))}.{r.string(r.ptr(k+0x10))}".lstrip('.')
    for q in sys.argv[1:]:
        k = rt.cached_class(md.find(q))
        if not k:
            print('==', q, 'class not initialized'); continue
        print('==', q, hex(k))
        while k and cname(k) not in ('System.Object', 'UnityEngine.MonoBehaviour', 'UnityEngine.Object'):
            sb = r.ptr(k + 0xb8)
            print('  [', cname(k), ']')
            for f in rt.fields(k):
                if f['literal']:
                    continue
                extra = ''
                if f['static'] and sb:
                    v = r.ptr(sb + f['offset'])
                    extra = f' = {v:#x}'
                    if v > 0x10000 and f['type'] in (18, 21):
                        try: extra += f' ({cname(r.ptr(v))})'
                        except Exception: pass
                print(f"    {'static ' if f['static'] else ''}{f['offset']:#6x} t{f['type']:<3} {f['name']}{extra}")
            k = r.ptr(k + 0x58)
