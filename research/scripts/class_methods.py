"""Read-only: list runtime methods (name, parameter count, RVA) of classes by name or name pattern.

usage: class_methods.py [--grep REGEX] Gallop.SomeClass [...]
       class_methods.py --types REGEX        # list matching type names from metadata only
Classes must be initialized in the running game (class cache entry non-null).
"""
import re, sys
sys.path.insert(0, r'C:\Users\blazi\Desktop\AI Projects\BlazeUmamusumePOC')
from umamemory.win32 import Reader
from umamemory.runtime import Runtime
from umamemory.metadata import Metadata

md = Metadata()
args = sys.argv[1:]
grep = None
if args[:1] == ['--types']:
    pat = re.compile(args[1])
    for i in range(md.type_size // 88):
        o = md.types + i * 88
        n, ns = md.values(o, '<II')
        full = '.'.join(filter(None, [md.string(ns), md.string(n)]))
        if pat.search(full):
            print(i, full)
    sys.exit()
if args[:1] == ['--grep']:
    grep = re.compile(args[1], re.I)
    args = args[2:]

with Reader(None) as r:
    rt = Runtime(r)
    for q in args:
        k = rt.cached_class(md.find(q))
        if not k:
            print('==', q, '(class not initialized)')
            continue
        methods = r.ptr(k + 0x98)
        count = r.unpack(k + 0x120, '<H')
        print(f'== {q} ({count} methods)')
        for i in range(count):
            m = r.ptr(methods + i * 8)
            name = r.string(r.ptr(m + 0x18))
            if grep and not grep.search(name):
                continue
            fn = r.ptr(m)
            print(f'   {name:<55} rva={fn - r.base:#x}' if fn else f'   {name:<55} (no code)')
