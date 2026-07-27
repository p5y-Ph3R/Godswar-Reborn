# Phase 4 controlled-host offline gates

Run these from an ordinary `powershell.exe -NoLogo -NoProfile` before client
Apply. They are read-only except for ordinary build outputs and bounded test
fixtures. The secure-Docker server and PostgreSQL must already be healthy.
The active protected handoff is
`C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV2`; both the
PreviewReadyV1 handoff and the unsuffixed LegacyV1 handoff are historical and
read-only.

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

$previewBuild=& .\tools\BuildPhase4PreviewReadyNetworkShim.ps1
if($previewBuild.CandidateSha256 -cne
   'EFFC21D1500C39352ADEFB2B2D6388912A7EF50505BD3AD8CB043D32D7D956CE' -or
   $previewBuild.NativeChecksSha256 -cne
   '237EA0A3B90A4642DADA1170B1A740B966984C8004B99698F752491EC6732187'){
 throw 'Preview-ready public-trust build changed.'
}
& .\tools\TestControlledHostPrivacyEvidence.ps1
& .\tools\TestControlledHostActivationSecurity.ps1
& .\tools\TestPhase4SecureDockerClientCampaign.ps1
& .\tools\TestPhase4CompletionReceipt.ps1
& .\tools\TestPhase4LoopbackAcceptanceRunner.ps1
& .\tools\TestSecureDockerProfile.ps1
```

Require zero build warnings/errors, the complete protocol-check pass summary,
native client checks, deterministic public-trust build/probes, the
privacy-evidence profile tests, the repeatable Apply/Restore campaign tests,
the isolated loopback-runner profile/result tests, and both rendered
Docker-profile checks.

`BuildPhase4PreviewReadyNetworkShim.ps1` temporarily generates a verification
header from only the pinned current and next public trusts, performs two clean
deterministic builds, runs the native offline/manifest/contract probes, and
restores the checked-in placeholder header exactly. It never accesses a
private signing key. The active immutable
`20260727-185522-preview-ready-v2\candidate` fixture is pinned as:

```text
preview-ready Net.dll       EFFC21D1500C39352ADEFB2B2D6388912A7EF50505BD3AD8CB043D32D7D956CE
preview-ready native checks 237EA0A3B90A4642DADA1170B1A740B966984C8004B99698F752491EC6732187
placeholder header          D72E7E218E2DD6D1730C1A5194965600DEBECDC9232BCF3DAA86494D863519D1
```

The active campaign reads that immutable fixture directly, so a later ordinary
native build cannot silently replace its candidate.

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

$candidate=(
 'C:\Reborn\artifacts\controlled-host-acceptance\' +
 '20260727-185522-preview-ready-v2\candidate\Net.dll')
$checks=(
 'C:\Reborn\artifacts\controlled-host-acceptance\' +
 '20260727-185522-preview-ready-v2\candidate\' +
 'Godswar.NetShim.Checks.exe')
$candidateSha=(Get-FileHash $candidate -Algorithm SHA256).Hash
$checksSha=(Get-FileHash $checks -Algorithm SHA256).Hash
if($candidateSha -cne
   'EFFC21D1500C39352ADEFB2B2D6388912A7EF50505BD3AD8CB043D32D7D956CE' -or
   $checksSha -cne
   '237EA0A3B90A4642DADA1170B1A740B966984C8004B99698F752491EC6732187'){
 throw 'Preview-ready Phase 4 fixture changed.'
}

& $checks --offline
if($LASTEXITCODE){throw 'Preview-ready native offline checks failed.'}
& $checks --offline-manifest-probe $candidate `
 'C:\Reborn\artifacts\secure-network\RebornNetwork.gwem'
if($LASTEXITCODE){throw 'Preview-ready manifest probe failed.'}
& $checks --offline-contract-probe $candidate
if($LASTEXITCODE){throw 'Preview-ready contract probe failed.'}

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
gates and still require the manual acceptance matrix. Their active evidence
root is
`C:\Reborn\artifacts\controlled-host-acceptance\20260727-185522-preview-ready-v2\server-evidence`;
the earlier campaign's five apparent successful previews followed by three
persistent blank previews remain preserved failed evidence, not acceptance.
