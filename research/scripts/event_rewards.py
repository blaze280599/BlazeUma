"""Decode WorkSingleModeData.EventChoiceRewardDict: choice -> branches -> gain params. Read-only.

Dictionary<ObscuredInt, EventChoiceReward>; entry stride 40 (hash, next, 20-byte ObscuredInt key, pad, ref value).
"""
import sys, io, contextlib
sys.argv = [sys.argv[0]]
with contextlib.redirect_stdout(io.StringIO()):
    import inspect_obj as I  # reuses reader, metadata, decoders
r = I.r

def arr_items(a):
    return [r.ptr(a + 0x20 + 8 * i) for i in range(r.unpack(a + 0x18, '<Q'))]

def obj_fields(o):
    """name -> decoded value for every instance field (ObscuredInt/int)."""
    out = {}
    k = r.ptr(o)
    tbl = r.ptr(k + 0x80)
    for i in range(r.unpack(k + 0x124, '<H')):
        f = tbl + i * 32
        ty = r.ptr(f + 8); bits = r.unpack(ty + 8, '<I')
        if bits & 0x50: continue
        name = r.string(r.ptr(f)); off = r.unpack(f + 0x18, '<i')
        kind, tn = I.type_name(ty)
        if kind == 17 and 'ObscuredInt' in tn: out[name] = I.decode_obscured(I.OI, o + off)
        elif kind == 8: out[name] = r.unpack(o + off, '<i')
        else: out[name] = f'{r.ptr(o + off):#x}'
    return out

sm = I.resolve('sm')
print('turn', I.oint(sm, '_totalTurnNum'), 'cachedRewardEventId', r.unpack(I.field_addr(sm, '_cachedRewardEventId'), '<i'))
d = r.ptr(I.field_addr(sm, 'EventChoiceRewardDict'))
ent, cnt = r.ptr(d + 0x18), r.unpack(d + 0x20, '<i')
for i in range(cnt):
    e = ent + 0x20 + 40 * i
    key = I.decode_obscured(I.OI, e + 8)
    reward = r.ptr(e + 32)
    print(f'choice key {key}:')
    branches = r.ptr(reward + 0x10)
    for b, br in enumerate(arr_items(branches)):
        gains = [obj_fields(g) for g in arr_items(r.ptr(br + 0x10))]
        print(f'  branch {b}: {gains}')
