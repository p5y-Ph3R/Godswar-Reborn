# B19 reconciliation and restore drills

Status: repository and local/CI foundation completed and verified on
2026-07-31; production backup policy, PITR, signed evidence, and declared
RPO/RTO remain deployment gates

## Outcome

B19 adds a bounded, report-only PostgreSQL reconciliation service and a
disposable logical-backup restore drill. It does not create another source of
truth:

```text
PostgreSQL authority
  -> read-only repeatable-read reconciliation snapshot
       -> finite mismatch categories + bounded telemetry
  -> optional, explicit expired-outbox-lease CAS recovery only

disposable PostgreSQL 17.9
  -> migrate + seed + reconcile
  -> serializable custom dump + SHA-256
  -> isolated restore
  -> migration/reconciliation/fingerprint verification
  -> proven cleanup
```

PostgreSQL remains authoritative for player value and durable messaging.
Redis remains disposable coordination state. MongoDB is neither introduced
nor required.

This milestone closes the implementable local portions of roadmap Phases 12
and 13. It does not claim production-scale recovery, provider failover, WAL
point-in-time recovery, or a business RPO/RTO.

## Authority and repair boundary

Normal operation is `ReconciliationMode.ReportOnly`, disabled by default.
The worker and CLI never update character, inventory, wallet, progression,
pet, content, migration, inbox, or outbox state while reporting.

The only mutation exposed through `IReconciliationRepairer` is bounded
recovery of already-expired outbox leases. `PostgresExpiredOutboxLeaseRepairer`
delegates to the existing dispatcher transition, which:

- locks the event and matching consumer position;
- requires exact event, consumer, owner, token, and expiry identity;
- clears a fully matched expired lease and applies bounded retry policy, or
  poisons it after the configured maximum; and
- rolls back on cancellation or compare-and-swap conflict.

There is deliberately no automatic player-value repair. The broad legacy
store still has compatibility writers until B20; reconstructing current
balances from only native ledgers could erase legitimate legacy changes.
Every value mismatch therefore requires classification and a feature-specific
forward repair.

The repair CLI engine is not an authorized production operator surface by
itself. Production use requires an authenticated wrapper and durable approval,
operator, request, and outcome audit. Its local SHA-256 receipt provides
integrity evidence only; it is not a signature or authentication.

## Implementation boundaries

| Concern | Repository implementation |
| --- | --- |
| Application contracts/options/categories | `src/Godswar.Server/Application/Reconciliation/ReconciliationContracts.cs` |
| Bounded logical sweep | `ReconciliationRunner.cs` and `ReconciliationRunner.Scanning.cs` |
| Finite metrics | `ReconciliationMetrics.cs` |
| Shared PostgreSQL snapshot | `Infrastructure/Reconciliation/PostgresReconciliationReader.cs` |
| Character/economy checks | `PostgresReconciliationSnapshot.Characters.cs`, `.CharacterSql.cs`, and `.LedgerChains.cs` |
| Outbox checks | `PostgresReconciliationSnapshot.Outbox.cs` and `.Positions.cs` |
| Schema/content checks | `PostgresReconciliationSnapshot.Manifest.cs` |
| Scheduled worker | `PostgresReconciliationWorker.cs` |
| Scoped repair | `PostgresExpiredOutboxLeaseRepairer.cs` and `PostgresOutboxDispatcher.Repair.cs` |
| Runtime composition | `ServerReconciliationComposition.cs`, `PostgresApplicationDataRuntime.cs`, and `CriticalTaskSupervisor.cs` |
| Readiness | `ServerReadinessMonitor.cs` |
| Non-listening CLI | `Operations/PostgresReconciliationCommand.cs`, `Infrastructure/Reconciliation/PostgresReconciliationCommandRuntime.cs`, and `ServerStartupCommandDispatcher.cs` |
| Recovery gate | `tools/InvokeB19PostgresRecoveryGate.ps1`, `B19PostgresRecoveryGate.Helpers.ps1`, and `B19PostgresRecoveryGate.Reconciliation.ps1` |
| CI | `.github/workflows/phase5a-network-gate.yml` |
| Operations | `docs/operations/b19-reconciliation-restore-runbook.md` |

The periodic worker reuses the process-wide `NpgsqlDataSource`. It is a
supervised critical task and performs no work on a simulation tick. Each
invocation opens one read-only, repeatable-read transaction with explicit
statement and run deadlines.

## Bounded logical sweep

Character IDs, outbox event IDs, and the composite outbox consumer-position
key use keyset pagination. Character and outbox row budgets are independently
bounded; event and position pages alternate under the shared outbox budget.
Completed scopes are latched so later scheduled invocations continue the
unfinished scopes rather than repeatedly scanning the first page.

The continuation is process-local operational state, not authority. A timeout,
failure, or caller cancellation does not commit speculative cursor progress.
After all scopes reach their end, the logical sweep completes and cursors
reset for the next sweep. Finite findings are accumulated across every
segment of that sweep so a clean final page cannot hide drift found by an
earlier truncated segment.

Every invocation still verifies the exact migration manifest and published
NPC content. A schema-manifest mismatch stops the data scan, preserves the
previous continuation, and returns a non-clean truncated report. An enabled
worker becomes ready only after its first complete logical sweep; truncated
progress can refresh an already-established heartbeat but cannot establish
first-pass readiness.

The selected character count is capped. A selected character's historical
ledger rows are bounded by statement/run time rather than a second row cap;
this is an explicit load-test item before production activation.

## Finite finding protocol

The 32 code-defined categories are:

- wallet: missing character/baseline, identity, revision gap/mismatch,
  balance mismatch, and ledger-chain mismatch;
- inventory: missing character/baseline, identity, baseline snapshot,
  revision gap/mismatch, item mismatch, duplicate slot, orphan template, and
  ledger-chain mismatch;
- progression/pets: reward revision/evidence gaps, pet presence conflict,
  and pet stream evidence gap;
- lifecycle: a retained character without durable purge evidence;
- outbox: poison, expired lease, sequence gap, lease mismatch, unknown
  consumer, policy mismatch, and consumer-position mismatch;
- schema: migration-manifest mismatch; and
- content: NPC publication and actor/dialogue count mismatch.

Hard-purged characters with the required durable lifecycle audit,
command-inbox, or outbox evidence are not reported as retained drift.
Strict-consumer pending gaps and permitted latest-wins version jumps are
classified according to the shared `PostgresOutboxConsumerCatalog`, avoiding
policy-specific false positives.

## Configuration and readiness

`Storage.Reconciliation` is present in `appsettings.json` and
`appsettings.docker.json` with `enabled=false`. Its validated controls are:

- batch size: 1-500;
- maximum characters and outbox rows per invocation: 1-1,000,000 each;
- poll interval: 10 seconds-24 hours;
- command timeout: 100 milliseconds-60 seconds; and
- run timeout: command timeout-10 minutes.

Enabling reconciliation with a non-PostgreSQL provider fails startup.
Environment overrides use only the documented
`GODSWAR_RECONCILIATION_*` names. The report CLI additionally requires the
dedicated `GODSWAR_POSTGRES_RECONCILIATION_CONNECTION_STRING`; it never falls
back to the ordinary server connection variable.

There is no distributed worker election. In a multi-process realm, enable the
periodic worker on exactly one designated operations process; enabling it on
every game worker duplicates full-database scans and metrics. A future
deployment may add a PostgreSQL/Redis lease, but the checked-in default remains
disabled.

Both report and repair commands reserve a new absolute report path and
receipt path before database access. Existing evidence is never overwritten.
Report exit code `0` requires a complete, non-truncated, zero-finding result.
Findings, timeout, truncation, cancellation, or runtime failure are nonzero.

## Observability

The finite `Godswar.Server.Reconciliation` meter contains:

- `godswar_reconciliation_runs_total` by finite mode/outcome;
- `godswar_reconciliation_findings_total` by finite category;
- `godswar_reconciliation_rows_scanned_total` by finite scope;
- `godswar_reconciliation_run_duration_ms` by finite mode/outcome;
- `godswar_reconciliation_repair_attempts_total` by finite outcome;
- `godswar_reconciliation_repair_rows_total` with finite recovered outcome;
  and
- `godswar_reconciliation_repair_duration_ms` by finite outcome.

No player, account, character, event, lease, network, exception, or arbitrary
text value is a metric label. Dashboard queries and alerts are in
`docs/operations/b13-dashboard-queries.md` and
`operations/prometheus/godswar-server-alerts.yml`.

## Disposable recovery gate

`InvokeB19PostgresRecoveryGate.ps1` accepts no database host, live container,
or production target. It:

1. exclusively creates a new report file before starting Docker;
2. creates one random `reborn-b19-<token>` PostgreSQL 17.9 container with an
   exact owner label, loopback random port, tmpfs data, and unprinted random
   credential;
3. creates token-scoped source and restore databases;
4. runs all 36 migrations through
   `20260731_035_tempest_realm_authority`;
5. seeds and publishes the representative content/economy fixture;
6. requires clean bounded reconciliation;
7. produces a serializable custom dump, positive size, and SHA-256;
8. restores into the isolated database;
9. compares a canonical logical fingerprint covering migrations, characters,
   items, economy evidence, inbox/audit/outbox/checkpoints, rewards, pets,
   NPC actors, and dialogue;
10. reruns reconciliation against the restored database; and
11. proves cleanup of the exact labelled container.

The machine report records source-tree dirtiness, image digest/version,
migration head, reconciliation outcome, dump hash/size, logical fingerprints,
timings, cleanup, and failure category. `logicalRpoLostTransactions=0`
describes only the quiesced synthetic snapshot after exact fingerprint
comparison. `productionRpoClaim` and `productionRtoClaim` remain false.

The tool rejects an existing report path without starting a container. Its
failure-path drill also proves labelled-container cleanup. It never starts,
stops, restarts, connects to, or removes `godswar-server` or
`godswar-postgres`.

## Verification

Final B19 closeout results:

- Release build: passed with 0 warnings and 0 errors;
- managed protocol catalog: 294 passed, 0 failed;
- focused B19 checks: 6 passed, 0 failed;
- data-boundary ratchet: baseline/current Npgsql references both 330, with
  zero new debt, stale debt, or rule violations;
- disposable PostgreSQL recovery gate: 2 checks and 2 scenarios passed in
  17,134 ms; 36 migrations reached the exact expected head;
- fixture reconciliation: 0 wallet and 0 inventory unexplained mismatches,
  with the exact 1/1 proven-purge rows;
- dump/restore: 669,673 bytes, 376 ms dump, 547 ms restore, and 1,542 ms
  restored verification;
- source/restored logical fingerprint:
  `7253BAA3889D2F91F2E7AD75DF3C970A1660F01BC64C85F02BDB58081C02E99F`;
- observed synthetic logical loss: 0, while both production RPO/RTO claims
  remained false; and
- live `godswar-server` and `godswar-postgres` container IDs remained
  unchanged and healthy; the gate left no B19 container.

These results are local/CI evidence on the recorded host and synthetic
fixture, not a capacity or production-recovery guarantee.

## Rollback

Runtime rollback is configuration-only: leave
`Storage.Reconciliation.Enabled=false` or disable the supervised worker.
Reporting has no durable mutation. Preserve incomplete evidence and rerun to
a new path.

For repair, drain the dispatcher first. There is no generic "undo delivery"
because an external idempotent consumer may already have acted. Stop repair,
preserve event/position rows, run report-only reconciliation, and
forward-reconcile the affected consumer. PostgreSQL remains authoritative.

The recovery gate's databases are disposable and never promotable. Its
rollback is exact labelled-container cleanup plus preservation of the
machine report.

## Remaining production gates

B19 does not close:

- a selected PostgreSQL provider's encrypted backup frequency/retention;
- WAL archiving, PITR, off-host/region copies, and provider failover;
- declared business RPO/RTO and production-volume restore rehearsals;
- signed or otherwise authenticated immutable reconciliation reports;
- an authenticated repair surface with durable approval/operator audit;
- a distributed election/lease, or an enforced single designated worker,
  before periodic reconciliation is enabled across multiple processes;
- production-sized history-query, WAL, lock, and pool impact measurements;
- scheduled restore ownership, alerts, and long-term evidence retention; or
- B20 retirement of broad compatibility writers and legacy persistence.

Until those pass in an authorized staging environment, B19 must be described
as a completed repository/local foundation, not production disaster-recovery
readiness.
