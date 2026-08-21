[CmdletBinding()]
param()

# Exact-template clone test. The isolated development server must already be
# stopped so PostgreSQL can clone the source database without terminating any
# connection. This script never targets or inspects the main/B20H containers.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$databaseUser = 'godswar'
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$database = "godswar_pet_rank100_$suffix"
$databasePattern = '^godswar_pet_rank100_[a-f0-9]{10}$'
if ($database -notmatch $databasePattern) {
    throw 'Disposable database name generation failed.'
}

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
$environment = Initialize-RebirthRepairEnvironment `
    $postgresContainer 'godswar-dev-tempest-openworld-01' `
    'godswar-dev-redis-coordination'
if ($environment.Server.State.Running) {
    throw 'Stop godswar-dev-tempest-openworld-01 before the exact-template clone test.'
}

function Invoke-AdminSql([string]$Sql) {
    $output = $Sql | & docker exec -i $postgresContainer `
        psql -X -q -A -t -v ON_ERROR_STOP=1 `
        -U $databaseUser -d postgres 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Disposable database operation failed:`n$($output -join "`n")"
    }
}

$created = $false
try {
    Invoke-AdminSql "CREATE DATABASE $database TEMPLATE godswar;"
    $created = $true
    $tool = Join-Path $PSScriptRoot 'SetLocalDevelopmentPetRank100.ps1'
    $ready = & $tool -Mode Status -Database $database -DisposableTest
    if ($ready.Status -ne 'Ready' -or $ready.CurrentRank -ne 5.59 -or
        $ready.CurrentPetRevision -ne 1202) {
        throw 'Disposable clone did not match the pinned source state.'
    }
    $applied = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    if ($applied.status -ne 'Applied' -or
        $applied.currentRank -ne 100 -or
        $applied.currentPetRevision -ne 1203) {
        throw 'Disposable fixture did not commit the exact rank transition.'
    }
    $repeat = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    if ($repeat.Status -ne 'Applied' -or $repeat.CurrentRank -ne 100 -or
        $repeat.ReceiptAuditId -ne $applied.auditId) {
        throw 'Disposable fixture replay was not idempotent.'
    }
    [pscustomobject]@{
        Status = 'Passed'
        Database = $database
        PreviousRank = $applied.previousRank
        CurrentRank = $applied.currentRank
        CurrentPetRevision = $applied.currentPetRevision
        AuditId = $applied.auditId
    }
}
finally {
    if ($created) {
        if ($database -notmatch $databasePattern) {
            throw 'Refusing to drop an unvalidated disposable database.'
        }
        Invoke-AdminSql "DROP DATABASE $database;"
    }
}
