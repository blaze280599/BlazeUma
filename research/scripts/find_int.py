"""Read-only: find aligned int32 values in private memory and name the enclosing managed object.

usage: find_int.py 501009713 [more ints]
"""
import struct, sys
sys.path.insert(0, r'C:\Users\blazi\Desktop\AI Projects\BlazeUmamusumePOC')
from umamemory.win32 import Reader

def cname(r, k):
    try:
        n = r.string(r.ptr(k + 0x10), 120); ns = r.string(r.ptr(k + 0x18), 120)
        if n and n.isprintable():
            return f'{ns}.{n}'.lstrip('.')
    except Exception:
        pass
    return None

def owner(r, a):
    for back in range(0, 0x200, 8):
        h = (a & ~7) - back
        try:
            k = r.ptr(h)
            if k and not k & 7 and r.ptr(k) and r.ptr(h + 8) == 0:
                n = cname(r, k)
                if n and r.unpack(k + 0xf8, '<I') > back:  # offset within instance size
                    return h, a - h, n
        except Exception:
            continue
    return None

values = [int(v, 0) for v in sys.argv[1:]]
with Reader(None) as r:
    needles = {struct.pack('<i', v): v for v in values}
    for base, length, kind in r.regions():
        if kind != 0x20000:
            continue
        for off in range(0, length, 1 << 20):
            amt = min(1 << 20, length - off)
            try:
                data = r.read(base + off, amt)
            except OSError:
                continue
            for needle, v in needles.items():
                pos = data.find(needle)
                while pos >= 0:
                    if pos % 4 == 0:
                        a = base + off + pos
                        o = owner(r, a)
                        print(f'{v} @ {a:#x}', f'in {o[2]} {o[0]:#x} +{o[1]:#x}' if o else '(no object header)')
                    pos = data.find(needle, pos + 4)
