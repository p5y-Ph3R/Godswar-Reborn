[CmdletBinding()]
param(
    [string]$BaseImage = 'reborn-server:latest',

    [string]$TargetImage = 'reborn-server:latest',

    [switch]$AllowMutation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $AllowMutation) {
    throw (
        'This command replaces the local server image tag. Pass ' +
        '-AllowMutation after confirming no observation is active.')
}

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
Import-Module `
    (Join-Path $PSScriptRoot 'B20HDockerObservation.Integrity.psm1') `
    -Force

$status = Invoke-B20Command git @(
    '-C', $repositoryRoot, 'status', '--porcelain')
if (-not [string]::IsNullOrWhiteSpace($status)) {
    throw 'Commit or remove repository changes before building the image.'
}
$sourceCommit = (
    Invoke-B20Command git @(
        '-C', $repositoryRoot, 'rev-parse', 'HEAD')
).Trim()
if ($sourceCommit -cnotmatch '^[0-9a-f]{40}$') {
    throw 'The source revision is not an exact Git commit.'
}

$activePath = Join-Path `
    $repositoryRoot 'artifacts/b20h-observation/active-observation.json'
if (Test-Path -LiteralPath $activePath -PathType Leaf) {
    throw 'Never replace the server image during an active B20H window.'
}

$shortCommit = $sourceCommit.Substring(0, 12)
$baseTag = "reborn-server:b20h-base-$shortCommit"
$candidateTag = "reborn-server:b20h-candidate-$shortCommit"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'reborn-b20h-image-' + [Guid]::NewGuid().ToString('N'))
$contextRoot = Join-Path $temporaryRoot 'context'
$publishRoot = Join-Path $contextRoot 'publish'
$savedBuildKit = $env:DOCKER_BUILDKIT

try {
    $null = Invoke-B20Command docker @(
        'image', 'inspect', $BaseImage)
    $null = [IO.Directory]::CreateDirectory($contextRoot)

    & dotnet publish `
        (Join-Path $repositoryRoot `
            'src/Godswar.Server/Godswar.Server.csproj') `
        --configuration Release `
        --output $publishRoot `
        --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Copy-Item -LiteralPath `
        (Join-Path $repositoryRoot 'appsettings.docker.json') `
        -Destination (Join-Path $contextRoot 'appsettings.json')
    Copy-Item -LiteralPath `
        (Join-Path $repositoryRoot `
            'tools/docker/secure-healthcheck.sh') `
        -Destination (Join-Path $contextRoot 'secure-healthcheck.sh')

    $null = Invoke-B20Command docker @(
        'image', 'tag', $BaseImage, $baseTag)
    $dockerfile = @"
FROM $baseTag
WORKDIR /app
ARG GODSWAR_SOURCE_COMMIT
RUN find /app -mindepth 1 -maxdepth 1 -exec rm -rf '{}' +
COPY publish/ ./
COPY appsettings.json ./appsettings.json
COPY secure-healthcheck.sh ./secure-healthcheck.sh
RUN chmod 0555 ./secure-healthcheck.sh
LABEL org.opencontainers.image.revision=`$GODSWAR_SOURCE_COMMIT
EXPOSE 5999/tcp 7000/tcp 6599/tcp 7443/tcp 7444/udp
ENTRYPOINT ["dotnet", "Godswar.Server.dll", "appsettings.json"]
"@
    [IO.File]::WriteAllText(
        (Join-Path $contextRoot 'Dockerfile'),
        $dockerfile,
        [Text.UTF8Encoding]::new($false))

    $env:DOCKER_BUILDKIT = '0'
    Invoke-B20Command docker @(
        'build', '--pull=false', '--network=none',
        '--tag', $candidateTag,
        '--build-arg', "GODSWAR_SOURCE_COMMIT=$sourceCommit",
        $contextRoot) | Write-Host

    $candidate = @(
        (Invoke-B20Command docker @(
            'image', 'inspect', $candidateTag)) | ConvertFrom-Json
    )[0]
    $revision = [string]$candidate.Config.Labels.
        'org.opencontainers.image.revision'
    if ($revision -cne $sourceCommit) {
        throw 'The prepared image revision label is incorrect.'
    }
    if (@($candidate.Config.Entrypoint) -join ' ' -cne
        'dotnet Godswar.Server.dll appsettings.json') {
        throw 'The prepared image entrypoint is incorrect.'
    }

    $null = Invoke-B20Command docker @(
        'image', 'tag', $candidateTag, $TargetImage)
    [pscustomobject]@{
        Status = 'prepared'
        SourceCommit = $sourceCommit
        Image = $TargetImage
        ImageId = [string]$candidate.Id
        BaseImage = $BaseImage
        BaseSnapshotImage = $baseTag
        NetworkUsed = $false
    }
}
finally {
    $env:DOCKER_BUILDKIT = $savedBuildKit
    try {
        $null = Invoke-B20Command docker @(
            'image', 'rm', $candidateTag)
    }
    catch {
        Write-Warning (
            "Could not remove temporary image tag ${candidateTag}: $_")
    }
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
