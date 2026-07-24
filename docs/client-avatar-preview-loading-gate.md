# Character-selection avatar loading gate

## Result

The x86 `Net.dll` compatibility shim contains a narrowly scoped loading gate
for the intermittent character-selection race. When the one-character preview
packet arrives before the six native avatar resources are ready, the client
remains in its normal responsive loading state. The exact packet is delivered
automatically as soon as those resources are available; the player does not
need to relaunch or select the slot again.

Installed on 2026-07-24 with:

- Apply backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-150036083`
- Pre-upgrade shim recovery backup:
  `C:\Reborn\backups\client-network-shim-v1-Revert-20260724-150029673`

This complements the guarded `Origin.exe` patch documented in
[`client-avatar-preview-crash-fix.md`](client-avatar-preview-crash-fix.md).
The executable guards prevent null-resource crashes. The shim gate prevents
the winning packet from being discarded by waiting and retrying it.

Supported binaries:

- `Origin.exe` SHA-256:
  `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`
- `NetLegacy.dll` SHA-256:
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`
- loading-gate `Net.dll` SHA-256:
  `2D819908BEE2FA7D8BE4957E18358DEFFB5FD65D01AC26D6F73F29F4C71E2AE0`

No server, packet format, database, or character data change is part of this
fix.

## Root cause

The server sends one valid 188-byte character-preview message, opcode `10002`
(`0x2712`), for the observed one-character selection flow. The stock
`NetLegacy.dll` can expose that message before `Origin.exe` has populated all
six male/female avatar-resource pointers:

```text
0x01576088  0x0157608C  0x01576090
0x0157609C  0x015760A0  0x015760A4
```

The executable guards safely skip the native builders while a pointer is
absent. Skipping prevents a crash but consumes the only preview message, which
explains the clickable slot and successful world entry with no 3D model.
Stock and shim timing observations both reproduced the model-only symptom, so
this is a native resource/packet race rather than malformed server data.

## Ownership and loading behavior

The implementation preserves the stock `CMsg` object rather than copying or
reconstructing proprietary bytes:

1. `NetLegacy.dll::PickMsg` removes and returns the first `CMsg`.
2. The shim recognizes only the audited 188-byte opcode-`10002`,
   one-character message.
3. If every resource pointer is ready, the pointer passes through immediately.
4. Otherwise the shim retains that one pointer, returns `nullptr` from
   `PickMsg`, includes the retained item in `GetMsgNum`, and does not poll past
   it. The render/UI loop continues normally, so the selection screen stays
   responsive and loading remains visible.
5. On readiness, the next `PickMsg` returns the same pointer. `Origin.exe`
   handles it and invokes its ordinary scalar-deleting destructor.

Later messages cannot overtake the held preview. Only one message can be held.
All other messages and all nine legacy virtual methods retain their original
ABI and behavior.

On disconnect, reconnect, or release, the shim disposes an undelivered pointer
through its real `CMsg` virtual scalar-deleting destructor exactly once. A
30-second monotonic fail-safe disposes it and enters the stock disconnected
path rather than leaving an unbounded wait or retained allocation. Readiness
wins if it becomes true at the timeout boundary.

## Compatibility guard

The loading gate enables only when all of the following match the audited
client:

- process filename and full `Origin.exe` SHA-256;
- x86 PE timestamp, image base, image size, and entry point; and
- all three installed avatar-lifecycle/guard hook byte sequences.

An unknown or unpatched executable receives the ordinary pass-through shim
behavior. The installer independently pins the supported executable and
legacy-DLL hashes and refuses unknown state.

## Automated verification

From `C:\Reborn`:

```powershell
.\tools\TestClientNetworkShim.ps1
.\tools\TestClientNetworkShimInstaller.ps1
.\tools\TestClientNetworkShimWindowsEvidence.ps1
```

The native suite covers:

- exact x86 ABI/export/runtime hardening;
- immediate and delayed preview delivery;
- exact-pointer and packet-order preservation;
- malformed, wrong-opcode, wrong-length, wrong-count, and inaccessible input;
- saturated message counts;
- reconnect, disconnect, release, and timeout cleanup;
- a real MSVC scalar-deleting destructor; and
- two clean release builds with identical SHA-256.

## Interactive acceptance

Repeat the race-prone flow several times:

1. Launch the client and alternate accounts 7 and 13, including same-process
   return-to-login and full relaunch cycles.
2. If preview resources are late, confirm the selection screen remains
   responsive and visibly loading rather than closing or showing a permanent
   blank model.
3. Confirm the 3D character appears automatically without another login or
   slot click.
4. Confirm the slot remains selectable and world entry still succeeds.
5. Confirm later packets and normal gameplay are not delayed after the model
   appears.
6. Confirm no new dump or avatar access-violation entry is produced.

If resources are already ready, no visible delay is expected. The current
candidate has passed static and automated validation; the interactive sequence
is the remaining proof of the native UI behavior.
