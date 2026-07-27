#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(100, 10000)]
    [int]$ConnectTimeoutMilliseconds = 3000,

    [ValidateRange(100, 10000)]
    [int]$IoTimeoutMilliseconds = 3000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (
    Join-Path $PSScriptRoot 'Phase4LoopbackStopControl.psm1'
) -Force

Invoke-RebornPhase4LoopbackGracefulStop `
    -ConnectTimeoutMilliseconds $ConnectTimeoutMilliseconds `
    -IoTimeoutMilliseconds $IoTimeoutMilliseconds
