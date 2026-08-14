# Pet Level Savvy Client Refresh

## Purpose

The stock client handles S2C opcode `10286` (`0x282E`) at native address
`0x006A18F0`. Its verified 20-byte packet updates only:

- pet level from packet offset `+8` to pet-object offset `+0x40`;
- remaining EXP from `+12` to `+0x78`; and
- next-level cost from `+16` to `+0x7C`.

It does not refresh the six basic-savvy values. Sending the full owned-pet
bootstrap (`10237`) after each click is unsafe because that handler clears and
rebuilds the pet collection, selected-pet state, and related UI pointers.

The two persistent display vectors have distinct semantics. **Basic Savvy** is
the immutable hatch allocation plus pet-to-pet Merge gains. **Cumulative Added
Value** is `(base_growth_rate + growth_acceleration) * current pet level`.
Raw Growth Rate is a durable input to Added; it is not the second pet-bean
vector.

## Compatible extensions

`PatchClientPetLevelSavvyRefresh.ps1` adds an exact-length-gated extension:

| Packet length | Behavior |
|---:|---|
| `20` | Stock handler behavior; savvy is untouched. |
| `44` | Stock prefix plus six little-endian `uint32` basic-savvy totals. |
| Any other length | Stock prefix behavior; the extension is ignored. |

The six appended values occupy offsets `+20`, `+24`, `+28`, `+32`, `+36`, and
`+40`, in native stat-code order `1..6`. Each is fixed point `value * 100`.
The patch copies them into the client object's Basic Savvy destinations at
`+0x84` through `+0x9B`, the same destinations used by the audited
`0x006A6340` full-record copy routine. This 44-byte form was an intermediate
compatibility step and does not refresh the separate cumulative Added fields.

The existing 20-byte prefix and success notification remain unchanged. The
44-byte form is retained as patch history; the current contract is the 68-byte
form below.

The Phoenix Feather flow proved that refreshing only Basic Savvy is
insufficient. A committed reroll changes the base rate and therefore the
current cumulative Added Value, while the full `10237` bootstrap remains unsafe
for an actively carried pet. The follow-up
`PatchClientPetSavvyGrowthRefresh.ps1` upgrades the installed model refresh
and adds a guarded Pet Detail redraw:

| Packet length | Current behavior |
|---:|---|
| `20` | Stock level/EXP prefix; Basic and Added are untouched. |
| `44` | Legacy-compatible prefix only; no extension read. |
| `68` | Prefix plus six Basic Savvy and six cumulative Added Values. |
| Any other length | Prefix only; the extension is ignored. |

The Basic Savvy vector remains at packet offsets `+20..+40` and is copied to
pet-object offsets `+0x84..+0x9B`. The cumulative Added vector occupies packet
offsets `+44..+64` and is copied to `+0x9C..+0xB3`, matching the two contiguous
vectors populated by the audited full-record handler. All twelve values are
little-endian `uint32` fixed point (`value * 100`). This narrow refresh does
not rebuild the pet collection, recall the companion, or alter presence.

## Native field evidence

The stock `10237` record and patched `10286` destinations agree:

| Meaning | `10237` record | Pet bean | Extended `10286` |
|---|---:|---:|---:|
| Basic Savvy, six values | `+0x6C..+0x83` | `+0x84..+0x9B` | `+20..+40` |
| Cumulative Added Value, six values | `+0x84..+0x9B` | `+0x9C..+0xB3` | `+44..+64` |

Origin routines `0x006A6434..0x006A6456` and
`0x006A645B..0x006A646D` copy those `10237` vectors into the two bean ranges.
Pet Detail XML IDs `832003/7/11/15/19/23` are the Basic rows and
`832004/8/12/16/20/24` are the Added rows. Renderers `0x005BAC50`,
`0x005BC350`, and `0x005BC920` divide each fixed-point value by 100 and render
it directly. Derived-stat routine `0x006A0790` likewise divides and sums Basic
plus Added; it performs no level multiplication.

The Phoenix success page `130` is the separate UI that shows the six proposed
effective rates (`nature base + count-derived Rebirth modifier`).
Original-server `10237` capture
`captures/working-multiplayer-20260514-193356.log` instead contains cumulative,
level-scaled Added values. For example, its level-107 pet carries Added values
`91.16, 106.00, 66.78, 62.54, 98.58, 99.64`; dividing by 107 produces plausible
per-level rates. Sending raw Growth Rate in the second `10237` or `10286`
vector would therefore understate both the displayed Added value and the
pet-derived stats.

Updating the pet bean alone does not invalidate controls that are already
visible. After the copy cave restores its state, the final branch now enters a
separate 58-byte stub at VA `0x009C3658` / file offset `0x5C3658`. The stub:

- preserves flags plus `EAX`, `ECX`, and `EDX`;
- null-checks the audited Pet UI controller at `[0x015AD084]`;
- calls the stock `0x005C4E60` wrapper, which redraws only when Pet Detail is
  open and visible; and
- restores all saved state before returning to `0x006A1967`.

The stock wrapper resolves the currently selected pet from the client pet
manager. Therefore a selected secondary pet is redrawn from its own already
updated bean. A hidden dialog safely does nothing and will read the new values
when opened later.

The Pet Merge window has a separate cache. Placing a primary or deputy pet
stores their IDs in the Merge controller and computes all six preview rows,
but stock opcode `10286` never asks that controller to recompute. As a result,
a Fairy's Feather reset correctly changed the deputy pet bean while an already
open Merge window continued to display its old values.

The current redraw stub also null-checks the audited Pet Merge controller at
`[0x015AD098]` and its initialized `BondWin` pointer at controller offset
`+4`. It then calls the stock full Merge redraw at `0x0056DA60` even when an
NPC result modal temporarily marks the underlying Merge window hidden. That
timing matters: Fairy's Feather opcode `10286` arrives while page `120` can be
on top, and the earlier visibility gate permanently skipped the recompute.
The stock routine is safe for an initialized hidden window, resolves the
primary and deputy again from their stored IDs, and recomputes all six preview
rows from the updated pet beans. It neither changes window visibility nor
rebuilds the owned-pet collection or carry/summon state; opcode `10237`
remains deliberately absent from this flow.

## Binary guard

The hook is at VA `0x006A195C` / file offset `0x2A195C`. It redirects one
complete 11-byte instruction to reserved executable padding at VA
`0x009C3480` / file offset `0x5C3480`. The cave begins after the independently
reserved avatar timeout/retry cave.

The patch:

- accepts only the three audited predecessor SHA-256 states and the exact
  final state;
- pins the x86 PE image, opcode registration, handler prefix, continuation,
  full-snapshot savvy destination, Pet Detail wrapper, and Pet Merge redraw;
- validates all relative branches and exact cave semantics before writing;
- changes only the allowlisted hook and cave ranges;
- stages and verifies the complete output hash before atomic replacement;
- creates and verifies a backup; and
- restores the predecessor automatically if installation fails.

Supported transitions:

| Predecessor | Patched |
|---|---|
| `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79` | `7FB43C8D6BBA42CE533EE4CB78075CA88D3D6C11F2F79224C56A8A4F50BA07F9` |
| `E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C` | `2BD6B3DD6FA9F608D0580264F1E548309F2C4F469E8CB69190CFE19083C8E0F7` |

The second predecessor includes the avatar timeout/retry guard. Apply that
guard before this patch if both are wanted; its older standalone patcher does
not recognize the new combined hash.

The current combined client has this guarded two-byte 68-byte upgrade:

| Current 44-byte state | Current 68-byte state |
|---|---|
| `9354BDB00376E16F5C2D1E682637790D90C3930B8F3655456F8F49F3314C6728` | `31B4CE0E0445958C7814BCD2572381F9115DE194E0E13CB3ED7502F02C9FB9B2` |

At file offsets `0x5C3484` and `0x5C3494`, the model refresh changes the exact
packet length `44` to `68` and the copy count `6` to `12`. The redraw revision
retargets only the copy cave's final branch and occupies a separately pinned
72-byte zero reserve; it deliberately does not extend into native code at
`0x5C34B0`. The patcher refuses a running client, verifies its backup and
stage, and hash-verifies rollback.

| 68-byte model-only state | 68-byte model + Pet Detail redraw |
|---|---|
| `31B4CE0E0445958C7814BCD2572381F9115DE194E0E13CB3ED7502F02C9FB9B2` | `C642C3F9F4F3458BC4DBAD126E06C1661C7F1C418FB63BD037543CA1892D5656` |

The first Merge live-refresh revision accepted that Pet Detail-only state as a
pinned predecessor:

| Pet Detail-only predecessor | Pet Detail + visible-only Pet Merge redraw |
|---|---|
| `C642C3F9F4F3458BC4DBAD126E06C1661C7F1C418FB63BD037543CA1892D5656` | `7B837397F5387186001B7CB155FBADD2B3AA2CA425B7568A21F9C66EDA90A8DA` |

The hidden-window correction is a guarded two-byte successor: it neutralizes
only the conditional branch after the visibility read, retaining every null
guard, call target, register save, and branch layout.

| Visible-only predecessor | Current hidden-safe Merge redraw |
|---|---|
| `7B837397F5387186001B7CB155FBADD2B3AA2CA425B7568A21F9C66EDA90A8DA` | `39CC2ECEF6F7428A5870AABB1F16567BC31B9AC671CC5189DD9F790D8FBFF89B` |

Applying from any audited predecessor converges to the same current hash.
Reverting removes the redraw but deliberately retains the safe 68-byte model
refresh required by the current server.

## Verification

Status is read-only:

```powershell
.\tools\PatchClientPetSavvyGrowthRefresh.ps1 -Mode Status
```

The disposable-copy test covers all supported predecessor chains, exact
hashes, PE mappings, branch targets, packet-length gate, twelve-dword copy,
the null gates for both UI controllers, hidden Merge recompute, register/flag restoration,
allowlisted mutations, backups, idempotency, foreign/partial-state refusal,
and exact apply/revert round trips:

```powershell
.\tools\TestClientPetLevelSavvyRefreshPatch.ps1
.\tools\TestClientPetSavvyGrowthRefreshPatch.ps1
```

The patcher never changes a client unless `-Mode Apply` is requested. The
server-side 68-byte response is covered separately by protocol and
PostgreSQL integration checks.
