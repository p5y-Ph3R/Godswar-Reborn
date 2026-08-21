# Client Speed and Penetration statistics

## Outcome

The character-stat dialog has one compact two-column row beneath the stock
Healing and Absorb rows:

| Column | Label | Value source |
| --- | --- | --- |
| Left | `Speed` / `速度` | Current effective movement Speed |
| Right | `Pen.` / `穿透` | Physical for classes 0/1; Magical for classes 2/3 |

Both values are server-authoritative percentages. Speed trims unnecessary
zeroes: `10000 bp` renders as `100%`, `333 bp` as `3.33%`, and `10 bp` as
`0.1%`. Penetration always renders exactly two decimal places: `350 bp` is
`3.50%`, `10 bp` is `0.10%`, and the cap is `80.00%`. An unavailable class
lookup renders Penetration as `--`; it never reinterprets or combines the two
channels.

The Penetration tooltip identifies the active class channel and explains the
mechanic: matching damage ignores that percentage of the target's matching
Defense. Effective Physical and Magical Penetration are each capped at 80%.

The window remains non-modal. It grows from `100,100,363,626` to
`100,100,454,652`: one native 26-pixel row taller and 91 pixels wider. Its
close button moves with the right edge, from `196,13,233,50` to
`287,13,324,50`.

| Entry | Label rectangle | Value rectangle |
| --- | --- | --- |
| Left combat column | `x=24..80` | `x=84..160` |
| Right combat column | `x=173..253` | `x=257..333` |
| Speed | `24,517,80,533` | `84,517,160,533` |
| Penetration | `173,517,253,533` | `257,517,333,533` |

The top backgrounds and character-name field end at X 334, preserving the
stock 20-pixel right frame inset. The combat backgrounds are
`19,330,166,536` and `168,330,334,536`. Every combat value
field is 76 pixels wide, enough for the native dialog's long integer samples.
A dedicated, patch-owned
`RebornPersonalInfoStatsUpdater` text control occupies the harmless 1x1
rectangle `1,1,2,2` and owns the `OnUpdate` callback. Neither native-populated
value control is reused as an update driver.

## Existing Physical and Magical Defense rows

The widened layout preserves the stock native-fed Defense controls; it does
not add duplicate fields or extend SID 200:

| Statistic | Label/value controls | Native ID pair | Status wire field |
| --- | --- | --- | --- |
| Physical Defense | `Defend` / `DefendText` | `281114` / `281014` | opcode 10166 offset 164 |
| Magical Defense | `MagicDefend` / `MagicDefendText` | `281116` / `281016` | opcode 10166 offset 172 |

The client resolves these controls by element name and formats their existing
game-data values as integers. The patch changes only their rectangles from
40-pixel value fields to 76-pixel value fields. Cooled Holy Spirit effects do
not change these raw rows; their typed reduction, flat absorption, critical
mitigation, and rebound mechanics apply later in incoming-damage resolution.

## Pull-only SID 200 protocol

The UI does not extend or overload opcode 10166. While the panel is visible,
its Lua sends the canonical capability request:

```text
GameAPI:ConsEventRequest(200,200,1,0)
1800392800000000C800C800010000000000000000000000
```

The stock builder emits PlayerId zero. The server replies through the existing
Constellation `SMsg(sid,v1,v2,v3)` path:

| Field | Meaning | Client validation |
| --- | --- | --- |
| `sid` | `200` | Dedicated early branch |
| `v1` | Effective Speed, basis points | Clamped to `1000..100000` |
| `v2` | Effective Physical Penetration, basis points | Clamped to `0..8000` |
| `v3` | Effective Magical Penetration, basis points | Clamped to `0..8000` |

Non-numeric payload values are not rendered; their corresponding value stays
`--`. Numeric values outside the contract are clamped to the documented
range.

Receipt of SID 200 is the capability ACK. Before ACK, the visible panel sends
at most three probes, approximately one second apart, so an old server is not
polled forever. After ACK it polls approximately once per second while visible
to keep Speed and Penetration current as gear, buffs, mounts, and combat state
change.

The PersonalInfo root registers `EUSER_EVENT_FIRSTENTERGAME`. A new session or
window show clears both values to `--`, resets the capability state, and starts
a fresh bounded handshake. This prevents a Lua ACK from an old connection from
suppressing discovery after reconnect.

## Canonical `SMsg` integration

The installer patches each locale's existing `Constellation.lua`; it never
defines or wraps a second global `SMsg`. The SID 200 branch is the first code in
the canonical function, before any Zodiac window lookup. It caches all three
validated values, marks the ACK, calls `RebornPersonalInfoRenderStats` only if
that function is already defined, and returns.

This ordering allows SID 200 to arrive before the PersonalInfo helper is
loaded without touching `ConstellationWin`, `SkillViewUI`, or other optional UI
modules. It also preserves every stock SID branch.

Stock `Constellation.lua` declares `local type=nil` before `SMsg`. The injected
validator therefore captures `_G.type` under a patch-owned name and never calls
the shadowed bare `type` symbol.

The owned `PersonalInfoSpeedStats.lua` contains rendering, formatting,
tooltips, session reset, bounded request scheduling, and class selection. It
does not contain an `SMsg` definition.

## NPC-interaction safety

The final patch restores the audited stock bytes at PersonalInfo refresh hook
VA `0x005B5B97` and zeros only the patch-owned 128-byte cave at
`0x009C3F20`. No character-stat executable trampoline remains.

This is required because the first opcode-10166 copy maps wire offset 60 into
game data `+0x290`; byte `+0x292` is part of the native actor-interaction
identity gate. Writing a Riding Speed float there caused client-side target
rejection before NPC interaction opcode 10067 could be sent.

Other tempting opcode-10166 fields are stock-owned as well:

- wire offsets 204/208 map to the stock healing pair, including `CureText`;
- wire offsets 212/216 map to active stock integer properties and are nonzero
  in retained captures;
- none is a safe custom Penetration carrier.

SID 200 therefore avoids both the interaction regression and corruption of
unrelated native stats.

## Shared executable cave

The final character-stat owner range is empty, while neighboring owners remain
independent:

| Owner | File range | VA range | Bytes |
| --- | --- | --- | ---: |
| QuestView frame guard | `0x5C3F00-0x5C3F1F` | `0x009C3F00-0x009C3F1F` | 32 |
| Character stats, final empty state | `0x5C3F20-0x5C3F9F` | `0x009C3F20-0x009C3F9F` | 128 |
| Fashion Show auto-check | `0x5C3FA0-0x5C3FFF` | `0x009C3FA0-0x009C3FFF` | 96 |

The installer validates the QuestView owner but mutates only the five-byte
legacy PersonalInfo hook and the character-stat owner's 128-byte range. Both
QuestView installation orders and independent reverts are fixture-tested. The
published Fashion hook and 96-byte cave are also pinned by fixtures and remain
byte-identical through legacy migration, Apply, and Revert.

## Installer states and migration

Status detection is fail-closed and recognizes these complete states:

- `Original`;
- the exact installed `LegacyPartial` state (legacy binary hook, stock XML,
  no owned helper);
- legacy `PatchedV1`, `PatchedV2`, and `PatchedV3` layouts;
- compact `PatchedSid200V1`, used by an earlier final build;
- `PatchedSid200FrameV1`, the deployed 440-right-edge predecessor;
- final `PatchedSid200`.

Each layout must also parse as well-formed, DTD-free XML. Validation compares
the live DOM node names, parents, cardinalities, and callback multiset with the
canonical text, so commented controls, case-variant owned tags, malformed
closing tags, and duplicate callback references cannot masquerade as a valid
state. The patch-owned Lua helper must match canonical raw BOM-less UTF-8 bytes;
UTF-8 BOM and UTF-16 variants are rejected.

Apply migrates every recognized predecessor, including `PatchedSid200V1` and
`PatchedSid200FrameV1`, to the widened `PatchedSid200`. Revert removes
only owned XML attributes/controls, the exact owned helper, and the exact
Constellation marker blocks, then restores the stock binary state. Unrelated
Constellation content and unrelated root attributes are preserved. Unknown or
mixed binary/XML/Lua states are rejected before writes.

Every actual mutation creates SHA-256-verified backups. Text and executable
targets are regenerated from those verified snapshots, then revalidated
immediately before atomic replacement so compatible concurrent owner edits are
preserved. The rollback journal is populated before each write, restores every
changed target atomically, and verifies the complete pre-state byte for byte.
An inaccessible or newly started `Origin.exe` process fails closed.

Apply and Revert preserve each existing XML and Constellation file's UTF-8 BOM
policy. The stock PersonalInfo XML files are BOM-less; the stock Constellation
files carry a BOM. The patch-owned `PersonalInfoSpeedStats.lua` helper is
BOM-less.

## Commands

```powershell
# Read-only state and executable-safety report
.\tools\PatchClientCharacterSpeedStats.ps1 -Mode Status

# Install after Origin.exe is closed
.\tools\PatchClientCharacterSpeedStats.ps1 -Mode Apply

# Remove this patch without changing QuestView
.\tools\PatchClientCharacterSpeedStats.ps1 -Mode Revert

# Isolated migration, ownership, corruption, and install-order coverage
.\tools\TestClientCharacterSpeedStatsPatch.ps1
```

The fixture suite covers byte-exact XML and Constellation round trips, BOM and
CRLF preservation, the installed partial predecessor, all three legacy
layouts, QuestView and Fashion ownership, idempotence, localized content,
bounded probing, class-channel selection, reconnect reset, shadowed-Lua-
builtin safety, exact XML cardinality/casing, compatible concurrent edits,
injected mid-transaction rollback, exact wide-row geometry, preserved native
Defense IDs, fixed two-decimal Penetration examples, compact-final migration,
and unknown binary/XML/Lua rejection.
