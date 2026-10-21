"""Generic read-only object inspector: names field types via metadata handles, decodes Obscured* locally.

usage: inspect_obj.py PATH   where PATH is like  sm  |  sm._homeInfo  |  ch  |  0xADDR
"""
import struct, sys
sys.path.insert(0, r'C:\Users\blazi\Desktop\AI Projects\BlazeUmamusumePOC')
from umamemory.win32 import Reader
from umamemory.runtime import Runtime
from umamemory.metadata import Metadata

md = Metadata()
r = Reader(None).__enter__()
rt = Runtime(r)
TD = 88

def cname(k):
    try:
        return f"{r.string(r.ptr(k+0x18))}.{r.string(r.ptr(k+0x10))}".lstrip('.')
    except Exception:
        return '?'

# Calibrate the in-memory metadata type-definition base from a known field (_speed -> ObscuredInt).
chara_k = rt.cached_class(md.find('Gallop.WorkSingleModeCharaData'))
obsint_def = md.find('CodeStage.AntiCheat.ObscuredTypes.ObscuredInt')
tbl = r.ptr(chara_k + 0x80)
for i in range(r.unpack(chara_k + 0x124, '<H')):
    f = tbl + i * 32
    if r.string(r.ptr(f)) == '_speed':
        handle = r.ptr(r.ptr(f + 8))
        TD_BASE = handle - obsint_def['index'] * TD
        break

def type_name(il2cpptype):
    bits = r.unpack(il2cpptype + 8, '<I'); kind = (bits >> 16) & 255
    data = r.ptr(il2cpptype)
    if kind in (17, 18):
        idx = (data - TD_BASE) // TD
        if (data - TD_BASE) % TD == 0 and 0 <= idx < md.type_size // TD:
            t = md.type(idx); return kind, f"{t['namespace']}.{t['name']}".lstrip('.')
    if kind == 21:  # Il2CppGenericClass*: +0 type handle/Il2CppType* of definition
        try:
            gt = r.ptr(data)
            d2 = r.ptr(gt)
            idx = (d2 - TD_BASE) // TD
            if (d2 - TD_BASE) % TD == 0: return kind, md.type(idx)['name'] + '<..>'
        except Exception: pass
        return kind, 'generic'
    return kind, {1:'void',2:'bool',3:'char',4:'sbyte',5:'byte',6:'short',7:'ushort',8:'int',9:'uint',10:'long',11:'ulong',12:'float',13:'double',14:'string',29:'array[]',28:'object'}.get(kind, f'kind{kind}')

static_keys = {}
def obscured_layout(cls_name):
    k = rt.cached_class(md.find(cls_name))
    lay = {f['name']: f['offset'] - 16 for f in rt.fields(k) if not f['static']}
    sfs = [f for f in rt.fields(k) if f['static'] and not f['literal']]
    sb = r.ptr(k + 0xb8)
    return lay, sb, sfs, k

layouts = {}
def decode_obscured(tname, addr):
    if tname not in layouts:
        try: layouts[tname] = obscured_layout(tname)
        except Exception as e: layouts[tname] = None
    L = layouts[tname]
    if not L: return '?'
    lay, sb, sfs, _ = L
    short = tname.rsplit('.', 1)[1]
    if short in ('ObscuredInt', 'ObscuredUInt'):
        key, hidden = r.unpack(addr + lay['currentCryptoKey'], '<I'), r.unpack(addr + lay['hiddenValue'], '<I')
        inited = r.read(addr + lay['inited'], 1)[0]
        if not inited: return 0
        k = key or r.unpack(sb, '<I')
        v = hidden ^ k
        return v - (1 << 32) if short == 'ObscuredInt' and v >= 1 << 31 else v
    if short == 'ObscuredBool':
        key = r.read(addr + lay['currentCryptoKey'], 1)[0]
        hidden = r.unpack(addr + lay['hiddenValue'], '<i')
        inited = r.read(addr + lay['inited'], 1)[0]
        if not inited: return False
        k = key or r.read(sb, 1)[0]
        return (hidden ^ k) != 181  # ACTk ObscuredBool: encrypted false == 181
    if short in ('ObscuredLong',):
        key, hidden = r.unpack(addr + lay['currentCryptoKey'], '<q'), r.unpack(addr + lay['hiddenValue'], '<q')
        return hidden ^ (key or r.unpack(sb, '<q'))
    return f'{short}:{r.read(addr, 24).hex()}'

def fields_all(klass):
    out = []
    while klass:
        out = rt.fields(klass) + out if True else out
        klass = r.ptr(klass + 0x58)
        if cname(klass) in ('System.Object', '?'): break
    return out

def elems(obj):
    """Print List/array/Dictionary elements (int or ref keys/values, best effort)."""
    n = cname(r.ptr(obj))
    if n.endswith('List`1'):
        arr, cnt = r.ptr(obj + 0x10), r.unpack(obj + 0x18, '<i')
        for i in range(cnt):
            v = r.ptr(arr + 0x20 + 8 * i)
            print(f'  [{i}] {v:#x} ({cname(r.ptr(v)) if v > 0x10000 else "int-ish"})')
        return True
    if 'Dictionary' in n:
        ent, cnt = r.ptr(obj + 0x18), r.unpack(obj + 0x20, '<i')
        stride = None
        for s in (24, 16, 32, 40):
            stride = s; break
        alen = r.unpack(ent + 0x18, '<Q')
        # infer stride from element class size (klass+0xf8 of array element class is not trivial); use 24 for int->ref
        for i in range(cnt):
            e = ent + 0x20 + 24 * i
            h, nx, key = struct.unpack('<iii', r.read(e, 12)); val = r.ptr(e + 16)
            print(f'  {{{i}}} hash={h} key={key} val={val:#x} ({cname(r.ptr(val)) if val > 0x10000 else val})')
        return True
    if n.endswith('[]'):
        cnt = r.unpack(obj + 0x18, '<Q')
        print('  raw', r.read(obj + 0x20, min(cnt, 32) * 8).hex())
        return True
    return False

def dump(obj, depth=0):
    if elems(obj): return
    k = r.ptr(obj)
    print(f'{obj:#x} {cname(k)} size={r.unpack(k+0xf8,"<I")}')
    kk = k; seen = []
    while kk and cname(kk) != 'System.Object':
        seen.append(kk); kk = r.ptr(kk + 0x58)
    for klass in reversed(seen):
        tbl = r.ptr(klass + 0x80); n = r.unpack(klass + 0x124, '<H')
        for i in range(n):
            f = tbl + i * 32
            ty = r.ptr(f + 8); bits = r.unpack(ty + 8, '<I')
            if bits & 0x50: continue
            name = r.string(r.ptr(f)); off = r.unpack(f + 0x18, '<i')
            kind, tn = type_name(ty)
            a = obj + off
            try:
                if kind == 17 and 'Obscured' in tn: val = decode_obscured(tn, a - 16 + 16)
                elif kind == 8: val = r.unpack(a, '<i')
                elif kind == 2: val = bool(r.read(a, 1)[0])
                elif kind == 12: val = round(r.unpack(a, '<f'), 4)
                elif kind == 10: val = r.unpack(a, '<q')
                elif kind == 14:
                    p = r.ptr(a); val = repr(r.read(p + 0x14, min(r.unpack(p + 0x10, '<i'), 60) * 2).decode('utf-16le')) if p else None
                elif kind in (18, 21, 29, 28):
                    p = r.ptr(a)
                    val = f'{p:#x} ({cname(r.ptr(p))})' if p else 'null'
                    if p and kind == 29: val += f' len={r.unpack(p + 0x18, "<Q")}'
                    if p and kind == 21 and cname(r.ptr(p)).endswith('List`1'): val += f' count={r.unpack(p + 0x18, "<i")}'
                    if p and kind == 21 and 'Dictionary' in cname(r.ptr(p)): val += f' count={r.unpack(p + 0x20, "<i")}'
                else: val = r.read(a, 8).hex()
            except Exception as e:
                val = f'<err {e}>'
            print(f'  {off:#6x} {tn:<45} {name:<55} {val}')

def resolve(path):
    wdm_k = rt.cached_class(md.find('Gallop.WorkDataManager'))
    inst = r.ptr(r.ptr(r.ptr(wdm_k + 0x58) + 0xb8))
    roots = {'wdm': inst, 'sm': r.ptr(inst + 0x58)}
    roots['ch'] = r.ptr(roots['sm'] + 0x30)
    parts = path.split('.')
    obj = int(parts[0], 0) if parts[0].startswith('0x') else roots[parts[0]]
    for p in parts[1:]:
        if p.startswith('['):  # array / list element
            i = int(p[1:-1])
            if cname(r.ptr(obj)).endswith('List`1'): obj = r.ptr(obj + 0x10)
            obj = r.ptr(obj + 0x20 + 8 * i); continue
        if p.startswith('{'):  # dictionary value by entry index (int key, ref value)
            i = int(p[1:-1])
            obj = r.ptr(r.ptr(obj + 0x18) + 0x20 + 24 * i + 16); continue
        k = r.ptr(obj); found = False
        while k and not found:
            tbl = r.ptr(k + 0x80)
            for i in range(r.unpack(k + 0x124, '<H')):
                f = tbl + i * 32
                if r.string(r.ptr(f)) in (p, f'<{p}>k__BackingField'):
                    obj = r.ptr(obj + r.unpack(f + 0x18, '<i')); found = True; break
            k = r.ptr(k + 0x58)
        if not found: raise KeyError(p)
    return obj

def field_addr(obj, name):
    k = r.ptr(obj)
    while k:
        tbl = r.ptr(k + 0x80)
        for i in range(r.unpack(k + 0x124, '<H')):
            f = tbl + i * 32
            if r.string(r.ptr(f)) in (name, f'<{name}>k__BackingField'):
                return obj + r.unpack(f + 0x18, '<i')
        k = r.ptr(k + 0x58)
    raise KeyError(name)

OI = 'CodeStage.AntiCheat.ObscuredTypes.ObscuredInt'
def oint(obj, name): return decode_obscured(OI, field_addr(obj, name))

def dict_items(d):
    ent, cnt = r.ptr(d + 0x18), r.unpack(d + 0x20, '<i')
    for i in range(cnt):
        e = ent + 0x20 + 24 * i
        yield r.unpack(e + 8, '<i'), r.ptr(e + 16)

def list_items(l):
    arr, cnt = r.ptr(l + 0x10), r.unpack(l + 0x18, '<i')
    return [r.ptr(arr + 0x20 + 8 * i) for i in range(cnt)]

def turn_summary():
    sm = resolve('sm')
    print('turn', oint(sm, '_totalTurnNum'))
    first_horse = None
    for ctype, lst in dict_items(r.ptr(field_addr(resolve('sm._homeInfo'), '_turnInfoListDic'))):
        for ti in list_items(lst):
            gains = {k: (oint(v, 'Value'), oint(v, 'BonusValue')) for k, v in dict_items(r.ptr(field_addr(ti, 'ParamIncDecInfoDic')))}
            horses = list_items(r.ptr(field_addr(ti, 'TrainingHorseList')))
            first_horse = first_horse or (horses[0] if horses else None)
            en = r.unpack(field_addr(ti, 'IsEnable') + 4, '<i') ^ r.read(field_addr(ti, 'IsEnable'), 1)[0]
            print(f"type={ctype} id={oint(ti, '_commandId')} enabled={en != 181} fail={r.unpack(field_addr(ti, 'TrainingFailureRate'), '<i')}% gains={gains} partners={len(horses)}")
    if first_horse:
        dump(first_horse)

if sys.argv[1:] == ['turn']:
    turn_summary(); sys.exit()
for p in sys.argv[1:]:
    print('=====', p); dump(resolve(p))
