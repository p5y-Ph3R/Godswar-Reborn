# B13 observability and readiness incident runbooks

## Scope

These runbooks cover the application management plane, PostgreSQL readiness,
critical workers, outbox/checkpoint state, telemetry pressure, and B12 durable
operations.

For volumetric UDP/TCP attacks, SYN exhaustion, origin exposure, upstream
provider failure, authentication abuse, or network-key compromise, use
[`../network-infrastructure-phase5a-operations.md`](../network-infrastructure-phase5a-operations.md).
An application dashboard cannot observe traffic scrubbed before the origin and
cannot replace upstream arbitrary-port L3/L4 protection.

No threshold in this document is a production capacity guarantee.

## Safe management access

Use only the configured loopback/private management address:

```text
GET  /live
GET  /ready
GET  /metrics
GET  /traces
POST /drain  (only after explicit drain-token configuration)
```

Never forward these routes through login/game ingress. Never paste a drain
token into chat, shell history, tickets, logs, query strings, or screenshots.
Supply it through the approved local secret mechanism.

The checked-in Compose profiles do not set
`GODSWAR_MANAGEMENT_DRAIN_TOKEN_FILE`. HTTP `/drain` therefore fails
authentication by design. This does not disable graceful shutdown:
`docker compose stop server` sends `SIGTERM`, which enters the same bounded
drain coordinator before process shutdown. Do not use `SIGKILL` for an
ordinary stop.

Responses and metric labels contain finite reason codes. If a response
contains a connection string, credential, packet data, player identity,
endpoint, exception message, or operation UUID, preserve access-controlled
evidence and treat it as an observability privacy defect.

## Common triage

1. Confirm whether the alert is application, host/container, database, or
   upstream-provider state.
2. Check `/live`, then `/ready`; do not repeatedly invoke `/drain`.
3. Compare `godswar_server_operations_liveness`,
   `godswar_server_operations_readiness{phase,reason}`,
   `godswar_server_operations_critical_tasks{task,state}`,
   queue/backlog age, checkpoint heartbeat, simulation deadlines, and
   PostgreSQL reachability.
4. Check whether a deployment, migration, certificate/key rotation, or
   configuration change preceded the event.
5. Protect established sessions and authoritative data before increasing new
   admission.
6. Avoid verbose diagnostics during overload. Use bounded structured events
   and sampled traces.
7. Record UTC start/end, build/schema/content version, player impact, recovery
   actions and any durable reconciliation required.

## PostgreSQL unavailable or slow

Expected behavior:

- `/live` normally remains healthy;
- `/ready` returns `503` with a finite PostgreSQL reason;
- new valuable commands fail closed rather than reporting success;
- simulation work does not wait indefinitely for PostgreSQL;
- established sessions are preserved only within documented safety bounds;
- checkpoint/outbox/retry state stays bounded.

Response:

1. Confirm PostgreSQL health from the private database path and provider/host
   telemetry. Do not expose the database port publicly.
2. Check connection, command and pool-wait latency without printing the
   connection string. Use the fixed `_bucket` series for bounded p95/p99
   estimates where the relevant millisecond histogram exists.
3. Stop new-session admission if readiness has not already done so.
4. Do not bypass PostgreSQL with JSON or an in-memory value mutation.
5. Do not increase pool size until database capacity, per-instance limits and
   total instance count are understood.
6. Restore connectivity or fail over using the provider-approved process.
7. Require consecutive healthy readiness probes before restoring admission.
8. Verify checkpoint dirty age, progression retry depth, outbox backlog/age,
   command failures and simulation deadlines return toward baseline.
9. Run report-only reconciliation if any value operation had an uncertain
   outcome.

Escalate immediately if a client was told that a valuable mutation succeeded
without a durable receipt.

## Checkpoint coordinator not ready

1. Check coordinator state, heartbeat age, queue depth, active writes, scheduled
   retries and oldest dirty age.
2. Correlate with PostgreSQL latency and command timeouts.
3. Keep readiness false while the coordinator cannot accept ordinary work.
4. Do not discard or silently supersede a newer pending revision.
5. If retry age is exhausted, drain the instance and preserve the finite
   failure reason.
6. After recovery, flush/reload a representative position and vitals
   checkpoint and verify revision monotonicity.

## Outbox backlog, gap or poison

1. Check `godswar_outbox_backlog`,
   `godswar_outbox_oldest_age_seconds`, retry, gap and poison counters.
2. Identify the finite consumer family; never put aggregate/event IDs into a
   metric label or ordinary log.
3. Confirm PostgreSQL health and the named outbox worker heartbeat.
4. For a retry, preserve the existing lease, attempt count and ordering
   policy.
5. For a sequence gap, inspect durable rows read-only. Do not skip a strict
   event merely to clear the alert.
6. For poison, preserve the row and failure evidence. Use a reviewed,
   idempotent repair or replay procedure.
7. Verify consumer checkpoints and derived projections before clearing the
   incident.

The authoritative PostgreSQL transaction remains committed even when a
projection is delayed. Never reverse player value by deleting outbox evidence.

## Durable progression retry pressure

1. Check queue depth, oldest age, heartbeat and command outcomes.
2. Confirm failures are dependency or finite precondition failures, not a
   repeated programming fault.
3. Keep the process-owned queue within its hard capacity; do not make it
   unbounded.
4. A reconnect must replay an older exact interval before accounting a newer
   interval for that character.
5. Restore PostgreSQL, then verify exact envelopes drain without overlapping
   or duplicating online duration.
6. Remember that a process crash can lose only the documented uncommitted tail;
   do not claim the process-local handoff is a durable journal.

## Reward, pet or inventory durable-command anomalies

Signals include request-hash conflicts, provider-unavailable outcomes,
duplicate spikes, inventory reconciliation mismatch, and repeated reward
settlement retry.

1. Determine the finite command family and disposition.
2. Inspect durable inbox, audit, ledger, settlement and outbox evidence using
   an authorized database session.
3. Never retry with a new operation/death identity to force success.
4. An exact replay must return the original result without a second value
   mutation.
5. A changed request under the same identity remains a conflict and requires
   security review.
6. Use report-only wallet/inventory reconciliation before any repair.
7. Preserve evidence for suspected duplication, client forgery, operator
   misuse or data corruption.

## Critical task fault or stale heartbeat

1. Identify the finite task and state in
   `godswar_server_operations_critical_tasks{task,state}`.
2. Confirm whether it stopped through normal cancellation/drain or faulted.
3. Check the task's dependency and queue/backlog state.
4. If the task is required for safe admission, keep readiness false.
5. If the supervisor declares the process irrecoverable, allow the host to
   replace it after bounded drain; avoid an uncontrolled restart loop during a
   shared dependency outage.
6. After restart, verify task heartbeat, schema/content compatibility and
   durable recovery before admission.

Do not log the exception message as a task label or structured property. Use a
finite failure code and protected crash diagnostics.

## Management rejection or overload

1. Check
   `godswar_server_operations_management_requests{route,outcome}` grouped
   only by the finite `route` and `outcome` labels.
2. Correlate `unauthorized`, `rejected`, `bad_request`,
   `headers_too_large`, and `overloaded` outcomes with the trusted local
   collector and host telemetry.
3. Confirm the listener is still bound only to exact loopback and is absent
   from every published Compose port.
4. Never log request headers, bearer tokens, endpoints, or arbitrary request
   text to investigate the rejection.
5. Preserve the bounded concurrency, header, deadline, and response limits.
   Do not raise them until a legitimate caller and measured requirement are
   established.

## Log flood, blocked sink or disk pressure

1. Check emitted, sampled and dropped log counters plus host disk/log-driver
   pressure.
2. Confirm repetitive events are being rate-limited and memory remains
   bounded.
3. Disable or isolate the optional sink if necessary; preserve durable
   PostgreSQL audits.
4. Never enable packet hex, credentials, arbitrary chat text or full exception
   logging to investigate the flood.
5. Apply container log rotation and repair the sink/collector.
6. Verify gameplay tick, queue and persistence latency were not blocked by
   logging.
7. Tune sampling only from recorded evidence and keep security/value failures
   visible through finite events.

## Metrics or trace exporter unavailable

1. Confirm the server remains live and gameplay is not blocked.
2. Check bounded exporter-drop counters and local management health.
3. Verify collector reachability through the private management path.
4. Do not expose `/metrics` or `/traces` publicly as a workaround.
5. Disable the exporter independently if it consumes excessive CPU, memory or
   queue capacity.
6. Restore collection and confirm current gauges, not stale counters alone,
   reflect healthy state.

Loss of optional telemetry does not authorize accepting work while an
authoritative dependency is unhealthy.

## Readiness false after deployment

1. Read the finite readiness reason.
2. Verify binary/schema/content compatibility before opening listeners.
3. Check PostgreSQL, checkpoint, outbox, progression-retry and critical-task
   state.
4. Confirm the instance is not intentionally draining.
5. Do not force readiness true or bypass a component check.
6. If the release is incompatible, keep the instance out of service and roll
   back to the prior schema-compatible binary.

## Graceful drain

The default Compose procedure is:

1. Run `docker compose stop server` (with the secure override files too when
   that profile is active).
2. Let Compose send `SIGTERM`; the server marks itself draining and stops new
   login/session admission.
3. Allow bounded network, checkpoint, progression and outbox shutdown work.
4. Observe the configured drain deadline and Compose
   `stop_grace_period`. Preserve durable leases/evidence if a deadline expires.
5. Verify the stopped lifecycle evidence before replacing the process.

Enable HTTP `/drain` only when an operator workflow genuinely requires a
pre-stop drain. Use an operator-owned Compose override outside the repository;
the following contains no secret value:

```yaml
services:
  server:
    environment:
      GODSWAR_MANAGEMENT_DRAIN_TOKEN_FILE: /run/secrets/godswar-management-drain-token
    secrets:
      - source: godswar-management-drain-token
        target: godswar-management-drain-token
        mode: 0400

secrets:
  godswar-management-drain-token:
    file: ${GODSWAR_DRAIN_TOKEN_SOURCE:?Set an absolute host secret-file path}
```

Set `GODSWAR_DRAIN_TOKEN_SOURCE` through the approved untracked operator
environment to an absolute host path. The source file must remain outside
source control and contain 32–256 visible ASCII bytes, optionally followed by
newline characters. Compose mounts the target read-only; the server requires
the configured container path to be absolute. Do not put the token itself in
Compose YAML, an environment variable, a command line, or a shell history.

With HTTP drain explicitly enabled:

1. Invoke it only from the private loopback management context using an
   approved secret-reading wrapper that does not expose the bearer value.
2. Confirm readiness becomes false and repeated drain is idempotent.
3. After the bounded drain response, use the normal orchestrator stop path.

A drain is an operational mutation. It must be auditable without recording its
secret.

## Post-incident verification

- `/live` and `/ready` show the expected state.
- No required task is faulted or stale.
- PostgreSQL transactions and snapshots are healthy.
- Checkpoint dirty age and retry state return toward baseline.
- Outbox backlog/age drains without skipped strict events.
- Progression retry intervals drain exactly once.
- Wallet/inventory/pet/reward reconciliation has no unexplained mismatch.
- Tick deadlines, network queues and established sessions are healthy.
- Logs/traces contain no prohibited data.
- Any threshold/configuration change has an owner, evidence window and rollback
  condition.

Production restore, PITR and declared RPO/RTO drills remain B19/provider work.
