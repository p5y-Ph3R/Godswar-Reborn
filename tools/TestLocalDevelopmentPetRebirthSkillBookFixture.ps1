[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgres = 'godswar-dev-postgres'
$token = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$database = "godswar_pet_rebirth_fixture_$token"
$dump = "/tmp/$database.dump"
$databaseCreated = $false
$tool = Join-Path $PSScriptRoot `
    'PrepareLocalDevelopmentPetRebirthSkillBookFixture.ps1'
$assertions = 0

. (Join-Path $PSScriptRoot `
    'RepairLocalDevelopmentPetRebirthExperience.Guards.ps1')
. (Join-Path $PSScriptRoot `
    'PrepareLocalDevelopmentPetRebirthSkillBookFixture.Sql.ps1')
. (Join-Path $PSScriptRoot `
    'PrepareLocalDevelopmentPetRebirthSkillBookFixture.Apply.Audit.ps1')
. (Join-Path $PSScriptRoot `
    'PrepareLocalDevelopmentPetRebirthSkillBookFixture.Apply.ps1')

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) {
        throw "$Label expected '$Expected', got '$Actual'."
    }
    $script:assertions++
}

function Invoke-Docker([string[]]$Arguments, [string]$Label) {
    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed:`n$(@($output) -join "`n")"
    }
    @($output)
}

function Invoke-TestPsql([string]$Sql, [string]$Marker) {
    $output = $Sql | & docker exec -i $postgres psql -X -q -A -t `
        -v ON_ERROR_STOP=1 -U godswar -d $database 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Disposable fixture SQL failed:`n$(@($output) -join "`n")"
    }
    $line = @($output | ForEach-Object { $_.ToString() }) |
        Where-Object {
            $_.StartsWith($Marker, [StringComparison]::Ordinal)
        } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw "Disposable fixture SQL returned no '$Marker' record."
    }
    $line.Substring($Marker.Length) | ConvertFrom-Json
}

function Get-StateEvidence {
    $sql = @'
SELECT 'FIXTURE_TEST_STATE|' || jsonb_build_object(
  'nonBagHash', md5(COALESCE((SELECT jsonb_agg(to_jsonb(item)
      ORDER BY item.item_location,item.slot_index,item.id)::text
      FROM public.character_items item
      WHERE item.user_id=2 AND item.item_location<>1),'')),
  'otherPetsHash', md5(COALESCE((SELECT jsonb_agg(to_jsonb(pet)
      ORDER BY pet.id)::text FROM public.character_pets pet
      WHERE pet.user_id=2 AND pet.id<>1),'')),
  'statsHash', md5(COALESCE((SELECT jsonb_agg(to_jsonb(stat)
      ORDER BY stat.stat_code)::text
      FROM public.character_pet_stat_values stat WHERE stat.pet_id=1),'')),
  'inventoryRevision',(SELECT inventory_revision FROM public.character_base
      WHERE id=2),
  'experience',(SELECT experience FROM public.character_pets WHERE id=1),
  'petRevision',(SELECT revision FROM public.character_pets WHERE id=1),
  'bagRows',(SELECT count(*) FROM public.character_items
      WHERE user_id=2 AND item_location=1),
  'bagUnits',(SELECT COALESCE(sum(stack),0) FROM public.character_items
      WHERE user_id=2 AND item_location=1),
  'commandAudits',(SELECT count(*) FROM public.command_audit
      WHERE command_family='pet_rebirth_skillbook_fixture'
        AND aggregate_key='character:2|pet:1'),
  'itemAudits',(SELECT count(*) FROM public.character_item_audit
      WHERE source='localdev-pet-rebirth-skillbook-fixture-v1'),
  'deleteAudits',(SELECT count(*) FROM public.character_item_audit
      WHERE source='localdev-pet-rebirth-skillbook-fixture-v1'
        AND action='delete'),
  'addAudits',(SELECT count(*) FROM public.character_item_audit
      WHERE source='localdev-pet-rebirth-skillbook-fixture-v1'
        AND action='add'),
  'bagExact', (WITH desired(slot_index,prop_id,stack) AS (VALUES
      (0,10464,1),(1,10465,1),(2,10466,1),(3,10467,1),
      (4,10468,1),(5,10469,1),(6,10510,1),(7,10511,1),
      (8,10512,1),(9,10513,1),(10,10514,1),(11,10515,1),
      (12,10590,1),(13,10591,1),(14,10592,1),(15,10593,1),
      (16,10594,1),(17,10595,1),(18,10700,1),(19,10701,1),
      (20,10702,1),(21,10703,1),(22,10704,1),(23,10705,1),
      (24,10104,99))
    SELECT count(item.id)=25 AND bool_and(
      item.item_quality=1 AND item.item_grade=1 AND item.bound=0
      AND item.stack=desired.stack AND item.item_exp=0
      AND item.holy_suit_code=0)
    FROM desired LEFT JOIN public.character_items item
      ON item.user_id=2 AND item.item_location=1
     AND item.slot_index=desired.slot_index AND item.prop_id=desired.prop_id),
  'sealedLinks',(SELECT count(*) FROM public.character_items item
      JOIN public.sealed_pet_items sealed ON sealed.item_instance_id=item.id
      WHERE item.user_id=2),
  'immutableReceipt',EXISTS(SELECT 1 FROM pg_trigger
      WHERE tgrelid='public.command_audit'::regclass
        AND tgname='trg_command_audit_immutable' AND tgenabled<>'D'))::text;
'@
    Invoke-TestPsql $sql 'FIXTURE_TEST_STATE|'
}

try {
    Invoke-Docker @(
        'exec',$postgres,'pg_dump','-U','godswar','-Fc','-d','godswar',
        '-f',$dump) 'isolated-development clone' | Out-Null
    Invoke-Docker @(
        'exec',$postgres,'createdb','-U','godswar',$database
    ) 'disposable database creation' | Out-Null
    $databaseCreated = $true
    Invoke-Docker @(
        'exec',$postgres,'pg_restore','-U','godswar','-d',$database,
        '--no-owner','--no-privileges',$dump
    ) 'disposable database restore' | Out-Null

    $before = Get-StateEvidence
    Assert-Equal $before.inventoryRevision 690 'source inventory revision'
    Assert-Equal $before.experience 7597955 'source pet EXP'
    Assert-Equal $before.petRevision 1168 'source pet revision'
    Assert-Equal $before.bagRows 62 'source bag rows'
    Assert-Equal $before.bagUnits 1806 'source bag units'

    $revision = (Get-RepairSha256Hex(
        "disposable-pet-rebirth-skillbook-content|$token")).ToUpperInvariant()
    $publishSql = @'
BEGIN;
DO $publish$
DECLARE
  v_old text;
  v_table text;
  v_columns text;
BEGIN
  SELECT revision INTO STRICT v_old
  FROM public.item_template_content_publication WHERE family='items';
  INSERT INTO public.item_template_content_revisions(
    revision,entry_count,source,manifest_version,attribute_count,
    equipment_rank_count,holy_suit_effect_count,material_policy_count,
    material_recipe_count,holy_suit_tier_count,holy_suit_upgrade_count,
    holy_suit_consumable_count,holy_suit_policy_count)
  SELECT '__REVISION__',
    (SELECT count(*) FROM public.item_template_content_definitions
       WHERE revision=v_old AND id<>ALL(ARRAY[
       10464,10465,10466,10467,10468,10469,
       10510,10511,10512,10513,10514,10515,
       10590,10591,10592,10593,10594,10595,
       10700,10701,10702,10703,10704,10705]))+24,
    'disposable-pet-rebirth-skillbook-fixture-test',manifest_version,
    attribute_count,equipment_rank_count,holy_suit_effect_count,
    material_policy_count,material_recipe_count,holy_suit_tier_count,
    holy_suit_upgrade_count,holy_suit_consumable_count,holy_suit_policy_count
  FROM public.item_template_content_revisions WHERE revision=v_old;
  INSERT INTO public.item_template_content_definitions
  SELECT '__REVISION__',id,kind,name_key,display_name,equipment_slot,
    class_ids,min_level,max_level,hand,skill_flag,texture,icon,stats
  FROM public.item_template_content_definitions
  WHERE revision=v_old AND id<>ALL(ARRAY[
    10464,10465,10466,10467,10468,10469,
    10510,10511,10512,10513,10514,10515,
    10590,10591,10592,10593,10594,10595,
    10700,10701,10702,10703,10704,10705]);
  FOREACH v_table IN ARRAY ARRAY[
    'item_attribute_content_definitions',
    'equipment_rank_content_definitions',
    'holy_suit_effect_content_definitions',
    'item_material_content_definitions',
    'holy_suit_tier_content_definitions',
    'holy_suit_consumable_content_definitions',
    'holy_suit_upgrade_content_definitions',
    'holy_suit_operation_policy_content_definitions'] LOOP
    SELECT string_agg(quote_ident(column_name),',' ORDER BY ordinal_position)
      INTO v_columns FROM information_schema.columns
      WHERE table_schema='public' AND table_name=v_table
        AND column_name<>'revision';
    EXECUTE format('INSERT INTO public.%I (revision,%s) '
      || 'SELECT $1,%s FROM public.%I WHERE revision=$2',
      v_table,v_columns,v_columns,v_table) USING '__REVISION__',v_old;
  END LOOP;
END
$publish$;
WITH expected(slot_index,prop_id,display_name,pet_skill) AS (VALUES
__MANIFEST_VALUES__
), inserted_definitions AS (
  INSERT INTO public.item_template_content_definitions(
    revision,id,kind,name_key,display_name,equipment_slot,class_ids,
    min_level,max_level,hand,skill_flag,texture,icon,stats)
  SELECT '__REVISION__',prop_id,'consume item','Pet'||prop_id::text,
    display_name,0,'{}'::smallint[],NULL,NULL,NULL,NULL,
    './Localization/en_us/UI/Texture/Icon2.gwo','216,972',
    jsonb_build_object('ID',prop_id::text,'Type','consume item','Texture',
      './Localization/en_us/UI/Texture/Icon2.gwo','Icon','216,972',
      'Random','0','Distribution','0,0','Money','0','Overlap','99',
      'Use','1','ItemType',CASE WHEN slot_index%6=0 THEN '4' ELSE '3' END,
      'PetSkill',pet_skill::text)
  FROM expected RETURNING *)
INSERT INTO public.item_templates(
  id,kind,name_key,display_name,equipment_slot,class_ids,min_level,
  max_level,hand,skill_flag,texture,icon,stats)
SELECT id,kind,name_key,display_name,equipment_slot,class_ids,min_level,
  max_level,hand,skill_flag,texture,icon,stats FROM inserted_definitions
ON CONFLICT(id) DO UPDATE SET kind=excluded.kind,name_key=excluded.name_key,
  display_name=excluded.display_name,equipment_slot=excluded.equipment_slot,
  class_ids=excluded.class_ids,min_level=excluded.min_level,
  max_level=excluded.max_level,hand=excluded.hand,
  skill_flag=excluded.skill_flag,texture=excluded.texture,
  icon=excluded.icon,stats=excluded.stats;
UPDATE public.item_template_content_publication
SET revision='__REVISION__' WHERE family='items';
COMMIT;
'@
    $publishSql = $publishSql.Replace('__REVISION__',$revision).Replace(
        '__MANIFEST_VALUES__',(Get-PetRebirthSkillBookManifestValues))
    Invoke-TestPsql ($publishSql + "`nSELECT 'PUBLISHED|{}';") 'PUBLISHED|' |
        Out-Null

    $ready = & $tool -Mode Status -Database $database -DisposableTest
    Assert-Equal $ready.Status 'Ready' 'pre-fixture status'
    Assert-Equal $ready.ContentDefinitionCount 24 'published book count'
    Assert-Equal $ready.ContentValid $true 'published content validity'
    Assert-Equal $ready.CurrentBagRows 62 'ready bag rows'
    Assert-Equal $ready.TargetBagRows 25 'target bag rows'

    $whatIf = @(& $tool -Mode Apply -Database $database `
        -DisposableTest -WhatIf)
    Assert-Equal $whatIf.Count 0 'WhatIf makes no mutation result'
    $stillReady = & $tool -Mode Status -Database $database -DisposableTest
    Assert-Equal $stillReady.Status 'Ready' 'WhatIf preserves source'

    $applied = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    Assert-Equal $applied.status 'Applied' 'fixture apply result'
    Assert-Equal $applied.removedRows 62 'removed row count'
    Assert-Equal $applied.addedRows 25 'added row count'
    Assert-Equal $applied.currentExperience 1507597955 'target EXP result'
    Assert-Equal $applied.currentPetRevision 1169 'target pet revision result'
    Assert-Equal $applied.currentInventoryRevision 691 `
        'target inventory revision result'

    $operationText =
      'localdev|pet-rebirth-skillbooks|account:13|character:2|pet:1|v2'
    $requestText = $operationText +
      '|source-inv:690|source-pet-rev:1168|source-exp:7597955' +
      '|delta:1500000000|target:1507597955' +
      '|bag:skills-10464-10469,10510-10515,10590-10595,10700-10705' +
      '|rebirth-spirit:10104x99@24'
    $directReplay = Invoke-TestPsql (Get-PetRebirthSkillBookApplySql `
      (Get-RepairSha256Hex $operationText) `
      (Get-RepairSha256Hex $requestText)) `
      'PET_REBIRTH_SKILLBOOK_FIXTURE_RESULT|'
    Assert-Equal $directReplay.status 'AlreadyApplied' `
        'transactional receipt replay'
    Assert-Equal $directReplay.auditId $applied.auditId `
        'transactional replay audit identity'

    $retried = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    Assert-Equal $retried.Status 'Applied' 'wrapper idempotent retry'
    Assert-Equal $retried.ReceiptAuditId $applied.auditId `
        'wrapper retry audit identity'

    $after = Get-StateEvidence
    Assert-Equal $after.nonBagHash $before.nonBagHash `
        'equipment and storage unchanged'
    Assert-Equal $after.otherPetsHash $before.otherPetsHash `
        'other pets unchanged'
    Assert-Equal $after.statsHash $before.statsHash 'main pet stats unchanged'
    Assert-Equal $after.inventoryRevision 691 'stored inventory revision'
    Assert-Equal $after.experience 1507597955 'stored pet EXP'
    Assert-Equal $after.petRevision 1169 'stored pet revision'
    Assert-Equal $after.bagRows 25 'stored bag rows'
    Assert-Equal $after.bagUnits 123 'stored bag units'
    Assert-Equal $after.bagExact $true 'exact 25-row bag manifest'
    Assert-Equal $after.commandAudits 1 'one permanent command receipt'
    Assert-Equal $after.itemAudits 87 'all item audit rows'
    Assert-Equal $after.deleteAudits 62 'delete audit rows'
    Assert-Equal $after.addAudits 25 'add audit rows'
    Assert-Equal $after.sealedLinks 0 'sealed links remain absent'
    Assert-Equal $after.immutableReceipt $true `
        'command receipt immutability trigger'

    Write-Host (
      "Pet rebirth skill-book fixture checks passed: $assertions assertions.")
}
finally {
    if ($databaseCreated -and $database -match
        '^godswar_pet_rebirth_fixture_[a-f0-9]{10}$') {
        & docker exec $postgres dropdb -U godswar --force $database `
            2>$null | Out-Null
    }
    if ($dump -match
        '^/tmp/godswar_pet_rebirth_fixture_[a-f0-9]{10}\.dump$') {
        & docker exec $postgres rm -f -- $dump 2>$null | Out-Null
    }
}
