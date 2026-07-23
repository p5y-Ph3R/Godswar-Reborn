[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$requiredVcToolsVersion = '14.44.35207'
$requiredWindowsSdkVersion = '10.0.26100.0'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'client\network-shim\Godswar.NetShim.sln'
$vswhere = Join-Path ${env:ProgramFiles(x86)} `
    'Microsoft Visual Studio\Installer\vswhere.exe'

if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw "Visual Studio locator not found: $vswhere"
}

$installPath = & $vswhere `
    -latest `
    -products '*' `
    -version '[17.0,18.0)' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath

if (-not $installPath) {
    throw 'Visual Studio 2022 with the x86/x64 C++ tools is required.'
}

$msbuild = Join-Path $installPath `
    'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    throw "MSBuild not found: $msbuild"
}

$vcToolsPath = Join-Path $installPath `
    "VC\Tools\MSVC\$requiredVcToolsVersion"
if (-not (Test-Path -LiteralPath $vcToolsPath -PathType Container)) {
    throw (
        "Required MSVC v143 tools $requiredVcToolsVersion not found: " +
        $vcToolsPath
    )
}

$sdkPath = Join-Path ${env:ProgramFiles(x86)} `
    "Windows Kits\10\Include\$requiredWindowsSdkVersion"
if (-not (Test-Path -LiteralPath $sdkPath -PathType Container)) {
    throw (
        "Required Windows SDK $requiredWindowsSdkVersion not found: " +
        $sdkPath
    )
}

& $msbuild $solution `
    /m `
    /t:Rebuild `
    "/p:Configuration=$Configuration" `
    /p:Platform=Win32 `
    "/p:VCToolsVersion=$requiredVcToolsVersion" `
    "/p:WindowsTargetPlatformVersion=$requiredWindowsSdkVersion" `
    /v:minimal

if ($LASTEXITCODE -ne 0) {
    throw "Network-shim build failed with exit code $LASTEXITCODE."
}

$outputDirectory = Join-Path $repoRoot `
    "client\network-shim\bin\$Configuration\Win32"
$shimPath = Join-Path $outputDirectory 'Net.dll'
$testPath = Join-Path $outputDirectory 'Godswar.NetShim.Tests.exe'

if (-not (Test-Path -LiteralPath $shimPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $testPath -PathType Leaf)) {
    throw 'Build completed without the expected shim and test outputs.'
}

[pscustomobject]@{
    Configuration = $Configuration
    ShimPath = $shimPath
    ShimSha256 = (Get-FileHash -LiteralPath $shimPath -Algorithm SHA256).Hash
    TestPath = $testPath
}
