"""Which CodeStage ACTk detector classes are initialized, and do they have a live Instance? Read-only."""
import sys
sys.path.insert(0, r'C:\Users\blazi\Desktop\AI Projects\BlazeUmamusumePOC')
from umamemory.win32 import Reader
from umamemory.runtime import Runtime
from umamemory.metadata import Metadata

md = Metadata()
with Reader(None) as r:
    rt = Runtime(r)
    for idx in range(md.type_size // 88):
        off = md.types + idx * 88
        ns = md.string(md.values(off + 4, '<I')[0])
        if not ns.startswith('CodeStage.AntiCheat'):
            continue
        t = md.type(idx)
        name = f"{ns}.{t['name']}"
        if 'Detector' not in name and 'Obscured' not in t['name'] and 'ACTk' not in name:
            continue
        if 'Obscured' in t['name'] and 'Detector' not in t['name']:
            continue
        k = rt.cached_class(t)
        if not k:
            print(f'{name:<75} class not initialized'); continue
        sb = r.ptr(k + 0xb8)
        statics = [f for f in rt.fields(k) if f['static'] and not f['literal']]
        vals = []
        for f in statics:
            try:
                p = r.ptr(sb + f['offset']) if sb else 0
                desc = f"{f['name']}={p:#x}"
                if p and f['type'] in (18, 21) and p > 0x10000:
                    kk = r.ptr(p)
                    desc += f"({r.string(r.ptr(kk+0x10))})"
                vals.append(desc)
            except Exception as e:
                vals.append(f"{f['name']}=<{e}>")
        print(f'{name:<75} initialized; statics: {", ".join(vals)}')
        # instance fields of a live Instance (e.g. IsRunning / enabled flags)
        for f in statics:
            if f['name'] in ('<Instance>k__BackingField', 'instance', 'Instance') and sb:
                inst = r.ptr(sb + f['offset'])
                if inst:
                    for g in rt.fields(r.ptr(inst)):
                        if not g['static'] and g['type'] == 2:
                            print(f"    instance bool {g['name']} = {r.read(inst + g['offset'], 1)[0]}")
