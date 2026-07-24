# Character-selection avatar loading gate

## Current status

The first loading-gate build failed live validation and was rolled back:

- failed v1 `Net.dll` SHA-256:
  `2D819908BEE2FA7D8BE4957E18358DEFFB5FD65D01AC26D6F73F29F4C71E2AE0`
- historical v1 Apply backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-150036083`
- installed stable pass-through shim SHA-256:
  `528913E66888D5C070C39949D2FC1AE439B8414B15152312D4E093A29D17A6DD`
- stable rollback Apply backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-151248244`

The current v2 candidate is not installed and has not passed live validation:

- candidate SHA-256:
  `73E65FBFA3EA9809AF597DA3D25D1E0963B0A4A467549191BAFB4FAE9F2902FD`
- state: repository candidate; pending install, account-switch parity, soak,
  rollback, and reapply evidence

The immutable v1 failure record and dump evidence are in
[`client-avatar-preview-loading-gate-incident-20260724.md`](client-avatar-preview-loading-gate-incident-20260724.md).
No server, packet-format, database, or character-data change is part of either
loading-gate version.

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

## V2 candidate contract

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

The five-second fallback prevents an unbounded hold and stays below the
observed v1 disconnect interval. It is a local compatibility choice, not a
readiness guarantee: if the resources never become ready, the executable
guards may safely skip the returned preview and the model can remain blank.
Documentation and tests must not claim that v2 always makes the model appear.

A held pointer is disposed exactly once only when an explicit lifecycle reset
still owns it: `Connect`, `DisConnect`, `Release`, or proxy destruction.
Timeout alone is not cleanup.

## Compatibility and verification

The gate enables only when the audited process name, full Origin hash, x86 PE
identity, and installed avatar-guard hook bytes match. Unknown hosts retain
pass-through behavior. Automated tests cover ABI preservation, exact-pointer
ordering, continuous `Process`, malformed input, the five-second handoff, and
explicit lifecycle cleanup; they do not prove native UI behavior.

V2 remains pending until fresh evidence confirms:

1. alternating account 7/account 13 full relaunch and same-process cycles;
2. a delayed preview remains connected beyond the old 14.6-second failure;
3. the model appears without another click when readiness occurs before the
   fallback;
4. the known five-second blank-model fallback is reported as a failure of the
   desired loading result, not hidden as success;
5. world entry and later packet ordering remain normal;
6. no server-full state, new dump, or `0x005F58BC` recurrence; and
7. stock restore, v2 reapply, and final soak use a fresh evidence run.
