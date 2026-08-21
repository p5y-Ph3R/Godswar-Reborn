[CmdletBinding()]
param()

# Logical-clone apply/replay proof. The live isolated database is fingerprinted
# before and after; only the validated disposable clone is mutated and dropped.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$databaseUser = 'godswar'
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$database = "godswar_empty_seal_jade_v3_$suffix"
$databasePattern = '^godswar_empty_seal_jade_v3_[a-f0-9]{10}$'
if ($database -notmatch $databasePattern) {
    throw 'Disposable database name generation failed.'
}

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
$null = Initialize-RebirthRepairEnvironment $postgresContainer `
    'godswar-dev-tempest-openworld-01' 'godswar-dev-redis-coordination'

function Invoke-EmptySealJadeV3TestSql(
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

function Get-EmptySealJadeV3LiveFingerprint {
    $sql = @'
BEGIN READ ONLY;
SELECT 'LIVE|' || encode(sha256(convert_to(jsonb_build_object(
 'account',(SELECT to_jsonb(a) FROM accounts a WHERE id=13),
 'character',(SELECT to_jsonb(c) FROM character_base c WHERE id=2),
 'items',COALESCE((SELECT jsonb_agg(to_jsonb(i) ORDER BY i.item_location,
    i.slot_index,i.id) FROM character_items i WHERE i.user_id=2),'[]'::jsonb),
 'pets',COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id)
    FROM character_pets p WHERE p.user_id=2),'[]'::jsonb),
 'stats',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.pet_id,
    s.stat_code) FROM character_pet_stat_values s WHERE s.pet_id IN
      (SELECT id FROM character_pets WHERE user_id=2)),'[]'::jsonb),
 'skills',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.pet_id,
    s.slot_index) FROM character_pet_skills s WHERE s.pet_id IN
      (SELECT id FROM character_pets WHERE user_id=2)),'[]'::jsonb),
 'receipts',COALESCE((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id)
    FROM command_audit a
    WHERE a.command_family='empty_seal_jade_grant_repeat'
      AND a.principal_key='13' AND a.aggregate_key='character:2'),
    '[]'::jsonb),
 'itemAudits',COALESCE((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id)
    FROM character_item_audit a
    WHERE a.source='localdev-empty-seal-jade-grant-v3'
      AND a.user_id=2),'[]'::jsonb)
)::text,'UTF8')),'hex');
COMMIT;
'@
    $lines = Invoke-EmptySealJadeV3TestSql 'godswar' $sql
    $line = $lines | Where-Object {
        $_.StartsWith('LIVE|', [StringComparison]::Ordinal)
    } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw 'Live fingerprint query returned no marker.'
    }
    $line.Substring(5)
}

function Invoke-EmptySealJadeV3AdminSql([string]$Sql) {
    $null = Invoke-EmptySealJadeV3TestSql 'postgres' $Sql
}

$liveBefore = Get-EmptySealJadeV3LiveFingerprint
$created = $false
try {
    Invoke-EmptySealJadeV3AdminSql "CREATE DATABASE $database;"
    $created = $true
    $copyCommand =
        "pg_dump --no-owner --no-privileges -U $databaseUser godswar" +
        " | psql -X -q -v ON_ERROR_STOP=1 -U $databaseUser -d $database"
    $copyOutput = & docker exec $postgresContainer sh -c $copyCommand 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Logical clone failed:`n$($copyOutput -join "`n")"
    }

    $tool = Join-Path $PSScriptRoot `
        'GrantLocalDevelopmentEmptySealJadeV3.ps1'
    $ready = & $tool -Mode Status -Database $database -DisposableTest
    if ($ready.Status -ne 'Ready' -or -not $ready.ContentValid -or
        -not $ready.CatalogReviewed -or -not $ready.SourceReady -or
        $ready.CurrentInventoryRevision -ne 735 -or
        $ready.CurrentHp -ne 134341 -or $ready.CurrentMp -ne 6047 -or
        $ready.VitalsRevision -ne 9896 -or $ready.CurrentBagRows -ne 1 -or
        $ready.TargetSlot -ne 0 -or $ready.MainPet.revision -ne 1414 -or
        $ready.MainPet.energy -ne 100) {
        throw 'Disposable clone did not preserve the exact repaired state.'
    }

    $applied = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    if ($applied.status -ne 'Applied' -or -not $applied.changed -or
        $applied.auditId -le 0 -or $applied.itemAuditId -le 0 -or
        $applied.itemId -ne 10108 -or $applied.slot -ne 0 -or
        $applied.inventoryRevisionAfter -ne 736) {
        throw 'Disposable fixture did not commit the exact grant.'
    }
    $repeat = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    if ($repeat.Status -ne 'Applied' -or -not $repeat.PostReady -or
        $repeat.ReceiptAuditId -ne $applied.auditId -or
        $repeat.LinkedItemAuditCount -ne 1 -or
        $repeat.CurrentHp -ne 134341 -or $repeat.CurrentMp -ne 6047 -or
        $repeat.MainPet.revision -ne 1414 -or
        $repeat.MainPet.energy -ne 100) {
        throw 'Immediate replay was not idempotent and fully audited.'
    }

    $rows = Invoke-EmptySealJadeV3TestSql $database @'
BEGIN READ ONLY;
SELECT concat_ws('|',c.inventory_revision,c."curHP",c."curMP",
 c.vitals_revision,p.revision,p.current_energy,p.maximum_energy,
 (SELECT count(*) FROM character_items i WHERE i.user_id=2
   AND i.item_location=1 AND i.slot_index=0 AND i.prop_id=10108),
 (SELECT count(*) FROM command_audit a
   WHERE a.command_family='empty_seal_jade_grant_repeat'
     AND a.detail_payload->>'fixtureVersion'='3'),
 (SELECT count(*) FROM character_item_audit a
   WHERE a.source='localdev-empty-seal-jade-grant-v3' AND a.user_id=2))
FROM character_base c CROSS JOIN character_pets p
WHERE c.id=2 AND p.id=1;
COMMIT;
'@
    if (@($rows)[0] -cne '736|134341|6047|9896|1414|100|100|1|1|1') {
        throw "Exact SQL verification failed: $(@($rows)[0])"
    }

    [pscustomobject]@{
        Status = 'Passed'
        Database = $database
        InventoryRevision = '735 -> 736'
        GrantedItem = '10108 x1 @ slot 0'
        CharacterVitals = '134341 HP / 6047 MP @ revision 9896 preserved'
        PetState = 'pet 1 @ revision 1414, energy 100/100 preserved'
        CommandAuditId = $applied.auditId
        ItemAuditId = $applied.itemAuditId
        Replay = 'Idempotent'
    }
}
finally {
    if ($created) {
        if ($database -notmatch $databasePattern) {
            throw 'Refusing to drop an unvalidated disposable database.'
        }
        Invoke-EmptySealJadeV3AdminSql "DROP DATABASE $database;"
    }
    $liveAfter = Get-EmptySealJadeV3LiveFingerprint
    if ($liveAfter -cne $liveBefore) {
        throw 'The disposable test detected a live-database state change.'
    }
}
