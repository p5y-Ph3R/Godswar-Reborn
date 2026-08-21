[CmdletBinding()]
param(
    [string]$ConfigurationDirectory,
    [switch]$AllowMutation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DevelopmentStack.Common.psm1') -Force

if (-not $AllowMutation) {
    throw 'Pass -AllowMutation to activate the local Tempest and Dwargon catalog rows.'
}

$postgres = Assert-DevelopmentContainer 'godswar-dev-postgres' 'postgres'
if ($postgres.State.Status -cne 'running' -or
    $postgres.State.Health.Status -cne 'healthy') {
    throw 'The isolated development PostgreSQL container must be healthy.'
}

$workers = @(
    @{
        Name = 'godswar-dev-tempest-openworld-01'
        Service = 'server'
        Realm = 'GODSWAR_WORLD_INSTANCE_REALM_ID=1'
    }
    @{
        Name = 'godswar-dev-dwargon-openworld-01'
        Service = 'server-dwargon'
        Realm = 'GODSWAR_WORLD_INSTANCE_REALM_ID=2'
    }
)
$workerImage = $null
foreach ($worker in $workers) {
    $container = Assert-DevelopmentContainer `
        $worker.Name $worker.Service
    if ($container.State.Status -cne 'running' -or
        $container.State.Health.Status -cne 'healthy' -or
        @($container.Config.Env) -cnotcontains $worker.Realm) {
        throw "The $($worker.Name) realm worker must be healthy and correctly scoped."
    }
    if ($null -eq $workerImage) {
        $workerImage = [string]$container.Image
    }
    elseif ([string]$container.Image -cne $workerImage) {
        throw 'Tempest and Dwargon must run the same reviewed image.'
    }
}

$sql = @'
BEGIN;

DO $local_multi_realm_preflight$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM public.schema_migrations
        WHERE migration_id =
            '20260820_094_multi_realm_character_authority'
    ) OR NOT EXISTS (
        SELECT 1
        FROM public.schema_migrations
        WHERE migration_id =
            '20260820_095_realm_scoped_world_boss_control'
    ) THEN
        RAISE EXCEPTION
            'The multi-realm schema migrations have not completed.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM public.server
        WHERE id = 1
          AND name = 'Tempest'
          AND identifier = 'KAL3jcIzqGgKvOf1dbYZKC8cS'
    ) OR NOT EXISTS (
        SELECT 1
        FROM public.server
        WHERE id = 2
          AND name = 'Dwargon'
          AND identifier = 'DWG3jcIzqGgKvOf1dbYZKC8cS'
    ) THEN
        RAISE EXCEPTION
            'The local Tempest/Dwargon realm identities do not match the reviewed catalog.';
    END IF;
END
$local_multi_realm_preflight$;

UPDATE public.server
SET ip_address = '127.1.1.111',
    game_port = 7000,
    server_limit = 250,
    enabled = true,
    recommended = true,
    display_order = 1
WHERE id = 1;

UPDATE public.server
SET ip_address = '127.1.1.112',
    game_port = 7000,
    server_limit = 250,
    enabled = true,
    recommended = false,
    display_order = 2
WHERE id = 2;

COMMIT;

SELECT json_build_object(
    'status', 'enabled_local_multi_realm',
    'realms', json_agg(json_build_object(
        'id', id,
        'name', name,
        'host', ip_address,
        'gamePort', game_port,
        'enabled', enabled,
        'recommended', recommended,
        'displayOrder', display_order
    ) ORDER BY display_order, id)
)::text
FROM public.server
WHERE id IN (1, 2);
'@

$raw = & docker exec godswar-dev-postgres psql `
    --no-psqlrc `
    --quiet `
    --username godswar `
    --dbname godswar `
    --tuples-only `
    --no-align `
    --set ON_ERROR_STOP=1 `
    --command $sql
if ($LASTEXITCODE -ne 0) {
    throw 'Local multi-realm catalog activation failed.'
}

$json = @($raw | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_)
})[-1]
$result = $json | ConvertFrom-Json
if ($result.status -cne 'enabled_local_multi_realm' -or
    @($result.realms).Count -ne 2) {
    throw 'Local multi-realm activation returned an invalid catalog snapshot.'
}

$result
