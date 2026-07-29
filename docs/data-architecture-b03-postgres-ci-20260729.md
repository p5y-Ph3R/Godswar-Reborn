# B03 mandatory disposable PostgreSQL CI

Status: complete locally; awaiting the first GitHub-hosted workflow run

- Implementation commit:
  `daf77e3543ed5cade843641a22c4830af7a2467e`
- Implementation tree:
  `8b48ed1e145cb1f645ac3af0ba5853eb7f98a7f3`
- Base documentation commit: `2a106dd`
- Date: 2026-07-29
- Local result: passed
- Runtime behavior change: none
- Production database/schema change: none

## Outcome

B03 adds a mandatory PostgreSQL 17 migration and repository gate to the
existing GitHub Actions workflow. The gate exercises three disposable schema
states, runs every mutable repository smoke check in an isolated clone of the
fully migrated baseline, refuses skipped checks, produces a machine-readable
result, and removes every database and temporary fixture artifact it creates.

The implementation does not use the live game database or any captured
player backup. Its historical upgrade input is a small, deterministic,
synthetic SQL fixture.

## Files

| File | Responsibility |
|---|---|
| `.github/workflows/phase5a-network-gate.yml` | Adds the isolated `postgresql-17-gate` Linux job, PostgreSQL 17 service, Release build, mandatory gate invocation, summary, and result upload |
| `tools/InvokeB03PostgresCiGate.ps1` | Orchestrates version validation, migration scenarios, repository/concurrency smoke checks, cleanup, and JSON reporting |
| `tools/B03PostgresCiGate.Helpers.ps1` | Contains the bounded Docker, database, connection, fingerprint, required-check, and result helpers used by the runner |
| `tests/Godswar.Server.ProtocolChecks/Fixtures/b03-prefix-008.sql` | Adds only deterministic synthetic account, character, inventory, and captured-packet sentinels to the exact migration-008 schema |
| `tests/Godswar.Server.ProtocolChecks/PostgresSchemaReleaseIntegrationChecks.cs` | Existing runtime migration-path assertion reused by all three scenarios |
| `tests/Godswar.Server.ProtocolChecks/PostgresMigrationPrefixFixtureChecks.cs` | Existing exact-prefix builder reused to construct the historical migration-008 source |
| `tests/Godswar.Server.ProtocolChecks/Program.cs` | Existing named check dispatcher used by the fail-closed wrapper |

No production runtime, migration, protocol, client, or database schema file
was changed by B03.

## CI design

The existing Windows network job remains independent. A second job named
`postgresql-17-gate` runs on `ubuntu-latest` with a version-pinned
`postgres:17.9-alpine` service. The service has an isolated CI-only role and
password, a dynamically published PostgreSQL port, and a bounded health
check. The runner accepts only `127.0.0.1`, `localhost`, or `::1` as the
database host and verifies that the supplied host port is the mapping Docker
reports for port `5432/tcp` on the exact service container. The job:

1. restores and builds the complete solution in Release mode;
2. supplies the PostgreSQL service container ID and mapped port to the gate;
3. requires PostgreSQL major version 17;
4. runs the fourteen required checks and three migration scenarios;
5. writes `artifacts/b03/postgres-ci-result.json`;
6. creates a sanitized fallback failure result if restore, build, or another
   earlier workflow step fails before the gate can write its own report;
7. publishes the JSON result for 14 days even when the gate fails; and
8. fails if neither the normal result nor fallback result can be produced.

The normal report contains the source commit, PostgreSQL version number,
check names, statuses, durations, exit codes, skip counts, migration counts
and heads, cleanup result, and a bounded failure category. The sanitized
fallback contains only workflow identity, failure classification, timestamps,
empty check/scenario lists, and cleanup status `not-started`. Neither report
contains a connection string, password, packet payload, dump, account
credential, or other production data.

## Fail-on-skip behavior

The protocol-check executable historically permits PostgreSQL checks to print
`SKIP` and still return success when an environment variable is absent. B03
does not accept that behavior for its required checks.

For every named check, `InvokeB03PostgresCiGate.ps1` requires all of:

- process exit code `0`;
- no output line beginning with `SKIP`; and
- exactly one output line equal to `PASS <required check name>`.

Any missing environment variable, unavailable database, test failure,
unexpected skip, missing exact receipt, version mismatch, dump/restore error,
or cleanup error makes the report and job fail. The script restores its
temporary test environment variables after each invocation.

## Migration scenarios

| Scenario | Initial history | Required result | Evidence |
|---|---:|---:|---|
| Empty bootstrap | 0 migrations | 25 migrations through `20260729_024_npc_dialogue_content_release` | Runs the actual embedded bootstrap and `PostgresGameStore.EnsureSeedDataAsync`; the existing check also verifies a second initialization is a no-op |
| Restored prefix-008 upgrade | 9 migrations through `20260723_008_zodiac_skill_grid_state` | 25 migrations through `024` | Builds the exact prefix, loads synthetic durable sentinels, creates and restores a PostgreSQL 17 custom dump, then runs the production migration path |
| Current-schema idempotence | 25 migrations through `024` | unchanged at 25 through `024` | Reopens the upgraded restored fixture in a separate check invocation and proves another startup is a durable-state no-op |

The shared release-path check verifies the exact registered migration order
and checksums, packet metadata relations, trigger/function, cascade foreign
key, validated constraints, valid indexes, and durable fingerprints. On the
historical path, the fingerprints are non-empty because the fixture contains
synthetic identity, inventory, and raw packet-byte sentinels.

## Synthetic historical restore

`b03-prefix-008.sql` is deliberately small and reviewable. It first refuses
to run unless the database contains exactly nine migrations ending at
`20260723_008_zodiac_skill_grid_state`. It then inserts:

- one negative-ID synthetic account;
- one negative-ID synthetic character;
- one seven-item HP-potion stack referencing template `4000`;
- one fixed synthetic packet-capture session; and
- one transaction with deterministic clear and raw byte sentinels.

The gate generates the custom-format dump inside the disposable PostgreSQL
17 container using compression, no owner, and no privileges. It restores the
dump with `--exit-on-error`, verifies the restored prefix before migration,
and deletes both the SQL copy and dump from container `/tmp` during cleanup.
The dump is neither committed nor uploaded.

Before dumping, the runner calculates a bounded fingerprint containing exact
sentinel row counts plus an MD5 over the synthetic account, character,
inventory row, and clear/raw packet bytes. It requires all four sentinel
counts to equal one. After restoring, it calculates the same fingerprint and
requires exact string equality with the source before any forward migration
is allowed to run.

This is intentionally separate from the local B01A/B01B backup artifacts,
which contain real player and packet-capture data and remain Git-ignored.

## Required check set

The current gate requires these fourteen checks:

1. `PostgreSQL migration safety foundation`
2. `PostgreSQL schema release migration paths` from an empty database
3. `PostgreSQL migration-prefix fixture` through migration `008`
4. `PostgreSQL schema release migration paths` from the restored prefix
5. `PostgreSQL schema release migration paths` against the current schema
6. `PostgreSQL forward-only database cleanup`
7. `PostgreSQL official NPC content publication`
8. `PostgreSQL official NPC dialogue publication`
9. `PostgreSQL pinned world-content baseline`
10. `PostgreSQL consistent character snapshot reader`
11. `PostgreSQL equipment-forge race and preservation`
12. `PostgreSQL Zodiac level-up race`
13. `PostgreSQL authoritative pet level-up`
14. `PostgreSQL pet-egg hatch transaction`

The final nine provide current-schema content publication, consistent-read,
repository, ownership, transaction, audit, persistence-reload, and
concurrency smoke coverage in addition to the migration checks. Each receives
its own clone of the migrated empty baseline, so no check depends on mutations
left by a prior check. The character snapshot check also covers the
PostgreSQL concurrent single-slot create guard.

## Original B03 local validation

The initial ten-check B03 gate was run locally against an isolated PostgreSQL
17 container with no production database connection.

| Measurement | Result |
|---|---|
| PostgreSQL version | 17.9 |
| `server_version_num` | `170009` |
| Required checks | 10 passed, 0 failed, 0 skipped |
| Migration scenarios | `0 -> 23`, `9 -> 23`, `23 -> 23` |
| Expected/final head | `20260729_022_pet_level_progression` |
| Total gate duration | 112,732 ms |
| Cleanup | passed, zero reported errors |
| Fail-on-skip negative proof | synthetic `SKIP` produced failed status, `skipCount = 1`, and successful cleanup |
| Release build | 0 warnings, 0 errors |

The ignored local receipt is:

`artifacts/b03-final/postgres-ci-result.json`

Its SHA-256 is:

`0D413316AA8E76C200F03A6488CDEE72380FD1F58468F2A4E35A4AB3F566EF13`

The receipt identifies source commit
`2a106dd119cd80a8bea8b4a8d1cd6e8f2b0777c1`, which was the committed base
while the B03 working-tree implementation was under validation. The receipt
is verification output, not a tracked release input.

## Current B06 extension validation

The fourteen-check gate was rerun after adding database-authoritative NPC
dialogue, the consistent character snapshot reader, and per-check database
isolation:

| Measurement | Result |
|---|---|
| PostgreSQL version | 17.9 |
| `server_version_num` | `170009` |
| Required checks | 14 passed, 0 failed, 0 skipped |
| Migration scenarios | `0 -> 25`, `9 -> 25`, `25 -> 25` |
| Expected/final head | `20260729_024_npc_dialogue_content_release` |
| Total gate duration | 158,431 ms |
| Cleanup | passed, zero errors and zero residual `godswar_b03_%` databases |
| Release build | 0 warnings, 0 errors |
| Full protocol suite | 191 passed, 0 failed |

The ignored local receipt is
`artifacts/b03/postgres-ci-result-b06-final.json`; its SHA-256 is
`FA604B2D997F4D027BBCEF0A7266DED62B96DC1A50E8E79A9BC6C2BC46F51E88`.
It identifies source commit `c10cd6e`, the committed B05C base used while the
B06 working tree was under validation.

## Safety and cleanup

- Database names include a random token and must match the exact
  `godswar_b03_<token>_(empty|prefix|restored|smoke_<two digits>)` pattern
  before creation or deletion.
- The script can create only the three scenario databases and bounded
  per-check smoke clones bearing that run token. Cleanup records each name
  before creation, attempts all names in reverse order even if a
  database-create response was lost, and uses `DROP DATABASE IF EXISTS` with
  forced connection cleanup.
- The database host must be loopback, and the runner verifies the supplied
  port against the exact PostgreSQL service container's published mapping.
- Temporary container paths are unique per run and removed in `finally`.
- Connection pooling is disabled for scenario connections so teardown is
  deterministic.
- PostgreSQL connection and command timeouts are bounded.
- The script records a cleanup failure even when the primary test work
  succeeded.
- No persistent named volume, live container, production database, or local
  game server is selected by the workflow.

## Remaining verification

The implementation and complete gate have passed locally. The GitHub Actions
job has not yet run on a hosted runner, so hosted service-container startup,
mapped-port behavior, artifact publication, and workflow duration remain to
be confirmed by the first push or pull-request run. This report must not
represent local execution as hosted-CI evidence.

Repository workflow code alone does not make a check mandatory for merges.
The GitHub branch-protection rule or repository ruleset for every protected
branch must require the `postgresql-17-gate` job after its first hosted run
establishes the check name. Until that operational setting is applied and
verified, B03 is implemented and locally validated but is not an enforced
merge barrier.

If the hosted job exposes a runner-specific problem, keep the local
environment-based checks available while repairing the workflow. Do not
weaken the PostgreSQL 17 requirement, remove fail-on-skip enforcement, or
substitute a production backup.

## Rollback

Revert the workflow job, orchestration script, and synthetic fixture as one
unit. This requires no schema rollback because B03 changes no production
schema or runtime path. Existing local environment-based PostgreSQL checks
remain available during workflow repair.
