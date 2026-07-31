function Invoke-Docker {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [string]$Operation,
        [switch]$AllowFailure
    )

    $resolvedArguments = @(
        $Arguments | ForEach-Object {
            if ($_ -eq '__GODSWAR_EMPTY_ARGUMENT__') {
                ''
            }
            else {
                $_
            }
        }
    )
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& docker @resolvedArguments 2>&1)
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    $exitCode = $LASTEXITCODE
    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "Docker operation '$Operation' failed."
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = @($output | ForEach-Object { "$_" })
    }
}

function Wait-RedisReady {
    param(
        [Parameter(Mandatory)]
        [string]$Username,
        [Parameter(Mandatory)]
        [string]$Password
    )

    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $probe = Invoke-RedisCli `
            -Username $Username `
            -Password $Password `
            -Command @('PING') `
            -Operation 'Redis readiness probe' `
            -AllowFailure
        if ($probe.ExitCode -eq 0 -and
            ($probe.Output -join "`n").Trim() -eq 'PONG') {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'Disposable Redis did not become ready within 15 seconds.'
}

function Get-RedisPort {
    $portResult = Invoke-Docker `
        -Arguments @('port', $containerName, '6379/tcp') `
        -Operation 'published Redis port discovery'
    $binding = ($portResult.Output | Select-Object -First 1).Trim()
    if ($binding -notmatch ':(\d+)$') {
        throw 'Could not parse the disposable Redis loopback port.'
    }
    return [int]$Matches[1]
}
