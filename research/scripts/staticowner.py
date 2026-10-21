"""Find Il2CppClass whose static-fields block (klass+0xB8) contains a given address."""
import struct, sys
sys.path.insert(0, r'C:\Users\blazi\Desktop\AI Projects\BlazeUmamusumePOC')
from umamemory.win32 import Reader

hits = [int(a, 0) for a in sys.argv[1:]]
WINDOW = 0x400

with Reader(None) as r:
    found = []
    for base, length, kind in r.regions():
        if kind != 0x20000:
            continue
        for off in range(0, length, 1 << 20):
            amt = min(1 << 20, length - off)
            try:
                data = r.read(base + off, amt)
            except OSError:
                continue
            for i in range(0, amt - 7, 8):
                v = struct.unpack_from('<Q', data, i)[0]
                for h in hits:
                    if h - WINDOW <= v <= h:
                        found.append((base + off + i, v, h))
    for addr, block, h in found:
        klass = addr - 0xB8
        try:
            name = r.string(r.ptr(klass + 0x10)); ns = r.string(r.ptr(klass + 0x18))
        except Exception:
            continue
        if name and name.isprintable():
            print(f'static hit {h:#x}: class {klass:#x} {ns}.{name} block {block:#x} field_off {h - block:#x}')
