# B20A legacy persistence inventory and retirement ratchet

Status: completed local architecture foundation on 2026-07-31; no runtime
persistence behavior, database schema, player data, or deployment was changed

## Outcome

B20A establishes the exact starting line for retiring the remaining broad and
legacy persistence architecture. The repository now fails its managed
architecture gate if legacy usage grows, moves to an unreviewed file, or is
removed without shrinking the reviewed baseline in the same change.

This is a source and configuration ratchet, not proof of a production zero-use
window. PostgreSQL remains the production authority. JSON remains a
local-development compatibility authority until later B20 slices remove it.

## Complete legacy invocation baseline

The previous data-boundary check counted 53 calls but intentionally skipped
`Program.cs` and `State`. B20A closes that blind spot:

| Classification | Exact count |
| --- | ---: |
| Broad `IGameStore` invocations | 59 |
| Concrete JSON checkpoint invocation | 1 |
| Total legacy persistence data invocations | 60 |
| Reads | 15 |
| Mutations or mixed read/write operations | 43 |
| Bootstrap operations | 2 |
| Broad `IGameStore` methods | 44 |
| Broad-store caller files | 24 |
| Invoked broad-store members | 42 |

`DisposeAsync` is resource cleanup and is not one of the 60 data operations.

### Invocation ownership

| Area | Exact current calls | Current status and migration implication |
| --- | ---: | --- |
| Composition and semantic gateway | 3 | `Program.EnsureSeedDataAsync`; gateway seed plus first-character routing. The gateway is a high-risk authentication/routing boundary. |
| Account, authentication, and session | 14 | Login/create, account lookup, credential lookup/CAS/create, online/offline transitions. Several are production-critical. |
| Checkpoints and character lifecycle | 9 | Position/vitals compatibility calls, create/delete compatibility calls, and four `LegacyCharacterCheckpointStore` operations including the concrete JSON revisioned position write. |
| Inventory, equipment, crafting, and developer grants | 12 | Direct compatibility operations for grants, bag/equipment movement, forging, enhancement, Gear Mentor, talent, and Holy Stone. PostgreSQL durable executors already cover many secure paths. |
| Progression, world boss, and Zodiac level | 11 | Five live reads plus world/progression writes and compatibility interval/reward fallbacks. |
| Zodiac skill grids | 6 | Activate, upgrade, and select compatibility branches; focused PostgreSQL command executors already exist. |
| Pets | 5 | Three compatibility mutations and two broad live pet reads. |

The concrete inventory is stored in
`tests/Godswar.Server.ProtocolChecks/DataBoundaryArchitectureBaseline.cs`.
Every allowance is a path, member, and exact count rather than one aggregate
ceiling.

## Provider and composition debt

| Dependency | Exact baseline | Meaning |
| --- | ---: | --- |
| `IGameStore` tokens | 19 in 13 server files | Broad contract, consumers, and adapters still exist. |
| `JsonGameStore` tokens | 15 in 13 server files | Ten `JsonGameStore*.cs` implementation files plus composition/adapters. |
| `PostgresGameStore` tokens | 34 in 33 server files, plus 3 in one secure-smoke tool | PostgreSQL is authoritative, but much of its implementation still sits behind the broad legacy store. |
| `GameStorageProviderKind.Json` | 7 in 4 server files | JSON remains selectable only in the local-development profile. |
| JSON snapshot branch | 2 in 2 files | Program composition and finite provider metrics. |
| Generated JSON world-content fallback | 2 in 2 files | Local JSON composition still loads compiled content. |
| `GameDatabase` | 13 in 6 files | Whole-file JSON aggregate backing `state.json`. |
| `LegacyCharacterCheckpointStore` | 2 in 2 files | Process-local ownership and JSON compatibility adapter. |
| `LegacySemanticGatewayDataSession` | 5 in 2 files | Focused gateway interface backed by broad store composition. |
| Checked-in JSON configurations | 2 | `appsettings.json` and `appsettings.backhaul-worker.example.json`. |
| JSON B18C smoke selections | 2 | The local smoke worker selects JSON in both its environment and generated configuration. |

The JSON store serializes the mutable `GameDatabase` aggregate to
`DataPath/state.json`. It is guarded only by a process-shared semaphore and is
not suitable as multi-process or production authority. The production profile
already rejects JSON; B20 still needs to remove the selectable compatibility
path rather than merely rely on that policy.

## Legacy schema and projection dependencies

| Dependency | Exact baseline | Required retirement proof |
| --- | ---: | --- |
| `LegacySchemaBootstrap` | 21 references in the project file, loader, and broad PostgreSQL store | Prove fresh and upgraded database creation before removing the runtime bootstrap dependency. Keep immutable applied history. |
| Embedded bootstrap SQL | 6 resources | Do not edit or delete until forward installation and rollback packaging are proven. |
| Docker legacy init mount | 1 | `docker-compose.yml` still mounts `database/postgres` into `/docker-entrypoint-initdb.d`. |
| `character_item_loadout` runtime reads | 7 SQL reads in 4 server files | Replace with focused authoritative item projection; observe no reads before archive/drop. |
| `character_item_loadout` operations script | 1 | `tools/SetEquippedWeapon.ps1` must migrate with the projection. |

Database compatibility views and applied migrations are not alternate
authorities by themselves. The risk is that current runtime readers and tools
still require them, so a premature drop would break login, equipment, or
operator workflows.

## Content and capture boundary

The audit distinguishes runtime content from research capture data:

- 46 direct compiled seed references remain in 18 non-generated server
  consumers. This includes map traversal, combat/world-boss rules, JSON
  compatibility, PostgreSQL startup seeding, and several item/skill planners.
- `PostgresWorldContentReaderLoader` still reads two capture-derived
  operational tables: `monster_spawn_packets` and
  `server_packet_templates`.
- Runtime access to research-only `packet_capture_sessions`,
  `packet_transactions`, `captures`, `_reference`, or `origin_disasm` is
  hard-zero. Current violations: 0.
- Applied migration SQL may mention research tables and is deliberately not
  rewritten by the ratchet.
- `CapturedMonsterSpawn`, packet-layout names, legacy TCP adapters, and other
  original-client protocol terminology are not automatically persistence
  debt. Wire compatibility is a different boundary.

Tracked generated C# content is not treated as disposable build output. The
ratchet excludes generated declaration files from the consumer count but
tracks every non-generated consumer, preventing another gameplay system from
binding directly to compiled seed authority without review.

## Ratchet implementation

The existing data-boundary analyzer now scans `Program.cs` and `State` and
recognizes `_gameStore`, raising its exact broad-call baseline from 53 to 59.
The new B20A analyzer adds:

- exact per-path reference ceilings for broad, JSON, PostgreSQL, adapter,
  schema, projection, content, and tool dependencies;
- structural JSON parsing for every root `appsettings*.json` provider;
- a separate concrete JSON checkpoint-call allowance;
- explicit tracking of runtime/operations tooling and the Docker init mount;
- a hard-zero capture-authority rule outside immutable migrations; and
- `RetirementComplete=false`, which may become `true` only with an empty
  baseline and then permanently rejects reintroduction.

New debt fails. Removed debt also fails as stale until its allowance is
reduced in the same change. This converts every later B20 slice into a
measurable ratchet reduction rather than allowing debt to move between files.

The check intentionally excludes tests, documentation, artifacts, scratch
data, and `bin`/`obj` output from production counts. Tests remain migration
work when JSON is removed, but they cannot make production debt look larger.

## Migration order

### B20B - Account/authentication/session contracts

Create focused Application contracts and PostgreSQL Infrastructure adapters
for credential lookup/create/CAS, account lookup, and online transitions.
Replace `AccountAuthenticationService` and the semantic-gateway broad-store
adapter. Add a finite runtime invocation counter before claiming a zero-use
window. This is the next recommended slice because it removes the highest-risk
trust-boundary dependency.

### B20C - Remaining live PostgreSQL reads and writes

Move character stats/skills/pets, boost queries, Zodiac level, and world-boss
state behind feature-specific contracts. Reuse existing snapshot and durable
command contracts only when their transaction semantics match.

### B20D - Compatibility mutation fallback removal

Remove already fail-closed JSON/raw direct-handler mutation branches. Convert
their tests to narrow fakes, then remove `LegacyCharacterCheckpointStore`.

### B20E - PostgreSQL-only runtime composition

Remove the JSON provider switches, `JsonGameStore*`, `GameDatabase`, JSON
snapshot provider, `DataPath`/`GODSWAR_DATA_PATH`, JSON configurations, and
the B18C JSON worker profile. Preserve an explicit test fake where needed;
do not retain a selectable server authority.

### B20F - Bootstrap and projection cutover

Move migration/bootstrap composition out of `PostgresGameStore`, prove clean
and upgraded installs, remove the historical Docker init mount, and replace
all `character_item_loadout` readers before a measured archive/drop window.

### B20G - Content publication and capture separation

Make versioned PostgreSQL publications the only runtime content source,
remove direct compiled-seed consumers, and either publish or formally promote
the two capture-backed monster/template tables. Keep research capture storage
on a separate tool-only boundary.

### B20H - Observation and final removal

Require the runtime legacy-invocation metric to remain zero for the approved
window. Then run B19 reconciliation, backup/restore, clean-install,
upgrade-install, prior-binary rollback, and archive-parity gates before
setting `RetirementComplete=true` and deleting compatibility code.

## Verification and limits

Local B20A verification:

- Release build: passed with 0 warnings and 0 errors;
- full protocol suite: 295 passed, 0 failed;
- data-boundary ratchet: 59/59 broad calls, no new/stale debt or rule
  violations; and
- B20A ratchet: 60/60 total calls, 15 reads, 43 mutation/mixed, 2 bootstrap,
  46 generated-seed references, 2 JSON configs, and 0 capture-authority
  violations.

B20A does not claim that legacy paths are unused at runtime, does not remove
JSON, does not drop a schema/view/table, and does not alter live containers or
player data. Those claims require the later slices and their observation
windows.
