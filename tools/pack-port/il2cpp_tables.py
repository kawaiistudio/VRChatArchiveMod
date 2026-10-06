# Rebuild of il2cpp_tables.py (lost with an old session scratchpad).
#
# UnityPlayer.dll resolves GameAssembly's ~232 il2cpp_* exports BY NAME, through a
# CONTIGUOUS run of `lea reg,[rip+disp32]` instructions whose targets are the export
# NAME STRINGS, emitted in header order. VRChat obfuscates most of those names and
# reshuffles them every build, but the ORDER of the run is stable -- so old[i] maps to
# new[i]. Three names ship in clear and act as anchors (il2cpp_free, native_stack_trace,
# gchandle_get_target).
#
# READ-ONLY: this only reads the two DLLs and prints. It never writes to them.
import sys, re, json
import pefile

def export_names(path):
    pe = pefile.PE(path, fast_load=True)
    pe.parse_data_directories([pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_EXPORT']])
    names = set()
    if hasattr(pe, 'DIRECTORY_ENTRY_EXPORT'):
        for e in pe.DIRECTORY_ENTRY_EXPORT.symbols:
            if e.name:
                names.add(e.name.decode('ascii', 'ignore'))
    pe.close()
    return names

def scan_lea_run(unity_path, valid_names):
    pe = pefile.PE(unity_path, fast_load=True)
    image = {}
    for s in pe.sections:
        data = s.get_data()
        image[(s.VirtualAddress, s.VirtualAddress + max(s.Misc_VirtualSize, len(data)))] = (s.VirtualAddress, data)

    def read_cstr(rva, limit=128):
        for (lo, hi), (base, data) in image.items():
            if lo <= rva < hi:
                off = rva - base
                if off < 0 or off >= len(data):
                    return None
                end = data.find(b'\x00', off, off + limit)
                if end == -1:
                    return None
                try:
                    return data[off:end].decode('ascii')
                except UnicodeDecodeError:
                    return None
        return None

    hits = []  # (instr_rva, name)
    for s in pe.sections:
        if b'text' not in s.Name.lower():
            continue
        data = s.get_data()
        base = s.VirtualAddress
        i = 0
        n = len(data)
        while i < n - 7:
            if data[i] in (0x48, 0x4C) and data[i + 1] == 0x8D and (data[i + 2] & 0xC7) == 0x05:
                disp = int.from_bytes(data[i + 3:i + 7], 'little', signed=True)
                target = base + i + 7 + disp
                sname = read_cstr(target)
                if sname and sname in valid_names:
                    hits.append((base + i, sname))
                i += 7
                continue
            i += 1
    pe.close()
    return hits

def longest_run(hits, max_gap=0x60):
    """The table is one contiguous cluster: consecutive refs sit ~0x26 bytes apart."""
    if not hits:
        return []
    hits = sorted(hits)
    best, cur = [], [hits[0]]
    for prev, item in zip(hits, hits[1:]):
        if item[0] - prev[0] <= max_gap:
            cur.append(item)
        else:
            if len(cur) > len(best):
                best = cur
            cur = [item]
    if len(cur) > len(best):
        best = cur
    return best

def analyse(tag, unity, gameasm):
    names = export_names(gameasm)
    hits = scan_lea_run(unity, names)
    run = longest_run(hits)
    seq = [n for _, n in run]
    print(f"[{tag}] exports={len(names)}  lea-hits={len(hits)}  longest-run={len(seq)}"
          f"  block_rva=0x{run[0][0]:X}" if run else f"[{tag}] NO RUN")
    for anchor in ("il2cpp_free", "il2cpp_native_stack_trace", "il2cpp_gchandle_get_target"):
        idx = [i for i, n in enumerate(seq) if n == anchor]
        print(f"    anchor {anchor:34} -> index {idx}")
    return seq

if __name__ == "__main__":
    old_dir, new_dir = sys.argv[1], sys.argv[2]
    old = analyse("OLD", old_dir + r"\UnityPlayer.dll", old_dir + r"\GameAssembly.dll")
    print()
    new = analyse("NEW", new_dir + r"\UnityPlayer.dll", new_dir + r"\GameAssembly.dll")
    print()
    print(f"OLD len={len(old)}  NEW len={len(new)}  same_length={len(old) == len(new)}")
    out = r"C:\Users\kaich\AppData\Local\Temp\claude\F--VRCHAT-ARCHIVE\8c8a004c-4b70-4ced-8f47-fa07933a9e9c\scratchpad"
    json.dump(old, open(out + r"\lea_old.json", "w"), indent=1)
    json.dump(new, open(out + r"\lea_new.json", "w"), indent=1)
    if len(old) == len(new):
        changed = [(o, n) for o, n in zip(old, new) if o != n]
        print(f"entries that CHANGED name: {len(changed)} / {len(old)}")
        json.dump([{"old": o, "new": n} for o, n in changed],
                  open(out + r"\translate.json", "w"), indent=1)
        for o, n in changed[:8]:
            print(f"    {o}  ->  {n}")
