# Phase 4 controlled-host rollback commands

This is the mandatory rollback and the recovery path after a failed Phase 4
profile. It is deliberately narrow: close the disposable client, stop the
foreground loopback server gracefully, restore the exact secure-Docker
profile, then use the protected campaign handoff to restore the stock client,
hosts bytes, CurrentUser trust, and safe-disabled activation state.

Do not change Norton, Windows Firewall, adapters, routes, or internet
connectivity. Do not start the raw Docker profile. Do not stop PostgreSQL.

## Stop the controlled-host server

Close `C:\RebornNetworkAcceptanceClient\Origin.exe`. From a second ordinary
console under the same issued user, request the runner's bounded graceful
shutdown:

```powershell
powershell.exe -NoProfile -File "C:\Reborn\tools\StopPhase4LoopbackAcceptanceServer.ps1"
```

Wait for the foreground runner to return its result before restoring Docker.
If no controlled-host runner is active, preserve the stop error and verify
that no foreground game listener remains; do not kill an unrelated process.

## Restore the secure-Docker owner

From `C:\Reborn`, with no foreground game server running:

```powershell
Set-Location C:\Reborn
$ErrorActionPreference='Stop'
$secureCompose=@(
 '--env-file','.env.secure.local',
 '-f','docker-compose.yml',
 '-f','docker-compose.secure.yml',
 '--profile','secure'
)
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
```

`ManagePhase4SecureDockerClient.ps1 -Mode Restore` refuses to run unless this
exact secure-Docker boundary is healthy. If the restart fails, preserve the
output and do not mutate client/hosts/trust state with ad hoc commands.

## Restore the receipt-bound client campaign

Close `C:\RebornNetworkAcceptanceClient\Origin.exe`. Open a fresh elevated
`powershell.exe -NoLogo -NoProfile` under the issued user account, not SYSTEM:

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

The two development CNG private signing keys were absent before Apply and are
not needed by an already-signed manifest. The campaign never recreates them.
Restore removes only the exact public CurrentUser root authorized by its own
protected receipt, restores the original hosts bytes and stock client bundle,
sets activation mode to 0, and retains environment 1 and monotonic floor 3.

Retain:

- `C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV1` as the active
  PreviewReadyV1/schema-2 protected campaign audit/recovery record;
- the protected evidence files under
  `C:\Reborn\artifacts\controlled-host-acceptance\20260727-004151-preview-ready-v1\server-evidence`;
  and
- `C:\ProgramData\RebornSecureNetworkPhase4Docker` plus its earlier evidence
  as the read-only failed legacy campaign record. Its five apparent successful
  previews followed by three persistent blank previews are not accepted.

Deleting the disposable client, database, Docker volume, or evidence is a
separate destructive action and is not part of Phase 4 rollback.
