# B20G pet-content cutover

Date: 2026-08-01

Status: implemented and locally verified. B20H's real seven-day deployed
observation and final compatibility deletion remain pending.

## Outcome

Active pet gameplay no longer reads mutable `pet_templates`,
`pet_aptitude_templates`, or compiled pet-balance catalogs. PostgreSQL owns one
official immutable pet publication, and each server process pins one complete
revision at startup.

Migration `20260801_042_pet_content_release` publishes:

- global level, capacity, merge, skill, and material settings;
- 45 species, including egg mapping, starter skill, food kind, lifetime, and
  captured Magic Jade identity;
- all 16 aptitude names and growth/initial-savvy/added-savvy brackets;
- species/aptitude native stat and lifetime profiles;
- the complete level 1-120 EXP ladder; and
- all 100 rebirth requirements, chance items, and increase ranges.

The canonical SHA-256 covers every field and collection above. Its manifest
declares an exact count for every definition family. The database locks the
revision row on insert, rejects each table as soon as its declared bound is
reached, requires exact completeness before publication, seals the revision,
and rejects updates, deletes, late inserts, and deletion of the official
pointer.

## Runtime boundary

`IPetContentCatalog` is the application contract.
`PinnedPetContentCatalog` validates bounds, uniqueness, family completeness,
cross-family references, and the canonical hash while taking defensive copies.
`PostgresPetContentReader` loads one pointer and all families in a read-only
repeatable-read transaction. No process-global mutable catalog or hot reload
was introduced.

The pinned catalog is passed through `Program`, `PostgresGameStore`,
`PostgresApplicationDataRuntime`, `GameClientHandlerFactory`, and
`GameClientHandler`. It now governs:

- bag egg recognition and authoritative hatch outcomes;
- species, aptitude, native profile, growth, and savvy generation;
- pet capacity and skill limits;
- level EXP and maximum-level behavior;
- owned-pet wire projection, including food, lifetime, level, and skill count;
- owner merge, pet merge, and rebirth validation; and
- legacy PostgreSQL safety checks for existing pet rows.

`PetContentBaseline` is the sole cold-publication boundary allowed to consume
the reviewed compiled declarations. A repository ratchet permits only that
file and definition-internal dependencies to reference those declarations.
Runtime SQL outside migrations is also forbidden from reading the two mutable
pet authoring tables.

## Item-reference rule

Startup cross-checks every currently active pet item reference against the
already pinned item revision: egg IDs, merge/rebirth spirits, and rebirth
chance items. A missing active item fails startup.

Magic Jade IDs 11050-11094 are reviewed item templates and captured, hashed
pet facts. Startup now cross-checks every active Jade reference against both
the pinned item and pet revisions. The durable `pet_appearance_change`
command resolves the target species only through that pinned mapping; a
client-supplied species is never accepted.

Migration `20260812_085_pet_magic_jade_appearance_groups` makes the captured
mapping directly queryable without duplicating it: the immutable species row
owns the exact `magic_jade_item_id = 11049 + species_id` mapping, while
`pet_content_magic_jade_appearance_groups` joins it to the same revision's
Merge factor, lookup maximum, and five-spirit bounds. Use
`current_pet_magic_jade_appearance_groups` to list all 45 appearances for the
official publication. The view records the client files from which the
appearance and Merge policy were transcribed. Migration
`20260812_086_pet_appearance_change` separately authorizes the durable command
evidence and exactly-once item consumption; keeping these migrations separate
preserves the content/runtime boundary.

## Compatibility and rollout

The legacy `pet_templates` and `pet_aptitude_templates` tables remain stable
identity/import projections. Foreign keys prevent a published species or
aptitude identity from disappearing, while changes to non-key authoring fields
cannot affect a publication or running process. Corrections require a new
complete revision, an atomic pointer move, and a coordinated worker restart.

The process content fingerprint includes the pet SHA alongside world/gameplay
and item SHAs. Realm admission therefore rejects workers with different pet
rules even if they host different maps.

## Verification

The following checks were added and passed locally:

- Release build of `GodswarServer.sln`;
- `Pinned PostgreSQL pet-content boundary`;
- `Owned-pet native protocol and login ordering`;
- `Data-boundary architecture ratchet` with no allowance expansion;
- `PostgreSQL migration safety foundation` at 46 migrations through 045; and
- `PostgreSQL immutable pet-content publication` on a fresh PostgreSQL 17
  database.

The real database check proves cold publication and repeat idempotence, item
reference validation, non-key source-mutation isolation, stable-identity
foreign keys, sealed-row and late-insert rejection, no-delete pointer behavior,
incomplete-publication rejection, and overflow rejection for all six child
tables. The fresh publication contained 776 bounded pet entries.

The mandatory combined disposable release gate remains:

```powershell
$env:GODSWAR_B03_POSTGRES_PASSWORD = '<ephemeral-test-password>'
.\tools\InvokeB03PostgresCiGate.ps1 -ContainerId <postgres-17-container-id>
```

It expects exactly 46 migrations through
`20260801_045_item_material_recipe_content_release` and includes the pet
publication check as a required non-skippable repository smoke test.
