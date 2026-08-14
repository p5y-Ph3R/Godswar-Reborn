[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$postgres = 'godswar-dev-postgres'
$token = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$database = "godswar_pet_rebirth_attempts_$token"
$dump = "/tmp/$database.dump"
$tool = Join-Path $PSScriptRoot `
    'GrantLocalDevelopmentPetRebirthAttempts.ps1'
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
    Assert-Equal $ready.Status 'Ready' 'pre-grant status'
    Assert-Equal $ready.PetId 1 'target pet'
    Assert-Equal $ready.PetLevel 118 'source level'
    Assert-Equal $ready.PetExperience 1239780 'source experience'
    Assert-Equal $ready.CompletedRebirths 1 'completed rebirths'
    Assert-Equal $ready.RebirthsRemaining 0 'source attempts'
    Assert-Equal $ready.TargetRemaining 10 'target attempts'
    Assert-Equal $ready.CurrentPetRevision 468 'source revision'
    Assert-Equal $ready.MaximumRebirthCount 100 'content maximum'

    $applied = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    Assert-Equal $applied.status 'Applied' 'apply result'
    Assert-Equal $applied.previousRemaining 0 'previous attempts'
    Assert-Equal $applied.currentRemaining 10 'granted attempts'
    Assert-Equal $applied.completedRebirths 1 'preserved completed rebirths'
    Assert-Equal $applied.totalRebirthAllowance 11 'wire total allowance'
    Assert-Equal $applied.previousPetRevision 468 'previous revision'
    Assert-Equal $applied.currentPetRevision 469 'advanced revision'

    $retried = & $tool -Mode Apply -Database $database `
        -DisposableTest -Confirm:$false
    Assert-Equal $retried.Status 'Applied' 'idempotent retry status'
    Assert-Equal $retried.AuditId $applied.auditId `
        'idempotent audit identity'
    Assert-Equal $retried.RebirthsRemaining 10 `
        'idempotent attempts'

    $sql = @'
SELECT jsonb_build_object(
    'petCount', count(*) FILTER (
        WHERE pet.id = 1 AND pet.user_id = 2 AND pet.name = 'Jolo'),
    'level', max(pet.level) FILTER (WHERE pet.id = 1),
    'experience', max(pet.experience) FILTER (WHERE pet.id = 1),
    'completed', max(pet.completed_rebirths) FILTER (WHERE pet.id = 1),
    'remaining', max(pet.rebirths_remaining) FILTER (WHERE pet.id = 1),
    'revision', max(pet.revision) FILTER (WHERE pet.id = 1),
    'otherPetChanges', count(*) FILTER (
        WHERE pet.id <> 1 AND pet.updated_at >= fixture.created_at),
    'auditCount', (SELECT count(*) FROM public.command_audit
        WHERE command_family = 'pet_rebirth_attempt_fixture'
          AND aggregate_type = 'pet' AND aggregate_key = '1'),
    'auditId', (SELECT min(id) FROM public.command_audit
        WHERE command_family = 'pet_rebirth_attempt_fixture'
          AND aggregate_type = 'pet' AND aggregate_key = '1'),
    'auditPermanent', (SELECT bool_and(retention_policy = 'permanent')
        FROM public.command_audit
        WHERE command_family = 'pet_rebirth_attempt_fixture'
          AND aggregate_type = 'pet' AND aggregate_key = '1'),
    'inventoryRevision', (SELECT inventory_revision
        FROM public.character_base WHERE id = 2),
    'rebirthSpiritStack', (SELECT sum(stack)
        FROM public.character_items
        WHERE user_id = 2 AND item_location = 1 AND prop_id = 10104),
    'immutableTrigger', EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgrelid = 'public.command_audit'::regclass
          AND tgname = 'trg_command_audit_immutable'
          AND tgenabled <> 'D'))::text
FROM public.character_pets pet
CROSS JOIN LATERAL (
    SELECT created_at FROM public.command_audit
    WHERE command_family = 'pet_rebirth_attempt_fixture'
      AND aggregate_type = 'pet' AND aggregate_key = '1'
) fixture;
'@
    $raw = $sql | & docker exec -i $postgres psql -X -q -A -t `
        -v ON_ERROR_STOP=1 -U godswar -d $database 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Grant verification query failed:`n$(@($raw) -join "`n")"
    }
    $evidence = @($raw)[-1] | ConvertFrom-Json
    Assert-Equal $evidence.petCount 1 'single exact target pet'
    Assert-Equal $evidence.level 118 'level preserved'
    Assert-Equal $evidence.experience 1239780 'experience preserved'
    Assert-Equal $evidence.completed 1 'completed rebirths preserved'
    Assert-Equal $evidence.remaining 10 'remaining attempts stored'
    Assert-Equal $evidence.revision 469 'stored revision'
    Assert-Equal $evidence.otherPetChanges 0 'other pets untouched'
    Assert-Equal $evidence.auditCount 1 'one permanent audit'
    Assert-Equal $evidence.auditId $applied.auditId 'stored audit identity'
    Assert-Equal $evidence.auditPermanent $true 'permanent evidence'
    Assert-Equal $evidence.inventoryRevision 502 `
        'inventory revision untouched'
    Assert-Equal $evidence.rebirthSpiritStack 94 `
        'rebirth materials untouched'
    Assert-Equal $evidence.immutableTrigger $true `
        'audit immutability remains enabled'

    Write-Host (
        "Pet rebirth-attempt grant checks passed: $assertions assertions.")
}
finally {
    if ($database -match
        '^godswar_pet_rebirth_attempts_[a-f0-9]{10}$') {
        & docker exec $postgres dropdb -U godswar --force $database `
            2>$null | Out-Null
    }
    if ($dump -match
        '^/tmp/godswar_pet_rebirth_attempts_[a-f0-9]{10}\.dump$') {
        & docker exec $postgres rm -f -- $dump 2>$null | Out-Null
    }
}
