$ErrorActionPreference = 'Stop'

function Get-ParitySha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Resolve-ParityDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label,
        [switch]$AllowMissing
    )

    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetPathRoot($resolved).TrimEnd('\')
    if (-not $resolved -or
        $resolved.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label cannot be a filesystem root: $resolved"
    }
    if (-not $AllowMissing -and
        -not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "$Label not found: $resolved"
    }

    return $resolved
}

function Write-ParityJsonNew {
    param(
        [Parameter(Mandatory)][object]$Value,
        [Parameter(Mandatory)][string]$Path
    )

    if (Test-Path -LiteralPath $Path) {
        throw "Evidence file already exists: $Path"
    }

    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = $Value | ConvertTo-Json -Depth 12
        $encoding = New-Object Text.UTF8Encoding($false)
        [IO.File]::WriteAllText($temporary, $json, $encoding)
        [IO.File]::Move($temporary, $Path)
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Write-ParityTextNew {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Path
    )

    if (Test-Path -LiteralPath $Path) {
        throw "Evidence file already exists: $Path"
    }

    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $encoding = New-Object Text.UTF8Encoding($false)
        [IO.File]::WriteAllText($temporary, $Value, $encoding)
        [IO.File]::Move($temporary, $Path)
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Get-ParityFileEvidence {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$BasePath
    )

    $item = Get-Item -LiteralPath $Path -Force
    $fullBase = [IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($item.FullName)
    if (-not $fullPath.StartsWith(
            $fullBase,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Evidence path escaped its base directory: $fullPath"
    }

    return [pscustomobject][ordered]@{
        relativePath = $fullPath.Substring($fullBase.Length)
        length = $item.Length
        creationUtc = $item.CreationTimeUtc.ToString('O')
        lastWriteUtc = $item.LastWriteTimeUtc.ToString('O')
        sha256 = Get-ParitySha256 $fullPath
    }
}

function Get-ParityInventory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$MaximumFiles = 4096
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return @()
    }

    $files = @(
        Get-ChildItem -LiteralPath $Path -File -Recurse -Force |
            Sort-Object FullName
    )
    if ($files.Count -gt $MaximumFiles) {
        throw "Evidence inventory exceeds $MaximumFiles files: $Path"
    }

    return @(
        foreach ($file in $files) {
            Get-ParityFileEvidence $file.FullName $Path
        }
    )
}

function Compare-ParityInventory {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Before,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$After
    )

    $beforeByPath = @{}
    $afterByPath = @{}
    foreach ($item in $Before) {
        $beforeByPath[[string]$item.relativePath] = $item
    }
    foreach ($item in $After) {
        $afterByPath[[string]$item.relativePath] = $item
    }

    $added = @()
    $removed = @()
    $changed = @()
    foreach ($key in $afterByPath.Keys) {
        if (-not $beforeByPath.ContainsKey($key)) {
            $added += $key
            continue
        }
        $old = $beforeByPath[$key]
        $new = $afterByPath[$key]
        if ($old.length -ne $new.length -or
            $old.lastWriteUtc -ne $new.lastWriteUtc -or
            $old.sha256 -ne $new.sha256) {
            $changed += $key
        }
    }
    foreach ($key in $beforeByPath.Keys) {
        if (-not $afterByPath.ContainsKey($key)) {
            $removed += $key
        }
    }

    return [ordered]@{
        added = @($added | Sort-Object)
        changed = @($changed | Sort-Object)
        removed = @($removed | Sort-Object)
    }
}

function Get-ParityClientSnapshot {
    param(
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)][string]$OriginHash,
        [Parameter(Mandatory)][string]$ShimHash,
        [Parameter(Mandatory)][string]$LegacyHash
    )

    $root = Resolve-ParityDirectory $ClientRoot 'ClientRoot'
    $originPath = Join-Path $root 'Origin.exe'
    $netPath = Join-Path $root 'Net.dll'
    $legacyPath = Join-Path $root 'NetLegacy.dll'
    foreach ($required in @($originPath, $netPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Required client file not found: $required"
        }
    }

    $actualOrigin = Get-ParitySha256 $originPath
    $actualNet = Get-ParitySha256 $netPath
    $actualLegacy = if (Test-Path -LiteralPath $legacyPath -PathType Leaf) {
        Get-ParitySha256 $legacyPath
    } else {
        $null
    }
    $state = if ($actualNet -eq $ShimHash -and
        $actualLegacy -eq $LegacyHash) {
        'InstalledExact'
    } elseif ($actualNet -eq $LegacyHash -and -not $actualLegacy) {
        'Stock'
    } elseif ($actualNet -eq $LegacyHash -and
        $actualLegacy -eq $LegacyHash) {
        'RecoverablePartial'
    } else {
        'UnknownRefused'
    }

    $files = @()
    foreach ($name in @('Origin.exe', 'Net.dll', 'NetLegacy.dll', 'config.ini')) {
        $path = Join-Path $root $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $files += Get-ParityFileEvidence $path $root
        }
    }

    return [ordered]@{
        state = $state
        originSupported = $actualOrigin -eq $OriginHash
        originSha256 = $actualOrigin
        netSha256 = $actualNet
        netLegacySha256 = $actualLegacy
        files = $files
    }
}

function Get-ParityRepositorySnapshot {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $head = (& git -C $RepositoryRoot rev-parse HEAD 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve repository HEAD.'
    }
    $branch = (
        & git -C $RepositoryRoot branch --show-current 2>$null | Out-String
    ).Trim()
    $changes = @(& git -C $RepositoryRoot status --porcelain=v1)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect repository status.'
    }

    return [ordered]@{
        head = $head
        branch = $branch
        clean = $changes.Count -eq 0
        changeCount = $changes.Count
    }
}

function Get-ParityServerSnapshot {
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string[]]$Endpoints,
        [switch]$SkipChecks
    )

    if ($SkipChecks) {
        return [ordered]@{
            checksSkipped = $true
            running = $null
            endpointsPresent = $null
        }
    }

    $containerJson = (& docker inspect $ContainerName 2>$null | Out-String)
    if ($LASTEXITCODE -ne 0 -or -not $containerJson.Trim()) {
        throw "Docker container not found: $ContainerName"
    }
    $container = @($containerJson | ConvertFrom-Json)[0]
    $listeners = @(
        [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().
            GetActiveTcpListeners() |
            ForEach-Object { "$($_.Address):$($_.Port)" }
    )
    $missing = @(
        foreach ($endpoint in $Endpoints) {
            if ($listeners -notcontains $endpoint) {
                $endpoint
            }
        }
    )
    $health = $null
    if ($container.State.Health) {
        $health = [string]$container.State.Health.Status
    }

    return [ordered]@{
        checksSkipped = $false
        name = [string]$container.Name.TrimStart('/')
        id = [string]$container.Id
        imageId = [string]$container.Image
        configuredImage = [string]$container.Config.Image
        running = [bool]$container.State.Running
        startedUtc = [string]$container.State.StartedAt
        health = $health
        endpoints = $Endpoints
        endpointsPresent = $missing.Count -eq 0
        missingEndpoints = $missing
    }
}

function Get-ParityBackupSnapshot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$OriginHash,
        [Parameter(Mandatory)][string]$ShimHash,
        [Parameter(Mandatory)][string]$LegacyHash
    )

    $root = Resolve-ParityDirectory $Path 'ApplyBackupPath'
    $manifestPath = Join-Path $root 'manifest.json'
    $stockPath = Join-Path $root 'Net.dll'
    foreach ($required in @($manifestPath, $stockPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Apply backup is incomplete: $required"
        }
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw |
        ConvertFrom-Json
    $stockHash = Get-ParitySha256 $stockPath
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.mode -ne 'Apply' -or
        $manifest.originSha256 -ne $OriginHash -or
        $manifest.after.netSha256 -ne $ShimHash -or
        $manifest.after.netLegacySha256 -ne $LegacyHash -or
        $stockHash -ne $LegacyHash) {
        throw "Apply backup validation failed: $root"
    }

    return [ordered]@{
        path = $root
        manifestSha256 = Get-ParitySha256 $manifestPath
        stockNetSha256 = $stockHash
        createdUtc = [string]$manifest.createdUtc
        clientRoot = [string]$manifest.clientRoot
        beforeState = [string]$manifest.before.state
        beforeNetSha256 = [string]$manifest.before.netSha256
        beforeNetLegacySha256 = [string]$manifest.before.netLegacySha256
    }
}

function Test-ParityBackupSnapshot {
    param(
        [Parameter(Mandatory)][object]$Current,
        [Parameter(Mandatory)][object]$Expected
    )

    return (
        $Current.manifestSha256 -eq [string]$Expected.manifestSha256 -and
        $Current.stockNetSha256 -eq [string]$Expected.stockNetSha256 -and
        $Current.createdUtc -eq [string]$Expected.createdUtc
    )
}

function Get-ParityFinalBackupErrors {
    param(
        [Parameter(Mandatory)][object]$Backup,
        [Parameter(Mandatory)][object]$Original,
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)][string]$LegacyHash,
        [Parameter(Mandatory)][string]$RunStartedUtc,
        [DateTimeOffset]$NowUtc = [DateTimeOffset]::UtcNow
    )

    $errors = @()
    if ($Backup.path.Equals(
            [string]$Original.path,
            [StringComparison]::OrdinalIgnoreCase)) {
        $errors += 'Final reapply must produce a new Apply backup.'
    }
    if (-not $Backup.clientRoot.Equals(
            $ClientRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        $errors += 'Final Apply backup belongs to another client root.'
    }
    if ($Backup.beforeState -ne 'Stock' -or
        $Backup.beforeNetSha256 -ne $LegacyHash -or
        $Backup.beforeNetLegacySha256) {
        $errors += 'Final Apply backup was not created from exact Stock state.'
    }
    if ([DateTimeOffset]$Backup.createdUtc -le
        [DateTimeOffset]$RunStartedUtc) {
        $errors += 'Final Apply backup predates the evidence run.'
    }
    if ([DateTimeOffset]$Backup.createdUtc -gt $NowUtc.AddSeconds(1)) {
        $errors += 'Final Apply backup timestamp is in the future.'
    }
    return $errors
}

function Read-ParityManifest {
    param([Parameter(Mandatory)][string]$EvidencePath)

    $root = Resolve-ParityDirectory $EvidencePath 'EvidencePath'
    $manifestPath = Join-Path $root 'manifest.json'
    $checksumPath = Join-Path $root 'manifest.sha256'
    foreach ($required in @($manifestPath, $checksumPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Evidence manifest is incomplete: $required"
        }
    }
    $expected = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    if ($expected -notmatch '^[0-9A-F]{64}$' -or
        (Get-ParitySha256 $manifestPath) -ne $expected) {
        throw 'Evidence manifest checksum mismatch.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw |
        ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$manifest.runId)) {
        throw 'Unsupported evidence manifest.'
    }
    try {
        [void][DateTimeOffset]$manifest.startedUtc
    }
    catch {
        throw 'Evidence manifest has an invalid start timestamp.'
    }

    return [pscustomobject]@{
        Root = $root
        Manifest = $manifest
    }
}

function Read-ParityCompletion {
    param(
        [Parameter(Mandatory)][string]$EvidencePath,
        [string]$ExpectedRunId
    )

    $root = Resolve-ParityDirectory $EvidencePath 'EvidencePath'
    $completionPath = Join-Path $root 'completion.json'
    $checksumPath = Join-Path $root 'completion.sha256'
    if (-not (Test-Path -LiteralPath $completionPath -PathType Leaf)) {
        if (Test-Path -LiteralPath $checksumPath) {
            throw 'Completion checksum exists without completion.json.'
        }
        return $null
    }
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw 'Completion checksum is missing.'
    }
    if ((Get-Item -LiteralPath $completionPath).Length -gt 4194304) {
        throw 'Completion evidence exceeds 4194304 bytes.'
    }
    $expected = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    if ($expected -notmatch '^[0-9A-F]{64}$' -or
        (Get-ParitySha256 $completionPath) -ne $expected) {
        throw 'Completion checksum mismatch.'
    }
    $completion = Get-Content -LiteralPath $completionPath -Raw |
        ConvertFrom-Json
    if ($completion.schemaVersion -ne 1 -or
        $completion.result -notin @('Pass', 'Fail') -or
        ($ExpectedRunId -and $completion.runId -ne $ExpectedRunId)) {
        throw 'Unsupported completion evidence.'
    }
    return $completion
}

function Get-ParityObservations {
    param(
        [Parameter(Mandatory)][string]$EvidencePath,
        [string]$ExpectedRunId
    )

    $observationRoot = Join-Path $EvidencePath 'observations'
    if (-not (Test-Path -LiteralPath $observationRoot -PathType Container)) {
        return @()
    }
    $files = @(
        Get-ChildItem -LiteralPath $observationRoot `
            -File -Filter '*.json' -Force
    )
    if ($files.Count -gt 64) {
        throw 'Observation count exceeds the hard limit of 64.'
    }
    return @(
        foreach ($file in ($files | Sort-Object Name)) {
            if ($file.Length -gt 262144) {
                throw "Observation exceeds 262144 bytes: $($file.Name)"
            }
            $checksumPath = "$($file.FullName).sha256"
            if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
                throw "Observation checksum is missing: $($file.Name)"
            }
            $expected = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
            if ($expected -notmatch '^[0-9A-F]{64}$' -or
                (Get-ParitySha256 $file.FullName) -ne $expected) {
                throw "Observation checksum mismatch: $($file.Name)"
            }
            $observation = Get-Content -LiteralPath $file.FullName -Raw |
                ConvertFrom-Json
            if ($observation.schemaVersion -ne 1 -or
                ($ExpectedRunId -and
                    $observation.runId -ne $ExpectedRunId) -or
                $observation.stage -notin @(
                    'ShimParity',
                    'StockRollback',
                    'FinalReapply'
                ) -or
                $observation.accountId -notin @(7, 13) -or
                $observation.passed -isnot [bool]) {
                throw "Observation structure is invalid: $($file.Name)"
            }
            $observation
        }
    )
}

function Get-ParityInventorySummary {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Inventory
    )

    $canonical = @(
        foreach ($item in ($Inventory | Sort-Object relativePath)) {
            '{0}|{1}|{2}|{3}' -f
                $item.relativePath,
                $item.length,
                $item.lastWriteUtc,
                $item.sha256
        }
    ) -join "`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes($canonical)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = [BitConverter]::ToString(
            $sha.ComputeHash($bytes)
        ).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
    [long]$totalBytes = 0
    foreach ($item in $Inventory) {
        $totalBytes += [long]$item.length
    }

    return [pscustomobject][ordered]@{
        count = $Inventory.Count
        totalBytes = $totalBytes
        inventorySha256 = $digest
    }
}

Export-ModuleMember -Function @(
    'Compare-ParityInventory',
    'Get-ParityFinalBackupErrors',
    'Get-ParityBackupSnapshot',
    'Get-ParityClientSnapshot',
    'Get-ParityFileEvidence',
    'Get-ParityInventory',
    'Get-ParityInventorySummary',
    'Get-ParityObservations',
    'Get-ParityRepositorySnapshot',
    'Get-ParityServerSnapshot',
    'Get-ParitySha256',
    'Read-ParityCompletion',
    'Read-ParityManifest',
    'Resolve-ParityDirectory',
    'Test-ParityBackupSnapshot',
    'Write-ParityJsonNew',
    'Write-ParityTextNew'
)
