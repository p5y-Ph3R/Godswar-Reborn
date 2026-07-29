# B05C database-authoritative NPC dialogue

Date: 2026-07-29
Roadmap dependency: B05B - database-authoritative NPC spawns
Next roadmap ticket: B06 - consistent character snapshot reader

## Outcome

The server now treats one immutable PostgreSQL publication as the official
runtime source for NPC text metadata, client dialogue identity, finite
behavior selection, and ordered initial menu choices.

The reviewed v1 publication is:

```text
spawn revision       06BCC3DD4665BB5F3F3AE0843B1AA2A1B6C211DDA07DB0381B5EA663068040C7
dialogue revision    CC1CE5D182C68C728AD824D04F87F29DC66B0446D959C0EA08B7DD2712C6908D
NPC text rows        383
dialogue profiles    4
ordered menu rows    23
NPC route bindings   8
hashed entries       391
```

The eight active bindings cover the Athens and Sparta Gear Mentor, Origin
Enhancer, Master Vestment Forger/Holy Suit, and Holy Stone NPCs. The other
published NPCs have official text metadata but no invented server behavior.

## Authority boundary

PostgreSQL tables introduced by migration
`20260729_024_npc_dialogue_content_release` are the runtime authority:

- `npc_dialogue_revisions`
- `npc_dialogue_texts`
- `npc_dialogue_profiles`
- `npc_dialogue_profile_entries`
- `npc_dialogue_bindings`
- `npc_dialogue_publication`

`npc_text_templates`, `npc_function_templates`, and `npc_dialog_templates`
remain legacy import/research material. A cold database may use the reviewed
legacy NPC text rows once to create the frozen v1 publication. After
publication, runtime loading reads only the published tables. Mutating the
legacy rows cannot change the active release.

The original client does not receive arbitrary dialogue strings from the
server. It receives an NPC/script key, dialogue index, and ordered sub-IDs,
then renders the localized text bundled in its own assets. Therefore:

- PostgreSQL owns every server-selectable dialogue value;
- changing visible client wording still requires a compatible client asset or
  protocol change; and
- the server does not pretend it can replace client-localized strings with a
  database-only edit.

## Runtime flow

```text
PostgreSQL publication
  -> repeatable-read world-content load
  -> canonical count/hash and spawn-revision validation
  -> immutable PinnedWorldContentReader
  -> NPC interaction resolves route by published NPC key
  -> finite behavior allowlist validates client compatibility
  -> response uses the database route's script, dialog, and ordered menu
```

`PostgresWorldContentReaderLoader` reads the NPC spawn and dialogue
publication pointers in the same read-only `REPEATABLE READ` transaction.
Startup fails closed if either publication is absent, malformed, incomplete,
hash-mismatched, or targets a different spawn revision.

`GameClientHandler` no longer builds an initial NPC menu from compiled
fallback lists. The operation implementations remain compiled server code;
database content selects only a finite `NpcDialogueBehavior` value. It cannot
name a method, type, script file, or arbitrary executable target.

The behavior registry also requires the reviewed physical NPC endpoint,
client script key, dialogue index, and exact menu shape. This prevents a
malicious or mistaken publication from routing a valid NPC into another
privileged workflow.

## Publication and migration safety

The release model provides:

- a canonical SHA-256 revision over all 383 text rows and eight flattened
  routes;
- an explicit foreign key to the immutable NPC spawn revision;
- declared text/profile/route/menu counts;
- bounded field and row counts;
- normalized, contiguous ordered menus;
- immutable release, text, profile, entry, and binding rows;
- a validated publication pointer;
- transaction-scoped advisory locking for concurrent cold publishers; and
- atomic, idempotent publication.

No migration deletes or rewrites legacy player, item, NPC, or capture data.
Future content changes publish a new complete revision; they do not edit an
active revision in place.

## Repository evidence

| Concern | Repository location |
| --- | --- |
| Application contract and manifest | `src/Godswar.Server/Application/World/IWorldContentReader.cs` |
| Immutable pinning and validation | `src/Godswar.Server/Application/World/PinnedWorldContentReader.Dialogues.cs` |
| Canonical hashing | `src/Godswar.Server/Application/World/WorldContentRevisionHasher.cs` |
| Finite domain definitions | `src/Godswar.Server/Domain/World/Content/NpcDialogueDefinition.cs` |
| Reviewed v1 profiles/bindings | `src/Godswar.Server/Infrastructure/WorldContent/NpcDialogueBaselineV1.cs` |
| Atomic publisher | `src/Godswar.Server/Infrastructure/WorldContent/PostgresNpcDialogueBaselinePublisher*.cs` |
| Database-only loader | `src/Godswar.Server/Infrastructure/WorldContent/PostgresWorldContentReaderLoader.NpcDialogues.cs` |
| Schema and database guards | `src/Godswar.Server/State/DatabaseMigrations/PostgresSchemaMigrationCatalog.NpcDialogueContent.cs` |
| Runtime resolution and capability guard | `src/Godswar.Server/Game/GameClientHandler.NpcDialogueAuthority.cs`; `src/Godswar.Server/Game/NpcDialogueBehaviorRegistry.cs` |
| Pure reader checks | `tests/Godswar.Server.ProtocolChecks/WorldContentReaderDialogueChecks.cs` |
| Migration contract checks | `tests/Godswar.Server.ProtocolChecks/PostgresNpcDialogueMigrationChecks.cs` |
| Disposable PostgreSQL proof | `tests/Godswar.Server.ProtocolChecks/PostgresNpcDialoguePublicationIntegrationChecks*.cs` |

## Verification

```text
Release solution build                         PASS (0 warnings, 0 errors)
Pinned/generated dialogue checks               PASS
Gear Mentor database-route protocol            PASS
Holy Suit database-route protocol              PASS
Migration-024 static safety contract           PASS
Disposable PostgreSQL 17 empty migration       PASS (000 through 024)
Concurrent/idempotent cold publication         PASS (6 publishers)
Publication count/hash/route parity             PASS (383/4/23/8)
Partial and over-count publication rejection   PASS
Spawn/dialogue mismatch rejection              PASS
Published child-row immutability               PASS
Disposable database cleanup                    PASS (0 remain)
Development database migration/publication     PASS (25 migrations, 383 texts)
```

The isolated development-database verifier bound only to loopback ports
15991 and 17091. It was stopped after verification. The existing Docker game
server remained running and was not rebuilt or replaced.

## Rollback

Migration 024 is additive and forward-only. An older application can ignore
the new tables while they remain in place.

For a future content rollback, atomically repoint
`npc_dialogue_publication` to a previously verified complete revision that
targets the active spawn revision. Never mutate published rows. The v1
release has no earlier database dialogue revision, so its application
fallback is the preceding B05B artifact.

## Next dependency

B06 now proceeds without carrying an NPC-dialogue exception. It will replace
the mixed multi-query character login path with one bounded, transactionally
consistent `ICharacterSnapshotReader`.
