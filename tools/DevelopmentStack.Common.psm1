Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:ComposePath = Join-Path $script:RepositoryRoot 'docker-compose.dev.yml'
$script:DefaultConfigurationDirectory = Join-Path `
    $script:RepositoryRoot 'artifacts\development-stack'

function Protect-DevelopmentPrivateDirectory {
    param([Parameter(Mandatory)][string]$LiteralPath)

    $resolved = [IO.Path]::GetFullPath($LiteralPath)
    $volumeRoot = [IO.Path]::GetPathRoot($resolved).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $bounded = $resolved.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if ($bounded -ceq $volumeRoot) {
        throw 'A filesystem root cannot be a private development directory.'
    }
    [IO.Directory]::CreateDirectory($resolved) | Out-Null
    $directory = Get-Item -LiteralPath $resolved -Force
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Private development directory cannot be a reparse point: $resolved"
    }

    if ($env:OS -eq 'Windows_NT') {
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $result = & icacls.exe `
            $resolved `
            '/inheritance:r' `
            '/grant:r' "*$sid`:(OI)(CI)(F)" 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Could not protect private development directory: $result"
        }
        $acl = Get-Acl -LiteralPath $resolved
        $inherited = @($acl.Access | Where-Object { $_.IsInherited })
        $ownerRules = @($acl.Access | Where-Object {
            try {
                $_.IdentityReference.Translate(
                    [Security.Principal.SecurityIdentifier]).Value -ceq $sid
            }
            catch {
                $false
            }
        })
        $unexpectedAllows = @($acl.Access | Where-Object {
            if ($_.AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow) {
                return $false
            }
            try {
                $_.IdentityReference.Translate(
                    [Security.Principal.SecurityIdentifier]).Value -cne $sid
            }
            catch {
                $true
            }
        })
        if (-not $acl.AreAccessRulesProtected -or
            $inherited.Count -ne 0 -or
            $unexpectedAllows.Count -ne 0 -or
            -not ($ownerRules | Where-Object {
                $_.AccessControlType -eq
                    [Security.AccessControl.AccessControlType]::Allow -and
                ($_.FileSystemRights -band
                    [Security.AccessControl.FileSystemRights]::FullControl) -ne 0
            })) {
            throw 'Private development directory ACL verification failed.'
        }
    }
    else {
        & chmod 700 $resolved
        if ($LASTEXITCODE -ne 0) {
            throw "Could not protect private development directory: $resolved"
        }
    }

    return $resolved
}

function Protect-DevelopmentPrivateFile {
    param([Parameter(Mandatory)][string]$LiteralPath)

    $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Private development secret must be a regular file.'
    }
    if ($env:OS -eq 'Windows_NT') {
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $output = & icacls.exe `
            $item.FullName '/inheritance:r' '/grant:r' "*$sid`:(F)" 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Could not restrict private development file: $output"
        }
        $acl = Get-Acl -LiteralPath $item.FullName
        $unexpectedAllows = @($acl.Access | Where-Object {
            if ($_.AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow) {
                return $false
            }
            try {
                $_.IdentityReference.Translate(
                    [Security.Principal.SecurityIdentifier]).Value -cne $sid
            }
            catch {
                $true
            }
        })
        $ownerFullControl = @($acl.Access | Where-Object {
            try {
                $isOwner = $_.IdentityReference.Translate(
                    [Security.Principal.SecurityIdentifier]).Value -ceq $sid
            }
            catch {
                $isOwner = $false
            }
            $isOwner -and -not $_.IsInherited -and
                $_.AccessControlType -eq
                    [Security.AccessControl.AccessControlType]::Allow -and
                ($_.FileSystemRights -band
                    [Security.AccessControl.FileSystemRights]::FullControl) -ne 0
        })
        if (-not $acl.AreAccessRulesProtected -or
            @($acl.Access | Where-Object { $_.IsInherited }).Count -ne 0 -or
            $unexpectedAllows.Count -ne 0 -or
            $ownerFullControl.Count -lt 1) {
            throw 'Private development file ACL verification failed.'
        }
    }
    else {
        & chmod 600 $item.FullName
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not restrict private development file.'
        }
    }

    return $item.FullName
}

function Read-DevelopmentSecretFile {
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [ValidateRange(1, 65536)][int]$MaximumBytes = 4096
    )

    if (-not [IO.Path]::IsPathRooted($LiteralPath)) {
        throw 'Development secret-file path must be absolute.'
    }
    $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Development secret file cannot be a reparse point.'
    }

    $bytes = [byte[]]::new($MaximumBytes + 1)
    try {
        $stream = [IO.FileStream]::new(
            $item.FullName,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read,
            $MaximumBytes,
            [IO.FileOptions]::SequentialScan)
        try {
            $count = 0
            while ($count -lt $bytes.Length) {
                $read = $stream.Read($bytes, $count, $bytes.Length - $count)
                if ($read -eq 0) { break }
                $count += $read
            }
        }
        finally {
            $stream.Dispose()
        }
        if ($count -lt 1 -or $count -gt $MaximumBytes) {
            throw "Development secret must contain 1-$MaximumBytes bytes."
        }
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        $value = $encoding.GetString($bytes, 0, $count).TrimEnd("`r", "`n")
        if ([string]::IsNullOrWhiteSpace($value) -or
            $value.IndexOf("`0", [StringComparison]::Ordinal) -ge 0 -or
            $value.IndexOf("`r", [StringComparison]::Ordinal) -ge 0 -or
            $value.IndexOf("`n", [StringComparison]::Ordinal) -ge 0) {
            throw 'Development secret contains invalid control characters.'
        }
        return $value
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Get-DevelopmentRepositoryRoot {
    $script:RepositoryRoot
}

function Get-DevelopmentConfigurationDirectory {
    param([string]$ConfigurationDirectory)

    if ([string]::IsNullOrWhiteSpace($ConfigurationDirectory)) {
        return [IO.Path]::GetFullPath($script:DefaultConfigurationDirectory)
    }
    return [IO.Path]::GetFullPath($ConfigurationDirectory)
}

function Get-DevelopmentEnvironmentPath {
    param([string]$ConfigurationDirectory)

    Join-Path `
        (Get-DevelopmentConfigurationDirectory $ConfigurationDirectory) `
        'development.local.env'
}

function Get-DevelopmentComposeArguments {
    param([string]$EnvironmentFile)

    $resolvedEnvironment = [IO.Path]::GetFullPath($EnvironmentFile)
    if (-not (Test-Path -LiteralPath $resolvedEnvironment -PathType Leaf)) {
        throw "Development environment file is missing: $resolvedEnvironment"
    }
    @(
        'compose'
        '--project-name', 'reborn-dev'
        '--env-file', $resolvedEnvironment
        '--file', $script:ComposePath
    )
}

function Get-DotEnvValue {
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "Environment file is missing: $LiteralPath"
    }
    $matches = @(
        Get-Content -LiteralPath $LiteralPath | ForEach-Object {
            if ($_ -match '^\s*#' -or [string]::IsNullOrWhiteSpace($_)) {
                return
            }
            $separator = $_.IndexOf('=')
            if ($separator -le 0) {
                return
            }
            if ($_.Substring(0, $separator).Trim() -ceq $Name) {
                $_.Substring($separator + 1).Trim()
            }
        }
    )
    if ($matches.Count -ne 1 -or [string]::IsNullOrWhiteSpace($matches[0])) {
        throw "Environment value '$Name' is missing or duplicated."
    }
    return [string]$matches[0]
}

function Get-DockerContainer {
    param([Parameter(Mandatory)][string]$Name)

    $container = TryGet-DockerContainer $Name
    if ($null -eq $container) {
        throw "Docker container '$Name' does not exist."
    }
    return $container
}

function TryGet-DockerContainer {
    param([Parameter(Mandatory)][string]$Name)

    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $raw = @(& docker inspect --type container $Name 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
    if ($exitCode -eq 0) {
        return @($raw | ConvertFrom-Json)[0]
    }
    $message = ($raw -join "`n")
    if ($exitCode -eq 1 -and
        $message -match '(?i)no such (object|container)') {
        return $null
    }
    throw "Could not inspect Docker container '$Name': $message"
}

function Get-MainObservationGuard {
    $activePath = Join-Path `
        $script:DefaultConfigurationDirectory '..\b20h-observation\active-observation.json'
    $activePath = [IO.Path]::GetFullPath($activePath)
    $status = $null
    $observationActive = Test-Path -LiteralPath $activePath -PathType Leaf
    if ($observationActive) {
        $status = & (Join-Path $PSScriptRoot 'GetB20HDockerObservation.ps1') |
            ConvertFrom-Json
        $continuityHealthy =
            [int]$status.TargetUp -eq 1 -and
            [int]$status.ObserverReady -eq 1 -and
            [int]$status.RedisCoordinationReady -eq 1 -and
            [bool]$status.RevisionMatchesApproval -and
            [bool]$status.ServerIdentityMatchesStart -and
            [bool]$status.PrometheusIdentityMatchesStart -and
            [bool]$status.PostgreSqlVolumeMatchesStart -and
            [bool]$status.ObservationArtifactHashesMatch -and
            [bool]$status.ComposeInputHashesMatch -and
            [bool]$status.ServerRedisTopologyMatchesStart -and
            [bool]$status.RedisIdentityMatchesStart -and
            [string]$status.RedisHealth -ceq 'healthy'
        if (-not $continuityHealthy) {
            throw (
                'Active B20H observation continuity is not healthy: ' +
                [string]$status.CurrentStatus)
        }
    }

    $names = @(
        'godswar-server'
        'godswar-main-redis-coordination'
        'godswar-b20h-prometheus'
        'godswar-postgres'
    )
    $containers = [ordered]@{}
    foreach ($name in $names) {
        $container = if ($observationActive) {
            Get-DockerContainer $name
        }
        else {
            TryGet-DockerContainer $name
        }
        if ($null -eq $container) {
            $containers[$name] = [ordered]@{ exists = $false }
            continue
        }
        $healthProperty = $container.State.PSObject.Properties['Health']
        $health = if ($null -eq $healthProperty -or
            $null -eq $healthProperty.Value) {
            ''
        }
        else {
            [string]$healthProperty.Value.Status
        }
        $containers[$name] = [ordered]@{
            exists = $true
            id = [string]$container.Id
            startedAt = [string]$container.State.StartedAt
            restartCount = [long]$container.RestartCount
            status = [string]$container.State.Status
            health = $health
        }
    }
    $postgresVolume = $null
    if ($containers['godswar-postgres'].exists) {
        $postgres = Get-DockerContainer 'godswar-postgres'
        $postgresVolumes = @($postgres.Mounts | Where-Object {
            $_.Type -ceq 'volume' -and
            $_.Destination -ceq '/var/lib/postgresql/data'
        })
        if ($postgresVolumes.Count -ne 1 -or
            [string]$postgresVolumes[0].Name -cne
                'reborn_godswar-postgres-data') {
            throw 'The authoritative PostgreSQL volume identity is unexpected.'
        }
        $postgresVolume = [string]$postgresVolumes[0].Name
    }

    [pscustomobject]@{
        CapturedAtUtc = [DateTimeOffset]::UtcNow.UtcDateTime.ToString('O')
        ObservationActive = $observationActive
        ObservationStatus = $status
        PostgreSqlVolume = $postgresVolume
        Containers = $containers
    }
}

function Assert-MainObservationGuardUnchanged {
    param([Parameter(Mandatory)]$Before)

    $after = Get-MainObservationGuard
    if ([bool]$after.ObservationActive -ne
            [bool]$Before.ObservationActive) {
        throw 'The B20H observation active state changed during dev setup.'
    }
    if ([string]$after.PostgreSqlVolume -cne
            [string]$Before.PostgreSqlVolume) {
        throw 'The authoritative PostgreSQL volume changed during dev setup.'
    }
    foreach ($name in $Before.Containers.Keys) {
        $expected = $Before.Containers[$name]
        $actual = $after.Containers[$name]
        if ([bool]$actual.exists -ne [bool]$expected.exists) {
            throw "Main container '$name' presence changed during dev setup."
        }
        if (-not [bool]$expected.exists) { continue }
        if ($actual.id -cne $expected.id -or
            $actual.startedAt -cne $expected.startedAt -or
            [long]$actual.restartCount -ne [long]$expected.restartCount) {
            throw "Monitored container '$name' changed during dev setup."
        }
    }
    return $after
}

function Assert-DevelopmentContainer {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Service
    )

    $container = Get-DockerContainer $Name
    if ([string]$container.Config.Labels.'com.docker.compose.project' -cne
            'reborn-dev' -or
        [string]$container.Config.Labels.'com.docker.compose.service' -cne
            $Service -or
        [string]$container.Config.Labels.'com.reborn.environment.scope' -cne
            'isolated-development') {
        throw "Container '$Name' is outside the isolated dev scope."
    }
    return $container
}

Export-ModuleMember -Function @(
    'Protect-DevelopmentPrivateDirectory'
    'Protect-DevelopmentPrivateFile'
    'Read-DevelopmentSecretFile'
    'Get-DevelopmentRepositoryRoot'
    'Get-DevelopmentConfigurationDirectory'
    'Get-DevelopmentEnvironmentPath'
    'Get-DevelopmentComposeArguments'
    'Get-DotEnvValue'
    'Get-DockerContainer'
    'Get-MainObservationGuard'
    'Assert-MainObservationGuardUnchanged'
    'Assert-DevelopmentContainer'
)
