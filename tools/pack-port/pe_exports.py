# Print export name -> RVA and the first bytes of code at each, for a list of names.
#
# Why: the game dies on every interop call that returns a VALUE TYPE (int, enum, struct), while
# calls returning objects or nothing come back fine. Il2CppInterop unboxes such returns through
# il2cpp_object_unbox, so the fastest check is static: on stock il2cpp that export is exactly
#     48 8D 41 10 C3        lea rax,[rcx+10h] ; ret
# and anything else under that name means the P/Invoke table binds it to the wrong function.
#
# usage: python pe_exports.py <GameAssembly.dll> name1 name2 ...
import struct, sys

def exports(path):
    d = open(path, 'rb').read()
    pe = struct.unpack_from('<I', d, 0x3C)[0]
    nsec = struct.unpack_from('<H', d, pe + 6)[0]
    optsz = struct.unpack_from('<H', d, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from('<H', d, opt)[0]
    dirs = opt + (96 if magic == 0x10B else 112)
    exp_rva = struct.unpack_from('<I', d, dirs)[0]
    secs = opt + optsz
    tbl = []
    for i in range(nsec):
        s = secs + i * 40
        va, sz, raw = struct.unpack_from('<III', d, s + 12)
        tbl.append((va, sz, raw))
    def off(rva):
        for va, sz, raw in tbl:
            if va <= rva < va + sz:
                return raw + (rva - va)
        return -1
    ed = off(exp_rva)
    nnames, afunc, aname, aord = struct.unpack_from('<IIII', d, ed + 24)
    out = {}
    for i in range(nnames):
        nr = struct.unpack_from('<I', d, off(aname) + i * 4)[0]
        o = struct.unpack_from('<H', d, off(aord) + i * 2)[0]
        fr = struct.unpack_from('<I', d, off(afunc) + o * 4)[0]
        e = off(nr); z = d.index(b'\0', e)
        out[d[e:z].decode('ascii')] = fr
    return d, off, out

if __name__ == '__main__':
    d, off, ex = exports(sys.argv[1])
    for name in sys.argv[2:]:
        rva = ex.get(name)
        if rva is None:
            print(f"{name:<14} ABSENT")
            continue
        o = off(rva)
        aliases = [k for k, v in ex.items() if v == rva and k != name]
        print(f"{name:<14} rva=0x{rva:X}  bytes={d[o:o+12].hex(' ')}  alias={aliases}")
