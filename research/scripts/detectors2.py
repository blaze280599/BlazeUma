"""Walk each detector's parent chain (generic singleton bases) and print static fields. Read-only."""
import sys
sys.path.insert(0, r'C:\Users\blazi\Desktop\AI Projects\BlazeUmamusumePOC')
from umamemory.win32 import Reader
from umamemory.runtime import Runtime
from umamemory.metadata import Metadata
md = Metadata()
with Reader(None) as r:
    rt = Runtime(r)
    for n in ('InjectionDetector','ObscuredCheatingDetector','SpeedHackDetector','TimeCheatingDetector','WallHackDetector'):
        k = rt.cached_class(md.find('CodeStage.AntiCheat.Detectors.' + n))
        print('==', n)
        while k:
            nm = r.string(r.ptr(k + 0x10))
            if nm in ('MonoBehaviour', 'Object'): break
            sb = r.ptr(k + 0xb8)
            for f in rt.fields(k):
                if f['static'] and not f['literal']:
                    v = r.ptr(sb + f['offset']) if sb else None
                    print(f'   {nm}: static {f["name"]} = {v:#x}' if v is not None else f'   {nm}: static {f["name"]} (no block)')
            k = r.ptr(k + 0x58)
