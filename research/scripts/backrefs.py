"""Read-only reverse pointer search: who points at a given object? Prints owner class names."""
import struct, sys, time
sys.path.insert(0, r'C:\Users\blazi\Desktop\AI Projects\BlazeUmamusumePOC')
from umamemory.win32 import Reader

targets = [int(a, 0) for a in sys.argv[1:]]

def class_name(r, obj):
    """Return 'ns.name' if obj looks like a managed object (klass->klass->... sane), else None."""
    try:
        k = r.ptr(obj)
        if not k or k & 7:
            return None
        name = r.string(r.ptr(k + 0x10))
        ns = r.string(r.ptr(k + 0x18))
        if not name or not name.isprintable() or len(name) > 120:
            return None
        return f'{ns}.{name}' if ns else name
    except Exception:
        return None

with Reader(None) as r:
    needles = {struct.pack('<Q', t): t for t in targets}
    hits = {t: [] for t in targets}
    start = time.monotonic()
    for base, length, kind in r.regions():
        if kind not in (0x20000, 0x1000000):  # private + image
            continue
        for off in range(0, length, 1 << 20):
            amt = min(1 << 20, length - off)
            try:
                data = r.read(base + off, amt)
            except OSError:
                continue
            for needle, t in needles.items():
                pos = data.find(needle)
                while pos >= 0:
                    if pos % 8 == 0:
                        hits[t].append(base + off + pos)
                    pos = data.find(needle, pos + 8)
        if time.monotonic() - start > 60:
            print('TIMEOUT'); break
    for t, addrs in hits.items():
        print(f'== refs to {t:#x}: {len(addrs)}')
        for a in addrs[:40]:
            # find the enclosing object: walk back up to 0x400 bytes for a plausible header
            owner = None
            for back in range(0, 0x400, 8):
                n = class_name(r, a - back)
                if n and r.ptr(a - back + 8) in (0,) :
                    owner = (a - back, back, n); break
            print(f'  {a:#x}', owner and f'owner {owner[0]:#x} +{owner[1]:#x} {owner[2]}')
