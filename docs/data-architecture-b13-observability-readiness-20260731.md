# B13 structured observability and readiness

Date: 2026-07-31

Status: implementation complete; final verification is recorded below.

Next roadmap ticket after verified completion: B14 - raw authentication
retirement

## Outcome and boundary

B13 adds an operational boundary around the existing authoritative server. It
does not change gameplay authority, packet semantics, persistence ownership, or
the secure TCP/UDP design.

The intended result is:

- bounded, structured, privacy-aware operational logs;
- bounded retained activities at selected instrumented server seams;
- export of the repository's existing low-cardinality meters;
- a private management listener, separate from every public game listener;
- process liveness, aggregate readiness, and explicit draining state;
- named supervision for critical runtime tasks;
- operator dashboards, alerts, and incident procedures; and
- tests proving that logging, tracing, metrics, and dependency failures cannot
  create an unbounded or public diagnostic path.

This record distinguishes three control layers:

| Layer | B13 scope |
| --- | --- |
| Application | Logging, tracing, metrics, management endpoints, readiness, supervision, bounded telemetry behavior |
| Host/container | Private binding, resource budgets, rotating container logs, process restart and socket policy |
| Upstream provider | Volumetric TCP/UDP scrubbing, clean bandwidth/PPS, origin hiding, regional failover and mitigation telemetry |

B13 cannot make a production capacity or DDoS-resistance guarantee. No hosting
provider, region plan, clean-bandwidth target, packet-per-second allowance,
production RPO, or production RTO has been selected.

## Repository baseline

The implementation started with useful instrumentation already present:

| Existing meter | Repository evidence | Existing signal |
| --- | --- | --- |
| `Godswar.Server.Networking` | `Networking/NetworkRuntimeMetrics.cs` | connections, bounded queues, bytes, timeouts, disconnect and drain outcomes |
| `Godswar.Server.Networking.Secure` | `Networking/Secure/SecureNetworkMetrics.cs`; `Secure/Udp/SecureUdpMetrics.cs` | TLS, secure queues, protected UDP outcomes |
| `Godswar.Server.Security.Authentication` | `Security/Authentication/AuthenticationMetrics.cs` | finite authentication outcomes and duration |
| `Godswar.Server.Simulation` | `Game/SimulationLoopMetrics.cs` | loop lifecycle, tick duration/drift, missed deadlines, heartbeat |
| `Godswar.Server.OperationalState` | `Operations/OperationalStateMetrics.cs` | admission, tickets, UDP sessions and limiter occupancy |
| `Godswar.Server.Application.Commands` | `Application/Commands/CommandMetrics.cs` | finite family, identity-strength and outcome dimensions |
| `Godswar.Server.Infrastructure.PostgresCommands` | `Infrastructure/Messaging/PostgresCommandMetrics.cs` | inbox duration/outcome and outbox backlog, age, retry, gap and poison |
| `Godswar.Server.Application.CharacterCheckpoints` | `Application/Characters/CharacterCheckpointMetrics.cs` | queue, writes, retry, readiness, dirty age and heartbeat |
| `Godswar.Server.Application.CharacterSnapshots` | `Application/Characters/MeasuredCharacterSnapshotReader.cs` | load outcome and latency |
| `Godswar.Server.WorldContent` | `Application/World/WorldContentMetrics.cs` | content-load outcome and latency |
| `Godswar.Server.RuntimeProfile` | `Operations/ServerProfileMetrics.cs` | finite startup rejection and legacy-authentication outcomes |

B12's reward, progression, pet, inventory and outbox operations already record
their bounded command-family outcomes through the command meters. B13 exports
those instruments rather than duplicating player-value counters.

Before B13, direct console and legacy payload diagnostics created privacy
risk; no activity source, metric exporter, private management endpoint, named
worker heartbeat, or dependency-aware Docker readiness path was composed.

## Management-plane contract

The management listener must be configured separately from login, game, and
UDP endpoints. The first B13 implementation is exactly loopback-only:
`127.0.0.1` or `::1`, with default TCP port `9090`. A future deployment adapter
may propose an isolated private management network, but that requires a
separate reviewed configuration change.

It must never be exposed through a public game address, ordinary ingress, or
an HTTP CDN.

| Method and route | Meaning | Success | Failure behavior |
| --- | --- | --- | --- |
| `GET /live` | Process and critical supervisor can still make progress | `200` with a small finite response | `503`; no exception, identity or dependency detail |
| `GET /ready` | Instance can accept new sessions and valuable commands | `200` with finite component codes | `503` with finite reason codes |
| `GET /metrics` | Private Prometheus representation of approved meters | bounded `200` response | bounded `503`; exporter failure never blocks gameplay |
| `GET /traces` | Bounded, sampled, sanitized recent activity view | bounded `200` response | bounded empty/error response; never raw payloads |
| `POST /drain` | Stop readiness and begin graceful admission shutdown | authenticated `202` or idempotent success | `401/403` without details; no state change |

`POST /drain` requires a secret supplied outside source control, compared
without timing-dependent early exit, and omitted from logs, traces, metrics,
process arguments, and responses. Repeated drain requests are idempotent.

Both checked-in Compose profiles intentionally leave
`GODSWAR_MANAGEMENT_DRAIN_TOKEN_FILE` unset. The resulting empty
`Operations.DrainTokenFile` means every HTTP `/drain` authentication attempt
fails closed. Docker `SIGTERM` is the default graceful path: the registered
signal handler invokes the same bounded drain coordinator, stops new
admission, waits within the configured drain deadline, and then requests
shutdown. HTTP drain is an optional operator control and must be enabled only
with an absolute path to a read-only container secret file.

Management request bodies, header sizes, concurrent requests, response sizes,
read/write time, and retained trace entries must all have hard bounds. HTTP
request values are never metric-label values.

## Readiness decisions

Liveness is intentionally narrower than readiness. Dependency failure should
normally remove readiness without turning a database outage into a process
restart loop. An irrecoverably faulted critical supervisor may fail liveness so
the host can replace the process.

Readiness is true only when all required conditions are true:

1. the selected listener profile has initialized and its coherent login/game
   pair is listening;
2. the loaded schema and immutable content revisions are compatible with the
   binary;
3. PostgreSQL is reachable through a short, bounded background probe;
4. the checkpoint coordinator is ready and its heartbeat is within its
   configured bound;
5. the outbox worker is healthy when enabled;
6. the durable-progression retry worker is healthy and its finite handoff is
   below its rejection threshold;
7. required simulation loops are active and each heartbeat is within its
   expected period plus the configured grace;
8. all other critical runtime tasks are running or stopped only for normal
   shutdown;
9. bounded queues are below their configured rejection thresholds; and
10. the instance is not draining.

Metric callbacks must not query PostgreSQL or perform other I/O. Dependency
probes update a bounded cached snapshot on their own supervised cadence.
Readiness reads only that snapshot.

A dependency response uses finite codes such as `database_not_ready`,
`checkpoint_worker_not_ready`, `persistence_worker_not_ready`,
`simulation_loop_not_ready`, `queue_saturated`, `critical_task_faulted`, or
`draining`. Exception messages, connection strings, database names and player
identifiers are excluded.

## Logging contract

Structured logs use:

- a finite event catalog and event ID;
- timestamp, level, component and finite outcome/reason fields;
- an application-level rate budget;
- a bounded non-blocking queue whose dedicated writer owns sink I/O; and
- counters for enqueued, written, rate-limited, oversized, queue-full and
  sink-failure outcomes.

Shutdown waits only the configured finite timeout. If a sink remains blocked,
pending queued records are discarded and counted by the logger's
`ShutdownDropped` runtime snapshot field; that process-local final counter is
not promised as an exported metric after termination.

Logs must never contain credentials, connection strings, certificate material,
tickets, cookies, UDP keys, raw packets, packet hex, arbitrary player text,
usernames, account/character/pet/death/operation IDs, IP addresses, ports, or
raw exception messages. Detailed durable audits remain in PostgreSQL and are
not replaced by operational logging.

Legacy payload diagnostics are disabled by default. Production does not gain
an override that can turn raw packet or credential logging back on.

The source instrument and its exported form are:

```text
source:   godswar.server.logs.events{log.event,log.outcome}
exported: godswar_server_logs_events{log_event,log_outcome}
```

The event and outcome value sets are fixed in code and tests. Log level remains
inside the structured record; it is deliberately not another metric dimension.

## Trace contract

The implemented activity seams are:

| Span | Repository evidence | Implemented behavior |
| --- | --- | --- |
| Login/game packet handler | `Game/LoginClientHandler.cs`; `Game/GameClientHandler.cs`; `Operations/Observability/ServerActivity.cs` | Begins after one framed packet has been read; deterministically retains 1 of every 64 accepted handlers, while rejected and faulted handlers are materialized even when not selected for normal sampling |
| Application command | `Application/Commands/CommandMetrics.cs` | Records the finite command family, outcome and duplicate classification |
| Durable-command PostgreSQL inbox transaction | `Infrastructure/Messaging/PostgresCommandMetrics.cs` | Records bounded inbox transaction duration, family, stage and outcome |
| Checkpoint write | `Application/Characters/CharacterCheckpointMetrics.cs` | Records bounded write duration, facet and outcome |
| Outbox dispatch | `Infrastructure/Messaging/PostgresOutboxDispatcher.cs` | Covers each bounded dispatch pass and its terminal outcome |
| Management request | `Operations/ServerOperationsMetrics.cs` | Records only the finite management route and outcome |

These activities may inherit `Activity.Current` when invoked beneath an
active sampled parent, but B13 does not guarantee one continuous end-to-end
trace for every packet or command. Socket reads/writes, connection admission,
ECS projection, state replication, and client acknowledgement do not have
composed production spans in this increment.

`ServerTraceOperation.EcsProjection` and
`ServerTraceOperation.ClientAcknowledgement` reserve reviewed finite operation
codes, but no production call site emits those spans. A catalogued code is not
evidence that its seam is implemented.

Activities use server-generated trace context. They may include finite fields
such as transport kind, endpoint role, command family, result class and
duplicate status. They do not include player, network, packet, database-row or
operation identities.

Accepted packet spans use deterministic 1/64 sampling to keep packet-rate
allocation and ring contention bounded in proportion to the sample rate.
Rejected and faulted packet outcomes bypass that sampling decision so an
unsampled packet failure still reaches the diagnostic ring. The non-packet
application, PostgreSQL, checkpoint, outbox, and management seams above are
retained when invoked.

The in-process trace ring and each span's tag count are bounded. When full, the
oldest retained activity is overwritten and the outcome counter advances.
Collector failure drops or expires telemetry without blocking a network
handler, simulation loop, persistence transaction, shutdown drain, or client
response.

The source trace instrument and exported form are:

```text
source:   godswar.server.traces.spans{trace.outcome}
exported: godswar_server_traces_spans{trace_outcome}
```

The bounded collector publishes its own state without unbounded labels:

```text
godswar_server_metrics_collector{state}
```

The finite `state` values include instrument/series/measurement counts,
dropped instrument/series/tag/measurement counts, and truncated snapshots.

All accepted histograms use milliseconds and export cumulative fixed buckets:

```text
0.5, 1, 2.5, 5, 10, 25, 50, 100, 250, 500, 1000, 2500,
5000, 10000, 30000, +Inf
```

Each histogram exports `_bucket`, `_sum`, and `_count`. The finite bucket
policy makes Prometheus `histogram_quantile` queries valid without dynamic
per-instrument bucket allocation. Non-millisecond histograms are rejected by
the bounded collector.

## Critical-task and progression-retry signals

The named-task catalog is finite and created during composition. A task name
cannot be derived from an exception, player, map or request.

The critical-task supervisor feeds current `/live` and `/ready` decisions and
exports its finite task/state view:

```text
godswar_server_operations_critical_tasks{task,state}
```

The operational management families are:

```text
godswar_server_operations_liveness
godswar_server_operations_readiness{phase,reason}
godswar_server_operations_management_requests{route,outcome}
```

The request counter uses only the finite route and outcome catalogs. Request
paths, headers, tokens, network addresses, and arbitrary text never become
labels.

The process-local progression retry worker publishes:

```text
godswar_progression_retry_queue_depth
godswar_progression_retry_oldest_age_seconds
godswar_progression_retry_heartbeat_age_seconds
godswar_progression_retry_worker_state
```

The existing outbox instruments remain:

```text
godswar_outbox_backlog
godswar_outbox_oldest_age_seconds
```

Current readiness is returned by `/ready`; liveness is returned by `/live`.
The same cached operational state drives the exported readiness/liveness
gauges, so metric callbacks perform no dependency I/O.

## Dashboard, alerts and runbooks

The operator artifacts are:

- [dashboard/query contract](operations/b13-dashboard-queries.md);
- [incident runbooks](operations/b13-incident-runbooks.md); and
- [`operations/prometheus/godswar-server-alerts.yml`](../operations/prometheus/godswar-server-alerts.yml).

The earlier
[Phase 5A network operations guide](network-infrastructure-phase5a-operations.md)
remains authoritative for volumetric TCP/UDP incidents, origin exposure,
upstream-provider failure, and network-key compromise. B13 does not relabel
application rate limiting as upstream DDoS mitigation.

Alert expressions are conservative initial signals, not SLOs or production
capacity statements. Thresholds must be tuned from authorized staging and
production baselines after a provider and deployment model are selected.

## Docker and CI expectations

The base and secure Compose profiles should use `/live` or `/ready`, not only
socket inspection. The management endpoint remains private or host-loopback
only. Container logs require size/file rotation, and telemetry endpoints must
not share login, game, or UDP mappings.

Both profiles allow a 45-second stop grace period, leaving margin beyond the
bounded 32-second worst-case application shutdown budget before Docker may
force termination.

CI and local gates should prove:

- configuration is fail closed;
- management routes and authentication are exact;
- PostgreSQL outage removes readiness and recovery restores it;
- draining removes readiness while liveness remains healthy;
- critical-worker fault and stale heartbeat are observable;
- exporter or log-sink failure cannot block gameplay;
- a high-rate repeated diagnostic workload remains within memory/output
  bounds and increments drop counters;
- metric and trace dimensions are finite and privacy-safe;
- secure and raw Compose profiles expose no public management endpoint; and
- existing B03, managed, native and Phase 5A gates remain green.

## Known initial limitations

- The first management listener is process-loopback only. An external
  collector requires a trusted host-local mechanism; the repository does not
  publish a management port.
- The fixed histogram buckets support quantile estimation, but the estimates
  have bucket-level resolution and are not exact percentile samples.
- Npgsql pool metric names and dimensions are not claimed until observed and
  ratcheted through the bounded collector.
- The trace endpoint is a bounded in-process diagnostic ring, not a durable
  trace archive or a managed collector service.
- Reconciliation automation, production backup/PITR, scale-out ownership and
  upstream mitigation remain their explicitly assigned later tickets.

## Backup and restore boundary

B01B and B03 already prove a local PostgreSQL custom dump/restore and upgrade
path. They are useful recovery evidence, but not production backup retention,
WAL/PITR, off-host durability, or an RPO/RTO guarantee.

B13 documents dependency health and recovery signals. B19 remains responsible
for the reconciliation service and scheduled restore drills. Production
backup policy requires a provider, retention decision, RPO and RTO.

## Rollback

Telemetry is not authoritative state.

1. Drain or stop the affected binary through the normal bounded shutdown.
2. Disable the exporter or structured sink independently if it is the fault.
3. Roll back to the prior schema-compatible binary and configuration.
4. Preserve PostgreSQL audits, inboxes, ledgers, outbox rows and player state.
5. Verify listener, checkpoint, outbox and dependency readiness before
   accepting sessions.

Never disable durable audits, command idempotency, authentication, replay
protection, persistence bounds, or upstream ingress protection to recover an
observability component.

## Final verification

The recorded B13 verification evidence is:

```text
Implementation commit:                 this B13 commit
Release build:                         PASS, 0 warnings / 0 errors
Managed protocol checks:               PASS, 258 passed / 0 failed
B13 focused checks:                    PASS, 8 passed / 0 failed
PostgreSQL outage and recovery:         PASS, outage ready=1 / live=0;
                                       recovery ready=0; server stayed running
Base/secure Docker and profile checks: PASS
Live private metrics/traces scrape:     PASS, 16,133-byte metrics response,
                                       96 histogram bucket lines,
                                       0 prohibited labels, 64 bounded spans
Docker SIGTERM graceful drain:          PASS, exit 0 / stopped lifecycle /
                                       healthy after restart
B03 PostgreSQL migration gate:         PASS, 42 checks / 4 scenarios,
                                       441.3 seconds
Native shim offline checks:            PASS,
                                       SHA-256 D32C41F80EBCBB5C7870953B095C73D425D06EF3A882C957F4E04303252144E8
File-size and privacy ratchets:        PASS, 70 changed files under
                                       20 KB / 600 lines; live logs contained
                                       0 prohibited diagnostic values
```

The readiness values above are management-probe process exit codes: `0`
means the probe condition passed and `1` means it correctly failed. The
outage therefore proved not-ready while live, and the recovery probe returned
ready. The metrics and trace scrape ran from inside the container because
the management port is intentionally not published to the host.
