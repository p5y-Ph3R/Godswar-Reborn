Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'ControlledHostReadOnlyArtifactAcl.psm1'
) -Force
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
) -Force

$script:RuntimeEnvironmentNames = @(
    'DOTNET_ENVIRONMENT',
    'ASPNETCORE_ENVIRONMENT'
)
$script:MaximumProfileResultBytes = 8KB
$script:ProfileResultMode = 'Phase4LoopbackAcceptanceProfileResult'

function Get-RebornPhase4AcceptanceRuntimeEnvironmentNames {
    return @($script:RuntimeEnvironmentNames)
}

function Get-RebornPhase4AcceptanceProfilePolicy {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Baseline', 'Fallback', 'Soak')]
        [string]$EvidenceProfile
    )

    switch ($EvidenceProfile.ToUpperInvariant()) {
        'BASELINE' {
            $canonicalName = 'Baseline'
            $faultsEnabled = $false
        }
        'FALLBACK' {
            $canonicalName = 'Fallback'
            $faultsEnabled = $true
        }
        'SOAK' {
            $canonicalName = 'Soak'
            $faultsEnabled = $false
        }
        default {
            throw 'The Phase 4 evidence profile is unsupported.'
        }
    }

    return [pscustomobject][ordered]@{
        EvidenceProfile = $canonicalName
        FaultsEnabled = $faultsEnabled
    }
}

function Set-RebornPhase4AcceptanceProfileEnvironment {
    param(
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Environment,

        [Parameter(Mandatory)]
        [ValidateSet('Baseline', 'Fallback', 'Soak')]
        [string]$EvidenceProfile
    )

    $policy = Get-RebornPhase4AcceptanceProfilePolicy $EvidenceProfile
    foreach ($name in $script:RuntimeEnvironmentNames) {
        if ($Environment.Contains($name)) {
            $Environment.Remove($name)
        }
    }
    $Environment['GODSWAR_SECURE_PHASE4_ACCEPTANCE_FAULTS_ENABLED'] =
        $policy.FaultsEnabled.ToString().ToLowerInvariant()
    if ($policy.FaultsEnabled) {
        foreach ($name in $script:RuntimeEnvironmentNames) {
            $Environment[$name] = 'Development'
        }
    }
}

function New-RebornPhase4PostgresConnectionString {
    param(
        [Parameter(Mandatory)][string]$Username,
        [Parameter(Mandatory)][string]$Password,
        [Parameter(Mandatory)][string]$DatabaseName
    )

    if ([string]::IsNullOrWhiteSpace($Username) -or
        [string]::IsNullOrEmpty($Password) -or
        [string]::IsNullOrWhiteSpace($DatabaseName)) {
        throw 'The Phase 4 PostgreSQL connection input is invalid.'
    }

    $builder = [Data.Common.DbConnectionStringBuilder]::new()
    $builder['Host'] = '127.0.0.1'
    $builder['Port'] = 5432
    $builder['Database'] = $DatabaseName
    $builder['Username'] = $Username
    $builder['Password'] = $Password
    $builder['Pooling'] = $true
    return [string]$builder.ConnectionString
}

function Test-RebornPhase4AcceptanceSha256 {
    param([Parameter(Mandatory)][string]$Value)

    return $Value -cmatch '^[0-9A-F]{64}$'
}

function Assert-RebornPhase4AcceptanceProfileRecord {
    param([Parameter(Mandatory)][object]$Record)

    $expectedProperties = @(
        'schemaVersion',
        'mode',
        'campaignId',
        'issuedUserSid',
        'profile',
        'completedUtc',
        'observedDurationSeconds',
        'evidencePath',
        'evidenceSha256',
        'evidenceBytes',
        'evidenceEvents',
        'serverSha256',
        'managedReleaseSetSha256',
        'optionsSha256',
        'candidateSha256',
        'manifestSha256',
        'databaseName'
    )
    $actualProperties = @($Record.PSObject.Properties.Name)
    if ($actualProperties.Count -ne $expectedProperties.Count) {
        throw 'The Phase 4 profile-result schema is not exact.'
    }
    foreach ($name in $expectedProperties) {
        if ($actualProperties -cnotcontains $name) {
            throw "The Phase 4 profile result lacks $name."
        }
    }

    $campaignId = [Guid]::Empty
    $completedUtc = [DateTimeOffset]::MinValue
    try {
        $issuedSid =
            [Security.Principal.SecurityIdentifier]::new(
                [string]$Record.issuedUserSid)
    }
    catch {
        throw 'The Phase 4 profile-result issued-user SID is invalid.'
    }
    $profile =
        Get-RebornPhase4AcceptanceProfilePolicy ([string]$Record.profile)
    $duration = [double]$Record.observedDurationSeconds
    $hashes = @(
        [string]$Record.evidenceSha256,
        [string]$Record.serverSha256,
        [string]$Record.managedReleaseSetSha256,
        [string]$Record.optionsSha256,
        [string]$Record.candidateSha256,
        [string]$Record.manifestSha256
    )
    if ($Record.schemaVersion -ne 1 -or
        [string]$Record.mode -cne $script:ProfileResultMode -or
        -not [Guid]::TryParse(
            [string]$Record.campaignId,
            [ref]$campaignId) -or
        $campaignId -eq [Guid]::Empty -or
        $issuedSid.Value -ceq 'S-1-5-18' -or
        [string]$Record.profile -cne $profile.EvidenceProfile -or
        -not [DateTimeOffset]::TryParseExact(
            [string]$Record.completedUtc,
            'O',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$completedUtc) -or
        $completedUtc.Offset -ne [TimeSpan]::Zero -or
        [double]::IsNaN($duration) -or
        [double]::IsInfinity($duration) -or
        $duration -lt 0 -or
        -not [IO.Path]::IsPathRooted([string]$Record.evidencePath) -or
        [Int64]$Record.evidenceBytes -lt 1 -or
        [Int64]$Record.evidenceBytes -gt 64KB -or
        [int]$Record.evidenceEvents -lt 1 -or
        [int]$Record.evidenceEvents -gt 64 -or
        @($hashes | Where-Object {
            -not (Test-RebornPhase4AcceptanceSha256 $_)
        }).Count -ne 0 -or
        [string]$Record.databaseName -cne 'godswar_secure_dev') {
        throw 'The Phase 4 profile-result record is outside policy.'
    }
    return $Record
}

function New-RebornPhase4AcceptanceProfileRecord {
    param(
        [Parameter(Mandatory)][string]$CampaignId,
        [Parameter(Mandatory)][string]$IssuedUserSid,
        [Parameter(Mandatory)]
        [ValidateSet('Baseline', 'Fallback', 'Soak')]
        [string]$EvidenceProfile,
        [Parameter(Mandatory)][double]$ObservedDurationSeconds,
        [Parameter(Mandatory)][string]$EvidencePath,
        [Parameter(Mandatory)][string]$EvidenceSha256,
        [Parameter(Mandatory)][Int64]$EvidenceBytes,
        [Parameter(Mandatory)][int]$EvidenceEvents,
        [Parameter(Mandatory)][string]$ServerSha256,
        [Parameter(Mandatory)][string]$ManagedReleaseSetSha256,
        [Parameter(Mandatory)][string]$OptionsSha256,
        [Parameter(Mandatory)][string]$CandidateSha256,
        [Parameter(Mandatory)][string]$ManifestSha256,
        [Parameter(Mandatory)][string]$DatabaseName
    )

    $profile = Get-RebornPhase4AcceptanceProfilePolicy $EvidenceProfile
    $record = [pscustomobject][ordered]@{
        schemaVersion = 1
        mode = $script:ProfileResultMode
        campaignId = $CampaignId
        issuedUserSid = $IssuedUserSid
        profile = $profile.EvidenceProfile
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        observedDurationSeconds = $ObservedDurationSeconds
        evidencePath = [IO.Path]::GetFullPath($EvidencePath)
        evidenceSha256 = $EvidenceSha256
        evidenceBytes = $EvidenceBytes
        evidenceEvents = $EvidenceEvents
        serverSha256 = $ServerSha256
        managedReleaseSetSha256 = $ManagedReleaseSetSha256
        optionsSha256 = $OptionsSha256
        candidateSha256 = $CandidateSha256
        manifestSha256 = $ManifestSha256
        databaseName = $DatabaseName
    }
    return Assert-RebornPhase4AcceptanceProfileRecord $record
}

function Get-RebornPhase4AcceptanceProfileResultPaths {
    param([Parameter(Mandatory)][string]$EvidencePath)

    $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
    $directory = [IO.Path]::GetDirectoryName($resolvedEvidence)
    $baseName = [IO.Path]::GetFileNameWithoutExtension($resolvedEvidence)
    $resultPath = Join-Path $directory "$baseName.profile.json"
    return [pscustomobject]@{
        ResultPath = $resultPath
        ChecksumPath = [IO.Path]::ChangeExtension(
            $resultPath,
            '.sha256')
    }
}

function Protect-RebornPhase4AcceptanceProfileArtifact {
    param(
        [Parameter(Mandatory)][string]$Path,
        [scriptblock]$SetAclAction,
        [switch]$AllowTestHook
    )

    if ($null -ne $SetAclAction -and -not $AllowTestHook) {
        throw 'Custom Phase 4 profile ACL mutation is test-only.'
    }
    $resolved = Assert-RebornSingleLinkRegularFilePath `
        $Path 'Phase 4 profile-result ACL target'
    $reader =
        [Security.Principal.WindowsIdentity]::GetCurrent().User
    $security =
        New-RebornControlledHostReadOnlyArtifactSecurity `
            -File -ReaderSid $reader -OwnerSid $reader
    if ($null -eq $SetAclAction) {
        Set-Acl -LiteralPath $resolved -AclObject $security
        Assert-RebornControlledHostReadOnlyArtifactAcl `
            $resolved -File -AllowCurrentUserOwner | Out-Null
    }
    else {
        & $SetAclAction $resolved $security
    }
}

function Write-RebornPhase4AcceptanceProfileResult {
    param(
        [Parameter(Mandatory)][object]$Record,
        [scriptblock]$SetAclAction,
        [scriptblock]$BeforeChecksumWriteAction,
        [switch]$AllowTestHook
    )

    if (($null -ne $SetAclAction -or
         $null -ne $BeforeChecksumWriteAction) -and
        -not $AllowTestHook) {
        throw 'Custom Phase 4 profile hooks are test-only.'
    }
    Assert-RebornPhase4AcceptanceProfileRecord $Record | Out-Null
    $paths = Get-RebornPhase4AcceptanceProfileResultPaths `
        ([string]$Record.evidencePath)
    if ((Test-Path -LiteralPath $paths.ResultPath) -or
        (Test-Path -LiteralPath $paths.ChecksumPath)) {
        throw 'Refusing to overwrite a Phase 4 profile-result artifact.'
    }

    $primaryError = $null
    try {
        $json = $Record | ConvertTo-Json -Compress -Depth 4
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
        try {
            if ($bytes.Length -le 0 -or
                $bytes.Length -gt $script:MaximumProfileResultBytes) {
                throw 'The Phase 4 profile-result artifact exceeds 8KB.'
            }
            $stream = [IO.FileStream]::new(
                $paths.ResultPath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush($true)
            }
            finally {
                $stream.Dispose()
            }
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                $sha256 = ([BitConverter]::ToString(
                    $algorithm.ComputeHash($bytes))).Replace('-', '')
            }
            finally {
                $algorithm.Dispose()
            }
            if ($null -ne $BeforeChecksumWriteAction) {
                & $BeforeChecksumWriteAction $paths.ChecksumPath
            }
            $checksumBytes =
                [Text.Encoding]::ASCII.GetBytes($sha256)
            try {
                $checksumStream = [IO.FileStream]::new(
                    $paths.ChecksumPath,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None)
                try {
                    $checksumStream.Write(
                        $checksumBytes,
                        0,
                        $checksumBytes.Length)
                    $checksumStream.Flush($true)
                }
                finally {
                    $checksumStream.Dispose()
                }
            }
            finally {
                [Array]::Clear(
                    $checksumBytes,
                    0,
                    $checksumBytes.Length)
            }
        }
        finally {
            [Array]::Clear($bytes, 0, $bytes.Length)
        }
    }
    catch {
        $primaryError = $_
    }

    $protectionErrors = [Collections.Generic.List[string]]::new()
    foreach ($path in @($paths.ResultPath, $paths.ChecksumPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }
        try {
            Protect-RebornPhase4AcceptanceProfileArtifact `
                $path `
                -SetAclAction $SetAclAction `
                -AllowTestHook:$AllowTestHook
        }
        catch {
            $protectionErrors.Add($_.Exception.Message)
        }
    }
    if ($null -ne $primaryError) {
        if ($protectionErrors.Count -ne 0) {
            throw (
                "Phase 4 profile-result write failed: " +
                "$($primaryError.Exception.Message) Partial artifact " +
                "protection also failed: $($protectionErrors -join '; ')")
        }
        throw $primaryError
    }
    if ($protectionErrors.Count -ne 0) {
        throw (
            'Phase 4 profile-result read-only protection failed: ' +
            ($protectionErrors -join '; '))
    }

    return [pscustomobject]@{
        ProfileResultPath = $paths.ResultPath
        ProfileResultChecksumPath = $paths.ChecksumPath
        ProfileResultSha256 = $sha256
    }
}

function New-RebornPhase4LoopbackAcceptanceResult {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Baseline', 'Fallback', 'Soak')]
        [string]$EvidenceProfile,

        [Parameter(Mandatory)][object]$EvidenceResult,
        [Parameter(Mandatory)][string]$ServerSha256,
        [Parameter(Mandatory)][string]$ManagedReleaseSetSha256,
        [Parameter(Mandatory)][string]$OptionsSha256,
        [Parameter(Mandatory)][string]$DatabaseName,
        [Parameter(Mandatory)][int[]]$SecureTcpPorts,
        [Parameter(Mandatory)][int]$SecureUdpPort,
        [Parameter(Mandatory)][string]$EvidenceSha256,
        [Parameter(Mandatory)][object]$ProfileResult
    )

    $profile = Get-RebornPhase4AcceptanceProfilePolicy $EvidenceProfile
    foreach ($hash in @(
        $ServerSha256,
        $ManagedReleaseSetSha256,
        $OptionsSha256,
        $EvidenceSha256,
        [string]$ProfileResult.ProfileResultSha256
    )) {
        if ($hash -cnotmatch '^[0-9A-F]{64}$') {
            throw 'A Phase 4 acceptance result SHA-256 pin is invalid.'
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$EvidenceResult.Path) -or
        $null -eq $EvidenceResult.Events -or
        [double]$EvidenceResult.ObservedDurationSeconds -lt 0 -or
        $DatabaseName -cne 'godswar_secure_dev' -or
        [string]::IsNullOrWhiteSpace(
            [string]$ProfileResult.ProfileResultPath) -or
        [string]::IsNullOrWhiteSpace(
            [string]$ProfileResult.ProfileResultChecksumPath) -or
        -not [IO.Path]::IsPathRooted(
            [string]$ProfileResult.ProfileResultPath) -or
        -not [IO.Path]::IsPathRooted(
            [string]$ProfileResult.ProfileResultChecksumPath) -or
        $SecureTcpPorts.Count -eq 0 -or
        $SecureUdpPort -lt 1 -or
        $SecureUdpPort -gt 65535) {
        throw 'The Phase 4 acceptance result input is incomplete.'
    }
    foreach ($port in $SecureTcpPorts) {
        if ($port -lt 1 -or $port -gt 65535) {
            throw 'A Phase 4 secure TCP port is invalid.'
        }
    }

    return [pscustomobject][ordered]@{
        Result = 'Accepted'
        EvidenceProfile = $profile.EvidenceProfile
        EvidencePath = [string]$EvidenceResult.Path
        EvidenceEvents = $EvidenceResult.Events
        ObservedDurationSeconds =
            [double]$EvidenceResult.ObservedDurationSeconds
        ServerSha256 = $ServerSha256
        ManagedReleaseSetSha256 = $ManagedReleaseSetSha256
        OptionsSha256 = $OptionsSha256
        DatabaseName = $DatabaseName
        SecureTcpPorts = $SecureTcpPorts -join ','
        SecureUdpPort = $SecureUdpPort
        EvidenceSha256 = $EvidenceSha256
        ProfileResultPath = [string]$ProfileResult.ProfileResultPath
        ProfileResultChecksumPath =
            [string]$ProfileResult.ProfileResultChecksumPath
        ProfileResultSha256 =
            [string]$ProfileResult.ProfileResultSha256
    }
}

Export-ModuleMember -Function @(
    'Get-RebornPhase4AcceptanceRuntimeEnvironmentNames',
    'Get-RebornPhase4AcceptanceProfilePolicy',
    'Set-RebornPhase4AcceptanceProfileEnvironment',
    'New-RebornPhase4PostgresConnectionString',
    'Assert-RebornPhase4AcceptanceProfileRecord',
    'New-RebornPhase4AcceptanceProfileRecord',
    'Get-RebornPhase4AcceptanceProfileResultPaths',
    'Protect-RebornPhase4AcceptanceProfileArtifact',
    'Write-RebornPhase4AcceptanceProfileResult',
    'New-RebornPhase4LoopbackAcceptanceResult'
)
