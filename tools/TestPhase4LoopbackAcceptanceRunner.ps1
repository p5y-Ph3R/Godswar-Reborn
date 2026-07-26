$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'Phase4LoopbackAcceptanceProfile.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'ControlledHostServerLauncherDependencies.psm1'
) -Force

$passed = 0
$expectedChecks = 10

function Invoke-Check {
    param(
        [Parameter(Mandatory)][scriptblock]$Body,
        [Parameter(Mandatory)][string]$Name
    )

    & $Body
    $script:passed++
    Write-Host "PASS $Name"
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Body,
        [Parameter(Mandatory)][string]$Name
    )

    try {
        & $Body
    }
    catch {
        $script:passed++
        Write-Host "PASS $Name"
        return
    }
    throw "Expected failure did not occur: $Name"
}

$runtimeNames =
    @(Get-RebornPhase4AcceptanceRuntimeEnvironmentNames)
$processEnvironmentBefore = @{}
foreach ($name in $runtimeNames) {
    $processEnvironmentBefore[$name] =
        [Environment]::GetEnvironmentVariable(
            $name,
        [EnvironmentVariableTarget]::Process)
}
$temporary = Join-Path (
    [IO.Path]::GetTempPath()
) ('reborn-phase4-runner-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
$issuedUserSid =
    [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$capturedAclPaths = [Collections.Generic.List[string]]::new()
$aclTestHook = {
    param($path, $security)

    $owner = $security.GetOwner(
        [Security.Principal.SecurityIdentifier]).Value
    $rules = $security.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier])
    $rightsBySid = @{}
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow) {
            throw 'Generated profile-result ACL contains a deny rule.'
        }
        $sid = $rule.IdentityReference.Value
        if (-not $rightsBySid.ContainsKey($sid)) {
            $rightsBySid[$sid] =
                [Security.AccessControl.FileSystemRights]0
        }
        $rightsBySid[$sid] =
            $rightsBySid[$sid] -bor $rule.FileSystemRights
    }
    $mutation =
        [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    if ($owner -cne $issuedUserSid -or
        -not $security.AreAccessRulesProtected -or
        -not $rightsBySid.ContainsKey($issuedUserSid) -or
        ($rightsBySid[$issuedUserSid] -band $mutation) -ne 0 -or
        ($rightsBySid[$issuedUserSid] -band
            [Security.AccessControl.FileSystemRights]::Read) -ne
            [Security.AccessControl.FileSystemRights]::Read) {
        throw 'Generated profile-result ACL is not ordinary-user read-only.'
    }
    foreach ($trusted in @('S-1-5-18', 'S-1-5-32-544')) {
        if (-not $rightsBySid.ContainsKey($trusted) -or
            ($rightsBySid[$trusted] -band
                [Security.AccessControl.FileSystemRights]::FullControl) -ne
                [Security.AccessControl.FileSystemRights]::FullControl) {
            throw 'Generated profile-result ACL lacks trusted full control.'
        }
    }
    $capturedAclPaths.Add([string]$path)
}.GetNewClosure()

try {
Invoke-Check {
    $runnerPath =
        Join-Path $PSScriptRoot 'RunPhase4LoopbackAcceptanceServer.ps1'
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $runnerPath,
        [ref]$tokens,
        [ref]$errors)
    if ($errors.Count -ne 0) {
        throw 'The Phase 4 loopback runner does not parse.'
    }
    $commands = @(
        $ast.FindAll(
            {
                param($node)
                $node -is [Management.Automation.Language.CommandAst]
            },
            $true) |
            ForEach-Object { $_.GetCommandName() })
    foreach ($required in @(
        'Get-RebornPhase4AcceptanceProfilePolicy',
        'Get-Phase4LoopbackActivationState',
        'Assert-RebornControlledHostUnsetEnvironmentNames',
        'Set-RebornPhase4AcceptanceProfileEnvironment',
        'New-RebornPhase4AcceptanceProfileRecord',
        'Write-RebornPhase4AcceptanceProfileResult',
        'New-RebornPhase4LoopbackAcceptanceResult'
    )) {
        if ($commands -cnotcontains $required) {
            throw "The loopback runner is not wired to $required."
        }
    }
} 'runner wires reviewed profile and durable-result functions'

Invoke-Check {
    if ($runtimeNames.Count -ne 2 -or
        $runtimeNames -notcontains 'DOTNET_ENVIRONMENT' -or
        $runtimeNames -notcontains 'ASPNETCORE_ENVIRONMENT') {
        throw 'The reviewed runtime-environment set changed.'
    }
} 'exact runtime-environment isolation set'

Invoke-Check {
    $policy = Get-RebornPhase4AcceptanceProfilePolicy 'baseline'
    $environment = [ordered]@{
        DOTNET_ENVIRONMENT = 'Inherited'
        ASPNETCORE_ENVIRONMENT = 'Inherited'
    }
    Set-RebornPhase4AcceptanceProfileEnvironment `
        $environment $policy.EvidenceProfile
    if ($policy.EvidenceProfile -cne 'Baseline' -or
        $policy.FaultsEnabled -or
        $environment.Contains('DOTNET_ENVIRONMENT') -or
        $environment.Contains('ASPNETCORE_ENVIRONMENT') -or
        $environment[
            'GODSWAR_SECURE_PHASE4_ACCEPTANCE_FAULTS_ENABLED'
        ] -cne 'false') {
        throw 'Baseline profile mapping is invalid.'
    }
} 'case-insensitive Baseline mapping removes runtime profile'

Invoke-Check {
    $policy = Get-RebornPhase4AcceptanceProfilePolicy 'fallback'
    $environment = [ordered]@{}
    Set-RebornPhase4AcceptanceProfileEnvironment `
        $environment $policy.EvidenceProfile
    if ($policy.EvidenceProfile -cne 'Fallback' -or
        -not $policy.FaultsEnabled -or
        $environment.DOTNET_ENVIRONMENT -cne 'Development' -or
        $environment.ASPNETCORE_ENVIRONMENT -cne 'Development' -or
        $environment[
            'GODSWAR_SECURE_PHASE4_ACCEPTANCE_FAULTS_ENABLED'
        ] -cne 'true') {
        throw 'Fallback profile mapping is invalid.'
    }
} 'case-insensitive Fallback mapping enables reviewed faults'

Invoke-Check {
    $policy = Get-RebornPhase4AcceptanceProfilePolicy 'sOaK'
    $environment = [ordered]@{
        DOTNET_ENVIRONMENT = 'Development'
    }
    Set-RebornPhase4AcceptanceProfileEnvironment `
        $environment $policy.EvidenceProfile
    if ($policy.EvidenceProfile -cne 'Soak' -or
        $policy.FaultsEnabled -or
        $environment.Contains('DOTNET_ENVIRONMENT') -or
        $environment.Contains('ASPNETCORE_ENVIRONMENT') -or
        $environment[
            'GODSWAR_SECURE_PHASE4_ACCEPTANCE_FAULTS_ENABLED'
        ] -cne 'false') {
        throw 'Soak profile mapping is invalid.'
    }
} 'mixed-case Soak mapping removes runtime profile'

Assert-Throws {
    Get-RebornPhase4AcceptanceProfilePolicy 'Unknown' | Out-Null
} 'unknown profile is rejected'

Invoke-Check {
    $hash = 'A' * 64
    $evidence = [pscustomobject]@{
        Path = Join-Path $temporary 'secure-server-fixture.log'
        Bytes = 128
        Events = 2
        ObservedDurationSeconds = 12.5
    }
    $record = New-RebornPhase4AcceptanceProfileRecord `
        -CampaignId '4d5961c3-7c87-49ed-9df5-1056d6d16e78' `
        -IssuedUserSid (
            [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        ) `
        -EvidenceProfile 'fallback' `
        -ObservedDurationSeconds $evidence.ObservedDurationSeconds `
        -EvidencePath $evidence.Path `
        -EvidenceSha256 $hash `
        -EvidenceBytes $evidence.Bytes `
        -EvidenceEvents $evidence.Events `
        -ServerSha256 $hash `
        -ManagedReleaseSetSha256 $hash `
        -OptionsSha256 $hash `
        -CandidateSha256 $hash `
        -ManifestSha256 $hash `
        -DatabaseName 'godswar_secure_dev'
    $profileResult =
        Write-RebornPhase4AcceptanceProfileResult `
            $record `
            -SetAclAction $aclTestHook `
            -AllowTestHook
    $resultBytes =
        [IO.File]::ReadAllBytes($profileResult.ProfileResultPath)
    $checksum = (
        Get-Content -LiteralPath `
            $profileResult.ProfileResultChecksumPath -Raw
    ).Trim()
    if ($resultBytes.Length -le 0 -or
        $resultBytes.Length -gt 8KB -or
        ($resultBytes.Length -ge 3 -and
            $resultBytes[0] -eq 0xEF -and
            $resultBytes[1] -eq 0xBB -and
            $resultBytes[2] -eq 0xBF) -or
        $checksum -cne $profileResult.ProfileResultSha256 -or
        (Get-FileHash -LiteralPath `
            $profileResult.ProfileResultPath -Algorithm SHA256
        ).Hash -cne $checksum -or
        $capturedAclPaths.Count -ne 2 -or
        $capturedAclPaths -cnotcontains
            $profileResult.ProfileResultPath -or
        $capturedAclPaths -cnotcontains
            $profileResult.ProfileResultChecksumPath) {
        throw 'Durable profile-result bytes or checksum are invalid.'
    }
    $result = New-RebornPhase4LoopbackAcceptanceResult `
        -EvidenceProfile 'fallback' `
        -EvidenceResult $evidence `
        -ServerSha256 $hash `
        -ManagedReleaseSetSha256 $hash `
        -OptionsSha256 $hash `
        -DatabaseName 'godswar_secure_dev' `
        -SecureTcpPorts @(6599, 7443) `
        -SecureUdpPort 7444 `
        -EvidenceSha256 $hash `
        -ProfileResult $profileResult
    if ($result.Result -cne 'Accepted' -or
        $result.EvidenceProfile -cne 'Fallback' -or
        $result.EvidencePath -cne $evidence.Path -or
        $result.ObservedDurationSeconds -ne 12.5 -or
        $result.SecureTcpPorts -cne '6599,7443' -or
        $result.SecureUdpPort -ne 7444 -or
        $result.ProfileResultPath -cne
            $profileResult.ProfileResultPath -or
        $result.ProfileResultSha256 -cne $checksum) {
        throw 'Acceptance result production changed.'
    }
} 'canonical checksummed durable result production'

Assert-Throws {
    $hash = 'A' * 64
    $record = New-RebornPhase4AcceptanceProfileRecord `
        -CampaignId '4d5961c3-7c87-49ed-9df5-1056d6d16e78' `
        -IssuedUserSid (
            [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        ) `
        -EvidenceProfile 'Baseline' `
        -ObservedDurationSeconds 1 `
        -EvidencePath (
            Join-Path $temporary 'secure-server-fixture.log'
        ) `
        -EvidenceSha256 $hash `
        -EvidenceBytes 1 `
        -EvidenceEvents 1 `
        -ServerSha256 $hash `
        -ManagedReleaseSetSha256 $hash `
        -OptionsSha256 $hash `
        -CandidateSha256 $hash `
        -ManifestSha256 $hash `
        -DatabaseName 'godswar_secure_dev'
    Write-RebornPhase4AcceptanceProfileResult $record |
        Out-Null
} 'durable result refuses overwrite'

Invoke-Check {
    $hash = 'A' * 64
    $partialRecord = New-RebornPhase4AcceptanceProfileRecord `
        -CampaignId '4d5961c3-7c87-49ed-9df5-1056d6d16e78' `
        -IssuedUserSid $issuedUserSid `
        -EvidenceProfile 'Baseline' `
        -ObservedDurationSeconds 1 `
        -EvidencePath (
            Join-Path $temporary 'secure-server-partial.log'
        ) `
        -EvidenceSha256 $hash `
        -EvidenceBytes 1 `
        -EvidenceEvents 1 `
        -ServerSha256 $hash `
        -ManagedReleaseSetSha256 $hash `
        -OptionsSha256 $hash `
        -CandidateSha256 $hash `
        -ManifestSha256 $hash `
        -DatabaseName 'godswar_secure_dev'
    $partialPaths =
        Get-RebornPhase4AcceptanceProfileResultPaths `
            $partialRecord.evidencePath
    $protectedPartialPaths =
        [Collections.Generic.List[string]]::new()
    $partialAclHook = {
        param($path, $security)
        $protectedPartialPaths.Add([string]$path)
    }.GetNewClosure()
    $failed = $false
    try {
        Write-RebornPhase4AcceptanceProfileResult `
            $partialRecord `
            -SetAclAction $partialAclHook `
            -BeforeChecksumWriteAction {
                throw 'Injected checksum interruption.'
            } `
            -AllowTestHook | Out-Null
    }
    catch {
        $failed = $true
    }
    if (-not $failed -or
        -not (Test-Path -LiteralPath `
            $partialPaths.ResultPath -PathType Leaf) -or
        (Test-Path -LiteralPath `
            $partialPaths.ChecksumPath -PathType Leaf) -or
        $protectedPartialPaths.Count -ne 1 -or
        $protectedPartialPaths[0] -cne $partialPaths.ResultPath) {
        throw 'Interrupted profile-result protection is invalid.'
    }
} 'interrupted result protects the partial durable artifact'

Invoke-Check {
    foreach ($name in $runtimeNames) {
        $after = [Environment]::GetEnvironmentVariable(
            $name,
            [EnvironmentVariableTarget]::Process)
        if ($after -cne $processEnvironmentBefore[$name]) {
            throw "The offline harness changed process environment: $name"
        }
    }
} 'offline harness leaves process environment unchanged'

if ($passed -ne $expectedChecks) {
    throw "Expected $expectedChecks checks, got $passed."
}
Write-Host "Phase 4 loopback runner checks passed: $passed"
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        $resolvedTemporary =
            [IO.Path]::GetFullPath($temporary).TrimEnd('\')
        $temporaryBase =
            [IO.Path]::GetFullPath(
                [IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedTemporary.StartsWith(
                $temporaryBase,
                [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTemporary) -notmatch
                '^reborn-phase4-runner-test-[0-9a-f]{32}$') {
            throw 'Test cleanup target escaped its issued temporary scope.'
        }
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
