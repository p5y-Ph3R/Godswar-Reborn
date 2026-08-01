# 16. Deployment and operations

## 16.1 Configuration and secrets

- B20E enforces an allowlisted, PostgreSQL-only provider and fail-closed validation; preserve that startup matrix.
- Production must explicitly select PostgreSQL; missing/malformed numeric/bool configuration must not silently fall back.
- Keep secrets out of tracked files and command lines. Use provider secret manager/container secret files; the secure certificate password-file handling is a good local model.
- Rotate database credentials, TLS certificates, UDP cookie/key roots, and operator credentials with documented overlap/recovery.
- Pin .NET SDK, NuGet graph, GitHub Actions, PostgreSQL/server image digests, and generate an SBOM.

## 16.2 Migration and rolling deployment

- A one-shot migrator takes the existing advisory lock, verifies backup/build manifest, applies expand migrations, and exits.
- Instances verify exact supported schema/content ranges before readiness.
- Deploy new readers/writers, backfill, reconcile, drain old binaries, then apply contract migrations in a later release.
- Do not run competing startup content upserts from different versions. Publish an immutable content revision once and pin it.
- Rollback uses a schema-compatible prior binary. Database restore is reserved for destructive corruption; ordinary rollback should be code/config plus forward repair.

## 16.3 Connections and runtime resources

- One shared `NpgsqlDataSource`; pool maximum based on DB capacity and instance count, not defaults multiplied blindly.
- Explicit connect/command/lock/idle lifetime and cancellation.
- Separate bounded worker concurrency for checkpoints, valuable commands, outbox, and reconciliation.
- Opt-in B17 Redis uses one shared multiplexer per process, strict async
  deadlines, bounded concurrency, circuit breaking, opaque keys, and
  `noeviction`; production provider/capacity approval remains outstanding.
- MongoDB connection management is not applicable until an approved ADR.
- Run containers as non-root with read-only filesystem where practical, dropped capabilities, `no-new-privileges`, CPU/memory/PID/file limits, bounded logging, and pinned images.

## 16.4 Health and readiness

Liveness answers only whether the process and critical supervisor are functioning. Readiness must include:

- listener profile initialized;
- schema/content version compatible;
- PostgreSQL reachable within deadline and pool not exhausted;
- critical ECS/background-loop heartbeat healthy;
- command/outbox queues below rejection thresholds;
- instance not draining;
- if multi-instance, ownership/Redis coordination healthy enough for new sessions.

Both current Docker profiles use the bounded in-image management readiness
probe against a loopback-only listener. The listener is not published on a
host or public game port. Read-only health, metrics, and trace routes are
process-local; the state-changing drain route fails closed unless an operator
supplies its bearer token through a secret file.

## 16.5 Observability

Adopt structured `ILogger`-style logging or equivalent with finite event IDs, levels, sampling, redaction, and bounded sinks. Add `ActivitySource` spans across:

```text
transport receive -> decode -> command queue -> application handler
-> PG transaction -> ECS projection -> outbox/replication -> acknowledgement
```

Propagate opaque correlation/operation IDs, not usernames/IPs in metric labels. Export existing .NET `Meter` instruments through a private OpenTelemetry/Prometheus path. Add process/runtime, Npgsql, worker, outbox, and reconciliation instruments.

Recommended metrics:

- login authentication and character-load p50/p95/p99 latency;
- character save/checkpoint latency, dirty age, failures, conflicts, retries;
- PG pool active/idle/waiters/timeouts; transaction retries/deadlocks;
- Redis hit rate/latency/errors after adoption;
- duplicate commands, request-hash conflicts, rejected replayed/stale packets;
- active TCP connections, authenticated sessions, UDP sessions, reconnects;
- conflicting player ownership/lease renew failures;
- command/checkpoint/egress queue depth, age, drops/rejections;
- outbox backlog, oldest age, retries, poison count;
- reconciliation rows/mismatches/repairs;
- tick duration/drift/missed deadlines and critical-loop heartbeat;
- packet/byte rate, snapshot size, RTT/jitter/loss/fallback;
- log drops/rate limiting and audit-write failures.

Dashboards should cover login/session, world/tick, persistence/economy, networking/abuse, and deployment/schema. Alerts require configurable thresholds based on baselines, not invented production guarantees.

## 16.6 Backups and disaster recovery

- B19 implements a mandatory local/CI logical recovery drill against an
  isolated PostgreSQL 17.9 container. It verifies the exact migration head,
  clean synthetic reconciliation, custom-dump SHA-256/size, restored logical
  fingerprint, restored reconciliation, timing evidence, and exact cleanup.
  Its zero-loss observation applies only to the quiesced synthetic snapshot;
  it is not a production RPO/RTO claim.
- Define RPO/RTO before production.
- Use automated PostgreSQL backups plus WAL/PITR as supported by the chosen provider.
- Encrypt backups, restrict access, record checksums/schema/build/content versions, and test restoration on schedule.
- Run migration and reconciliation against restored copies.
- Redis coordination/cache does not determine player-data RPO; rebuild it. If Redis persistence is enabled, it shortens operational recovery only.
- Preserve immutable economy/security audits according to retention policy.
- Document region/provider failure, database corruption, origin exposure, credential/key compromise, and rollback runbooks.

Use the
[B19 reconciliation and restore runbook](../operations/b19-reconciliation-restore-runbook.md)
for the implemented local gate and its promotion boundary. A provider,
backup/WAL retention, off-host or off-region isolation, production-volume
rehearsal, and approved business RPO/RTO are still required before production.

## 16.7 Capacity and DDoS responsibilities

| Layer | Responsibility |
| --- | --- |
| Application | Bounded decoders/queues/maps, authentication, cookies/replay, rate/cost limits, load shedding, no amplification, secure command validation |
| OS/network | Socket backlog, SYN cookies/connection tracking, file descriptors, buffers, firewall, process/container limits, private management network |
| Upstream provider | Arbitrary TCP/UDP L3/L4 scrubbing, clean bandwidth/PPS, origin hiding, IPv4/IPv6, regional failover, health/telemetry/SLA |

Autoscaling is capacity management, not volumetric DDoS mitigation. No hosting provider or production capacity target is currently selected, so this roadmap makes no capacity guarantee.
