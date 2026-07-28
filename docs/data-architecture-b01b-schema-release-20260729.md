# B01B coherent schema release

**Completed:** 2026-07-29 NZST

**Roadmap task:** B01B

**Result:** passed

## Release identity

| Item | Identity |
|---|---|
| Source commit | `f2228f6fdd57a72fb6e355c8e79f1d2477350ed7` |
| Commit subject | `feat(pets): capture schema 022 with reproducible bootstrap` |
| Annotated tag | `data-b01b-pet-schema-022-verified` |
| Local Docker image | `reborn-server:data-b01b-schema-022-f2228f6` |
| Docker image digest | `sha256:0ae865e3b9dc8e1898248bfe8e98b24457f8b4ecdab699da903eb9738ce53d95` |
| Server assembly SHA-256 | `CBE995179A116F40E6144477AB909C12D0D58C52F75388D87E4633F613B54623` |
| Migration range | `20260723_000_legacy_schema_baseline` through `20260729_022_pet_level_progression` |
| Registered migrations | 23 |

The source commit contains the complete pet runtime, PostgreSQL persistence,
client compatibility tooling, migrations 013-022, the repaired embedded
bootstrap, and their tests. The tag message pins both the Docker image and
backup hashes.

## Current backup and restore proof

| Item | Value |
|---|---|
| Dump | `artifacts/data-architecture-b01b-post-022-20260729-110222/godswar-post-022.custom.dump` |
| Dump size | 1,355,316 bytes |
| Dump SHA-256 | `318A3269F325B83E35DFDDC210F10DB33C33D17F6646FBBE7156B1BBE9CE8DCE` |
| Receipt | `artifacts/data-architecture-b01b-post-022-20260729-110222/RESTORE_RECEIPT.md` |
| Receipt SHA-256 | `799E8016A499A2D21252C2B7684F68C31A51B28300BC84C2F57950CEC3FDD5AE` |
| Migration-ledger SHA-256 | `D493B1A957EF3C57CE11E5AC9253B17EC96191CD8D3561887428DF3FF527DA6F` |
| Table-digest ledger SHA-256 | `DA116F4133E216CD87355E36F498766E9E7DAA38052DD8AAFF784DA45BC7709F` |

The dump was captured with:

```text
pg_dump --format=custom --compress=9 --serializable-deferrable
        --no-owner --no-privileges
```

It restored with `pg_restore --exit-on-error --no-owner --no-privileges`
into PostgreSQL 17.9 using isolated tmpfs storage, no published port, and no
shared volume. Verification found:

- all 23 migration IDs, descriptions, and checksums identical;
- all 27 selected table counts and logical fingerprints identical;
- all 21 integrity queries clean on source and restore; and
- no remaining disposable container or volume.

This is a current logical recovery point, unlike the B01A protected backup
which stopped at migration 008. It is still local and Git-ignored; it is not
an off-host backup or a replacement for WAL/PITR and retention automation.

## Bootstrap repair

The original five embedded fragments omitted the packet metadata tables needed
by migrations 009, 013, 014, and 022. B01B adds the immutable structural
fragment:

`src/Godswar.Server/State/DatabaseMigrations/LegacySchemaBootstrap.006.sql`

It creates:

- `public.packet_capture_sessions`;
- `public.packet_transactions`, its identity key, cascade foreign key, and
  five indexes;
- `public.packet_opcodes`, composite key, and direction check;
- `public.set_packet_transaction_opcode_name()`; and
- `trg_packet_transactions_opcode_name`.

The six-fragment embedded stream is exactly 91,130 bytes with SHA-256:

`89A0954633CD65DE8AB9C72D74CFE65F651D24171DE60071102B3958B848C7C7`

Fragment 006 itself is 3,557 bytes with SHA-256:

`4EC647964E11C3B0FF9C96883C49A8F71E975CD784F7220A06A2F1D50D8EEE93`

Fragments 001-005 and all applied migration SQL/checksums remain unchanged.
The new fragment is deliberately structural-only. Historical
`011_packet_opcodes.sql` data is not a canonical current opcode catalog, so
copying it would make fresh and restored databases diverge in a different
way. The runtime does not read `packet_opcodes`; later migrations own the
specific rows they require.

## Exact applied migration ledger

Every checksum below matches the release catalog, the live database, the
current backup, and its isolated restore.

| Migration ID | SHA-256 |
|---|---|
| `20260723_000_legacy_schema_baseline` | `9461408F8808C7C5D2A19BD755BC8ABB57EDF38F078E4422067637ED4133C99D` |
| `20260723_001_mount_ride_compatibility` | `AF575B25F0F4C1FE81C4207D0FB66C9ED998F61933B207FDA442DAA4B695D1F9` |
| `20260723_002_mount_rank_guard` | `470D13D19B176E5164115FDBD02960F05655F7DA858FA6FC3962C8DEAF561C84` |
| `20260723_003_erebus_lion_mount` | `B793F56B7F0939C150C5D87E7D308239611FE793D7E6CF231E7B8ACC5A4906F3` |
| `20260723_004_remove_redundant_indexes` | `B03F10848397CB71726C1E2C8C9678A62D1C5EC3494C431A8DAB3696DE13C038` |
| `20260723_005_starter_consumable_templates` | `BC6EC9E541C733F229759D7771EAE241B325A712BB6322478D079B103EFA74C6` |
| `20260723_006_archive_legacy_character_kitbag` | `00A2ADC79BE94898B2BCE051D12815C4D790BB4BEDC231486051D24EB1F6AA8A` |
| `20260723_007_character_item_template_foreign_key` | `1A1A994F276A6EAA63E7E341E52C8313836F3BD984EFE3CCB0156D2021647B8C` |
| `20260723_008_zodiac_skill_grid_state` | `5AA2157E3BD3C5961D3B56E20520D5EC28C4E70C8F7E549FBE1CDAD5EDFA0390` |
| `20260728_009_skill_cast_interrupt_opcode` | `D8B13DD774BB3651D5EBC70C5B7BEED6A0DA904E903710352C8A145D9CD69745` |
| `20260728_010_pet_foundation` | `B8D27EDC05F65EDDB233AB2E08DAA46A4CD33462F2B9797EA8518DCD6582A8ED` |
| `20260728_011_pet_aptitude_range` | `FBEFBE88B9EF99F9F0EF015E75EA3BCDCF120F5DD618195FB97B1B684D98E71C` |
| `20260728_012_pet_aptitude_catalog` | `32CEA927FDFAC4D3F74CF502074777F4B9FE252611E2AE11AC9DA902D5A1FDA1` |
| `20260728_013_owned_pet_bootstrap_opcode` | `C470A86EF71771BD474AB272F8CD406EF98E31409EE316EC650133DFD0A30044` |
| `20260728_014_pet_presence_protocol` | `DB8147B0288F6F71686DC29E070A2F05AC6ED147F503A773D1A8BA461B7272C4` |
| `20260728_015_pet_presence_audit_operation` | `54FCC215ADE65398B464B13830D3A6BAA2601C187FC37C829B453A2FA4A4DC35` |
| `20260728_016_pet_growth_policy` | `78B985BEC1AADD431417AB77A1A326D42AF584D0962E6308EFD80D7ECF2DFB53` |
| `20260728_017_pet_growth_midpoint_backfill` | `C733FFED31504C9FCB5115EDB931A585896AA1D3FCA73DA6C2EDB667BF5C13F3` |
| `20260728_018_pet_growth_policy_v2` | `656868F264E75D81A582CEE14C91126907A8A28E1D20649FF351B0914C5993CC` |
| `20260728_019_pet_initial_savvy_policy` | `22D76D95138EF56F7B66496D0D00328203C5FEAA6CEC28BE57A201833D024AA5` |
| `20260729_020_pet_savvy_semantics` | `847BD78F4792AB9EC28DEFE3E94EB2FB4FDCDBC931FB92FF6DBF35FC98D1BED6` |
| `20260729_021_pet_savvy_semantics_hardening` | `309A8A24F8F02D17D87D93E623319BCA2834F151976095624C0753FA77F60019` |
| `20260729_022_pet_level_progression` | `86C581294D06B00E64AA8C7F84C79019521BCA2E3B860B09FBA77942E5BD288D` |

## Verification results

### Build and clean checkout

- Main worktree Release build: 0 warnings, 0 errors.
- Detached clean checkout at `f2228f6`: restore and Release build passed.
- Clean checkout bootstrap identity check passed.
- Clean checkout Git status before removal: zero changes.
- No staged release file exceeded 20 KB or 600 lines.
- `git diff --check`: passed.
- Staged secret-pattern scan: zero matches.

The clean checkout's initial `--no-restore` attempt correctly reported missing
`project.assets.json`; the subsequent normal restore/build passed. The SDK was
the preview `10.0.100-rc.2.25502.107`.

### Protocol and client compatibility

- Complete non-database protocol suite: 177 passed, 0 failed.
- Pet level/savvy client patch: 162 assertions passed.
- Current restored PostgreSQL suite: 24 passed, 0 failed.
- Migration foundation and embedded-resource identity: passed.

### Migration paths

The permanent
`PostgresSchemaReleaseIntegrationChecks` invokes the actual
`PostgresGameStore.EnsureSeedDataAsync`, not a SQL-only imitation. It checks
the exact ledger, packet tables/FK/indexes/check/function/trigger, constraints,
indexes, player identity, inventory, raw captured packet bytes, current pet
rows, and second-start idempotence.

| Initial state | Input | Result |
|---|---|---|
| 0 migrations, truly empty vanilla PostgreSQL 17 | no Compose init mount | reached exact 23; second startup no-op |
| 9 migrations through 008 | protected dump SHA-256 `7EC9775B...AAE` | reached exact 23; identity, inventory, and captured bytes preserved |
| 23 migrations through 022 | current dump SHA-256 `318A3269...DCE` | stayed at exact 23; durable fingerprints preserved |

All three disposable containers were removed.

Migration-specific rollback/reconciliation tests also ran on their required
prefixes or original snapshots:

- 018 growth-v2: passed from prefix 017;
- 019 initial-savvy: passed from prefix 018;
- 020 savvy-semantics: passed after restoring the pre-019 snapshot
  `8A5D84D3...C05E` and applying 019;
- 021 savvy hardening: passed on snapshot `1B527FAF...DD41`; and
- 022 pet-level progression: passed on snapshot `870CAAB0...B56`.

An initial 020 test attempt on a completely empty pet table was rejected by
the test's required managed-pet precondition. The canonical run used the
original pre-migration pet snapshot and passed; this was a fixture-selection
issue, not a migration failure.

### Exact image startup

The tagged image was started without published ports against two isolated
PostgreSQL stacks:

| Database | Login listener | Game listener | History |
|---|---:|---:|---|
| Truly empty | ready | ready | exact 23 through 022 |
| Restored current backup | ready | ready | exact 23 through 022 |

Both servers, databases, and private Docker networks were removed. The live
`godswar-postgres` stayed healthy and the existing `godswar-server` was not
replaced.

## Remaining operational gaps

- The current backup is local and contains sensitive player data. Add
  encrypted off-host retention plus WAL/PITR and scheduled restore drills.
- The exact release image is built and tagged locally but is not deployed to
  the running game server.
- Fresh and historical databases can retain different non-runtime
  `packet_opcodes` research rows. If canonical content is required, introduce
  a reviewed forward migration 023; never edit an applied checksum.
- Pet persistence in this release is PostgreSQL-authoritative. The JSON
  provider intentionally has compatibility stubs rather than equivalent
  durable pet functionality.
- B03 still needs to make disposable PostgreSQL tests mandatory in CI rather
  than relying on explicitly supplied local connection strings.

## Rollback

Use the prior matching code tag and a backup captured for that code/schema
pair. For this release, the verified recovery anchor is the current post-022
dump above. Never remove or rewrite rows in `schema_migrations`; schema repair
remains forward-only.
