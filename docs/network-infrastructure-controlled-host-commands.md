# Phase 4 controlled-host command reference

Use this only with the
[Phase 4 controlled-host acceptance runbook](network-infrastructure-controlled-host-acceptance.md).
The current campaign is repeatable and remains limited to the disposable
client, exact loopback endpoints, `godswar_secure_dev`, and the protected
campaign receipt.

Do not disable Norton or Windows Firewall, add a firewall rule, change a
network adapter or route, or stop PostgreSQL. Do not run the original
launcher/patcher. The only client executable used here is
`C:\RebornNetworkAcceptanceClient\Origin.exe`.

## Gate 1: offline and secure-Docker baseline

From an ordinary `powershell.exe -NoLogo -NoProfile`:

```powershell
Set-Location C:\Reborn
$ErrorActionPreference='Stop'

& .\tools\TestClientNetworkShim.ps1
& .\tools\RestorePhase4AcceptedNetworkShimArtifacts.ps1
& .\tools\TestControlledHostPrivacyEvidence.ps1
& .\tools\TestPhase4SecureDockerClientCampaign.ps1
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
```

The smoke must authenticate through TLS, bind authenticated UDP, send one
authoritative input, receive its acknowledged snapshot, clean up its random
fixture, and leave secure Docker healthy with zero restarts.

The accepted candidate and native-check pins are:

```text
Net.dll                    0328D7EA84B68DD8D5A1DF7B0A291B9DC17EF3337C0114A7A396283FC4EF852B
Godswar.NetShim.Checks.exe D583309B921C7AA795F7A044F096762703AA2DB376A1D07B9EEB4F44312208D0
RebornNetwork.gwem         3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C
```

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
   $status.RootState -cne 'InstalledExact' -or
   $status.ActivationMode -ne 1 -or
   $status.ActivationEnvironment -ne 1 -or
   $status.SequenceFloor -ne 3 -or
   $status.ManifestSequence -ne 3 -or
   [string]::IsNullOrWhiteSpace($status.HandoffPath)){
 throw 'Phase 4 installed campaign state is not exact.'
}
$apply,$status|Format-List
```

`Apply` keeps secure Docker running. It installs only:

- the exact public development root in the issued user's CurrentUser store;
- the checked, receipt-bound loopback hosts mapping;
- the hash-pinned client bundle; and
- the monotonic HKLM activation state at environment 1, mode 1, floor 3.

The campaign writes independent protected cleanup authority beneath
`C:\ProgramData\RebornSecureNetworkPhase4Docker`. Record the displayed
campaign ID and handoff path. No reboot is needed because this operation does
not change the already-hardened client inventory or its accepted reboot epoch.

Close the elevated console.

## Gate 3: Docker-to-foreground handoff

First confirm the installed state from a fresh ordinary console:

```powershell
Set-Location C:\Reborn
$ErrorActionPreference='Stop'
$status=& .\tools\ManagePhase4SecureDockerClient.ps1 -Mode Status
if($status.State -cne 'InstalledExact' -or
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

The command remains in the foreground. In a second ordinary console, launch:

```powershell
Start-Process `
 -FilePath 'C:\RebornNetworkAcceptanceClient\Origin.exe' `
 -WorkingDirectory 'C:\RebornNetworkAcceptanceClient'
```

Perform the non-fault rows in the
[manual acceptance matrix](network-infrastructure-controlled-host-acceptance.md#manual-acceptance-matrix),
including five alternating account 7/13 entries, preview readiness, unmounted
and mounted movement, map transition, death/revive, lifecycle, and viewer
parity when a second client is available.

Close the client and stop the server gracefully. The runner must return
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

Launch the same disposable `Origin.exe`, enter the world, and move
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

Close the client and stop the server gracefully after the behavior completes.
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
Close the client and stop the server gracefully. The runner rejects an
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
   $status.ActivationMode -ne 0 -or
   $status.ActivationEnvironment -ne 1 -or
   $status.SequenceFloor -ne 3 -or
   $status.ManifestSequence -ne 3){
 throw 'Mandatory Phase 4 Restore is not exact.'
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
by Apply, disable activation, and retain the monotonic floor at sequence 3.
It does not create, use, or remove a CNG private signing key.

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
the healthy secure-Docker inspection, and the successful stock-client
Restore. It then creates one bounded, BOM-free, checksummed, read-only
`completion-<campaign-id>.json` receipt in the protected campaign root.
Existing or partial completion output is never overwritten. This command
does not change listeners, trust, hosts, adapters, firewall, Norton, Docker,
or client files; its only mutation is the final protected receipt.
