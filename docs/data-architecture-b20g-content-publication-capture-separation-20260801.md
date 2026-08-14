# B20G immutable gameplay-content publication and capture separation

Status: implemented and locally verified on 2026-08-01. B20H's real seven-day
deployed observation and final compatibility deletion remain pending.

## Outcome

The server no longer treats packet captures, mutable relational authoring rows,
or generated C# declarations as runtime gameplay authority. Startup promotes
reviewed sources into immutable, revision-owned PostgreSQL publications and
pins one complete content set for the lifetime of the process.

PostgreSQL is the official durable owner. Capture tables remain research and
forensics inputs only. Redis receives only a combined content fingerprint for
worker compatibility; it does not own content.

## Published families

| Family | Migration/publication | Runtime reader |
| --- | --- | --- |
| NPC placement | `20260729_023_npc_content_release` | `PostgresWorldContentReaderLoader.Npcs.cs` |
| NPC dialogue/routes | `20260729_024_npc_dialogue_content_release` | `PostgresWorldContentReaderLoader.NpcDialogues.cs` |
| Monster spawns | `20260801_036_monster_content_release` | `PostgresWorldContentReaderLoader.Monsters.cs` |
| Enter bootstrap | `20260801_037_enter_bootstrap_content_release` | `PostgresWorldContentReaderLoader.EnterBootstrap.cs` |
| Item templates and skill-book items | `20260801_038_item_template_content_release` | `PostgresItemTemplateContentBootstrapper` |
| Maps, links, monster metadata, world bosses, pending boss policy, classes, talents, skill combat and skill books | `20260801_039_gameplay_content_release` | `PostgresWorldContentReaderLoader.Gameplay*.cs` |
| Item attributes, equipment ranks and holy-suit effects | `20260801_041_item_policy_content_release` | `PostgresItemTemplateCatalogLoader.Policy.cs` |
| Pet species, aptitude, growth/savvy, level and rebirth policy | `20260801_042_pet_content_release` | `PostgresPetContentReader*.cs` |
| Forging, gear-enhancement and Gear Mentor material policy | `20260801_044_item_material_content_release` | `PostgresItemTemplateCatalogLoader.Materials.cs` |
| Gear Mentor crystal-transform and gem-piece-combination recipes | `20260801_045_item_material_recipe_content_release` | `PostgresItemTemplateCatalogLoader.Materials.cs` |

Migrations 041, 044, and 045 extend the same item-template revision and `items`
pointer rather than creating independent publication families. Each top-level
family has a revision row, revision-owned definition rows, a singular
publication pointer, bounded counts and values, immutable-row triggers, a
complete-publication guard, and a no-delete pointer guard. Canonical SHA-256
revisions are recalculated by the loader before content is accepted.

## Reviewed clean-install input

`PostgresRelationalContentBaselineBootstrapper` runs the reviewed relational
SQL only when the corresponding authoring tables are empty. Every embedded
resource is checked against a source-controlled SHA-256 before execution:

- `database/postgres/005_item_attributes.sql`
- `database/postgres/006_skills_and_talents.sql`
- `database/postgres/007_npcs.sql`
- `database/postgres/008_maps.sql`
- `database/postgres/009_monsters.sql`

The bootstrapper then applies explicit topology, world-boss and skill-timing
policy before immutable publication. It canonicalizes duplicate portal
identities by source map, target map and coordinates. This makes clean and
upgraded installations publish the same 50-link topology.

The remaining code-authored first-publication inputs are deliberately narrow:

- `PostgresItemTemplateBaselinePublisher` creates the first reviewed immutable
  manifest-v4 item/template/material/recipe release. Its three compiled
  material catalogs and reviewed Gear Mentor recipe declarations are
  publication inputs only, not runtime authorities.
- `PostgresSkillTimingBaselinePublisher` supplies timing values omitted by the
  historical reviewed SQL before the first gameplay publication.
- `PetContentBaseline` converts the reviewed pet policy declarations into the
  first immutable pet release and validates its item references against the
  already pinned item catalog.

`B20LegacyPersistenceAnalyzer` permits generated declarations only at an exact
path allowlist. Arbitrary files named `*.Generated.cs` do not bypass the rule,
and both publisher references are exact path-and-count baseline entries.

## Startup and pinning

`Program.cs` orders startup as follows:

1. Apply the forward-only schema catalog through migration 045.
2. Validate and, only where empty, load the reviewed relational baseline.
3. Ensure and pin the immutable item-template publication.
4. Ensure and pin the immutable pet publication against that item revision.
5. Ensure and pin all immutable world/gameplay publications.
6. Validate cross-family item/skill-book references and build runtime catalogs.
7. Start coordination and listeners.

`GameplayRuntimeCatalogs` constructs map traversal, world-boss, monster combat
and skill-combat lookups once. Gameplay systems do not consult mutable content
tables or generated seeds after startup.

Character load/stat/rank projections read `character_items` directly and join
the process-pinned item template, attribute, equipment-rank, and holy-suit
definitions. They deliberately bypass pointer-following compatibility views
such as `character_equip`; advancing the publication pointer therefore cannot
change a running process's calculated stats or aura ranks.

Forging grants, Gear Mentor recipes/decomposition, and gear-enhancement
materials resolve through the same process-pinned manifest-v4 catalog.
`IItemMaterialCatalog` is the authoritative runtime boundary for both material
policy and the revision-owned crystal-transform and gem-piece-combination
recipes. Durable material-conversion receipt reconstruction uses the same
pinned recipe lookups as mutation planning. The code-versioned executable
forge calculators and generated forge-rule catalog remain outside this content
slice; migration 044 remains the immutable v3 material-policy release, while
migration 045 adds recipes in a new v4 publication.

`RuntimeContentFingerprint` hashes the pinned world manifest, item-template,
and pet revisions using a versioned canonical form. Worker registration holds
one realm-wide compatibility lease, so even workers hosting disjoint maps in
the same realm reject mixed content. Separate realms may deliberately run
different revisions.

## Capture boundary

`monster_spawn_packets`, `server_packet_templates`, `packet_transactions`,
`packet_capture_sessions`, local capture paths and disassembly/reference paths
are not runtime authorities. The sole capture-backed content exception is the
read-only `tools/ExportMonsterContentBaseline.ps1` review tool. Its analyzer
exception is exact, single-reference and read-only; an additional query or a
new tool path fails the B20 ratchet.

The reviewed compressed monster artifact is
`Infrastructure/WorldContent/Baselines/MonsterContentBaseline.v1.gz`. Runtime
publishing reads that checked artifact, not the capture table.

## Failure behavior

Startup fails closed when a publication is missing, incomplete, malformed,
oversized, internally inconsistent, or hashes differently from its pointer.
It never falls back to captures or mutable authoring tables. A process keeps
its pinned revision until restart; publishing a later revision cannot mutate a
running process in place.

Publication and baseline work uses bounded commands, transactions and advisory
locks. Repeated startup is idempotent. Immutability means a correction is a new
revision plus an atomic pointer move, never an update to an existing revision.

Manifest-v1, manifest-v2, and manifest-v3 revisions remain sealed historical
records. A pointer to any of them is upgraded forward by publishing a new v4
revision without rewriting the historical rows. The current server accepts
only v4 for live processes. Consequently, moving the pointer back to v1, v2,
or v3 makes the next current-binary startup fail closed; a current-binary
content rollback must target a compatible v4 revision. An application rollback
to a pre-v4 binary needs a coordinated binary/schema/content compatibility plan
because forward-only migration 045 is not removed.

## Verification and release gate

The relevant checks cover:

- exact forward-only order and checksums through migration 045;
- immutable and bounded publication schema/trigger contracts;
- deterministic world, gameplay and item revisions;
- exact generated-seed and capture-authority ratchets;
- startup ordering and removal of the broad `EnsureSeedDataAsync` startup call;
- combined world/item/pet worker fingerprint and realm-wide admission behavior;
- clean-install and historical-upgrade idempotence;
- PostgreSQL publication counts, portal deduplication and capture-decoy
  isolation;
- malformed, incomplete, mutated and deletion-guard rejection paths;
- same-count poisoned publication rejection by content hash;
- partial-source fail-closed behavior and proof that a published authority
  does not repair or read a later mutable-source edit; and
- a transitive source-isolation ratchet covering direct table reads and SQL
  views/functions derived from mutable authoring tables.

The mandatory release command is:

```powershell
$env:GODSWAR_B03_POSTGRES_PASSWORD = '<ephemeral-test-password>'
.\tools\InvokeB03PostgresCiGate.ps1 -ContainerId <postgres-17-container-id>
```

The gate owns disposable databases, verifies all 46 migrations through
`20260801_045_item_material_recipe_content_release`, runs
publication/repository checks, and removes its databases in `finally`.

## Known limitations

- The first skill-timing publication still consumes reviewed generated timing
  declarations because the legacy SQL lacks those fields. This is an explicit
  publication boundary, not a runtime dependency. A later reviewed SQL content
  artifact can remove it.
- `faction_area_experience_control.map_id` still has an `ON DELETE CASCADE`
  foreign key to legacy authoring table `world_boss_areas(map_id)`. Runtime
  boss definitions are revision-owned in `gameplay_world_boss_definitions`,
  but durable faction control remains structurally dependent on the legacy
  table. B20H must not archive or drop `world_boss_areas` until a forward
  migration introduces a stable non-authoring area identity, or explicitly
  stores the content revision; backfills and validates control rows; repoints
  the foreign key without cascade-loss risk; and passes reconciliation and
  rollback tests.
- The monster baseline exporter is a controlled offline review tool. It must
  never run in the server process or against production without an approved
  content-release procedure.
- Content hot reload is intentionally absent. New content takes effect after a
  new immutable publication and controlled worker restart/drain.
- Magic Jade IDs 11050-11094 are reviewed item templates and pinned pet facts.
  Migration 085 exposes the immutable appearance-to-Merge-cap mapping;
  migration 086 authorizes the separate durable `pet_appearance_change`
  runtime. Startup validates every Jade across both pinned revisions. The
  command consumes one selected authoritative bag item and changes species
  atomically, while the content views remain read-only projections.
