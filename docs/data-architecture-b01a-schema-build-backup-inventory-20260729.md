# B01A schema, build, and backup inventory

**Captured:** 2026-07-29 10:55 NZST

**Roadmap task:** B01A

**Repository:** `C:\Reborn`

**Git branch / HEAD:** `main` / `54f2d4b62f5382556e61f617a05a3f9c7f329bac`

**Assessment result:** evidence collection passed; coherent-release readiness failed

## Scope and safety

This is the read-only baseline required by
`ROADMAP_DATA_ARCHITECTURE.md` before B01B changes migration or release
state. The audit:

- queried the connected PostgreSQL database only inside read-only
  transactions;
- compared Git HEAD, the dirty worktree, and applied migration history;
- verified a protected backup's hash and receipt;
- restored that backup only into an isolated disposable PostgreSQL
  container with tmpfs storage and no published host port; and
- built and tested the current worktree without starting a migrator.

It did **not** mutate the connected `godswar` database, its Docker volume,
production code, migration history, or existing backup artifacts. The
disposable restore container was removed after verification.

## Executive finding

The connected database and current dirty worktree agree on all 23 migration
IDs and checksums through `20260729_022_pet_level_progression`. Git HEAD
contains only the first 13 migrations through
`20260728_012_pet_aptitude_catalog`. Migrations 013-022 and their dependent
pet runtime work are not present in any reachable commit.

Therefore:

1. a server built from HEAD cannot open the current database because the
   migration runner correctly rejects a database ten migrations ahead;
2. no protected, independently restored recovery point was proven for the
   database's current post-022 state;
3. the embedded empty-database bootstrap is incomplete and fails before the
   latest migration chain can finish; and
4. B01B must cut one coherent release rather than commit individual migration
   files piecemeal.

## Runtime and database baseline

| Item | Observed value |
|---|---|
| .NET SDK | `10.0.100-rc.2.25502.107` |
| Server image | `reborn-server`, image ID `f3a4a14b196b...`, running |
| PostgreSQL image | `postgres:17-alpine`, image ID `c7526c0f6c3f...`, healthy |
| PostgreSQL server | 17.9 |
| Database / user | `godswar` / `godswar` |
| Data volume | `reborn_godswar-postgres-data` |
| Postmaster start | `2026-07-28 03:35:40.728835Z` |
| Database size | 43,726,515 bytes (42 MB) |
| Relations | `public`: 54 tables, 21 views, 11 sequences; `legacy`: 1 table |
| Constraints | 184 total; 0 unvalidated |
| Indexes | 116 total; 0 invalid and 0 not-ready |
| Identity sequences | 11/11 at or ahead of their table maximum |

The worktree is intentionally dirty with ongoing pet-system work. This audit
preserved all pre-existing changes.

## Exact migration comparison

Checksums use `PostgresSchemaMigration.ComputeChecksum`: normalize line endings
to LF, trim SQL, then SHA-256. Every database checksum equals the currently
registered worktree SQL.

| Migration ID | SHA-256 checksum | In HEAD | Current source state | Applied | Match |
|---|---|---:|---|---:|---:|
| `20260723_000_legacy_schema_baseline` | `9461408F8808C7C5D2A19BD755BC8ABB57EDF38F078E4422067637ED4133C99D` | Yes | tracked | Yes | Yes |
| `20260723_001_mount_ride_compatibility` | `AF575B25F0F4C1FE81C4207D0FB66C9ED998F61933B207FDA442DAA4B695D1F9` | Yes | tracked | Yes | Yes |
| `20260723_002_mount_rank_guard` | `470D13D19B176E5164115FDBD02960F05655F7DA858FA6FC3962C8DEAF561C84` | Yes | tracked | Yes | Yes |
| `20260723_003_erebus_lion_mount` | `B793F56B7F0939C150C5D87E7D308239611FE793D7E6CF231E7B8ACC5A4906F3` | Yes | tracked | Yes | Yes |
| `20260723_004_remove_redundant_indexes` | `B03F10848397CB71726C1E2C8C9678A62D1C5EC3494C431A8DAB3696DE13C038` | Yes | tracked | Yes | Yes |
| `20260723_005_starter_consumable_templates` | `BC6EC9E541C733F229759D7771EAE241B325A712BB6322478D079B103EFA74C6` | Yes | tracked | Yes | Yes |
| `20260723_006_archive_legacy_character_kitbag` | `00A2ADC79BE94898B2BCE051D12815C4D790BB4BEDC231486051D24EB1F6AA8A` | Yes | tracked | Yes | Yes |
| `20260723_007_character_item_template_foreign_key` | `1A1A994F276A6EAA63E7E341E52C8313836F3BD984EFE3CCB0156D2021647B8C` | Yes | tracked | Yes | Yes |
| `20260723_008_zodiac_skill_grid_state` | `5AA2157E3BD3C5961D3B56E20520D5EC28C4E70C8F7E549FBE1CDAD5EDFA0390` | Yes | tracked | Yes | Yes |
| `20260728_009_skill_cast_interrupt_opcode` | `D8B13DD774BB3651D5EBC70C5B7BEED6A0DA904E903710352C8A145D9CD69745` | Yes | tracked | Yes | Yes |
| `20260728_010_pet_foundation` | `B8D27EDC05F65EDDB233AB2E08DAA46A4CD33462F2B9797EA8518DCD6582A8ED` | Yes | tracked | Yes | Yes |
| `20260728_011_pet_aptitude_range` | `FBEFBE88B9EF99F9F0EF015E75EA3BCDCF120F5DD618195FB97B1B684D98E71C` | Yes | tracked | Yes | Yes |
| `20260728_012_pet_aptitude_catalog` | `32CEA927FDFAC4D3F74CF502074777F4B9FE252611E2AE11AC9DA902D5A1FDA1` | Yes | tracked | Yes | Yes |
| `20260728_013_owned_pet_bootstrap_opcode` | `C470A86EF71771BD474AB272F8CD406EF98E31409EE316EC650133DFD0A30044` | No | untracked `PetProtocol.cs` | Yes | Yes |
| `20260728_014_pet_presence_protocol` | `DB8147B0288F6F71686DC29E070A2F05AC6ED147F503A773D1A8BA461B7272C4` | No | untracked `PetPresence.cs` | Yes | Yes |
| `20260728_015_pet_presence_audit_operation` | `54FCC215ADE65398B464B13830D3A6BAA2601C187FC37C829B453A2FA4A4DC35` | No | untracked `PetPresenceAudit.cs` | Yes | Yes |
| `20260728_016_pet_growth_policy` | `78B985BEC1AADD431417AB77A1A326D42AF584D0962E6308EFD80D7ECF2DFB53` | No | untracked `PetGrowth.cs` | Yes | Yes |
| `20260728_017_pet_growth_midpoint_backfill` | `C733FFED31504C9FCB5115EDB931A585896AA1D3FCA73DA6C2EDB667BF5C13F3` | No | untracked `PetGrowthBackfill.cs` | Yes | Yes |
| `20260728_018_pet_growth_policy_v2` | `656868F264E75D81A582CEE14C91126907A8A28E1D20649FF351B0914C5993CC` | No | untracked `PetGrowthV2.cs` | Yes | Yes |
| `20260728_019_pet_initial_savvy_policy` | `22D76D95138EF56F7B66496D0D00328203C5FEAA6CEC28BE57A201833D024AA5` | No | untracked `PetInitialSavvy.cs` | Yes | Yes |
| `20260729_020_pet_savvy_semantics` | `847BD78F4792AB9EC28DEFE3E94EB2FB4FDCDBC931FB92FF6DBF35FC98D1BED6` | No | two untracked `PetSavvySemantics*` files | Yes | Yes |
| `20260729_021_pet_savvy_semantics_hardening` | `309A8A24F8F02D17D87D93E623319BCA2834F151976095624C0753FA77F60019` | No | untracked `PetSavvyHardening.cs` | Yes | Yes |
| `20260729_022_pet_level_progression` | `86C581294D06B00E64AA8C7F84C79019521BCA2E3B860B09FBA77942E5BD288D` | No | untracked `PetLevel.cs` | Yes | Yes |

`PostgresSchemaMigrationCatalog.cs` is tracked but modified to register
013-022. `git log --all -S <migration-id>` finds no reachable commit for any
of those ten IDs. Their migration partials, tests, and dependent pet runtime
files must be reviewed and captured together.

The separate `server_data_migrations` history is:

| Key | Applied UTC | Affected rows |
|---|---|---:|
| `20260718_repair_sparta_starting_map` | `2026-07-18 12:08:29.375219Z` | 2 |
| `20260721_legacy_character_kitbag_import` | `2026-07-21 00:27:24.110532Z` | 0 |

## Empty-database bootstrap blocker

The five tracked embedded bootstrap resources are unchanged from HEAD:

| Resource | Bytes | SHA-256 |
|---|---:|---|
| `LegacySchemaBootstrap.001.sql` | 18,103 | `BC9FAF894AED75B27D891BB2C75FD2FFCEA07202F29948BE3CD377E392EF77D9` |
| `LegacySchemaBootstrap.002.sql` | 15,903 | `D35AFCED157C5921F978D0CAA21DB995D218118B4D098C205AA2AE285F0CDB8F` |
| `LegacySchemaBootstrap.003.sql` | 18,434 | `C68D1B66A9AD544922BCC879FE0244AB6C24D76B1C7BA42044B092CB481B5A3F` |
| `LegacySchemaBootstrap.004.sql` | 15,297 | `98A7299A55052F6654C1C39B12D5823141864F98259DAECB969CE5EC5973EFC1` |
| `LegacySchemaBootstrap.005.sql` | 19,836 | `948382D09ECC95EA60096BF8E7BE3E8E971147609BDB986EA56ED8CB7487792C` |

Combined: 87,573 bytes,
`F10E4B8752506AA10E72D88C62D49850A3F9B3197ECB0E2CBD693AFC34B9B09A`.

The embedded resources do not create `packet_capture_sessions`,
`packet_transactions`, or `packet_opcodes`. Migrations 009, 013, 014, and
022 reference packet tables, and `PostgresGameStore.WorldSync.cs` reads
`packet_transactions`. Those tables currently arrive only through historical
files in `database/postgres`, which the runtime migration runner explicitly
does not scan.

Consequently, Docker can initialize through its mounted
`/docker-entrypoint-initdb.d` scripts, but the runtime-only fresh-database path
is not self-contained. It appears to reach 008 and then fail at 009 with an
undefined `packet_opcodes` relation. Existing tests seed prerequisite tables
and do not prove an empty database can migrate to 022.

## Current row and integrity snapshot

| Category | Rows |
|---|---:|
| Accounts / characters | 11 / 9 |
| Character items | 105: 64 equipped, 41 bag, 0 storage |
| Character-item audit rows | 244 |
| Archived legacy kitbags | 6 |
| Character skills / talents / Zodiac grids | 41 / 37 / 22 |
| Experience modifiers | 8 |
| Pets / pet stat rows / pet skills / pet audit rows | 1 / 6 / 1 / 81 |
| Pet aptitude templates / pet templates | 16 / 45 |
| Item templates / attributes | 1,363 / 195 |
| Item qualities / grades | 20 / 25 |
| Maps | 81 |
| Monster templates / spawn packets | 1,246 / 270 |
| NPC spawn references / packets | 2,104 / 84 |
| Packet transactions | 10,105 |

Fingerprints:

- `character_items`:
  `105:55a573a399ee32b50cc0118d351c5051`
- archived kitbag, including `archived_at`:
  `6:7e24205448ca84a8529e550d2aed793f`

Read-only checks found zero:

- orphaned characters, items, skills, talents, Zodiac rows, modifiers, pet
  rows, pet stats, pet skills, or pet bonuses;
- duplicate character names or authoritative inventory slots;
- negative currency, non-positive item stacks, or missing item templates;
- pets with incomplete six-stat groups, missing owner/species/aptitude,
  out-of-bracket growth, invalid savvy provenance/totals, invalid
  carry/summon/merge combinations, or duplicate audit request IDs; and
- unvalidated constraints, invalid indexes, or lagging identity sequences.

Two observations are documented, not classified as corruption:

- 12 consumable rows use `item_quality=0`, `item_grade=10`;
- two characters have current HP/MP above stored base maxima because equipment
  and runtime bonuses are derived separately.

Five accounts had `login_status=1` while the game server was running, so this
audit cannot classify them as stale presence.

## Backup and isolated restore evidence

Protected recovery artifact:

| Item | Value |
|---|---|
| Dump | `C:\ProgramData\RebornSecureNetworkBackups\controlled-host-database\godswar-20260726-141154.dump` |
| Receipt | `database-backup-godswar-20260726-141154-7EC9775B2F6F0836.json` |
| Size | 1,340,532 bytes |
| Expected and actual SHA-256 | `7EC9775B2F6F08361F606FEC2968623573A632D2FCD02EBDD12327B6407F4AAE` |
| Protection check | `Protected`; receipt schema v2 and ACL/reader SID valid |

Restore drill:

- target: unique `postgres:17-alpine` container
  `reborn-b01a-restore-3c73f301118c`;
- isolation: tmpfs `PGDATA`, no host port, shared volume, or connection to the
  live database;
- restore: `pg_restore --no-owner --no-privileges --exit-on-error`;
- result: success on PostgreSQL 17.9;
- restored schema: 44 public tables, 21 views, 0 unvalidated constraints;
- restored history: 9 migrations, baseline through
  `20260723_008_zodiac_skill_grid_state`;
- restored counts: 9 accounts, 7 characters, 87 character items
  (56 equipped, 31 bag), 1,303 item templates, 10,105 packet transactions,
  and 0 missing item-template references; and
- cleanup: the disposable container was forcibly removed; no
  `reborn-b01a-restore-*` container remains. The existing database stayed
  healthy and the game server stayed running.

This proves that artifact is authentic and restorable. It is **not a current
recovery point**: it predates migrations 009-022 and current player/pet
changes. The live Docker volume is not a backup.

The repository has tooling to protect and verify an already-created dump, but
no script was found that creates/schedules `pg_dump`, manages retention, or
runs recurring restore drills.

## Build and test evidence

Executed against the preserved dirty worktree:

```text
dotnet build GodswarServer.sln --configuration Release --nologo
Build succeeded. 0 warnings, 0 errors.

dotnet run --project tests/Godswar.Server.ProtocolChecks/\
Godswar.Server.ProtocolChecks.csproj --configuration Release --no-build -- \
"PostgreSQL migration safety foundation"
PASS PostgreSQL migration safety foundation
Protocol checks: 1 passed, 0 failed
```

The build used a preview .NET SDK and emitted the standard informational
`NETSDK1057` preview-support message. No current empty-database end-to-end test
was run because the known prerequisite-table defect would make that a
destructive B01B repair task, not B01A evidence collection.

## B01A decision and B01B entry gates

B01A is complete as a read-only evidence task. B01B must not mutate schema or
publish a release until all of these gates are satisfied:

1. Create a transactionally consistent backup of the current post-022
   database, pin its SHA-256/receipt, restore it in isolation, and compare the
   23-row migration history plus current data invariants.
2. Review the complete dirty pet-system slice and capture migrations 013-022,
   catalog registration, tests, runtime code, and documentation in one
   coherent commit/release. Do not rewrite any applied ID or checksum.
3. Add packet metadata to the authoritative embedded bootstrap or introduce a
   prior forward migration that works from the true embedded baseline.
4. Prove both paths on disposable PostgreSQL 17:
   - genuinely empty database to the exact 23-migration history;
   - restored representative backup to that same exact history.
5. Build the release from a clean checkout and produce a manifest tying Git
   commit, image digest, migration IDs/checksums, content identity, and backup
   receipt together.
6. Confirm the prior matching binary plus verified backup as the rollback
   point. Migration history remains forward-only.

The B04 authentication/profile hardening described by the roadmap remains a
separate task; it must not be smuggled into this migration reconciliation
without its own compatibility tests.
