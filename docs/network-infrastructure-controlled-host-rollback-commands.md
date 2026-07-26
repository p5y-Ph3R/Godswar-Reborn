# Controlled-host rollback commands

Close disposable `Origin.exe` and gracefully stop the secure server. Open a
fresh elevated console, run the common block from the
[command reference](network-infrastructure-controlled-host-commands.md), and
set the exact recorded Apply handoff:

```powershell
$applyBackupPath='<EXACT_RECORDED_BUNDLE_BACKUP_PATH>'
$applyReceiptSha='<EXACT_RECORDED_APPLY_RECEIPT_SHA256>'
$applyChecksumSha='<EXACT_RECORDED_APPLY_CHECKSUM_SHA256>'
$inventoryPath='<EXACT_RECORDED_INVENTORY_RECEIPT_PATH>'
$inventorySha='<EXACT_RECORDED_INVENTORY_RECEIPT_SHA256>'
$hostsReceiptPath='<EXACT_RECORDED_HOSTS_RECEIPT_PATH>'
$hostsReceiptSha='<EXACT_RECORDED_HOSTS_RECEIPT_SHA256>'
$hostsBackupPath='<EXACT_RECORDED_HOSTS_BACKUP_PATH>'
$hostsBackupSha='<EXACT_RECORDED_HOSTS_BACKUP_SHA256>'
foreach($authority in @(
 @((Join-Path $applyBackupPath 'receipt.json'),$applyReceiptSha),
 @((Join-Path $applyBackupPath 'receipt.sha256'),$applyChecksumSha),
 @($inventoryPath,$inventorySha),
 @($hostsReceiptPath,$hostsReceiptSha),
 @($hostsBackupPath,$hostsBackupSha)
)){
 if(-not(Test-Path -LiteralPath $authority[0] -PathType Leaf)-or
    (Get-FileHash -LiteralPath $authority[0] -Algorithm SHA256).Hash -cne
     $authority[1].ToUpperInvariant()){
  throw "Recorded rollback authority mismatch: $($authority[0])"
 }
}
$hostsPre=& .\tools\ManageDevelopmentNetworkHosts.ps1 -Mode Status
if($hostsPre.State -cne 'InstalledExact' -or
   -not $hostsPre.ReceiptExists -or
   $hostsPre.ReceiptState -cne 'InstalledExact' -or
   $hostsPre.ReceiptPath -cne $hostsReceiptPath){
 throw 'Recorded hosts rollback authority is not active and exact.'
}
$bundleArgs=@{
 ClientRoot='C:\RebornNetworkAcceptanceClient';CandidatePath=$candidate
 ManifestPath=$manifest;TrustPath=$manifestTrust
 ExpectedCandidateSha256=$pins[1][1];ExpectedChecksSha256=$pins[2][1]
 ExpectedManifestSha256=$pins[3][1];ExpectedTrustSha256=$pins[4][1]
 ClientInventoryReceiptPath=$inventoryPath
 ExpectedClientInventoryReceiptSha256=$inventorySha
}
$bundleRestore=& .\tools\InstallSecureNetworkBundle.ps1 @bundleArgs `
 -Mode Restore -ApplyBackupPath $applyBackupPath -AllowHklmWrite -Confirm:$false
if($bundleRestore.Result -cne 'StockFilesRestored'){throw 'Bundle Restore failed.'}
$bundleStatus=& .\tools\InstallSecureNetworkBundle.ps1 @bundleArgs -Mode Status
if($bundleStatus.State -cne 'Stock' -or
   $bundleStatus.ActivationMode -ne 0 -or
   $bundleStatus.Environment -ne 1 -or
   $bundleStatus.SequenceFloor -ne 1){
 throw 'Client or safe-disabled activation state is not exact.'
}
Import-Module .\tools\SecureNetworkActivationState.psm1 -Force
$activationFinal=Get-RebornActivationState -Provider Hklm
if(-not $activationFinal.Exists -or -not $activationFinal.Complete -or
   -not $activationFinal.ModeExists -or
   -not $activationFinal.EnvironmentExists -or
   -not $activationFinal.SequenceFloorExists -or
   $activationFinal.Mode -ne 0 -or $activationFinal.Environment -ne 1 -or
   $activationFinal.SequenceFloor -ne 1){
 throw 'The safe-disabled HKLM activation state was not retained exactly.'
}
$clientStatus=& .\tools\PrepareControlledHostClient.ps1 -Mode Status `
 -ClientRoot C:\RebornNetworkAcceptanceClient
if($clientStatus.State -cne 'Hardened' -or
   $clientStatus.OriginSha256 -cne '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79' -or
   $clientStatus.NetSha256 -cne '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'){
 throw 'Stock client rollback verification failed.'
}
$hostsRestore=& .\tools\ManageDevelopmentNetworkHosts.ps1 -Mode Restore `
 -AllowHostsWrite -Confirm:$false
$hostsStatus=& .\tools\ManageDevelopmentNetworkHosts.ps1 -Mode Status
if($hostsRestore.Result -notin @('Restored','AlreadyRestored') -or
   $hostsStatus.State -cne 'Absent' -or
   $hostsStatus.ReceiptExists -or
   $hostsRestore.HostsSha256 -cne $expectedHosts -or
   $hostsStatus.HostsSha256 -cne $expectedHosts){
 throw 'Hosts rollback failed.'
}
$trustRemoval=& .\tools\RemoveDevelopmentNetworkTrust.ps1 -Mode Remove `
 -ReceiptPath "$runtime\tls\current-user-trust-receipt.json" `
 -ClientRoot C:\RebornNetworkAcceptanceClient `
 -ClientInventoryReceiptPath $inventoryPath `
 -ExpectedClientInventoryReceiptSha256 $inventorySha `
 -AllowTrustRemoval -Confirm:$false
if($trustRemoval.Result -notin @('Removed','AlreadyAbsent')){
 throw 'Trust cleanup failed.'
}
$keyRemoval=& .\tools\ManageDevelopmentEndpointManifestKeys.ps1 -Mode Remove `
 -ReceiptPath "$runtime\bundle\development-manifest-key-receipt.json" `
 -RuntimeRoot $runtime -ClientRoot C:\RebornNetworkAcceptanceClient `
 -ClientInventoryReceiptPath $inventoryPath `
 -ExpectedClientInventoryReceiptSha256 $inventorySha `
 -AllowKeyRemoval -Confirm:$false
if($keyRemoval.Result -cne 'Removed'){throw 'Manifest-key cleanup failed.'}
$trustFinal=& .\tools\RemoveDevelopmentNetworkTrust.ps1 -Mode Status `
 -ReceiptPath "$runtime\tls\current-user-trust-receipt.json"
$keyFinal=& .\tools\ManageDevelopmentEndpointManifestKeys.ps1 -Mode Status `
 -ReceiptPath "$runtime\bundle\development-manifest-key-receipt.json"
$trustRecord=Get-Content `
 "$runtime\tls\current-user-trust-receipt.json" -Raw|ConvertFrom-Json
$rootStore=[Security.Cryptography.X509Certificates.X509Store]::new(
 [Security.Cryptography.X509Certificates.StoreName]::Root,
 [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
try{
 $rootStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
 $rootMatches=$rootStore.Certificates.Find(
  [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
  [string]$trustRecord.thumbprint,$false).Count
}finally{$rootStore.Dispose()}
$keyReceiptFinal=Get-Content `
 "$runtime\bundle\development-manifest-key-receipt.json" -Raw|ConvertFrom-Json
if($trustFinal.State -cne 'Removed' -or $keyFinal.CurrentExists -or
   $rootMatches -ne 0 -or $keyFinal.NextExists -or
   $keyReceiptFinal.state -cne 'Removed' -or
   -not $keyReceiptFinal.current.removed -or -not $keyReceiptFinal.next.removed){
 throw 'Final trust/key absence gate failed.'
}
```

Now Codex performs the exact
[preimage-guarded header rollback](network-infrastructure-controlled-host-header-rollback.md).

Only after trust/key removal and header restoration, remove runtime last:

```powershell
$runtimeRemoval=& .\tools\RemoveControlledHostServerRuntime.ps1 `
 -ReceiptPath "$runtime\receipt.json" `
 -ClientInventoryReceiptPath $inventoryPath `
 -ExpectedClientInventoryReceiptSha256 $inventorySha `
 -AllowRuntimeRemoval -Confirm:$false
if($runtimeRemoval.Result -cne 'Removed' -or (Test-Path -LiteralPath $runtime)){
 throw 'Final runtime cleanup failed.'
}
$runtimeRemoval|Format-List
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
if((& docker inspect -f '{{.State.Running}}' godswar-server).Trim() -cne 'true' -or
   (& docker inspect -f '{{.State.Health.Status}}' godswar-postgres).Trim() -cne 'healthy'){
 throw 'Original Docker services were not restored.'
}
if(@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue|? `
 LocalPort -in 6599,7443).Count -or
   @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue|? LocalPort -eq 7444).Count){
 throw 'A secure listener remained after rollback.'
}
$raw=@(Get-NetTCPConnection -State Listen -ErrorAction Stop|? `
 LocalPort -in 5998,5999,7000)
foreach($port in 5998,7000){
 $match=@($raw|? LocalPort -eq $port)
 if($match.Count -ne 1 -or $match[0].LocalAddress -cne '127.1.1.110'){
  throw "Original raw listener $port was not restored exactly."
 }
}
if(@($raw|? LocalPort -eq 5999).Count){
 throw 'Unexpected host listener 5999 exists after rollback.'
}
$raw|Sort-Object LocalPort|Format-Table LocalAddress,LocalPort,OwningProcess
```

Retain the protected dump and the displayed external runtime-cleanup receipt
for review. Dropping the acceptance database/client is a separate action.
