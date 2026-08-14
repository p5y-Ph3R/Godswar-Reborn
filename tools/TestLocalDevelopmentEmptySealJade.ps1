[CmdletBinding()]
param()

# Logical-clone mutation test. Only a validated disposable database is
# changed/dropped; the isolated live authority is fingerprinted before/after.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$databaseUser = 'godswar'
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$database = "godswar_empty_seal_jade_$suffix"
$databasePattern = '^godswar_empty_seal_jade_[a-f0-9]{10}$'
if ($database -notmatch $databasePattern) {
    throw 'Disposable database name generation failed.'
}

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
$null = Initialize-RebirthRepairEnvironment $postgresContainer `
    'godswar-dev-server' 'godswar-dev-redis-coordination'

function Invoke-EmptySealJadeTestSql(
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

function Get-EmptySealJadeLiveFingerprint {
    $sql = @'
BEGIN READ ONLY;
SELECT 'LIVE|' || encode(sha256(convert_to(jsonb_build_object(
 'account',(SELECT to_jsonb(a) FROM accounts a WHERE id=13),
 'character',(SELECT to_jsonb(c) FROM character_base c WHERE id=2),
 'items',COALESCE((SELECT jsonb_agg(to_jsonb(i) ORDER BY i.item_location,
    i.slot_index,i.id) FROM character_items i WHERE i.user_id=2),'[]'::jsonb),
 'pets',COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id)
    FROM character_pets p WHERE p.user_id=2),'[]'::jsonb),
 'stats',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.pet_id,s.stat_code)
    FROM character_pet_stat_values s WHERE s.pet_id IN
      (SELECT id FROM character_pets WHERE user_id=2)),'[]'::jsonb),
 'skills',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.pet_id,s.slot_index)
    FROM character_pet_skills s WHERE s.pet_id IN
      (SELECT id FROM character_pets WHERE user_id=2)),'[]'::jsonb),
 'growth',COALESCE((SELECT jsonb_agg(to_jsonb(g) ORDER BY g.pet_id)
    FROM character_pet_growth_previews g WHERE g.user_id=2),'[]'::jsonb),
 'savvy',COALESCE((SELECT jsonb_agg(to_jsonb(g) ORDER BY g.pet_id)
    FROM character_pet_basic_savvy_previews g WHERE g.user_id=2),'[]'::jsonb),
 'sealed',COALESCE((SELECT jsonb_agg(to_jsonb(l) ORDER BY l.id)
    FROM sealed_pet_items l WHERE l.owner_character_id=2),'[]'::jsonb),
 'receipt',COALESCE((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id)
    FROM command_audit a WHERE a.command_family='empty_seal_jade_grant'
      AND a.principal_key='13' AND a.aggregate_key='character:2'),
    '[]'::jsonb),
 'itemAudits',COALESCE((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id)
    FROM character_item_audit a
    WHERE a.source='localdev-empty-seal-jade-grant-v1'
      AND a.user_id=2),'[]'::jsonb)
)::text,'UTF8')),'hex');
COMMIT;
'@
    $lines = Invoke-EmptySealJadeTestSql 'godswar' $sql
    $line = $lines | Where-Object {
        $_.StartsWith('LIVE|', [StringComparison]::Ordinal)
    } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw 'Live fingerprint query returned no marker.'
    }
    $line.Substring(5)
}

function Invoke-EmptySealJadeAdminSql([string]$Sql) {
    $null = Invoke-EmptySealJadeTestSql 'postgres' $Sql
}

$liveBefore = Get-EmptySealJadeLiveFingerprint
$created = $false
try {
    Invoke-EmptySealJadeAdminSql "CREATE DATABASE $database;"
    $created = $true
    $copyCommand =
        "pg_dump --no-owner --no-privileges -U $databaseUser godswar" +
        " | psql -X -q -v ON_ERROR_STOP=1 -U $databaseUser -d $database"
    $copyOutput = & docker exec $postgresContainer sh -c $copyCommand 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Logical clone failed:`n$($copyOutput -join "`n")"
    }

    $tool = Join-Path $PSScriptRoot `
        'GrantLocalDevelopmentEmptySealJade.ps1'
    $ready = & $tool -Mode Status -Database $database -DisposableTest
    if ($ready.Status -ne 'Ready' -or -not $ready.ContentValid -or
        -not $ready.CatalogReviewed -or -not $ready.SourceReady -or
        $ready.CurrentInventoryRevision -ne 729 -or
        $ready.CurrentBagRows -ne 1 -or $ready.TargetSlot -ne 0 -or
        $ready.MainPet.petId -ne 1 -or $ready.MainPet.revision -ne 1406) {
        throw 'Disposable clone did not preserve the exact ready state.'
    }

    $applied = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    if ($applied.status -ne 'Applied' -or -not $applied.changed -or
        $applied.auditId -le 0 -or $applied.itemAuditId -le 0 -or
        $applied.itemId -ne 10108 -or $applied.slot -ne 0 -or
        $applied.inventoryRevisionAfter -ne 730) {
        throw 'Disposable fixture did not commit the exact Seal Jade grant.'
    }
    $repeat = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    if ($repeat.Status -ne 'Applied' -or
        $repeat.ReceiptAuditId -ne $applied.auditId -or
        $repeat.LinkedItemAuditCount -ne 1 -or -not $repeat.PostReady) {
        throw 'Immediate replay was not idempotent and fully audited.'
    }

    $consumeSql = @'
BEGIN;
DELETE FROM character_items
WHERE user_id=2 AND item_location=1 AND slot_index=0 AND prop_id=10108;
UPDATE character_base SET inventory_revision=731
WHERE id=2 AND inventory_revision=730;
COMMIT;
'@
    $null = Invoke-EmptySealJadeTestSql $database $consumeSql
    $afterConsumption = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    if ($afterConsumption.Status -ne 'Applied' -or
        $afterConsumption.ReceiptAuditId -ne $applied.auditId -or
        $afterConsumption.PostReady) {
        throw 'Permanent receipt did not prevent a consumed-item re-grant.'
    }
    $rows = Invoke-EmptySealJadeTestSql $database @'
BEGIN READ ONLY;
SELECT count(*) FROM character_items
WHERE user_id=2 AND prop_id=10108;
SELECT count(*) FROM command_audit
WHERE command_family='empty_seal_jade_grant'
  AND principal_key='13' AND aggregate_key='character:2';
SELECT count(*) FROM character_item_audit
WHERE source='localdev-empty-seal-jade-grant-v1' AND user_id=2;
COMMIT;
'@
    if (@($rows) -join ',' -ne '0,1,1') {
        throw 'Consumed-item replay changed the permanent audit cardinality.'
    }

    [pscustomobject]@{
        Status = 'Passed'
        Database = $database
        InventoryRevisionBefore = 729
        InventoryRevisionAfterGrant = 730
        PreservedBagItem = '10104 x94 @ slot 24'
        GrantedItem = '10108 x1 @ slot 0'
        ItemAuditCount = 1
        CommandAuditId = $applied.auditId
        ReplayAfterConsumption = 'AppliedWithoutRegrant'
    }
}
finally {
    if ($created) {
        if ($database -notmatch $databasePattern) {
            throw 'Refusing to drop an unvalidated disposable database.'
        }
        Invoke-EmptySealJadeAdminSql "DROP DATABASE $database;"
    }
    $liveAfter = Get-EmptySealJadeLiveFingerprint
    if ($liveAfter -cne $liveBefore) {
        throw 'The disposable test detected a live-database state change.'
    }
}
