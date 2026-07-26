Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'Phase4SecureDockerClientCampaign.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'Phase4LoopbackAcceptanceProfile.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'ControlledHostPrivacyEvidence.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'ControlledHostReadOnlyArtifactAcl.psm1'
)

$script:IssuedRoot =
    'C:\ProgramData\RebornSecureNetworkPhase4Docker'
$script:IssuedEvidenceRoot = (
    'C:\Reborn\artifacts\controlled-host-acceptance\' +
    '20260727-011921\server-evidence')
$script:MaximumProfileBytes = 8KB
$script:HashPattern = '^[0-9A-F]{64}$'

function Get-RebornPhase4CompletionSha256 {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash($Bytes))).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-RebornPhase4CompletionProperties {
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string[]]$Names,
        [Parameter(Mandatory)][string]$Label
    )

    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Names.Count -or
        @($actual | Where-Object { $_ -notin $Names }).Count -ne 0 -or
        @($Names | Where-Object { $_ -notin $actual }).Count -ne 0) {
        throw "$Label property set is not exact."
    }
}

function Assert-RebornPhase4CompletionHash {
    param(
        [AllowEmptyString()][string]$Value,
        [Parameter(Mandatory)][string]$Label
    )

    if ($Value -cnotmatch $script:HashPattern) {
        throw "$Label is not an uppercase SHA-256 value."
    }
}

function Resolve-RebornPhase4CompletionRoot {
    param(
        [string]$Root = $script:IssuedRoot,
        [switch]$AllowTestPath
    )

    $resolved = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not $AllowTestPath) {
        if (-not $resolved.Equals(
                $script:IssuedRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Phase 4 completion output is outside its issued root.'
        }
    }
    else {
        $temp = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith(
                $temp,
                [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolved) -notmatch
                '^reborn-phase4-completion-test-[0-9a-f]{32}$') {
            throw 'Phase 4 completion test output escaped temporary scope.'
        }
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw 'The completion root must already contain its campaign.'
    }
    return $resolved
}

function Read-RebornPhase4ChecksummedJson {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$MaximumBytes,
        [Parameter(Mandatory)][string]$Label
    )

    $resolved = Assert-RebornSingleLinkRegularFilePath $Path $Label
    $checksumPath = [IO.Path]::ChangeExtension($resolved, '.sha256')
    Assert-RebornSingleLinkRegularFilePath `
        $checksumPath "$Label checksum" | Out-Null
    $item = Get-Item -LiteralPath $resolved
    if ($item.Length -le 0 -or $item.Length -gt $MaximumBytes -or
        (Get-Item -LiteralPath $checksumPath).Length -gt 66) {
        throw "$Label size is outside its fixed bound."
    }
    $expected = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    Assert-RebornPhase4CompletionHash $expected "$Label checksum"
    $bytes = [IO.File]::ReadAllBytes($resolved)
    try {
        if ($bytes.Length -ge 3 -and
            $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and
            $bytes[2] -eq 0xBF) {
            throw "$Label must be BOM-free UTF-8."
        }
        if ((Get-RebornPhase4CompletionSha256 $bytes) -cne $expected) {
            throw "$Label checksum failed."
        }
        $record = [Text.UTF8Encoding]::new(
            $false,
            $true).GetString($bytes) | ConvertFrom-Json
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
    return [pscustomobject]@{
        Path = $resolved
        ChecksumPath = $checksumPath
        Sha256 = $expected
        Record = $record
    }
}

function Read-RebornPhase4ProfileResult {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$AllowTestPath,
        [object]$Pins = (Get-RebornPhase4SecureDockerPins)
    )

    $artifact = Read-RebornPhase4ChecksummedJson `
        $Path $script:MaximumProfileBytes 'Phase 4 profile result'
    if (-not $AllowTestPath) {
        Assert-RebornControlledHostReadOnlyArtifactAcl `
            $artifact.Path -File -AllowCurrentUserOwner | Out-Null
        Assert-RebornControlledHostReadOnlyArtifactAcl `
            $artifact.ChecksumPath -File -AllowCurrentUserOwner | Out-Null
    }
    if ((Split-Path -Leaf $artifact.Path) -notmatch
            '^secure-server-.+\.profile\.json$') {
        throw 'Phase 4 profile result name is invalid.'
    }
    $record =
        Assert-RebornPhase4AcceptanceProfileRecord $artifact.Record
    if ([string]$record.candidateSha256 -cne $Pins.CandidateSha256 -or
        [string]$record.manifestSha256 -cne $Pins.ManifestSha256 -or
        [string]$record.databaseName -cne $Pins.DockerDatabase -or
        [double]$record.observedDurationSeconds -gt 86400) {
        throw 'Phase 4 profile result pins or duration are invalid.'
    }

    $evidencePath = [IO.Path]::GetFullPath(
        [string]$record.evidencePath)
    $profileRoot = Split-Path -Parent $artifact.Path
    if (-not (Split-Path -Parent $evidencePath).Equals(
            $profileRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        (-not $AllowTestPath -and
            -not $profileRoot.Equals(
                $script:IssuedEvidenceRoot,
                [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Phase 4 profile and evidence are outside one issued root.'
    }
    if ((Get-FileHash -LiteralPath $evidencePath `
            -Algorithm SHA256).Hash -cne
        [string]$record.evidenceSha256) {
        throw 'Phase 4 profile evidence hash changed.'
    }
    $evidence = Assert-RebornControlledHostPrivacyEvidence `
        -Path $evidencePath `
        -Profile ([string]$record.profile) `
        -ObservedDuration ([TimeSpan]::FromSeconds(
            [double]$record.observedDurationSeconds)) `
        -RequireStopped
    if (-not $AllowTestPath) {
        Assert-RebornControlledHostReadOnlyArtifactAcl `
            $evidence.Path -File -AllowCurrentUserOwner | Out-Null
    }
    if ([int]$record.evidenceBytes -ne [int]$evidence.Bytes -or
        [int]$record.evidenceEvents -ne [int]$evidence.Events) {
        throw 'Phase 4 profile evidence validation output changed.'
    }
    $artifact | Add-Member -NotePropertyName Evidence `
        -NotePropertyValue $evidence
    return $artifact
}

function New-RebornPhase4ManualAttestation {
    param(
        [switch]$AlternatingAccounts,
        [switch]$PreviewReadiness,
        [switch]$UnmountedMovement,
        [switch]$MountedMovement,
        [switch]$WorldGenerationChanges,
        [switch]$DeathAndRevive,
        [switch]$SessionLifecycle,
        [switch]$FallbackCorrection,
        [switch]$SoakStability,
        [switch]$DatabaseMutationReviewed,
        [ValidateSet('Passed', 'Unavailable')]
        [string]$ViewerParity = 'Unavailable'
    )

    return [pscustomobject][ordered]@{
        alternatingAccounts = [bool]$AlternatingAccounts
        previewReadiness = [bool]$PreviewReadiness
        unmountedMovement = [bool]$UnmountedMovement
        mountedMovement = [bool]$MountedMovement
        worldGenerationChanges = [bool]$WorldGenerationChanges
        deathAndRevive = [bool]$DeathAndRevive
        sessionLifecycle = [bool]$SessionLifecycle
        fallbackCorrection = [bool]$FallbackCorrection
        soakStability = [bool]$SoakStability
        databaseMutationReviewed = [bool]$DatabaseMutationReviewed
        viewerParity = $ViewerParity
    }
}

function Assert-RebornPhase4ManualAttestation {
    param([Parameter(Mandatory)][object]$Attestation)

    $names = @(
        'alternatingAccounts', 'previewReadiness',
        'unmountedMovement', 'mountedMovement',
        'worldGenerationChanges', 'deathAndRevive',
        'sessionLifecycle', 'fallbackCorrection', 'soakStability',
        'databaseMutationReviewed', 'viewerParity')
    Assert-RebornPhase4CompletionProperties `
        $Attestation $names 'Phase 4 manual attestation'
    foreach ($name in $names[0..9]) {
        if ($Attestation.$name -isnot [bool] -or
            -not $Attestation.$name) {
            throw "Phase 4 manual attestation did not pass: $name"
        }
    }
    if ([string]$Attestation.viewerParity -notin @(
            'Passed', 'Unavailable')) {
        throw 'Phase 4 viewer-parity attestation is invalid.'
    }
}

Export-ModuleMember -Function @(
    'Get-RebornPhase4CompletionSha256',
    'Assert-RebornPhase4CompletionProperties',
    'Assert-RebornPhase4CompletionHash',
    'Resolve-RebornPhase4CompletionRoot',
    'Read-RebornPhase4ChecksummedJson',
    'Read-RebornPhase4ProfileResult',
    'New-RebornPhase4ManualAttestation',
    'Assert-RebornPhase4ManualAttestation'
)
