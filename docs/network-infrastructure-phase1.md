# Client network shim Phase 1 runbook

## Verified state

- Status: automated gates complete; interactive parity pending
- Supported `Origin.exe` SHA-256:
  `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`
- Stock `Net.dll`/installed `NetLegacy.dll` SHA-256:
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`
- Installed shim SHA-256:
  `528913E66888D5C070C39949D2FC1AE439B8414B15152312D4E093A29D17A6DD`
- Installed state: `InstalledExact`
- Current Apply backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-112517594`

This is the executable verification and rollback contract for Phase 1 of
[`network-infrastructure-goal.md`](network-infrastructure-goal.md). `Pending`
interactive results mean Phase 1 is not yet accepted and TLS/UDP work must not
begin.

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
6. creates/releases 32 real stock client objects through the shim; and
7. proves missing/tampered legacy files fail closed with stable errors.

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
   `127.1.1.110:5998` and `:7000`; installer Status is `InstalledExact`.
2. Record file names, sizes, and timestamps under `C:\Godswar Origin\Dump`,
   `Dump\Error.log`, and `Log`; do not delete them.
3. Start the normal client. While open, run the evidence recorder. It prefers
   direct `Process.Modules` evidence and otherwise records the explicitly
   labeled, PID-and-FILETIME-bound Restart Manager file-use fallback described
   above; no launcher/extra process is added.
4. Login, select a character, enter the world, move continuously, change maps,
   fight, chat, and use inventory, equip/unequip, forge, Gear Mentor, and
   Zodiac. Logout cleanly.
5. Fully close and relaunch the client, alternating account 7 then account 13
   for five complete cycles. Both must enter the world on the first attempt.
6. Run a longer movement/map-transition soak and repeat the dump/log inventory.
   No new crash dump or network exception may appear. The shim must never log
   credentials, tickets, keys, or payloads.
7. Restore using the recorded Apply backup and run one stock login/world-entry
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
  -NoBehaviorDifference `
  -Notes 'Five alternating launches, rollback, and reapply behaved normally.'
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

Any difference or crash fails Phase 1 and triggers Restore.

## Interactive acceptance record

The 2026-07-24 evidence audit found no post-install client run: the latest
client/server activity preceded the 11:25 NZST shim installation. Therefore
this record correctly remains pending; pre-install logs are not parity
evidence.

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
