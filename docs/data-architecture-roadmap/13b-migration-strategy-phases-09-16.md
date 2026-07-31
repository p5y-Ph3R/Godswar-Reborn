# 13. Migration strategy - phases 9-16

## Phase 9 - Redis and Mongo decision gates

- **Goal:** approve only demonstrated stores.
- **Scope/tasks:** ADR 0003 records the historical defer; ADR 0004 confirmed
  the use case; ADR 0005 approved and bounds the now-implemented opt-in B17
  provider. Production activation still waits for measured SLO/cost/outage,
  provider, and remote failure-isolation evidence. Complete a Mongo ADR only
  if section 8 evidence appears.
- **Likely files/modules:** architecture/operations docs, load tools, deployment design.
- **Dependencies:** stable command ownership and production capacity inputs.
- **Data migrations:** none.
- **Acceptance criteria:** the Redis use case and activation boundary are
  explicit; SLO, TTL, capacity, outage, owner, and cost inputs are approved
  before deployment; Mongo remains absent without evidence.
- **Tests:** design simulations/failure prototypes only if a store is approved.
- **Metrics:** measured PG read load, session count, routing operations, cache candidate hit potential.
- **Rollback:** do not introduce package/infrastructure when decision is defer.
- **Risks:** adopting infrastructure for hypothetical scale.
- **Complexity:** Small for decision; Medium for evidence.

## Phase 10 - Redis tickets, admissions, presence, and routing, opt-in

- **Goal:** support multiple processes without moving player value from PG.
- **Scope/tasks:** implemented typed keys, consume-once tickets/admissions,
  worker registry/routes, PG-fenced player presence, Local/Redis adapters,
  deadlines, circuit breaker, readiness, metrics, CI, and runbooks.
- **Likely files/modules:** `Application/Sessions/IGameTicketStore`, session ownership application contracts, `GameSessionRegistry` boundary, Redis infrastructure/config/tests.
- **Dependencies:** ADR 0004, PG ownership fence, B18 local placement and
  sticky-routing design, B18C2 semantic gateway/session authority or another
  real shared-coordination boundary, and approved operational budgets.
- **Data migrations:** PG server/ownership audit metadata if needed; no player value in Redis.
- **Acceptance criteria:** atomic cross-process coordination is bounded;
  Redis loss cannot authorize durable value; public deployment remains gated
  until the two-process staging and provider controls pass.
- **Tests:** Testcontainers Redis, expiry/eviction/restart, split ownership, Lua atomicity, shared NAT rate limits, reconnect to another process.
- **Metrics:** Redis latency/errors, lease renew/conflict, active ownership, ticket outcomes, stale presence.
- **Rollback:** single-instance mode using local implementation; drain to one owner before disabling Redis.
- **Risks:** lease expiry under pause/network partition; solve with PG fencing and fail-safe drain.
- **Complexity:** Large.

## Phase 11 - TCP/UDP application integration

- **Current increments:** B18B completes process-local `WorldInstanceId`
  session routing, one bounded owner mailbox per map runtime, and bounded
  socket fanout outside owner commands. B18C1 adds a bounded opaque login/game
  TCP relay process to one private combined worker plus configurable worker
  `ServerNodeId` and raw advertised game `PublicPort`. It does not terminate
  TLS/auth, interpret packets, relay secure UDP, own sessions, select
  workers, route instances, or activate Redis. Its managed and real
  two-process smoke evidence is complete. B18C2 adds a loopback-only
  semantic legacy edge, hardened local authentication, bounded single-use
  login admissions, exact
  `RealmId`/`MapId`/`WorldInstanceId`/`ServerNodeId` routing, and a mutually
  authenticated TLS private worker backhaul. It is completed and verified.
  It does not add Redis, UDP routing, live cross-worker transfer, remote
  production placement, or a capacity guarantee.
- **Goal:** route all network inputs through typed command envelopes and single-owner mailboxes with explicit reliability.
- **Scope/tasks:** classify every opcode; require secure principal in production; add bounded map/player ingress; remove DB/socket fanout from tick; preserve UDP movement semantics and TLS fallback; make viewer replication fairness-aware.
- **Likely files/modules:** `LoginClientHandler`, partial `GameClientHandler`
  files, `ClientSession`, secure realtime classes, `GameSessionRegistry`,
  `MapInstance`, ECS boundaries,
  `src/Godswar.Server/Networking/RelayGateway`, and
  `src/Godswar.Server/Networking/SemanticGateway`,
  `src/Godswar.Server/Networking/Backhaul`, and
  `tools/Godswar.Server.B18CSmoke`.
- **Dependencies:** application contracts, checkpoint/ownership, valuable command idempotency.
- **Data migrations:** none beyond earlier inbox/fence.
- **Acceptance criteria:** no networking type calls Npgsql/store; no fixed-step system awaits DB or sequential client fanout; secure profile passes end-to-end and raw path is dev-only/removed.
- **Tests:** loss/dup/reorder/replay, TCP partial/coalesced/slow client,
  fallback, reconnect, map transfer, queue overload, deterministic replay,
  the Docker-free B18C1 real relay/worker process smoke, and B18C2
  two-worker mTLS route/drain/failure/replay/full-login coverage.
- **Metrics:** command queue depth/age/reject, tick drift, fanout latency, transport fallback, stale/replayed packet rejects.
- **Rollback:** omit `--semantic-gateway`/worker backhaul and return to
  B18C1 or the directly advertised single worker; retain per-command adapter
  flags/protocol compatibility and all PostgreSQL state.
- **Risks:** original client compatibility and packet-order assumptions.
- **Complexity:** Large.

## Phase 12 - Backfill and reconciliation

- **Goal:** prove new authoritative representations match legacy data before cutover.
- **Scope/tasks:** backfill versions/content revisions; create one audited opening-balance/cutover ledger entry for each existing wallet rather than inventing historical transactions; compare `character_items` with projections; validate balances/progression/pets; instrument and inventory reads/writes of legacy account/character columns before any archive/drop; inventory capture dependencies; produce signed reconciliation reports.
- **Likely files/modules:** migrations, `tools`, `database/fixtures`, `docs/database-migrations.md`.
- **Dependencies:** target schemas from Phases 5-8.
- **Data migrations:** bounded/idempotent backfills with progress markers.
- **Acceptance criteria:** zero unexplained mismatch; every preexisting wallet has one reconciled opening-balance entry and every post-cutover delta is ledgered; resumable batches; production estimates from staging copy; no mutation in report-only mode.
- **Tests:** interruption/resume, duplicate execution, representative backup, old/new server coexistence.
- **Metrics:** rows scanned/fixed, mismatches by finite category, ETA, lock/wal impact.
- **Rollback:** restore backup or forward-repair; never reverse destructive backfill without proof.
- **Risks:** table locks/WAL growth/incorrect automated repair.
- **Complexity:** Medium/Large.

## Phase 13 - Reliability, observability, and operations

- **Goal:** make persistence health and failures visible and actionable.
- **Scope/tasks:** structured redacted logging; OpenTelemetry traces/metrics exporter on private endpoint; readiness/liveness/drain; critical-task supervisor; outbox/reconciliation worker supervision; dashboards/runbooks; backup/PITR/restore drills.
- **Likely files/modules:** `Program.cs`, `Operations`, metrics classes, logging call sites, Docker/deployment docs.
- **Dependencies:** stable application and worker boundaries.
- **Data migrations:** operational retention/partition changes only if required.
- **Acceptance criteria:** alerts and runbooks validated in fault drills; no raw payload/secret logs; restore meets declared RPO/RTO.
- **Tests:** DB/Redis outage, worker fault, disk/pool pressure, restore, key compromise, log-flood bounds.
- **Metrics:** section 16 metrics and service/runtime resource metrics.
- **Rollback:** exporter/log sinks can be disabled independently; never disable durable audit.
- **Risks:** telemetry itself causing cardinality/CPU/disk denial of service.
- **Complexity:** Medium.

## Phase 14 - Performance, load, and soak

- **Goal:** establish reproducible capacity baselines, not production guarantees.
- **Scope/tasks:** extend Phase 5A with loopback live clients, PG workloads, AOI fanout, command mixes, persistence workers, and optional Redis; use latency/jitter/loss/reorder/MTU emulation; run authorized staging soak.
- **Likely files/modules:** `tools/Godswar.Server.Phase5A`,
  `tools/Godswar.Server.B18CSmoke`, secure smoke tool, test harness, and
  benchmark docs.
- **Dependencies:** phases under measurement complete; capacity assumptions provided.
- **Data migrations:** test fixtures only.
- **Acceptance criteria:** report environment, workload, CPU/memory/allocations/FDs/queues/ticks/bandwidth/DB/Redis; demonstrate overload recovery within budgets.
- **Tests:** bounded load, soak, burst, dependency degradation, region latency simulation.
- **Metrics:** all benchmark outputs with p50/p95/p99 and missed deadlines.
- **Rollback:** tools target loopback/allowlist only and do not change production.
- **Risks:** mistaking local numbers for capacity; unsafe traffic generation. Keep hard caps.
- **Complexity:** Medium/Large.

## Phase 15 - Production rollout

- **Goal:** deploy incrementally with rollback and data proof.
- **Scope/tasks:** canary/one shard first; explicit migrator; compatibility gate; shadow read/reconciliation; drain/restart; backup; feature flags; operator approval for each cutover.
- **Likely files/modules:** deployment manifests/IaC after provider choice, CI/CD, runbooks, feature configuration.
- **Dependencies:** provider, RPO/RTO, observability, load results.
- **Data migrations:** expand before rollout; contract only after all old binaries are gone.
- **Acceptance criteria:** canary SLO/error/economy invariants; rollback rehearsal; no schema-ahead binary; protected TCP/UDP edge selected if public.
- **Tests:** preproduction full suite, migration/restore, rolling old/new compatibility, failover.
- **Metrics:** login/load/save latency, conflicts, errors, outbox, reconciliation, ownership, tick health.
- **Rollback:** drain, revert compatible binary/config, restore only for proven destructive corruption, forward repair preferred.
- **Risks:** client version fragmentation, origin exposure, irreversible schema contract.
- **Complexity:** Large.

## Phase 16 - Remove legacy persistence and temporary compatibility

- **Goal:** leave one production authority and no permanent dual path.
- **Scope/tasks:** remove production `JsonGameStore`; remove broad `IGameStore` after all callers migrate; archive/drop obsolete views/columns only after measured no-read period; remove raw insecure auth from production; separate capture corpus.
- **Likely files/modules:** `JsonGameStore*`, `IGameStore`, old handlers/config, legacy schema/capture adapters.
- **Dependencies:** complete rollout, retention window, telemetry proving no use.
- **Data migrations:** forward archive/drop with parity and backup gates.
- **Acceptance criteria:** repository production profile cannot select JSON/raw insecure auth; all data loads from feature contracts; zero legacy reads during observation window.
- **Tests:** clean install, upgraded install, rollback-compatible prior release, archive parity.
- **Metrics:** legacy path invocation must remain zero; schema/storage size.
- **Rollback:** restore archived data and compatibility adapter within declared window.
- **Risks:** hidden capture/content or client compatibility dependency.
- **Complexity:** Medium/Large.
