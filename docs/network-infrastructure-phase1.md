# Client network shim Phase 1 runbook

## Verified state

- Status: loading-gate V1 and V2 are rejected; readiness-only V3 is
  `InstalledExact` with controlled live acceptance pending
- Supported `Origin.exe` SHA-256:
  `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`
- Stock `Net.dll`/installed `NetLegacy.dll` SHA-256:
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`
- Historical failed v1 shim SHA-256:
  `2D819908BEE2FA7D8BE4957E18358DEFFB5FD65D01AC26D6F73F29F4C71E2AE0`
- Historical network-stable pass-through shim SHA-256:
  `528913E66888D5C070C39949D2FC1AE439B8414B15152312D4E093A29D17A6DD`
- Historical pass-through Apply backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-151248244`
- Rejected V2 shim SHA-256:
  `73E65FBFA3EA9809AF597DA3D25D1E0963B0A4A467549191BAFB4FAE9F2902FD`
- Historical V2 Apply/stock-restore backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-155531621`
- Historical V2 Apply manifest SHA-256:
  `9A92451A6786EBBCBA65EA27B09A0EFDA0115754CCE73408CA717FC3CE4B8DFC`
- Installed V3 shim SHA-256:
  `17A7219868BAC19BA2BDDD2949FCF70884D4FD9F3EC5799455EF944F40D878D1`
- V3 Apply time: `2026-07-24T04:24:23.6140096Z`
- Current V3 Apply/stock-restore backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-162423590`
- Current Apply manifest SHA-256:
  `BD139E5D461BEF7B209945F21816E04A5E752F7C0447DB0EDAD5909F2E8CC4D2`

This is the executable verification and rollback contract for Phase 1 of
[`network-infrastructure-goal.md`](network-infrastructure-goal.md). Exact
installation and automated success do not prove native rendering. Phase 1
remains unaccepted and TLS/UDP work must not begin until live parity passes.

The intentional preview-timing exception and its exact native-message
ownership contract are documented in
[`client-avatar-preview-loading-gate.md`](client-avatar-preview-loading-gate.md).

## Build and automated verification

Run the automated suites:

```powershell
.\tools\TestClientNetworkShim.ps1
.\tools\TestClientNetworkShimInstaller.ps1
.\tools\TestClientNetworkShimWindowsEvidence.ps1
.\tools\TestClientNetworkShimParity.ps1
```

The native suite:

1. builds twice with Visual Studio 2022 MSVC x86, `/MT`, and strict warnings;
2. requires both clean builds to have the same SHA-256;
3. verifies PE32/x86, preferred base `0x50000000` (distinct from stock
   `NetLegacy.dll`'s `0x10000000`), ASLR, NX, Control Flow Guard, and the exact
   two name/ordinal exports;
4. rejects an unbundled Visual C++ runtime;
5. exercises all nine proxy slots and arguments against a controlled fake;
6. verifies preview readiness, continuous native `Process`, exact-pointer
   ordering across 4,096 unready scheduling cycles, malformed-message
   rejection, readiness-only release, and explicit lifecycle cleanup;
7. invokes a real MSVC scalar-deleting destructor for retained-message
   ownership coverage;
8. creates/releases 32 real stock client objects through the shim; and
9. proves missing/tampered legacy files fail closed with stable errors.

`NetServiceCreate` is name/ordinal and fail-closed checked but intentionally not
success-invoked because `Origin.exe` does not import that unaudited interface.

The installer suite covers Windows PowerShell 5.1 defaults, `WhatIf`, exact
custom-candidate validation, decoy rejection, Stock and RecoverablePartial
installs, idempotency, artifact-independent Restore, resumable interrupted
Restore, foreign legacy/unknown state, unsupported `Origin.exe`, and a running-
client refusal.

The parity-evidence suite covers manifests and checksums, hidden dump/log
evidence, semantic launch/module/connection validation, account/stage ordering,
time bounds, backup chronology, refusal paths, and checksummed completion. On a
clean worktree it also runs
`tools\TestClientNetworkShimParityComplete.ps1`, which exercises a successful
synthetic Begin-to-Complete record against a disposable client copy.

The Windows evidence suite covers the medium-integrity observer used when
`Origin.exe` runs elevated. It resolves the exact process image with
`QueryFullProcessImageNameW`, then queries each DLL in a separate Windows
Restart Manager session and binds its exact file-use result to both process ID
and creation FILETIME. The DLL is independently hashed. This fallback records
`RestartManagerFileUse`; it does not claim a module base address, memory size,
or distinguish a loaded image from another file-use mechanism. Direct
`Process.Modules` evidence remains preferred whenever Windows permits it.

Reproducible release builds require Visual Studio 2022 MSVC tools
`14.44.35207` and Windows SDK `10.0.26100.0`. Matching hashes are guaranteed
only for repeated clean builds in that pinned environment; another compiler
may legitimately produce different reviewed bytes.

The native and disposable installer suites pass for installed V3. Historical
V1/V2 evidence cannot be relabeled as V3 evidence; live parity requires a fresh
run pinned to the installed hash and current Apply backup.

## Status, Apply, and Restore

Read-only status:

```powershell
.\tools\InstallClientNetworkShim.ps1 -Mode Status
```

Guarded Apply:

```powershell
.\tools\InstallClientNetworkShim.ps1 -Mode Apply -Confirm:$false
```

Apply refuses a running client, unknown hashes/state, a foreign
`NetLegacy.dll`, or a candidate that fails the exact binary/runtime probe. It
writes and verifies the stock legacy copy before atomically replacing
`Net.dll`, creates a timestamped manifest backup, and is idempotent.

Restore requires the exact Apply backup printed by Apply:

```powershell
.\tools\InstallClientNetworkShim.ps1 `
  -Mode Restore `
  -ApplyBackupPath 'C:\Reborn\backups\client-network-shim-v1-Apply-...' `
  -Confirm:$false
```

Restore preserves the installed files in a Revert backup. If stock `Net.dll`
was restored but `NetLegacy.dll` cleanup was interrupted, Status reports
`RecoverablePartial`; rerun the same Restore after releasing the file lock.

For the current V3 installation, Restore must use:

```powershell
.\tools\InstallClientNetworkShim.ps1 `
  -Mode Restore `
  -ApplyBackupPath 'C:\Reborn\backups\client-network-shim-v1-Apply-20260724-162423590' `
  -Confirm:$false
```

That returns exact stock `Net.dll` hash `1CC3F9...BCA00C`. If V3 fails live
acceptance and the historical network-stable pass-through is required, apply
its separately preserved candidate only after that Restore:

```powershell
.\tools\InstallClientNetworkShim.ps1 `
  -Mode Apply `
  -ShimPath 'C:\Reborn\backups\client-network-shim-v1-Revert-20260724-155518012\Net.dll' `
  -Confirm:$false
```

The candidate hash is `528913E6...D17A6DD`; guarded Apply creates a new backup.
The historical `...151248244` Apply backup contains stock `Net.dll`, not the
pass-through candidate.

## Interactive parity acceptance

Use the read-only evidence recorder for this gate. It never launches, stops,
restores, applies, or otherwise modifies the client or server. It only records
bounded, checksummed evidence beneath the gitignored `artifacts` directory.
Start from a clean repository with `Origin.exe` closed:

```powershell
$run = .\tools\InvokeClientNetworkShimParity.ps1 -Mode Begin
$evidence = $run.EvidencePath
$evidence
```

Copy the exact printed evidence path if testing continues in another
PowerShell session. For each in-world launch, record it while `Origin.exe` is
still open:

```powershell
.\tools\InvokeClientNetworkShimParity.ps1 `
  -Mode Observe `
  -EvidencePath $evidence `
  -Stage ShimParity `
  -AccountId 7
```

Fully close and relaunch between observations. Record five complete shim
launches in the order `7, 13, 7, 13, 7`, changing `-AccountId` accordingly.
An observation verifies the exact process path, installed hashes, loaded
`Net.dll` and `NetLegacy.dll` paths/hashes (or the explicitly labeled,
PID-and-FILETIME-bound Restart Manager file-use fallback), a distinct process
start, and an established connection to `127.1.1.110:7000`. It does not infer
the logged-in account or gameplay result; those remain operator attestations.
A failed observation fails that evidence run rather than being silently
ignored.
The SHA-256 sidecars expose accidental edits; they are not an authenticity
boundary against a local operator who can rewrite both evidence and checksum.
Evidence runs are tool-version pinned; after a recorder upgrade, start a new
baseline rather than mixing observation schemas.

1. Preflight: `docker ps` reports `godswar-server` up; host listeners exist on
   `127.1.1.110:5998` and `:7000`; installer Status is `InstalledExact` for the
   exact candidate under test.
2. Record file names, sizes, and timestamps under `C:\Godswar Origin\Dump`,
   `Dump\Error.log`, and `Log`; do not delete them.
3. Start the normal client. While open, run the evidence recorder. It prefers
   direct `Process.Modules` evidence and otherwise records the explicitly
   labeled, PID-and-FILETIME-bound Restart Manager file-use fallback described
   above; no launcher/extra process is added.
4. Login, select a character, enter the world, move continuously, change maps,
   fight, chat, and use inventory, equip/unequip, forge, Gear Mentor, and
   Zodiac. Logout cleanly.
5. During any late preview, confirm the selection UI remains responsive and
   loading without an unready handoff, then shows the 3D character
   automatically when resources become ready. A blank model, crash, extra slot
   click, or required relaunch fails the desired loading result.
6. Fully close and relaunch the client, alternating account 7 then account 13
   for five complete cycles. Both must enter the world on the first attempt.
7. Run a longer movement/map-transition soak and repeat the dump/log inventory.
   No new crash dump or network exception may appear. The shim must never log
   credentials, tickets, keys, or payloads.
8. Restore using the recorded Apply backup and run one stock login/world-entry
   smoke. Apply the verified shim again and run one final login/world-entry
   smoke. Record the new Apply backup printed by the final Apply.

Record the stock and reapply launches with `-Stage StockRollback` and
`-Stage FinalReapply`, respectively. Both use the same `Observe` command and
the actual account ID. After closing the final client, complete the record:

```powershell
.\tools\InvokeClientNetworkShimParity.ps1 `
  -Mode Complete `
  -EvidencePath $evidence `
  -FinalApplyBackupPath 'C:\Reborn\backups\client-network-shim-v1-Apply-...' `
  -CompletedCycles 5 `
  -SoakMinutes 10 `
  -ChecklistPassed `
  -LogsReviewed `
  -AvatarPreviewLoadingGatePassed `
  -NoUnintendedBehaviorDifference `
  -Notes 'Loading preview appeared automatically; five alternating launches, rollback, and reapply otherwise behaved normally.'
```

`Complete` fails closed unless the repository and server instance are unchanged,
all required launches are present in order, the final install is exact, the
original backup is unchanged, the final Apply produced a new valid backup, no
dump changed, and the manual checks are attested. A pass writes tool-enforced
write-once, checksummed `completion.json` and `acceptance.md` files inside the
evidence path. Inspect progress at any time with:

```powershell
.\tools\InvokeClientNetworkShimParity.ps1 `
  -Mode Status `
  -EvidencePath $evidence
```

Failure of the intentional loading behavior, any unintended difference, or any
crash fails Phase 1 and triggers Restore.

## Historical pass-through evidence record

The 2026-07-24 evidence audit found no post-install client run: the latest
client/server activity preceded the 11:25 NZST shim installation. Therefore
this historical `528913E6...D17A6DD` pass-through record remains pending;
pre-install logs are not parity evidence. It cannot attest to the later loading
gate.

| Field | Value |
| --- | --- |
| Date/operator | Pending |
| Repository revision (`git rev-parse HEAD`) | Pending |
| Origin/shim hashes | `753BE49F...AD49ED79` / `528913E6...D17A6DD` |
| Server endpoint/commit | `127.1.1.110:5998,7000` / Pending |
| Accounts/cycles | `7 <-> 13` / five complete cycles |
| Soak duration | Pending |
| Dump/log before and after | Pending |
| Stock rollback smoke / final reapply smoke | Pending / Pending |
| Result and final Apply backup | Pending |

## Failed v1 loading-gate record

V1 evidence run
`20260724T030417842Z-94e2c5f4` is immutably completed as `Fail`. Account 7
displayed its model and entered the world. On the next full relaunch, account
13's game connection closed `14.633832034` seconds after CharacterPreview, the
UI showed server-full, and Origin dumped at `0x005F58BC`. The server container
did not crash. V1 was restored to the stable rollback shim.

| Field | Value |
| --- | --- |
| Date/operator | `2026-07-24` / `Iamc1` |
| Repository revision | `1417eed958788b5a5e690fb68e0f23f24c51affd` |
| Origin/shim hashes | `753BE49F...AD49ED79` / `2D819908...E2AE0` |
| Accounts/cycles | account 7 passed; account 13 failed / one completed cycle |
| Connection result | closed after `14.633832034` seconds; server stayed up |
| Client result | server-full UI; new dump at `0x005F58BC` |
| Evidence result | `Fail` |
| Post-failure installed shim at that time | `528913E6...D17A6DD` |
| Historical pass-through Apply evidence | `...\client-network-shim-v1-Apply-20260724-151248244` |

See
[`client-avatar-preview-loading-gate-incident-20260724.md`](client-avatar-preview-loading-gate-incident-20260724.md)
for hashes and fault evidence.

## Historical stable pass-through live baseline

At `2026-07-24T03:30:23Z`, account 7 received one valid 188-byte preview under
the `528913E6...D17A6DD` shim. Origin remained responsive and the TCP game
connection remained established for more than 142 seconds, but the 3D model
was blank. No new dump, game-close log, server exception, or container restart
occurred. This is diagnostic evidence of the native resource race, not a pass
for the loading gate.

## Rejected V2 and installed V3

V2 evidence run `20260724T040509293Z-4ce08407` is immutably completed as
`Fail`. Two cycles passed; on fresh account-7 cycle 3, the five-second unready
handoff left the model blank beyond 44 seconds while TCP stayed established and
no dump appeared. See the
[V2 incident](client-avatar-preview-loading-gate-v2-incident-20260724.md).

| Field | Value |
| --- | --- |
| Rejected V2 shim hash | `73E65FBF...F2902FD` |
| V2 evidence result | `Fail` / two completed cycles |
| Installed V3 shim hash | `17A72198...D878D1` |
| V3 install state/time | `InstalledExact` / `2026-07-24T04:24:23.6140096Z` |
| V3 automated gates | Pass |
| Current Apply backup | `...\client-network-shim-v1-Apply-20260724-162423590` |
| Accounts/cycles | `7 <-> 13` / five complete cycles pending |
| Responsive loading / automatic model | Pending / Pending |
| Connection beyond old 14.6-second failure | Pending |
| Readiness-only hold/release | Automated pass; native result pending |
| Soak / dump and log review | Pending / Pending |
| Stock rollback / final reapply | Pending / Pending |
| Result | Pending |

V3 requires a fresh evidence version and run. Neither V1, V2, nor stable-shim
observations can be reused.
