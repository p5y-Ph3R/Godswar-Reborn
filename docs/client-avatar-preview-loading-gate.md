# Character-selection avatar loading gate

## Current status

V1 failed live validation and was rolled back:

- failed v1 `Net.dll` SHA-256:
  `2D819908BEE2FA7D8BE4957E18358DEFFB5FD65D01AC26D6F73F29F4C71E2AE0`
- historical v1 Apply backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-150036083`
- historical network-stable pass-through shim SHA-256:
  `528913E66888D5C070C39949D2FC1AE439B8414B15152312D4E093A29D17A6DD`
- historical pass-through Apply backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-151248244`

V2 was installed at `2026-07-24T03:55:57Z`, but its five-second unready
handoff recreated the permanent blank model during fresh account-7 cycle 3.
It is rejected:

- rejected V2 `Net.dll` SHA-256:
  `73E65FBFA3EA9809AF597DA3D25D1E0963B0A4A467549191BAFB4FAE9F2902FD`
- historical V2 Apply/stock-restore backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-155531621`
- historical V2 Apply manifest SHA-256:
  `9A92451A6786EBBCBA65EA27B09A0EFDA0115754CCE73408CA717FC3CE4B8DFC`
- V2 evidence run/result:
  `20260724T040509293Z-4ce08407` / `Fail`

V3 passed its automated gates but failed its cold account-13 live run and is
rejected:

- rejected V3 `Net.dll` SHA-256:
  `17A7219868BAC19BA2BDDD2949FCF70884D4FD9F3EC5799455EF944F40D878D1`
- historical V3 Apply/stock-restore backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-162423590`
- historical V3 Apply manifest SHA-256:
  `BD139E5D461BEF7B209945F21816E04A5E752F7C0447DB0EDAD5909F2E8CC4D2`
- immutable V3 evidence/result:
  `artifacts/network-shim/manual-parity/20260724T043833399Z-2bd75dd7` /
  `Fail`

V4 passed automated tests but failed its final cold smoke before character
selection and is rejected:

- rejected V4 `Origin.exe` SHA-256:
  `E0F5BC951C6E37550F4D9CC1E25BFDCB4F020466ADD854DC2E7EA04E0D22F81C`
- V4 Origin Apply backup:
  `C:\Reborn\backups\origin-avatar-preload-v4-Apply-20260724-213316596-5256fb25`
- rejected V4 `Net.dll` SHA-256:
  `EF531F8CB20A4FCA8D1DBA979FD131ECA002383AE862890435426DF948817597`
- V4 Net Apply/stock-restore backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-213354864`
- V4 Net Apply manifest SHA-256:
  `5E8986F01742F855D2248B899C58590AB57F4B72D1C27A10F25BDEC290CAD04B`
- immutable V4 evidence/result:
  `artifacts/network-shim/manual-parity/20260724T095739213Z-db16daa7` /
  `Fail`
- V4 failure boundary: Origin PID `64928` established TCP to
  `127.1.1.110:7000`, but the server received no `LoginGameServer`;
  CharacterSelection, AfterLogin, and V4 preload never ran
- dump result: no new dump
- Net Revert backup:
  `C:\Reborn\backups\client-network-shim-v1-Revert-20260724-221318157`
- Origin Revert backup:
  `C:\Reborn\backups\origin-avatar-preload-v4-Revert-20260724-221319380-aeb5325a`

The immutable V1 dump record is in
[`client-avatar-preview-loading-gate-incident-20260724.md`](client-avatar-preview-loading-gate-incident-20260724.md).
The V2 blank-model rejection is in
[`client-avatar-preview-loading-gate-v2-incident-20260724.md`](client-avatar-preview-loading-gate-v2-incident-20260724.md).
The V3 timeout and crash are in
[`client-avatar-preview-v3-failure-20260724.md`](client-avatar-preview-v3-failure-20260724.md).
No server, packet-format, database, or character-data change is part of these
loading-gate versions.

The ordered rollback is complete. Current exact predecessor:

- `Origin.exe` SHA-256:
  `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`
- stock `Net.dll` SHA-256:
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`
- `NetLegacy.dll`: absent

## Problem and live baseline

The server sends one valid 188-byte character-preview message, opcode `10002`
(`0x2712`), for the observed one-character selection flow. `NetLegacy.dll` can
expose it before `Origin.exe` populates all six avatar-resource pointers:

```text
0x01576088  0x0157608C  0x01576090
0x0157609C  0x015760A0  0x015760A4
```

The executable guards documented in
[`client-avatar-preview-crash-fix.md`](client-avatar-preview-crash-fix.md)
skip two native builders while a required pointer is absent. That avoids those
two null dereferences but consumes the only preview message, leaving a
clickable character slot with no 3D model.

The stable pass-through shim reproduced this model-only state on 2026-07-24:

- account 7 authenticated and the game connection opened at
  `2026-07-24T03:30:23Z`;
- the server emitted one valid 188-byte opcode-`10002` preview at
  `03:30:23.586601356Z`;
- the TCP game connection remained established for more than 142 seconds;
- the character model stayed blank; and
- the client stayed responsive with no new dump or server/container restart.

This baseline separates the native resource race from malformed server data
and from the separate v1 disconnect incident.

## Historical v1 failure

V1 retained the exact preview pointer but deliberately stopped delegating
native `Process()` while it was held. Account 7 first passed, but the next full
relaunch for account 13 lost its game connection after `14.633832034` seconds,
showed the native server-full state, and produced a new access violation at
`0x005F58BC`.

V1's 30-second timeout disposed the held pointer and called native disconnect,
but transport starvation closed the connection before that deadline.
Disassembly of Origin's caller rules out a `GetMsgNum` drain-loop spin:
a null `PickMsg` exits that per-frame call, and the next frame can call
`Process`. V1 failed because its proxy itself suppressed that delegation.

## Historical V2 contract and rejection

V2 preserves the stock `CMsg` allocation and ownership:

1. Every proxy `Process()` call delegates to `NetLegacy.dll`, including while a
   preview is held.
2. The gate recognizes only the audited 188-byte opcode-`10002`,
   one-character message.
3. If all six resources are ready, the original pointer passes through.
4. Otherwise the gate retains exactly that pointer, returns `nullptr` from
   `PickMsg`, includes the held item in `GetMsgNum`, and prevents later
   messages from overtaking it.
5. Readiness returns the exact pointer to Origin, which then owns and destroys
   it normally.
6. If readiness is still false after five monotonic seconds, the gate returns
   that same pointer as a bounded guarded fallback. It does not dispose the
   message or disconnect the transport.

The five-second fallback was unsafe. On cycle 3, V2 returned the pointer while
readiness was false and the model stayed blank for more than 44 seconds despite
a responsive client and established TCP connection. V2 is rejected.

## Historical V3 contract and rejection

V3 retains the safe V2 scheduling and ownership rules but removes all timeout
and clock behavior:

1. Every proxy `Process()` call delegates to `NetLegacy.dll`.
2. Only the audited 188-byte, one-character opcode-`10002` message is eligible.
3. An unready preview retains its exact pointer and blocks later `PickMsg`
   polling, preserving order.
4. No elapsed duration releases the pointer. `TryRelease` succeeds only when
   all six avatar-resource pointers are ready.
5. Once returned, Origin owns and destroys the original pointer normally.

A held pointer is disposed exactly once only when an explicit lifecycle reset
still owns it: `Connect`, `DisConnect`, `Release`, or proxy destruction.

That contract was insufficient. In the immutable V3 failure run, cold
account 13 received a valid preview but never sent EnterGame. The TCP
connection closed about 14.8 seconds later, the client displayed its native
server-unavailable dialog, and acknowledging it produced dump
`20260724210050.dmp` with SHA-256
`7A5B34B86A2A2E9F8281A1B9F7DDDA9579AAE9AFDC839E4A43D26C7575E993D9`.
The x86 access violation was at `0x005F58BC`, with `ECX`/avatar root
`0x015760A0` null. Native `PickMsg()` returning null exited the LOGIN update
before the missing selection resources could be initialized.

## Historical V4 contract

V4 keeps V3's exact-pointer ownership, queue order, continuous native
`Process()`, readiness-only release, and lifecycle cleanup, then adds a bounded
native lifecycle correction:

1. The shim recognizes only the exact AfterLogin bootstrap record and requests
   native state 2 without overwriting a different pending transition.
2. Immediately after native LOGIN state registration, the Origin patch calls
   the existing initializer at `0x00467280` synchronously on the main thread.
3. The exact opcode-`10002` preview remains retained until all six resource
   roots are non-null.
4. The later `0x005F58BC` path checks all six roots. A missing root skips the
   unsafe avatar calls and schedules a clean state-2 transition.

V4 does not add a server delay or change the legacy packet bytes. Working
captures send AfterLogin and preview nearly back-to-back; timing the server is
not a deterministic substitute for repairing the client lifecycle.

## Completed rollback and pass-through recovery

The mandatory rollback completed in this order:

1. While Origin still has V4 hash `E0F5BC95...D22F81C`, restore Net with
   `client-network-shim-v1-Apply-20260724-213354864`.
2. Verify Net is exact stock `1CC3F9...BCA00C` and `NetLegacy.dll` is absent.
3. Run `PatchClientAvatarPreload.ps1 -Mode Revert`; its recorded Apply backup
   is `origin-avatar-preload-v4-Apply-20260724-213316596-5256fb25`.

The Origin patcher refuses mutation unless step 2 is true. Its writes are
staged, hash-verified, and atomically replace the destination.

The resulting current files are predecessor Origin `753BE49F...9ED79`, stock
Net `1CC3F9AA...BCA00C`, and no `NetLegacy.dll`. The Revert backups are
`client-network-shim-v1-Revert-20260724-221318157` and
`origin-avatar-preload-v4-Revert-20260724-221319380-aeb5325a`.
The pass-through binary remains a separately
preserved recovery candidate at:

```text
C:\Reborn\backups\client-network-shim-v1-Revert-20260724-155518012\Net.dll
```

Its hash is `528913E6...D17A6DD`. Complete the ordered V4 restore first, then
use guarded Apply with that explicit `-ShimPath`; this creates a new backup. The
historical `...151248244` Apply backup records the earlier pass-through
installation and its stock predecessor, but it is not the current V4 rollback
backup and does not itself contain the pass-through candidate.

## Compatibility and verification

The gate enables only when the audited process name, full V4 Origin hash, x86
PE identity, preload/timeout hook bytes, and legacy hash match. Unknown hosts
retain pass-through behavior. Automated tests cover ABI preservation,
AfterLogin recognition and bounded state-2 requests, exact-pointer ordering,
4,096 unready scheduling cycles with continuous `Process`, malformed input,
readiness-only release, lifecycle cleanup, and guarded binary Apply/Revert;
they do not prove native UI behavior.

The final fresh cold launch is sealed `Fail` at
`20260724T095739213Z-db16daa7`. It failed before the gate could execute:
redirected game TCP connected, but no `LoginGameServer` reached the server.
This is not evidence that the preload path itself ran or caused the stall, and
it is not Phase 1 acceptance. Per the user-set stop boundary, the avatar issue
is parked and work advances to Phase 2 without another preload iteration.
