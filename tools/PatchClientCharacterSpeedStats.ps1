[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',

    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$BackupRoot,

    [ValidateRange(0, 16)]
    [int]$InternalTestFailAfterWrite = 0,

    [scriptblock]$InternalTestBeforeBackup
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Binary.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Text.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.XmlValidation.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Core.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Layout.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.XmlState.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Lua.ps1')
. (Join-Path $PSScriptRoot 'PatchClientCharacterSpeedStats.Transaction.ps1')

function Test-TargetClientRunning([string]$ExecutablePath) {
    $target = [IO.Path]::GetFullPath($ExecutablePath)
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
        try { $path = $process.Path } catch {
            throw 'Close every Origin.exe process; one executable path could not be verified.'
        }
        if ([string]::IsNullOrWhiteSpace($path)) {
            throw 'Close every Origin.exe process; one executable path is unavailable.'
        }
        if ($path -and [string]::Equals(
                [IO.Path]::GetFullPath($path), $target,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Invoke-InternalWriteCheckpoint {
    $script:transactionWriteCount++
    if ($InternalTestFailAfterWrite -gt 0 -and
        $script:transactionWriteCount -eq $InternalTestFailAfterWrite) {
        throw "Injected character-stat transaction failure after write $script:transactionWriteCount."
    }
}

function Get-PersonalInfoLuaState(
    [string]$Path,
    [string]$Locale,
    [Text.Encoding]$Encoding
) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return 'Original'
    }
    [byte[]]$actual = [IO.File]::ReadAllBytes($Path)
    [byte[]]$legacy = [Text.UTF8Encoding]::new($false, $true).GetBytes(
        (Get-PersonalInfoSpeedLua $Locale))
    [byte[]]$patchedV1 = [Text.UTF8Encoding]::new($false, $true).GetBytes(
        (Get-PersonalInfoStatsLua $Locale $false))
    [byte[]]$patched = [Text.UTF8Encoding]::new($false, $true).GetBytes(
        (Get-PersonalInfoStatsLua $Locale))
    if ($actual.Length -eq $legacy.Length -and
        (Test-RebornBytes $actual 0 $legacy)) {
        return 'Legacy'
    }
    if ($actual.Length -eq $patchedV1.Length -and
        (Test-RebornBytes $actual 0 $patchedV1)) {
        return 'PatchedV1'
    }
    if ($actual.Length -eq $patched.Length -and
        (Test-RebornBytes $actual 0 $patched)) {
        return 'Patched'
    }
    throw "PersonalInfoSpeedStats.lua has unknown content or encoding for $Locale; exact BOM-less UTF-8 is required."
}

function Resolve-CombinedCharacterStatsState(
    [string]$Binary,
    [string]$Xml,
    [string]$Lua,
    [string]$Constellation
) {
    if ($Binary -eq 'Original' -and $Xml -eq 'Original' -and
        $Lua -eq 'Original' -and $Constellation -eq 'Original') {
        return 'Original'
    }
    if ($Binary -eq 'LegacyPatched' -and $Xml -eq 'Original' -and
        $Lua -eq 'Original' -and $Constellation -eq 'Original') {
        return 'LegacyPartial'
    }
    if ($Binary -eq 'LegacyPatched' -and $Xml -eq 'PatchedV1' -and
        $Lua -eq 'Original' -and $Constellation -eq 'Original') {
        return 'PatchedV1'
    }
    if ($Binary -eq 'LegacyPatched' -and
        $Xml -in 'PatchedV2', 'PatchedV3' -and $Lua -eq 'Legacy' -and
        $Constellation -eq 'Original') {
        return $Xml
    }
    if ($Binary -eq 'Original' -and
        $Xml -in 'PatchedSid200', 'PatchedSid200FrameV1' -and
        $Lua -eq 'Patched' -and $Constellation -eq 'Patched') {
        return $Xml
    }
    if ($Binary -eq 'Original' -and $Xml -eq 'PatchedSid200V1' -and
        $Lua -eq 'PatchedV1' -and $Constellation -eq 'Patched') {
        return 'PatchedSid200V1'
    }
    throw 'The character-stat binary, XML, and Lua files are partially applied.'
}

$profile = Get-CharacterStatsBinaryProfile
$clientRootPath = [IO.Path]::GetFullPath($ClientRoot)
$clientExe = Join-Path $clientRootPath 'Origin.exe'
$xmlPaths = [ordered]@{}
$luaPaths = [ordered]@{}
$constellationPaths = [ordered]@{}
foreach ($locale in 'en_us', 'zh_cn') {
    $directory = Join-Path $clientRootPath "Localization\$locale\UI\XML"
    $xmlPaths[$locale] = Join-Path $directory 'PersonalInfoUI.xml'
    $luaPaths[$locale] = Join-Path $directory 'PersonalInfoSpeedStats.lua'
    $constellationPaths[$locale] = Join-Path $directory 'Constellation.lua'
}

if (-not (Test-Path -LiteralPath $clientExe -PathType Leaf)) {
    throw "Origin.exe is missing: $clientExe"
}
foreach ($locale in $xmlPaths.Keys) {
    foreach ($path in $xmlPaths[$locale], $constellationPaths[$locale]) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required $locale client file is missing: $path"
        }
    }
}
if ($Mode -ne 'Status' -and (Test-TargetClientRunning $clientExe)) {
    throw 'Close Origin.exe before changing the character-stat UI.'
}

[byte[]]$data = [IO.File]::ReadAllBytes($clientExe)
$binaryState = Get-CharacterStatsBinaryState $data $profile
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$xmlText = [ordered]@{}
$constellationText = [ordered]@{}
$xmlStates = @()
$luaStates = @()
$constellationStates = @()
foreach ($locale in $xmlPaths.Keys) {
    $xml = [IO.File]::ReadAllText($xmlPaths[$locale], $utf8)
    $xmlText[$locale] = $xml
    $xmlState = Get-PersonalInfoXmlState $xml
    Assert-PersonalInfoLocale $xml $locale $xmlState
    $xmlStates += $xmlState
    $luaStates += Get-PersonalInfoLuaState $luaPaths[$locale] $locale $utf8
    $constellation = [IO.File]::ReadAllText(
        $constellationPaths[$locale], $utf8)
    $constellationText[$locale] = $constellation
    $constellationStates += Get-ConstellationStatsLuaState $constellation
}
if (@($xmlStates | Select-Object -Unique).Count -ne 1 -or
    @($luaStates | Select-Object -Unique).Count -ne 1 -or
    @($constellationStates | Select-Object -Unique).Count -ne 1) {
    throw 'Localized character-stat files do not have a uniform state.'
}
$state = Resolve-CombinedCharacterStatsState $binaryState $xmlStates[0] (
    $luaStates[0]) $constellationStates[0]

if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Mode = $Mode
        Changed = $false
        State = $state
        BinaryState = $binaryState
        Sha256 = (Get-FileHash $clientExe -Algorithm SHA256).Hash
        HookVa = ('0x{0:X8}' -f $profile.HookVa)
        CaveVa = ('0x{0:X8}' -f $profile.CaveVa)
        CaveReserveBytes = $profile.CaveReserveLength
        NpcInteractionSafe = $binaryState -eq 'Original'
        WindowRectangle = if ($state -eq 'PatchedSid200') {
            '100,100,454,652'
        } elseif ($state -eq 'PatchedSid200FrameV1') {
            '100,100,440,652 (frame v1)'
        } elseif ($state -eq 'PatchedSid200V1') {
            '100,100,363,652 (SID200 v1)'
        } else { 'legacy or stock layout' }
        Transport = if ($state -in 'PatchedSid200', 'PatchedSid200V1',
            'PatchedSid200FrameV1') {
            'pull-only ConsEvent SID 200'
        } else { 'none or legacy opcode 10166 hook' }
        Locales = @($xmlPaths.Keys)
    }
    return
}

$targetPatched = $Mode -eq 'Apply'
if (($targetPatched -and $state -eq 'PatchedSid200') -or
    (-not $targetPatched -and $state -eq 'Original')) {
    [pscustomobject]@{ Mode = $Mode; Changed = $false; State = $state }
    return
}

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'backups'
}
if ($null -ne $InternalTestBeforeBackup) {
    & $InternalTestBeforeBackup
}
$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'client-character-stats-' + $Mode + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$preStates = [Collections.Generic.List[object]]::new()
$backupExe = Join-Path $backupDirectory 'Origin.exe'
Copy-Item -LiteralPath $clientExe -Destination $backupExe
$backupExeHash = Get-FileSha256 $backupExe
$preStates.Add([pscustomobject]@{
        Label = 'Origin.exe'
        Path = $clientExe
        Backup = $backupExe
        Hash = $backupExeHash
        WasAbsent = $false
    }) | Out-Null
$backupXml = [ordered]@{}
$backupLua = [ordered]@{}
$backupConstellation = [ordered]@{}
foreach ($locale in $xmlPaths.Keys) {
    $backupXml[$locale] = Join-Path $backupDirectory (
        "PersonalInfoUI.$locale.xml")
    Copy-Item -LiteralPath $xmlPaths[$locale] -Destination $backupXml[$locale]
    $preStates.Add([pscustomobject]@{
            Label = "PersonalInfoUI.$locale.xml"
            Path = $xmlPaths[$locale]
            Backup = $backupXml[$locale]
            Hash = Get-FileSha256 $backupXml[$locale]
            WasAbsent = $false
        }) | Out-Null
    $backupConstellation[$locale] = Join-Path $backupDirectory (
        "Constellation.$locale.lua")
    Copy-Item -LiteralPath $constellationPaths[$locale] `
        -Destination $backupConstellation[$locale]
    $preStates.Add([pscustomobject]@{
            Label = "Constellation.$locale.lua"
            Path = $constellationPaths[$locale]
            Backup = $backupConstellation[$locale]
            Hash = Get-FileSha256 $backupConstellation[$locale]
            WasAbsent = $false
        }) | Out-Null
    if (Test-Path -LiteralPath $luaPaths[$locale] -PathType Leaf) {
        $backupLua[$locale] = Join-Path $backupDirectory (
            "PersonalInfoSpeedStats.$locale.lua")
        Copy-Item -LiteralPath $luaPaths[$locale] `
            -Destination $backupLua[$locale]
        $preStates.Add([pscustomobject]@{
                Label = "PersonalInfoSpeedStats.$locale.lua"
                Path = $luaPaths[$locale]
                Backup = $backupLua[$locale]
                Hash = Get-FileSha256 $backupLua[$locale]
                WasAbsent = $false
            }) | Out-Null
    } else {
        $backupLua[$locale] = $null
        $preStates.Add([pscustomobject]@{
                Label = "PersonalInfoSpeedStats.$locale.lua"
                Path = $luaPaths[$locale]
                Backup = $null
                Hash = $null
                WasAbsent = $true
            }) | Out-Null
    }
}

$snapshotsByPath = @{}
foreach ($snapshot in $preStates) {
    $snapshotsByPath[$snapshot.Path] = $snapshot
    if ($snapshot.WasAbsent) {
        if (Test-Path -LiteralPath $snapshot.Path -PathType Leaf) {
            throw "$($snapshot.Label) appeared while backups were created."
        }
        continue
    }
    if (-not (Test-Path -LiteralPath $snapshot.Path -PathType Leaf) -or
        (Get-FileSha256 $snapshot.Path) -ne $snapshot.Hash) {
        throw "$($snapshot.Label) changed while backups were created."
    }
    if ((Get-FileSha256 $snapshot.Backup) -ne $snapshot.Hash) {
        throw "$($snapshot.Label) backup verification failed."
    }
}

[byte[]]$backupData = [IO.File]::ReadAllBytes($backupExe)
$latestBinaryState = Get-CharacterStatsBinaryState $backupData $profile
$latestXmlStates = @()
$latestLuaStates = @()
$latestConstellationStates = @()
$latestXmlText = [ordered]@{}
$latestConstellationText = [ordered]@{}
$targetXmlBytes = [ordered]@{}
$targetConstellationBytes = [ordered]@{}
$targetLuaBytes = [ordered]@{}
foreach ($locale in $xmlPaths.Keys) {
    $latestXmlText[$locale] = [IO.File]::ReadAllText(
        $backupXml[$locale], $utf8)
    $xmlSnapshotState = Get-PersonalInfoXmlState $latestXmlText[$locale]
    Assert-PersonalInfoLocale $latestXmlText[$locale] $locale (
        $xmlSnapshotState)
    $latestXmlStates += $xmlSnapshotState
    if ($null -eq $backupLua[$locale]) {
        $latestLuaStates += 'Original'
    } else {
        $latestLuaStates += Get-PersonalInfoLuaState (
            $backupLua[$locale]) $locale $utf8
    }
    $latestConstellationText[$locale] = [IO.File]::ReadAllText(
        $backupConstellation[$locale], $utf8)
    $latestConstellationStates += Get-ConstellationStatsLuaState (
        $latestConstellationText[$locale])
}
if (@($latestXmlStates | Select-Object -Unique).Count -ne 1 -or
    @($latestLuaStates | Select-Object -Unique).Count -ne 1 -or
    @($latestConstellationStates | Select-Object -Unique).Count -ne 1) {
    throw 'Localized character-stat backup files do not have a uniform state.'
}
$latestState = Resolve-CombinedCharacterStatsState $latestBinaryState (
    $latestXmlStates[0]) $latestLuaStates[0] $latestConstellationStates[0]
if ($latestState -ne $state) {
    throw 'Character-stat state changed while verified backups were created.'
}

$expectedXml = if ($targetPatched) { 'PatchedSid200' } else { 'Original' }
$expectedConstellation = if ($targetPatched) { 'Patched' } else { 'Original' }
foreach ($locale in $xmlPaths.Keys) {
    $targetXml = Convert-PersonalInfoXml (
        $latestXmlText[$locale]) $locale $targetPatched
    if ((Get-PersonalInfoXmlState $targetXml) -ne $expectedXml) {
        throw "Generated $locale PersonalInfoUI.xml state is invalid."
    }
    Assert-PersonalInfoLocale $targetXml $locale $expectedXml
    $targetXmlBytes[$locale] = Get-Utf8Bytes $targetXml (
        Test-Utf8Bom $backupXml[$locale])

    $targetConstellation = Convert-ConstellationStatsLua (
        $latestConstellationText[$locale]) $targetPatched
    if ((Get-ConstellationStatsLuaState $targetConstellation) -ne
        $expectedConstellation) {
        throw "Generated $locale Constellation.lua state is invalid."
    }
    $targetConstellationBytes[$locale] = Get-Utf8Bytes (
        $targetConstellation) (Test-Utf8Bom $backupConstellation[$locale])
    $targetLuaBytes[$locale] = Get-Utf8Bytes (
        Get-PersonalInfoStatsLua $locale) $false
}
Restore-CharacterStatsOriginalBinary $backupData $profile
if ((Get-CharacterStatsBinaryState $backupData $profile) -ne 'Original') {
    throw 'Generated Origin.exe state is invalid.'
}

$mutations = [Collections.Generic.List[object]]::new()
$script:transactionWriteCount = 0
if (Test-TargetClientRunning $clientExe) {
    throw 'Origin.exe started while the character-stat transaction was staged.'
}
try {
    if ($latestBinaryState -ne 'Original') {
        $snapshot = $snapshotsByPath[$clientExe]
        Assert-CharacterStatsSnapshotCurrent $snapshot
        $writtenHash = Get-BytesSha256 $backupData
        Add-CharacterStatsMutation $mutations $snapshot $writtenHash $false
        Write-BytesAtomic $clientExe $backupData $snapshot.Hash (
            $snapshot.WasAbsent) -VerifyCurrent
        Invoke-InternalWriteCheckpoint
    }
    foreach ($locale in $xmlPaths.Keys) {
        $snapshot = $snapshotsByPath[$xmlPaths[$locale]]
        Assert-CharacterStatsSnapshotCurrent $snapshot
        $writtenHash = Get-BytesSha256 $targetXmlBytes[$locale]
        Add-CharacterStatsMutation $mutations $snapshot $writtenHash $false
        Write-BytesAtomic $xmlPaths[$locale] $targetXmlBytes[$locale] (
            $snapshot.Hash) $snapshot.WasAbsent -VerifyCurrent
        Invoke-InternalWriteCheckpoint
        $snapshot = $snapshotsByPath[$constellationPaths[$locale]]
        $writtenHash = Get-BytesSha256 $targetConstellationBytes[$locale]
        if ($snapshot.Hash -ne $writtenHash) {
            Assert-CharacterStatsSnapshotCurrent $snapshot
            Add-CharacterStatsMutation $mutations $snapshot $writtenHash $false
            Write-BytesAtomic $constellationPaths[$locale] (
                $targetConstellationBytes[$locale]) $snapshot.Hash (
                $snapshot.WasAbsent) -VerifyCurrent
            Invoke-InternalWriteCheckpoint
        }
        if ($targetPatched) {
            $snapshot = $snapshotsByPath[$luaPaths[$locale]]
            Assert-CharacterStatsSnapshotCurrent $snapshot
            $writtenHash = Get-BytesSha256 $targetLuaBytes[$locale]
            Add-CharacterStatsMutation $mutations $snapshot $writtenHash $false
            Write-BytesAtomic $luaPaths[$locale] $targetLuaBytes[$locale] (
                $snapshot.Hash) $snapshot.WasAbsent -VerifyCurrent
            Invoke-InternalWriteCheckpoint
        } elseif (Test-Path -LiteralPath $luaPaths[$locale] -PathType Leaf) {
            $snapshot = $snapshotsByPath[$luaPaths[$locale]]
            Assert-CharacterStatsSnapshotCurrent $snapshot
            Add-CharacterStatsMutation $mutations $snapshot $null $true
            Remove-Item -LiteralPath $luaPaths[$locale] -Force
            Invoke-InternalWriteCheckpoint
        }
    }
    $verify = & $PSCommandPath -ClientRoot $clientRootPath -Mode Status
    $expectedState = if ($targetPatched) { 'PatchedSid200' } else { 'Original' }
    if ($verify.State -ne $expectedState) {
        throw 'Character-stat post-write verification failed.'
    }
}
catch {
    $originalError = $_
    $rollbackErrors = [Collections.Generic.List[string]]::new()
    for ($index = $mutations.Count - 1; $index -ge 0; $index--) {
        $mutation = $mutations[$index]
        try {
            Restore-CharacterStatsMutation $mutation
        }
        catch {
            $rollbackErrors.Add(
                "$($mutation.Label): $($_.Exception.Message)") | Out-Null
        }
    }
    foreach ($snapshot in $preStates) {
        try {
            if ($snapshot.WasAbsent) {
                if (Test-Path -LiteralPath $snapshot.Path -PathType Leaf) {
                    throw 'expected the pre-transaction file to be absent'
                }
            } elseif (-not (Test-Path -LiteralPath $snapshot.Path -PathType Leaf) -or
                (Get-FileSha256 $snapshot.Path) -ne $snapshot.Hash) {
                throw 'SHA-256 does not match the verified backup'
            }
        }
        catch {
            $rollbackErrors.Add(
                "$($snapshot.Label) verification: $($_.Exception.Message)") |
                Out-Null
        }
    }
    if ($rollbackErrors.Count -gt 0) {
        $message = $originalError.Exception.Message +
            ' Rollback failures: ' + ($rollbackErrors -join '; ')
        throw [InvalidOperationException]::new(
            $message, $originalError.Exception)
    }
    throw $originalError
}

[pscustomobject]@{
    Mode = $Mode
    Changed = $true
    State = if ($targetPatched) { 'PatchedSid200' } else { 'Original' }
    Backup = $backupDirectory
    Sha256 = (Get-FileHash $clientExe -Algorithm SHA256).Hash
    SpeedDisplay = 'effective movement speed from SID 200 v1'
    PenetrationDisplay = 'physical v2 for class 0/1; magical v3 for class 2/3'
}
