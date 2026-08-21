# Isolated local development stack

Status: implemented and locally verified on 2026-08-08.

## Purpose

The `reborn-dev` Docker project allows server code to be rebuilt and tested
while the pinned `reborn` B20H observation continues uninterrupted. The two
deployments do not share containers, host endpoints, Docker networks, Redis
keys, or PostgreSQL volumes.

The B20H run remains a rehearsal for its pinned commit. A new final 168-hour
window is still required after the release candidate is committed, especially
when development changes persistence, networking, inventory, progression, or
other monitored paths.

## Isolation boundary

| Resource | B20H/main | Development |
|---|---|---|
| Compose project | `reborn` | `reborn-dev` |
| Login endpoint | `127.1.1.110:5998` | `127.1.1.111:5998` |
| Game endpoint | `127.1.1.110:7000` | `127.1.1.111:7000` |
| PostgreSQL host endpoint | `127.0.0.1:5432` | `127.0.0.1:55432` |
| PostgreSQL volume | `reborn_godswar-postgres-data` | `godswar-dev-postgres-data` |
| Redis environment | `tempest-local` | `tempest-dev` |
| Docker network | `reborn_default` | `reborn_dev_runtime` |
| Server image | pinned `reborn-server` | `reborn-server:dev` |
| Server container | `godswar-server` | `godswar-dev-tempest-openworld-01` |

`docker-compose.dev.yml` is intentionally standalone. Do not layer it onto
`docker-compose.yml`, invoke it with project name `reborn`, attach its
long-running containers to `reborn_default`, or mount the authoritative main
PostgreSQL volume.

## Initial setup and ordinary rebuilds

The first run generates ignored random local credentials, starts isolated
PostgreSQL and Redis, takes a transactionally consistent logical dump of the
main database, restores and verifies the clone, builds `reborn-server:dev`,
and starts the development server:

```powershell
./tools/StartDevelopmentStack.ps1
```

PostgreSQL and Redis credentials are stored only in owner-restricted files
beneath `artifacts/development-stack`. The server receives absolute
`*_CONNECTION_STRING_FILE` paths; plaintext connection strings are not placed
in its Docker environment. `StartDevelopmentStack.ps1` automatically upgrades
an older generated configuration that still contains the PostgreSQL password.
The same non-rotating upgrade can be invoked explicitly:

```powershell
./tools/NewDevelopmentStackConfiguration.ps1 -UpgradeExisting
```

Running the same command after source edits rebuilds/recreates only
`godswar-dev-tempest-openworld-01`. Existing development database changes are preserved.
The command captures the monitored container identities before work and fails
if the B20H server, Redis, Prometheus, or authoritative PostgreSQL identity
changes. A gameplay/acceptance alert in the pinned B20H run remains visible in
the returned status but does not block isolated development when every
continuity, identity, topology, and health guard still passes.

Use the existing image without rebuilding only when that is intentional:

```powershell
./tools/StartDevelopmentStack.ps1 -SkipBuild
```

## Local multi-realm proof of concept

Start Tempest and Dwargon as two realm workers with one shared account
authority, PostgreSQL database, Redis coordination service, image, and Docker
network:

```powershell
./tools/StartDevelopmentStack.ps1 -MultiRealm
```

The workflow starts Tempest first so it can apply the forward multi-realm
migrations, then starts and health-checks Dwargon from the already-built
image. Only after both workers are healthy and confirmed to use that same
image does it enable their local catalog endpoints in one PostgreSQL
transaction. It never replaces the development database or uses
`docker compose down`.

| Realm | Container | Login | Game |
|---|---|---|---|
| Tempest (`1`) | `godswar-dev-tempest-openworld-01` | `127.1.1.111:5998` | `127.1.1.111:7000` |
| Dwargon (`2`) | `godswar-dev-dwargon-openworld-01` | `127.1.1.112:5998` | `127.1.1.112:7000` |

`public.server` is the parent realm catalog. Accounts stay global.
`public.account_realm` owns each account's per-realm lifecycle version and
slot limit, while `character_base.server_id` assigns each character to its
home realm. Active character slots and mutable world-boss control are scoped
by realm; globally shared content and globally unique character names remain
shared intentionally.

The future management workflow should follow the same order: create or update
a disabled catalog row, provision and health-check its realm worker through a
trusted deployment controller, then enable the row. The management website
must request that workflow through the controller; it must not run Docker or
cloud-orchestrator commands itself.

## Development database refresh

The initial development database is a point-in-time logical clone. It is
expected to diverge from main after development gameplay testing. Refreshing
it destroys and replaces only the isolated development database:

```powershell
./tools/StartDevelopmentStack.ps1 -RefreshDatabaseFromMain
```

The clone uses PostgreSQL 17 helpers and the reviewed flags:

```text
pg_dump --format=custom --compress=9 --serializable-deferrable
        --no-owner --no-privileges
pg_restore --exit-on-error --single-transaction
           --no-owner --no-privileges
```

The workflow refuses a source snapshot with active event or consumer-position
outbox leases, or an event type outside the finite reviewed allowlist. It
validates and completes the source dump before stopping the development
server. Restore and verification happen in a uniquely named staging database;
only a fully verified staging database is promoted, with automatic rollback to
the previous dev database if promotion fails. The restored snapshot is checked
again for leases and unreviewed events. It also compares ordered migration
IDs/checksums and records account, character, and character-item counts. If
the live source changes during the dump, that drift is recorded instead of
mistaking a valid point-in-time snapshot for corruption. The temporary dump
and credential files must be deleted successfully before an ignored checksum
receipt is written beneath:

```text
artifacts/development-stack/clone-receipts/
```

Never use physical Docker-volume copying, `pg_dumpall`, or a restore aimed at
port `5432`/`godswar-postgres`.

## Client selection

The actively patched client remains the development client because repository
patch tools default to that location:

```text
C:\Godswar Origin\Launch.exe
```

Its ignored `config.ini` targets `127.1.1.111:5998`. A runtime snapshot was
created for exercising the pinned B20H server:

```text
C:\Godswar Origin B20H\Launch.exe
```

That snapshot targets `127.1.1.110:5998`. The endpoint switcher makes verified
backups of `config.ini` and `Launch.exe`, then updates both the configuration
and the launcher's three fixed-length region endpoints. Launcher mutation is
hash- and offset-gated, reversible, and prevents `Launch.exe` from silently
rewriting a development target back to the B20H address:

```powershell
./tools/SetDevelopmentClientTarget.ps1 `
  -Target Development `
  -ClientRoot 'C:\Godswar Origin' `
  -AllowMutation

./tools/SetDevelopmentClientTarget.ps1 `
  -Target B20H `
  -ClientRoot 'C:\Godswar Origin B20H' `
  -AllowMutation
```

Inspect either client without changing it:

```powershell
./tools/SetDevelopmentClientTarget.ps1 -ClientRoot 'C:\Godswar Origin'
```

## Verification and operation

Validate the rendered configuration only:

```powershell
./tools/TestDevelopmentStackIsolation.ps1
```

Validate all live containers, volume ownership, and endpoints:

```powershell
./tools/TestDevelopmentStackIsolation.ps1 -RequireLive
./tools/GetB20HDockerObservation.ps1
```

The isolation test also verifies that the rendered and live server
environments contain only the PostgreSQL file-secret path, never the password
or direct connection string.

Inspect the development stack:

```powershell
docker ps --filter 'label=com.docker.compose.project=reborn-dev'
docker logs --tail 200 godswar-dev-tempest-openworld-01
```

Stop only development services when required:

```powershell
docker compose `
  --project-name reborn-dev `
  --env-file ./artifacts/development-stack/development.local.env `
  --file ./docker-compose.dev.yml `
  stop
```

Do not use `down --volumes` during normal operation. It is unnecessary and
would delete the development clone. It cannot target main when the reviewed
command and project name are used, but avoiding volume deletion is safer.

## B20H constraints that remain

Until the active observation reaches its target end:

- do not restart or recreate `godswar-server`;
- do not restart or recreate `godswar-main-redis-coordination`;
- do not restart or recreate `godswar-b20h-prometheus`;
- do not stop Docker Desktop or reboot/sleep the host; and
- do not deploy development images into the `reborn` project.

Normal gameplay workload against the pinned B20H client should still cover
authentication, inventory/economy, progression, pets/mounts, map transfers,
and at least two scheduled world-boss cycles.
