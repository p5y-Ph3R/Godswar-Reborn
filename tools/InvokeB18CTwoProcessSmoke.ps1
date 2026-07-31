[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot (
    'tools\Godswar.Server.B18CSmoke\' +
    'Godswar.Server.B18CSmoke.csproj')
$targetFramework = 'net10.0'
$serverDll = Join-Path $repositoryRoot (
    "src\Godswar.Server\bin\$Configuration\" +
    "$targetFramework\Godswar.Server.dll")
$smokeDll = Join-Path $repositoryRoot (
    "tools\Godswar.Server.B18CSmoke\bin\$Configuration\" +
    "$targetFramework\Godswar.Server.B18CSmoke.dll")
$dotnetHost = (Get-Command dotnet -ErrorAction Stop).Source

Push-Location -LiteralPath $repositoryRoot
try {
    & $dotnetHost build $projectPath `
        --configuration $Configuration `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "B18C1 smoke build failed with exit code $LASTEXITCODE."
    }

    foreach ($requiredPath in @($serverDll, $smokeDll)) {
        if (-not [IO.File]::Exists($requiredPath)) {
            throw "Expected build output was not found: $requiredPath"
        }
    }

    & $dotnetHost $smokeDll `
        --server-dll $serverDll `
        --dotnet-host $dotnetHost
    if ($LASTEXITCODE -ne 0) {
        throw "B18C1 two-process smoke failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
