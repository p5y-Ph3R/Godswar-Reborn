[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgres = 'godswar-dev-postgres'
$token = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$database = "godswar_b12_rebirth_compensation_$token"
$dump = "/tmp/$database.dump"
$tool = Join-Path $PSScriptRoot `
    'CompensateLocalDevelopmentPetRebirthExperience.ps1'
$assertions = 0

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

try {
    Invoke-Docker @(
        'exec', $postgres, 'pg_dump', '-U', 'godswar',
        '-Fc', '-d', 'godswar', '-f', $dump
    ) 'isolated-development clone' | Out-Null
    Invoke-Docker @(
        'exec', $postgres, 'createdb', '-U', 'godswar', $database
    ) 'disposable database creation' | Out-Null
    Invoke-Docker @(
        'exec', $postgres, 'pg_restore', '-U', 'godswar',
        '-d', $database, '--no-owner', '--no-privileges', $dump
    ) 'disposable database restore' | Out-Null

    $ready = & $tool -Mode Status -Database $database -DisposableTest
    Assert-Equal $ready.Status 'Ready' 'pre-compensation status'
    Assert-Equal $ready.PetLevel 91 'pre-compensation level'
    Assert-Equal $ready.CurrentExperience 0 'pre-compensation EXP'
    Assert-Equal $ready.WrongRefund 127824945 'incorrect refund evidence'
    Assert-Equal $ready.CorrectRefund 242980800 'correct refund evidence'
    Assert-Equal $ready.Compensation 115155855 'compensation delta'
    Assert-Equal $ready.LevelUpgradeTraceCount 90 'exact spend trace'

    $applied = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    Assert-Equal $applied.status 'Applied' 'compensation apply result'
    Assert-Equal $applied.petLevel 91 'compensation preserves level'
    Assert-Equal $applied.currentExperience 115155855 `
        'compensation adds only the missing delta'
    Assert-Equal $applied.currentPetRevision 441 `
        'compensation increments revision'

    $retried = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    Assert-Equal $retried.Status 'Applied' 'idempotent retry status'
    Assert-Equal $retried.CompensationAuditId `
        $applied.compensationAuditId 'idempotent audit identity'

    $sql = @'
SELECT jsonb_build_object(
    'level', pet.level, 'experience', pet.experience,
    'revision', pet.revision,
    'auditCount', count(audit.id),
    'auditId', min(audit.id),
    'sourceRepairPreserved', EXISTS (
        SELECT 1 FROM public.command_audit WHERE id = 8023
          AND command_family = 'pet_rebirth_experience_repair'
          AND outcome_code = 'repaired'),
    'levelTracePreserved', 90 = (SELECT count(*)
        FROM public.command_audit WHERE id BETWEEN 8031 AND 8120
          AND command_family = 'pet_level_upgrade'),
    'statsRemainLevel91', 6 = (SELECT count(*)
        FROM public.character_pet_stat_values WHERE pet_id = 1
          AND added_savvy = (base_growth_rate + growth_acceleration) * 91),
    'immutableTrigger', EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgrelid = 'public.command_audit'::regclass
          AND tgname = 'trg_command_audit_immutable'
          AND tgenabled <> 'D'))::text
FROM public.character_pets pet
LEFT JOIN public.command_audit audit
  ON audit.command_family = 'pet_rebirth_experience_compensation'
 AND audit.aggregate_type = 'pet' AND audit.aggregate_key = '1'
WHERE pet.id = 1
GROUP BY pet.level, pet.experience, pet.revision;
'@
    $raw = $sql | & docker exec -i $postgres psql -X -q -A -t `
        -v ON_ERROR_STOP=1 -U godswar -d $database 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Compensation verification query failed:`n$(@($raw) -join "`n")"
    }
    $evidence = @($raw)[-1] | ConvertFrom-Json
    Assert-Equal $evidence.level 91 'stored level'
    Assert-Equal $evidence.experience 115155855 'stored EXP'
    Assert-Equal $evidence.revision 441 'stored revision'
    Assert-Equal $evidence.auditCount 1 'one compensation audit'
    Assert-Equal $evidence.auditId $applied.compensationAuditId `
        'stored compensation audit identity'
    Assert-Equal $evidence.sourceRepairPreserved $true `
        'incorrect repair evidence remains immutable'
    Assert-Equal $evidence.levelTracePreserved $true `
        '90-level spend evidence remains intact'
    Assert-Equal $evidence.statsRemainLevel91 $true `
        'EXP-only compensation preserves stats'
    Assert-Equal $evidence.immutableTrigger $true `
        'compensation evidence is immutable'

    Write-Host (
        "Pet rebirth EXP compensation checks passed: $assertions assertions.")
}
finally {
    if ($database -match
        '^godswar_b12_rebirth_compensation_[a-f0-9]{10}$') {
        & docker exec $postgres dropdb -U godswar --force $database `
            2>$null | Out-Null
    }
    if ($dump -match
        '^/tmp/godswar_b12_rebirth_compensation_[a-f0-9]{10}\.dump$') {
        & docker exec $postgres rm -f -- $dump 2>$null | Out-Null
    }
}
