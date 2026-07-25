[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

dotnet run `
    --project (Join-Path $repositoryRoot 'tests\Godswar.Server.ProtocolChecks\Godswar.Server.ProtocolChecks.csproj') `
    --configuration Release `
    -- 'Secure Phase 3 UDP bounded loopback baseline'

if ($LASTEXITCODE -ne 0) {
    throw "Secure UDP bounded loopback baseline failed with exit code $LASTEXITCODE."
}
