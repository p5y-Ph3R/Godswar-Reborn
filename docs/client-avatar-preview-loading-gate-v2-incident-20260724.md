# Avatar-preview loading-gate V2 incident - 2026-07-24

## Immutable result

Loading-gate V2 is rejected. It must not be described as accepted or reused as
the Phase 1 compatibility baseline.

- V2 `Net.dll` SHA-256:
  `73E65FBFA3EA9809AF597DA3D25D1E0963B0A4A467549191BAFB4FAE9F2902FD`
- V2 Apply/stock-restore backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-155531621`
- V2 Apply manifest SHA-256:
  `9A92451A6786EBBCBA65EA27B09A0EFDA0115754CCE73408CA717FC3CE4B8DFC`
- Evidence run: `20260724T040509293Z-4ce08407`
- Evidence tool version: `1.3.0`
- Repository revision: `8da301c278af7b9a0038e2ab8762c3b6bf6e3830`
- Result/completion time: `Fail` /
  `2026-07-24T04:14:58.4864448Z`
- Completed account-switch cycles: `2`

The evidence files are retained locally under:

```text
C:\Reborn\artifacts\network-shim\manual-parity\
  20260724T040509293Z-4ce08407
```

Their immutable file hashes are:

- `manifest.json`:
  `C960F29745CB0419333CC66B69F8D1F6374D051BC3DD04FDDB38CA96870F50A6`
- `completion.json`:
  `59A976B027D2186CE97595B595918B3C39B65238D52C68A98E79FF946CC687DE`

## Live failure

The first two recorded V2 observations passed for account 7 and account 13.
Cycle 3 then used a fresh account-7 process:

```text
Origin PID                         66268
Origin process start              2026-07-24T04:12:55.4346229Z
CharacterPreview opcode 10002     2026-07-24T04:13:00.835294301Z
Observed blank-model duration     more than 44 seconds
```

The 3D character model remained blank while Origin stayed responsive and its
TCP game connection remained `Established`. The installed V2 and
`NetLegacy.dll` hashes were exact. No new dump appeared and `Error.log` did not
change.

V2 released the exact held preview pointer after five seconds even though the
avatar-resource readiness probe was still false. That unready handoff
recreated the permanent blank-preview symptom. It avoided the V1 transport
starvation and disconnect, but it did not satisfy automatic model loading.

No passing observation was recorded for cycle 3. Its failure facts are stored
in the checksummed completion attestation, whose result is `Fail`.

## Decision

Elapsed time is not evidence that the six native avatar-resource pointers are
safe. A loading-gate candidate therefore may not release opcode `10002` because
a timer expired.

V3 keeps the V2 properties that did work:

- every native `Process()` call remains delegated;
- the exact stock `CMsg` pointer is retained;
- `PickMsg()` does not poll past it, preserving message order; and
- an explicit lifecycle reset disposes the pointer exactly once if the shim
  still owns it.

V3 removes the timer handoff. It returns the retained pointer only after the
readiness probe succeeds.

## Installed V3 candidate

V3 is installed for controlled live validation; it is not yet accepted.

- V3 `Net.dll` SHA-256:
  `17A7219868BAC19BA2BDDD2949FCF70884D4FD9F3EC5799455EF944F40D878D1`
- Install state: `InstalledExact`
- V3 Apply/stock-restore backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-162423590`
- V3 Apply manifest SHA-256:
  `BD139E5D461BEF7B209945F21816E04A5E752F7C0447DB0EDAD5909F2E8CC4D2`
- Apply creation time: `2026-07-24T04:24:23.6140096Z`
- Stock `Net.dll` and installed `NetLegacy.dll` SHA-256:
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`

V1 evidence remains separately preserved in
[`client-avatar-preview-loading-gate-incident-20260724.md`](client-avatar-preview-loading-gate-incident-20260724.md).
Neither V1 nor V2 evidence can satisfy V3 acceptance.
