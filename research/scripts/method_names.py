"""Read-only: dump RVA -> "Namespace.Class$$Method" for initialized classes whose name matches a regex.

usage: method_names.py OUT.json [REGEX]
Writes {"rva_hex": "Class$$Method", ...}. Feed it to IDA to name functions (see apply in the docs).
"""
import json, re, sys, time
sys.path.insert(0, r'C:\Users\blazi\Desktop\AI Projects\BlazeUmamusumePOC')
from umamemory.win32 import Reader
from umamemory.runtime import Runtime
from umamemory.metadata import Metadata

out = sys.argv[1]
pattern = re.compile(sys.argv[2] if len(sys.argv) > 2 else
                     r'^Gallop\.(SingleMode|Story|Dialog|Scene|Button|Training|Outing|Race|ViewController|ViewBase|PartsSingleMode)')
md = Metadata()

def cstr(r, addr):
    """Chunked native string read (faster than byte-wise); falls back near page ends."""
    try:
        data = r.read(addr, 128)
    except Exception:
        return r.string(addr)
    end = data.find(b'\0')
    return data[:end].decode('utf-8', 'replace') if end >= 0 else r.string(addr)

names, start, classes = {}, time.monotonic(), 0
with Reader(None) as r:
    rt = Runtime(r)
    cache = None
    for idx in range(md.type_size // 88):
        o = md.types + idx * 88
        n, ns = md.values(o, '<II')
        full = '.'.join(filter(None, [md.string(ns), md.string(n)]))
        if not pattern.search(full):
            continue
        try:
            k = rt.cached_class({'index': idx, 'name': md.string(n), 'namespace': md.string(ns)})
        except Exception:
            continue
        if not k:
            continue
        methods, count = r.ptr(k + 0x98), r.unpack(k + 0x120, '<H')
        if not methods or not 0 < count < 2048:
            continue
        classes += 1
        for i in range(count):
            try:
                m = r.ptr(methods + i * 8)
                fn = r.ptr(m)
                if fn:
                    names.setdefault(hex(fn - r.base), f'{full}$${cstr(r, r.ptr(m + 0x18))}')
            except Exception:
                pass
json.dump(names, open(out, 'w'), indent=0)
print(f'{len(names)} methods from {classes} classes in {time.monotonic() - start:.0f}s -> {out}')
