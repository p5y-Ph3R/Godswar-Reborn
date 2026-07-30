# Zodiac Protocol (`10297`)

This note records the implemented synchronization boundary and separates captured behavior from configurable emulator policy.

## Confirmed full sync

The native client sends a 24-byte module `0`, SID `1` request when the Zodiac window opens:

```text
180039280000000000000100000000000000000000000000
```

The working server responds with exactly 328 bytes: a 24-byte event header followed by a mandatory 304-byte client state block. The client copies the whole state block, so a short generic acknowledgement is unsafe.

| Packet offset | Type | Meaning |
|---:|---|---|
| `+0` | `u16` | Length (`328`) |
| `+2` | `u16` | Opcode (`10297`) |
| `+4` | `u32` | Local player object ID |
| `+8` | `u16` | Module (`0`) |
| `+10` | `u16` | SID (`1`) |
| `+12` | `i32` | Accumulated combat EXP, x100 units |
| `+16` | `i32` | Accumulated Talent EXP, x100 units |
| `+20` | `i32` | Captured full-sync marker (`1`) |

Important state offsets below are relative to the state block at packet `+24`:

| State offset | Type | Meaning |
|---:|---|---|
| `+0` | `i32` | Zodiac type (`0..11`) |
| `+4` | `i32` | Lucky Day active flag |
| `+8` | `u8` | Zodiac level (`1..30`) |
| `+12` | `i32` | Zodiac energy |
| `+16` | 12-byte record | First native Zodiac-stone record; zero-filled when empty |
| `+28` | 12-byte record | Second native Zodiac-stone record; zero-filled when empty |
| `+40` | `f32` | Combat EXP mirror overwritten from header `v1` by the client |
| `+44` | `f32` | Talent EXP mirror overwritten from header `v2` by the client |
| `+48` | 16 records | Sixteen 16-byte skill-training-grid records, ending exactly at state `+304` |

Each grid record stores `((gridIndex / 4) << 8) | level` in its first
little-endian dword and the selected skill ID in its second dword. Thus the
zero-based row markers are `0x000`, `0x100`, `0x200`, and `0x300`; an
unassigned skill uses `-1`. Captured retail-state anchors are:

| Grid | State offset | Captured packed level |
|---:|---:|---:|
| `0` | `+48` | `0x0000000C` (level 12) |
| `4` | `+112` | `0x0000010C` (level 12) |
| `8` | `+176` | `0x00000209` (level 9) |
| `12` | `+240` | `0x0000030A` (level 10) |

This layout is independently confirmed by `Origin.exe`: the constructor
initializes sixteen 16-byte records at state `+0x30`, SID `1` copies exactly
`0x130` (304) state bytes, and the SID `100`/`101` handlers write the record's
low level byte at `state + 0x30 + index * 0x10`. The packet anchors are in
`captures/working-enter-20260513-233020.log`; UI behavior and costs are in
`SkillTrainProc.lua` and `SkillTrainConfig.lua`.

## Character creation

Creation payload byte `35` is the selected Zodiac (`0..11`). Hair begins at byte `36`; Faith remains the independent byte `70` field. Invalid or missing Zodiac values safely fall back to Aries (`0`).

## Zodiac level up (SID `3`)

The shipped client requests a Zodiac level-up with
`GameAPI:ConsEventRequest(0, 3, 1, 1)`. The two fixed values are UI mode
parameters, not trusted levels, costs, or balances. The server derives the
current character and all requirements from authoritative storage.

On success the server atomically deducts energy and advances one level, then
sends the 24-byte SID `3` response below. Its field meaning is decoded from the
shipped `Constellation.lua`; unlike SID `1`, this response has not yet been
confirmed against a retail-server packet capture:

| Packet offset | Type | Meaning |
|---:|---|---|
| `+0` | `u16` | Length (`24`) |
| `+2` | `u16` | Opcode (`10297`) |
| `+8` | `u16` | Module (`0`) |
| `+10` | `u16` | SID (`3`) |
| `+12` | `i32` | New authoritative Zodiac level |
| `+16` | `i32` | Remaining integer Zodiac energy |
| `+20` | `i32` | Unused (`0`) |

An authoritative full sync follows both success and rejection so the native UI
refreshes its level, balance, and new storage ceiling. Rejections intentionally
omit SID `3`: the shipped client has no SID `3` failure branch and treats every
such response as a successful upgrade, so returning it on failure would animate
a false success. No separate native failure response has been identified; SID
`1` full sync is therefore the safe rejection behavior until a retail failure
packet is captured.

Energy costs and player level gates are server-owned copies of the shipped
`Constellationlevup_Level1..29` and `Player_Level1..29` tables. PostgreSQL uses a
row lock and the live session uses the same gate as periodic online-energy
accrual, preventing double-spend and stale in-memory overwrite.

## Skill-grid activation (SID `100`)

The shipped client requests activation with
`GameAPI:ConsEventRequest(0, 100, index, -1)`, where `index` is the zero-based
grid number `0..15`. The server treats only that index as intent. Ownership,
existing level, and the premium-gold balance are read authoritatively, and the
server derives the exact `UnlockG` cost from the shipped table:

```text
grids  0..3: 0, 2300, 7200, 14400
grids  4..7: 0, 2300, 7200, 14400
grids 8..11: 0, 0, 920, 920
grids 12..15: 0, 0, 920, 920
```

Here “gold” means premium gold: `GameCharacter.Gold`, stored in the legacy
`character_base."Stone"` column. It is not silver (`"Money"`). PostgreSQL
locks the owning character row and atomically deducts premium gold plus
upserts the grid record. JSON uses its serialized store gate. A successful
activation changes level `0` to `1`, retains selected skill `-1`, sends the
24-byte SID `100` success response (`v1=gridIndex`, `v2=v3=0`), refreshes the
wallet, and follows with a full sync.

Invalid, already-active, insufficient-gold, and wrong-owner requests never
receive SID `100`, because the client treats every SID `100` response as
success. Rejected requests receive only authoritative state synchronization.

With PostgreSQL, this one-time `level 0 -> 1` transition now uses a stable
family-19 command identity derived from the authenticated account, character,
grid index, and expected inactive level. A paid success commits the grid,
Gold wallet revision, negative Gold ledger, permanent audit/inbox receipt,
and strict outbox event in one transaction before acknowledgement. Free
activation does not invent a zero-delta wallet revision or ledger entry.
Insufficient Gold is not stored as a terminal result, so the same request can
succeed after a later top-up. Exact reconnect retry returns the permanent
receipt plus the current authoritative projection and suppresses a second
SID `100` animation. See
[`data-architecture-b09-zodiac-grid-activation-20260730.md`](data-architecture-b09-zodiac-grid-activation-20260730.md).

## Skill-grid upgrade (SID `101`)

Native `Origin.exe` sends an ordinary click on an already-active grid as
module `255`, SID `101`, `v1=gridIndex`, `v2=-1`. Module `0` is also accepted
for compatibility, but every cost and requirement remains server-owned. The
server uses the shipped `SkillTrainConfig.lua` arrays:

- `UpdateE[1..49]`: Zodiac-energy cost for level `1 -> 2` through `49 -> 50`;
- `UpdateS[1..49]`: Talent Point cost (`character_base."SkillPoint"`);
- `Starlv[1..49]`: required authoritative Zodiac level.

Level `50` is final. One grid costs `827,921` energy and `726,024` Talent
Points in total from level `1` through `50`. PostgreSQL locks the owning
character row and commits both balances and the grid row in one transaction;
JSON uses its serialized store gate. The live-session gate also serializes the
mutation against Zodiac energy accrual and Zodiac-level changes.

On success the server sends the 24-byte SID `101` response with
`v1=gridIndex`, refreshes the Player Status Talent Point field, and sends a
full sync. Inactive, maximum-level, Zodiac-gated, resource-limited, invalid,
and wrong-owner requests do not receive SID `101`: the native handler blindly
increments the displayed grid level on every such response. A full sync safely
repairs rejected or stale UI state.

SID `102` is a separate native skill-selection mode, not part of activation or
ordinary grid upgrading. It sends module `255`, `v1=gridIndex`, and a selected
client skill-kind ID. Combat effects remain intentionally unwired until those
client IDs (captured examples include `10057` and `20053`) are authoritatively
mapped to server skill templates and the shipped effect/MP rules are covered
as a separate slice.

## Captured accumulation event

A working-server normal-monster kill that awarded `+80` fighter EXP and `+2` Talent EXP also sent SID `7` with `v1=8`, `v2=2`:

```text
180039289F04000000000700080000000200000000000000
```

The server has a matching SID `7` builder and atomic storage mutation, but does not yet grant it automatically. One capture is not enough to distinguish base versus boosted EXP, client-side x100 conversion, Zodiac effect-level scaling, and accumulation caps.

## Continuous-login energy (SID `5`)

The shipped `Constellation.lua` identifies SID `5` as `SID_ENERGY_INCR`. It treats `v1` as the new authoritative integer energy total and displays `v2 / 100` as the gain. The stock script had that notification path commented out; the companion client patch enables its existing `GameAPI:AddPersonalMessage_UTF8(CON_L0_1..(v2 / 100), 6, 1)` call in both locales. This is the native personal-message overlay also used by short EXP and safe-map notices, rather than a new server opcode. Reverse inspection of the native dispatcher confirms SID `5` uses the ordinary 24-byte `SMsg(v1,v2,v3)` form:

| Packet offset | Type | Meaning |
|---:|---|---|
| `+0` | `u16` | Length (`24`) |
| `+2` | `u16` | Opcode (`10297`) |
| `+4` | `u32` | Local player object ID |
| `+8` | `u16` | Module (`0`) |
| `+10` | `u16` | SID (`5`) |
| `+12` | `i32` | New integer energy total |
| `+16` | `i32` | Applied gain in hundredths |
| `+20` | `i32` | Unused (`0`) |

No SID `5` packet appears in the stored working-server captures, including a roughly 24-minute continuous game connection. Therefore the wire shape is client-decoded, not a captured retail response.

Contemporary system guides agree on these timing semantics: energy accrues every five minutes; the first three online hours of each day earn at a higher rate; and being absent longer than one day or recording less than one online hour in the prior day grants one hour of compensation. References: [GodsArena Zodiac guide](https://wiki.godsarena.online/books/godsarena-godswar/page/zodiac), [IGG announcement mirror](https://www.gamerstemple.com/news/5886/godswar-online-adds-zodiac-system), and [Online Station guide](https://www.online-station.net/pc-console-game/117899).

The numeric retail awards have not been recovered. The server consequently exposes clearly named emulator-policy values:

- tick: 300 online seconds;
- boosted window: first 10,800 online seconds per server day;
- `emulatorBoostedEnergyPerTickX100`: `2000` (`20` energy), inferred and unverified;
- `emulatorNormalEnergyPerTickX100`: `1000` (`10` energy), inferred and unverified;
- compensation: twelve boosted ticks (one hour, currently `240` energy);
- persistence flush: every 30 seconds plus the disconnect tail;
- day boundary: the original fixed UTC-8 server clock advertised by the server-time packet.

Only completed five-minute online intervals award energy and emit SID `5`. Sub-tick duration persists across disconnect/reconnect, so a 299-second session followed by one second online completes exactly one tick. Daily online duration, centi-energy remainder, last-online timestamp, and the compensation-day marker are persisted for both JSON and PostgreSQL providers. Compensation never counts as online duration, and storage-cap clipping is reflected in SID `5`'s applied-gain field.

The exact storage ceilings are shipped as `MaxPower1..MaxPower30` in `ConstellationConfig.lua`:

```text
L01 1,000      L06 42,000     L11 150,000    L16 315,000    L21 555,000    L26 845,000
L02 3,000      L07 60,000     L12 180,000    L17 355,000    L22 610,000    L27 905,000
L03 8,000      L08 80,000     L13 210,000    L18 400,000    L23 665,000    L28 965,000
L04 20,000     L09 100,000    L14 245,000    L19 445,000    L24 725,000    L29 1,025,000
L05 28,000     L10 125,000    L15 280,000    L20 500,000    L25 785,000    L30 1,090,000
```

These are storage caps, not `Constellationlevup_Level*` level-up thresholds or `Power_Level*` stone/grid upgrade costs.

The caps govern ordinary online accrual. An explicit administrative test grant
may exceed the current level's cap: full sync exposes that raw nonnegative
balance, level-up spends and carries it forward, and automatic accrual adds
nothing while the balance remains at or above the normal cap. This narrowly
preserves a manually granted test balance without allowing regular play to
generate over-cap energy or silently destroying the balance on the next tick.

To replace the emulator rates with retail values, capture a below-cap character through at least one tick in the first three daily online hours and one tick after three hours, recording SID `5` `v1/v2`. A capture spanning the UTC-8 day boundary plus the next login after an offline day is also needed to validate compensation delivery timing.

## Remaining gameplay SIDs

- `2`: change Zodiac
- `4`: open stone
- `5`: continuous-login energy increment (implemented; numeric rates remain configurable emulator policy)
- `6` / `10`: stone upgrade variants
- `7`: accumulated EXP/Talent EXP notification
- `8`: energy return
- `9`: claim accumulated EXP/Talent EXP
- `11`: synchronize trained skill
- `12`: stone/skill-growth action
- `100`: activate skill grid (implemented)
- `101`: upgrade skill grid (implemented)
- `102`: select the skill assigned to a grid (not implemented)

The unimplemented mutations require server-side requirements, costs, caps, and
atomic persistence. The client tables provide UI requirements but are not
sufficient evidence for every authoritative economy rule.
