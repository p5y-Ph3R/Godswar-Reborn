# Phase 4 controlled-host rollback commands

This is the mandatory rollback and the recovery path after a failed Phase 4
profile. It is deliberately narrow: close the disposable client, stop the
foreground loopback server gracefully, restore the exact secure-Docker
profile, then use the protected campaign handoff to restore the stock client,
including both Origin and Net predecessors, hosts bytes, an absent CurrentUser
development root, and safe-disabled activation state.

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
$client='C:\RebornNetworkAcceptanceClient'
if((Get-FileHash (Join-Path $client 'Origin.exe') -Algorithm SHA256).Hash -cne
   '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79' -or
   (Get-FileHash (Join-Path $client 'Net.dll') -Algorithm SHA256).Hash -cne
   '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C' -or
   (Test-Path (Join-Path $client 'NetLegacy.dll')) -or
   (Test-Path (Join-Path $client 'RebornNetwork.gwem'))){
 throw 'Paired client rollback is not exact.'
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
V6 never installs a CurrentUser root. Restore requires that root to remain
absent, atomically restores stock Origin and Net plus original hosts bytes,
sets activation mode to 0, and retains environment 1 and floor 3.

Retain:

- `C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV6` as the active
  paired-Origin protected campaign audit/recovery record;
- the protected evidence files under
  `C:\Reborn\artifacts\controlled-host-acceptance\20260728-102640-preview-ready-v6\server-evidence`;
  and
- `C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV5`, whose exact
  rejected terminal restore is protected as `handoff-000024.json`;
- `C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV4`,
  `C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV3`,
  `C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV2`,
  `C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV1`, and
  `C:\ProgramData\RebornSecureNetworkPhase4Docker` as read-only historical
  campaign records.

The failed PreviewReadyV2 fixture
`C:\Reborn\artifacts\controlled-host-acceptance\20260727-185522-preview-ready-v2`
retains candidate/check hashes
`EFFC21D1500C39352ADEFB2B2D6388912A7EF50505BD3AD8CB043D32D7D956CE` /
`237EA0A3B90A4642DADA1170B1A740B966984C8004B99698F752491EC6732187`
and dump `20260728001641.dmp`; do not move them into a later generation.

PreviewReadyV3 was manually rejected after dump `20260728011349.dmp`,
SHA-256
`18176B45640DADB220EA090D718927CB742029352405ACA71791183B3E280B7A`,
and a second authenticated pre-world disconnect. Its protected revision-13
handoff records exact `Restored`; keep its fixture
`20260728-004030-preview-ready-v3` and evidence read-only. The LegacyV1
campaign's five apparent successful previews followed by three persistent
blank previews are also not accepted.

PreviewReadyV5 was rejected because live Net sent stock Origin identity
`753BE49F...ED79` while the server allowed patched `E177D94D...CC76C`; its
native probe masked that mismatch through CLI identity injection.
PreviewReadyV6 binds the identity through `GWKEY02` and a paired-file offline
probe. Final campaign `0a73fd79-961b-42c7-82cc-9e4a6f9e3355` completed the
full foreground matrix and exact rollback; protected completion receipt
`completion-0a73fd79-961b-42c7-82cc-9e4a6f9e3355.json` has SHA-256
`5EB6E369...F4A6F`. Rollback remains mandatory for future activations.

Deleting the disposable client, database, Docker volume, or evidence is a
separate destructive action and is not part of Phase 4 rollback.
