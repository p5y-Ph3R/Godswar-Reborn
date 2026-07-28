[CmdletBinding()]
param(
    [ValidateRange(1, 60)]
    [int]$SoakSeconds = 10,

    [ValidateRange(1, 512)]
    [int]$Bots = 64,

    [uint32]$Seed = 20260728
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Seed -eq 0) {
    throw 'Seed must be non-zero.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'GodswarServer.sln'
$protocolDll = Join-Path $repoRoot (
    'tests\Godswar.Server.ProtocolChecks\bin\Release\net10.0\' +
    'Godswar.Server.ProtocolChecks.dll')
$loadDll = Join-Path $repoRoot (
    'tools\Godswar.Server.Phase5A\bin\Release\net10.0\' +
    'Godswar.Server.Phase5A.dll')
$evidenceRoot = Join-Path $repoRoot 'artifacts\phase5a'

function Invoke-DotnetChecked {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $output = @(& dotnet @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }

    return $output
}

function Invoke-GitRaw {
    param(
        [Parameter(Mandatory)]
        [string]$Arguments,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory
    )

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = 'git'
    $startInfo.Arguments = $Arguments
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    $output = New-Object IO.MemoryStream
    try {
        if (-not $process.Start()) {
            throw 'Unable to start git.'
        }

        $errorTask = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($output)
        $process.WaitForExit()
        $errorText = $errorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            $summary = $errorText.Replace("`r", ' ').Replace("`n", ' ')
            if ($summary.Length -gt 160) {
                $summary = $summary.Substring(0, 160)
            }
            throw "git $Arguments failed with exit code " +
                "$($process.ExitCode): $summary"
        }

        return ,$output.ToArray()
    }
    finally {
        $output.Dispose()
        $process.Dispose()
    }
}

function ConvertFrom-NulSeparatedUtf8 {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes
    )

    $encoding = New-Object Text.UTF8Encoding($false, $true)
    $values = New-Object 'Collections.Generic.List[string]'
    $start = 0
    for ($index = 0; $index -lt $Bytes.Length; $index++) {
        if ($Bytes[$index] -ne 0) {
            continue
        }
        if ($index -eq $start) {
            throw 'Git returned an empty repository path.'
        }

        $values.Add(
            $encoding.GetString(
                $Bytes,
                $start,
                $index - $start))
        $start = $index + 1
    }
    if ($start -ne $Bytes.Length) {
        throw 'Git returned a repository path list without a NUL terminator.'
    }

    return ,$values.ToArray()
}

function Add-ManifestUInt64 {
    param(
        [Parameter(Mandatory)]
        [Security.Cryptography.IncrementalHash]$Hash,

        [Parameter(Mandatory)]
        [uint64]$Value
    )

    $bytes = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) {
        [Array]::Reverse($bytes)
    }
    $Hash.AppendData($bytes)
}

function Get-RepositorySourceManifest {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $rawPaths = Invoke-GitRaw `
        -Arguments 'ls-files --cached --others --exclude-standard -z' `
        -WorkingDirectory $RepositoryRoot
    [string[]]$paths = ConvertFrom-NulSeparatedUtf8 $rawPaths
    [Array]::Sort($paths, [StringComparer]::Ordinal)

    $encoding = New-Object Text.UTF8Encoding($false, $true)
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    $fileCount = 0
    $missingPathCount = 0
    [uint64]$totalBytes = 0
    $repositoryPrefix = [IO.Path]::GetFullPath($RepositoryRoot)
    $repositoryPrefix = $repositoryPrefix.TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)) +
        [IO.Path]::DirectorySeparatorChar
    try {
        $hash.AppendData(
            $encoding.GetBytes(
                "reborn.repository-source-manifest.v1`0"))
        foreach ($path in $paths) {
            $fullPath = [IO.Path]::GetFullPath(
                [IO.Path]::Combine($RepositoryRoot, $path))
            if (-not $fullPath.StartsWith(
                    $repositoryPrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Repository path escapes the root: $path"
            }

            $pathBytes = $encoding.GetBytes($path)
            Add-ManifestUInt64 $hash ([uint64]$pathBytes.Length)
            $hash.AppendData($pathBytes)

            $marker = New-Object byte[] 1
            if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
                $marker[0] = 0
                $hash.AppendData($marker)
                Add-ManifestUInt64 $hash 0
                $missingPathCount++
                continue
            }

            $marker[0] = 1
            $hash.AppendData($marker)
            $stream = [IO.FileStream]::new(
                $fullPath,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::Read,
                65536,
                [IO.FileOptions]::SequentialScan)
            try {
                [uint64]$length = $stream.Length
                Add-ManifestUInt64 $hash $length
                $buffer = New-Object byte[] 65536
                [uint64]$readTotal = 0
                while (($read = $stream.Read(
                            $buffer,
                            0,
                            $buffer.Length)) -gt 0) {
                    $hash.AppendData($buffer, 0, $read)
                    $readTotal += [uint64]$read
                }
                if ($readTotal -ne $length) {
                    throw "Repository file changed while hashing: $path"
                }
                $totalBytes += $length
                $fileCount++
            }
            finally {
                $stream.Dispose()
            }
        }

        $digest = $hash.GetHashAndReset()
        return [pscustomobject][ordered]@{
            schemaVersion =
                'reborn.repository-source-manifest.v1'
            algorithm = 'SHA-256'
            sha256 =
                [BitConverter]::ToString($digest).Replace('-', '')
            enumeration =
                'git ls-files --cached --others --exclude-standard'
            pathOrder = 'ordinal'
            pathEncoding = 'UTF-8'
            framing =
                'domain NUL; repeated uint64be path length, path, state byte, uint64be content length, content'
            entryCount = $paths.Length
            fileCount = $fileCount
            missingPathCount = $missingPathCount
            totalBytes = $totalBytes
        }
    }
    finally {
        $hash.Dispose()
    }
}

function Get-RepositoryEvidence {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $encoding = New-Object Text.UTF8Encoding($false, $true)
    $headBytes = Invoke-GitRaw `
        -Arguments 'rev-parse --verify HEAD' `
        -WorkingDirectory $RepositoryRoot
    $head = $encoding.GetString($headBytes).Trim()
    if ($head -notmatch '\A[0-9a-fA-F]{40,64}\z') {
        throw 'Git returned an invalid HEAD object identifier.'
    }

    $statusBytes = Invoke-GitRaw `
        -Arguments 'status --porcelain=v1 -z --untracked-files=all' `
        -WorkingDirectory $RepositoryRoot
    return [pscustomobject][ordered]@{
        head = $head
        dirty = $statusBytes.Length -ne 0
        sourceManifest =
            Get-RepositorySourceManifest $RepositoryRoot
    }
}

Push-Location $repoRoot
try {
    $repositoryBefore = Get-RepositoryEvidence $repoRoot

    & dotnet build $solution --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }

    $protocolOutput = Invoke-DotnetChecked `
        -Arguments @(
            $protocolDll,
            'Secure Phase 5A',
            'deterministic network emulation and overload',
            'UDP bounded loopback baseline'
        ) `
        -Description 'Phase 5A protocol checks'
    $selfCheckJson = (
        Invoke-DotnetChecked `
            -Arguments @($loadDll, '--self-check') `
            -Description 'Phase 5A load-tool self-check') -join "`n"
    $loadJson = (
        Invoke-DotnetChecked `
            -Arguments @(
                $loadDll,
                '--mode', 'load',
                '--bots', $Bots.ToString(
                    [Globalization.CultureInfo]::InvariantCulture),
                '--duration-seconds', '10',
                '--seed', $Seed.ToString(
                    [Globalization.CultureInfo]::InvariantCulture)
            ) `
            -Description 'Phase 5A in-process load baseline') -join "`n"
    $soakJson = (
        Invoke-DotnetChecked `
            -Arguments @(
                $loadDll,
                '--mode', 'paced-soak',
                '--bots', $Bots.ToString(
                    [Globalization.CultureInfo]::InvariantCulture),
                '--duration-seconds', $SoakSeconds.ToString(
                    [Globalization.CultureInfo]::InvariantCulture),
                '--seed', $Seed.ToString(
                    [Globalization.CultureInfo]::InvariantCulture)
            ) `
            -Description 'Phase 5A paced soak baseline') -join "`n"

    $selfCheck = $selfCheckJson | ConvertFrom-Json
    $load = $loadJson | ConvertFrom-Json
    $soak = $soakJson | ConvertFrom-Json
    if ($selfCheck.result -ne 'passed' -or
        $load.result -ne 'passed' -or
        $soak.result -ne 'passed') {
        throw 'A Phase 5A report did not declare a passed result.'
    }

    $repositoryAfter = Get-RepositoryEvidence $repoRoot
    if ($repositoryBefore.head -cne $repositoryAfter.head -or
        $repositoryBefore.dirty -ne $repositoryAfter.dirty -or
        $repositoryBefore.sourceManifest.sha256 -cne
            $repositoryAfter.sourceManifest.sha256) {
        throw 'Repository source state changed during the Phase 5A baseline.'
    }

    New-Item -ItemType Directory -Path $evidenceRoot -Force |
        Out-Null
    $stamp = [DateTimeOffset]::UtcNow.ToString(
        'yyyyMMddTHHmmssfffZ',
        [Globalization.CultureInfo]::InvariantCulture)
    $receiptPath = Join-Path $evidenceRoot "phase5a-$stamp.json"
    $receipt = [ordered]@{
        schemaVersion = 'reborn.phase5a.baseline-receipt.v2'
        generatedAtUtc = [DateTimeOffset]::UtcNow
        head = $repositoryAfter.head
        commit = $repositoryAfter.head
        workingTreeDirty = $repositoryAfter.dirty
        sourceManifest = $repositoryAfter.sourceManifest
        boundary = 'in-process-only; no sockets or configurable target'
        protocolChecks = @($protocolOutput)
        selfCheck = $selfCheck
        load = $load
        pacedSoak = $soak
    }
    $receipt |
        ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $receiptPath -Encoding utf8

    [pscustomobject]@{
        Result = 'Pass'
        Receipt = $receiptPath
        ProtocolResult = @($protocolOutput)[-1]
        LoadDigest = $load.digest.value
        SoakDigest = $soak.digest.value
    }
}
finally {
    Pop-Location
}
