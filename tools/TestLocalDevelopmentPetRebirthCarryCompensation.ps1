[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgres = 'godswar-dev-postgres'
$token = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$database = "godswar_rebirth_carry_$token"
$dump = "/tmp/$database.dump"
$databaseCreated = $false
$tool = Join-Path $PSScriptRoot `
    'CompensateLocalDevelopmentPetRebirthCarry.ps1'
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
    $databaseCreated = $true
    Invoke-Docker @(
        'exec', $postgres, 'pg_restore', '-U', 'godswar',
        '-d', $database, '--no-owner', '--no-privileges', $dump
    ) 'disposable database restore' | Out-Null

    $ready = & $tool -Mode Status -Database $database -DisposableTest
    Assert-Equal $ready.Status 'Ready' 'pre-compensation status'
    Assert-Equal $ready.CurrentLevel 120 'pre-compensation level'
    Assert-Equal $ready.CurrentExperience 7052180 'pre-compensation EXP'
    Assert-Equal $ready.CurrentPetRevision 1004 'pre-compensation revision'
    Assert-Equal $ready.Compensation 13493595 'four-pool compensation'
    Assert-Equal $ready.TargetExperience 20545775 'target EXP'
    Assert-Equal $ready.ServerRunning $true `
        'disposable mode reports the running shared server'

    $applied = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    Assert-Equal $applied.status 'Applied' 'compensation apply result'
    Assert-Equal $applied.petLevel 120 'compensation preserves level'
    Assert-Equal $applied.currentExperience 20545775 `
        'compensation adds only four dropped pools'
    Assert-Equal $applied.currentPetRevision 1005 `
        'compensation increments pet revision'

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
    'sourceCount', (SELECT count(*)
        FROM public.pet_operation_audit WHERE id BETWEEN 631 AND 634
          AND operation = 'rebirth' AND outcome = 'committed'),
    'sourceUnchanged', 13493595 = (SELECT sum(
        (before_state->>'Experience')::bigint)
        FROM public.pet_operation_audit WHERE id BETWEEN 631 AND 634),
    'statsRemainLevel120', 6 = (SELECT count(*)
        FROM public.character_pet_stat_values WHERE pet_id = 1
          AND added_savvy = (base_growth_rate + growth_acceleration) * 120),
    'immutableTrigger', EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgrelid = 'public.command_audit'::regclass
          AND tgname = 'trg_command_audit_immutable'
          AND tgenabled <> 'D'))::text
FROM public.character_pets pet
LEFT JOIN public.command_audit audit
  ON audit.command_family = 'pet_rebirth_carry_compensation'
 AND audit.aggregate_type = 'pet' AND audit.aggregate_key = '1'
WHERE pet.id = 1
GROUP BY pet.level, pet.experience, pet.revision;
'@
    $raw = $sql | & docker exec -i $postgres psql -X -q -A -t `
        -v ON_ERROR_STOP=1 -U godswar -d $database 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Carry verification query failed:`n$(@($raw) -join "`n")"
    }
    $evidence = @($raw)[-1] | ConvertFrom-Json
    Assert-Equal $evidence.level 120 'stored level'
    Assert-Equal $evidence.experience 20545775 'stored EXP'
    Assert-Equal $evidence.revision 1005 'stored revision'
    Assert-Equal $evidence.auditCount 1 'one compensation audit'
    Assert-Equal $evidence.auditId $applied.compensationAuditId `
        'stored compensation audit identity'
    Assert-Equal $evidence.sourceCount 4 'four source audits remain'
    Assert-Equal $evidence.sourceUnchanged $true `
        'source rebirth evidence remains immutable'
    Assert-Equal $evidence.statsRemainLevel120 $true `
        'EXP-only compensation preserves stats'
    Assert-Equal $evidence.immutableTrigger $true `
        'compensation evidence is immutable'

    Write-Host (
        "Pet rebirth carry compensation checks passed: $assertions assertions.")
}
finally {
    if ($databaseCreated -and
        $database -match '^godswar_rebirth_carry_[a-f0-9]{10}$') {
        & docker exec $postgres dropdb -U godswar --force $database `
            2>$null | Out-Null
    }
    if ($dump -match
        '^/tmp/godswar_rebirth_carry_[a-f0-9]{10}\.dump$') {
        & docker exec $postgres rm -f -- $dump 2>$null | Out-Null
    }
}
