# Controlled-host offline gates

Run the common variable/pin block from the
[command reference](network-infrastructure-controlled-host-commands.md) first.
Use an ordinary `powershell.exe -NoLogo -NoProfile`.

## Build, managed integration, native, and transaction suites

```powershell
dotnet build .\src\Godswar.Server\Godswar.Server.csproj --configuration Release
if($LASTEXITCODE){throw 'Managed Release build failed.'}
.\tools\RunControlledHostPostgresProtocolChecks.ps1 `
 -ExpectedDatabaseName $dbName -PostgresConnectionSecretPath $pgSecret `
 -ExpectedPostgresConnectionSecretSha256 $pins[11][1]
.\tools\TestClientNetworkShim.ps1
.\tools\TestControlledHostPrivacyEvidence.ps1
.\tools\TestControlledHostActivationSecurity.ps1
.\tools\TestControlledHostCleanupPolicy.ps1
.\tools\TestControlledHostDatabaseBackup.ps1
.\tools\TestControlledHostFinalCleanupDependencies.ps1
.\tools\TestControlledHostManagedRelease.ps1
.\tools\TestControlledHostRuntimeCleanup.ps1
.\tools\TestControlledHostServerValidation.ps1
.\tools\TestDevelopmentEndpointManifestKeyRemoval.ps1
.\tools\TestDevelopmentNetworkHosts.ps1
.\tools\TestDevelopmentNetworkHostsAcl.ps1
.\tools\TestDevelopmentNetworkHostsHardLinks.ps1
.\tools\TestDevelopmentNetworkHostsRuntimeGate.ps1
.\tools\TestDevelopmentNetworkTrustReceipt.ps1
.\tools\TestSecureNetworkActivationCommit.ps1
.\tools\TestSecureNetworkBundleRestoreState.ps1
.\tools\TestSecureNetworkBundleTransaction.ps1
.\tools\TestSecureNetworkOperationLock.ps1
.\tools\TestControlledHostInstalledCertificateValidation.ps1 `
 -ServerAssemblyPath $server -CertificatePath $pfx `
 -RootCertificatePath $rootCer -TrustReceiptPath $trustSource `
 -CertificatePasswordSecretPath $certSecret
$runtimeSource=& .\tools\PrepareControlledHostServerRuntime.ps1 @runtimeArgs `
 -Mode Status
if($runtimeSource.State -cne 'SourceVerified' -or
   $runtimeSource.RuntimeRoot -cne $runtime -or $runtimeSource.Elevated){
 throw 'Controlled-host runtime source preflight failed.'
}
```

The PostgreSQL wrapper builds the protocol-check project before decrypting the
secret, validates literal `127.0.0.1:5432` plus the exact disposable database,
sets the connection only in process environment for the child, runs all `131`
checks including the ten environment-gated PostgreSQL integrations plus the
static migration-foundation check, and clears it in `finally`. Require the
literal `Protocol checks: 131 passed, 0 failed` summary and no PostgreSQL
`SKIP` line.

## Parser, diff, JSON, and repository-size gates

```powershell
$parseFailures=@()
Get-ChildItem .\tools -Recurse -File|? Extension -in '.ps1','.psm1'|%{
 $tokens=$null;$errors=$null
 [Management.Automation.Language.Parser]::ParseFile(
  $_.FullName,[ref]$tokens,[ref]$errors)|Out-Null
 foreach($error in $errors){$parseFailures+="$(($_.FullName)): $($error.Message)"}
}
if($parseFailures.Count){throw ($parseFailures -join [Environment]::NewLine)}
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
if($oversized.Count){throw ($oversized -join [Environment]::NewLine)}
```

## Process, listener, hosts, and activation baseline

```powershell
if(@(Get-Process Origin -ErrorAction SilentlyContinue).Count){
 throw 'Origin.exe must be closed before controlled-host preparation.'
}
if(@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue|? `
 LocalPort -in 6599,7443).Count -or
   @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue|? LocalPort -eq 7444).Count){
 throw 'A secure acceptance listener is already active.'
}
if((& docker inspect -f '{{.State.Running}}' godswar-server).Trim() -cne 'true' -or
   (& docker inspect -f '{{.State.Running}}' godswar-postgres).Trim() -cne 'true' -or
   (& docker inspect -f '{{.State.Health.Status}}' godswar-postgres).Trim() -cne
    'healthy'){
 throw 'The original Docker server/PostgreSQL baseline is not healthy.'
}
$raw=@(Get-NetTCPConnection -State Listen -ErrorAction Stop|? `
 LocalPort -in 5998,5999,7000)
foreach($port in 5998,7000){
 $match=@($raw|? LocalPort -eq $port)
 if($match.Count -ne 1 -or $match[0].LocalAddress -cne '127.1.1.110'){
  throw "Original raw listener $port is not exact."
 }
}
if(@($raw|? LocalPort -eq 5999).Count){
 throw 'Unexpected raw listener 5999 exists before acceptance.'
}
$hosts='C:\Windows\System32\drivers\etc\hosts'
if((Get-FileHash $hosts -Algorithm SHA256).Hash -cne $expectedHosts){
 throw 'Original hosts bytes changed before acceptance.'
}
$hostsStatus=& .\tools\ManageDevelopmentNetworkHosts.ps1 -Mode Status
if($hostsStatus.State -cne 'Absent' -or $hostsStatus.ReceiptExists){
 throw 'A development hosts transaction is already active.'
}
if(Test-Path 'HKLM:\SOFTWARE\Reborn\NetworkManifest'){
 throw 'Secure-network HKLM activation is already present.'
}
if(Test-Path $runtime){
 throw 'The controlled-host protected runtime already exists.'
}
$candidateActual=(Get-FileHash $candidate -Algorithm SHA256).Hash
$checksActual=(Get-FileHash $nativeChecks -Algorithm SHA256).Hash
if($candidateActual -cne $pins[1][1] -or $checksActual -cne $pins[2][1]){
 throw 'Native output changed after reproducibility checks.'
}
$release=Get-RebornControlledHostManagedReleaseSet $managed
$managedSetSha=$release.SetSha256
$serverSha=(Get-FileHash $server -Algorithm SHA256).Hash
$optionsSha=(Get-FileHash $options -Algorithm SHA256).Hash
[pscustomobject]@{
 Result='OfflineGatesPassed'
 ManagedReleaseSetSha256=$managedSetSha
 ServerSha256=$serverSha
 OptionsSha256=$optionsSha
 CandidateSha256=$candidateActual
 NativeChecksSha256=$checksActual
 HostsSha256=$expectedHosts
}|Format-List
```
