# B19 reconciliation and restore runbook

## Scope and safety boundary

B19 reports drift between PostgreSQL current state and durable evidence and
provides an isolated logical-restore drill. PostgreSQL remains authoritative.
A finding requires investigation, not ledger-driven repair; broad legacy
writers remain until B20. Do not:

- update `character_base`, `character_items`, an economy baseline, or a ledger
  directly from an incident shell;
- delete or rewrite `schema_migrations`;
- treat Redis as a recovery copy of player value;
- clear, deliver, poison, or replay an outbox event solely to make a metric
  green;
- restore a database over a running world; or
- run the disposable gate against `godswar-postgres`, `godswar-server`, a
  public address, or any shared database.

The recovery harness accepts no external target. It creates a uniquely named,
labelled PostgreSQL container on loopback with tmpfs, generated credentials,
and token-scoped databases. Its guards reject live Compose container names.

## Report-only reconciliation

The production application boundary is
`Godswar.Server.Application.Reconciliation`. `ReconciliationOptions` accepts
only `ReconciliationMode.ReportOnly`; any other enum value fails validation.
Its conservative defaults are:

| Control | Default | Valid range |
|---|---:|---:|
| enabled | false | explicit opt-in |
| batch size | 100 | 1-500 |
| maximum characters per run | 5,000 | 1-1,000,000 |
| maximum outbox events per run | 5,000 | 1-1,000,000 |
| poll interval | 300,000 ms | 10,000-86,400,000 ms |
| command timeout | 5,000 ms | 100-60,000 ms |
| run timeout | 30,000 ms | command timeout-600,000 ms |

The worker is bound under `Storage.Reconciliation` in `appsettings*.json`.
Every field is validated by `ReconciliationOptions.Validate()`, and enabling
it while `Storage.Provider` is not PostgreSQL fails startup. Deployments may
override the typed values with:

- `GODSWAR_RECONCILIATION_ENABLED`;
- `GODSWAR_RECONCILIATION_MODE`;
- `GODSWAR_RECONCILIATION_BATCH_SIZE`;
- `GODSWAR_RECONCILIATION_MAXIMUM_CHARACTERS_PER_RUN`;
- `GODSWAR_RECONCILIATION_MAXIMUM_OUTBOX_EVENTS_PER_RUN`;
- `GODSWAR_RECONCILIATION_POLL_INTERVAL_MILLISECONDS`;
- `GODSWAR_RECONCILIATION_COMMAND_TIMEOUT_MILLISECONDS`; and
- `GODSWAR_RECONCILIATION_RUN_TIMEOUT_MILLISECONDS`.

No distributed election exists. In a multi-process realm, enable this worker
on one designated operations process only; otherwise every process repeats the
full scan and metrics. The default remains disabled.

Do not invent aliases or bypass validation. `ReconciliationRunner` opens one
read-only, repeatable-read snapshot. It keyset-selects a bounded page of
character IDs from `public.character_economy_baseline` and
`public.character_base`, then compares only those characters' baseline,
currency/inventory ledger, current character/item, progression, pet, and
purge evidence. It separately keyset-scans `public.outbox_events` and checks
the schema manifest plus published NPC content. The periodic service does not
scan the two global `character_*_reconciliation` views; the disposable
recovery gate checks those views independently.

Results are `Completed`, `Truncated`, or `TimedOut`. Truncated and timed-out
reports are not clean receipts. The periodic runner keeps serialized,
process-local keyset continuation across scheduled invocations. Completed
character, outbox-event, and outbox-position scopes are latched while the
remaining scopes advance; event and position pages alternate under the shared
outbox budget. A complete logical sweep resets all cursors for the next sweep.
Findings are accumulated across the whole sweep, so a clean final page cannot
hide an earlier mismatch. Timeout, failure, or caller cancellation commits no
speculative cursor or finding state.

The worker's first-pass readiness requires one complete logical sweep.
Truncated progress may refresh an already-established heartbeat but cannot
establish first-pass readiness. The continuation is not durable authority; a
process restart safely starts a new sweep from the beginning. The row caps
bound selected characters/events/positions, while statement and run deadlines
bound time. A selected character's accumulated ledger history is not
independently row-capped.

Finding names are a 32-value finite protocol set. They include
wallet/inventory baseline, identity, revision, balance/item, and ledger-chain
mismatches; duplicate slots; orphan templates; reward/pet evidence gaps;
retained characters without purge evidence; outbox poison, expired lease,
sequence, lease, consumer-position, unknown-consumer, and policy mismatches;
schema-manifest mismatch; and NPC publication/count mismatch. Adding a
category requires code and metric review.

Before investigating a mismatch:

1. Confirm the database has exactly 36 migrations through
   `20260731_035_tempest_realm_authority`.
2. Confirm PostgreSQL readiness and outbox/checkpoint worker health.
3. Drain the affected character or realm before considering a mutation.
4. Record a bounded report receipt and the finite mismatch category. Do not
   copy inventory JSON, usernames, packet payloads, connection strings, or
   other player-identifying data into logs or metric labels.
5. Determine whether the mismatch is an identity/baseline problem, a revision
   gap, current-state drift, or an incomplete ledger sequence.
6. Escalate any missing baseline, identity conflict, revision gap, or
   ambiguous source-of-truth case. Those categories are not auto-repairable.

Restarting an interrupted report is safe. Keyset scanning may re-read a
bounded page, but it must not mutate the views or authoritative rows.

The meter is `Godswar.Server.Reconciliation`:

- `godswar_reconciliation_runs_total`, labelled only by finite mode/outcome;
- `godswar_reconciliation_findings_total`, labelled only by category;
- `godswar_reconciliation_rows_scanned_total`, labelled only by
  `characters` or `outbox`;
- `godswar_reconciliation_run_duration_ms`, labelled only by mode/outcome;
- `godswar_reconciliation_repair_attempts_total`, labelled only by finite
  outcome;
- `godswar_reconciliation_repair_rows_total`, labelled only as recovered; and
- `godswar_reconciliation_repair_duration_ms`, labelled only by finite
  outcome.

### Report CLI

The server assembly exposes a non-listening report command:

```powershell
# Inject the connection string through the approved process-secret mechanism.
$evidenceDirectory = [IO.Path]::GetFullPath(
    'artifacts\b19\operator')
New-Item -ItemType Directory -Path $evidenceDirectory -Force |
    Out-Null
$reportPath = Join-Path $evidenceDirectory (
    'reconciliation-' +
    [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') +
    '.json')

dotnet .\src\Godswar.Server\bin\Release\net10.0\Godswar.Server.dll `
  --postgres-reconciliation report $reportPath
$reportExitCode = $LASTEXITCODE
```

`GODSWAR_POSTGRES_RECONCILIATION_CONNECTION_STRING` is mandatory and is the
only connection variable this command accepts. It deliberately does not fall
back to the server's ordinary connection variable. Never put the connection
string in the argument list, report path, console transcript, or evidence.
The CLI optionally accepts the batch, character/outbox maximum, command
timeout, and run timeout `GODSWAR_RECONCILIATION_*` variables listed above; it
does not start the periodic worker.

The report path must be absolute, at most 1,024 characters, and have an
existing parent. The CLI creates it and `<reportPath>.receipt.json` before
opening PostgreSQL. If either exists, it refuses to overwrite evidence.

Exit codes are:

- `0`: the report completed, was not truncated, and had zero findings;
- `1`: findings, truncation, timeout, cancellation, or a runtime failure; and
- `2`: malformed arguments, missing dedicated connection configuration, an
  invalid/missing output directory, or an existing evidence path.

The receipt contains the report filename and SHA-256 of the exact report
bytes. This detects accidental alteration only when the receipt itself is
trusted; it is not a signature, operator authentication, or durable audit.
Retain the exit code with both files and treat missing, empty, truncated,
timed-out, or failed evidence as non-clean.

## Expired outbox lease repair

The ordinary outbox dispatcher already recovers a fully matched expired lease
transactionally. It locks an expired `outbox_events` row together with the
matching `outbox_consumer_positions` row. It then either:

- clears both lease records and reschedules the event with bounded backoff; or
- clears the lease and poisons the event as
  `lease_expired_max_attempts`.

It uses the exact event ID, consumer, lease owner, lease token, and expiry as
compare-and-swap preconditions. A lease that changed concurrently must cause
the operation to fail, not be overwritten.

The only B19 mutation contract is
`IReconciliationRepairer.RecoverExpiredOutboxLeasesAsync(maximumRepairs)`.
It returns a recovered count and whether the caller's bound was reached.
There is no B19 wallet, inventory, pet, progression, content, schema, or
arbitrary outbox repair API.

An explicit invocation needs incident-owner and database-owner approval.
It is appropriate only when all of the following are true:

- the dispatcher and affected consumer are drained;
- the event is not delivered or poisoned;
- the lease is expired according to PostgreSQL time;
- the event and consumer-position lease identities match exactly;
- the aggregate's preceding durable position is understood;
- the consumer's side effect is idempotent by event identity and version; and
- a current backup/recovery point and forward-recovery plan are recorded.

The approved operation may only invoke that bounded repair contract with a
reviewed maximum from 1 through 500. All selected rows are repaired in one
read-committed PostgreSQL transaction; cancellation or a compare-and-swap
conflict rolls the transaction back. Direct SQL is not an approved interface.

The server assembly contains the following repair CLI engine:

```powershell
dotnet .\src\Godswar.Server\bin\Release\net10.0\Godswar.Server.dll `
  --postgres-reconciliation repair-expired-outbox `
  $absoluteNewReportPath `
  --allow-repair `
  --max 100
$repairExitCode = $LASTEXITCODE
```

In addition to the dedicated reconciliation connection string, repair refuses
to run unless every deployed outbox policy value is explicitly supplied:

- `GODSWAR_OUTBOX_BATCH_SIZE`;
- `GODSWAR_OUTBOX_POLL_INTERVAL_MILLISECONDS`;
- `GODSWAR_OUTBOX_LEASE_MILLISECONDS`;
- `GODSWAR_OUTBOX_MAXIMUM_DELIVERY_ATTEMPTS`;
- `GODSWAR_OUTBOX_BASE_RETRY_DELAY_MILLISECONDS`;
- `GODSWAR_OUTBOX_MAXIMUM_RETRY_DELAY_MILLISECONDS`;
- `GODSWAR_OUTBOX_GAP_RETRY_DELAY_MILLISECONDS`; and
- `GODSWAR_OUTBOX_COMMAND_TIMEOUT_MILLISECONDS`.

Copy reviewed values from the drained deployment; do not guess defaults.
`--allow-repair` is an explicit safety acknowledgement, not authentication.
The current CLI does not durably record operator identity, approval, or repair
outcome in PostgreSQL. Its local checksum receipt is not a durable operator
audit, and an abrupt process termination can leave reserved but incomplete
evidence. Therefore production manual repair through this engine is
unsupported and not authorized unless an approved authenticated wrapper
records those facts durably and invokes it with a new absolute evidence path.
Otherwise, allow the supervised dispatcher to use its normal compare-and-swap
recovery.

After repair:

1. Re-run the report-only scan.
2. Confirm the event is either pending without a lease at its new
   `available_at`, or poisoned with the expected finite reason.
3. Confirm the matching consumer position has no stale inflight lease.
4. Resume one dispatcher and watch backlog age, retries, poison count,
   duplicate consumer outcomes, and reconciliation mismatches.
5. Keep the incident open until the consumer checkpoint and authoritative
   projection agree.

There is no safe "undo delivery" rollback because an external side effect may
already have occurred. Roll back the repair feature to report-only, stop the
dispatcher, preserve the rows and audit, and forward-reconcile the consumer.
A database restore is a last-resort corruption recovery coordinated with a
world drain; it is not an ordinary outbox rollback.

## Disposable recovery gate

Build Release first:

```powershell
dotnet restore .\GodswarServer.sln
dotnet build .\GodswarServer.sln `
  --configuration Release `
  --no-restore `
  --nologo
```

Run the self-owned drill:

```powershell
$recoveryReport = 'artifacts\b19\recovery-gate-' +
  [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') +
  '.json'
.\tools\InvokeB19PostgresRecoveryGate.ps1 `
  -PostgresImage 'postgres:17.9-alpine' `
  -ReportPath $recoveryReport
```

Do not enable shell tracing or print the container environment. The generated
PostgreSQL password is never written to the result. The tool exclusively
creates and holds the report before Docker access. It refuses an existing
path, so every run needs a new timestamped or otherwise unique artifact name.

The gate:

1. creates `reborn-b19-<token>` with the owner label
   `com.reborn.test-scope=b19-postgres-recovery`;
2. publishes one random port on `127.0.0.1` only and uses tmpfs for `PGDATA`;
3. creates only `godswar_b19_<token>_source` and `_restored`;
4. runs `PostgreSQL bounded economy reconciliation` against the source;
5. verifies the exact migration head and zero unexplained mismatch;
6. creates a serializable custom dump with no owner or privilege records;
7. records dump SHA-256, byte count, and duration;
8. restores with `pg_restore --exit-on-error --no-owner --no-privileges`;
9. compares the source and restored canonical SHA-256 logical fingerprints;
10. runs `PostgreSQL restored reconciliation verification`; and
11. removes the dump, both databases, and its labelled container on success or
    failure.

A skipped protocol check is a failure. The Release protocol-check assembly
must already exist.

## Machine result and acceptance

The report records `sourceTreeDirty`; a dirty local tree is provenance, not a
failure. CI and release evidence should record `false`. The `sourceCommit`
field names the checked-out commit and must not be interpreted as including
uncommitted changes.

Acceptance requires:

- `status` is `passed`;
- both required checks and both scenarios passed;
- source and restored migration state are 36 through the expected head;
- `walletUnexplainedMismatches` and
  `inventoryUnexplainedMismatches` are `0`, while both proven-purge counts
  are `1` for this fixture;
- source and restored logical fingerprint SHA-256 values are identical;
- dump SHA-256 is present and `dumpBytes` is positive;
- dump, restore, and verified-ready durations are present; and
- `cleanup.status` is `passed` with no cleanup errors.

The receipt intentionally contains:

- `logicalRpoLostTransactions = 0` only after the source/restored fingerprint
  comparison and restored verification pass;
- `logicalRpoScope = "quiesced synthetic snapshot only"`;
- `productionRpoClaim = false`; and
- `productionRtoClaim = false`.

This means only that the static synthetic state captured by the logical dump
was identical after restore. It is not a claim about writes that happen after
the snapshot, WAL retention, point-in-time recovery, provider failover,
regional loss, production data volume, or production recovery time.
Failed receipts leave `logicalRpoLostTransactions` null unless that exact
observation was already verified. Likewise, top-level migration count/head
remain null until the source migration state has been read and validated; do
not interpret a null as the expected value.

## Interrupted drill and cleanup

The gate arms cleanup before `docker run`, so it also handles a partially
successful container start. Cleanup lists containers and matches the exact
generated name, validates the exact owner's label before removal, removes it,
then lists again to prove that exact name is absent. A Docker list, inspect, or
removal error fails cleanup closed; it is never treated as proof of absence.
On cancellation, allow the `finally` cleanup to finish before starting another
run.

If the host or shell was forcibly terminated, list only B19-labelled
containers:

```powershell
docker ps --all `
  --filter 'label=com.reborn.test-scope=b19-postgres-recovery' `
  --format '{{.Names}}|{{.Status}}'
```

Every returned name must match `^reborn-b19-[a-f0-9]{12}$`. Inspect the owner
label again before removing a leftover:

```powershell
$labelsJson = docker inspect `
  --format '{{json .Config.Labels}}' `
  '<exact-reborn-b19-name>'
if ($LASTEXITCODE -ne 0) { throw 'Docker inspect failed.' }
$labels = $labelsJson | ConvertFrom-Json -ErrorAction Stop
if ($labels.'com.reborn.test-scope' -cne 'b19-postgres-recovery') {
  throw 'Container is not owned by B19.'
}
```

Only that exact label authorizes removal of the exact disposable name:

```powershell
docker rm --force --volumes <exact-reborn-b19-name>
```

Never substitute a variable, wildcard, Compose project, `godswar-postgres`,
or `godswar-server`. The tmpfs database and remote dump disappear with the
owned container. A failed cleanup receipt remains a failed gate until the
exact leftover is verified absent.

## Restore validation and promotion boundary

The disposable restore is validation evidence, not a promotable database.
Before a real restore can serve players, an approved provider runbook must:

1. select the intended encrypted backup and verify its immutable receipt;
2. restore into an isolated replacement database, never over the source;
3. validate exact migration IDs and checksums;
4. run migrations only through the repository migration runner;
5. run bounded reconciliation and classify every mismatch;
6. verify content revision, ownership fences, inbox/outbox state, and
   character snapshot reads;
7. keep listeners unready while validation is incomplete;
8. rehearse application rollback and forward recovery; and
9. switch traffic only after database, game, security, and incident owners
   approve.

Redis coordination state is reconstructable and does not determine player
data RPO. Invalidate old tickets/leases according to the B17 failover policy
instead of restoring Redis as player authority.

## Alerts and response

Alert on finite, low-cardinality signals for:

- any wallet or inventory reconciliation mismatch;
- report scan failure, timeout, or excessive scan age;
- expired outbox lease retry or poison outcomes;
- outbox poison count or oldest-pending age above the approved baseline;
- repair conflict, duplicate repair, or repair without a matching audit;
- source/restored fingerprint mismatch;
- migration count/head mismatch;
- nonzero recovery-gate cleanup errors; and
- restore/verified-ready duration regression against prior comparable local
  drills.

Do not label metrics with character IDs, account IDs, event IDs, lease owners,
IP addresses, or attacker-controlled strings. A mismatch alert is not an
authorization to repair.

## Remaining production work

B19 provides bounded reconciliation and a repeatable local logical-restore
drill. Production still requires a selected PostgreSQL provider and approved:

- an authenticated wrapper with a durable operator audit before manual repair;
- signed or otherwise authenticated immutable report evidence;
- one designated operations process or a distributed worker lease before
  multi-process activation;
- backup frequency and retention;
- encryption and off-host/region isolation;
- WAL archiving and PITR;
- declared business RPO and RTO;
- production-sized restore and reconciliation rehearsal;
- credential and corruption recovery; and
- scheduled restore evidence with an owner and alerting.

Until those exist and pass, no local B19 timing or zero-loss observation may
be represented as a production guarantee.
