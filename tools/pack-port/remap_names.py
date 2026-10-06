# Translate the obfuscated il2cpp export names inside the BepInEx/doorstop binaries
# from the OLD VRChat build to the NEW one, using the index mapping proven by
# il2cpp_tables.py (232-entry lea run, 3 clear anchors at identical indices).
#
# Every name is exactly 11 chars, so every replacement is SAME-LENGTH and can be done
# IN PLACE: no RVA/offset/stream-length in the PE or the .NET metadata shifts.
# Both encodings must be handled: the native P/Invoke ImplMap strings are ASCII, while
# the C# `ldstr` literals in the #US heap are UTF-16LE.
#
# Default is a DRY RUN. Pass --apply to actually write (a .bak-preremap is made first).
import json, os, sys, shutil

SCRATCH = os.path.dirname(os.path.abspath(__file__))   # lea_old.json / lea_new.json live beside this script
old = json.load(open(os.path.join(SCRATCH, "lea_old.json")))
new = json.load(open(os.path.join(SCRATCH, "lea_new.json")))
assert len(old) == len(new), "table length mismatch -- refuse to map"

PACK = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("--") else r"patch.new"
APPLY = "--apply" in sys.argv

TARGETS = [
    os.path.join(PACK, r"BepInEx\core\Il2CppInterop.Runtime.dll"),
    os.path.join(PACK, r"BepInEx\core\BepInEx.Unity.IL2CPP.dll"),
    os.path.join(PACK, "winhttp.dll"),
]

# Only names that actually differ need touching; skip the 3 plaintext anchors.
pairs = [(o, n) for o, n in zip(old, new) if o != n]
assert all(len(o) == len(n) for o, n in pairs), "non-equal length pair -- unsafe, abort"

print(f"pack       : {PACK}")
print(f"mapping    : {len(pairs)} names changed out of {len(old)}")
print(f"mode       : {'APPLY (writing)' if APPLY else 'DRY RUN (no writes)'}")
print()

grand_old = grand_new = 0
for path in TARGETS:
    if not os.path.exists(path):
        print(f"  !! MISSING {path}")
        continue
    data = bytearray(open(path, "rb").read())
    orig = bytes(data)
    n_ascii = n_utf16 = 0
    hits_old = set()

    for o, n in pairs:
        ob, nb = o.encode("ascii"), n.encode("ascii")
        c = data.count(ob)
        if c:
            hits_old.add(o); n_ascii += c
            if APPLY:
                data = bytearray(data.replace(ob, nb))
        ow, nw = o.encode("utf-16-le"), n.encode("utf-16-le")
        c = data.count(ow)
        if c:
            hits_old.add(o); n_utf16 += c
            if APPLY:
                data = bytearray(data.replace(ow, nw))

    # how many NEW names are already present (i.e. file already remapped)?
    already = sum(1 for _, n in pairs if n.encode("ascii") in orig or n.encode("utf-16-le") in orig)

    print(f"  {os.path.basename(path)}")
    print(f"      size={len(orig):,}  OLD-name refs: ascii={n_ascii} utf16={n_utf16}"
          f"  distinct={len(hits_old)}   NEW-names already present={already}")
    grand_old += n_ascii + n_utf16
    grand_new += already

    if APPLY and (n_ascii or n_utf16):
        assert len(data) == len(orig), "LENGTH CHANGED -- refusing to write"
        bak = path + ".bak-preremap"
        if not os.path.exists(bak):
            shutil.copy2(path, bak)
        open(path, "wb").write(bytes(data))
        # verify
        after = open(path, "rb").read()
        left = sum(after.count(o.encode("ascii")) + after.count(o.encode("utf-16-le")) for o, _ in pairs)
        print(f"      WRITTEN (backup {os.path.basename(bak)}) -- old names left: {left}")

print()
print(f"TOTAL old-name references found: {grand_old}")
if not APPLY:
    print("dry run only -- rerun with --apply to write")
