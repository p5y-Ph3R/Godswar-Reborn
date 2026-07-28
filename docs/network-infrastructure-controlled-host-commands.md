# Phase 4 controlled-host command reference

Use this only with the
[Phase 4 controlled-host acceptance runbook](network-infrastructure-controlled-host-acceptance.md).
This workflow is repeatable and remains limited to the disposable
client, exact loopback endpoints, `godswar_secure_dev`, and the protected
campaign receipt.

Do not disable Norton or Windows Firewall, add a firewall rule, change a
network adapter or route, or stop PostgreSQL. Do not run anything from the
original client tree or approve launcher elevation or an update. Start the
disposable client only through
`C:\RebornNetworkAcceptanceClient\Launch.exe`; the validated chain is
`Launch.exe` -> `patcher.exe autorun` -> `Origin.exe` within that same tree.

The accepted PreviewReadyV6 workflow writes only beneath
`C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV6`; its foreground
profiles write beneath
`C:\Reborn\artifacts\controlled-host-acceptance\20260728-102640-preview-ready-v6\server-evidence`.
V6 is accepted under final campaign
`0a73fd79-961b-42c7-82cc-9e4a6f9e3355`; see the
[V6 candidate note](network-infrastructure-preview-ready-v6.md).

## Gate 1: offline and secure-Docker baseline

From an ordinary `powershell.exe -NoLogo -NoProfile`:

```powershell
Set-Location C:\Reborn
$ErrorActionPreference='Stop'

$guardedOrigin=(
 'C:\Reborn\artifacts\controlled-host-acceptance\' +
 '20260728-102640-preview-ready-v6\candidate\Origin.exe')
if((Get-FileHash $guardedOrigin -Algorithm SHA256).Hash -cne
 'E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C'){
  throw 'Guarded V6 Origin fixture changed.'
}
$previewBuild=& .\tools\BuildPhase4PreviewReadyNetworkShim.ps1 `
 -CandidateOriginPath $guardedOrigin
if($previewBuild.CandidateSha256 -cne
   '2169589316DE3157F999563F80A3DFE9B73A120F73AFE1723D92338B816CAE97' -or
   $previewBuild.NativeChecksSha256 -cne
   'FD34DD6F8FBD518D55C3833FB7E33C5DC819FD546D6799B201CE43E2A7424F75'){
 throw 'Preview-ready public-trust build changed.'
}
& .\tools\TestClientAvatarTimeoutRetryGuardPatch.ps1
& .\tools\TestSecureNetworkPairedOriginBundle.ps1
& .\tools\TestControlledHostPrivacyEvidence.ps1
& .\tools\TestControlledHostMutableOutput.ps1
& .\tools\TestPhase4SecureDockerClientBundle.ps1
& .\tools\TestPhase4SecureDockerClientCampaign.ps1
& .\tools\TestPhase4CompletionReceipt.ps1
& .\tools\TestPhase4LoopbackAcceptanceRunner.ps1
& .\tools\TestSecureDockerProfile.ps1

$status=& .\tools\ManagePhase4SecureDockerClient.ps1 -Mode Status
if($status.State -notin @('Ready','Restored')){
 throw "Phase 4 campaign is not ready: $($status.State)"
}
if($status.DockerState -cne 'HealthyExact' -or
   $status.BundleState -cne 'Stock' -or
   $status.HostsState -cne 'Absent' -or
   $status.RootState -cne 'Absent' -or
   $status.MutableOutputState -cne 'Inactive' -or
   $status.ActivationMode -ne 0 -or
   $status.ActivationEnvironment -ne 1 -or
   $status.SequenceFloor -ne 3 -or
   $status.ManifestSequence -ne 3){
 throw 'Phase 4 safe-disabled baseline is not exact.'
}

$keys=& .\tools\ManageDevelopmentEndpointManifestKeys.ps1 -Mode Status
if($keys.CurrentExists -or $keys.NextExists -or
   $keys.PrivateKeysExportable){
 throw 'Development signing keys must remain absent.'
}

& .\tools\InvokeSecureDockerSmoke.ps1 `
 -RootCertificatePath `
 'C:\Reborn\artifacts\controlled-host-acceptance\20260727-011921\tls\reborn-development-root.cer'

$checks=(
 'C:\Reborn\artifacts\controlled-host-acceptance\' +
 '20260728-102640-preview-ready-v6\candidate\' +
 'Godswar.NetShim.Checks.exe')
$candidateNet=Join-Path (Split-Path $checks) 'Net.dll'
& $checks --offline-origin-contract-probe $candidateNet $guardedOrigin
if($LASTEXITCODE){throw 'Paired Origin/Net identity probe failed.'}
& $checks --controlled-host-tls-probe `
 753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79
if($LASTEXITCODE){throw 'Native TLS/preface probe failed.'}
```

The smoke must authenticate through TLS, bind authenticated UDP, send one
authoritative input, receive its acknowledged snapshot, clean up its random
fixture, and leave secure Docker healthy with zero restarts.
The native probe separately proves the x86 Schannel client path and secure
preface while the Windows development root remains absent.

The accepted paired V6 candidate pins are:

```text
Origin.exe                 E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C
Net.dll                    2169589316DE3157F999563F80A3DFE9B73A120F73AFE1723D92338B816CAE97
Godswar.NetShim.Checks.exe FD34DD6F8FBD518D55C3833FB7E33C5DC819FD546D6799B201CE43E2A7424F75
RebornNetwork.gwem         3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C
```

PreviewReadyV5 is rejected and frozen under
`C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV5`, with fixture
`C:\Reborn\artifacts\controlled-host-acceptance\20260728-031445-preview-ready-v5`.
Its protected terminal restore is `handoff-000024.json`; do not use it for a
new Apply.

The rejected PreviewReadyV3 campaign is frozen under
`C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV3`, with fixture
`C:\Reborn\artifacts\controlled-host-acceptance\20260728-004030-preview-ready-v3`.
Do not use it for a new Apply.

The failed PreviewReadyV2 campaign remains immutable under
`C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV2`, with fixture
`C:\Reborn\artifacts\controlled-host-acceptance\20260727-185522-preview-ready-v2`
and historical Net/check hashes
`EFFC21D1500C39352ADEFB2B2D6388912A7EF50505BD3AD8CB043D32D7D956CE` /
`237EA0A3B90A4642DADA1170B1A740B966984C8004B99698F752491EC6732187`.
Do not use that fixture for a new Apply.

The already-signed manifest and compiled public verification keys are all the
runtime needs. The two development CNG private signing keys are intentionally
absent and are never recreated by this campaign.

## Gate 2: receipt-bound client Apply

Close `Origin.exe`. Open a fresh elevated
`powershell.exe -NoLogo -NoProfile` under the issued user account, not SYSTEM:

```powershell
Set-Location C:\Reborn
$ErrorActionPreference='Stop'

$apply=& .\tools\ManagePhase4SecureDockerClient.ps1 `
 -Mode Apply -AllowMutation -Confirm:$false
if($apply.Result -notin @('InstalledExact','AlreadyInstalledExact')){
 throw "Phase 4 Apply failed: $($apply.Result)"
}

$status=& .\tools\ManagePhase4SecureDockerClient.ps1 -Mode Status
if($status.State -cne 'InstalledExact' -or
   $status.DockerState -cne 'HealthyExact' -or
   $status.BundleState -cne 'InstalledExact' -or
   $status.HostsState -cne 'InstalledExact' -or
   $status.RootState -cne 'Absent' -or
   $status.TlsTrustMode -cne 'EmbeddedDevelopmentRoot' -or
   $status.MutableOutputState -cne 'Active' -or
   $status.ActivationMode -ne 1 -or
   $status.ActivationEnvironment -ne 1 -or
   $status.SequenceFloor -ne 3 -or
   $status.ManifestSequence -ne 3 -or
   [string]::IsNullOrWhiteSpace($status.HandoffPath)){
 throw 'Phase 4 installed campaign state is not exact.'
}
if((Get-FileHash `
 'C:\RebornNetworkAcceptanceClient\Origin.exe' -Algorithm SHA256).Hash -cne
 'E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C'){
  throw 'Guarded V6 Origin was not installed.'
}
$apply,$status|Format-List
```

`Apply` keeps secure Docker running. It installs only:

- no Windows trust-store certificate; V6 pins its development root in Net;
- the checked, receipt-bound loopback hosts mapping;
- the schema-4 paired guarded-Origin/public-trust-Net bundle;
- bounded read/write access to the existing `patcher\patcher.log` file only;
  its parent directory remains protected and read-only; and
- the monotonic HKLM activation state at environment 1, mode 1, floor 3.

The campaign writes independent protected cleanup authority beneath
`C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV6`. Record the
displayed PreviewReadyV6 campaign ID and handoff path. PreviewReadyV5 and
earlier generations are historical and read-only. No reboot is needed
because Apply preserves the accepted inventory epoch.

Close the elevated console.

## Gate 3: Docker-to-foreground handoff

First confirm the installed state from a fresh ordinary console:

```powershell
Set-Location C:\Reborn
$ErrorActionPreference='Stop'
$status=& .\tools\ManagePhase4SecureDockerClient.ps1 -Mode Status
if($status.State -cne 'InstalledExact' -or
   $status.MutableOutputState -cne 'Active' -or
   $status.DockerState -cne 'HealthyExact'){
 throw 'Installed client or secure-Docker baseline drifted.'
}
```

Do not launch the client yet. Stop only the secure-Docker server and leave
PostgreSQL healthy:

```powershell
$secureCompose=@(
 '--env-file','.env.secure.local',
 '-f','docker-compose.yml',
 '-f','docker-compose.secure.yml',
 '--profile','secure'
)
& docker compose @secureCompose stop server
if($LASTEXITCODE){throw 'Stopping secure-Docker server failed.'}

$serverRunning=(& docker inspect -f '{{.State.Running}}' `
 godswar-server).Trim()
$postgresRunning=(& docker inspect -f '{{.State.Running}}' `
 godswar-postgres).Trim()
$postgresHealth=(& docker inspect -f '{{.State.Health.Status}}' `
 godswar-postgres).Trim()
if($serverRunning -cne 'false' -or
   $postgresRunning -cne 'true' -or $postgresHealth -cne 'healthy'){
 throw 'Docker-to-foreground handoff is not exact.'
}
if(@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
     Where-Object LocalPort -in 5998,5999,6599,7000,7443).Count -or
   @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
     Where-Object LocalPort -eq 7444).Count){
 throw 'A game listener remains after secure-Docker stop.'
}
```

If this block fails, do not start a foreground server. Restart the exact
secure-Docker profile using the recovery block below.

## Gate 4: Baseline evidence profile

In the same ordinary server console:

```powershell
& .\tools\RunPhase4LoopbackAcceptanceServer.ps1 `
 -EvidenceProfile Baseline -AllowLoopbackAcceptance
```

The command remains in the foreground. In a second ordinary console, start
the validated disposable launcher chain:

```powershell
Start-Process `
 -FilePath 'C:\RebornNetworkAcceptanceClient\Launch.exe' `
 -WorkingDirectory 'C:\RebornNetworkAcceptanceClient'
```

Do not approve launcher elevation or an update. The launcher must hand off to
the disposable `patcher.exe` and then the disposable `Origin.exe`; direct
`Origin.exe` startup is not accepted because it can omit required client
initialization.

Perform the non-fault rows in the
[manual acceptance matrix](network-infrastructure-controlled-host-acceptance.md#manual-acceptance-matrix),
including five alternating account 7/13 entries, preview readiness, unmounted
and mounted movement, map transition, death/revive, lifecycle, and viewer
parity when a second client is available.

Close the client, then request the bounded same-user graceful stop from a
second ordinary console:

```powershell
powershell.exe -NoProfile -File "C:\Reborn\tools\StopPhase4LoopbackAcceptanceServer.ps1"
```

The runner must return
`Result: Accepted`, `EvidenceProfile: Baseline`, database
`godswar_secure_dev`, and a protected evidence path. Baseline evidence must
contain accepted UDP movement followed by a queued UDP snapshot and must
contain no fault-campaign event.

## Gate 5: one-shot fallback profile

Restart the foreground server:

```powershell
& .\tools\RunPhase4LoopbackAcceptanceServer.ps1 `
 -EvidenceProfile Fallback -AllowLoopbackAcceptance
```

Launch the same disposable `Launch.exe` chain, enter the world, and move
continuously. The server logically suppresses the selected epoch-one snapshot
acknowledgement for 1.5 seconds. Do not simulate loss with Norton, Windows
Firewall, an adapter, or a route.

The client must switch once to the adjacent TLS epoch, accept one
authoritative `NotReady` correction, keep moving on TLS, and never switch back
in that session. These five fixed events must complete within 15 seconds after
the eligible UDP snapshot triggers the campaign:

```text
[secure-acceptance] phase4 fault campaign enabled
[secure-acceptance] snapshot ACK drop started window_ms=1500 max_recorded_drops=32
[secure-acceptance] one-way TLS fallback observed
[secure-acceptance] authoritative correction forced reason=not_ready
[secure-acceptance] post-fallback TLS movement observed no_switchback=true
```

Close the client and, after the behavior completes, run:

```powershell
powershell.exe -NoProfile -File "C:\Reborn\tools\StopPhase4LoopbackAcceptanceServer.ps1"
```

The runner must return `Result: Accepted` and `EvidenceProfile: Fallback`.
An incomplete or expired campaign fails closed.

## Gate 6: ten-minute Soak profile

Restart without the fault switch:

```powershell
& .\tools\RunPhase4LoopbackAcceptanceServer.ps1 `
 -EvidenceProfile Soak -AllowLoopbackAcceptance
```

Launch the disposable client and perform normal movement for at least ten
measured minutes, including mount/dismount, one map transition, and reconnect.
Close the client, then run:

```powershell
powershell.exe -NoProfile -File "C:\Reborn\tools\StopPhase4LoopbackAcceptanceServer.ps1"
```

The runner rejects an
observed foreground lifetime below ten minutes, any fault event, missing UDP
movement/snapshot evidence, or malformed/repeating evidence.

Record the returned evidence path, Release set SHA-256, server SHA-256,
options SHA-256, and observed duration. A server event does not replace the
operator's visual result for the manual matrix.

## Gate 7: restore secure Docker

With every foreground server stopped:

```powershell
& docker compose @secureCompose up -d server
if($LASTEXITCODE){throw 'Secure-Docker restart failed.'}

for($i=0;$i -lt 30;$i++){
 $running=(& docker inspect -f '{{.State.Running}}' `
  godswar-server 2>$null).Trim()
 $health=(& docker inspect -f '{{.State.Health.Status}}' `
  godswar-server 2>$null).Trim()
 $restarts=(& docker inspect -f '{{.RestartCount}}' `
  godswar-server 2>$null).Trim()
 if($running -ceq 'true' -and $health -ceq 'healthy' -and
    $restarts -ceq '0'){break}
 Start-Sleep -Seconds 1
}
if($running -cne 'true' -or $health -cne 'healthy' -or
   $restarts -cne '0'){
 throw 'Secure-Docker server did not recover exactly.'
}

$status=& .\tools\ManagePhase4SecureDockerClient.ps1 -Mode Status
if($status.State -cne 'InstalledExact' -or
   $status.MutableOutputState -cne 'Active' -or
   $status.DockerState -cne 'HealthyExact'){
 throw 'Secure-Docker recovery or active client campaign drifted.'
}

& .\tools\InvokeSecureDockerSmoke.ps1 `
 -RootCertificatePath `
 'C:\Reborn\artifacts\controlled-host-acceptance\20260727-011921\tls\reborn-development-root.cer'
```

If a foreground start or evidence profile fails, use this same recovery block
before mandatory Restore. Do not run raw Docker Compose or improvise alternate
ports.

## Gate 8: mandatory campaign Restore

Close `Origin.exe`. From a fresh elevated console under the issued user:

```powershell
Set-Location C:\Reborn
$ErrorActionPreference='Stop'

$restore=& .\tools\ManagePhase4SecureDockerClient.ps1 `
 -Mode Restore -AllowMutation -Confirm:$false
if($restore.Result -notin @('Restored','AlreadyRestored')){
 throw "Phase 4 Restore failed: $($restore.Result)"
}

$status=& .\tools\ManagePhase4SecureDockerClient.ps1 -Mode Status
if($status.State -cne 'Restored' -or
   $status.DockerState -cne 'HealthyExact' -or
   $status.BundleState -cne 'Stock' -or
   $status.HostsState -cne 'Absent' -or
   $status.RootState -cne 'Absent' -or
   $status.MutableOutputState -cne 'Inactive' -or
   $status.ActivationMode -ne 0 -or
   $status.ActivationEnvironment -ne 1 -or
   $status.SequenceFloor -ne 3 -or
   $status.ManifestSequence -ne 3){
 throw 'Mandatory Phase 4 Restore is not exact.'
}
if((Get-FileHash `
 'C:\RebornNetworkAcceptanceClient\Origin.exe' -Algorithm SHA256).Hash -cne
 '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79' -or
   (Get-FileHash `
 'C:\RebornNetworkAcceptanceClient\Net.dll' -Algorithm SHA256).Hash -cne
 '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'){
 throw 'Paired client predecessors were not restored.'
}

$keys=& .\tools\ManageDevelopmentEndpointManifestKeys.ps1 -Mode Status
if($keys.CurrentExists -or $keys.NextExists -or
   $keys.PrivateKeysExportable){
 throw 'Development signing-key absence changed.'
}
$restore,$status,$keys|Format-List
```

Restore is intentionally blocked until secure Docker is healthy again. It
uses the checksummed campaign handoff to restore stock client files and
original hosts bytes, remove only the exact CurrentUser public root installed
by Apply, return `patcher\patcher.log` to read-only, disable activation, and
retain the monotonic floor at sequence 3. It does not create, use, or remove a
CNG private signing key.

Preserve the campaign receipt and the three protected evidence files for
review. Do not delete `godswar_secure_dev`, its Docker volume, the disposable
client, or the prior historical acceptance record as part of this rollback.

## Gate 9: issue the Phase 4 completion receipt

Each foreground profile returns `ProfileResultPath`,
`ProfileResultChecksumPath`, and `ProfileResultSha256` after its evidence has
been validated and protected. Keep those three profile-result paths. After
Gate 8 reports the exact Restored state, run the completion gate from a fresh
elevated console under the same issued user:

```powershell
$completion=& .\tools\CompletePhase4LoopbackAcceptance.ps1 `
 -BaselineProfileResultPath $baselineProfileResultPath `
 -FallbackProfileResultPath $fallbackProfileResultPath `
 -SoakProfileResultPath $soakProfileResultPath `
 -AttestAlternatingAccounts `
 -AttestPreviewReadiness `
 -AttestUnmountedMovement `
 -AttestMountedMovement `
 -AttestWorldGenerationChanges `
 -AttestDeathAndRevive `
 -AttestSessionLifecycle `
 -AttestFallbackCorrection `
 -AttestSoakStability `
 -AttestDatabaseMutationReviewed `
 -ViewerParity Passed `
 -AllowCompletion
$completion|Format-List
```

Use `-ViewerParity Unavailable` only when the documented second-client check
could not be run. The gate revalidates every checksummed profile and evidence
file, their profile/duration policy, campaign/user/build/client/manifest pins,
including the guarded candidate Origin, the healthy secure-Docker inspection,
and the successful paired-predecessor Restore. It then creates one bounded,
BOM-free, checksummed, read-only
`completion-<campaign-id>.json` receipt in the protected campaign root.
Existing or partial completion output is never overwritten. This command
does not change listeners, trust, hosts, adapters, firewall, Norton, Docker,
or client files; its only mutation is the final protected receipt.

The sealed V6 run used these profile results:

- `secure-server-20260728-020955-8076984.profile.json` — Baseline;
- `secure-server-20260728-021202-8420702.profile.json` — Fallback; and
- `secure-server-20260728-021422-2616795.profile.json` — Soak.

All belong to campaign `0a73fd79-961b-42c7-82cc-9e4a6f9e3355`. Receipt
`completion-0a73fd79-961b-42c7-82cc-9e4a6f9e3355.json` has SHA-256
`5EB6E369652605CA58A0D5CE2F01604268FAA2CE9A1323A4346F7DBFA15F4A6F`
and records `ValidatedRestoredCampaign`, `HealthyExact`, and viewer parity
`Unavailable`.
