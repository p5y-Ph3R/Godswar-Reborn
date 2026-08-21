[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Status', 'Apply')]
    [string]$Mode = 'Status',

    [ValidatePattern('^godswar$')]
    [string]$Database = 'godswar'
)

# Provisions only the five named max-combat identities in the isolated local
# development stack. It is deliberately not a migration or production tool.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$serverContainer = 'godswar-dev-tempest-openworld-01'
$redisContainer = 'godswar-dev-redis-coordination'
$databaseUser = 'godswar'
$fixtureDirectory = Join-Path $PSScriptRoot `
    '..\database\fixtures\max-combat-characters'

. (Join-Path $PSScriptRoot `
    'ProvisionLocalDevelopmentMaxCombatFixture.Guards.ps1')
. (Join-Path $PSScriptRoot `
    'ProvisionLocalDevelopmentMaxCombatFixture.Sql.ps1')
. (Join-Path $PSScriptRoot `
    'ProvisionLocalDevelopmentMaxCombatFixture.Status.ps1')

function Invoke-MaxFixturePsql(
    [string]$Sql,
    [string]$Marker,
    [hashtable]$Variables = @{}
) {
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in @(
        'exec','-i',$postgresContainer,
        'psql','-X','-q','-A','-t','-v','ON_ERROR_STOP=1',
        '-U',$databaseUser,'-d',$Database)) {
        $arguments.Add($argument)
    }
    foreach ($entry in $Variables.GetEnumerator()) {
        $arguments.Add('-v')
        $arguments.Add("$($entry.Key)=$($entry.Value)")
    }
    $output = $Sql | & docker @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { $_.ToString() })
    if ($exitCode -ne 0) {
        throw "Max-combat fixture failed and rolled back:`n$($lines -join "`n")"
    }
    $receipt = $lines | Where-Object {
        $_.StartsWith($Marker, [StringComparison]::Ordinal)
    } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($receipt)) {
        throw 'The database returned no max-combat fixture receipt.'
    }
    return $receipt.Substring($Marker.Length) | ConvertFrom-Json
}

function New-MaxFixtureBackup {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $name = "godswar-before-max-combat-$stamp.dump"
    $containerPath = "/tmp/$name"
    $directory = Join-Path $PSScriptRoot `
        '..\artifacts\development-backups'
    $hostPath = Join-Path $directory $name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    try {
        & docker exec $postgresContainer pg_dump `
            -U $databaseUser -d $Database -Fc -f $containerPath
        if ($LASTEXITCODE -ne 0) { throw 'pg_dump did not complete.' }
        & docker exec $postgresContainer pg_restore --list $containerPath |
            Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The PostgreSQL backup is invalid.' }
        & docker cp "${postgresContainer}:$containerPath" $hostPath
        if ($LASTEXITCODE -ne 0) { throw 'Could not copy the backup to host.' }
    }
    finally {
        if ($containerPath -match '^/tmp/godswar-before-max-combat-' +
            '[0-9TZ]+\.dump$') {
            & docker exec $postgresContainer rm -f -- $containerPath |
                Out-Null
        }
    }
    $file = Get-Item -LiteralPath $hostPath
    if ($file.Length -le 0) { throw 'The PostgreSQL backup is empty.' }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $hostPath).Hash
    [pscustomobject]@{ Path = $hostPath; Sha256 = $hash; Bytes = $file.Length }
}

$environment = Initialize-MaxFixtureEnvironment `
    $postgresContainer $serverContainer $redisContainer
$status = Invoke-MaxFixturePsql (Get-MaxFixtureStatusSql $fixtureDirectory) `
    'MAX_COMBAT_FIXTURE_STATUS|'
$identities = @($status.identities)
$serverRunning = [bool]$environment.Server.State.Running
$originRunning = Test-MaxFixtureOriginRunning
$redisLeaseCount = Get-MaxFixtureRedisKeyCount `
    $redisContainer $identities
$state = if ($status.applied) { 'Applied' }
    elseif (-not $serverRunning -and -not $originRunning -and
        $redisLeaseCount -eq 0) { 'Ready' }
    else { 'AwaitingOffline' }
$summary = [pscustomobject]@{
    Status = $state
    Database = $Database
    Accounts = $status.accounts
    Characters = $status.characters
    StableIds = $status.stableIds
    EquipmentRows = $status.equipmentRows
    MaxTalentRows = $status.maxTalentRows
    SkillRows = $status.skillRows
    Pets = $status.pets
    SavvyRows = $status.savvyRows
    PetSkillRows = $status.petSkillRows
    PetBonusRows = $status.petBonusRows
    ZodiacRows = $status.zodiacRows
    DriftDomains = @($status.driftDomains)
    Identities = $identities
    ServerRunning = $serverRunning
    OriginRunning = $originRunning
    RedisTargetLeaseCount = $redisLeaseCount
}
if ($Mode -eq 'Status') { return $summary }
if ($serverRunning -or $originRunning -or $redisLeaseCount -ne 0) {
    throw 'Stop the development game server, close Origin, and clear target leases.'
}
if (-not $PSCmdlet.ShouldProcess(
        'isolated-development godswar database / five named identities',
        'Back up and provision the max-combat character fixture')) {
    return $summary
}

Assert-MaxFixtureOffline $environment $redisContainer $identities
$backup = New-MaxFixtureBackup
Assert-MaxFixtureOffline $environment $redisContainer $identities

$variables = $null
try {
    $variables = @{
        test25_verifier = 'gws$pbkdf2-sha256$v1$600000$' +
          '3EIgjUktl5sFyy2YYK3ynQ==$' +
          '6WxhR6jeTEkdPBelif9J9Gze55MimrguFawh6gSezuw='
        ares_bulwark_verifier = 'gws$pbkdf2-sha256$v1$600000$' +
          'X3ai97nBWQlEwekDRlx6XQ==$' +
          'C7pivBvf5jYBDIu6XDEPM70wwyIwL38Krd7Y0D5VsSs='
        ares_mirage_verifier = 'gws$pbkdf2-sha256$v1$600000$' +
          'P88aW0u5fjI/FtRnLS7osg==$/FT0LhRB4RwmLq5gIDvyMtCnxKsQR1lp4HglLNdsEkU='
        athena_bulwark_verifier = 'gws$pbkdf2-sha256$v1$600000$' +
          'T110O7doZ1mRNNFo18aXaA==$' +
          'yT7Tlq9Qh9FpL+uUWTD+mLs96wKHe2mhhXFj4/R3Wq8='
        athena_mirage_verifier = 'gws$pbkdf2-sha256$v1$600000$' +
          'mEhv48pAxaEwP8Qxko4g7w==$' +
          'dwe1oEPL49+xZ2k4M5ZbJc+2zEOPsKn0M31yofGVOSk='
    }
    $result = Invoke-MaxFixturePsql `
        (Get-MaxFixtureSqlText $fixtureDirectory) `
        'MAX_COMBAT_FIXTURE_RESULT|' $variables
}
finally { $variables = $null }

$result | Add-Member NoteProperty BackupPath $backup.Path
$result | Add-Member NoteProperty BackupSha256 $backup.Sha256
$result | Add-Member NoteProperty BackupBytes $backup.Bytes
$result
