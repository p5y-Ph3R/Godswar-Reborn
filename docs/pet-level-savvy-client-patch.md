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

## Compatible extension

`PatchClientPetLevelSavvyRefresh.ps1` adds an exact-length-gated extension:

| Packet length | Behavior |
|---:|---|
| `20` | Stock handler behavior; savvy is untouched. |
| `44` | Stock prefix plus six little-endian `uint32` basic-savvy totals. |
| Any other length | Stock prefix behavior; the extension is ignored. |

The six appended values occupy offsets `+20`, `+24`, `+28`, `+32`, `+36`, and
`+40`, in native stat-code order `1..6`. Each is fixed point `value * 100`,
matching the full `10237` pet record. The patch copies them into pet-object
offsets `+0x84` through `+0x9B`, the same destination used by the audited
`0x006A6340` full-record copy routine.

The existing 20-byte prefix and success notification remain unchanged. The
current server emits the 44-byte form after its authoritative transaction
commits, and the installed audited client carries this patch.

## Binary guard

The hook is at VA `0x006A195C` / file offset `0x2A195C`. It redirects one
complete 11-byte instruction to reserved executable padding at VA
`0x009C3480` / file offset `0x5C3480`. The cave begins after the independently
reserved avatar timeout/retry cave.

The patch:

- accepts only the two audited predecessor SHA-256 states;
- pins the x86 PE image, opcode registration, handler prefix, continuation,
  and full-snapshot savvy destination;
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

## Verification

Status is read-only:

```powershell
.\tools\PatchClientPetLevelSavvyRefresh.ps1 -Mode Status
```

The disposable-copy test covers both supported predecessor chains, exact
hashes, PE mappings, branch targets, packet-length gate, six-dword copy,
register/flag restoration, allowlisted mutations, backups, idempotency,
foreign/partial-state refusal, and exact apply/revert round trips:

```powershell
.\tools\TestClientPetLevelSavvyRefreshPatch.ps1
```

The patcher never changes a client unless `-Mode Apply` is requested. The
server-side 44-byte response is covered separately by protocol and
PostgreSQL integration checks.
