# Redis main-Compose activation

Status: staged local-alpha procedure. Stage 2 may follow either a completed
B20H window or an explicitly authorized invalidation, but activation must
start a new full Redis-aware B20H window.

## Scope and authority

This runbook moves the disposable Redis coordination dependency into the
main Docker Compose project named `reborn`. It does not move player value out
of PostgreSQL and does not claim that multi-worker gameplay is complete.

Redis remains its own private container by design. It is one shared
coordination authority that present and future map, dungeon, and battlefield
workers can reach; embedding one Redis inside each worker would split that
authority. "Main Compose" means one visible deployment lifecycle, not one
operating-system process.

PostgreSQL remains authoritative for accounts, characters, ownership fences,
inventory, equipment, currency, progression, rewards, pets, mounts, command
inbox/outbox records, and audits. Redis contains only reconstructable
tickets, admissions, worker/route registrations, and PostgreSQL-fenced player
presence leases.

The staged local topology is:

```text
Stage 1, during B20H
  reborn
    |- godswar-postgres           unchanged
    |- godswar-server             unchanged; Coordination.Provider=Local
    |- godswar-b20h-prometheus    unchanged
    `- godswar-main-redis-coordination private, unused, nonpersistent

Stage 2, after completion or authorized invalidation of the old B20H window
  reborn
    |- godswar-postgres
    |- godswar-main-redis-coordination private, nonpersistent coordination
    |- godswar-server             one combined Redis-coordinated worker
    |                             owning legacy maps 0 through 22
    `- godswar-b20h-prometheus    new Redis-aware 168-hour observation
```

All directly connected open-world maps remain on that one combined worker.
Do not split connected portal maps between processes until an authoritative
cross-worker handoff protocol exists.

## Safety rules

- If the current B20H evidence must remain valid, start only
  `redis-coordination`; do not alter `server`, `postgres`, or the observer.
- An early cutover requires explicit owner authorization. Export and retain
  the partial result as invalidated evidence, remove only the observer
  sidecar, activate Redis, and start a new full 168-hour window. Never count
  the partial Local-provider window toward the Redis-aware window.
- Never use `docker compose down`, `down -v`, `FLUSHDB`, `FLUSHALL`, or
  wildcard key deletion in this procedure.
- Never print, commit, paste into a command line, or include in logs the
  generated password or Redis connection string.
- Do not treat a missing Redis key as proof that a player, route, ticket, or
  admission is free.
- Do not run mixed `Local` and `Redis` authorities for the same live Tempest
  realm. Stage 2 requires a drain, server restart, and fresh player login.
- Redis loss must fail closed. Do not add an automatic per-request fallback
  to local in-memory coordination.

## Files and generated secrets

The Compose files are deliberately layered:

- `docker-compose.yml` owns PostgreSQL, the existing combined server, and
  the B20H observer;
- `docker-compose.redis.yml` adds the private main-project Redis dependency
  and the opt-in server coordination environment; and
- `deploy/local/redis-coordinated-worker.json` declares the combined
  worker's stable node and exact map `0..22` route ownership;
- `ops/redis/redis-coordination.local.conf` supplies the bounded local Redis
  policy.

Generate credentials into the ignored artifact directory:

```powershell
$redisArtifacts = '.\artifacts\redis-main-local'
$redisConfig = .\tools\NewRedisMainLocalConfiguration.ps1 `
  -OutputDirectory $redisArtifacts
```

The generator creates:

- `artifacts/redis-main-local/redis.acl`;
- `artifacts/redis-main-local/redis.password`; and
- `artifacts/redis-main-local/redis.connection-string`; and
- `artifacts/redis-main-local/redis.local.env`.

The ACL, password, and connection-string files are secret-bearing; all four
generated files must remain untracked. The nonsecret env file contains only
paths and safe Compose parameters. The ACL disables the default Redis
identity, uses application identity `godswar_runtime`, and limits it to
`~godswar:tempest-local:v1:*` with the reviewed command set. The generated
application connection string uses the private Compose endpoint
`redis-coordination:6379`. If the coordination environment is renamed,
regenerate an ACL with the identical prefix before starting the application;
the Redis key prefix and ACL scope must never disagree. Production must
replace this local file workflow with an approved secret manager or
read-only secret injection mechanism.

If the directory already exists, do not use `-Force` casually. `-Force`
rotates the credential and requires a controlled Redis/server restart plus
fresh authentication.

Define the common Compose arguments once per PowerShell session. The base
environment must be first and the generated Redis environment second. An
explicit Compose `--env-file` disables automatic `.env` loading; omitting the
first file can silently change the launcher login port and developer-command
setting.

```powershell
$baseEnv = (Resolve-Path .\.env).Path
$redisEnv = (Resolve-Path $redisConfig.EnvironmentFile).Path
$compose = @(
  '--project-name', 'reborn',
  '--env-file', $baseEnv,
  '--env-file', $redisEnv,
  '-f', '.\docker-compose.yml',
  '-f', '.\docker-compose.redis.yml'
)
```

Inspect and validate the rendered model:

```powershell
docker compose @compose config --quiet
.\tools\TestRedisMainComposeProfile.ps1 `
  -BaseEnvironmentFile $baseEnv `
  -EnvironmentFile $redisEnv
```

The validator proves the login/game host ports and developer-command setting
still equal the base env, the connection string is secret-file mounted, and
the Redis image equals the reviewed digest pin. It also binds all three
rendered Compose secret sources to the exact absolute paths in the Redis env
file and runs bounded conflict probes. Duplicate env keys, differing ambient
values, and ambient Compose substitutions absent from both env files are
rejected; only the controlled source-commit and B20H-evidence variables are
exempt. Rendered runtime/PostgreSQL continuity is mandatory, and errors never
echo values. Create and review `.env` from `.env.example` if it is absent.

## Historical Stage 1: private Redis beside the Local observation

Stage 1 proves only that Redis belongs to the main `reborn` Compose project
and can start securely. It intentionally leaves the game server on its
current local coordination provider.

Record the current server identity and B20H status first. Use the observation
tools from the commit that created an older v1 Local-provider campaign; the
current tools intentionally accept only the Redis-aware v2 active schema.

```powershell
$serverBefore = docker inspect godswar-server `
  --format '{{.Id}} {{.State.StartedAt}}'
# Run GetB20HDockerObservation.ps1 from the active campaign's pinned commit.
```

Start exactly the new dependency:

```powershell
docker compose @compose --profile redis-coordinated `
  up -d --wait redis-coordination
docker compose @compose --profile redis-coordinated `
  ps redis-coordination
```

Verify authenticated Redis health without echoing the password:

```powershell
docker compose @compose --profile redis-coordinated `
  exec -T redis-coordination sh -ec `
  'REDISCLI_AUTH="$(cat /run/secrets/redis_password)" redis-cli --user godswar_runtime ping'
```

The only acceptable response is `PONG`. Confirm that Redis is private and
that the observed server was not recreated:

```powershell
$redisContainer = docker compose @compose --profile redis-coordinated `
  ps -q redis-coordination
docker inspect $redisContainer --format '{{json .NetworkSettings.Ports}}'
$serverAfter = docker inspect godswar-server `
  --format '{{.Id}} {{.State.StartedAt}}'
if ($serverAfter -ne $serverBefore) {
  throw 'Stage 1 changed the B20H server process.'
}
# Re-run the status tool from the active campaign's pinned commit.
```

The Redis inspection may show the private container port but must show no
published host binding. The B20H status must
remain healthy with the same server process start time and observation
window. Do not run Redis outage drills during B20H.

## Stage 2: activate Redis and restart B20H

### Preconditions

Do not begin Stage 2 until:

1. either the current B20H window completed and was exported, or the owner
   explicitly authorized invalidating it and restarting the full window;
2. a final or partial telemetry export was attempted and its outcome retained;
3. every client is closed and new admissions are stopped;
4. PostgreSQL readiness, checkpoint queues, outbox, and reconciliation are
   healthy;
5. no active PostgreSQL player-ownership checkpoint remains; and
6. the combined-worker configuration declares stable, unique routes for
   every legacy map ID from `0` through `22`.

The initial node ID must remain stable across ordinary restarts:
`tempest-openworld-01`. Each route needs a stable nonzero
`WorldInstanceId`; do not regenerate route GUIDs on every deployment. A new
random worker boot ID is expected for each process incarnation.

Before activating Redis, verify the rendered worker configuration contains
exactly one route for each map ID `0..22`, no duplicate realm/map pair, no
duplicate world-instance ID, and no route assigned to another node. The
combined server may still run all dynamic ECS runtimes in one process, but
only declared routes participate in Redis route and player-lease proofs.

### Authorized invalidation, when the old window has not completed

Skip this subsection only when the prior pointer was already retired through
the approved completed-window path. While the old observer is still running,
inspect it and attempt a final partial export with the observation tools from
the commit that created that campaign. The current status/export tools
intentionally reject old v1 Local campaigns rather than treating them as
Redis-aware evidence.

```powershell
# Run these two commands before moving away from the campaign's pinned tools.
.\tools\GetB20HDockerObservation.ps1
try {
  .\tools\ExportB20HDockerObservationTelemetry.ps1
} catch {
  Write-Warning 'Partial export unavailable; record this failed attempt.'
}

# After updating to the Redis-aware v2 tooling:
.\tools\InvalidateB20HDockerObservation.ps1 `
  -Reason topology-correction-local-to-redis `
  -AllowMutation
```

The export may report `in_progress` or fail if the old observer is already
unavailable; record that outcome, but do not mislabel it as qualifying
evidence. The invalidation command is evidence-only: it writes an immutable
authorization receipt, atomically retires the active pointer, and preserves
the TSDB and exports. It does not change Docker.

### Drain and activation

Record durable baselines, drain the old local authority, and close the legacy
client. Never remove PostgreSQL or its volume.

Activate only from an exact clean commit and preserve the revision in the
container metadata:

```powershell
if (git status --porcelain) {
  throw 'Commit or intentionally remove all changes before activation.'
}
$env:GODSWAR_SOURCE_COMMIT = (git rev-parse HEAD).Trim()
.\tools\TestRedisMainComposeProfile.ps1 `
  -BaseEnvironmentFile $baseEnv `
  -EnvironmentFile $redisEnv `
  -RequireLivePostgres
```

Start the new observation with a unique change ID. This is the single
controlled topology restart: the tool removes only the old observer,
recreates disposable Redis, rebuilds/recreates the server with `--no-deps`,
then creates the observer and records Redis-aware T0 evidence.

```powershell
.\tools\StartB20HDockerObservation.ps1 `
  -ChangeId 'alpha-b20h-redis-20260801' `
  -ApprovedByRole 'project-owner' `
  -BaseEnvironmentFile $baseEnv `
  -RedisEnvironmentFile $redisEnv `
  -AllowMutation
.\tools\GetB20HDockerObservation.ps1
```

The start must fail rather than fall back to base-only/Local coordination.
Its schema-v2 record must bind the server, private Redis container, both env
files, both Compose files, provider `Redis`, environment `tempest-local`, and
all 23 routes. Local plaintext is allowed only on this private
`LocalDevelopment` network; production must use TLS.

### Readiness and functional checks

The server is ready only after PostgreSQL, persistence workers, simulation
loops, listeners, and Redis worker coordination are all ready. Check the
container health and the internal readiness probe:

```powershell
docker compose @compose --profile redis-coordinated `
  ps redis-coordination
docker inspect godswar-server --format '{{.State.Health.Status}}'
docker exec godswar-server /app/secure-healthcheck.sh
docker compose @compose --profile redis-coordinated `
  logs --tail 200 server redis-coordination
```

Expected results:

- both health states are `healthy`;
- the management readiness probe exits zero;
- no `redis_coordination_not_ready`, route conflict, ACL denial, timeout,
  unavailable, overload, or circuit-open loop appears;
- the worker registers all 23 routes before publishing available;
- a login acquires a player lease carrying the current PostgreSQL ownership
  UUID/generation; and
- portal travel among maps `0..22`, reconnect, inventory, equipment,
  progression, pet, mount, and checkpoint operations remain authoritative.

The new B20H gate requires these exact low-cardinality series at T0 and
throughout the window:

- `godswar_server_operational_coordination{operational_state="ready"} = 1`;
- `godswar_server_operational_coordination{operational_state="routes"} = 23`.

Also observe:

- `godswar.coordination.operations`;
- `godswar.coordination.duration`; and
- `godswar.coordination.logical_results`.

Do not use account, character, endpoint, route, node, lease, ticket, or Redis
error text as metric labels.

## Local outage and restart validation

Do not run these drills during the new qualifying B20H window. Run them only
after that window is exported/retired, or in a separate explicitly
non-qualifying test campaign. Keep PostgreSQL running throughout.

### Dependency unavailable

With a test character online, stop only Redis:

```powershell
docker compose @compose --profile redis-coordinated `
  stop redis-coordination
```

Expected behavior:

- coordination readiness becomes false;
- new distributed login, route, ticket, admission, and player-lease work
  fails closed within configured deadlines;
- ECS/map ticks do not wait on Redis I/O;
- existing coordination proof is usable only until its bounded local proof
  expires;
- unproven sessions stop valuable commands and are drained or disconnected;
  and
- PostgreSQL player value and ownership generations remain intact.

Start Redis again:

```powershell
docker compose @compose --profile redis-coordinated `
  up -d --wait redis-coordination
docker exec godswar-server /app/secure-healthcheck.sh
```

Because local Redis is nonpersistent, treat tickets, admissions, routes, and
presence as lost. The live server must re-register its exact node/routes and
reacquire player leases through PostgreSQL fencing. Require fresh login for
lost tickets or admissions.

### Empty-keyspace restart

Repeat with a Redis restart:

```powershell
docker compose @compose --profile redis-coordinated `
  restart redis-coordination
docker compose @compose --profile redis-coordinated `
  up -d --wait redis-coordination
docker exec godswar-server /app/secure-healthcheck.sh
```

Verify all of the following:

- unauthenticated Redis access remains denied;
- the application identity authenticates after restart;
- the server becomes not-ready before it reconstructs coordination state;
- the same still-running worker boot ID re-registers after Redis state loss;
- stale player owners cannot commit against the PostgreSQL fence;
- all 23 routes return without conflict;
- fresh login succeeds; and
- durable PostgreSQL counts, ownership generations, inventory, currency,
  equipment, progression, pets, and mounts are unchanged.

Record Redis latency, timeout/unavailable/circuit counters, readiness
recovery time, player impact, and reconciliation results. This proves safe
state loss and reconstruction, not ticket continuity.

## Rollback to local coordination

Rollback is a coordinated process restart, never an automatic provider
fallback:

1. stop new login, reconnect, portal transfer, and placement;
2. drain or disconnect sessions and let bounded operations finish;
3. confirm PostgreSQL fences, checkpoints, outbox, and reconciliation are
   healthy;
4. recreate only one combined server using the base Compose file, which
   leaves `Coordination.Provider=Local`;
5. require fresh login; and
6. stop Redis only after the local authority is verified.

```powershell
docker compose --project-name reborn `
  -f .\docker-compose.yml `
  --profile legacy-raw up -d --wait `
  --no-deps --build --force-recreate server
docker exec godswar-server /app/secure-healthcheck.sh
docker compose @compose --profile redis-coordinated `
  stop redis-coordination
```

Do not remove the PostgreSQL volume. Do not delete Redis keys as the first
rollback action. Retain the Redis container and finite evidence until
PostgreSQL reconciliation and fresh-login validation pass.

## Production provider requirements

The local container is not production HA. Apply the provider criteria and
latency budgets in `docs/adr/0005-b17-redis-coordination-activation.md` and
`docs/data-architecture-roadmap/16-deployment-operations.md`: private TLS,
secret-manager rotation, scoped ACL, `noeviction`, capacity telemetry, and a
reviewed failover epoch or zero-data-loss failover. Redis Cluster is
incompatible with the current multi-key Lua workflows. Redis is internal
coordination, not a public DDoS edge; workers and stores remain private while
an upstream L3/L4 provider protects the published TCP/UDP edge.

## Explicitly unimplemented capabilities

This step does not implement cross-worker handoff/reconnect, map splitting,
dynamic dungeon or battlefield placement/recovery, cross-server Pindus,
automatic worker replacement, gateway UDP routing/NAT rebinding, or managed
Redis HA. Those need separate milestones. Until handoff exists, one combined
worker owns maps `0..22`; dungeon and battlefield runtime remains local to
that process.
