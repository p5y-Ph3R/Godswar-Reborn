# B17 Redis coordination runbooks

## Scope and safety

These runbooks cover the disposable B17 Redis dependency, coordination
readiness, outage/restart recovery, credential rotation, and rollback to one
local authority.

Redis is never the only copy of player value. PostgreSQL owns durable player
state and the monotonic ownership fence. Do not use `FLUSHALL`, `FLUSHDB`,
wildcard deletion, or a database restore as an ordinary recovery step.

The local Compose profile is not production-ready. It publishes only
`127.0.0.1`, disables persistence, uses 256 MiB with `noeviction`, and has no
HA/provider SLA.

The current atomic scripts require one primary keyspace. Do not point B17 at
a Redis Cluster/sharded endpoint, and do not enable automatic promotion of an
asynchronous replica. Promotion can resurrect a consumed ticket or a
superseded lease. Public HA needs an approved zero-data-loss policy or a
failover epoch that invalidates all pre-promotion coordination state.

## Local dependency setup

Generate random untracked ACL material:

```powershell
.\tools\NewB17RedisLocalConfiguration.ps1
```

The command returns paths but never prints the password or connection string.
It refuses to overwrite existing material unless `-Force` is explicitly
supplied for a deliberate credential rotation.

Validate and start the dependency:

```powershell
$redisEnv = '.\artifacts\b17-redis-local\redis.local.env'
docker compose `
  --env-file $redisEnv `
  -f .\docker-compose.redis-coordination.yml `
  config --quiet
docker compose `
  --env-file $redisEnv `
  -f .\docker-compose.redis-coordination.yml `
  up -d --wait
```

Load the generated values into only the current PowerShell process:

```powershell
Get-Content $redisEnv |
  Where-Object { $_ -and -not $_.StartsWith('#') } |
  ForEach-Object {
    $name, $value = $_.Split('=', 2)
    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
  }
```

Do not display the environment, run with command tracing, paste the generated
file into chat, or commit `artifacts/`.

The local server must remain `LocalDevelopment` because this Redis endpoint
does not use TLS. Production must keep
`GODSWAR_REDIS_REQUIRE_TLS=true`.

## Local health check

Use the in-container secret without exposing it on the host command line:

```powershell
docker compose `
  --env-file $redisEnv `
  -f .\docker-compose.redis-coordination.yml `
  exec -T redis-coordination sh -c `
  'REDISCLI_AUTH="$(cat /run/secrets/redis_password)" redis-cli --user godswar_b17 ping'
```

Expected output is `PONG`. Also inspect:

```powershell
docker compose `
  --env-file $redisEnv `
  -f .\docker-compose.redis-coordination.yml `
  ps
```

Healthy Redis alone does not make the game process ready. Verify the
application coordination health, worker registration, route count, circuit
state, PostgreSQL readiness, and ownership-fence path.

## CI contract

`.github/workflows/phase5a-network-gate.yml` invokes
`tools/InvokeB17RedisCiGate.ps1` and uploads its bounded JSON result.
The Redis gate must use an isolated job/container, generated independent
application and disposable-admin credentials, database `0`, `noeviction`,
and no persistent volume. The default user must be disabled. It must:

1. build once in Release;
2. prove the application identity can use only the reviewed commands and
   expected test key patterns;
3. prove out-of-prefix, administrative, key-discovery, arbitrary-string,
   publish, and destructive-script operations are denied;
4. run live Redis ticket, worker, route, player-lease, and semantic-gateway
   checks without unexpected ACL denials;
5. inject a bounded pause using only the disposable-admin identity and prove
   the application fails closed;
6. seed disposable state, restart Redis, prove that state is lost and
   unauthenticated access still fails, re-authenticate, and rerun the live
   workflows;
7. prove empty-keyspace recovery and PostgreSQL fencing still reject stale
   owners; and
8. always remove the labelled container and secret artifacts.

A skipped live-Redis check is not a pass. Public targets, arbitrary target
arguments, source spoofing, and unrestricted load generation are forbidden.
The harness may pause/restart only its labelled disposable container. Never
grant `CLIENT PAUSE`, `CONFIG`, `FLUSH*`, or container control to the
application ACL merely to inject a test fault.

The machine result records
`expectedOutcome=state_loss_requires_fresh_authentication` and
`liveTicketContinuityClaimed=false`. A successful restart drill proves safe
failure and reconstruction after disposable state loss; it does not prove
that a live ticket survives that restart.

## Signals

The implemented key families are `server`, `route`, `player`, `ticket`,
`ticket-grant`, `ticket-generations`, `outstanding-tickets`,
`login-account`, `login-name`, `login-connection`, `admission`,
`gateway-counters`, and `gateway-expiry`. Identity-bearing suffixes are
opaque hashes. Never use raw identity-bearing values when inspecting or
exporting the keyspace.

The B17 Redis executor emits the .NET meter
`Godswar.Server.Infrastructure.RedisCoordination`:

| Instrument | Finite labels |
| --- | --- |
| `godswar.coordination.operations` | `coordination.family`, `coordination.outcome` |
| `godswar.coordination.duration` | `coordination.family`, `coordination.outcome` |
| `godswar.coordination.logical_results` | `coordination.family`, `coordination.result` |

Families are `health`, `worker`, `route`, `player`, `ticket`, and
`admission`. Operation outcomes are `success`, `timeout`, `unavailable`,
`overloaded`, `circuit_open`, and `cancelled`. These describe Redis
execution: a completed Lua script is `success` even if it returns a lease
conflict.

`logical_results` separately reports the finite worker/route/player decisions
`applied`, `current`, `not_found`, and `conflict`, without duplicating latency
or transport-failure counts.

Also observe bounded process snapshots: readiness, configured capacity,
maximum concurrency, in-flight operations, locally observed registered
routes/player leases, conflicts, timeouts, unavailable results,
overload/circuit rejects, and last successful operation time. These are not
a global key-cardinality or provider-capacity measurement.

Do not add account, character, username, route, node, connection, ticket,
lease token, endpoint, or exception text as a metric label.

Initial alert candidates require staging calibration:

- any sustained `circuit_open` or `unavailable` result;
- p99 operation duration above 50 ms for five minutes;
- in-flight work above 75% of 128 for five minutes;
- any player-lease conflict or renewal failure;
- a registered-route drop not caused by a recorded drain;
- no successful health operation for more than one 20-second server TTL; or
- Redis `used_memory` approaching the configured 256 MiB local limit or the
  approved provider limit.

These thresholds are safety prompts, not production capacity guarantees.

## Redis unavailable or slow

Expected behavior:

- application coordination readiness becomes false;
- new distributed login, admission, route, transfer, reconnect, and lease
  operations fail closed within their deadline;
- the circuit opens after the configured failure threshold;
- ECS/map ticks continue without waiting for Redis; and
- PostgreSQL player value remains unchanged and authoritative.

Response:

1. Stop new admissions and transfers; do not enable a silent local fallback.
2. Check provider/container health and network/TLS/ACL state without printing
   the connection string.
3. Confirm PostgreSQL and the durable ownership-fence path are healthy.
4. Identify sessions whose 30-second player lease is no longer proven.
5. Stop valuable commands for unproven sessions and drain or disconnect them.
6. Restore Redis or execute the coordinated single-authority rollback below.
7. Require consecutive healthy checks before admitting new distributed work.
8. Re-register workers/routes and reacquire player leases through PostgreSQL
   fencing.
9. Run report-only reconciliation for any command with an uncertain client
   acknowledgement.

Never extend TTLs manually to hide an outage.

## Redis restart or empty keyspace

For the disposable local drill:

```powershell
docker compose `
  --env-file $redisEnv `
  -f .\docker-compose.redis-coordination.yml `
  restart redis-coordination
```

The keyspace is expected to return empty because persistence is disabled.
The Redis process again requires authentication after restart. Existing
application credentials may authenticate the newly started process, but
lost tickets/admissions are not recreated and must not be treated as live.

Recovery order:

1. keep distributed admission closed;
2. verify PostgreSQL owner UUID/generation high-water marks;
3. let each still-running worker re-register with its existing process boot
   ID; a process that actually restarted must use its newly generated boot
   ID;
4. publish exact routes only from current worker registrations;
5. reacquire player leases with the valid or newly advanced PG fence;
6. require fresh authentication for expired/lost tickets and admissions;
7. reopen admission gradually; and
8. verify stale workers cannot commit valuable PostgreSQL mutations.

Do not reconstruct ownership from old logs or assume a missing key means no
owner exists.

## Lease conflict or split ownership

1. Stop new work for the affected session and preserve finite audit evidence.
2. Lock/read the PostgreSQL ownership row through the approved application or
   read-only operations path.
3. Treat the highest valid PostgreSQL generation as authoritative.
4. Drain the Redis/local claimant whose owner UUID/generation does not match.
5. Reject its valuable writes through the existing transaction-wide PG fence.
6. Reinstall one Redis lease carrying the authoritative fence.
7. Reconcile checkpoints and any uncertain nonvaluable projection.
8. Alert on the conflict; never solve it by deleting the durable ownership
   row or lowering its generation.

## Credential or endpoint compromise

1. Drain new coordination operations and isolate the private Redis endpoint.
2. Generate a new ACL credential through the provider secret workflow.
3. Update each process secret without logging it.
4. restart/drain processes in a controlled sequence;
5. revoke the old ACL user/password after the overlap window; and
6. verify no unknown client remains and all expected workers re-register.

For local development only:

```powershell
.\tools\NewB17RedisLocalConfiguration.ps1 -Force
```

Restart the local container after rotation. Existing disposable keys may be
lost; follow the empty-keyspace recovery order.

## Coordinated rollback to `Local`

Use this only when the realm can run on one authoritative gateway/worker
boundary:

1. mark Redis-backed ingress and workers draining;
2. stop new login, game admission, reconnect, transfer, and placement;
3. wait for bounded in-flight coordination operations to finish or time out;
4. disconnect/drain sessions whose lease cannot be proven;
5. verify one worker owns every route needed by connected open-world portals;
6. verify PostgreSQL readiness, ownership generations, checkpoint queues, and
   outbox state;
7. set `GODSWAR_COORDINATION_PROVIDER=Local`;
8. remove `GODSWAR_REDIS_CONNECTION_STRING` from the new process environment;
9. restart one semantic gateway/worker authority;
10. require fresh login; then reopen admission gradually; and
11. keep Redis intact until rollback verification is complete.

Rollback retains all PostgreSQL data, migrations, inbox/outbox, audits, and
owner generations. It discards tickets, presence, routes, and lease
projections. Do not run mixed `Local` and Redis authorities for the same
realm.

After verification, stop the disposable local dependency:

```powershell
docker compose `
  --env-file $redisEnv `
  -f .\docker-compose.redis-coordination.yml `
  down --remove-orphans
```

There is intentionally no volume to delete.

## Post-incident verification

- PostgreSQL is ready and durable ownership generations never decreased.
- Exactly one accepted owner can commit for each active character.
- Current workers have fresh boot IDs and exact nonconflicting routes.
- No stale ticket/admission can be consumed.
- Coordination latency, in-flight work, timeout, unavailable, and circuit
  counters return toward baseline.
- Checkpoint/outbox queues drain without discarded valuable state.
- Logs and metrics contain no connection string, credential, ticket, lease
  token, player identity, or arbitrary Redis error text.
- The incident record states UTC times, build/config revision, outage mode,
  player impact, recovery/rollback path, and reconciliation outcome.

Managed HA, regional failover, backup/restore, declared provider RTO, and
public production activation remain separate approvals.
