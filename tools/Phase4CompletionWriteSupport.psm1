Set-StrictMode -Version Latest

function Assert-RebornPhase4CompletionWriteAuthority {
    param([switch]$AllowTestPath)

    if ($AllowTestPath) {
        return
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if ($null -eq $identity.User -or
        $identity.User.Value -ceq 'S-1-5-18' -or
        -not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Completion writes require an elevated issued-user token.'
    }
}

function Assert-RebornPhase4FinalStatus {
    param(
        [Parameter(Mandatory)][object]$Status,
        [Parameter(Mandatory)][object]$DockerStatus,
        [Parameter(Mandatory)][object]$Campaign,
        [Parameter(Mandatory)][object]$Pins
    )

    if ([string]$Status.State -cne 'Restored' -or
        [string]$Status.DockerState -cne 'HealthyExact' -or
        [string]$Status.BundleState -cne 'Stock' -or
        [string]$Status.HostsState -cne 'Absent' -or
        [string]$Status.RootState -cne 'Absent' -or
        [UInt64]$Status.ActivationMode -ne 0 -or
        [UInt64]$Status.ActivationEnvironment -ne
            $Pins.ActivationEnvironment -or
        [UInt64]$Status.SequenceFloor -ne $Pins.ManifestSequence -or
        [UInt64]$Status.ManifestSequence -ne $Pins.ManifestSequence -or
        [string]$Status.HandoffState -cne 'Restored' -or
        -not ([IO.Path]::GetFullPath(
            [string]$Status.HandoffPath)).Equals(
                $Campaign.Path,
                [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Final Phase 4 client Restore state is not exact.'
    }
    if ([string]$DockerStatus.State -cne 'HealthyExact' -or
        [string]$DockerStatus.Profile -cne $Pins.DockerProfile -or
        [string]$DockerStatus.Database -cne $Pins.DockerDatabase -or
        [int]$DockerStatus.RestartCount -ne 0 -or
        [int]$DockerStatus.UdpPort -ne 7444 -or
        @($DockerStatus.TcpPorts).Count -ne 2 -or
        6599 -notin @($DockerStatus.TcpPorts) -or
        7443 -notin @($DockerStatus.TcpPorts)) {
        throw 'Final Phase 4 secure-Docker state is not exact.'
    }
}

function Write-RebornPhase4CompletionFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][byte[]]$Bytes
    )

    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function New-RebornPhase4CompletionProfileBindings {
    param([Parameter(Mandatory)][object[]]$Results)

    return @(
        foreach ($name in @('Baseline', 'Fallback', 'Soak')) {
            $result = @(
                $Results |
                    Where-Object { $_.Record.profile -ceq $name })[0]
            [pscustomobject][ordered]@{
                profile = $name
                profileResultPath = $result.Path
                profileResultSha256 = $result.Sha256
                evidencePath = [string]$result.Record.evidencePath
                evidenceSha256 = [string]$result.Record.evidenceSha256
                observedDurationSeconds =
                    [double]$result.Record.observedDurationSeconds
            }
        })
}

Export-ModuleMember -Function @(
    'Assert-RebornPhase4CompletionWriteAuthority',
    'Assert-RebornPhase4FinalStatus',
    'Write-RebornPhase4CompletionFile',
    'New-RebornPhase4CompletionProfileBindings'
)
