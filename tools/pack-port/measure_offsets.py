# Measure the il2cpp struct field offsets in the OLD vs NEW GameAssembly.dll.
#
# VRChat reshuffles the il2cpp struct layouts on (some) builds, which is why
# VRChatStructFix carries a hardcoded VrcOffsets table. Its source is lost, so before
# anything else we need to know whether the layout actually MOVED this build: if every
# accessor still reads the same displacement, the existing VRChatStructFix.dll stays
# valid and nothing has to be rebuilt.
#
# Method: most il2cpp accessors are 2-3 instruction leaf functions like
#     mov rax,[rcx+0xNN] ; ret
# so disassembling the first few instructions and taking the first [rcx+disp] read
# yields the field offset directly. Names are obfuscated and differ per build, so we
# go API -> old obfuscated name (final_mapping.csv) -> index in the lea run -> new name.
#
# READ-ONLY: reads the two DLLs and the CSV, writes nothing but a report.
import csv, json, os, sys
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64, CS_OP_MEM, CS_OP_REG

SCRATCH = os.path.dirname(os.path.abspath(__file__))
OLD_DIR = r"patch"        # the previous build's pack folder (edit or pass paths)
NEW_DIR = r"patch.new"    # the new build's pack folder
CSV = os.path.join(SCRATCH, "final_mapping.csv")

lea_old = json.load(open(os.path.join(SCRATCH, "lea_old.json")))
lea_new = json.load(open(os.path.join(SCRATCH, "lea_new.json")))
idx_of_old = {n: i for i, n in enumerate(lea_old)}

def load(path):
    pe = pefile.PE(path, fast_load=True)
    pe.parse_data_directories([pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_EXPORT']])
    rva = {}
    for e in pe.DIRECTORY_ENTRY_EXPORT.symbols:
        if e.name:
            rva[e.name.decode('ascii', 'ignore')] = e.address
    return pe, rva

def code_at(pe, rva, n=48):
    for s in pe.sections:
        lo = s.VirtualAddress
        hi = lo + max(s.Misc_VirtualSize, s.SizeOfRawData)
        if lo <= rva < hi:
            d = s.get_data()
            off = rva - lo
            return d[off:off + n]
    return b""

md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True

def first_disp(pe, rva):
    """First [reg+disp] memory read in the function prologue -- the field offset."""
    code = code_at(pe, rva)
    if not code:
        return None, ""
    txt = []
    for ins in md.disasm(code, rva):
        txt.append(f"{ins.mnemonic} {ins.op_str}")
        for op in ins.operands:
            if op.type == CS_OP_MEM and op.mem.base != 0 and op.mem.index == 0:
                if op.mem.disp != 0 or ins.mnemonic in ("mov", "movzx", "movsx"):
                    return op.mem.disp, " ; ".join(txt[:3])
        if ins.mnemonic == "ret" or len(txt) >= 6:
            break
    return None, " ; ".join(txt[:3])

pe_o, rva_o = load(os.path.join(OLD_DIR, "GameAssembly.dll"))
pe_n, rva_n = load(os.path.join(NEW_DIR, "GameAssembly.dll"))

rows = []
for r in csv.reader(open(CSV, encoding="utf-8", errors="replace")):
    if len(r) < 2:
        continue
    api, oldname = r[0].strip(), r[1].strip()
    if not api.startswith("il2cpp_") or len(oldname) != 11:
        continue
    i = idx_of_old.get(oldname)
    if i is None:
        continue
    newname = lea_new[i]
    if oldname not in rva_o or newname not in rva_n:
        continue
    do, to = first_disp(pe_o, rva_o[oldname])
    dn, tn = first_disp(pe_n, rva_n[newname])
    rows.append((api, oldname, newname, do, dn, to, tn))

moved = [r for r in rows if r[3] is not None and r[4] is not None and r[3] != r[4]]
same = [r for r in rows if r[3] is not None and r[3] == r[4]]

print(f"accessors compared : {len(rows)}")
print(f"  offset UNCHANGED : {len(same)}")
print(f"  offset MOVED     : {len(moved)}")
print()
if moved:
    print("=== MOVED (struct layout changed -> VrcOffsets would need updating) ===")
    for api, o, n, do, dn, to, tn in moved:
        print(f"  {api:42} 0x{do:X} -> 0x{dn:X}")
        print(f"      old[{o}]: {to}")
        print(f"      new[{n}]: {tn}")
else:
    print("=== NO field offset moved: the existing VRChatStructFix.dll stays valid ===")
print()
print("sample of unchanged (sanity):")
for api, o, n, do, dn, to, tn in same[:8]:
    print(f"  {api:42} 0x{do:X}   [{to}]")
