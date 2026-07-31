# 18. Implementation backlog

## 18.1 Prioritized tickets

| ID / ticket title | Purpose | Scope and expected files/modules | Dependencies | Acceptance criteria | Required tests | Observability | Rollback | Complexity | Risk |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| B01A - Inventory schema/build/backup state read-only | Establish evidence without changing history | Read migration rows/checksums, dirty-tree catalog, schema/row invariants and backup restore; publish manifest | None | Exact mismatch list and verified restore report; no DB/source mutation | Read-only history comparison and restore verification | Migration/schema mismatch count | Delete report only | Small | Low |
| B01B - Cut a coherent schema release | Restore release reproducibility | Reconcile intended applied pet migrations/code, repair empty-bootstrap packet metadata, commit/tag matching build, reconcile Compose init path | B01A and owner confirmation of intended WIP | Empty/restored DB reaches exact history; binary/schema compatibility manifest passes | Prefix/checksum/ahead, empty/bootstrap dependency, restore rehearsal, row invariants | Migration version/duration/mismatch | Prior matching tag + verified backup; never rewrite applied checksums | Medium | High |
| B02 - Enforce data-boundary architecture | Prevent new direct coupling | Add ownership ADR and a ratcheting architecture check with an explicit baseline allowlist for existing `_store` callers; define `Application`/`Infrastructure` dependency rules | B01A, section 4 accepted | No new forbidden dependency is added; allowlist shrinks as each slice migrates and cannot grow without review | Build-time namespace/reference ratchet | Current/baseline/new violation counts | Freeze the prior allowlist, not remove the gate | Small | Low |
| B03 - Mandatory disposable PostgreSQL CI | Make persistence evidence trustworthy | Testcontainers/equivalent PG 17 job, fail on skip, repaired empty bootstrap and restored fixture migrations, workflow | B01A; B01B required before the empty path can pass | Every PR runs PG tests and publishes machine-readable result | Migration/repository/concurrency smoke | CI duration/failure category | Keep local env-based runner while CI job is repaired | Medium | Low |
| B04 - Fail-closed storage and security profiles | Remove silent JSON fallback and immediately contain raw auth | `ServerOptions`, `Program.cs`, listener/login composition, appsettings/Compose | B01A and controlled-client compatibility plan | Unknown/missing production provider fails; JSON and raw `LoginOrCreateAccountAsync`/username-only bind run only in an explicit local-development profile | Config/listener/auth startup matrix plus controlled client smoke | Startup rejection reason and raw-profile attempts | Restore controlled local-dev profile only; never expose it as production | Small/Medium | Medium |
| B05 - Extract `IWorldContentReader` | First boundary vertical slice | `PostgresGameStore.WorldSync`, map/NPC/monster initialization, composition | B01B, B02-B03 | Same reviewed content packets/definitions; one authoring input and runtime revision pinned per family | Golden content, source/PG checksum, missing/revision tests | Load latency/missing/fallback | Delegate to legacy store reader | Medium | Medium |
| B06 - Extract consistent `ICharacterSnapshotReader` | Prevent mixed login snapshots | Character store files, short read-only transaction, login handler, hydrators | B01B, B02-B03, pet schema baseline | One closed transaction returns one snapshot/version before hydration; account slot semantics are explicit | Concurrent update/login, old/new contract, preview client | Load latency/query count/failure | Legacy multi-query reader flag | Large | Medium |
| B07 - Resolve legacy operation identity and add command envelope | Separate transport identity from business retry | compatibility spike for shim operation ID/server token/limited semantics, then codecs/handlers/application envelope for one command | B01B, B02 and client compatibility decision | Chosen command has a stable cross-reconnect operation identity or explicitly documented weaker guarantee; auth IDs are server-derived | malformed, legitimate repeated command, duplicate, reconnect, request-hash conflict | command outcomes/duplicates/unsupported legacy retries | Per-command legacy adapter | Medium | Medium |
| B08 - PostgreSQL inbox/outbox foundation | Make retries/events safe | new migrations, Npgsql helpers/worker, operation-specific transaction, versioned/ordered consumer policy | B03, B07 | Same transaction as sample mutation; restart resumes outbox; consumers handle stale/gap ordering correctly | crash at all commit/delivery points, concurrent pollers, v2-before-v1 | backlog/age/retry/poison/gaps | Disable dispatcher; authoritative rows/inbox remain | Large | High |
| B09 - Inventory/currency ledger migration | Protect economy | inventory/crafting/mentor/GM handlers, wallet schema, audits | B08 | Existing valuable commands are idempotent, constrained, audited | races, duplicate, overflow, disconnect-after-commit, reconciliation | economy command/ledger mismatch | Feature-specific compatibility adapter | Large | High |
| B10 - Checkpoint versions and bounded workers **(completed 2026-07-30)** | Keep I/O off ticks and reject stale saves | position/vitals coordinators, background loops, PG columns, task supervisor | B02-B03 | Bounded queues; position transfer requires exactly one affected row; stale owner/revision cannot overwrite; loop faults stop readiness | zero-row/wrong-owner, delay/reorder/crash/queue/critical fault | dirty age, queue, conflicts, heartbeat | Coordinated prior-binary rollback; additive migration retained | Large | High |
| B11 - Character lifecycle/tombstone **(completed 2026-07-30)** | Make create/delete recoverable and retry-safe | character handler/store/schema/audit, confirmed account-slot constraint | B07-B08 | Duplicate-safe create/delete; approved character cardinality; restore window; controlled purge | concurrent/lost-ACK create, slot limit, delete, restore/purge | lifecycle/audit counts | Keep tombstone columns; disable purge | Medium | High |
| B12 - Progression/reward/pet durability **(completed 2026-07-31)** | Close post-combat and pet retry gaps | progression/combat kill projection/zodiac/pet files; non-repeating boot/map-runtime + spawn/death event identity | B08-B10 | One reward per death ID that cannot repeat after restart; the same ID survives retries; intervals/pets retry safely | death retry/restart/collision, interval overlap, pet concurrency | duplicate/lost reward, revision conflict | Slice feature flags | Large | High |
| B13 - Structured logs, traces, readiness **(completed 2026-07-31)** | Operate safely | logging call sites, `Operations`, metrics/exporter/private management endpoint | B02-B03; can start in parallel | No secret/raw production payload logs; actionable readiness/traces | redaction, log flood, exporter down, critical-task fault | implemented B13 signals; deferred section 16 gaps stay explicit | Disable exporter/sink, keep audits | Medium | Medium |
| B14 - Raw authentication retirement **(completed 2026-07-31)** | Close current account-binding risk | login/game handlers, listener profile/config, client secure acceptance | Secure client profile accepted and rollback ready | Production rejects raw; TLS auth/game bind passes | credential/ticket forgery/replay/expiry/client smoke | auth outcomes/raw attempts | Controlled dev-only profile | Medium | High |
| B15 - PostgreSQL player ownership fence **(completed and verified 2026-07-31)** | Prepare safe scale-out | authoritative PG ownership row, monotonic `owner_generation`, conflicting transaction locks/CAS, session service, registry boundary | B06, B10 | Every valuable transaction locks/validates the owner row for its full mutation; transfer takes the conflicting lock; two owners cannot both commit; versioned async results revalidate owner generation | check-then-mutate race, child-row mutation, split-brain, stale higher token after cache loss, pause/reconnect/transfer | conflicts/fence generations | Coordinated B14 application rollback; retain the additive B10 owner columns and generations | Large | High |
| B16 - Redis decision ADR **(completed 2026-07-31: historical defer)** | Avoid premature infrastructure | evidence and ADR 0003 | B13-B15 | Defer correctly reflected the then-known one-process target | Evidence review | candidate capacity gaps | Documentation revert | Small | Low |
| B17 - Redis coordination **(completed and verified 2026-07-31; opt-in, deployment gated)** | Coordinate authoritative processes | Async tickets/admissions, routes, presence, PG-fenced leases | B15, B18C2, ADR 0005 | State is atomic/bounded; Redis loss cannot lose value | restart/slow/expiry, replay, PG fence | finite Redis/lease/route signals | Drain to one local authority | Large | High |
| B18A/B - Realm/instance identity and fair mailboxes **(completed 2026-07-31)** | Local scale-out foundation | typed IDs, placement/runtime directory, instance sessions, owner mailboxes, bounded fanout | ADR 0004, B02, B10 | Isolated single-owned instances; I/O outside owner commands | lifecycle/isolation/transfer/overload | instance/queue/fanout | Tempest default-map bridge | Large | High |
| B18C1 - Local opaque TCP relay **(completed 2026-07-31)** | Prove a process boundary | `Networking/RelayGateway`, worker node/public port, Docker-free smoke | B18A/B | One bounded relay reaches one private combined worker; no semantic claims | in-process plus real two-process smoke | finite snapshot/meter | Omit relay mode | Medium | Medium |
| B18C2 - Semantic gateway/backhaul **(completed and verified 2026-07-31)** | Move session/routing authority to the gateway | loopback auth edge, mTLS worker hop, exact realm/map/instance/node route and admission identity | B18C1, B15 | Single-use login; exact route/drain/replay/capacity policy | focused and real mTLS multi-worker checks | bounded gateway/route signals | Drain to B18C1/direct worker | Large | High |
| B19 - Reconciliation/restore drills **(completed local foundation 2026-07-31)** | Detect drift | worker/tools/runbook/CI | B08-B12 | Bounded report/lease repair and isolated restore; production RPO/RTO gated | interruption, repair, restore | mismatch/repair/restore time | Disable worker | Medium | Medium |
| B20 - Remove JSON/broad store/legacy capture dependency | Finish migration | `JsonGameStore*`, `IGameStore`, config, content/capture adapters | All callers migrated and observation window | One production authority; no legacy reads | clean/upgraded install, archive parity | legacy-call counter zero | Restore compatibility release/archive | Large | Medium |
| B21 - MongoDB reconsideration ADR, conditional | Enforce evidence threshold | Documentation/prototype only if real document feature exists | Scheduled feature with measured JSONB limitation | Section 8 evidence and operational plan approved | workload/index/backup prototype | workload/cost/SLO | Reject/remove prototype | Small decision / Large adoption | High |

**B06 completed 2026-07-29:** [implementation evidence](../data-architecture-b06-character-snapshot-reader-20260729.md)
records the consistent PostgreSQL/JSON character snapshot readers, login
hydration boundary, bounded PostgreSQL snapshot fingerprint, account-session
replacement and cancellation cleanup, application-level single-slot mutation
guard, contract/concurrency coverage, and architecture-ratchet reduction. The
slot guard has no schema-level uniqueness constraint; that durable lifecycle
decision remains B11 work.

**B07 completed 2026-07-29:** [implementation evidence](../data-architecture-b07-command-envelope-20260729.md)
records the talent-upgrade command selection, stable expected-rank transition
identity, canonical request hash, server-derived principal boundary, raw
legacy compatibility, rejected pet/forge candidates, and the exact B08
PostgreSQL inbox/outbox handoff. B07 itself does not claim durable
cross-process deduplication; that work was explicitly deferred to B08.

**B08 completed 2026-07-29:** [implementation evidence](../data-architecture-b08-command-inbox-outbox-20260729.md)
records the atomic PostgreSQL talent mutation, permanent audit/inbox result,
exact duplicate replay, versioned outbox, strict/latest-wins dispatcher,
bounded one-at-a-time leasing, retry/poison/gap/stale/lease recovery,
database-enforced event/checkpoint state transitions, supervised runtime
composition, and low-cardinality telemetry. The JSON provider remains a local
compatibility path without durable inbox/outbox semantics. B15 now supplies
the PostgreSQL player-ownership fence required before safe multi-process
ownership.

**B09 completed 2026-07-30:** the
[closure evidence](../data-architecture-b09-closure-20260730.md) records the
PostgreSQL economy ledgers, durable native command families, exact replay,
finite mutation audit, frozen-tree gates, limitations, and B10-B12 handoff.
The entry-point roadmap links every detailed B09 increment. Raw TCP remains
weaker and is not replay-safe.

**B10 completed 2026-07-30:** the
[implementation evidence](../data-architecture-b10-character-checkpoints-20260730.md)
records independent position/vitals revisions, the PostgreSQL owner UUID and
monotonic generation fence, exact replay/conflict/stale-owner results, the
bounded process-wide coalescing worker, finite direct barriers and retries,
supervised readiness/shutdown, runtime lifecycle integration, configuration,
metrics, and PostgreSQL/JSON authority distinction. The production guarantee
is PostgreSQL-backed; JSON ownership remains process-local compatibility.
**B11 completed 2026-07-30:** the
[implementation evidence](../data-architecture-b11-character-lifecycle-20260730.md)
records the database-enforced `SingleCharacterV1` active slot, recoverable
tombstones, 30-day restore plus 7-day purge grace, account-owned monotonic
lifecycle revision, secure client operation families 22/23, service-only
restore/purge families 24/25, and atomic PostgreSQL inbox/audit/outbox
evidence. The production guarantee is the secure PostgreSQL path. Raw TCP and
JSON remain explicitly weaker compatibility paths. Its B12 handoff has since
been completed and is recorded below.

**B12 completed 2026-07-31:** the
[implementation evidence](../data-architecture-b12-progression-reward-pets-20260731.md)
records the globally unique and immutable monster-death reward settlement,
server-sequenced online progression intervals, secure native pet operation
families 2/26/27, retry-safe pet hatch/level/presence mutations, and family-26
inventory ledger/reconciliation integration. PostgreSQL is the production
authority; JSON and unidentified raw TCP remain weaker local compatibility
paths. Failed disconnect checkpoints have a bounded process-owned retry
handoff; a full process crash can still lose only its uncommitted interval
tail. A crash before the lethal runtime event reaches PostgreSQL remains an
explicit combat-journal gap. B15 now supplies the shared player-ownership
fence required for safe multi-process ownership.

**B13 completed 2026-07-31:** the
[implementation evidence](../data-architecture-b13-observability-readiness-20260731.md)
records bounded logs/metrics/traces, readiness, task supervision, private
management endpoints, authenticated drain, health probes, dashboards, and
runbooks. PostgreSQL outage/recovery passed without a server restart;
upstream telemetry and L3/L4 mitigation remain deployment responsibilities.

**B14 completed 2026-07-31:** the
[implementation evidence](../data-architecture-b14-raw-auth-retirement-20260731.md)
records fail-closed defaults, explicit loopback-only `legacy-raw` rollback,
secure activation, credential clearing, TLS/ticket checks, and sealed client
acceptance. It claims neither production deployment nor upstream protection.

**B15 completed and verified 2026-07-31:** the
[implementation evidence](../data-architecture-b15-player-ownership-fence-20260731.md)
records the PostgreSQL-issued owner UUID/generation, transaction-wide fence
validation for valuable writes, post-commit/session/ECS revalidation, and
bounded metrics. Its final gate passed **263 managed checks** and **42
PostgreSQL checks across 4 scenarios**.

**B16-B18C rollout:** [B16/B17](../data-architecture-b16-b17-redis-decision-20260731.md)
records the historic defer; [B18A](../data-architecture-b18a-realm-instance-foundation-20260731.md),
[B18B](../data-architecture-b18b-instance-routing-mailboxes-20260731.md),
[B18C1](../data-architecture-b18c1-local-relay-gateway-20260731.md), and
[B18C2](../data-architecture-b18c2-semantic-gateway-backhaul-20260731.md)
complete the local identity/owner/gateway boundary. Opt-in
[B17](../data-architecture-b17-redis-coordination-20260731.md) is verified;
public deployment remains gated.

**B19 completed locally:** see its
[evidence](../data-architecture-b19-reconciliation-restore-20260731.md) and
[runbook](../operations/b19-reconciliation-restore-runbook.md). Production
PITR, RPO/RTO, and repair authorization remain gated.

## 18.2 Dependency and parallelization notes

- **Blocks most PG/application work:** B01B, B02, B03, B07, and B08.
- **Completed prerequisite for scale-out/Redis and multi-owner writes:** B15.
- **Can run in parallel after B01A:** B02 boundary rules, B04 configuration/security hardening, and initial B13 logging/readiness. B03 CI scaffolding can start, but its empty-bootstrap gate cannot pass until B01B repairs the baseline.
- **Can run in parallel after B08:** B09 economy, B11 lifecycle, and portions of B12 progression/pets, provided migrations are ordered and aggregate ownership does not overlap.
- **Completed opt-in scale-out boundary:** B17 disposable Redis coordination;
  PostgreSQL remains durable. Public activation still needs provider,
  SLO/cost, region, and measured staging approval.
- **Requires a new architectural decision first:** B21 for MongoDB; character deletion retention; reward failure semantics.
- **Wait until gameplay exists:** quest schema/progress, real guilds, party, trade, mail, auction, friends, achievements, housing, player-generated content, seasonal systems. Their illustrative placement in section 11 is not an implementation request.

## 18.3 Architectural mistakes to avoid

- Persisting every ECS component or a complete ECS world snapshot.
- Treating mutable `GameCharacter`, an ECS projection, JSON file, and PG row as simultaneous authorities.
- Calling Npgsql/Redis directly from arbitrary packet handlers or ECS systems.
- Retaining one universal `IGameStore`/`IRepository<T>` that hides transaction semantics.
- Acknowledging item/currency/progression success before PG commit.
- Adding Redis before ownership/fencing and then relying on a lock for economy correctness.
- Storing valuable state only in Redis.
- Writing PG and Redis/Mongo independently in one request.
- Introducing MongoDB as a miscellaneous JSON store.
- Using account names, IP addresses, UDP ports, or runtime ECS IDs as durable identity.
- Assuming TCP and UDP relative ordering.
- Using packet replay protection as a substitute for business idempotency.
- Blocking a fixed-step ECS/map loop on database calls or sequential client fanout.
- Letting background, persistence, logging, limiter, or retry queues grow without bounds.
- Editing an applied migration or allowing a database to be ahead of its binary.
- Running multiple server/content versions that overwrite mutable startup seeds.
- Treating skipped DB integration tests as passed.
- Logging raw payloads, credentials, tickets, keys, or high-cardinality player/network labels.
- Claiming ordinary autoscaling/firewalls replace upstream arbitrary-UDP/TCP DDoS protection.
- Designing tables and infrastructure for hypothetical features before their invariants and access patterns exist.
