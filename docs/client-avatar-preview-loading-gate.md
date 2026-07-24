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

V3 is now `InstalledExact`. Its automated gates pass; controlled live
validation is pending:

- installed V3 `Net.dll` SHA-256:
  `17A7219868BAC19BA2BDDD2949FCF70884D4FD9F3EC5799455EF944F40D878D1`
- current V3 Apply/stock-restore backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-162423590`
- Apply manifest SHA-256:
  `BD139E5D461BEF7B209945F21816E04A5E752F7C0447DB0EDAD5909F2E8CC4D2`
- acceptance state: pending fresh account-switch parity, soak, rollback, and
  reapply evidence

The immutable V1 dump record is in
[`client-avatar-preview-loading-gate-incident-20260724.md`](client-avatar-preview-loading-gate-incident-20260724.md).
The V2 blank-model rejection is in
[`client-avatar-preview-loading-gate-v2-incident-20260724.md`](client-avatar-preview-loading-gate-v2-incident-20260724.md).
No server, packet-format, database, or character-data change is part of these
loading-gate versions.

Supported host binaries remain:

- `Origin.exe` SHA-256:
  `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`
- `NetLegacy.dll` SHA-256:
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`

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

## Installed V3 contract

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

## Rollback and pass-through recovery

The current V3 Apply backup is distinct from the historical V1, V2, and
pass-through Apply backups. Restoring V3 with
`client-network-shim-v1-Apply-20260724-162423590` returns the client to exact
stock `Net.dll` hash `1CC3F9...BCA00C` and removes the installed legacy copy.

If V3 fails live acceptance, the pass-through binary remains a separately
preserved recovery candidate at:

```text
C:\Reborn\backups\client-network-shim-v1-Revert-20260724-155518012\Net.dll
```

Its hash is `528913E6...D17A6DD`. Restore V3 to stock first, then use guarded
Apply with that explicit `-ShimPath`; this creates a new Apply backup. The
historical `...151248244` Apply backup records the earlier pass-through
installation and its stock predecessor, but it is not the current V3 rollback
backup and does not itself contain the pass-through candidate.

## Compatibility and verification

The gate enables only when the audited process name, full Origin hash, x86 PE
identity, and installed avatar-guard hook bytes match. Unknown hosts retain
pass-through behavior. Automated tests cover ABI preservation, exact-pointer
ordering, 4,096 unready scheduling cycles with continuous `Process`, malformed
input, readiness-only release, and explicit lifecycle cleanup; they do not
prove native UI behavior.

V3 remains pending until fresh evidence confirms:

1. alternating account 7/account 13 full relaunch and same-process cycles;
2. a delayed preview remains connected beyond the old 14.6-second failure;
3. an unready preview stays in a responsive loading state without being handed
   to the guarded builder;
4. the model appears automatically, without another click, when readiness
   occurs;
5. world entry and later packet ordering remain normal;
6. no server-full state, new dump, or `0x005F58BC` recurrence; and
7. stock restore, V3 reapply, and final soak use a fresh evidence run.
