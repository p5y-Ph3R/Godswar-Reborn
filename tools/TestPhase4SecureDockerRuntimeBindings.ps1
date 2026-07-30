$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (
    Join-Path $PSScriptRoot 'Phase4SecureDockerClientCampaign.psm1'
) -Force
Import-Module (
    Join-Path $PSScriptRoot 'Phase4SecureDockerClientRuntime.psm1'
) -Force

$pins = Get-RebornPhase4SecureDockerPins

try {
    Assert-RebornPhase4DockerInspection `
        -Containers @() `
        -TcpListeners @() `
        -UdpListeners @() `
        -Pins $pins | Out-Null
}
catch {
    if ($_.Exception.Message -cne
        'Secure Docker containers are not uniquely present.') {
        throw (
            'Empty listener collections did not reach the bounded ' +
            "runtime-policy check: $($_.Exception.Message)")
    }

    Write-Host (
        'PASS empty listener collections reach the secure Docker ' +
        'runtime-policy check')
    exit 0
}

throw 'Expected the empty secure Docker inspection fixture to be rejected.'
