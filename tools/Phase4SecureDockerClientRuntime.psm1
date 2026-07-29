Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'Phase4SecureDockerClientCampaign.psm1'
)

$script:RootSubject = 'CN=Reborn Development Root CA'
$script:MaximumDockerOutputBytes = 1MB

function Get-RebornPhase4RuntimeSha256 {
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

function Invoke-RebornPhase4DockerInspect {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'docker.exe'
    $start.Arguments = 'inspect godswar-server godswar-postgres'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            throw 'Docker inspection did not start.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) {
            try { $process.Kill() } catch {}
            throw 'Docker inspection exceeded its 10-second deadline.'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($stdout.Length -gt $script:MaximumDockerOutputBytes -or
            $stderr.Length -gt 4096) {
            throw 'Docker inspection output exceeded its bound.'
        }
        if ($process.ExitCode -ne 0) {
            throw "Docker inspection failed with exit code $($process.ExitCode)."
        }
        return @($stdout | ConvertFrom-Json)
    }
    finally {
        $process.Dispose()
    }
}

function Get-RebornPhase4EnvironmentMap {
    param([object[]]$Values)

    $result = @{}
    foreach ($entry in @($Values)) {
        $separator = ([string]$entry).IndexOf('=')
        if ($separator -le 0) {
            throw 'Docker environment contains an invalid entry.'
        }
        $name = ([string]$entry).Substring(0, $separator)
        if ($result.ContainsKey($name)) {
            throw 'Docker environment contains a duplicate key.'
        }
        $result[$name] = ([string]$entry).Substring($separator + 1)
    }
    return $result
}

function Assert-RebornPhase4DockerInspection {
    param(
        [Parameter(Mandatory)][object[]]$Containers,
        [Parameter(Mandatory)][object[]]$TcpListeners,
        [Parameter(Mandatory)][object[]]$UdpListeners,
        [object]$Pins = (Get-RebornPhase4SecureDockerPins)
    )

    $normalized = @(
        foreach ($entry in $Containers) {
            if ($entry -is [Array]) {
                @($entry)
            } else {
                $entry
            }
        })
    if ($normalized.Count -gt 8) {
        throw 'Docker inspection container count exceeded its bound.'
    }
    $server = @(
        $normalized |
            Where-Object {
                $_.Name -ceq ('/' + $Pins.ServerContainer)
            })
    $postgres = @(
        $normalized |
            Where-Object {
                $_.Name -ceq ('/' + $Pins.PostgresContainer)
            })
    if ($server.Count -ne 1 -or $postgres.Count -ne 1) {
        throw 'Secure Docker containers are not uniquely present.'
    }
    $server = $server[0]
    $postgres = $postgres[0]
    if (-not $server.State.Running -or
        $server.State.Health.Status -cne 'healthy' -or
        [int]$server.RestartCount -ne 0 -or
        -not $postgres.State.Running -or
        $postgres.State.Health.Status -cne 'healthy') {
        throw 'Secure Docker containers are not healthy and stable.'
    }
    if ($server.Config.Labels.'com.reborn.network.profile' -cne
        $Pins.DockerProfile) {
        throw 'Docker server is not the secure-hybrid profile.'
    }
    $networkNames = @($server.NetworkSettings.Networks.PSObject.Properties.Name)
    if ($networkNames.Count -ne 1 -or
        $networkNames[0] -cne $Pins.DockerNetwork) {
        throw 'Secure Docker server network scope is not exact.'
    }

    $expectedPorts = @('6599/tcp', '7443/tcp', '7444/udp')
    $actualPorts = @($server.NetworkSettings.Ports.PSObject.Properties.Name)
    if (@($actualPorts | Where-Object { $_ -notin $expectedPorts }).Count -or
        @($expectedPorts | Where-Object { $_ -notin $actualPorts }).Count) {
        throw 'Secure Docker published port set is not exact.'
    }
    foreach ($port in $expectedPorts) {
        $binding = @(
            $server.NetworkSettings.Ports.PSObject.Properties[$port].Value)
        $number = $port.Split('/')[0]
        if ($binding.Count -ne 1 -or
            $binding[0].HostIp -cne '127.0.0.1' -or
            $binding[0].HostPort -cne $number) {
            throw "Secure Docker binding is not exact: $port"
        }
    }

    $environment = Get-RebornPhase4EnvironmentMap $server.Config.Env
    foreach ($required in @{
        GODSWAR_RUNTIME_PROFILE = 'LocalDevelopment'
        GODSWAR_SECURE_ENABLED = 'true'
        GODSWAR_SECURE_UDP_ENABLED = 'true'
        GODSWAR_SECURE_UDP_GAMEPLAY_MOVEMENT_ENABLED = 'true'
        GODSWAR_SECURE_PHASE4_ACCEPTANCE_FAULTS_ENABLED = 'false'
        GODSWAR_AUTH_ALLOW_REGISTRATION = 'false'
    }.GetEnumerator()) {
        if (-not $environment.ContainsKey($required.Key) -or
            $environment[$required.Key] -cne $required.Value) {
            throw "Secure Docker policy is not exact: $($required.Key)"
        }
    }
    if (-not $environment.ContainsKey(
            'GODSWAR_POSTGRES_CONNECTION_STRING') -or
        $environment.GODSWAR_POSTGRES_CONNECTION_STRING -notmatch
            '(^|;)Database=godswar_secure_dev(;|$)') {
        throw 'Secure Docker database scope is not godswar_secure_dev.'
    }

    foreach ($port in 6599, 7443) {
        $matches = @($TcpListeners | Where-Object LocalPort -eq $port)
        if ($matches.Count -ne 1 -or
            [string]$matches[0].LocalAddress -cne '127.0.0.1') {
            throw "Host secure TCP listener is not exact: $port"
        }
    }
    if (@(
            $TcpListeners |
                Where-Object { $_.LocalPort -in 5998,5999,7000 }
        ).Count) {
        throw 'A raw game listener is active during secure acceptance.'
    }
    $udp = @($UdpListeners | Where-Object LocalPort -eq 7444)
    if ($udp.Count -ne 1 -or
        [string]$udp[0].LocalAddress -cne '127.0.0.1') {
        throw 'Host secure UDP listener is not exact: 7444'
    }
    return [pscustomobject]@{
        State = 'HealthyExact'
        Profile = $Pins.DockerProfile
        Database = $Pins.DockerDatabase
        TcpPorts = @(6599, 7443)
        UdpPort = 7444
        RestartCount = [int]$server.RestartCount
    }
}

function Assert-RebornPhase4SecureDockerRuntime {
    param([object]$Pins = (Get-RebornPhase4SecureDockerPins))

    $containers = Invoke-RebornPhase4DockerInspect
    $tcp = @(
        Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
            Where-Object {
                $_.LocalPort -in 5998,5999,6599,7000,7443
            })
    $udp = @(
        Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
            Where-Object LocalPort -eq 7444)
    return Assert-RebornPhase4DockerInspection `
        $containers $tcp $udp $Pins
}

function Get-RebornPhase4RootStatus {
    param([object]$Pins = (Get-RebornPhase4SecureDockerPins))

    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::Root,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    $store.Open(
        [Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
    try {
        $subjectMatches = @(
            $store.Certificates |
                Where-Object Subject -ceq $script:RootSubject)
        $exact = @(
            $subjectMatches |
                Where-Object Thumbprint -ceq $Pins.RootThumbprint)
        if ($subjectMatches.Count -eq 0) {
            return [pscustomobject]@{
                State = 'Absent'
                MatchCount = 0
                Thumbprint = $Pins.RootThumbprint
            }
        }
        if ($subjectMatches.Count -ne 1 -or $exact.Count -ne 1 -or
            (Get-RebornPhase4RuntimeSha256 $exact[0].RawData) -cne
                $Pins.RootCertificateSha256 -or
            $exact[0].HasPrivateKey) {
            return [pscustomobject]@{
                State = 'Conflict'
                MatchCount = $subjectMatches.Count
                Thumbprint = $Pins.RootThumbprint
            }
        }
        return [pscustomobject]@{
            State = 'InstalledExact'
            MatchCount = 1
            Thumbprint = $Pins.RootThumbprint
        }
    }
    finally {
        $store.Close()
        $store.Dispose()
    }
}

function Add-RebornPhase4Root {
    param([object]$Pins = (Get-RebornPhase4SecureDockerPins))

    if ((Get-RebornPhase4RootStatus $Pins).State -cne 'Absent') {
        throw 'Phase 4 root installation requires exact absent state.'
    }
    $certificate =
        [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $Pins.RootCertificatePath)
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::Root,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $store.Add($certificate)
    }
    finally {
        $store.Close()
        $store.Dispose()
        $certificate.Dispose()
    }
    if ((Get-RebornPhase4RootStatus $Pins).State -cne 'InstalledExact') {
        throw 'Phase 4 root installation did not reach exact state.'
    }
}

function Remove-RebornPhase4Root {
    param([object]$Pins = (Get-RebornPhase4SecureDockerPins))

    $status = Get-RebornPhase4RootStatus $Pins
    if ($status.State -eq 'Absent') {
        return 'AlreadyAbsent'
    }
    if ($status.State -ne 'InstalledExact') {
        throw 'Phase 4 root removal authority does not match the store.'
    }
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::Root,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    $store.Open(
        [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $matches = @(
            $store.Certificates |
                Where-Object Thumbprint -ceq $Pins.RootThumbprint)
        if ($matches.Count -ne 1 -or
            (Get-RebornPhase4RuntimeSha256 $matches[0].RawData) -cne
                $Pins.RootCertificateSha256) {
            throw 'Exact Phase 4 root changed before removal.'
        }
        $store.Remove($matches[0])
    }
    finally {
        $store.Close()
        $store.Dispose()
    }
    if ((Get-RebornPhase4RootStatus $Pins).State -cne 'Absent') {
        throw 'Exact Phase 4 root remains after removal.'
    }
    return 'Removed'
}

Export-ModuleMember -Function @(
    'Assert-RebornPhase4DockerInspection',
    'Assert-RebornPhase4SecureDockerRuntime',
    'Get-RebornPhase4RootStatus',
    'Add-RebornPhase4Root',
    'Remove-RebornPhase4Root'
)
