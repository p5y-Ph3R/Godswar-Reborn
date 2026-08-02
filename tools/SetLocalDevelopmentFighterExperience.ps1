[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "Medium")]
param(
    [ValidateRange(1, [int]::MaxValue)]
    [int]$AccountId = 13,

    [ValidatePattern('^[A-Za-z0-9_]{1,32}$')]
    [string]$CharacterName = "test2",

    [ValidateScript({ $_ -ge 0 -and $_ -le [uint32]::MaxValue })]
    [long]$Experience = 4000000000,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$PostgresContainer = "godswar-postgres",

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$ServerContainer = "godswar-server",

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$Database = "godswar",

    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]*$')]
    [string]$DatabaseUser = "godswar"
)

# Offline LocalDevelopment fixture only. Values above Int32.MaxValue exercise
# the stock client's mixed signed/unsigned four-byte EXP handling and therefore
# require a sealed fighter until that client experiment is complete.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker is required but was not found on PATH."
}

function Get-ContainerState {
    param([Parameter(Mandatory = $true)][string]$Name)

    $output = & docker container inspect `
        --format '{{json .State}}' $Name 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect Docker container '$Name': $output"
    }

    try {
        return (($output | ForEach-Object { $_.ToString() }) -join "`n") |
            ConvertFrom-Json
    } catch {
        throw "Docker returned invalid state for container '$Name'."
    }
}

$serverState = Get-ContainerState -Name $ServerContainer
if ($serverState.Running) {
    throw (
        "Refusing to change fighter EXP while server container " +
        "'$ServerContainer' is running. Stop it cleanly first."
    )
}
$serverEnvironment = & docker container inspect `
    --format '{{range .Config.Env}}{{println .}}{{end}}' `
    $ServerContainer 2>&1
if ($LASTEXITCODE -ne 0 -or
    -not ($serverEnvironment -contains
        "GODSWAR_RUNTIME_PROFILE=LocalDevelopment")) {
    throw (
        "Server container '$ServerContainer' is not an explicit " +
        "GODSWAR_RUNTIME_PROFILE=LocalDevelopment environment."
    )
}

$postgresState = Get-ContainerState -Name $PostgresContainer
if (!$postgresState.Running) {
    throw "PostgreSQL container '$PostgresContainer' is not running."
}
$health = $postgresState.PSObject.Properties["Health"]
if ($null -ne $health -and
    $null -ne $health.Value -and
    $health.Value.Status -ne "healthy") {
    throw (
        "PostgreSQL container '$PostgresContainer' is not healthy " +
        "(state: $($health.Value.Status))."
    )
}

$safeName = $CharacterName.Replace("'", "''")
$operationBytes = [Guid]::NewGuid().ToByteArray()
$operationHex = -join ($operationBytes | ForEach-Object {
    $_.ToString("x2")
})
$requestText = "$AccountId|$CharacterName|$Experience"
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $requestBytes = [Text.Encoding]::UTF8.GetBytes($requestText)
    $requestHash = $sha256.ComputeHash($requestBytes)
} finally {
    $sha256.Dispose()
}
$requestHashHex = -join ($requestHash | ForEach-Object {
    $_.ToString("x2")
})

$target = "account $AccountId / character '$CharacterName'"
if (!$PSCmdlet.ShouldProcess($target, "Set fighter EXP to $Experience")) {
    return
}

$sql = @"
BEGIN ISOLATION LEVEL SERIALIZABLE;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

CREATE TEMP TABLE fighter_exp_fixture_result (
    account_id integer NOT NULL,
    character_id integer NOT NULL,
    character_name text NOT NULL,
    old_experience bigint NOT NULL,
    new_experience bigint NOT NULL,
    changed boolean NOT NULL,
    audit_id bigint NOT NULL
);

DO `$fixture`$
DECLARE
    v_character_id integer;
    v_old_experience bigint;
    v_level integer;
    v_sealed boolean;
    v_checkpoint_owner uuid;
    v_changed boolean;
    v_row_count integer;
    v_audit_id bigint;
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'character_base'
          AND column_name = 'fighter_job_exp'
          AND data_type = 'bigint'
    ) THEN
        RAISE EXCEPTION
            'fighter_job_exp is not bigint; apply the unsigned EXP migration first';
    END IF;

    SELECT character_row.id,
           character_row.fighter_job_exp,
           character_row.fighter_job_lv,
           character_row.fighter_level_sealed,
           character_row.checkpoint_owner_id
    INTO v_character_id, v_old_experience, v_level, v_sealed,
         v_checkpoint_owner
    FROM public.character_base character_row
    WHERE character_row.account_id = $AccountId
      AND character_row.name = '$safeName'
      AND character_row.lifecycle_state = 'active'
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION
            'Active character % on account % does not exist',
            '$safeName', $AccountId;
    END IF;
    IF v_checkpoint_owner IS NOT NULL THEN
        RAISE EXCEPTION
            'Character % has checkpoint owner %; cleanly release it first',
            v_character_id, v_checkpoint_owner;
    END IF;
    IF $Experience > 2147483647 AND NOT v_sealed THEN
        RAISE EXCEPTION
            'Character % must be fighter-level sealed for experimental EXP above Int32.MaxValue',
            v_character_id;
    END IF;

    UPDATE public.character_base character_row
    SET fighter_job_exp = $Experience
    WHERE character_row.id = v_character_id
      AND character_row.fighter_job_exp IS DISTINCT FROM $Experience;
    GET DIAGNOSTICS v_row_count = ROW_COUNT;
    v_changed := v_row_count = 1;

    INSERT INTO public.command_audit (
        principal_type,
        principal_key,
        aggregate_type,
        aggregate_key,
        command_family,
        operation_id,
        request_hash,
        outcome_code,
        detail_payload,
        retention_policy
    ) VALUES (
        'developer',
        '$AccountId',
        'character',
        v_character_id::text,
        'fighter_experience_fixture',
        decode('$operationHex', 'hex'),
        decode('$requestHashHex', 'hex'),
        CASE WHEN v_changed THEN 'updated' ELSE 'unchanged' END,
        jsonb_build_object(
            'characterName', '$safeName',
            'fighterLevel', v_level,
            'fighterLevelSealed', v_sealed,
            'previousExperience', v_old_experience,
            'currentExperience', $Experience,
            'changed', v_changed,
            'source', 'offline_localdevelopment_fixture'
        ),
        'permanent'
    ) RETURNING id INTO v_audit_id;

    INSERT INTO fighter_exp_fixture_result
    VALUES (
        $AccountId, v_character_id, '$safeName', v_old_experience,
        $Experience, v_changed, v_audit_id);
END
`$fixture`$;

COMMIT;

SELECT 'FIGHTER_EXP_FIXTURE_RESULT|' || jsonb_build_object(
    'accountId', account_id,
    'characterId', character_id,
    'characterName', character_name,
    'previousExperience', old_experience,
    'currentExperience', new_experience,
    'changed', changed,
    'auditId', audit_id
)::text
FROM fighter_exp_fixture_result;
"@

$output = $sql | & docker exec -i $PostgresContainer `
    psql -X -q -A -t -v ON_ERROR_STOP=1 `
    -U $DatabaseUser -d $Database 2>&1
$exitCode = $LASTEXITCODE
$lines = @($output | ForEach-Object { $_.ToString() })
if ($exitCode -ne 0) {
    throw (
        "Fighter EXP fixture transaction failed and was rolled back:`n" +
        ($lines -join "`n")
    )
}

$result = $lines |
    Where-Object { $_.StartsWith("FIGHTER_EXP_FIXTURE_RESULT|") } |
    Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($result)) {
    throw "The transaction committed but returned no verification receipt."
}
$result.Substring("FIGHTER_EXP_FIXTURE_RESULT|".Length) |
    ConvertFrom-Json
