# VRChat GameAssembly.dll — il2cpp export de-obfuscation: final notes

Binary: the pack's `GameAssembly.dll` of VRChat build **1903** (the build that was live when these notes were written; the method is what carries over, the offsets are per build)
Imagebase `0x180000000`, 264 exports, 238 distinct RVAs (26 names are ICF-fold aliases).
Deployed P/Invoke table read directly out of `BepInEx\core\Il2CppInterop.Runtime.dll`
(ECMA-335 ImplMap: 174 rows, 90 repointed, 84 still naming the standard symbol).

Final table: `final_mapping.csv` — 100 rows.
* 83 of the 84 previously-unmapped functions
* 17 corrections to mappings that are **already deployed and wrong**
* 1 function left UNKNOWN

---

## 1. Struct layouts established on THIS build

Everything below was derived from disassembly of this exact binary. Several offsets are **not**
stock il2cpp — the class layout in particular is shuffled.

### Il2CppClass  (⚠ non-stock: `name` moved to +0x98)

| off | field | how proven |
|---|---|---|
| +0x00 | `image` | `class_has_attribute` 0xAAEA60 `mov rbx,[rcx]` then uses it as an Il2CppImage |
| +0x18 | `namespaze` | `Type::GetNameInternal` 0xA92B14 `mov rax,[rsi+0x18]; cmp byte[rax],0` = "is namespace empty" then appends `.` |
| +0x20 | `byval_arg` (Il2CppType, 16 B) | `class_get_type` = `lea rax,[rcx+0x20]`; `class_is_interface` reads `byte[rcx+0x2A]` = byval_arg.type |
| +0x40 | `element_class` | `enum_basetype` 0xAAE6B0 `cmp [rcx+0x40],rcx` … `mov rax,[rcx+0x40]; add rax,0x20` |
| +0x48 | **`parent`** | `get_property_from_name` 0xAFD0B0 `mov rbx,[rbx+0x48]` hierarchy walk; `Class::SetupTypeHierarchy` 0xAFFBB7 recurses on +0x48 while incrementing +0x130 |
| +0x50 | `implementedInterfaces` | `class_get_interfaces` iterator (count +0x12C) |
| +0x58 | **`declaringType`** | `Type::GetNameInternal` 0xA92ADD loads `[rsi+0x58]`, adds 0x20, recurses, emits `+`/`.` |
| +0x60 | `generic_class` | `class_is_inflated` = `cmp [rcx+0x60],0; setne`; `class_get_nested_types` guard |
| +0x68 | `typeMetadataHandle` | `Class::SetupProperties` 0xAFFB00 `mov rax,[rsi+0x68]` |
| +0x78 | `methods` | methods iterator (count +0x120); SetupProperties resolves get/set through `[rsi+0x78]` |
| +0x88 | `nestedTypes` | nested-types iterator (count +0x128) |
| +0x90 | `fields` | fields iterator (count +0x124) |
| +0x98 | **`name`** ⚠ | `Type::GetNameInternal` 0xA92B2E `mov rcx,[rsi+0x98]; mov edx,0x60; call strchr` — searching the class name for the generic `` ` `` marker |
| +0xA0 | `properties` | properties iterator (count +0x122); `SetupProperties` writes `[rsi+0xA0]` at the end |
| +0xA8 | `events` | events iterator (count +0x126) |
| +0xB8 | `static_fields` | unique `mov rax,[rcx+0xB8]` getter at 0x004120 |
| +0xC8 | `typeHierarchy` | `class_has_parent` 0xAAE750 indexes `[rdi+0xC8]` by depth |
| +0xF8 | `instance_size` | `class_instance_size` (deployed) |
| +0x104 | `element_size` | `array_get_byte_length` multiplier; `array_element_size` |
| +0x10C | `static_fields_size` | `class_get_data_size` (0xAAEB70, unmapped API) |
| +0x118 | `flags` (TypeAttributes) | `class_is_interface` masks 0x20; `class_is_abstract` masks 0x80 |
| +0x11C | `token` | `class_has_attribute` feeds it as the metadata token |
| +0x120…+0x12E | u16 counts, **stock order** | method 0x120, property 0x122, field 0x124, event 0x126, nested 0x128, vtable 0x12A, interfaces 0x12C, interface_offsets 0x12E |
| +0x130 | `typeHierarchyDepth` (u8) | `class_has_parent`; `SetupTypeHierarchy` `inc cl` |
| +0x132 | `rank` (u8) | `class_get_rank`; used by `array_get_byte_length` as the 1-D test |
| +0x135 | bitfield 1 | bit0 initialized_and_no_error, **bit1 initialized** (`Class::Init` 0xB00389 `test byte[rcx+0x135],2; jne ret`), bit2 enumtype, bit3 nullabletype, bit4 is_generic, **bit5 has_references** (`class_has_references` 0xAAEB20), bit6 init_pending, bit7 size_init_pending |
| +0x136 | bitfield 2 | **bit0 size_inited** (`class_has_references` setup guard 0xAAEB06), bit1 has_finalize, bit2 has_cctor, **bit3 is_blittable**, bit4 is_import_or_windows_runtime, bit5 is_vtable_initialized |

> The bitfield order was the single riskiest question in the class family. It is pinned by two
> independent bodies — `Class::Init` testing bit1 (`initialized`) and `Class::HasReferences`
> returning bit5 after gating on +0x136 bit0 (`size_inited`). That fixes the whole declaration
> order and makes +0x136 bit3 `is_blittable`, **not** `is_valuetype`.

### MethodInfo
`name` +0x10 · `klass` +0x18 · `return_type` +0x20 · `parameters` +0x30 · `genericMethod` +0x40 ·
`token` u32 +0x48 · `flags` u16 +0x4C · `iflags` u16 +0x4E · `slot` u16 +0x50 ·
`parameters_count` u8 +0x52 · bitfield u8 +0x53 (bit0 `is_generic`, bit1 `is_inflated`).

`klass`@+0x18 proven by `method_has_attribute` 0xAAFA50 `mov rax,[rcx+0x18]; mov rbx,[rax]`
(method→klass→image). `flags`/`iflags` proven by `method_get_flags` 0xAAFAF0.
bit1 = `is_inflated` proven by `method_get_param_name` 0xAAFB0B testing it before dereferencing
`genericMethod`@+0x40.

### FieldInfo
`name` +0x00 · `token` u32 +0x08 · `type` +0x10 · `offset` i32 +0x18 · `parent` +0x20.
All four confirmed by `field_get_flags`, `field_get_offset`, `field_has_attribute`,
and the `field_get_value` / `field_set_value` body pair.

### PropertyInfo — stride **0x30**
`attrs` u32 **+0x00** · `get` **+0x08** · `name` **+0x10** · `parent` **+0x18** · `set` **+0x20** · `token` u32 **+0x28**

Derived from the `Class::SetupProperties` metadata loop at 0xAFFB00–0xAFFB69, where `r8` is the
write cursor advancing by 0x30 and the record base is `r8-0x18`:
Il2CppPropertyDefinition `{nameIndex, get, set, attrs, token}` (stride 20) maps
def+0x00→base+0x10, def+0x04→base+0x08, def+0x08→base+0x20, def+0x0C→base+0x00, def+0x10→base+0x28,
plus the owning klass→base+0x18. Independently corroborated: the properties iterator advances by
0x30 and `get_property_from_name` strcmps against `[prop+0x10]`.

**`get` (+0x08) and `set` (+0x20) are NOT adjacent** — this is what the old deployed table got wrong.

### Il2CppType
`data` +0x00 · bitfield dword +0x08 = `{attrs:16, type:8 (byte at +0x0A), num_mods:5, byref:bit29, pinned:bit30}`.
Three witnesses: `Class::FromIl2CppType` 0xAFC10D `movsx ecx,byte[rcx+0xA]`; `type_is_byref`
(`[rcx+8]>>0x1D & 1`); `type_is_static` (`[rcx+8]>>4 & 1`).
`Type::GetName(std::string* out, type, fmt)` = 0xA92A00; formats: **0=IL, 1=REFLECTION, 2=FULL_NAME,
3=ASSEMBLY_QUALIFIED** — the builder 0xA91F90 picks `'.'` vs `'+'` on `fmt==0` and appends the
assembly name on `cmp r15d,3`.

### Il2CppImage — stride **0x48** ⚠ shuffled vs stock
`typeCount` u32 **+0x00** · `nameNoExt` **+0x08** · `dynamic` u8 +0x10 · `customAttributeCount` u32 +0x14 ·
`name` (with `.dll`) **+0x18** · `assembly` **+0x20** · `metadataHandle` **+0x28** ·
`nameToClassHashTable` +0x30 · `codeGenModule` +0x38 · `token` u32 +0x40 · `exportedTypeCount` u32 +0x44

Proven twice. (a) The image-table initializer 0xB01820–0xB018A2 scatters
Il2CppImageDefinition `{nameIndex, assemblyIndex, typeStart, typeCount, exportedTypeStart,
exportedTypeCount, entryPointIndex, token, customAttributeStart, customAttributeCount}` — a perfect
canonical field-order match — into those slots. (b) `Class::FromName` 0xA8F369 uses `cmp esi,[rdi]`
as its **type-enumeration loop bound** and `cmp dword[rdi+0x44],r12d` for exported types.

> The briefing's "Il2CppImage token u32 at +0x00" is **wrong**: +0x00 is `typeCount`, token is +0x40.

`Il2CppImageGlobalMetadata` (stride 0x18): typeStart +0x00, exportedTypeStart +0x04,
customAttributeStart +0x08, entryPointIndex +0x0C, back-pointer to image +0x10.
`Il2CppAssembly` stride 0x58, `image` at +0x00. `Il2CppTypeDefinition` stride 0x58.

### Globals
`Il2CppMemoryCallbacks` @ 0xD6E5748 (7 ptrs / 0x38 B; malloc +0x00, free +0x10) ·
config_dir std::string @ 0xD6E5848 · temp_dir @ 0xD6E5868 · data_dir @ 0xD6E5B08 ·
`GC_dont_gc` @ 0xD6E81F0 · `GC_heapsize` @ 0xD733340 · `GC_large_free_bytes` @ 0xD733360 ·
`GC_unmapped_bytes` @ 0xD7333E0 · intern table @ 0x18D6E9D98 · find-plugin callback @ 0xD6E9FE0 ·
log callback @ 0xD6E5740.

---

## 2. UNKNOWN — deliberately left unmapped

**`il2cpp_gc_collect_a_little`** — the two remaining candidates in the GC block,
`gYsBZmDHeZK` (0xAAF460, bare `jmp 0xA65AB0`) and `gjnSFyflogU` (0xAAF470, sets a global then
`jmp 0xA65AB0`), tail-jump into the *same* locking routine, which immediately overwrites `rcx`.
Nothing separates `gc_collect(int)` / `gc_collect_a_little()` /
`gc_start_incremental_collection()` from each other by body, and the canonical-order prior is
unusable here because `gc_disable`/`gc_enable` were folded far outside the API block
(onto bdwgc's own `GC_disable`/`GC_enable` at 0xA6EFD0/0xA6EF80). Left unmapped rather than guessed.

**`il2cpp_gc_collect` must additionally be REMOVED from the deployed table** — see §3.

Other exports that exist but have no standard-API home, listed so nobody maps onto them by accident:
`GeAeWkbwXJO` 0xAAE700 (`+0x135` bit1 = `initialized`), `Xrji_UvNs_d` 0xAAEB70
(`class_get_data_size`, not in Il2CppInterop's 174), `QtnsRQNovaw` 0xAAE6D0
(`class_from_system_type`), `wQFkKQRXPsu`/`UJOANnAZgco` (constant-returning size accessors).

---

## 3. Corrections to already-deployed mappings

These are in `final_mapping.csv` with `REPLACES DEPLOYED …` at the front of the evidence column.
They are **not** additions — applying the CSV must overwrite the existing EntryPoint.

| API | deployed (wrong) | what it actually is | correct export |
|---|---|---|---|
| `il2cpp_class_get_fields` | `IVjDwejqCp_` | the **properties** iterator | `NnUgnFASjZl` |
| `il2cpp_class_get_interfaces` | `npApjJKKUuO` | the **nested-types** iterator | `QKWWTobOjEx` |
| `il2cpp_class_get_nested_types` | `OfYRWVorWbz` | **not an export at all** | `npApjJKKUuO` |
| `il2cpp_class_get_element_class` | `jnARKkncjcT` | `enum_basetype` (NULL for non-enums) | `KnIRRdHoRuo` |
| `il2cpp_class_get_parent` | `EuNGSVcTusZ` | reads +0x20 = inside `byval_arg` | `fuIEbponSru` |
| `il2cpp_class_get_namespace` | `QdJhQEJilhG` | reads +0x28 = inside `byval_arg` | `GEQaqHTaHNz` |
| `il2cpp_class_is_enum` | `MNlemQXZQAE` | `is_generic` (bit4 not bit2) | `AZjeWkhbVyC` |
| `il2cpp_class_array_element_size` | `nTKazuUmOzK` | plain `element_size` getter | `tCCUCjaXvr_` |
| `il2cpp_method_get_class` | `pbByKDHkSQX` | reads +0x10 = `method->name` | `GpbVPFMyscA` |
| `il2cpp_method_get_declaring_type` | `jjuPxFvpWIP` | reads +0x10 = `method->name` | `pdNwqDVoBRW` |
| `il2cpp_property_get_get_method` | `GnJoUVCbOGk` | **a whole liveness routine**, not an accessor | `UpxOqXasgLu` |
| `il2cpp_property_get_name` | `UpxOqXasgLu` | reads +0x08 = the **get method** | `BmCgtBkwLob` |
| `il2cpp_image_get_name` | `tGMscglseyc` | reads +0x18 = the `.dll` **filename** | `UpxOqXasgLu` |
| `il2cpp_monitor_enter` | `legIIoGfFPc` | `monitor_try_**wait**` with an uninit timeout | `mzzIMXNeKvR` |
| `il2cpp_string_new` | `FKreBiXiPmS` | `String::NewLen` — reads `edx` as a length the 1-arg caller never sets | `fGGxfJQudbL` |
| `il2cpp_gc_is_disabled` | `gYsBZmDHeZK` | a large locking routine | `rdcBPyAmXpZ` |
| `il2cpp_gc_collect` | `JtkJvM_WCKb` | **`GC_enable`** — decrements `GC_dont_gc` | **DELETE — leave unmapped** |

`il2cpp_class_get_name` → `dgYXa_mduNh` is in the CSV marked UNCHANGED; it is already correct and
is the anchor that proves `name` is at +0x98.

### Stale rows in `known_good.csv` (do not use as anchors)
* `il2cpp_class_get_properties,EpavreEv_Rx` — `EpavreEv_Rx` is `class_get_property_from_name`.
* `il2cpp_class_get_type,QUNCjezHnSe` — `QUNCjezHnSe` is `format_stack_trace` (3 args).
  The **deployed** `class_get_type` → `zTnLSRwoUWN` (`lea rax,[rcx+0x20]`) is correct.
* `il2cpp_domain_get,LtWPyhoLidH` — `LtWPyhoLidH` is `get_corlib` (block index 11), as the deployed
  table already has it. The real `domain_get` is `EiQlgBpbhvd` 0xAAEFA0 (`jmp Domain::GetCurrent`),
  which Il2CppInterop does not P/Invoke.
* `il2cpp_method_get_class,pbByKDHkSQX` and `il2cpp_method_get_return_type,SHOnCfrkQkt` — the
  latter is right (+0x20), the former is not (+0x10).

---

## 4. Legitimate ICF folds in the final table

Multiple API rows sharing one RVA is expected and safe here — each pair reads the *same* offset of
*its own* struct, so one body correctly serves both.

| RVA | body | APIs |
|---|---|---|
| 0x079C50 | `mov rax,[rcx+0x18]` | image_get_filename · property_get_parent · class_get_namespace · method_get_class · method_get_declaring_type |
| 0x080420 | `mov rax,[rcx+0x20]` | image_get_assembly · property_get_set_method · (+ deployed method_get_return_type) |
| 0x1D2A10 | `mov eax,[rcx]` | image_get_class_count (typeCount) · property_get_flags (attrs) |
| 0x44DD80 | `mov rax,[rcx+8]` | property_get_get_method · image_get_name — **the only RVA where two rows share one export *name*** (`UpxOqXasgLu`), because it is the sole name there. Harmless: two P/Invokes may carry the same EntryPoint string. |
| 0xAAFFA0 | strlen + `String::NewLen` | string_new · string_new_wrapper (il2cpp defines them identically) |
| 0x004910 | `ret 0` | custom_attrs_free (empty in il2cpp) |

---

## 5. Runtime-verify these first

Ordered by blast radius, not by doubt.

1. **`il2cpp_class_get_fields` → `NnUgnFASjZl`** and **`il2cpp_class_get_properties` → `IVjDwejqCp_`.**
   These two were crossed in the deployed table. Enumerate a known class (e.g. `UnityEngine.Transform`)
   and check that field names/offsets are sane and that the counts match `class_num_fields`.
2. **`il2cpp_method_get_class` → `GpbVPFMyscA`** and **`il2cpp_class_get_parent` → `fuIEbponSru`.**
   Both sit on Il2CppInterop's method-resolution path and both were returning the wrong field.
   Resolving `UnityEngine.Object::op_Equality` and getting *that* method back is the check.
3. **`il2cpp_property_get_get_method` → `UpxOqXasgLu`.** Previously pointed at a full routine;
   this is the most likely current crash source in the property path.
4. **`il2cpp_custom_attrs_get_attr` → `BHspDCGHNDK`** vs **`has_attr` → `MNbTfAxIqdb`.**
   This build orders `has_attr` *before* `get_attr`, the reverse of the canonical header, so anyone
   re-deriving by RVA order will "fix" it back and break it. I separated them by return width —
   0xAC82F0 returns an 8-bit bool (`xor al,al` / `movzx eax,bl`), 0xAC8440 returns a 64-bit pointer
   (`mov rax,rsi`, or `xor eax,eax` on a miss). **Getting these backwards makes `get_attr` return
   `1` as an object pointer → immediate crash on deref.** Verify before trusting.
5. **`il2cpp_class_is_blittable` → `vrDXzzTzHAv`.** Rests entirely on the +0x135/+0x136 bitfield
   ordering. It is the unique reader of +0x136 bit3 in the whole export table and cannot crash
   (it returns a masked bit), but a wrong answer would silently change marshalling decisions.
6. **`il2cpp_gc_enable` → `JtkJvM_WCKb` plus deleting `il2cpp_gc_collect`.** If the delete is skipped,
   `gc_collect()` keeps decrementing `GC_dont_gc` until it goes negative and the GC stops forever.
7. **`il2cpp_custom_attrs_free` → `EYhKCcrTpxf`** (the only `medium` row). Placement is inference,
   not proof; the mitigation is that the target is literally `C2 00 00` (`ret 0`), which cannot
   misbehave in the x64 ABI regardless of what it "really" is — and it replaces a guaranteed
   `EntryPointNotFoundException`.

### Residual uncertainty, stated plainly
* Everything except `custom_attrs_free` is backed by a body-level semantic argument (a field offset,
  an arity, an immediate constant, a callee identity, or a global address) — not by export ordering
  alone. Ordering was used only as corroboration.
* The weakest *semantic* arguments in the `high` set are `il2cpp_set_temp_dir` (identified by
  elimination once config and data were pinned by their `"etc"` / `"Data"` fallbacks) and
  `il2cpp_set_find_plugin_callback` (identified by elimination against the log-callback setter plus
  canonical position). Both are startup-only and neither is called by Il2CppInterop, so the
  practical risk is nil.
* The 10-slot thread/stack-frame run (`is_vm_thread` … `thread_get_stack_depth`) matches
  il2cpp-api-functions.h one-for-one in **both order and arity**, with the deployed
  `current_thread_get_frame_at` as a mid-run anchor and two self-proving endpoints. I treat that as
  high confidence, but it is the one group where a *systematic* off-by-one would move all ten
  together. If stack walking misbehaves, suspect the whole run rather than one row.
