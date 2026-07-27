Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'Phase4SecureDockerClientCampaign.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'Phase4CompletionValidation.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'ControlledHostReadOnlyArtifactAcl.psm1'
)
Import-Module (
    Join-Path $moduleRoot 'Phase4CompletionWriteSupport.psm1'
)

$script:IssuedRoot = (
    Get-RebornPhase4SecureDockerPins).CampaignRoot
$script:MaximumCompletionBytes = 24KB

function Test-RebornPhase4CompletionPairedOrigin {
    param([Parameter(Mandatory)][object]$Pins)

    return (
        $null -ne $Pins.PSObject.Properties['CandidateOriginSha256'])
}

function Test-RebornPhase4CompletionTlsTrust {
    param([Parameter(Mandatory)][object]$Pins)

    return (
        $null -ne $Pins.PSObject.Properties['ClientTlsTrustMode'])
}

function Test-RebornPhase4CurrentCompletionPins {
    param([Parameter(Mandatory)][object]$Pins)

    $current = Get-RebornPhase4SecureDockerPins
    return (
        [string]$Pins.CampaignGeneration -ceq
            [string]$current.CampaignGeneration -and
        [string]$Pins.CompletionMode -ceq
            [string]$current.CompletionMode)
}

function New-RebornPhase4CompletionPinsRecord {
    param([Parameter(Mandatory)][object]$Pins)

    $values = [ordered]@{
        clientRoot = $Pins.ClientRoot
    }
    if (Test-RebornPhase4CompletionPairedOrigin $Pins) {
        $values.stockOriginSha256 = $Pins.OriginSha256
        $values.candidateOriginSha256 = $Pins.CandidateOriginSha256
    }
    else {
        $values.originSha256 = $Pins.OriginSha256
    }
    if (Test-RebornPhase4CompletionTlsTrust $Pins) {
        $values.tlsTrustMode = $Pins.ClientTlsTrustMode
    }
    foreach ($binding in ([ordered]@{
        stockNetSha256 = $Pins.StockNetSha256
        candidateSha256 = $Pins.CandidateSha256
        nativeChecksSha256 = $Pins.NativeChecksSha256
        manifestSha256 = $Pins.ManifestSha256
        manifestTrustSha256 = $Pins.ManifestTrustSha256
        inventorySetSha256 = $Pins.InventorySetSha256
        rootCertificateSha256 = $Pins.RootCertificateSha256
        serverPfxSha256 = $Pins.ServerPfxSha256
        manifestSequence = [UInt64]$Pins.ManifestSequence
        dockerProfile = $Pins.DockerProfile
        databaseName = $Pins.DockerDatabase
    }).GetEnumerator()) {
        $values[$binding.Key] = $binding.Value
    }
    return [pscustomobject]$values
}

function Write-RebornPhase4CompletionReceipt {
    param(
        [Parameter(Mandatory)][string[]]$ProfileResultPaths,
        [Parameter(Mandatory)][object]$ManualAttestation,
        [Parameter(Mandatory)][object]$FinalStatus,
        [Parameter(Mandatory)][object]$DockerStatus,
        [string]$CompletionRoot = $script:IssuedRoot,
        [switch]$AllowTestPath,
        [object]$Pins = (Get-RebornPhase4SecureDockerPins)
    )

    if (-not $AllowTestPath -and
        -not (Test-RebornPhase4CurrentCompletionPins $Pins)) {
        throw 'Historical completion receipts are read-only.'
    }
    Assert-RebornPhase4CompletionWriteAuthority `
        -AllowTestPath:$AllowTestPath
    $root = Resolve-RebornPhase4CompletionRoot `
        $CompletionRoot -AllowTestPath:$AllowTestPath -Pins $Pins
    $campaign = Read-RebornPhase4CampaignReceipt `
        $root -AllowTestPath:$AllowTestPath -Pins $Pins
    if ($null -eq $campaign -or
        [string]$campaign.Record.state -cne 'Restored') {
        throw 'Completion requires the latest Restored campaign.'
    }
    Assert-RebornPhase4ManualAttestation $ManualAttestation
    Assert-RebornPhase4FinalStatus `
        $FinalStatus $DockerStatus $campaign $Pins
    if ($ProfileResultPaths.Count -ne 3) {
        throw 'Completion requires exactly three profile results.'
    }
    $results = @(
        foreach ($path in $ProfileResultPaths) {
            Read-RebornPhase4ProfileResult `
                $path -AllowTestPath:$AllowTestPath -Pins $Pins
        })
    $profiles = @($results.Record.profile)
    if (@($profiles | Sort-Object -Unique).Count -ne 3 -or
        @(@('Baseline', 'Fallback', 'Soak') |
            Where-Object { $_ -notin $profiles }).Count -ne 0) {
        throw 'Phase 4 completion profile set is not exact.'
    }
    $campaignId = [string]$campaign.Record.campaignId
    $issuedUserSid = [string]$campaign.Record.issuedUserSid
    foreach ($result in $results) {
        if ([string]$result.Record.campaignId -cne $campaignId -or
            [string]$result.Record.issuedUserSid -cne $issuedUserSid) {
            throw 'A profile belongs to another campaign or user.'
        }
    }
    foreach ($property in @(
        'serverSha256', 'managedReleaseSetSha256', 'optionsSha256')) {
        if (@($results.Record.$property | Sort-Object -Unique).Count -ne 1) {
            throw "Phase 4 build pin changed: $property"
        }
    }

    $first = $results[0].Record
    $recordValues = [ordered]@{
        schemaVersion = $Pins.CompletionSchemaVersion
        mode = $Pins.CompletionMode
        result = 'Pass'
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        campaign = [pscustomobject][ordered]@{
            id = $campaignId
            issuedUserSid = $issuedUserSid
            handoffPath = $campaign.Path
            handoffSha256 = $campaign.Sha256
            revision = [UInt64]$campaign.Record.revision
            state = 'Restored'
        }
        pins = New-RebornPhase4CompletionPinsRecord $Pins
        build = [pscustomobject][ordered]@{
            serverSha256 = [string]$first.serverSha256
            managedReleaseSetSha256 =
                [string]$first.managedReleaseSetSha256
            optionsSha256 = [string]$first.optionsSha256
        }
        profiles =
            New-RebornPhase4CompletionProfileBindings $results
        manualAttestation = $ManualAttestation
        finalState = [pscustomobject][ordered]@{
            restore = 'ValidatedRestoredCampaign'
            dockerState = 'HealthyExact'
            dockerRestartCount = 0
            bundleState = 'Stock'
            hostsState = 'Absent'
            rootState = 'Absent'
            activationMode = [UInt64]0
            activationEnvironment = [UInt64]$Pins.ActivationEnvironment
            sequenceFloor = [UInt64]$Pins.ManifestSequence
        }
    }
    if ([string]$Pins.CampaignGeneration -cne 'LegacyV1') {
        $recordValues.Insert(
            2, 'generation', $Pins.CampaignGeneration)
        $recordValues.pins | Add-Member `
            -NotePropertyName nextManifestTrustSha256 `
            -NotePropertyValue $Pins.NextManifestTrustSha256
    }
    $record = [pscustomobject]$recordValues
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(
        ($record | ConvertTo-Json -Compress -Depth 8))
    try {
        if ($bytes.Length -gt $script:MaximumCompletionBytes) {
            throw 'Phase 4 completion receipt exceeds its byte budget.'
        }
        $base = Join-Path $root "completion-$campaignId"
        $jsonPath = "$base.json"
        $checksumPath = "$base.sha256"
        if ((Test-Path -LiteralPath $jsonPath) -or
            (Test-Path -LiteralPath $checksumPath)) {
            throw 'A Phase 4 completion receipt already exists.'
        }
        $sha = Get-RebornPhase4CompletionSha256 $bytes
        Write-RebornPhase4CompletionFile $jsonPath $bytes
        $checksum = [Text.Encoding]::ASCII.GetBytes($sha)
        try {
            Write-RebornPhase4CompletionFile $checksumPath $checksum
        }
        finally {
            [Array]::Clear($checksum, 0, $checksum.Length)
        }
        if (-not $AllowTestPath) {
            Protect-RebornControlledHostReadOnlyArtifact `
                $jsonPath -File | Out-Null
            Protect-RebornControlledHostReadOnlyArtifact `
                $checksumPath -File | Out-Null
        }
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
    return Read-RebornPhase4CompletionReceipt `
        $jsonPath -AllowTestPath:$AllowTestPath -Pins $Pins
}

function Assert-RebornPhase4CompletionRecord {
    param(
        [Parameter(Mandatory)][object]$Record,
        [Parameter(Mandatory)][object]$Campaign,
        [Parameter(Mandatory)][object]$Pins,
        [switch]$AllowTestPath
    )

    $recordProperties = @(
        'schemaVersion', 'mode', 'result', 'completedUtc',
        'campaign', 'pins', 'build', 'profiles',
        'manualAttestation', 'finalState')
    $originPinProperties = if (
        Test-RebornPhase4CompletionPairedOrigin $Pins) {
        @('stockOriginSha256', 'candidateOriginSha256')
    }
    else {
        @('originSha256')
    }
    $pinProperties = @(
        'clientRoot') + $originPinProperties + @('stockNetSha256',
        'candidateSha256', 'nativeChecksSha256', 'manifestSha256',
        'manifestTrustSha256', 'inventorySetSha256',
        'rootCertificateSha256', 'serverPfxSha256',
        'manifestSequence', 'dockerProfile', 'databaseName')
    if ([string]$Pins.CampaignGeneration -cne 'LegacyV1') {
        $recordProperties += 'generation'
        $pinProperties += 'nextManifestTrustSha256'
    }
    if (Test-RebornPhase4CompletionTlsTrust $Pins) {
        $pinProperties += 'tlsTrustMode'
    }
    Assert-RebornPhase4CompletionProperties `
        $Record $recordProperties 'Phase 4 completion receipt'
    Assert-RebornPhase4CompletionProperties $Record.campaign @(
        'id', 'issuedUserSid', 'handoffPath', 'handoffSha256',
        'revision', 'state') 'Phase 4 completion campaign'
    Assert-RebornPhase4CompletionProperties `
        $Record.pins $pinProperties 'Phase 4 completion pins'
    Assert-RebornPhase4CompletionProperties $Record.build @(
        'serverSha256', 'managedReleaseSetSha256',
        'optionsSha256') 'Phase 4 completion build'
    Assert-RebornPhase4CompletionProperties $Record.finalState @(
        'restore', 'dockerState', 'dockerRestartCount',
        'bundleState', 'hostsState', 'rootState',
        'activationMode', 'activationEnvironment',
        'sequenceFloor') 'Phase 4 completion final state'
    $completed = [DateTimeOffset]::MinValue
    $generationProperty = $Record.PSObject.Properties['generation']
    $generationValid = if (
        [string]$Pins.CampaignGeneration -ceq 'LegacyV1') {
        $null -eq $generationProperty
    } else {
        $null -ne $generationProperty -and
            [string]$Record.generation -ceq
                [string]$Pins.CampaignGeneration
    }
    if (-not $generationValid -or
        $Record.schemaVersion -ne $Pins.CompletionSchemaVersion -or
        [string]$Record.mode -cne $Pins.CompletionMode -or
        [string]$Record.result -cne 'Pass' -or
        @($Record.profiles).Count -ne 3 -or
        -not [DateTimeOffset]::TryParseExact(
            [string]$Record.completedUtc,
            'O',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$completed) -or
        $completed.Offset -ne [TimeSpan]::Zero) {
        throw 'Phase 4 completion receipt policy is invalid.'
    }
    Assert-RebornPhase4ManualAttestation $Record.manualAttestation
    if ([string]$Campaign.Record.state -cne 'Restored' -or
        [string]$Record.campaign.id -cne
            [string]$Campaign.Record.campaignId -or
        [string]$Record.campaign.issuedUserSid -cne
            [string]$Campaign.Record.issuedUserSid -or
        -not ([IO.Path]::GetFullPath(
            [string]$Record.campaign.handoffPath)).Equals(
                $Campaign.Path,
                [StringComparison]::OrdinalIgnoreCase) -or
        [string]$Record.campaign.handoffSha256 -cne $Campaign.Sha256 -or
        [UInt64]$Record.campaign.revision -ne
            [UInt64]$Campaign.Record.revision -or
        [string]$Record.campaign.state -cne 'Restored') {
        throw 'Phase 4 completion campaign binding is invalid.'
    }
    $pinNames = @(
        'clientRoot') + $originPinProperties + @('stockNetSha256',
        'candidateSha256', 'nativeChecksSha256', 'manifestSha256',
        'manifestTrustSha256', 'inventorySetSha256',
        'rootCertificateSha256', 'serverPfxSha256',
        'dockerProfile', 'databaseName')
    foreach ($name in $pinNames) {
        $expected = switch ($name) {
            'clientRoot' { $Pins.ClientRoot }
            'stockOriginSha256' { $Pins.OriginSha256 }
            'candidateOriginSha256' { $Pins.CandidateOriginSha256 }
            'originSha256' { $Pins.OriginSha256 }
            'stockNetSha256' { $Pins.StockNetSha256 }
            'candidateSha256' { $Pins.CandidateSha256 }
            'nativeChecksSha256' { $Pins.NativeChecksSha256 }
            'manifestSha256' { $Pins.ManifestSha256 }
            'manifestTrustSha256' { $Pins.ManifestTrustSha256 }
            'inventorySetSha256' { $Pins.InventorySetSha256 }
            'rootCertificateSha256' { $Pins.RootCertificateSha256 }
            'serverPfxSha256' { $Pins.ServerPfxSha256 }
            'dockerProfile' { $Pins.DockerProfile }
            'databaseName' { $Pins.DockerDatabase }
        }
        if ([string]$Record.pins.$name -cne [string]$expected) {
            throw "Phase 4 completion pin changed: $name"
        }
    }
    if ([string]$Pins.CampaignGeneration -cne 'LegacyV1' -and
        [string]$Record.pins.nextManifestTrustSha256 -cne
            [string]$Pins.NextManifestTrustSha256) {
        throw 'Phase 4 completion next-trust pin changed.'
    }
    if ((Test-RebornPhase4CompletionTlsTrust $Pins) -and
        [string]$Record.pins.tlsTrustMode -cne
            [string]$Pins.ClientTlsTrustMode) {
        throw 'Phase 4 completion TLS trust mode changed.'
    }
    if ([UInt64]$Record.pins.manifestSequence -ne
        $Pins.ManifestSequence) {
        throw 'Phase 4 completion manifest sequence changed.'
    }
    foreach ($hash in @(
        [string]$Record.build.serverSha256,
        [string]$Record.build.managedReleaseSetSha256,
        [string]$Record.build.optionsSha256)) {
        Assert-RebornPhase4CompletionHash $hash 'Phase 4 build hash'
    }
    $seen = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($profile in @($Record.profiles)) {
        Assert-RebornPhase4CompletionProperties $profile @(
            'profile', 'profileResultPath', 'profileResultSha256',
            'evidencePath', 'evidenceSha256',
            'observedDurationSeconds') 'Phase 4 completion profile'
        if (-not $seen.Add([string]$profile.profile)) {
            throw 'Phase 4 completion contains a duplicate profile.'
        }
        $source = Read-RebornPhase4ProfileResult `
            ([string]$profile.profileResultPath) `
            -AllowTestPath:$AllowTestPath -Pins $Pins
        if ([string]$profile.profile -cne
                [string]$source.Record.profile -or
            -not ([IO.Path]::GetFullPath(
                [string]$profile.profileResultPath)).Equals(
                    $source.Path,
                    [StringComparison]::OrdinalIgnoreCase) -or
            [string]$profile.profileResultSha256 -cne $source.Sha256 -or
            [string]$profile.evidencePath -cne
                [string]$source.Record.evidencePath -or
            [string]$profile.evidenceSha256 -cne
                [string]$source.Record.evidenceSha256 -or
            [double]$profile.observedDurationSeconds -ne
                [double]$source.Record.observedDurationSeconds -or
            [string]$source.Record.serverSha256 -cne
                [string]$Record.build.serverSha256 -or
            [string]$source.Record.managedReleaseSetSha256 -cne
                [string]$Record.build.managedReleaseSetSha256 -or
            [string]$source.Record.optionsSha256 -cne
                [string]$Record.build.optionsSha256 -or
            [string]$source.Record.campaignId -cne
                [string]$Record.campaign.id -or
            [string]$source.Record.issuedUserSid -cne
                [string]$Record.campaign.issuedUserSid) {
            throw 'Phase 4 completion profile binding is invalid.'
        }
    }
    if (@(@('Baseline', 'Fallback', 'Soak') |
            Where-Object { -not $seen.Contains($_) }).Count -ne 0 -or
        [string]$Record.finalState.restore -cne
            'ValidatedRestoredCampaign' -or
        [string]$Record.finalState.dockerState -cne 'HealthyExact' -or
        [int]$Record.finalState.dockerRestartCount -ne 0 -or
        [string]$Record.finalState.bundleState -cne 'Stock' -or
        [string]$Record.finalState.hostsState -cne 'Absent' -or
        [string]$Record.finalState.rootState -cne 'Absent' -or
        [UInt64]$Record.finalState.activationMode -ne 0 -or
        [UInt64]$Record.finalState.activationEnvironment -ne
            $Pins.ActivationEnvironment -or
        [UInt64]$Record.finalState.sequenceFloor -ne
            $Pins.ManifestSequence) {
        throw 'Phase 4 completion final state is invalid.'
    }
}

function Read-RebornPhase4CompletionReceipt {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$AllowTestPath,
        [object]$Pins = (Get-RebornPhase4SecureDockerPins)
    )

    $root = Resolve-RebornPhase4CompletionRoot `
        (Split-Path -Parent ([IO.Path]::GetFullPath($Path))) `
        -AllowTestPath:$AllowTestPath -Pins $Pins
    $artifact = Read-RebornPhase4ChecksummedJson `
        $Path $script:MaximumCompletionBytes 'Phase 4 completion receipt'
    $campaign = Read-RebornPhase4CampaignReceipt `
        $root -AllowTestPath:$AllowTestPath -Pins $Pins
    if ($null -eq $campaign) {
        throw 'Phase 4 completion campaign is absent.'
    }
    $expectedName =
        "completion-$($campaign.Record.campaignId).json"
    if ((Split-Path -Leaf $artifact.Path) -cne $expectedName) {
        throw 'Phase 4 completion receipt name is invalid.'
    }
    if (-not $AllowTestPath) {
        Assert-RebornControlledHostReadOnlyArtifactAcl `
            $artifact.Path -File | Out-Null
        Assert-RebornControlledHostReadOnlyArtifactAcl `
            $artifact.ChecksumPath -File | Out-Null
    }
    Assert-RebornPhase4CompletionRecord `
        $artifact.Record $campaign $Pins `
        -AllowTestPath:$AllowTestPath
    return $artifact
}

Export-ModuleMember -Function @(
    'Write-RebornPhase4CompletionReceipt',
    'Read-RebornPhase4CompletionReceipt'
)
