[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',

    [ValidateSet('Apply', 'Revert', 'Status')]
    [string]$Mode = 'Status',

    [string]$BackupRoot = (Join-Path $PSScriptRoot '..\backups')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$definitions = @(
    [pscustomobject]@{
        Locale = 'en_us'
        Stock = '6D1583BD8A8FAEE5F7609D05717D436D6354414CE4C5C0F58E84EE43EF0ECC33'
        Patched = '7EE7DF86612E8786E1162CA2F71E7445B1A290A0CC540209565DCDEB586842AE'
    },
    [pscustomobject]@{
        Locale = 'zh_cn'
        Stock = 'EECE12008019B9F0CC1AABB54214EDADC48471B3BF7932E8A82B0E71A5CD6A12'
        Patched = '1E3AE6AD950CBA8FB33F075ECA017E44CBEEA67BA9E5AD6C27EBCE6C02A96F8B'
    }
)

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Read-StrictUtf8([string]$Path) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF
    $offset = if ($hasBom) { 3 } else { 0 }
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    [pscustomobject]@{
        Text = $encoding.GetString(
            $bytes,
            $offset,
            $bytes.Length - $offset)
        HasBom = $hasBom
    }
}

function Write-StrictUtf8(
    $File,
    [string]$Path,
    [string]$Text
) {
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    [byte[]]$body = $encoding.GetBytes($Text)
    $offset = if ($File.HasBom) { 3 } else { 0 }
    [byte[]]$bytes = [byte[]]::new($body.Length + $offset)
    if ($File.HasBom) {
        $bytes[0] = 0xEF
        $bytes[1] = 0xBB
        $bytes[2] = 0xBF
    }
    [Array]::Copy($body, 0, $bytes, $offset, $body.Length)
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Replace-Exact(
    [string]$Text,
    [string]$Before,
    [string]$After,
    [string]$Label
) {
    $beforeCount = [regex]::Matches(
        $Text,
        [regex]::Escape($Before)).Count
    $afterCount = [regex]::Matches(
        $Text,
        [regex]::Escape($After)).Count
    if ($beforeCount -ne 1 -or $afterCount -ne 0) {
        throw "$Label is missing, duplicated, or partially patched " +
            "(source=$beforeCount target=$afterCount)."
    }
    $Text.Replace($Before, $After)
}

function Get-EnglishPairs {
    @(
        [pscustomobject]@{
            Label = 'English Seal policy page'
            Stock = 'NF_LO_PET15 = "When sealing a pet for trade, please note the following: 1. A sealing tool called |cff39D8B8Seal Jade (empty).|cffffffff 2. |cff39D8B8A pet bound to its owner|cffffffff cannot be sealed. 3. Before sealing, make sure your bag has at least |cff39D8B81 empty slot.|cffffffff 4. The sealing process terminates any soul contact agreement."'
            Patched = 'NF_LO_PET15 = "When sealing a pet, please note the following: 1. You need a |cff39D8B8Seal Jade (Empty)|cffffffff. 2. A bound pet is sealed into a |cff39D8B8bound, non-tradable|cffffffff packed jade. An unbound pet is sealed into a |cff39D8B8tradable|cffffffff packed jade; only a tradable packed jade can transfer its pet to another player. 3. One empty jade transforms in place and needs no free bag slot. A stack needs at least |cff39D8B81 empty bag slot|cffffffff for its packed output. 4. Sealing terminates any Soul Contract."'
        },
        [pscustomobject]@{
            Label = 'English Seal success page'
            Stock = 'NF_LO_PET1053 = "Successfully sealed! You have received a|cff39D8B8Seal Jade (sealed)|cffffffff."'
            Patched = 'NF_LO_PET1053 = "Successfully sealed! You received a |cff39D8B8Seal Jade (Packed)|cffffffff. Its tradability matches the sealed pet; a bound packed jade cannot be traded."'
        },
        [pscustomobject]@{
            Label = 'English former-rule replay page'
            Stock = 'NF_L0_PET1072 ="I''m sorry! The pet summoned|cff39D8B8has been bound to you|cffffffff, therefore this action cannot be performed."'
            Patched = 'NF_L0_PET1072 ="This request used the former sealing rule. Bound pets can now be sealed into bound, non-tradable packed jades. Please try again."'
        }
    )
}

function Expand-UnicodeLiteral([string]$Value) {
    [regex]::Replace(
        $Value,
        '\\u([0-9A-Fa-f]{4})',
        {
            param($match)
            [char][Convert]::ToUInt16($match.Groups[1].Value, 16)
        })
}

function Get-ChinesePairs {
    $stockPolicy = Expand-UnicodeLiteral 'NF_LO_PET15 = "   \u9009\u62e9\u5c01\u5370\u5ba0\u7269\uff0c\u9700\u8981\u6ce8\u610f\u5982\u4e0b\u51e0\u70b9\uff1a\n1\u3001\u5c01\u5370\u9700\u8981\u9053\u5177|cff39D8B8<\u4ed9\u5ba0\u7075\u7389\uff08\u7a7a\uff09>|cffffffff\n2\u3001|cff39D8B8\u5df2\u548c\u4e3b\u4eba\u7ed1\u5b9a|cffffffff\u7684\u5ba0\u7269\u65e0\u6cd5\u5c01\u5370\n3\u3001\u5c01\u5370\u65f6\u8bf7\u786e\u8ba4\u81f3\u5c11\u6709|cff39D8B8\u4e00\u4e2a\u5305\u88f9\u7a7a\u4f4d|cffffffff\n4\u3001\u5c01\u5370\u540e\u7684\u5ba0\u7269\u5c06\u5931\u53bb\u7075\u9b42\u5951\u7ea6\u7684\u7b7e\u8ba2"'
    $patchedPolicy = Expand-UnicodeLiteral 'NF_LO_PET15 = "   \u9009\u62e9\u5c01\u5370\u5ba0\u7269\uff0c\u9700\u8981\u6ce8\u610f\u5982\u4e0b\u51e0\u70b9\uff1a\n1\u3001\u5c01\u5370\u9700\u8981\u9053\u5177|cff39D8B8<\u4ed9\u5ba0\u7075\u7389\uff08\u7a7a\uff09>|cffffffff\n2\u3001\u5df2\u7ed1\u5b9a\u7684\u5ba0\u7269\u4f1a\u5c01\u5165|cff39D8B8\u7ed1\u5b9a\u4e14\u4e0d\u53ef\u4ea4\u6613|cffffffff\u7684\u7075\u7389\uff1b\u672a\u7ed1\u5b9a\u7684\u5ba0\u7269\u4f1a\u5c01\u5165|cff39D8B8\u53ef\u4ea4\u6613|cffffffff\u7684\u7075\u7389\uff0c\u53ea\u6709\u53ef\u4ea4\u6613\u7075\u7389\u624d\u80fd\u5c06\u5ba0\u7269\u8f6c\u7ed9\u5176\u4ed6\u73a9\u5bb6\n3\u3001\u4e00\u679a\u7a7a\u7075\u7389\u4f1a\u539f\u4f4d\u53d8\u4e3a\u5df2\u5c01\u5370\u7075\u7389\uff0c\u4e0d\u9700\u8981\u7a7a\u5305\u88f9\u4f4d\uff1b\u7a7a\u7075\u7389\u6210\u53e0\u65f6\uff0c\u5305\u88f9\u81f3\u5c11\u9700\u8981\u4e00\u4e2a\u7a7a\u4f4d\u6765\u653e\u7f6e\u5c01\u5370\u540e\u7684\u7075\u7389\n4\u3001\u5c01\u5370\u5c06\u89e3\u9664\u5ba0\u7269\u7684\u7075\u9b42\u5951\u7ea6"'
    $stockSuccess = Expand-UnicodeLiteral 'NF_LO_PET1053 = "\u5c01\u5370\u6210\u529f\uff01\u83b7\u5f97\u9053\u5177:|cff39D8B8<\u4ed9\u5ba0\u7075\u7389(\u5df2\u5c01\u5370)>|cffffffff\uff01"'
    $patchedSuccess = Expand-UnicodeLiteral 'NF_LO_PET1053 = "\u5c01\u5370\u6210\u529f\uff01\u83b7\u5f97\u9053\u5177:|cff39D8B8<\u4ed9\u5ba0\u7075\u7389(\u5df2\u5c01\u5370)>|cffffffff\uff01\u7075\u7389\u7684\u7ed1\u5b9a\u72b6\u6001\u4e0e\u5ba0\u7269\u4e00\u81f4\uff1b\u7ed1\u5b9a\u7075\u7389\u4e0d\u53ef\u4ea4\u6613\u3002"'
    $stockReplay = Expand-UnicodeLiteral 'NF_L0_PET1072 ="\u771f\u9057\u64bc\uff0c\u4f60\u5524\u51fa\u7684\u5ba0\u7269|cff39D8B8\u5df2\u548c\u4f60\u7ed1\u5b9a|cffffffff\uff0c\u65e0\u6cd5\u8fdb\u884c\u6b64\u9879\u64cd\u4f5c\u3002"'
    $patchedReplay = Expand-UnicodeLiteral 'NF_L0_PET1072 ="\u6b64\u8bf7\u6c42\u4f7f\u7528\u4e86\u65e7\u7248\u5c01\u5370\u89c4\u5219\u3002\u7ed1\u5b9a\u5ba0\u7269\u73b0\u5728\u53ef\u4ee5\u5c01\u5165\u7ed1\u5b9a\u4e14\u4e0d\u53ef\u4ea4\u6613\u7684\u7075\u7389\uff0c\u8bf7\u91cd\u8bd5\u3002"'
    @(
        [pscustomobject]@{
            Label = 'Chinese Seal policy page'
            Stock = $stockPolicy
            Patched = $patchedPolicy
        },
        [pscustomobject]@{
            Label = 'Chinese Seal success page'
            Stock = $stockSuccess
            Patched = $patchedSuccess
        },
        [pscustomobject]@{
            Label = 'Chinese former-rule replay page'
            Stock = $stockReplay
            Patched = $patchedReplay
        }
    )
}

function Convert-SealPolicy(
    [string]$Text,
    [string]$Locale,
    [string]$TargetState
) {
    $pairs = if ($Locale -eq 'en_us') {
        Get-EnglishPairs
    }
    else {
        Get-ChinesePairs
    }
    foreach ($pair in $pairs) {
        $before = if ($TargetState -eq 'Patched') {
            $pair.Stock
        }
        else {
            $pair.Patched
        }
        $after = if ($TargetState -eq 'Patched') {
            $pair.Patched
        }
        else {
            $pair.Stock
        }
        $Text = Replace-Exact $Text $before $after $pair.Label
    }
    $Text
}

function Assert-OriginClosed {
    $liveRoot = [IO.Path]::GetFullPath('C:\Godswar Origin')
    $targetRoot = [IO.Path]::GetFullPath($ClientRoot)
    if (-not $targetRoot.Equals(
            $liveRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        return
    }
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
        try {
            throw 'Close Origin.exe before changing the pet Seal policy.'
        }
        finally {
            $process.Dispose()
        }
    }
}

$records = foreach ($definition in $definitions) {
    $path = Join-Path $ClientRoot (
        "Localization\$($definition.Locale)\UI\Base\LuaText.lua")
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Seal policy resource is missing: $path"
    }
    $hash = Get-Sha256 $path
    $state = if ($hash -eq $definition.Stock) {
        'Stock'
    }
    elseif ($hash -eq $definition.Patched) {
        'Patched'
    }
    else {
        throw "Unsupported Seal policy resource (SHA-256 $hash): $path"
    }
    [pscustomobject]@{
        Definition = $definition
        Path = $path
        State = $state
    }
}

$states = @($records.State | Select-Object -Unique)
if ($states.Count -ne 1) {
    throw 'Seal policy client resources are in a mixed state.'
}
$current = $states[0]
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Status = if ($current -eq 'Patched') { 'Patched' } else { 'Ready' }
        BoundPetPackedJade = if ($current -eq 'Patched') {
            'BoundNonTradable'
        }
        else {
            'FormerRuleRejected'
        }
        Resources = $records.Count
    }
    return
}

Assert-OriginClosed
$target = if ($Mode -eq 'Apply') { 'Patched' } else { 'Stock' }
if ($current -eq $target) {
    [pscustomobject]@{
        Status = "Already $($target.ToLowerInvariant())"
        BoundPetPackedJade = if ($target -eq 'Patched') {
            'BoundNonTradable'
        }
        else {
            'FormerRuleRejected'
        }
    }
    return
}

$backupDirectory = Join-Path $BackupRoot (
    'pet-seal-binding-policy-' + $Mode + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$staged = @()
try {
    foreach ($record in $records) {
        $backup = Join-Path $backupDirectory (
            "$($record.Definition.Locale)-LuaText.lua")
        $stage = "$($record.Path).stage-$([Guid]::NewGuid().ToString('N'))"
        Copy-Item -LiteralPath $record.Path -Destination $backup
        if ((Get-Sha256 $backup) -ne (Get-Sha256 $record.Path)) {
            throw "Backup verification failed: $($record.Path)"
        }
        $file = Read-StrictUtf8 $record.Path
        $output = Convert-SealPolicy `
            $file.Text $record.Definition.Locale $target
        Write-StrictUtf8 $file $stage $output
        $expected = if ($target -eq 'Patched') {
            $record.Definition.Patched
        }
        else {
            $record.Definition.Stock
        }
        if ((Get-Sha256 $stage) -ne $expected) {
            throw "Staged Seal policy hash is not exact: $($record.Path)"
        }
        $staged += [pscustomobject]@{
            Path = $record.Path
            Backup = $backup
            Stage = $stage
            Expected = $expected
        }
    }
    foreach ($record in $staged) {
        Move-Item -LiteralPath $record.Stage `
            -Destination $record.Path -Force
    }
    foreach ($record in $staged) {
        if ((Get-Sha256 $record.Path) -ne $record.Expected) {
            throw "Installed Seal policy hash is not exact: $($record.Path)"
        }
    }
}
catch {
    $failure = $_
    foreach ($record in $staged) {
        if (Test-Path -LiteralPath $record.Backup -PathType Leaf) {
            Copy-Item -LiteralPath $record.Backup `
                -Destination $record.Path -Force
        }
        if (Test-Path -LiteralPath $record.Stage -PathType Leaf) {
            Remove-Item -LiteralPath $record.Stage -Force
        }
    }
    throw $failure
}

[pscustomobject]@{
    Status = if ($Mode -eq 'Apply') { 'Patched' } else { 'Reverted' }
    BoundPetPackedJade = if ($target -eq 'Patched') {
        'BoundNonTradable'
    }
    else {
        'FormerRuleRejected'
    }
    Backup = $backupDirectory
}
