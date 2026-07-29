# B05 world-content reader evidence

- Date: 2026-07-29
- Roadmap ticket: B05 - Extract `IWorldContentReader`
- Result: implemented and verified
- Implementation commit: `954c05c`
- Schema migration: none

## Outcome

World content is no longer loaded through the broad `IGameStore` during login
or map transfer. `Program.cs` now:

1. initializes the selected durable store;
2. loads the complete published world projection;
3. pins deterministic revisions before opening any listener; and
4. injects `IWorldContentReader` into each game handler.

PostgreSQL loading uses one read-only `REPEATABLE READ` transaction. The
returned reader owns cloned NPC, monster, and enter-bootstrap bytes, so later
database or caller mutation cannot alter the running process. Map entry and
transfer perform no content database queries.

```text
generated authoring + published PostgreSQL rows
                         |
                         v
 Infrastructure/WorldContent loader
 (one read-only REPEATABLE READ snapshot)
                         |
                         v
       PinnedWorldContentReader + SHA-256 manifest
                         |
                         v
 GameClientHandler login / transfer / bootstrap
```

## Boundary changes

The application contract is
`src/Godswar.Server/Application/World/IWorldContentReader.cs`. It returns:

- one atomic `WorldMapContent` containing that map's NPCs and monsters;
- `EnterWorldBootstrapContent`;
- one process manifest with independent `maps`, `npcs`, `monsters`, and
  `enter-bootstrap` revisions.

Pure NPC and captured-monster definitions moved from the mixed `State` area
to `Domain/World/Content`. PostgreSQL and generated-source loaders live under
`Infrastructure/WorldContent`. Application-to-State references are now an
explicit architecture-rule violation.

The following four methods were removed from `IGameStore`:

- `GetCapturedNpcSpawnsAsync`
- `GetNpcSpawnDefinitionsAsync`
- `GetCapturedMonsterSpawnsAsync`
- `GetEnterSyncPacketsAsync`

The ratchet consequently shrank from 48 to 44 broad-store methods and from 81
to 78 direct broad-store calls. Its accepted post-B05 snapshot is:

```text
calls=78
_store occurrences=103
store parameter occurrences=19
IGameStore type references=10
legacy Npgsql references=323
State -> Game imports=18
new debt=0
stale debt=0
rule violations=0
```

## Revision and validation rules

`WorldContentRevisionHasher` uses an explicit versioned canonical form:

- ordinal family and row ordering;
- little-endian integers and IEEE-754 bit representations;
- UTF-8 strings and length-prefixed byte sequences;
- final delivered definition fields and packet bytes.

It deliberately excludes timestamps, capture counts, database migration
checksums, source labels, and other mutable metadata. Every public read clones
mutable byte arrays. Unknown maps, missing map publication, malformed NPCs,
malformed monster packets, malformed bootstrap frames, and an explicitly
required revision mismatch produce typed failures.

The PostgreSQL reader consumes only `server_packet_templates` for legacy
post-enter templates. Both runtime fallbacks to research packet history were
deleted. The existing character-specific accepted-quest safety filter remains
at the game/protocol boundary, preserving the reviewed wire behavior.

## Observability

The low-cardinality meter `Godswar.Server.WorldContent` exposes:

- `godswar_world_content_loads_total`
- `godswar_world_content_load_duration_ms`
- `godswar_world_content_rejections_total`
- `godswar_world_content_fallback_attempts_total`

Tags are bounded source, outcome, family, and reason codes. Revisions, map
IDs, packet data, account data, and database credentials are not metric
labels. Startup logs the pinned source, revision, and family entry counts once.

## PostgreSQL evidence

`PostgresWorldContentReaderIntegrationChecks` runs in the mandatory disposable
PostgreSQL 17 gate. The test starts from the reproducible empty database,
publishes one tracked 108-byte monster fixture, and inserts an opcode-10090
research-history decoy. It proves:

- generated-source and PostgreSQL map/NPC counts and canonical checksums
  match;
- two unchanged loads produce identical revisions and definitions;
- only explicitly published bootstrap templates are read;
- the research-history decoy is never used as a fallback;
- the monster definition and packet are byte-identical;
- mutating the backing monster row cannot alter an already pinned reader; and
- a new reader observes a different monster and manifest revision.

Fixture rows use collision-checked keys and are deleted exactly. The final
machine-readable report confirms 11 required checks, three migration
scenarios, and successful cleanup:

`artifacts/b05/postgres-ci-result.json`

The current development PostgreSQL corpus was also loaded through the new
reader and passed every captured-monster validation.

## Verification

```text
Release solution build                         PASS (0 warnings, 0 errors)
Pinned immutable world-content reader          PASS (1/1)
Data-boundary architecture ratchet             PASS (1/1)
Map-transition readiness regression            PASS (1/1)
Secure realtime handler integration            PASS (1/1)
Complete protocol-check catalog                PASS (182/182)
Disposable PostgreSQL 17 gate                  PASS (11 checks, 3 scenarios)
Disposable database cleanup                    PASS (0 B03 databases remain)
Current development captured-monster corpus    PASS (1/1)
git diff --check                               PASS
```

The running development server and PostgreSQL containers were not stopped or
replaced by this verification.

## Provenance limitation

B05 pins one final runtime-serving projection per family, but it does not
claim that NPC authoring is already a pure PostgreSQL workflow. The existing
NPC projection deliberately combines published capture/catalog rows with
compiled generated templates and actor-placement overrides. The final result
is deterministic and pinned, while a future content-release publisher must
reconcile that composite provenance before authors can independently publish
immutable releases.

This slice pins published map membership only. Portal topology, item
definitions, monster combat templates, and other generated catalogs still
have their existing direct compiled-source readers and are not represented as
migrated by this evidence.

## Rollback

The safe operational rollback is the preceding B04 server artifact/commit.
That restores the prior read-through world loader as a unit. Keeping a dormant
runtime adapter in the new build would preserve the broad-store dependency and
the removed research-history fallback, so B05 intentionally does not ship
that unsafe compatibility path.

No database migration or player-value mutation was introduced, so rollback
requires no data reversal.

## Next dependency

B06 is next: extract one transactionally consistent
`ICharacterSnapshotReader` so login hydrates a single versioned player
snapshot rather than a sequence of independently changing reads.
