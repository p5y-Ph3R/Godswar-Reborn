# Origin Enhancer and Gear Mentor UI

## Current decision

The client has two distinct NPC workflows which must not be conflated:

- Gear Mentor (`Sparta_070` / `Athens_070`) uses the stock NPC function dialog
  `4`. The loaded NPC-function script declares `NPC_FLAG_SYS_BREAK = 4` and
  dispatches it through the
  untouched `NpcFunBreak.lua` presentation.
- Origin Enhancer (`Sparta_143` / `Athens_143`) uses enhancer dialog `118` and
  the separate `NpcFunEnhancer.lua` workflow documented below.

The original Gear Mentor dialog requires no new UI. For NPC 070, one native
opcode-`10067` advertisement packs dialog `4` followed by Class Suit dialog
`37` as `37004`; both appear in the client's first NPC-choice window. The
custom GWGE2 Origin Enhancer wrapper has been removed, so all three workflows
use their shipped client presentations.

The English client shipped `SYS_NPC_4` with the unrelated label `Salary` even
though the Chinese localization and the loaded script both identify dialog
`4` as equipment enhancement. The reproducible, idempotent text-table fix is:

```text
tools/PatchClientGearMentorLocalization.ps1
```

It changes only `Localization/en_us/Text/NPCDescription.dat`, replacing that
one UTF-16 entry with `Gear Enhancement`; it does not patch `Origin.exe` or
alter the dialog implementation. The client must be restarted to reload the
text table.

The shipped Forge window remains unchanged with its original four tabs:

1. Forge
2. Gems
3. Comp
4. Trans

Origin Enhancer is opened only by interacting with its physical NPC 143. The
custom `SystemBar` icon and E hotkey have been removed. Neither workflow is a
fifth Forge tab or reuses Forge item slots/opcodes.

## Client restoration

The supported restoration command is:

```text
tools/RestoreClientOriginalOriginEnhancer.ps1
```

It invokes the marker-aware rollback in
`PatchClientStandaloneGearEnhancement.ps1`, refuses to write while
`Origin.exe` is running, creates a timestamped backup, preserves UTF-8/BOM
state, writes atomically, and validates the resulting XML. It is idempotent
and verifies that `Origin.exe` and the separate Gear Mentor
`NPCDescription.dat` localization fix are unchanged.

Restoration removes only the marked GWGE2/GWGE3 blocks from these six client
files:

```text
Localization/en_us/UI/XML/SystemBar.xml
Localization/zh_cn/UI/XML/SystemBar.xml
Localization/en_us/UI/XML/NpcFun.xml
Localization/zh_cn/UI/XML/NpcFun.xml
Localization/en_us/UI/XML/SystemBar.lua
Localization/en_us/UI/XML/NpcFun/NpcFunEnhancer.lua
```

Both locale configurations use the shared English Lua scripts in this client
build. `Origin.exe`, `EquipForgeExUI.xml`, Forge localization, all Forge
controls, `NpcFunBreak.lua`, and the Gear Mentor label fix remain untouched.

Typical use from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\RestoreClientOriginalOriginEnhancer.ps1
```

The live client was restored on 2026-07-23. Its pre-restore backup is:

```text
C:\Reborn\backups\client-standalone-gear-enhancement-Revert-20260723-025031824
```

The failed fifth-tab experiment has been rolled back. The rollback snapshot is:

```text
C:\Reborn\backups\client-gear-enhancement-tab-Revert-20260722-181926110
```

The standalone four-file patch was installed and verified on 2026-07-22. Its
pre-install backup is:

```text
C:\Reborn\backups\client-standalone-gear-enhancement-Apply-20260722-191221569
```

The Forge-skinned six-file revision replaced that bridge on 2026-07-22. Its
pre-upgrade backup is:

```text
C:\Reborn\backups\client-standalone-gear-enhancement-Apply-20260722-224523344
```

`tools/PatchClientGearEnhancementTab.ps1` and the Apply mode of
`tools/PatchClientStandaloneGearEnhancement.ps1` describe obsolete GWGE1/2/3
experiments and are not the supported release path.

## Gear Mentor dialog-4 contract

The physical Gear Mentors are `Sparta_070` / `5067` and `Athens_070` / `5209`.
The authoritative Sparta and Athens server actor tables place both NPCs at
`142,-165` with facing `1.7`.
The client also contains a navigation destination named `Gear Enhancer` at
`144,-162` in both cities (`Monster/Sparta/Address.ini` and
`Monster/Athens/Address.ini`). That is the walk-to marker in front of the NPC,
not an actor spawn record: NPC actors and their coordinates are streamed by
the server in opcode `10020`. The client-side NPC appearance INI does not own
world placement. Sparta now uses the exact recovered original-server actor
table at `C:\Users\Iamc1\Downloads\Sparta\Sparta\NPC.INI` (SHA-256
`A7DFDF9D3C90D27960F730B4B65A7EA37D7F41FC80F7788E584AD80E59BFF340`);
the server maps file X to protocol X, file Y to protocol Z, and file Z to
facing. The capital actor tables are authoritative ahead of captured or
generated fallback positions.

A normal interaction opens NPC function dialog `4`, which the stock client
dispatches to `NpcFunBreak.lua`. The server accepts Gear Mentor actions only
when both the resolved NPC key is `*_070` and the request dialog is `4`.

The server now advertises all nine original `NpcFunBreak.lua` menu positions,
so IDs `4` and `5` no longer leave two blank rows between Add and Delete:

| Native ID | Original function | Server status |
|---:|---|---|
| `1` | Decompose | Implemented |
| `2` | Enhance | Implemented |
| `3` | Add | Implemented |
| `4` | Make Attribute Stones | Implemented |
| `5` | Instructions | Reserved / no mutation |
| `6` | Delete attributes | Implemented |
| `7` | Wash dust | Reserved / no mutation |
| `8` | Transform crystals | Implemented |
| `9` | Combine pieces into level-4/5 gems | Client confirms with action `9`; server normalizes it to authoritative page operation `201` |

Reserved entries `5` and `7` return the stock result `999` (`Temporarily
Disabled`) and never mutate inventory. `NpcFunBreak.lua` still supplies every
original label, position, item control, confirmation flow, and result message.

Gear Mentor dialog `4` and Origin Enhancer dialog `118` share the authoritative
transaction implementation for these three operations, but never NPC identity
or dialog validation. A `*_070` request carrying dialog `118`, or a `*_143`
request carrying dialog `4`, cannot mutate inventory.

## Origin Enhancer dialog-118 contract

The shipped workflow begins when the server opens dialog `118` for a physical
Origin Enhancer NPC. The client then sends opcode `10069` with initial sub-ID
`-1` and initialized item arguments set to `-1`. The removed SystemBar
experiment used `GameAPI:ExecUIScript(118)` and virtual NPC `0`; that is no
longer exposed by the client, and the matching server-only NPC-`0` launcher
and remote-session context have also been removed. An NPC-`0` action is now an
unknown NPC action and receives no enhancer menu.

The complete request is 92 bytes on the wire: its four-byte length/opcode
header is followed by an 88-byte payload. `GamePacket.Payload` removes that
header, so `GearEnhancerProtocol.FunctionActionPayloadLength` correctly remains
`88`. The remaining native payload tail can contain scratch stack data, so the
server treats unused fields only as navigation data and never trusts them as
item selections.

On a valid request from a currently visible physical NPC `143`, the server
returns the original captured wire order. The stock dialog positions buttons
by sub-ID, so its visible order is still Add, Enhance, Delete:

| Visible menu | Native sub-ID |
|---|---:|
| Add | `3` |
| Enhance | `2` |
| Delete | `6` |

The physical dialog-`118` endpoints are the Origin Enhancer NPCs:
`Sparta_143` / `5140` and `Athens_143` / `5282`. The stock Gear Mentors
(`Sparta_070` / `5067` and `Athens_070` / `5209`) instead open dialog `4` and
never enter this protocol.


## Window ownership and slot layout

Origin Enhancer uses the shipped `NpcFun.xml` `T_SimpleWindow` `FirstWin`
controls and untouched `NpcFunEnhancer.lua`, not the Forge modal. There is no
custom wrapper, frame, tab skin, SystemBar icon, or E hotkey.

The three clickable faces remain shipped native menu controls `800001` through
`800003`. The executable populates those controls with the server-provided
sub-ID, and their native click handler sends opcode `10069`. Visual-only tab
controls cannot replace them because they would not send an enhancer
operation.

The native item controls remain `800031` through `800034`; confirm and cancel
remain `800020` and `800021`; close remains `800040`. The shipped Origin
Enhancer presentation is:

```text
Gear  ->  Catalyst  ->  Attribute Stone
```

In the parsed 18-word item argument list, the confirmed selection is:

| Argument index | Native meaning | Visible position |
|---:|---|---|
| `6` | Gear | first |
| `7` | Catalyst | second |
| `8` | Attribute Stone | third |

Native bag references `100..195` map to authoritative kit-bag slots `0..95`.
A commit is accepted only when those three fixed arguments contain valid bag
references and every other argument is `-1`. Short or scratch-bearing menu
packets are navigation only and can never mutate inventory.

The physical Gear Mentor's stock `NpcFunBreak.lua` path behaves differently:
each item insertion/removal is sent separately as opcode `10193` with only a
bag page, page slot, and low-byte selected flag. The remaining three bytes are
unstable native scratch. It carries neither a destination-control ID nor an
item category. For Add, Enhance, and Delete, the server stages those selections
in the native control order for the active account/character/NPC/dialog:

```text
Gear  ->  operation catalyst  ->  Attribute Stone
```

The physical Gear Mentor creates an operation-unbound context when its initial
menu is returned. This is necessary because `NpcFunBreak.lua` changes from the
menu to Add, Enhance, Delete, Decompose, Make Attribute Stones, or Transform
Crystals entirely on the client; it does not send an intermediate opcode
`10069` identifying that choice. The final `10069` supplies the authoritative
operation sub-ID and binds the staged selection to that operation. Combine is
the exception: menu `9` requests the server-backed action page `201` before its
one-item confirmation.

When Start is clicked, the stock control sends `10193` removal events in role
order before its final `10069`. Those removals clear the client controls; they
are not a cancellation. A complete staged selection remains usable if no clear
has begun. Once a clear begins, the server preserves a one-shot snapshot only
for the complete operation-specific clear burst: three items for
Add/Enhance/Delete, one to three for Decompose, and one for Make Stones,
Transform, or Combine. A normal partial removal still removes that selection
and cannot revive an older request. Clear-burst snapshots expire after one
second; this narrowly correlates the stock client's immediately following
confirmation without turning an ordinary later deselection into a pending
operation.

The context expires after two minutes and is consumed by the first confirm, so
it cannot cross characters or be replayed. The final stock opcode `10069` may
contain harmless scratch instead of usable inline item references; when a
matching staged context exists, the exact item snapshots captured by `10193`
are revalidated against the locked authoritative bag rows. Replacing an item
in a staged slot therefore produces a stale-selection result instead of
consuming the replacement.

The shipped Lua/XML controls are generic and expose no category-filter API;
opcode `10193` also provides no trustworthy destination metadata. Accordingly,
the client may render a wrongly chosen item temporarily, but confirmation can
never accept it: the backend validates each fixed role and returns the matching
native slot/material error without consuming or changing anything. A visual
pre-drop rejection would require an optional executable hook and is not part of
the correctness or security boundary.

## Operations and materials

Each successful operation consumes exactly one Attribute Stone and exactly one
catalyst. The three selections must refer to distinct bag slots.

| Operation | Sub-ID | Stock Gear Mentor control order | Catalyst item |
|---|---:|---|---|
| Add | `3` | Gear, Flame Spark, Attribute Stone | Flame Spark `9990` |
| Enhance | `2` | Gear, Quartz Plate, Attribute Stone | Quartz Plate `9960`-`9963`, one piece |
| Delete | `6` | Gear, Water Grain, Attribute Stone | Water Grain `9991` |

Quartz Plate levels advance a matching enhanceable attribute by one level:

| Quartz item | Required level | Result level |
|---:|---:|---:|
| `9960` | 1 | 2 |
| `9961` | 2 | 3 |
| `9962` | 3 | 4 |
| `9963` | 4 | 5 |

The material catalog contains the 45 shipped Attribute Stones (`9930`-`9938`,
`9940`-`9959`, and `9970`-`9985`); item `9939` is an intentional client-data
gap. Only the nine ordinary stone families `9930`-`9938` have Quartz upgrade
chains. Other stones can be added or deleted but cannot be Quartz-enhanced.

Add chooses the first ordinary attribute template in the stone family that is allowed
by the equipment template's `MainAttribute` data. It rejects an incompatible
stone, an existing attribute from the same family, or a gear item whose five
ordinary attribute slots are full. The Class Suit workflow separately owns one
profession-specific field and two different-element fields on Class Suit
III/IV gear; none consumes an ordinary slot. Elemental stones deliberately
fail closed in ordinary Gear Enhancement. Enhance
requires the matching family, synchronized attribute template/level fields,
and the Quartz Plate for the current level. Delete removes the matching family
and compacts the remaining attribute and attribute-level pairs without
separating them. The compatible eight-field contract is documented in
[Five ordinary plus three Class Suit fields](class-suit-seven-attribute-protocol.md).

If any consumed material is bound, the resulting gear is bound. A failed
operation consumes nothing and does not change the gear.

The other Mentor material operations and their exact recipes are documented
in [Gear Mentor material workflows](gear-mentor-material-workflows.md).

## Authoritative server transaction

The server-owned durability and replay rules are in
[Gear Enhancement authoritative transaction](gear-enhancement-authoritative-transaction.md).

## Native result sub-IDs

The server returns the shipped result IDs on the originating dialog (`4` or
`118`) so the corresponding stock client script displays the outcome:

| Result ID | Meaning |
|---:|---|
| `301` | Invalid gem pieces |
| `302` | Fewer than 99 matching pieces |
| `303` | Bag is full while combining pieces |
| `304` | Gem pieces combined successfully |
| `999` | Original menu operation is temporarily disabled |
| `1001` | Nothing is selected |
| `1002` | A staged bag item is missing or changed |
| `1003` | A decomposition input is not gear |
| `1004` | Gear quality/grade is too low to decompose |
| `1005` | Decomposition succeeded |
| `1006` | Gear is missing |
| `1007` | Attribute Stone is missing |
| `1008` | Quartz Plate is missing |
| `1009` | Quartz Plate level does not match |
| `1010` | Enhance succeeded |
| `1011` | All gear attribute slots are full |
| `1012` | Attribute family is already present |
| `1013` | Add succeeded |
| `1014` | Gear level is too low to decompose |
| `1015` | Character level is too low to decompose |
| `1016` | Fewer than 99 matching dust |
| `1017` | Attribute Stone creation succeeded |
| `1018` | Attribute is not allowed on this gear |
| `1019` | Selection or request is invalid |
| `1020` | Result does not fit in the bag |
| `1021` | Flame Spark is missing |
| `1022` | Invalid Attribute Dust |
| `1023` | Matching gear attribute is missing for Enhance |
| `1024` | No gear selected for decomposition |
| `1025` | No dust selected |
| `1026` | Enhance materials are insufficient |
| `1027` | Add materials are insufficient |
| `1028` | Water Grain is missing |
| `1029` | Matching gear attribute is missing for Delete |
| `1030` | Delete succeeded |
| `1031` | This attribute cannot be Quartz-enhanced |
| `1032` | Class Suits cannot be decomposed |
| `1822` | Invalid Crystal transformation input |
| `1823` | Crystal transformation succeeded |

Success is acknowledged before refreshed bag pages are sent. Reject paths send
only the appropriate result and leave the authoritative inventory unchanged.

## Native evidence and NPC placement

The capture evidence, native Lua path, and restored Athens/Sparta NPC actor
coordinates are maintained in
[Gear enhancement native evidence](gear-enhancement-native-evidence.md).

## Acceptance criteria

The feature is ready for release when:

- Forge still displays and executes only its four original tabs;
- the stock Sparta and Athens Gear Mentors (NPC 070) open dialog `4` through
  untouched `NpcFunBreak.lua`;
- Origin Enhancer (NPC 143) remains separate and opens dialog `118` through
  `NpcFunEnhancer.lua`;
- no Origin Enhancer SystemBar icon, E hotkey, or GWGE wrapper remains;
- no virtual NPC `0` launcher or remote enhancer context remains server-side;
- an Origin Enhancer operation click emits opcode `10069` and receives its
  operation page on dialog `118`;
- a physical Gear Mentor operation opens locally with no intermediate request,
  stages its three `10193` choices, preserves the native three-clear Start
  burst, and commits only when the final dialog-`4` opcode `10069` arrives;
- close `800040`, confirm `800020`, cancel `800021`, and item controls
  `800031..34` retain their shipped behavior;
- one valid confirmation consumes exactly one stone and one catalyst;
- invalid, stale, replayed, incompatible, or insufficient selections consume
  nothing;
- changed attributes and binding persist through relog and refreshed bag data
  contains no stale item copy;
- all native result messages display correctly;
- the correct server-side dialog split is preserved, with only the isolated
  English `SYS_NPC_4` text-table correction applied for Gear Mentor;
- `Origin.exe` remains untouched by both Lua workflows.
