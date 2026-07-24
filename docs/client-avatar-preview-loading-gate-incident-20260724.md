# Avatar-preview loading-gate v1 incident - 2026-07-24

## Outcome

Loading-gate build
`2D819908BEE2FA7D8BE4957E18358DEFFB5FD65D01AC26D6F73F29F4C71E2AE0`
failed live account-switch validation and was rejected. Account 7 displayed its
model and entered the world. On the following full relaunch with account 13,
the selection flow remained waiting, displayed the native server-full message,
and later produced a new access-violation dump.

The client was immediately restored to the prior network-stable pass-through
shim:

- shim SHA-256:
  `528913E66888D5C070C39949D2FC1AE439B8414B15152312D4E093A29D17A6DD`
- current stable Apply backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-151248244`

The immutable failed evidence record is:

```text
C:\Reborn\artifacts\network-shim\manual-parity\
20260724T030417842Z-94e2c5f4
```

Its completion state is deliberately `Fail`; it must not be reused for a later
candidate.

## Preserved evidence

Server timestamps:

```text
2026-07-24T03:10:40.394844390Z  CharacterPreview opcode 10002 sent
2026-07-24T03:10:55.028676424Z  game connection closed
```

The interval was `14.633832034` seconds. The server container remained running
with restart count zero.

Client evidence:

- dump:
  `C:\Godswar Origin\Dump\20260724151147.dmp`
- dump size: `279533` bytes
- dump SHA-256:
  `38D558C9F4BDDC74BF5F41FB1338ACC8F8FE0CD5D2A064EE7D6AD27B9512A7F7`
- post-incident `Dump\Error.log` size: `95360` bytes
- post-incident `Dump\Error.log` SHA-256:
  `28449E24477B92B7B8A14095752CF64564D19863B8772E937E76D7B62E60A41C`
- exception: x86 `C0000005` read access violation
- fault VA: `0x005F58BC` / `Origin.exe+0x001F48BC`
- registers at the fault: `ECX=0`, `EIP=0x005F58BC`

The faulting instructions are:

```text
005F58B6  mov ecx, dword ptr [015760A0h]
005F58BC  mov eax, dword ptr [ecx]
```

The stack return at `0x001AF604` is `0x00597BF2`, proving the caller was the
state-dispatch branch at `0x00597BED -> 0x005F5810`. Jump-table states 33 and
35 reach that branch. This is a state-transition routine, not the opcode-10002
preview builder. It calls native network disconnect at
`0x005F5840..0x005F5855` before dereferencing the null avatar resource.

## Root cause in loading-gate v1

V1 returned early from the proxy's native `Process()` method while it held the
preview message. That starved the legacy transport and allowed the connection
to close after approximately 14.6 seconds. Its 30-second timeout could not run
first. The resulting server-full/disconnect state entered the separate,
unguarded `0x005F5810` path and exposed the null `0x015760A0` dereference.

The existing executable guards cover `0x005F060E` and `0x005F4ADD`; they do not
cover `0x005F58BC`. Documentation must not claim that every native
avatar-resource path is guarded.

Origin disassembly rules out the suspected message-drain spin. At `0x004DE842`
Origin calls `PickMsg` and saves the result; although it calls `GetMsgNum` at
`0x004DE851`, `test ebx,ebx` at `0x004DE85C` sends a null result to the
enclosing per-frame return. The next frame reaches `Process`. The failure was
V1's explicit suppression of native `Process`, not `GetMsgNum` scheduling.

## Stable-shim model-only baseline

The rollback shim is network-stable, but it does not solve the original
resource race. A read-only live capture for account 7 recorded:

```text
2026-07-24T03:30:23.056371626Z  game connection opened
2026-07-24T03:30:23.586601356Z  valid 188-byte CharacterPreview sent
```

The Origin process stayed responsive and its TCP game socket remained
established for more than 142 seconds. The 3D model remained blank, but there
was no new dump, game-close log, server exception, or container restart. The
installed `Net.dll` hash was the rollback hash `528913E6...D17A6DD`.

This is distinct from the V1 incident: a blank model can occur while the
connection remains healthy because the guarded native builder consumed the
only preview before its resources were ready.

## Installed V2 follow-up

The corrected V2 build has SHA-256:

```text
73E65FBFA3EA9809AF597DA3D25D1E0963B0A4A467549191BAFB4FAE9F2902FD
```

It reached `InstalledExact` at `2026-07-24T03:55:57Z`; automated and
disposable installer gates pass. Its current Apply/stock-restore backup is
`C:\Reborn\backups\client-network-shim-v1-Apply-20260724-155531621`
(manifest SHA-256
`9A92451A6786EBBCBA65EA27B09A0EFDA0115754CCE73408CA717FC3CE4B8DFC`).
Live account-switch acceptance is still pending. V2:

1. always delegates native `Process()` while retaining the preview;
2. keeps proxy `PickMsg()` from polling past the retained pointer, preserving
   message order;
3. uses a five-second observed-local compatibility deadline, below the failed
   14.6-second window;
4. returns the exact original pointer on readiness or at that deadline rather
   than disposing it or calling native disconnect; and
5. performs exact-once virtual-destructor cleanup only if an explicit
   `Connect`, `DisConnect`, `Release`, or destruction reset still owns a held
   preview.

The five-second value is a conservative local compatibility bound, not a
universal capacity or timing guarantee. If resources are still absent, the
guarded fallback can leave the model blank; it must not be described as
guaranteed automatic loading. A separate third executable guard for
`0x005F5810` remains an independently reviewed hardening candidate and is not
silently folded into the networking shim.
