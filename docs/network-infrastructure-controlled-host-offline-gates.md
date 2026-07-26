# Phase 4 controlled-host offline gates

Run these from an ordinary `powershell.exe -NoLogo -NoProfile` before client
Apply. They are read-only except for ordinary build outputs and bounded test
fixtures. The secure-Docker server and PostgreSQL must already be healthy.

## Build and focused suites

```powershell
Set-Location C:\Reborn
$ErrorActionPreference='Stop'

dotnet build .\src\Godswar.Server\Godswar.Server.csproj `
 --configuration Release
if($LASTEXITCODE){throw 'Managed Release build failed.'}

dotnet run --project .\tests\Godswar.Server.ProtocolChecks `
 --configuration Release
if($LASTEXITCODE){throw 'Managed protocol checks failed.'}

& .\tools\TestClientNetworkShim.ps1
& .\tools\RestorePhase4AcceptedNetworkShimArtifacts.ps1
& .\tools\TestControlledHostPrivacyEvidence.ps1
& .\tools\TestControlledHostActivationSecurity.ps1
& .\tools\TestPhase4SecureDockerClientCampaign.ps1
& .\tools\TestPhase4LoopbackAcceptanceRunner.ps1
& .\tools\TestSecureDockerProfile.ps1
```

Require zero build warnings/errors, the complete protocol-check pass summary,
native client checks, accepted-artifact restore/probes, the privacy-evidence
profile tests, the repeatable Apply/Restore campaign tests, the isolated
loopback-runner profile/result tests, and both rendered Docker-profile checks.

`TestClientNetworkShim.ps1` deliberately builds and tests the checked-in
source with the public-key placeholder header; that source build is not the
accepted signed Phase 4 client candidate. Immediately afterward,
`RestorePhase4AcceptedNetworkShimArtifacts.ps1` atomically restores exact
reviewed artifacts from the immutable `20260727-011921` fixture:

```text
source placeholder Net.dll  BEB6ED3A0582C1F2D1C64D548C143C690ED3BEED0D3208AB8812F10210BBD5BD
accepted signed Net.dll     0328D7EA84B68DD8D5A1DF7B0A291B9DC17EF3337C0114A7A396283FC4EF852B
accepted native checks      D583309B921C7AA795F7A044F096762703AA2DB376A1D07B9EEB4F44312208D0
```

The restore tool rechecks those source hashes and the signed manifest, then
runs the accepted offline, manifest-verification, and stock-delegation probes.
Do not run another native source build between that restore and campaign
Status/Apply.

The Phase 4 campaign test uses temporary paths and synthetic Docker inspection
objects. It must not modify the live client, CurrentUser root store, hosts
file, HKLM activation, Docker containers, or firewall.

## Parser, diff, JSON, and repository-size gates

```powershell
$parseFailures=@()
Get-ChildItem .\tools -Recurse -File |
 Where-Object Extension -in '.ps1','.psm1' |
 ForEach-Object {
  $tokens=$null
  $errors=$null
  [Management.Automation.Language.Parser]::ParseFile(
   $_.FullName,[ref]$tokens,[ref]$errors)|Out-Null
  foreach($error in $errors){
   $parseFailures+="$(($_.FullName)): $($error.Message)"
  }
 }
if($parseFailures.Count){
 throw ($parseFailures -join [Environment]::NewLine)
}

& git diff --check
if($LASTEXITCODE){throw 'git diff --check failed.'}
& git diff --cached --check
if($LASTEXITCODE){throw 'git diff --cached --check failed.'}

Get-Content .\appsettings.json -Raw|ConvertFrom-Json|Out-Null
Get-Content .\appsettings.docker.json -Raw|ConvertFrom-Json|Out-Null

$paths=@(& git diff HEAD --name-only --diff-filter=ACMRTUXB)
$paths+=@(& git diff --cached --name-only --diff-filter=ACMRTUXB)
$paths+=@(& git ls-files --others --exclude-standard)
$oversized=@()
$utf8=[Text.UTF8Encoding]::new($false)
foreach($relative in @($paths|Sort-Object -Unique)){
 $path=Join-Path (Get-Location) $relative
 if(-not(Test-Path -LiteralPath $path -PathType Leaf)){continue}
 $text=[IO.File]::ReadAllText($path)
 $crlf=[regex]::Replace($text,'\r\n|\r|\n',"`r`n")
 $projected=$utf8.GetByteCount($crlf)
 $lines=@(Get-Content -LiteralPath $path).Count
 if($projected -gt 20480 -or $lines -gt 600){
  $oversized+="$relative projectedBytes=$projected lines=$lines"
 }
}
if($oversized.Count){
 throw ($oversized -join [Environment]::NewLine)
}
```

## Exact safe-disabled runtime baseline

```powershell
if(@(Get-Process Origin -ErrorAction SilentlyContinue).Count){
 throw 'Origin.exe must be closed before Phase 4 Apply.'
}

$status=& .\tools\ManagePhase4SecureDockerClient.ps1 -Mode Status
if($status.State -notin @('Ready','Restored') -or
   $status.DockerState -cne 'HealthyExact' -or
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

$candidate='C:\Reborn\client\network-shim\bin\Release\Win32\Net.dll'
$checks=(
 'C:\Reborn\client\network-shim\bin\Release\Win32\' +
 'Godswar.NetShim.Checks.exe')
$candidateSha=(Get-FileHash $candidate -Algorithm SHA256).Hash
$checksSha=(Get-FileHash $checks -Algorithm SHA256).Hash
if($candidateSha -cne
   '0328D7EA84B68DD8D5A1DF7B0A291B9DC17EF3337C0114A7A396283FC4EF852B' -or
   $checksSha -cne
   'D583309B921C7AA795F7A044F096762703AA2DB376A1D07B9EEB4F44312208D0'){
 throw 'Accepted native Phase 4 outputs changed.'
}

& .\tools\InvokeSecureDockerSmoke.ps1 `
 -RootCertificatePath `
 'C:\Reborn\artifacts\controlled-host-acceptance\20260727-011921\tls\reborn-development-root.cer'

[pscustomobject]@{
 Result='OfflineGatesPassed'
 CampaignState=$status.State
 DockerState=$status.DockerState
 SequenceFloor=$status.SequenceFloor
 CandidateSha256=$candidateSha
 NativeChecksSha256=$checksSha
 SigningKeysPresent=$false
}|Format-List
```

The smoke is the machine-verifiable secure-Docker reference baseline. The
original-client Baseline, Fallback, and Soak profiles are separate foreground
gates and still require the manual acceptance matrix.
