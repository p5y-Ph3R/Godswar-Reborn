param(
    [string]$ClientRoot = "C:\Godswar Origin"
)

$ErrorActionPreference = "Stop"

$sourceRoot = Join-Path $ClientRoot "Characters\effect"
$targetRoot = Join-Path $ClientRoot "Characters_New\effect"

if (-not (Test-Path -LiteralPath $sourceRoot)) {
    throw "Source effect directory not found: $sourceRoot"
}

if (-not (Test-Path -LiteralPath $targetRoot)) {
    throw "Target effect directory not found: $targetRoot"
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupRoot = Join-Path (Resolve-Path ".").Path "backups\armor-rank14-characters-new-mirror-$timestamp"
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

$copied = 0
$backedUp = 0
foreach ($gender in @("male", "female")) {
    $sources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter "${gender}_body_effect_0014*" -File)
    if ($sources.Count -eq 0) {
        throw "Missing AR14 source effect files: $sourceRoot\${gender}_body_effect_0014*"
    }

    foreach ($source in $sources) {
        $targetPath = Join-Path $targetRoot $source.Name
        if (Test-Path -LiteralPath $targetPath) {
            Copy-Item -LiteralPath $targetPath -Destination (Join-Path $backupRoot $source.Name) -Force
            $backedUp++
        }

        Copy-Item -LiteralPath $source.FullName -Destination $targetPath -Force
        $copied++
    }
}

[pscustomobject]@{
    Source = $sourceRoot
    Target = $targetRoot
    CopiedFiles = $copied
    BackedUpExistingFiles = $backedUp
    BackupRoot = $backupRoot
}

Get-ChildItem -LiteralPath $targetRoot -Filter "*body_effect_0014*" -File |
    Select-Object DirectoryName, Name, Length |
    Sort-Object Name
