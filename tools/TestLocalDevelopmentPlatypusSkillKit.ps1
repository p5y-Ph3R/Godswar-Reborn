[CmdletBinding()]
param()

# Transaction-consistent logical-clone test. Only a validated disposable DB is
# mutated/dropped; the isolated live database is fingerprinted before/after.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgresContainer = 'godswar-dev-postgres'
$databaseUser = 'godswar'
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$database = "godswar_platypus_skill_kit_$suffix"
$databasePattern = '^godswar_platypus_skill_kit_[a-f0-9]{10}$'
if ($database -notmatch $databasePattern) {
    throw 'Disposable database name generation failed.'
}

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
. (Join-Path $PSScriptRoot `
    'TestLocalDevelopmentPlatypusSkillKit.Content.ps1')
$null = Initialize-RebirthRepairEnvironment $postgresContainer `
    'godswar-dev-server' 'godswar-dev-redis-coordination'

function Invoke-PlatypusKitTestSql(
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

function Get-PlatypusKitLiveFingerprint {
    $sql = @'
BEGIN READ ONLY;
SELECT 'LIVE|' || jsonb_build_object(
 'inventoryRevision',(SELECT inventory_revision FROM character_base WHERE id=2),
 'itemsSha256',encode(sha256(convert_to(COALESCE((SELECT jsonb_agg(
    to_jsonb(i) ORDER BY item_location,slot_index,id) FROM character_items i
    WHERE user_id=2),'[]'::jsonb)::text,'UTF8')),'hex'),
 'petsSha256',encode(sha256(convert_to(COALESCE((SELECT jsonb_agg(
    to_jsonb(p) ORDER BY id) FROM character_pets p WHERE user_id=2),
    '[]'::jsonb)::text,'UTF8')),'hex'),
 'itemRevision',(SELECT revision FROM item_template_content_publication
                 WHERE family='items'),
 'receiptCount',(SELECT count(*) FROM command_audit
    WHERE command_family='platypus_skill_kit_grant'
      AND principal_key='13' AND aggregate_key='character:2'))::text;
COMMIT;
'@
    $lines = Invoke-PlatypusKitTestSql 'godswar' $sql
    $line = $lines | Where-Object {
        $_.StartsWith('LIVE|', [StringComparison]::Ordinal)
    } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw 'Live fingerprint query returned no marker.'
    }
    $line.Substring(5)
}

function Invoke-PlatypusKitAdminSql([string]$Sql) {
    $null = Invoke-PlatypusKitTestSql 'postgres' $Sql
}

$liveBefore = Get-PlatypusKitLiveFingerprint
$created = $false
try {
    Invoke-PlatypusKitAdminSql "CREATE DATABASE $database;"
    $created = $true
    $copyCommand =
        "pg_dump --no-owner --no-privileges -U $databaseUser godswar" +
        " | psql -X -q -v ON_ERROR_STOP=1 -U $databaseUser -d $database"
    $copyOutput = & docker exec $postgresContainer sh -c $copyCommand 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Logical clone failed:`n$($copyOutput -join "`n")"
    }

    $tool = Join-Path $PSScriptRoot `
        'GrantLocalDevelopmentPlatypusSkillKit.ps1'
    $initial = & $tool -Mode Status -Database $database -DisposableTest
    if ($initial.Status -notin @('Refused', 'Ready') -or
        $initial.CurrentInventoryRevision -ne 721 -or
        $initial.CurrentBagRows -ne 1 -or
        $initial.MainPet.petId -ne 1 -or
        -not $initial.MainPet.isSummoned) {
        throw 'Disposable clone did not preserve the pinned source state.'
    }
    if (-not $initial.ItemContentValid) {
        $null = Invoke-PlatypusKitTestSql $database `
            (Get-PlatypusSkillKitDisposableContentSql)
    }
    $ready = & $tool -Mode Status -Database $database -DisposableTest
    if ($ready.Status -ne 'Ready' -or -not $ready.ItemContentValid -or
        -not $ready.ActivationContentValid -or
        -not $ready.ActivationPolicyReviewed) {
        throw 'Disposable exact content/source state did not become Ready.'
    }

    $applied = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    if ($applied.status -ne 'Applied' -or -not $applied.changed -or
        $applied.auditId -le 0) {
        throw 'Disposable fixture did not commit the seven-item grant.'
    }
    $repeat = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    if ($repeat.Status -ne 'Applied' -or
        $repeat.ReceiptAuditId -ne $applied.auditId -or
        $repeat.LinkedItemAuditCount -ne 7 -or -not $repeat.PostReady) {
        throw 'Immediate replay was not idempotent and fully audited.'
    }

    $consumeSql = @'
BEGIN;
DELETE FROM character_items
WHERE user_id=2 AND item_location=1 AND slot_index=31 AND prop_id=10535;
UPDATE character_base SET inventory_revision=723
WHERE id=2 AND inventory_revision=722;
COMMIT;
'@
    $null = Invoke-PlatypusKitTestSql $database $consumeSql
    $afterConsumption = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    if ($afterConsumption.Status -ne 'Applied' -or
        $afterConsumption.ReceiptAuditId -ne $applied.auditId -or
        $afterConsumption.PostReady) {
        throw 'Permanent receipt did not prevent a consumed-item re-grant.'
    }

    [pscustomobject]@{
        Status = 'Passed'
        Database = $database
        InitialPublicationAccepted = $initial.ItemContentValid
        InventoryRevisionBefore = 721
        InventoryRevisionAfterGrant = 722
        PreservedBagItem = '10104 x94 @ slot 24'
        GrantedItemIds = @(11080, 10530, 10531, 10532,
                           10533, 10534, 10535)
        ItemAuditCount = 7
        CommandAuditId = $applied.auditId
        ReplayAfterConsumption = 'AppliedWithoutRegrant'
    }
}
finally {
    if ($created) {
        if ($database -notmatch $databasePattern) {
            throw 'Refusing to drop an unvalidated disposable database.'
        }
        Invoke-PlatypusKitAdminSql "DROP DATABASE $database;"
    }
    $liveAfter = Get-PlatypusKitLiveFingerprint
    if ($liveAfter -cne $liveBefore) {
        throw 'The disposable test detected a live-database state change.'
    }
}
