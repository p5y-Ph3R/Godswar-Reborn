[CmdletBinding()]
param(
    [string]$FixtureExe =
        'C:\Reborn\backups\origin-avatar-preload-v4-Apply-20260724-213316596-5256fb25\Origin.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$priorSha256 =
    '753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79'
$patchedSha256 =
    'E0F5BC951C6E37550F4D9CC1E25BFDCB4F020466ADD854DC2E7EA04E0D22F81C'
$stockNetSha256 =
    '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C'
$stockNetFixture =
    'C:\Reborn\backups\client-network-shim-v1-Apply-20260724-213354864\Net.dll'
$patcher = Join-Path $PSScriptRoot 'PatchClientAvatarPreload.ps1'
$previewGuardPatcher = Join-Path $PSScriptRoot `
    'PatchClientAvatarPreviewGuard.ps1'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repoRoot 'artifacts'
$testRoot = Join-Path $artifactRoot (
    'avatar-preload-patch-test-' + [guid]::NewGuid().ToString('N'))
$runningProbe = $null

function Assert-Value {
    param($Actual, $Expected, [string]$Label)

    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
}

function Assert-Throws {
    param([scriptblock]$Operation, [string]$Label)

    try {
        & $Operation
    }
    catch {
        Write-Host "Expected refusal ($Label): $($_.Exception.Message)"
        return
    }
    throw "Expected operation to be refused: $Label"
}

try {
    if (-not (Test-Path -LiteralPath $FixtureExe -PathType Leaf)) {
        throw "Avatar-preload fixture not found: $FixtureExe"
    }
    Assert-Value `
        (Get-FileHash -LiteralPath $FixtureExe -Algorithm SHA256).Hash `
        $priorSha256 `
        'Fixture SHA-256'
    Assert-Value `
        (Get-FileHash -LiteralPath $stockNetFixture -Algorithm SHA256).Hash `
        $stockNetSha256 `
        'Stock Net fixture SHA-256'

    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $clientRoot = Join-Path $testRoot 'client'
    $backupRoot = Join-Path $testRoot 'backups'
    [IO.Directory]::CreateDirectory($clientRoot) | Out-Null
    $copy = Join-Path $clientRoot 'Origin.exe'
    Copy-Item -LiteralPath $FixtureExe -Destination $copy
    $pairedNet = Join-Path $clientRoot 'Net.dll'
    Copy-Item -LiteralPath $stockNetFixture -Destination $pairedNet

    $status = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $status.State 'V3PriorPatch' 'Initial state'

    $apply = & $patcher -ClientExe $copy -Mode Apply -BackupRoot $backupRoot
    Assert-Value $apply.ChangedBytes 206 'Apply mutation count'
    Assert-Value $apply.AfterSha256 $patchedSha256 'Apply result SHA-256'
    Assert-Value `
        (Get-FileHash -LiteralPath $apply.Backup -Algorithm SHA256).Hash `
        $priorSha256 `
        'Apply backup SHA-256'

    $status = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $status.State 'V4Patched' 'Applied state'
    Assert-Throws {
        & $previewGuardPatcher -ClientExe $copy -Mode Revert | Out-Null
    } 'coordinated V4 rollback'
    Assert-Value `
        (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash `
        $patchedSha256 `
        'Rejected prerequisite downgrade SHA-256'
    Remove-Item -LiteralPath $pairedNet -Force
    Assert-Throws {
        & $patcher -ClientExe $copy -Mode Revert | Out-Null
    } 'missing stock network DLL'
    Copy-Item -LiteralPath $stockNetFixture -Destination $pairedNet
    $pairedLegacy = Join-Path $clientRoot 'NetLegacy.dll'
    Copy-Item -LiteralPath $stockNetFixture -Destination $pairedLegacy
    Assert-Throws {
        & $patcher -ClientExe $copy -Mode Revert | Out-Null
    } 'interrupted network restore'
    Remove-Item -LiteralPath $pairedLegacy -Force
    [IO.File]::WriteAllText($pairedNet, 'non-stock-network-fixture')
    Assert-Throws {
        & $patcher -ClientExe $copy -Mode Revert | Out-Null
    } 'paired network rollback order'
    Copy-Item -LiteralPath $stockNetFixture -Destination $pairedNet -Force
    Assert-Value `
        (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash `
        $patchedSha256 `
        'Rejected paired rollback SHA-256'
    $backupCount = @(Get-ChildItem -LiteralPath $backupRoot -Directory).Count
    $idempotent = & $patcher -ClientExe $copy -Mode Apply `
        -BackupRoot $backupRoot
    Assert-Value $idempotent.Status 'Already patched' 'Idempotent Apply'
    Assert-Value `
        @(Get-ChildItem -LiteralPath $backupRoot -Directory).Count `
        $backupCount `
        'Idempotent Apply backup count'

    $revert = & $patcher -ClientExe $copy -Mode Revert `
        -BackupRoot $backupRoot
    Assert-Value $revert.ChangedBytes 206 'Revert mutation count'
    Assert-Value $revert.AfterSha256 $priorSha256 'Revert result SHA-256'
    Assert-Value `
        (Get-FileHash -LiteralPath $revert.Backup -Algorithm SHA256).Hash `
        $patchedSha256 `
        'Revert backup SHA-256'
    $status = & $patcher -ClientExe $copy -Mode Status
    Assert-Value $status.State 'V3PriorPatch' 'Reverted state'

    $tampered = Join-Path $testRoot 'TamperedOrigin.exe'
    Copy-Item -LiteralPath $FixtureExe -Destination $tampered
    $tamperedBytes = [IO.File]::ReadAllBytes($tampered)
    $tamperedBytes[$tamperedBytes.Length - 1] = [byte](
        $tamperedBytes[$tamperedBytes.Length - 1] -bxor 0xFF)
    [IO.File]::WriteAllBytes($tampered, $tamperedBytes)
    Assert-Throws {
        & $patcher -ClientExe $tampered -Mode Status | Out-Null
    } 'foreign SHA-256'

    $runningRoot = Join-Path $testRoot 'running'
    [IO.Directory]::CreateDirectory($runningRoot) | Out-Null
    $runningExe = Join-Path $runningRoot 'Origin.exe'
    Copy-Item -LiteralPath "$env:WINDIR\System32\ping.exe" `
        -Destination $runningExe
    $runningProbe = Start-Process -FilePath $runningExe `
        -ArgumentList @('-t', '127.0.0.1') -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 200
    Assert-Throws {
        & $patcher -ClientExe $runningExe -Mode Apply | Out-Null
    } 'running executable'

    Write-Host 'All avatar-preload patch checks passed.'
}
finally {
    if ($runningProbe -and -not $runningProbe.HasExited) {
        Stop-Process -Id $runningProbe.Id -Force
        $runningProbe.WaitForExit()
    }
    $resolvedArtifactRoot = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith(
            "$resolvedArtifactRoot\",
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
