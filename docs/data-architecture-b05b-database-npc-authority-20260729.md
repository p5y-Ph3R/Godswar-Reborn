# B05B database-authoritative NPC content evidence

- Date: 2026-07-29
- Roadmap dependency: B05 - Extract `IWorldContentReader`
- Result: implemented and verified
- Implementation commit: `b64490d`
- Schema migration: `20260729_023_npc_content_release`
- Next roadmap ticket: B06 - Extract `ICharacterSnapshotReader`

## Outcome

B05B closes the NPC provenance limitation recorded by B05 for the
PostgreSQL-backed runtime. PostgreSQL is now the single official runtime
source for the published NPC definition set. The production loader no longer
combines mutable capture tables with compiled NPC template and placement
catalogs.

The first official release is the reviewed, frozen 383-entry projection:

```text
entry count       383
content revision  06BCC3DD4665BB5F3F3AE0843B1AA2A1B6C211DDA07DB0381B5EA663068040C7
release source    reviewed-legacy-projection-v1
artifact SHA-256  4E6AEF697560276141C0A61E923FB016824FEEA607090C4294FEE1F6B6728926
```

`NpcContentBaselineV1` pins all four values. The embedded
`Baselines/NpcContentBaseline.v1.br` file is a reviewed bootstrap artifact,
not a second mutable runtime catalog. On a database with no NPC publication,
`PostgresNpcContentBaselinePublisher` validates that artifact and publishes
it transactionally. Once a publication exists, the PostgreSQL loader reads
the published database rows only.

```text
reviewed legacy projection (frozen once)
                    |
                    v
     NpcContentBaseline.v1.br + pinned SHA-256
                    |
                    v
 PostgresNpcContentBaselinePublisher (cold publication only)
                    |
                    v
 npc_content_revisions + npc_spawn_definitions
                    |
                    v
      npc_content_publication ('npcs' pointer)
                    |
                    v
 PostgresWorldContentReaderLoader (read-only snapshot)
                    |
                    v
       PinnedWorldContentReader used by gameplay
```

## Exact authority boundary

The official PostgreSQL revision owns the runtime fields represented by
`NpcSpawnDefinition`:

| Concern | Published fields |
| --- | --- |
| Map and identity | `map_id`, `scene_key`, `npc_key`, `template_key`, `object_id` |
| Spawn placement | `pos_x`, `pos_z`, `facing` |
| Appearance and interaction routing | `appearance_type`, `interaction_id` |
| NPC wire details | `detail_10077`, `detail_10080` |

This means the published revision controls which NPC actors exist, where they
appear, how the client identifies and renders them, which interaction identity
they expose, and the two retained detail payloads.

B05B deliberately does **not** move these concerns:

- NPC dialog, shop, quest, forging, teleport, or other business behavior;
- NPC behavior dispatch and interaction implementation;
- monster definitions or captured monster spawns;
- map catalog ownership, portals, or travel rules;
- item, skill, or other content catalogs.

Those behaviors remain in C# and their existing feature modules. In
particular, an NPC definition being present in PostgreSQL does not mean its
dialog or gameplay function has been converted into data-authored content.
That is a later, separately reviewed migration.

The legacy NPC capture/reference/appearance/text tables and compiled
`NpcTemplateSeeds` / `NpcActorPlacementCatalog` sources remain available as
legacy staging, reconstruction, and rollback evidence. They are not read by
the PostgreSQL production NPC loader. The JSON/generated world-content loader
still exists for the explicitly restricted `LocalDevelopment` storage profile;
`ServerRuntimeProfilePolicy` forbids JSON storage in `Production`.

## PostgreSQL release model

Migration `20260729_023_npc_content_release` adds three tables:

| Table | Responsibility |
| --- | --- |
| `npc_content_revisions` | Immutable release manifest: canonical revision, entry count, provenance label, and creation time |
| `npc_spawn_definitions` | Complete definition rows owned by one revision |
| `npc_content_publication` | Singleton `family = 'npcs'` pointer selecting the official revision |

The database enforces:

- a 64-character uppercase hexadecimal revision;
- at most 10,000 definitions per release;
- non-empty source and key fields;
- map foreign keys into `map_templates`;
- revision foreign keys with `ON DELETE RESTRICT`;
- unique object and interaction IDs within a revision and map;
- unsigned 32-bit protocol ranges for object, interaction, and appearance IDs;
- finite position and facing values;
- detail payloads no larger than 65,535 bytes;
- an insert count that cannot exceed the declared release manifest;
- publication only after the stored row count equals the declared count;
- rejection of release and definition updates or deletes; and
- rejection of deleting the publication pointer.

The publication pointer may be moved to another complete revision. This is
the intended future publish/rollback operation; mutating a published release
in place is not.

## Bootstrap and runtime flow

`Program.cs` first applies the selected store's migrations and seed data.
For PostgreSQL it then calls
`PostgresWorldContentBootstrapper.LoadAsync` before listeners open.

The bootstrapper:

1. asks `PostgresNpcContentBaselinePublisher` to ensure an official
   publication exists;
2. serializes concurrent cold starts with a transaction-scoped PostgreSQL
   advisory lock;
3. leaves an existing publication unchanged;
4. on the first publication only, verifies the embedded artifact checksum,
   bounded binary format, entry count, map membership, definition validity,
   and canonical content revision;
5. inserts the release manifest and all 383 definitions in one database
   transaction;
6. verifies the stored count and provenance; and
7. creates the singleton publication pointer only after the release is
   complete.

`PostgresWorldContentReaderLoader` then opens one read-only
`REPEATABLE READ` transaction for the full world-content load. Its NPC partial
reads the publication pointer, release manifest, and
`npc_spawn_definitions`. It orders the final rows canonically and recomputes
the same versioned SHA-256 revision used by `WorldContentRevisionHasher`.
The resulting NPC collection is copied into the process-pinned
`IWorldContentReader`; map entry and transfer do not query the NPC tables.

The frozen artifact is therefore a deterministic first-release installer.
After installation, changing compiled NPC sources, capture history, or the
artifact on disk cannot change the already selected database publication.
A future content-authoring tool must publish a new complete immutable revision
and deliberately move the pointer.

## Fail-closed behavior

There is no PostgreSQL runtime fallback to compiled NPC seeds or legacy NPC
capture/catalog tables.

The loader rejects:

- a missing `npcs` publication with
  `WorldContentFailureReason.Missing`;
- an out-of-range declared entry count;
- a definition that references an unpublished map;
- malformed or out-of-range protocol values;
- a different stored count from the release manifest; and
- a canonical SHA-256 that differs from the published revision.

`Program.cs` treats a typed world-content rejection as startup failure before
network listeners are opened. Artifact checksum or publication failures also
abort the cold-start publication transaction rather than creating a partial
official release.

The existing low-cardinality `Godswar.Server.WorldContent` metrics continue
to record load outcomes, duration, and typed rejections. Startup logs whether
it created the reviewed baseline publication or selected the already official
database revision, together with its revision and entry count.

## Repository evidence

| Evidence | Repository location |
| --- | --- |
| Official NPC schema and immutable guards | `src/Godswar.Server/State/DatabaseMigrations/PostgresSchemaMigrationCatalog.NpcContent.cs` |
| Frozen release identity and artifact verification | `src/Godswar.Server/Infrastructure/WorldContent/NpcContentBaselineV1.cs` |
| Bounded, versioned artifact codec | `src/Godswar.Server/Infrastructure/WorldContent/NpcContentBaselineCodec.cs` |
| Transactional cold-start publisher | `src/Godswar.Server/Infrastructure/WorldContent/PostgresNpcContentBaselinePublisher.cs` |
| Startup composition | `src/Godswar.Server/Infrastructure/WorldContent/PostgresWorldContentBootstrapper.cs`; `src/Godswar.Server/Program.cs` |
| Database-only NPC runtime read | `src/Godswar.Server/Infrastructure/WorldContent/PostgresWorldContentReaderLoader.Npcs.cs` |
| Canonical revision and process pinning | `src/Godswar.Server/Application/World/WorldContentRevisionHasher.cs`; `src/Godswar.Server/Application/World/PinnedWorldContentReader.cs` |
| Static authority and codec checks | `tests/Godswar.Server.ProtocolChecks/NpcContentAuthorityChecks.cs` |
| Migration contract checks | `tests/Godswar.Server.ProtocolChecks/PostgresNpcContentMigrationChecks.cs` |
| Disposable PostgreSQL publication proof | `tests/Godswar.Server.ProtocolChecks/PostgresNpcContentPublicationIntegrationChecks*.cs` |

## Verification

```text
Release solution build                         PASS (0 warnings, 0 errors)
Frozen artifact count/revision/codec checks     PASS
Database-only NPC authority source check        PASS
Migration checksum and schema contract checks   PASS
Disposable PostgreSQL cold publication          PASS
Concurrent/idempotent publication               PASS (6 cold publishers)
PostgreSQL row/artifact/reader parity            PASS (383/383)
Legacy-source mutation isolation                PASS
Database immutability/completeness guards       PASS
Process-pinned reader immutability               PASS
Complete protocol-check catalog                 PASS (184/184)
Disposable PostgreSQL 17 gate                   PASS (12 checks, 3 scenarios)
Disposable database cleanup                     PASS (0 B03 databases remain)
Current development database migration/load     PASS (24 migrations, 383 NPCs)
git diff --check                                PASS
```

The final machine-readable PostgreSQL gate report is
`artifacts/b05b/postgres-ci-result.json`. It records source commit `b64490d`,
migration head `20260729_023_npc_content_release`, a truthful 24-to-24
current-schema idempotence scenario, and successful fixture/database cleanup.

The development PostgreSQL database was advanced to the same 24-migration
head and contains one published release with a declared and stored count of
383. An isolated startup on loopback ports loaded and hash-validated that
release before opening listeners. Only that verifier was stopped afterward;
the existing Docker game-server container was not stopped or replaced.

## Rollback

The application rollback point is the prior B05 server artifact/commit. The
new migration is additive and deliberately retains legacy NPC source tables.
Because migration history is forward-only, rolling back application code does
not delete migration `023` or the official release rows.

For a content-only rollback after future revisions exist, publish the previous
complete revision by moving `npc_content_publication`; do not edit definitions
in place. The first release has no earlier database revision, so its operational
fallback is the reviewed B05 application artifact while the additive tables
remain dormant.

## Next dependency

B06 is now unblocked. It extracts a transactionally consistent
`ICharacterSnapshotReader`; it does not expand the NPC behavior/dialog scope.
