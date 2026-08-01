# Redis main-Compose activation

Status: staged local-alpha procedure; Stage 1 is safe during B20H, while
Stage 2 must wait until the active B20H observation is complete

## Scope and authority

This runbook moves the disposable Redis coordination dependency into the
main Docker Compose project named `reborn`. It does not move player value out
of PostgreSQL and does not claim that multi-worker gameplay is complete.

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

Stage 2, after B20H
  reborn
    |- godswar-postgres
    |- godswar-main-redis-coordination private, nonpersistent coordination
    `- godswar-server             one combined Redis-coordinated worker
                                  owning legacy maps 0 through 22
```

All directly connected open-world maps remain on that one combined worker.
Do not split connected portal maps between processes until an authoritative
cross-worker handoff protocol exists.

## Safety rules

- While B20H is active, start only `redis-coordination`. Do not recreate,
  restart, rebuild, or change the environment of `server`, `postgres`, or
  `b20h-prometheus`.
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

Define the common Compose arguments once per PowerShell session:

```powershell
$redisEnv = $redisConfig.EnvironmentFile
$compose = @(
  '--project-name', 'reborn',
  '--env-file', $redisEnv,
  '-f', '.\docker-compose.yml',
  '-f', '.\docker-compose.redis.yml'
)
```

Inspect and validate the rendered model:

```powershell
docker compose @compose config --quiet
.\tools\TestRedisMainComposeProfile.ps1
```

The connection string is mounted as a Compose secret rather than inserted
into the server environment. The validator proves the rendered model
contains only the secret-file target, not the secret content.

## Stage 1: add private Redis during active B20H

Stage 1 proves only that Redis belongs to the main `reborn` Compose project
and can start securely. It intentionally leaves the game server on its
current local coordination provider.

Record the current server identity and B20H status first:

```powershell
$serverBefore = docker inspect godswar-server `
  --format '{{.Id}} {{.State.StartedAt}}'
.\tools\GetB20HDockerObservation.ps1
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
.\tools\GetB20HDockerObservation.ps1
```

The Redis inspection may show the private container port but must show no
published host binding. The B20H status must
remain healthy with the same server process start time and observation
window. Do not run Redis outage drills during B20H.

## Stage 2: activate the combined worker after B20H

### Preconditions

Do not begin Stage 2 until:

1. the B20H target time has elapsed;
2. its immutable telemetry export and recovery gates have completed;
3. the result and any remaining retirement blocker have been recorded;
4. all players have been told about the bounded restart;
5. PostgreSQL readiness, checkpoint queues, outbox, and reconciliation are
   healthy; and
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

### Drain and activation

Record durable baselines using approved read-only operations, stop new
admissions, drain the current local authority, and close the legacy client.
After the final B20H export is validated and copied to its retained evidence
location, close the observer sidecar that shares the server network namespace:

```powershell
docker compose --project-name reborn -f .\docker-compose.yml `
  --profile b20h-observation stop b20h-prometheus
docker compose --project-name reborn -f .\docker-compose.yml `
  --profile b20h-observation rm -f b20h-prometheus
```

Record that exact observer-container removal in the B20H evidence. It removes
only the stopped observer container; it must not remove the retained bind-mounted
telemetry, PostgreSQL, its volume, or the game server. The observer must be
removed because `network_mode: service:server` otherwise keeps a Docker
dependency on the old server container and can block its replacement.

Then activate the Redis-coordinated server through both Compose files:

```powershell
docker compose @compose --profile redis-coordinated `
  up -d --wait redis-coordination
docker compose @compose --profile redis-coordinated up -d --wait `
  --no-deps --build --force-recreate server
docker compose @compose --profile redis-coordinated `
  ps postgres redis-coordination server
```

The override must inject at least:

```text
GODSWAR_COORDINATION_PROVIDER=Redis
GODSWAR_COORDINATION_ENVIRONMENT=tempest-local
GODSWAR_REDIS_CONNECTION_STRING_FILE=/run/secrets/redis_connection_string
GODSWAR_REDIS_REQUIRE_TLS=false
GODSWAR_REDIS_DATABASE=0
GODSWAR_COORDINATION_CAPACITY=4096
GODSWAR_REDIS_MAXIMUM_CONCURRENT_OPERATIONS=128
GODSWAR_REDIS_QUEUE_ADMISSION_TIMEOUT_MILLISECONDS=25
GODSWAR_REDIS_OPERATION_TIMEOUT_MILLISECONDS=250
GODSWAR_REDIS_CONNECT_TIMEOUT_MILLISECONDS=1000
GODSWAR_REDIS_CIRCUIT_FAILURE_THRESHOLD=5
GODSWAR_REDIS_CIRCUIT_OPEN_MILLISECONDS=5000
GODSWAR_COORDINATION_SERVER_HEARTBEAT_SECONDS=5
GODSWAR_COORDINATION_SERVER_TTL_SECONDS=20
GODSWAR_COORDINATION_PLAYER_LEASE_RENEWAL_SECONDS=10
GODSWAR_COORDINATION_PLAYER_LEASE_TTL_SECONDS=30
GODSWAR_WORLD_INSTANCE_SERVER_NODE_ID=tempest-openworld-01
```

Local plaintext is permitted only because this dependency is private and the
server runtime profile is `LocalDevelopment`. Production must use TLS.

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

Observe the existing metrics:

- `godswar.server.operational.coordination` with `ready=1`, `routes=23`, and
  bounded in-flight work;
- `godswar.coordination.operations`;
- `godswar.coordination.duration`; and
- `godswar.coordination.logical_results`.

Do not use account, character, endpoint, route, node, lease, ticket, or Redis
error text as metric labels.

## Local outage and restart validation

Run these drills only after Stage 2 activation, during an announced local
test window. Keep PostgreSQL running throughout.

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

The local container is not a production Redis architecture. Before public
activation, the selected provider must supply:

- one private, non-Internet-reachable primary keyspace;
- TLS with validated server identity and an approved certificate trust path;
- secret-manager injection and a tested credential-rotation procedure;
- a least-privilege application ACL scoped to the exact environment prefix;
- `noeviction` and an explicit memory/headroom alert policy;
- no automatic asynchronous replica promotion under the current protocol;
- either zero-data-loss failover or a reviewed failover epoch that
  invalidates all pre-promotion tickets, admissions, routes, and leases;
- same-region p95 at or below 10 ms and p99 at or below 50 ms under the
  measured alpha operation mix;
- declared availability, recovery, maintenance, capacity, connection, PPS,
  and cost limits;
- private security-group/firewall rules allowing only approved gateway and
  worker identities;
- provider telemetry for connection count, command latency, CPU, memory,
  rejected connections, ACL denials, failover, and eviction; and
- an authorized outage/failover exercise and rollback drill.

Redis is an internal coordination dependency, not a public DDoS edge. Only
the protected game gateway addresses should be exposed to clients. Origin
workers, PostgreSQL, Redis, management, metrics, profiling, and
administration must remain private. Upstream L3/L4 TCP and UDP protection is
still required at the provider edge.

Redis Cluster hash-slot sharding is not supported because the reviewed Lua
workflows touch multiple dynamic keys. Do not enable Cluster or widen the
application ACL to make an incompatible provider topology appear healthy.

## Explicitly unimplemented capabilities

Adding Redis to the main Compose project does not implement:

- authoritative live handoff between two gameplay workers;
- transparent reconnect to a different worker;
- splitting connected open-world maps across workers;
- dynamic dungeon creation, placement, lifecycle, or recovery;
- multiple simultaneous dungeon instances sharing one map ID;
- scheduled battlefield admission, placement, lifecycle, or settlement;
- cross-realm or cross-server Pindus admission and settlement;
- automatic worker replacement, map migration, or state reconstruction;
- containerized semantic-gateway edge binding for the unchanged client;
- secure-UDP routing or NAT rebinding through the semantic gateway; or
- a production managed Redis deployment, HA guarantee, capacity proof, or
  regional failover policy.

Those require separate accepted milestones. Until cross-worker transfer is
implemented, one combined worker owns maps `0..22`; dungeon and battlefield
runtime work remains process-local and must not be advertised as distributed
placement.
