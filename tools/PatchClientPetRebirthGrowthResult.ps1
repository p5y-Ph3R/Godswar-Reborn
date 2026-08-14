[CmdletBinding()]
param(
    [string]$ClientRoot = 'C:\Godswar Origin',

    [ValidateSet('Status', 'Apply', 'Revert')]
    [string]$Mode = 'Status',

    [string]$BackupRoot = (Join-Path $PSScriptRoot '..\backups')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$locales = @('en_us', 'zh_cn')
$relativeLua = 'UI\XML\PetSamsaraUI.lua'
$relativeXml = 'UI\XML\PetSamsaraUI.xml'
$xmlHash =
    '6174EC439D8495EF8CE27CFE018444A1F9068F239BB5414879475FAFF6A2D154'
$xmlLength = 10255
$contracts = @{
    en_us = @{
        StockHash =
            '35B9CD0BE68E240D286681A7804EA1DDC42984BFA8703BE9AB1E16FA7AAB2791'
        StockLength = 7703
        PatchedHash =
            '1EAA3CE171E1AEDE21DD45019FA1051DAA87441757EB13C8E5D14BF302EC3119'
        PatchedLength = 7668
    }
    zh_cn = @{
        StockHash =
            '1EC127A09AF40345B8E6E6DE82F7E3F5CA44F8EF52903EDCECB2AD75035244A3'
        StockLength = 7641
        PatchedHash =
            '891610CC00F5E27DCD3C90A0814A625D839176BD86929E047062108C86109A86'
        PatchedLength = 7606
    }
}

$stockFragment = @'
function Samsera_UpdateTraitBaseCol(state, id, value)
	pElement = uiapi:GetElement(id);
	if 1 == state then
		pElement:SetText(str_random_upate);
	elseif 2 == state then
		if 1 == value then
			pElement:SetText(str_update_1);
		elseif 2 == value then
			pElement:SetText(str_update_2);
		elseif 3 == value then
			pElement:SetText(str_update_3);
		elseif 4 == value then
			pElement:SetText(str_update_4);
		elseif 5 == value then
			pElement:SetText(str_update_5);
		end
	end
end
'@ -replace "`n", "`r`n"

$patchedFragment = @'
function Samsera_UpdateTraitBaseCol(state, id, value)
	pElement = uiapi:GetElement(id);
	if 1 == state then
		pElement:SetText(str_random_upate);
	elseif 2 == state then
		-- 10273 carries exact hundredths; 1..5 mean +0.01..+0.05.
		if type(value) == "number" and value >= 0 and value <= 255 and
			value == math.floor(value) then
			pElement:SetText(string.format("+%.2f", value / 100));
		else
			pElement:SetText(str_random_upate);
		end
	end
end
'@ -replace "`n", "`r`n"

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Get-ExactCount([string]$Text, [string]$Value) {
    [regex]::Matches($Text, [regex]::Escape($Value)).Count
}

function Read-Resource([string]$Path) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or
        $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        throw "Pet Rebirth Lua must retain its UTF-8 BOM: $Path"
    }
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $text = $encoding.GetString($bytes, 3, $bytes.Length - 3)
    if (([regex]::Matches($text, "(?<!`r)`n")).Count -ne 0 -or
        ([regex]::Matches($text, "`r(?!`n)")).Count -ne 0) {
        throw "Pet Rebirth Lua must retain CRLF line endings: $Path"
    }
    [pscustomobject]@{
        Text = $text
        Bytes = $bytes
    }
}

function Write-Resource(
    [string]$Path,
    [string]$Text
) {
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    [byte[]]$body = $encoding.GetBytes($Text)
    [byte[]]$output = [byte[]]::new($body.Length + 3)
    $output[0] = 0xEF
    $output[1] = 0xBB
    $output[2] = 0xBF
    [Array]::Copy($body, 0, $output, 3, $body.Length)
    [IO.File]::WriteAllBytes($Path, $output)
}

function Assert-LuaShape(
    [string]$Text,
    [string]$State,
    [string]$Path
) {
    $stockCount = Get-ExactCount $Text $stockFragment
    $patchedCount = Get-ExactCount $Text $patchedFragment
    if ($State -eq 'Stock') {
        if ($stockCount -ne 1 -or $patchedCount -ne 0) {
            throw "Stock Pet Rebirth result function is partial: $Path"
        }
    }
    elseif ($stockCount -ne 0 -or $patchedCount -ne 1) {
        throw "Patched Pet Rebirth result function is partial: $Path"
    }
    if (([regex]::Matches(
                $Text,
                'function\s+Samsera_UpdateTraitBaseCol\s*\(')).Count -ne 1) {
        throw "Pet Rebirth result function is missing or duplicated: $Path"
    }
}

function Assert-BytesAt(
    [byte[]]$Bytes,
    [int]$Offset,
    [byte[]]$Expected,
    [string]$Label
) {
    if ($Bytes.Length -lt $Offset + $Expected.Length) {
        throw "Origin.exe is too short for the audited $Label contract."
    }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Bytes[$Offset + $index] -ne $Expected[$index]) {
            throw "Origin.exe failed the audited $Label contract."
        }
    }
}

function Assert-NativeContract([string]$ClientExe) {
    if (-not (Test-Path -LiteralPath $ClientExe -PathType Leaf)) {
        throw "Origin client is missing: $ClientExe"
    }
    [byte[]]$bytes = [IO.File]::ReadAllBytes($ClientExe)
    Assert-BytesAt $bytes 0x1C0E59 ([byte[]]@(
            0x6A,0x06,0x83,0xC0,0x08,0x50,
            0x8D,0x4E,0x0C,0x6A,0x06,0x51)) `
        '10273 six-byte copy'
    Assert-BytesAt $bytes 0x1C0E6D ([byte[]]@(
            0x8B,0xC6,0x89,0x5E,0x14,
            0xC7,0x46,0x08,0x02,0x00,0x00,0x00)) `
        '10273 state-two transition'
    Assert-BytesAt $bytes 0x1C21F0 ([byte[]]@(
            0x68,0x60,0x9E,0x95,0x00,0x51,
            0x0F,0xB6,0x4C,0x2F,0x0C)) `
        'state-two raw result-byte renderer'
    Assert-BytesAt $bytes 0x1C24D5 ([byte[]]@(
            0x68,0x14,0x0D,0x95,0x00,
            0x8D,0x44,0x24,0x48,0xE9)) `
        'state-two adjacent-column blanking'
}

function Assert-XmlContract([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Pet Rebirth UI layout is missing: $Path"
    }
    $length = (Get-Item -LiteralPath $Path).Length
    $hash = Get-Sha256 $Path
    if ($length -ne $xmlLength -or $hash -cne $xmlHash) {
        throw "Unsupported Pet Rebirth UI layout (length $length, " +
            "SHA-256 $hash): $Path"
    }
    try { [xml]$xml = Get-Content -LiteralPath $Path -Raw }
    catch { throw "Pet Rebirth UI layout is invalid XML: $Path`n$_" }
    for ($index = 0; $index -lt 6; $index++) {
        $id = 874012 + (10 * $index)
        $expectedName = 'PetTraitBase' + ($index + 1)
        $nodes = @($xml.SelectNodes("//*[@ID='$id']"))
        if ($nodes.Count -ne 1 -or $nodes[0].Name -cne $expectedName -or
            $nodes[0].FontColor -cne 'DEFAULT_TEXTCOLOR') {
            throw "Pet Rebirth result control $id is not the audited label: $Path"
        }
    }
}

function Assert-OriginClosed([string]$ClientExe) {
    $expected = [IO.Path]::GetFullPath($ClientExe)
    $liveDefault = [IO.Path]::GetFullPath('C:\Godswar Origin\Origin.exe')
    foreach ($process in @(Get-Process Origin -ErrorAction SilentlyContinue)) {
        try {
            try { $path = $process.Path } catch { $path = $null }
            $isTarget = $path -and [string]::Equals(
                [IO.Path]::GetFullPath($path),
                $expected,
                [StringComparison]::OrdinalIgnoreCase)
            $isUnknownLiveTarget = -not $path -and [string]::Equals(
                $expected,
                $liveDefault,
                [StringComparison]::OrdinalIgnoreCase)
            if ($isTarget -or $isUnknownLiveTarget) {
                throw 'Close Origin.exe before changing Pet Rebirth UI resources.'
            }
        }
        finally { $process.Dispose() }
    }
}

$resolvedRoot = [IO.Path]::GetFullPath($ClientRoot)
$clientExe = Join-Path $resolvedRoot 'Origin.exe'
Assert-NativeContract $clientExe
$records = foreach ($locale in $locales) {
    $luaPath = Join-Path $resolvedRoot "Localization\$locale\$relativeLua"
    $xmlPath = Join-Path $resolvedRoot "Localization\$locale\$relativeXml"
    Assert-XmlContract $xmlPath
    if (-not (Test-Path -LiteralPath $luaPath -PathType Leaf)) {
        throw "Pet Rebirth Lua resource is missing: $luaPath"
    }
    $contract = $contracts[$locale]
    $hash = Get-Sha256 $luaPath
    $length = (Get-Item -LiteralPath $luaPath).Length
    $state = if ($hash -ceq $contract.StockHash -and
        $length -eq $contract.StockLength) {
        'Stock'
    }
    elseif ($hash -ceq $contract.PatchedHash -and
        $length -eq $contract.PatchedLength) {
        'Patched'
    }
    else {
        throw "Unsupported Pet Rebirth Lua (length $length, " +
            "SHA-256 $hash): $luaPath"
    }
    $resource = Read-Resource $luaPath
    Assert-LuaShape $resource.Text $state $luaPath
    [pscustomobject]@{
        Locale = $locale
        Path = $luaPath
        Hash = $hash
        State = $state
        Text = $resource.Text
        Contract = $contract
    }
}

$states = @($records.State | Select-Object -Unique)
if ($states.Count -ne 1) {
    throw 'Pet Rebirth locale resources are in a mixed state.'
}
$current = $states[0]
if ($Mode -eq 'Status') {
    [pscustomobject]@{
        Status = if ($current -eq 'Patched') { 'Patched' } else { 'Ready' }
        Locales = $locales -join ', '
        ResultControls = 6
        ExactHundredthsVisible = $current -eq 'Patched'
        ValueRange = '0..255 => +0.00..+2.55'
    }
    return
}

Assert-OriginClosed $clientExe
$target = if ($Mode -eq 'Apply') { 'Patched' } else { 'Stock' }
if ($current -eq $target) {
    [pscustomobject]@{
        Status = "Already $($target.ToLowerInvariant())"
        Locales = $locales -join ', '
        ResultControls = 6
        ExactHundredthsVisible = $target -eq 'Patched'
    }
    return
}

$backupDirectory = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) (
    'pet-rebirth-growth-result-' + $Mode.ToLowerInvariant() + '-' +
    (Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$staged = @()
try {
    foreach ($record in $records) {
        $backup = Join-Path $backupDirectory (
            "$($record.Locale)-PetSamsaraUI.lua")
        $stage = "$($record.Path).$([Guid]::NewGuid().ToString('N')).stage"
        Copy-Item -LiteralPath $record.Path -Destination $backup
        if ((Get-Sha256 $backup) -cne $record.Hash) {
            throw "Pet Rebirth Lua backup verification failed: $($record.Path)"
        }
        $stageRecord = [pscustomobject]@{
            Path = $record.Path
            Backup = $backup
            Stage = $stage
            ExpectedHash = $null
        }
        $staged += $stageRecord

        $from = if ($target -eq 'Patched') {
            $stockFragment
        }
        else { $patchedFragment }
        $to = if ($target -eq 'Patched') {
            $patchedFragment
        }
        else { $stockFragment }
        if ((Get-ExactCount $record.Text $from) -ne 1 -or
            (Get-ExactCount $record.Text $to) -ne 0) {
            throw "Pet Rebirth result function is partial: $($record.Path)"
        }
        $output = $record.Text.Replace($from, $to)
        Assert-LuaShape $output $target $record.Path
        Write-Resource $stage $output
        $expectedHash = if ($target -eq 'Patched') {
            $record.Contract.PatchedHash
        }
        else { $record.Contract.StockHash }
        if ((Get-Sha256 $stage) -cne $expectedHash) {
            throw "Staged Pet Rebirth Lua hash is not exact: $($record.Path)"
        }
        $stageRecord.ExpectedHash = $expectedHash
    }

    foreach ($record in $staged) {
        Move-Item -LiteralPath $record.Stage -Destination $record.Path -Force
    }
    foreach ($record in $staged) {
        if ((Get-Sha256 $record.Path) -cne $record.ExpectedHash) {
            throw "Installed Pet Rebirth Lua hash is not exact: $($record.Path)"
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
    Status = if ($target -eq 'Patched') { 'Patched' } else { 'Reverted' }
    Locales = $locales -join ', '
    ResultControls = 6
    ExactHundredthsVisible = $target -eq 'Patched'
    ValueRange = '0..255 => +0.00..+2.55'
    Backup = $backupDirectory
}
