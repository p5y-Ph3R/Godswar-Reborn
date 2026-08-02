# B20G item-template content cutover

Date: 2026-08-01

Status: implemented and locally verified. B20H's real seven-day deployed
observation and final compatibility deletion remain pending.

## Outcome

Runtime item gameplay no longer reads generated client template seeds or
mutable item-policy tables. PostgreSQL owns one official, versioned item
publication, and each server process pins and validates exactly one manifest-v4
revision before it constructs gameplay systems.

The generated client baseline remains only in
`Infrastructure/Items/PostgresItemTemplateBaselinePublisher.cs`. It is a
fresh-install publisher, not recurrent runtime authority: under a PostgreSQL
advisory transaction lock it first checks for a valid official publication.
When one exists, the publisher validates its count and SHA-256 and returns it
without writing `item_templates` or advancing the publication pointer.

`item_templates`, `item_attribute_templates`, `equipment_rank_rules`, and
`holy_suit_effect_templates` remain staging/import sources, but no runtime
decision or projection reads them. Focused equipment and Holy Stone commands
use the process-pinned catalog directly. Character loadout, calculated-stat,
rank/aura, and reconciliation SQL join immutable definition tables with the
exact process-pinned revision parameter. Changing a staging row or moving the
official pointer therefore cannot alter an already running process.

## Storage and failure semantics

Migration `20260801_038_item_template_content_release` creates:

- `item_template_content_revisions` for declared count, source, and permanent
  seal state;
- `item_template_content_definitions` for the complete immutable item rows;
- `item_template_content_publication` for the singleton `items` pointer.

Migration `20260801_041_item_policy_content_release` upgrades the release to
manifest v2 and adds immutable, revision-owned definitions for:

- item attribute scaling;
- equipment rank thresholds and aura effects;
- Holy Suit effects.

The v2 revision hash covers templates and all three policy families. Declared
counts for every family are checked before publication and again while loading.

Migration `20260801_044_item_material_content_release` introduces manifest v3
and adds the revision-owned material policies previously held by
`ForgingMaterialCatalog`, `GearEnhancementMaterialCatalog`, and
`GearMentorMaterialCatalog`: forging material family/level/piece semantics,
Attribute Stone chains, Quartz Plate level transitions, Flame Spark and Water
Grain roles, Dust-to-Stone recipes, stack limits, and grant binding. The v3
hash covers templates, the three v2 policy families, and every material-policy
row. Migration 044 and its sealed v3 revisions remain immutable history; v1
and v2 revisions have zero material-policy rows, while v3 revisions have zero
material-recipe rows.

Forward-only migration
`20260801_045_item_material_recipe_content_release` introduces manifest v4. It
adds a declared recipe count and revision-owned Gear Mentor
`crystal_transform` and `gem_piece_combination` recipes, including source and
target item IDs and quantities. The v4 hash covers everything in v3 plus the
complete ordered recipe set. Historical v1, v2, and v3 releases remain sealed
and valid as history, but every live process running the current server must
pin a complete manifest-v4 publication.

Database triggers reject updates and deletes of revision content, late inserts
after sealing, concurrent template, per-policy-family, or recipe inserts beyond
the declared counts, incomplete publication, and deletion of the official
pointer. Runtime loading uses one read-only repeatable-read transaction, checks
every declared count, recalculates the canonical SHA-256, and fails startup on
mismatch.

Historical manifest-v1, manifest-v2, and manifest-v3 publications are upgraded
without editing their sealed rows. The publisher validates the corresponding
legacy hash and declared counts, leaves every sealed historical revision and
its definitions untouched, appends only absent reviewed skill-book/material
item projections, supplies the reviewed material policies where the older
manifest lacks them, adds the reviewed Gear Mentor recipes, publishes v4, and
atomically moves the pointer. Conflicting identifiers or corrupt legacy hashes
fail closed. Re-running any of the three upgrade paths is idempotent.

## Runtime dependency boundary

`IItemTemplateCatalog` and `PinnedItemTemplateCatalog` expose immutable item,
attribute, rank, and Holy Suit definitions. Their `IItemMaterialCatalog` is the
authoritative runtime boundary for material policies and Gear Mentor recipes.
`GameplayItemContent` derives the
developer-mount and Ride views once from that pinned catalog. The catalog is
passed through startup,
`PostgresGameStore`, `PostgresApplicationDataRuntime`, focused inventory/pet
executors, `GameClientHandler`, and `GameSessionRegistry`.

The following former seed consumers now use the injected catalog:

- `DeveloperMountCatalog`
- `EquipmentEligibility`
- `EquipmentSlots`
- `GearEnhancementPlanner`
- `GearMentorPlanner`
- `MountCatalog`

No mutable process-global catalog or mid-process revision switching was added.
Developer grants, gear enhancement, Gear Mentor decomposition, and Attribute
Stone creation all resolve material semantics through the injected pinned v4
catalog. Crystal transforms and gem-piece combinations likewise resolve only
through `IItemMaterialCatalog`. Durable receipt reconstruction for material
conversions uses those same process-pinned recipes as mutation planning, so
receipt output IDs and quantities cannot drift from the recipe that governed
the command. The three compiled material catalogs and the reviewed recipe
declarations are publisher inputs only.

This cutover deliberately does **not** claim that every forge constant is
database content. `EquipmentForgeCatalog` and `ForgingMaterialRuleCatalog`
remain code-versioned executable mechanics in this slice; the large generated
forge-rule catalog is outside migration 044. Moving executable probability or
calculation rules requires a separate reviewed design and compatibility plan.

## Verification

`ItemTemplateContentArchitectureChecks` proves the storage guards, one allowed
seed boundary, absence of generated item seeds in all six runtime consumers,
repeatable-read loading, defensive copies, revision mismatch rejection, and a
repository-wide ban on mutable item and item-policy runtime reads outside the
publisher, reviewed baseline bootstrap, and migrations. It also rejects
runtime use of the pointer-following `character_rank_summary` and
`character_stat_summary` compatibility views.

`PostgresItemTemplateContentIntegrationChecks` exercises the real migration and
publisher when `GODSWAR_TEST_POSTGRES_CONNECTION_STRING` is configured. It
proves v1-to-v4, v2-to-v4, and v3-to-v4 preservation,
skill-book/material append behavior, repeat idempotence, complete
policy/material/recipe views, absence of mutable-view dependencies,
source-decoy isolation, and immutable-row/pointer guards.

`PostgresCharacterSnapshotReaderIntegrationChecks` moves the official item
pointer to a deliberately different, schema-complete revision while a reader
and store remain pinned. The compatibility view changes, while their
calculated stats, ranks, and aura values remain byte-for-byte stable.

## Operational rule

Publishing a new item catalog is an explicit release operation: create and
validate a complete new immutable revision, then atomically change the pointer.
Editing staging tables alone cannot change a running process or an already
published revision. Compatibility views follow the official pointer only for
old binaries and administrative inspection; current runtime code does not.
Pointer moves and rollback require a coordinated drain/restart so all workers
pin one compatible content fingerprint.

The Holy Suit release advances the current binary to manifest v5. A coordinated
LocalDevelopment pointer rollback may select only a previously validated,
sealed, complete v4 revision; see
`docs/local-development-item-content-v4-rollback.md`. That pointer change does
not reverse forward-only migration 046. A binary whose migration catalog ends
at 045 still rejects the ahead database, so application rollback also requires
a tested schema-compatible v4 image or a verified pre-046 database restore.
