"""Resolve WorkDataManager -> parent Singleton<T> static Instance -> SingleMode -> Character, read-only."""
import json, sys
sys.path.insert(0, r'C:\Users\blazi\Desktop\AI Projects\BlazeUmamusumePOC')
from umamemory.win32 import Reader
from umamemory.runtime import Runtime
from umamemory.metadata import Metadata

TYPE_NAMES = {2:'bool', 8:'int32', 17:'valuetype', 18:'class', 21:'generic', 29:'szarray', 12:'single', 10:'int64', 14:'string', 6:'int16', 4:'byte'}

md = Metadata()
with Reader(None) as r:
    rt = Runtime(r)
    def cname(k):
        return f"{r.string(r.ptr(k+0x18))}.{r.string(r.ptr(k+0x10))}"
    def dump(klass, title):
        print(f'--- {title} {cname(klass)} size={r.unpack(klass+0xf8,"<I")}')
        for f in rt.fields(klass):
            if not f['static']:
                print(f"  {f['offset']:#6x} {TYPE_NAMES.get(f['type'], f['type']):>9}  {f['name']}")
    wdm = rt.cached_class(md.find('Gallop.WorkDataManager'))
    parent = r.ptr(wdm + 0x58)
    print('WorkDataManager klass', hex(wdm), 'parent', hex(parent), cname(parent))
    static_block = r.ptr(parent + 0xb8)
    inst = r.ptr(static_block)
    print('static block', hex(static_block), 'instance', hex(inst), 'instance class', cname(r.ptr(inst)))
    # static fields of the singleton parent (inherited fields table)
    for f in rt.fields(parent):
        print('  singleton field', f)
    dump(wdm, 'WorkDataManager')
    sm = r.ptr(inst + 0x58)
    print('SingleMode', hex(sm), cname(r.ptr(sm)))
    dump(r.ptr(sm), 'WorkSingleModeData')
    ch = r.ptr(sm + 0x30)
    print('Character', hex(ch), cname(r.ptr(ch)))
