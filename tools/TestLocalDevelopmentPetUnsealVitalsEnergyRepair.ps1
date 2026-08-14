[CmdletBinding()]
param()

# Logical-clone mutation/replay test. Only a validated disposable database is
# changed and dropped; the isolated live rows are fingerprinted before/after.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$databaseUser = 'godswar'
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$database = "godswar_pet_unseal_vitals_$suffix"
$databasePattern = '^godswar_pet_unseal_vitals_[a-f0-9]{10}$'
if ($database -notmatch $databasePattern) {
    throw 'Disposable database name generation failed.'
}

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
$null = Initialize-RebirthRepairEnvironment $postgresContainer `
    'godswar-dev-server' 'godswar-dev-redis-coordination'

function Invoke-PetUnsealRepairTestSql(
    [string]$TargetDatabase,
    [string]$Sql
) {
    $output = $Sql | & docker exec -i $postgresContainer `
        psql -X -q -A -t -v ON_ERROR_STOP=1 `
        -U $databaseUser -d $TargetDatabase 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Disposable SQL failed:`n$($output -join "`n")"
    }
    @($output | ForEach-Object { $_.ToString() })
}

function Test-PetUnsealRepairSqlFailure(
    [string]$TargetDatabase,
    [string]$Sql
) {
    $savedErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = $Sql | & docker exec -i $postgresContainer `
            psql -X -q -A -t -v ON_ERROR_STOP=1 `
            -U $databaseUser -d $TargetDatabase 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorPreference
    }
    $failed = $exitCode -ne 0
    if (-not $failed) {
        throw "Expected disposable SQL failure but it committed:`n$($output -join "`n")"
    }
    $true
}

function Get-PetUnsealRepairLiveFingerprint {
    $sql = @'
BEGIN READ ONLY;
SELECT 'LIVE|' || encode(sha256(convert_to(jsonb_build_object(
 'account',(SELECT to_jsonb(a) FROM accounts a WHERE id=13),
 'character',(SELECT to_jsonb(c) FROM character_base c WHERE id=2),
 'pets',COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id)
    FROM character_pets p WHERE p.user_id=2),'[]'::jsonb),
 'items',COALESCE((SELECT jsonb_agg(to_jsonb(i) ORDER BY i.item_location,
    i.slot_index,i.id) FROM character_items i WHERE i.user_id=2),'[]'::jsonb),
 'skills',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.pet_id,
    s.slot_index) FROM character_pet_skills s WHERE s.pet_id IN
      (SELECT id FROM character_pets WHERE user_id=2)),'[]'::jsonb),
 'stats',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.pet_id,
    s.stat_code) FROM character_pet_stat_values s WHERE s.pet_id IN
      (SELECT id FROM character_pets WHERE user_id=2)),'[]'::jsonb),
 'bonuses',COALESCE((SELECT jsonb_agg(to_jsonb(b) ORDER BY b.pet_id,
    b.effect_code) FROM character_pet_character_bonuses b WHERE b.pet_id IN
      (SELECT id FROM character_pets WHERE user_id=2)),'[]'::jsonb),
 'sealed',COALESCE((SELECT jsonb_agg(to_jsonb(l) ORDER BY l.id)
    FROM sealed_pet_items l WHERE l.owner_character_id=2),'[]'::jsonb),
 'receipt',COALESCE((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id)
    FROM command_audit a
    WHERE a.command_family='pet_unseal_vitals_energy_repair'
      AND a.principal_key='13' AND a.aggregate_key='character:2'),
    '[]'::jsonb)
)::text,'UTF8')),'hex');
COMMIT;
'@
    $lines = Invoke-PetUnsealRepairTestSql 'godswar' $sql
    $line = $lines | Where-Object {
        $_.StartsWith('LIVE|', [StringComparison]::Ordinal)
    } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw 'Live fingerprint query returned no marker.'
    }
    $line.Substring(5)
}

function Invoke-PetUnsealRepairAdminSql([string]$Sql) {
    $null = Invoke-PetUnsealRepairTestSql 'postgres' $Sql
}

$liveBefore = Get-PetUnsealRepairLiveFingerprint
$created = $false
try {
    Invoke-PetUnsealRepairAdminSql "CREATE DATABASE $database;"
    $created = $true
    $copyCommand =
        "pg_dump --no-owner --no-privileges -U $databaseUser godswar" +
        " | psql -X -q -v ON_ERROR_STOP=1 -U $databaseUser -d $database"
    $copyOutput = & docker exec $postgresContainer sh -c $copyCommand 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Logical clone failed:`n$($copyOutput -join "`n")"
    }

    $tool = Join-Path $PSScriptRoot `
        'RepairLocalDevelopmentPetUnsealVitalsEnergy.ps1'
    $ready = & $tool -Mode Status -Database $database -DisposableTest
    if ($ready.Status -ne 'Ready' -or -not $ready.AuthorityValid -or
        -not $ready.SourceReady -or -not $ready.ImmutableAuditTrigger -or
        $ready.CurrentHp -ne 29350 -or $ready.CurrentMp -ne 1287 -or
        $ready.CalculatedMaximumHp -ne 134341 -or
        $ready.CalculatedMaximumMp -ne 6047 -or
        $ready.CurrentVitalsRevision -ne 9895 -or
        $ready.CurrentEnergy -ne 31 -or $ready.MaximumEnergy -ne 100 -or
        $ready.CurrentPetRevision -ne 1413) {
        throw 'Disposable clone did not preserve the exact ready state.'
    }

    $applied = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    if ($applied.status -ne 'Applied' -or -not $applied.changed -or
        $applied.auditId -le 0 -or $applied.currentHpBefore -ne 29350 -or
        $applied.currentHpAfter -ne 134341 -or
        $applied.currentMpBefore -ne 1287 -or
        $applied.currentMpAfter -ne 6047 -or
        $applied.vitalsRevisionAfter -ne 9896 -or
        $applied.energyBefore -ne 31 -or $applied.energyAfter -ne 100 -or
        $applied.petRevisionAfter -ne 1414) {
        throw 'Disposable repair did not commit the exact target state.'
    }
    $repeat = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    if ($repeat.Status -ne 'Applied' -or -not $repeat.PostReady -or
        $repeat.ReceiptAuditId -ne $applied.auditId -or
        $repeat.CurrentHp -ne 134341 -or $repeat.CurrentMp -ne 6047 -or
        $repeat.CurrentEnergy -ne 100) {
        throw 'Immediate replay was not idempotent and fully audited.'
    }

    $verifyRows = Invoke-PetUnsealRepairTestSql $database @'
BEGIN READ ONLY;
SELECT concat_ws('|',c."curHP",c."curMP",c.vitals_revision,
 p.current_energy,p.maximum_energy,p.revision,
 to_char(p.updated_at AT TIME ZONE 'UTC','YYYY-MM-DD HH24:MI:SS.US'),
 (SELECT count(*) FROM command_audit a
   WHERE a.command_family='pet_unseal_vitals_energy_repair'
     AND a.principal_key='13' AND a.aggregate_key='character:2'),
 (SELECT count(*) FROM pg_trigger WHERE
   tgrelid='public.command_audit'::regclass
   AND tgname='trg_command_audit_immutable' AND tgenabled<>'D'))
FROM character_base c CROSS JOIN character_pets p
WHERE c.id=2 AND c.account_id=13 AND p.id=1 AND p.user_id=2;
COMMIT;
'@
    $verify = @($verifyRows)[0]
    if ($verify -cne
        '134341|6047|9896|100|100|1414|2026-08-14 03:55:19.547448|1|1') {
        throw "Exact-state SQL verification failed: $verify"
    }

    $null = Test-PetUnsealRepairSqlFailure $database @"
BEGIN;
UPDATE command_audit SET outcome_code='tampered'
WHERE id=$($applied.auditId);
COMMIT;
"@

    $null = Invoke-PetUnsealRepairTestSql $database @'
BEGIN;
UPDATE character_base SET "MaxHP"=1501 WHERE id=2;
COMMIT;
'@
    $failedClosed = $false
    try {
        $null = & $tool -Mode Apply -Database $database `
            -DisposableTest -Confirm:$false
    }
    catch {
        $failedClosed = $true
    }
    if (-not $failedClosed) {
        throw 'A changed calculated maximum did not fail closed.'
    }

    [pscustomobject]@{
        Status = 'Passed'
        Database = $database
        CharacterHp = '29350 -> 134341'
        CharacterMp = '1287 -> 6047'
        VitalsRevision = '9895 -> 9896'
        PetEnergy = '31/100 -> 100/100'
        PetRevision = '1413 -> 1414'
        PetUpdatedAtPreserved = $true
        CommandAuditId = $applied.auditId
        Replay = 'Idempotent'
        ChangedMaximum = 'Refused'
        ImmutableAudit = 'Verified'
    }
}
finally {
    if ($created) {
        if ($database -notmatch $databasePattern) {
            throw 'Refusing to drop an unvalidated disposable database.'
        }
        Invoke-PetUnsealRepairAdminSql "DROP DATABASE $database;"
    }
    $liveAfter = Get-PetUnsealRepairLiveFingerprint
    if ($liveAfter -cne $liveBefore) {
        throw 'The disposable test detected a live-database state change.'
    }
}
