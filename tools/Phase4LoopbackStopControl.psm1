Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:PipeName = 'reborn-phase4-controlled-host-shutdown-v1'
$script:Request = [Text.Encoding]::ASCII.GetBytes(
    "REBORN_PHASE4_STOP_V1`n")
$script:Acknowledgement = [Text.Encoding]::ASCII.GetBytes(
    "REBORN_PHASE4_STOP_ACCEPTED_V1`n")

function Wait-RebornPipeOperation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [IAsyncResult]$Operation,

        [Parameter(Mandatory)]
        [int]$TimeoutMilliseconds,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not $Operation.AsyncWaitHandle.WaitOne($TimeoutMilliseconds)) {
        throw "Timed out while $Description."
    }
}

function Assert-RebornStopTimeout {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$Value,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($Value -lt 100 -or $Value -gt 10000) {
        throw "$Name must be between 100 and 10000 milliseconds."
    }
}

function Invoke-RebornPhase4LoopbackGracefulStop {
    [CmdletBinding()]
    param(
        [ValidateRange(100, 10000)]
        [int]$ConnectTimeoutMilliseconds = 3000,

        [ValidateRange(100, 10000)]
        [int]$IoTimeoutMilliseconds = 3000
    )

    Assert-RebornStopTimeout `
        $ConnectTimeoutMilliseconds 'ConnectTimeoutMilliseconds'
    Assert-RebornStopTimeout `
        $IoTimeoutMilliseconds 'IoTimeoutMilliseconds'
    if ([Environment]::OSVersion.Platform -ne
        [PlatformID]::Win32NT) {
        throw 'The Phase 4 same-user stop control is Windows-only.'
    }

    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        '.',
        $script:PipeName,
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect($ConnectTimeoutMilliseconds)
        $pipe.ReadMode = [IO.Pipes.PipeTransmissionMode]::Message

        $write = $pipe.BeginWrite(
            $script:Request,
            0,
            $script:Request.Length,
            $null,
            $null)
        try {
            Wait-RebornPipeOperation `
                $write $IoTimeoutMilliseconds 'writing the stop request'
            $pipe.EndWrite($write)
        }
        finally {
            $write.AsyncWaitHandle.Dispose()
        }

        $response = [byte[]]::new($script:Acknowledgement.Length)
        $offset = 0
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($offset -lt $response.Length) {
            $remaining = $IoTimeoutMilliseconds -
                [int]$timer.ElapsedMilliseconds
            if ($remaining -le 0) {
                throw 'Timed out while reading the stop acknowledgement.'
            }

            $read = $pipe.BeginRead(
                $response,
                $offset,
                $response.Length - $offset,
                $null,
                $null)
            try {
                Wait-RebornPipeOperation `
                    $read $remaining 'reading the stop acknowledgement'
                $count = $pipe.EndRead($read)
            }
            finally {
                $read.AsyncWaitHandle.Dispose()
            }
            if ($count -eq 0) {
                throw 'The stop control closed without acknowledgement.'
            }
            $offset += $count
        }
        if (-not $pipe.IsMessageComplete) {
            throw 'The stop control returned an oversized acknowledgement.'
        }

        if (-not [Collections.StructuralComparisons]::
            StructuralEqualityComparer.Equals(
                $response,
                $script:Acknowledgement)) {
            throw 'The stop control returned an invalid acknowledgement.'
        }

        [pscustomobject]@{
            Success = $true
            PipeName = $script:PipeName
            Acknowledgement = [Text.Encoding]::ASCII.GetString(
                $response).Trim()
        }
    }
    finally {
        $pipe.Dispose()
    }
}

Export-ModuleMember -Function Invoke-RebornPhase4LoopbackGracefulStop
