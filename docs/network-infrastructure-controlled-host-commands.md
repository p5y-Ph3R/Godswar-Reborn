# Controlled-host acceptance command reference

Use this only with the
[controlled-host acceptance runbook](network-infrastructure-controlled-host-acceptance.md).
Every command is fail-closed and local to the fixed disposable fixture.

## Common variables and pins

Start each console with the required token stated by the relevant gate:

```powershell
Set-Location C:\Reborn
$ErrorActionPreference='Stop'
$dbName='godswar_secure_acceptance_20260727_011921'
$fixture='C:\Reborn\artifacts\controlled-host-acceptance\20260727-011921'
$tls=Join-Path $fixture 'tls'
$dump='C:\Reborn\artifacts\controlled-host-acceptance\20260726-141154\godswar-20260726-141154.dump'
$managed='C:\Reborn\src\Godswar.Server\bin\Release\net10.0'
$options='C:\Reborn\appsettings.json'
$server=Join-Path $managed 'Godswar.Server.dll'
$pfx=Join-Path $tls 'reborn-development-server.pfx'
$rootCer=Join-Path $tls 'reborn-development-root.cer'
$trustSource=Join-Path $tls 'current-user-trust-receipt.json'
$certSecret=Join-Path $tls 'certificate-password.dpapi.clixml'
$pgSecret=Join-Path $tls 'postgres-connection.dpapi.clixml'
$manifest='C:\Reborn\artifacts\secure-network\RebornNetwork.gwem'
$manifestTrust='C:\Reborn\artifacts\secure-network\development-manifest-trust.json'
$nextTrust='C:\Reborn\artifacts\secure-network\development-manifest-next-trust.json'
$keyReceiptSource='C:\Reborn\artifacts\secure-network\development-manifest-key-receipt.json'
$candidate='C:\Reborn\client\network-shim\bin\Release\Win32\Net.dll'
$nativeChecks='C:\Reborn\client\network-shim\bin\Release\Win32\Godswar.NetShim.Checks.exe'
$header='C:\Reborn\client\network-shim\src\SecureClientManifestDevelopmentKeys.generated.h'
$runtime='C:\ProgramData\RebornSecureNetworkRuntime\20260727-011921'
$evidence=Join-Path $fixture 'server-evidence'
$expectedHosts='96B8714EAEB906C50EA8282A44C5A0A239BCAC1F723A89B5C4476957B496ADA3'
$pins=@(
 @($dump,'7EC9775B2F6F08361F606FEC2968623573A632D2FCD02EBDD12327B6407F4AAE'),
 @($candidate,'0328D7EA84B68DD8D5A1DF7B0A291B9DC17EF3337C0114A7A396283FC4EF852B'),
 @($nativeChecks,'D583309B921C7AA795F7A044F096762703AA2DB376A1D07B9EEB4F44312208D0'),
 @($manifest,'3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C'),
 @($manifestTrust,'A32B40917A01D510504528F5D6996F918A6A218991B64C50234ED84C75C75C07'),
 @($nextTrust,'582C252D31DE3361157C7625FB21DD104F907EA762FB77044E1CCEF2EA51E571'),
 @($keyReceiptSource,'A5C286694AA1361A8A18E9E42594A4D56563F9E4DD0563D5A464DC0941B39B50'),
 @($pfx,'C498666CC8D6ECF09DF92C217169A6F2CDA788DEDA60E5DD17B1EA9CA6C6BC0F'),
 @($rootCer,'911E3CF444B631AAB9EDCC5980DF65243CAAC42B9000C5E2410C7DADFEB54DED'),
 @($trustSource,'57FF8F9D9A5701E6AB3E79C243F69D412DE30BA085F9DAD0EED473208748BCF4'),
 @($certSecret,'58B26CCF6AE4B3311571B48F9A788B03245D8C11959BCFC79D840C9C74719A9D'),
 @($pgSecret,'C38710F43DBD73A164746F6530FF8B556F863D8D393094B8E577E932C84CABEE'),
 @($header,'D72E7E218E2DD6D1730C1A5194965600DEBECDC9232BCF3DAA86494D863519D1')
)
foreach($pin in $pins){
 if(-not(Test-Path -LiteralPath $pin[0] -PathType Leaf)-or
    (Get-FileHash -LiteralPath $pin[0] -Algorithm SHA256).Hash -cne $pin[1]){
   throw "Pinned fixture mismatch: $($pin[0])"
 }
}
Import-Module .\tools\ControlledHostManagedRelease.psm1 -Force
$release=Get-RebornControlledHostManagedReleaseSet $managed
$managedSetSha=$release.SetSha256
$serverSha=(Get-FileHash $server -Algorithm SHA256).Hash
$optionsSha=(Get-FileHash $options -Algorithm SHA256).Hash
$runtimeArgs=@{
 ExpectedDatabaseName=$dbName;ManagedReleaseDirectory=$managed;OptionsPath=$options
 CertificatePath=$pfx;RootCertificatePath=$rootCer;TrustReceiptPath=$trustSource
 ManifestPath=$manifest;ManifestTrustPath=$manifestTrust
 ManifestKeyReceiptPath=$keyReceiptSource;NativeChecksPath=$nativeChecks
 CertificatePasswordSecretPath=$certSecret;PostgresConnectionSecretPath=$pgSecret
 ExpectedManagedReleaseSetSha256=$managedSetSha;ExpectedOptionsSha256=$optionsSha
 ExpectedCertificateSha256=$pins[7][1];ExpectedRootCertificateSha256=$pins[8][1]
 ExpectedTrustReceiptSha256=$pins[9][1];ExpectedCertificateSecretSha256=$pins[10][1]
 ExpectedPostgresSecretSha256=$pins[11][1];ExpectedManifestSha256=$pins[3][1]
 ExpectedManifestTrustSha256=$pins[4][1];ExpectedManifestKeyReceiptSha256=$pins[6][1]
 ExpectedNativeChecksSha256=$pins[2][1]
}
```

## Gate 1: ordinary-token offline verification

Run the exact
[offline-gate commands](network-infrastructure-controlled-host-offline-gates.md)
after the common block above. Do not continue if any check is skipped or
fails.

## Gate 2: protected preparation

Open a fresh elevated `powershell.exe -NoLogo -NoProfile`, run the common
block, then:

```powershell
$prep=& .\tools\PrepareControlledHostAcceptance.ps1 `
 -DatabaseBackupPath $dump -ExpectedDatabaseBackupSha256 $pins[0][1] `
 -ClientRoot C:\RebornNetworkAcceptanceClient `
 -EvidenceDirectory (Join-Path $fixture 'client-acl') `
 -AllowPreparation -Confirm:$false
if($prep.Result -cne 'Prepared' -or -not $prep.ClientInventoryReceiptPath -or
   -not $prep.ClientInventoryReceiptSha256){throw 'Preparation handoff failed.'}
$runtimePrep=& .\tools\PrepareControlledHostServerRuntime.ps1 @runtimeArgs `
 -Mode Apply -AllowRuntimeWrite -Confirm:$false
if($runtimePrep.Result -notin @('Protected','AlreadyProtected') -or
   $runtimePrep.RuntimeRoot -cne $runtime){throw 'Runtime preparation failed.'}
$prep,$runtimePrep,[pscustomobject]@{
 ManagedReleaseSetSha256=$managedSetSha
 ServerSha256=$serverSha
 OptionsSha256=$optionsSha
}|Format-List
```

Record the output, close the elevated console, and stop for operator approval
before the mandatory reboot.

## Gate 3: post-reboot ordinary-token validation

Open a fresh ordinary `powershell.exe -NoLogo -NoProfile`, run the common
block, then:

```powershell
$id=[Security.Principal.WindowsIdentity]::GetCurrent()
$wp=[Security.Principal.WindowsPrincipal]::new($id)
if($wp.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
 throw 'Post-reboot validation must use an ordinary token.'
}
$clientStatus=& .\tools\PrepareControlledHostClient.ps1 -Mode Status `
 -ClientRoot C:\RebornNetworkAcceptanceClient
if($clientStatus.State -cne 'Hardened' -or $clientStatus.Elevated){
 throw 'Client ACL gate failed.'
}
& .\tools\TestControlledHostClientAcl.ps1
$dbStatus=& .\tools\ProtectControlledHostDatabaseBackup.ps1 -Mode Status `
 -SourcePath $dump -ExpectedSha256 $pins[0][1]
if($dbStatus.State -cne 'Protected' -or $dbStatus.Elevated){
 throw 'Protected database gate failed.'
}
$runtimeStatus=& .\tools\PrepareControlledHostServerRuntime.ps1 @runtimeArgs `
 -Mode Status
if($runtimeStatus.State -cne 'Protected' -or $runtimeStatus.Elevated){
 throw 'Protected runtime gate failed.'
}
& .\tools\TestControlledHostInstalledCertificateValidation.ps1 `
 -ServerAssemblyPath "$runtime\managed\Godswar.Server.dll" `
 -CertificatePath "$runtime\tls\reborn-development-server.pfx" `
 -RootCertificatePath "$runtime\tls\reborn-development-root.cer" `
 -TrustReceiptPath "$runtime\tls\current-user-trust-receipt.json" `
 -CertificatePasswordSecretPath "$runtime\tls\certificate-password.dpapi.clixml"
$trustStatus=& .\tools\RemoveDevelopmentNetworkTrust.ps1 -Mode Status `
 -ReceiptPath "$runtime\tls\current-user-trust-receipt.json"
if($trustStatus.State -cne 'Installed' -or -not $trustStatus.InstalledByScript){
 throw 'Staged trust authority is not Installed.'
}
$keyStatus=& .\tools\ManageDevelopmentEndpointManifestKeys.ps1 `
 -Mode ValidateReceipt `
 -ReceiptPath "$runtime\bundle\development-manifest-key-receipt.json" `
 -RuntimeRoot $runtime
if($keyStatus.Result -cne 'Validated' -or
   $keyStatus.ReceiptState -cne 'Issued' -or
   -not $keyStatus.PublicCoordinatesBound -or
   $keyStatus.PrivateKeysExportable){throw 'Manifest key gate failed.'}
$dbExists=(& docker exec godswar-postgres psql -U godswar -d postgres -tAc `
 "SELECT 1 FROM pg_database WHERE datname='$dbName'").Trim()
if($LASTEXITCODE -ne 0 -or $dbExists -cne '1'){
 throw 'Acceptance database is absent.'
}
if(@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue|? `
 LocalPort -in 6599,7443).Count -or
   @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue|? LocalPort -eq 7444).Count){
 throw 'A secure listener conflicts before activation.'
}
$hostsStatus=& .\tools\ManageDevelopmentNetworkHosts.ps1 -Mode Status
if($hostsStatus.State -cne 'Absent' -or $hostsStatus.ReceiptExists -or
   $hostsStatus.HostsSha256 -cne $expectedHosts){
 throw 'Original hosts bytes drifted after protected preparation.'
}
if(Test-Path 'HKLM:\SOFTWARE\Reborn\NetworkManifest'){
 throw 'HKLM activation appeared after protected preparation.'
}
if((& docker inspect -f '{{.State.Running}}' godswar-server).Trim() -cne 'true' -or
   (& docker inspect -f '{{.State.Running}}' godswar-postgres).Trim() -cne 'true' -or
   (& docker inspect -f '{{.State.Health.Status}}' godswar-postgres).Trim() -cne
    'healthy'){
 throw 'Original Docker services did not survive reboot exactly.'
}
$raw=@(Get-NetTCPConnection -State Listen -ErrorAction Stop|? `
 LocalPort -in 5998,5999,7000)
foreach($port in 5998,7000){
 $match=@($raw|? LocalPort -eq $port)
 if($match.Count -ne 1 -or $match[0].LocalAddress -cne '127.1.1.110'){
  throw "Post-reboot raw listener $port is not exact."
 }
}
if(@($raw|? LocalPort -eq 5999).Count){
 throw 'Unexpected post-reboot raw listener 5999 exists.'
}
```

## Gate 4: activation

Open a fresh elevated console and run the common block. Stop only the raw
server, then apply hosts and bundle serially:

```powershell
$hostsPre=& .\tools\ManageDevelopmentNetworkHosts.ps1 -Mode Status
if($hostsPre.State -cne 'Absent' -or $hostsPre.ReceiptExists -or
   $hostsPre.HostsSha256 -cne $expectedHosts){
 throw 'Original hosts bytes drifted immediately before activation.'
}
Import-Module .\tools\SecureNetworkActivationState.psm1 -Force
$activationPre=Get-RebornActivationState -Provider Hklm
if($activationPre.Exists -and
   (-not $activationPre.Complete -or $activationPre.Mode -ne 0 -or
    $activationPre.Environment -ne 1 -or
    $activationPre.SequenceFloor -ne 1)){
 throw 'HKLM activation is neither absent nor exact safe-disabled state.'
}
if((& docker inspect -f '{{.State.Running}}' godswar-server).Trim() -cne 'true' -or
   (& docker inspect -f '{{.State.Running}}' godswar-postgres).Trim() -cne 'true' -or
   (& docker inspect -f '{{.State.Health.Status}}' godswar-postgres).Trim() -cne
    'healthy'){
 throw 'Original Docker services drifted immediately before activation.'
}
$rawPre=@(Get-NetTCPConnection -State Listen -ErrorAction Stop|? `
 LocalPort -in 5998,5999,7000)
foreach($port in 5998,7000){
 $match=@($rawPre|? LocalPort -eq $port)
 if($match.Count -ne 1 -or $match[0].LocalAddress -cne '127.1.1.110'){
  throw "Pre-activation raw listener $port is not exact."
 }
}
if(@($rawPre|? LocalPort -eq 5999).Count){
 throw 'Unexpected pre-activation raw listener 5999 exists.'
}
& docker compose stop server
if($LASTEXITCODE){throw 'Stopping Docker server failed.'}
if((& docker inspect -f '{{.State.Running}}' godswar-server).Trim() -cne 'false'){
 throw 'Raw server is still running.'
}
if((& docker inspect -f '{{.State.Running}}' godswar-postgres).Trim() -cne 'true' -or
   (& docker inspect -f '{{.State.Health.Status}}' godswar-postgres).Trim() -cne 'healthy'){
 throw 'PostgreSQL is not healthy.'
}
if(@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue|? `
 LocalPort -in 5998,5999,7000,6599,7443).Count -or
   @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue|? LocalPort -eq 7444).Count){
 throw 'Acceptance ports are not free.'
}
Import-Module .\tools\ControlledHostClientInventoryReceipt.psm1 -Force
$inventory=Read-RebornControlledHostActiveClientInventoryReceipt
$inventoryPath=$inventory.ReceiptPath
$inventorySha=$inventory.ReceiptSha256
$hostsApply=& .\tools\ManageDevelopmentNetworkHosts.ps1 -Mode Apply `
 -AllowHostsWrite -Confirm:$false
if($hostsApply.Result -cne 'Applied'){throw 'Hosts Apply failed.'}
$bundleArgs=@{
 ClientRoot='C:\RebornNetworkAcceptanceClient';CandidatePath=$candidate
 ManifestPath=$manifest;TrustPath=$manifestTrust
 ExpectedCandidateSha256=$pins[1][1];ExpectedChecksSha256=$pins[2][1]
 ExpectedManifestSha256=$pins[3][1];ExpectedTrustSha256=$pins[4][1]
 ClientInventoryReceiptPath=$inventoryPath
 ExpectedClientInventoryReceiptSha256=$inventorySha
}
$bundleApply=& .\tools\InstallSecureNetworkBundle.ps1 @bundleArgs -Mode Apply `
 -AllowHklmWrite -ControlledHostSocketChecks -Confirm:$false
if($bundleApply.Result -cne 'InstalledExact' -or -not $bundleApply.BackupPath){
 throw 'Bundle Apply did not return rollback authority.'
}
$handoff=[pscustomobject]@{
 ApplyBackupPath=$bundleApply.BackupPath
 ApplyReceiptSha256=(Get-FileHash `
   (Join-Path $bundleApply.BackupPath 'receipt.json') -Algorithm SHA256).Hash
 ApplyChecksumSha256=(Get-FileHash `
   (Join-Path $bundleApply.BackupPath 'receipt.sha256') -Algorithm SHA256).Hash
 ClientInventoryReceiptPath=$inventoryPath
 ClientInventoryReceiptSha256=$inventorySha
 HostsReceiptPath=$hostsApply.ReceiptPath
 HostsReceiptSha256=(Get-FileHash $hostsApply.ReceiptPath -Algorithm SHA256).Hash
 HostsBackupPath=$hostsApply.BackupPath
 HostsBackupSha256=(Get-FileHash $hostsApply.BackupPath -Algorithm SHA256).Hash
}
$bundleApply,$hostsApply,$handoff|Format-List
```

Record the complete displayed handoff object in the acceptance record. On any
failure after hosts Apply, restore completed operations and restart Docker.
Close the elevated console only after recording those rollback authorities.

If any Gate 4 command fails before the complete handoff is displayed, do not
close that elevated console. Run this pre-handoff recovery block. It restores a
successfully applied bundle when `$bundleApply` contains its rollback authority,
restores hosts only when the checked active receipt is exact, then restarts and
revalidates the original raw server:

```powershell
if($null -ne $bundleApply -and
   $bundleApply.Result -ceq 'InstalledExact' -and
   $bundleApply.BackupPath){
 $bundleRestore=& .\tools\InstallSecureNetworkBundle.ps1 @bundleArgs `
  -Mode Restore -ApplyBackupPath $bundleApply.BackupPath `
  -AllowHklmWrite -Confirm:$false
 if($bundleRestore.Result -cne 'StockFilesRestored'){
  throw 'Pre-handoff bundle recovery failed.'
 }
}
$hostsStatus=& .\tools\ManageDevelopmentNetworkHosts.ps1 -Mode Status
if($hostsStatus.State -ceq 'InstalledExact' -and
   $hostsStatus.ReceiptExists -and
   $hostsStatus.ReceiptState -ceq 'InstalledExact'){
 $hostsRestore=& .\tools\ManageDevelopmentNetworkHosts.ps1 -Mode Restore `
  -AllowHostsWrite -Confirm:$false
 if($hostsRestore.Result -notin @('Restored','AlreadyRestored')){
  throw 'Pre-handoff hosts recovery failed.'
 }
}elseif($hostsStatus.State -cne 'Absent' -or $hostsStatus.ReceiptExists){
 throw 'Hosts state is ambiguous; do not restart or close this console.'
}
& docker compose up -d server
if($LASTEXITCODE){throw 'Raw server restart failed.'}
for($i=0;$i -lt 30;$i++){
 $running=(& docker inspect -f '{{.State.Running}}' godswar-server 2>$null).Trim()
 $probe=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue|? `
  LocalPort -in 5998,5999,7000)
 $rawReady=$running -ceq 'true' -and
  @($probe|? { $_.LocalPort -eq 5998 -and
    $_.LocalAddress -ceq '127.1.1.110' }).Count -eq 1 -and
  @($probe|? { $_.LocalPort -eq 7000 -and
    $_.LocalAddress -ceq '127.1.1.110' }).Count -eq 1 -and
  @($probe|? LocalPort -eq 5999).Count -eq 0
 if($rawReady){break}
 Start-Sleep -Seconds 1
}
if(-not $rawReady){throw 'Original raw server recovery did not validate.'}
if(@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue|? `
   LocalPort -in 6599,7443).Count -or
   @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue|? LocalPort -eq 7444).Count){
 throw 'A secure listener remained after pre-handoff recovery.'
}
```

If bundle or hosts restoration throws, recovery is incomplete and intentionally
stops before restarting Docker. Keep the console open and preserve its output
for exact recovery; do not improvise a raw-server restart over ambiguous state.

## Launcher preflight and foreground run

Open a fresh ordinary console, run the common block, then use hashes from the
staged runtime; not a mutable source directory:

```powershell
Import-Module .\tools\ControlledHostManagedRelease.psm1 -Force
Import-Module .\tools\ControlledHostClientInventoryReceipt.psm1 -Force
$stagedSet=Get-RebornControlledHostManagedReleaseSet "$runtime\managed"
$stagedServerSha=(Get-FileHash "$runtime\managed\Godswar.Server.dll" -Algorithm SHA256).Hash
$stagedOptionsSha=(Get-FileHash "$runtime\appsettings.json" -Algorithm SHA256).Hash
$inventory=Read-RebornControlledHostActiveClientInventoryReceipt
$inventoryPath=$inventory.ReceiptPath
$inventorySha=$inventory.ReceiptSha256
$serverArgs=@{
 ServerAssembly="$runtime\managed\Godswar.Server.dll"
 OptionsPath="$runtime\appsettings.json"
 CertificatePath="$runtime\tls\reborn-development-server.pfx"
 RootCertificatePath="$runtime\tls\reborn-development-root.cer"
 TrustReceiptPath="$runtime\tls\current-user-trust-receipt.json"
 ManifestTrustPath="$runtime\bundle\development-manifest-trust.json"
 ManifestKeyReceiptPath="$runtime\bundle\development-manifest-key-receipt.json"
 NativeChecksPath="$runtime\bundle\Godswar.NetShim.Checks.exe"
 CertificatePasswordSecretPath="$runtime\tls\certificate-password.dpapi.clixml"
 PostgresConnectionSecretPath="$runtime\tls\postgres-connection.dpapi.clixml"
 ClientInventoryReceiptPath=$inventoryPath;EvidenceDirectory=$evidence
 ExpectedServerSha256=$stagedServerSha
 ExpectedManagedReleaseSetSha256=$stagedSet.SetSha256
 ExpectedOptionsSha256=$stagedOptionsSha;ExpectedCandidateSha256=$pins[1][1]
 ExpectedManifestSha256=$pins[3][1];ExpectedCertificateSha256=$pins[7][1]
 ExpectedCertificateSecretSha256=$pins[10][1]
 ExpectedPostgresSecretSha256=$pins[11][1]
 ExpectedRootCertificateSha256=$pins[8][1]
 ExpectedTrustReceiptSha256=$pins[9][1]
 ExpectedManifestTrustSha256=$pins[4][1]
 ExpectedManifestKeyReceiptSha256=$pins[6][1]
 ExpectedNativeChecksSha256=$pins[2][1]
 ExpectedClientInventoryReceiptSha256=$inventorySha
 ExpectedDatabaseName=$dbName;ClientRoot='C:\RebornNetworkAcceptanceClient'
 AllowControlledHostActivation=$true
}
& .\tools\RunControlledHostSecureServer.ps1 @serverArgs -PreflightOnly
& .\tools\RunControlledHostSecureServer.ps1 @serverArgs
```

While it runs, use a second ordinary console:

```powershell
$tcp=@(Get-NetTCPConnection -State Listen -ErrorAction Stop|? `
 LocalPort -in 5998,5999,6599,7000,7443)
$udp=@(Get-NetUDPEndpoint -ErrorAction Stop|? LocalPort -eq 7444)
foreach($port in 6599,7443){
 $match=@($tcp|? LocalPort -eq $port)
 if($match.Count -ne 1 -or $match[0].LocalAddress -cne '127.0.0.1'){
  throw "Secure TCP listener $port is not exact loopback."
 }
}
if($udp.Count -ne 1 -or $udp[0].LocalAddress -cne '127.0.0.1'){
 throw 'Secure UDP listener 7444 is not exact loopback.'
}
if(@($tcp|? LocalPort -in 5998,5999,7000).Count){
 throw 'A raw listener remained during secure acceptance.'
}
$tcp,$udp|Sort-Object LocalPort|Format-Table LocalAddress,LocalPort,OwningProcess
```

Launch only `C:\RebornNetworkAcceptanceClient\Origin.exe`, never the
patcher/launcher.

For the one-shot fault run, gracefully stop, then invoke:

```powershell
& .\tools\RunControlledHostSecureServer.ps1 @serverArgs `
 -EnablePhase4AcceptanceFaults
```

## Mandatory rollback

Follow the exact
[rollback command sequence](network-infrastructure-controlled-host-rollback-commands.md).
