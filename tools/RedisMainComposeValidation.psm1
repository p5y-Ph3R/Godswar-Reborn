Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-RedisMainCondition {
    param([bool] $Condition, [string] $Message)

    if (-not $Condition) {
        throw "Redis main Compose validation failed: $Message"
    }
}

function Test-RedisMainFullyQualifiedPath {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or
        -not [IO.Path]::IsPathRooted($Path)) {
        return $false
    }
    if ($env:OS -eq 'Windows_NT') {
        return $Path -match '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+)'
    }
    return $Path.StartsWith('/', [StringComparison]::Ordinal)
}

function ConvertFrom-RedisMainEnvironmentText {
    param(
        [string] $Text,
        [string] $Description
    )

    $values = New-Object `
        'Collections.Generic.Dictionary[string,string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    $lineCount = 0
    foreach ($line in ($Text -split "`r?`n")) {
        $lineCount++
        Assert-RedisMainCondition `
            ($lineCount -le 4096) `
            "$Description exceeds the line-count limit"
        if ([string]::IsNullOrWhiteSpace($line) -or
            $line.TrimStart().StartsWith('#')) {
            continue
        }

        $separator = $line.IndexOf('=')
        Assert-RedisMainCondition `
            ($separator -gt 0) `
            "$Description contains a malformed entry"
        $name = $line.Substring(0, $separator)
        Assert-RedisMainCondition `
            ($name -cmatch '^[A-Za-z_][A-Za-z0-9_]*$') `
            "$Description contains an invalid variable name"
        Assert-RedisMainCondition `
            (-not $values.ContainsKey($name)) `
            "$Description contains a duplicate variable name"
        $values.Add($name, $line.Substring($separator + 1))
        Assert-RedisMainCondition `
            ($values.Count -le 512) `
            "$Description exceeds the variable-count limit"
    }
    return $values
}

function Read-RedisMainEnvironmentFile {
    param(
        [string] $Path,
        [string] $Description
    )

    Assert-RedisMainCondition `
        (Test-Path -LiteralPath $Path -PathType Leaf) `
        "$Description is missing"
    $item = Get-Item -LiteralPath $Path
    Assert-RedisMainCondition `
        ($item.Length -le 1MB) `
        "$Description exceeds the byte limit"
    return ConvertFrom-RedisMainEnvironmentText `
        -Text (Get-Content -LiteralPath $Path -Raw) `
        -Description $Description
}

function Merge-RedisMainEnvironmentValues {
    param(
        [Collections.IDictionary] $BaseValues,
        [Collections.IDictionary] $RedisValues
    )

    $merged = New-Object `
        'Collections.Generic.Dictionary[string,string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($source in @($BaseValues, $RedisValues)) {
        foreach ($entry in $source.GetEnumerator()) {
            Assert-RedisMainCondition `
                (-not $merged.ContainsKey([string] $entry.Key)) `
                'base and Redis env files contain a duplicate variable name'
            $merged.Add([string] $entry.Key, [string] $entry.Value)
        }
    }
    return $merged
}

function Get-RedisMainComposeSubstitutionNames {
    param([string[]] $ComposePaths)

    $names = New-Object 'Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $ComposePaths) {
        Assert-RedisMainCondition `
            (Test-Path -LiteralPath $path -PathType Leaf) `
            'a Compose file is missing'
        $item = Get-Item -LiteralPath $path
        Assert-RedisMainCondition `
            ($item.Length -le 1MB) `
            'a Compose file exceeds the byte limit'
        $text = Get-Content -LiteralPath $path -Raw
        foreach ($match in [regex]::Matches(
            $text,
            '(?<!\$)\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)')) {
            $null = $names.Add($match.Groups['name'].Value)
        }
    }
    Assert-RedisMainCondition `
        ($names.Count -le 512) `
        'Compose substitution count exceeds the limit'
    return @($names)
}

function Assert-RedisMainEnvironmentSnapshot {
    param(
        [Collections.IDictionary] $ExpectedValues,
        [string[]] $ComposeNames,
        [object[]] $ProcessEntries,
        [string[]] $AllowedUndeclaredNames
    )

    $compose = New-Object 'Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $ComposeNames) {
        $null = $compose.Add($name)
    }
    $allowed = New-Object 'Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $AllowedUndeclaredNames) {
        $null = $allowed.Add($name)
    }
    $seen = New-Object 'Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)

    foreach ($entry in $ProcessEntries) {
        $name = [string] $entry.Name
        $declared = $ExpectedValues.ContainsKey($name)
        $substitution = $compose.Contains($name)
        if (-not $declared -and -not $substitution) {
            continue
        }
        Assert-RedisMainCondition `
            ($seen.Add($name)) `
            'process environment contains a duplicate relevant variable name'
        if ($declared) {
            Assert-RedisMainCondition `
                ([string] $entry.Value -ceq [string] $ExpectedValues[$name]) `
                "process environment conflicts with declared variable $name"
            continue
        }
        Assert-RedisMainCondition `
            ($allowed.Contains($name)) `
            "process environment sets undeclared Compose variable $name"
    }
}

function Assert-RedisMainProcessEnvironment {
    param(
        [Collections.IDictionary] $ExpectedValues,
        [string[]] $ComposeNames,
        [string[]] $AllowedUndeclaredNames = @(
            'GODSWAR_SOURCE_COMMIT',
            'GODSWAR_B20H_EVIDENCE_DIRECTORY'
        )
    )

    $entries = @(
        [Environment]::GetEnvironmentVariables(
            [EnvironmentVariableTarget]::Process
        ).GetEnumerator() | ForEach-Object {
            [pscustomobject]@{
                Name = [string] $_.Key
                Value = [string] $_.Value
            }
        }
    )
    Assert-RedisMainEnvironmentSnapshot `
        -ExpectedValues $ExpectedValues `
        -ComposeNames $ComposeNames `
        -ProcessEntries $entries `
        -AllowedUndeclaredNames $AllowedUndeclaredNames
}

function Get-RedisMainComposeModel {
    param(
        [string] $DockerPath,
        [string[]] $Arguments
    )

    $rendered = & $DockerPath @Arguments
    Assert-RedisMainCondition `
        ($LASTEXITCODE -eq 0) `
        'Docker Compose could not render the Redis main profile'
    return ($rendered -join [Environment]::NewLine) | ConvertFrom-Json
}

function Assert-RedisMainRenderedSecrets {
    param(
        [object] $Model,
        [Collections.IDictionary] $RedisValues,
        [object[]] $Bindings
    )

    $comparison = if ($env:OS -eq 'Windows_NT') {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    foreach ($binding in $Bindings) {
        $property = $Model.secrets.PSObject.Properties[$binding.SecretName]
        Assert-RedisMainCondition `
            ($null -ne $property) `
            'the rendered model is missing a required secret'
        $renderedFile = [string] $property.Value.file
        Assert-RedisMainCondition `
            (-not [string]::IsNullOrWhiteSpace($renderedFile)) `
            'a rendered secret has no source file'
        $expectedPath = [IO.Path]::GetFullPath(
            [string] $RedisValues[$binding.EnvironmentName])
        $renderedPath = [IO.Path]::GetFullPath($renderedFile)
        Assert-RedisMainCondition `
            ($renderedPath.Equals($expectedPath, $comparison)) `
            'a rendered secret source differs from the reviewed Redis env'
    }
}

function Assert-RedisMainRenderedContinuity {
    param(
        [object] $Model,
        [Collections.IDictionary] $BaseValues
    )

    foreach ($name in @(
        'POSTGRES_DB',
        'POSTGRES_USER',
        'POSTGRES_PASSWORD',
        'GODSWAR_GAME_PUBLIC_HOST'
    )) {
        Assert-RedisMainCondition `
            ($BaseValues.ContainsKey($name)) `
            'the base env is missing a required continuity variable'
    }
    $postgres = $Model.services.postgres
    $server = $Model.services.server
    Assert-RedisMainCondition `
        ($null -ne $postgres -and $null -ne $server) `
        'the rendered model is missing PostgreSQL or server'
    Assert-RedisMainCondition `
        ([string] $postgres.environment.POSTGRES_DB -ceq
            [string] $BaseValues['POSTGRES_DB']) `
        'rendered PostgreSQL database differs from the base env'
    Assert-RedisMainCondition `
        ([string] $postgres.environment.POSTGRES_USER -ceq
            [string] $BaseValues['POSTGRES_USER']) `
        'rendered PostgreSQL user differs from the base env'
    Assert-RedisMainCondition `
        ([string] $postgres.environment.POSTGRES_PASSWORD -ceq
            [string] $BaseValues['POSTGRES_PASSWORD']) `
        'rendered PostgreSQL password differs from the base env'

    $expectedConnection =
        'Host=postgres;Port=5432;Database=' +
        [string] $BaseValues['POSTGRES_DB'] + ';Username=' +
        [string] $BaseValues['POSTGRES_USER'] + ';Password=' +
        [string] $BaseValues['POSTGRES_PASSWORD'] + ';Pooling=true'
    Assert-RedisMainCondition `
        ([string] $server.environment.GODSWAR_POSTGRES_CONNECTION_STRING -ceq
            $expectedConnection) `
        'rendered server PostgreSQL connection differs from the base env'
    Assert-RedisMainCondition `
        ([string] $server.environment.GODSWAR_GAME_PUBLIC_HOST -ceq
            [string] $BaseValues['GODSWAR_GAME_PUBLIC_HOST']) `
        'rendered public game host differs from the base env'
    Assert-RedisMainCondition `
        ([string] $server.environment.GODSWAR_MONSTER_RUNTIME -ceq 'Ecs' -and
            [string] $server.environment.GODSWAR_PLAYER_RUNTIME -ceq 'Ecs') `
        'rendered gameplay runtimes must both be Ecs'
}

function Get-RedisMainContainerEnvironment {
    param([object] $Container)

    $values = New-Object `
        'Collections.Generic.Dictionary[string,string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Container.Config.Env)) {
        $separator = ([string] $entry).IndexOf('=')
        Assert-RedisMainCondition `
            ($separator -gt 0) `
            'live PostgreSQL has a malformed environment entry'
        $name = ([string] $entry).Substring(0, $separator)
        Assert-RedisMainCondition `
            (-not $values.ContainsKey($name)) `
            'live PostgreSQL has a duplicate environment variable'
        $values.Add($name, ([string] $entry).Substring($separator + 1))
    }
    return $values
}

function Assert-RedisMainLivePostgres {
    param(
        [string] $DockerPath,
        [Collections.IDictionary] $BaseValues,
        [switch] $Required
    )

    $names = @(& $DockerPath ps -a --filter `
        'name=^/godswar-postgres$' --format '{{.Names}}')
    Assert-RedisMainCondition `
        ($LASTEXITCODE -eq 0) `
        'could not query the live PostgreSQL container'
    $matches = @($names | Where-Object { $_ -ceq 'godswar-postgres' })
    if ($matches.Count -eq 0) {
        Assert-RedisMainCondition `
            (-not $Required) `
            'the required live PostgreSQL container is absent'
        return $false
    }
    Assert-RedisMainCondition `
        ($matches.Count -eq 1) `
        'the live PostgreSQL container identity is ambiguous'

    $raw = & $DockerPath inspect godswar-postgres
    Assert-RedisMainCondition `
        ($LASTEXITCODE -eq 0) `
        'could not inspect the live PostgreSQL container'
    $containers = @(($raw -join [Environment]::NewLine) | ConvertFrom-Json)
    Assert-RedisMainCondition `
        ($containers.Count -eq 1 -and [bool] $containers[0].State.Running) `
        'the live PostgreSQL container is not uniquely running'
    $live = Get-RedisMainContainerEnvironment $containers[0]
    foreach ($name in @('POSTGRES_DB', 'POSTGRES_USER', 'POSTGRES_PASSWORD')) {
        Assert-RedisMainCondition `
            ($live.ContainsKey($name) -and
                [string] $live[$name] -ceq [string] $BaseValues[$name]) `
            'live PostgreSQL identity differs from the base env'
    }

    $loginScript = (
        'set -eu; export PGPASSWORD="$POSTGRES_PASSWORD"; ' +
        'exec psql --host=127.0.0.1 --no-password ' +
        '--username="$POSTGRES_USER" --dbname="$POSTGRES_DB" ' +
        '--tuples-only --no-align --command=''SELECT current_user;'''
    )
    $query = @(& $DockerPath exec godswar-postgres sh -ec $loginScript)
    Assert-RedisMainCondition `
        ($LASTEXITCODE -eq 0 -and $query.Count -eq 1 -and
            $query[0].Trim() -ceq [string] $BaseValues['POSTGRES_USER']) `
        'live PostgreSQL authentication does not match the base env'
    return $true
}

function Invoke-RedisMainSecretOverrideProbes {
    param(
        [string] $DockerPath,
        [string[]] $Arguments,
        [Collections.IDictionary] $RedisValues,
        [object[]] $Bindings
    )

    Assert-RedisMainCondition `
        ($Bindings.Count -ge 2 -and $Bindings.Count -le 8) `
        'secret probe binding count is invalid'
    $target = [EnvironmentVariableTarget]::Process
    $count = 0
    for ($index = 0; $index -lt $Bindings.Count; $index++) {
        $binding = $Bindings[$index]
        $name = [string] $binding.EnvironmentName
        $conflict = [string] $RedisValues[
            $Bindings[($index + 1) % $Bindings.Count].EnvironmentName
        ]
        $original = [Environment]::GetEnvironmentVariable($name, $target)
        try {
            [Environment]::SetEnvironmentVariable($name, $conflict, $target)
            $probe = Get-RedisMainComposeModel $DockerPath $Arguments
            $rejected = $false
            try {
                Assert-RedisMainRenderedSecrets $probe $RedisValues $Bindings
            } catch {
                $rejected = $true
            }
            Assert-RedisMainCondition `
                $rejected `
                'a process secret-source override probe was not rejected'
            $count++
        } finally {
            [Environment]::SetEnvironmentVariable($name, $original, $target)
        }
    }
    return $count
}

function Invoke-RedisMainValidationRegression {
    $expected = New-Object `
        'Collections.Generic.Dictionary[string,string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    $expected.Add('POSTGRES_DB', 'expected')
    $expected.Add('GODSWAR_GAME_PUBLIC_HOST', 'expected')
    $composeNames = @(
        'POSTGRES_DB',
        'GODSWAR_GAME_PUBLIC_HOST',
        'GODSWAR_PLAYER_RUNTIME',
        'GODSWAR_SOURCE_COMMIT'
    )
    $allowed = @('GODSWAR_SOURCE_COMMIT')
    $passed = 0

    try {
        $null = ConvertFrom-RedisMainEnvironmentText `
            "DUPLICATE_TEST=a`nduplicate_test=b" 'regression env'
        throw 'duplicate env regression was accepted'
    } catch {
        Assert-RedisMainCondition `
            (-not $_.Exception.Message.Contains('was accepted')) `
            'duplicate env regression was accepted'
        $passed++
    }
    $duplicateBase = ConvertFrom-RedisMainEnvironmentText `
        'CROSS_FILE_TEST=a' 'regression base env'
    $duplicateOverlay = ConvertFrom-RedisMainEnvironmentText `
        'cross_file_test=b' 'regression Redis env'
    $crossFileRejected = $false
    try {
        $null = Merge-RedisMainEnvironmentValues `
            $duplicateBase $duplicateOverlay
    } catch {
        $crossFileRejected = $true
    }
    Assert-RedisMainCondition `
        $crossFileRejected `
        'cross-file duplicate env regression was accepted'
    $passed++
    foreach ($entries in @(
        @([pscustomobject]@{ Name = 'postgres_db'; Value = 'wrong' }),
        @([pscustomobject]@{
            Name = 'godswar_game_public_host'; Value = 'wrong'
        }),
        @([pscustomobject]@{
            Name = 'GODSWAR_PLAYER_RUNTIME'; Value = 'Ecs'
        }),
        @(
            [pscustomobject]@{ Name = 'POSTGRES_DB'; Value = 'expected' },
            [pscustomobject]@{ Name = 'postgres_db'; Value = 'expected' }
        )
    )) {
        $rejected = $false
        try {
            Assert-RedisMainEnvironmentSnapshot `
                $expected $composeNames $entries $allowed
        } catch {
            $rejected = $true
        }
        Assert-RedisMainCondition $rejected 'environment regression was accepted'
        $passed++
    }
    Assert-RedisMainEnvironmentSnapshot $expected $composeNames @(
        [pscustomobject]@{ Name = 'POSTGRES_DB'; Value = 'expected' },
        [pscustomobject]@{
            Name = 'GODSWAR_GAME_PUBLIC_HOST'; Value = 'expected'
        },
        [pscustomobject]@{ Name = 'GODSWAR_SOURCE_COMMIT'; Value = 'controlled' }
    ) $allowed
    $passed++
    return $passed
}

Export-ModuleMember -Function @(
    'Test-RedisMainFullyQualifiedPath',
    'Read-RedisMainEnvironmentFile',
    'Merge-RedisMainEnvironmentValues',
    'Get-RedisMainComposeSubstitutionNames',
    'Assert-RedisMainProcessEnvironment',
    'Get-RedisMainComposeModel',
    'Assert-RedisMainRenderedSecrets',
    'Assert-RedisMainRenderedContinuity',
    'Assert-RedisMainLivePostgres',
    'Invoke-RedisMainSecretOverrideProbes',
    'Invoke-RedisMainValidationRegression'
)
