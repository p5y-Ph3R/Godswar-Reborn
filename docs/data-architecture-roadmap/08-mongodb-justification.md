# 8. MongoDB justification

**MongoDB should not be introduced during the initial migration.**

## 8.1 Concrete candidates found

| Candidate | Actual access/update pattern | PostgreSQL relational | PostgreSQL JSONB | MongoDB | Decision |
| --- | --- | --- | --- | --- | --- |
| Item/attribute/skill/talent templates | Stable numeric IDs, relational references, load by ID/class; bounded flexible stats | Strong for identity/FKs/queryable fields | Already suitable for flexible metadata/stats | Adds another deployment and cross-store content release | Keep PG relational + JSONB |
| NPC dialogue/function/spawn definitions | Load by map/NPC/function; stable references; content published as a set | Good normalized header/edge tables | Good for bounded dialogue payload/appearance metadata | Document nesting is possible but not evidence that PG is inadequate | Keep PG; version content releases |
| Map routes/safe areas/links | Graph-like but small, relation/query by source map and address point | Relational rows and indexes are natural | Optional route metadata | No demonstrated document-atomic workload | Keep PG |
| Monster/world-boss definitions | Load by map/template; stable IDs; JSON-like stats | Relational identity/spawn/control | Good for variable stat metadata | Transactions with world control would still require PG | Keep PG |
| Pet templates/growth policy | Stable catalog and relationships to owned pets | Natural fit | Optional policy metadata | No independent document access pattern | Keep PG |
| Item/pet audit before/after payloads | Append, lookup by operation/player/time; variable snapshot shape | Header/index columns | Existing JSONB payload is appropriate | Would create cross-store write for the same transaction | Keep audit row and JSONB in PG |
| Protocol packet captures | Append-heavy variable bytes/metadata, research lookup | Current tables work at present scale | Metadata may be JSONB; payload can be bytea/object storage | Mongo could store documents, but it does not solve cheap binary archive/retention by itself | Separate research schema/database and later object/archive/analytics platform; not Mongo now |
| Generated content seed source | Build-time/generated C# then startup upsert | Versioned relational content release is adequate | Can contain source metadata | Mongo does not fix source/release reproducibility | Move oversized immutable seed data to versioned resources if needed |

The following completes the operational comparison for every candidate:

| Candidate | Data size and lifetime | Consistency and transaction requirements | Indexing | Operational complexity, backup, and recovery | Developer experience |
| --- | --- | --- | --- | --- | --- |
| Item/attribute/skill/talent templates | Current bounded catalogs; retain current and prior compatible releases | One content revision must remain consistent with stable IDs and item/player FKs | B-tree ID/class indexes; targeted JSONB expression/GIN index only for demonstrated predicates | Already included in PG backup/PITR; Mongo would add release synchronization, monitoring, backup, and restore | Existing Npgsql/SQL/seed code and tests favor PG |
| NPC dialogue/function/spawn definitions | Bounded per map/NPC; versioned long-lived content | Publish related dialogue, function, spawn, and map references coherently | Map/NPC/function B-tree indexes; optional indexed JSONB discriminator | PG restore keeps relationships together; Mongo adds a second content migration/recovery toolchain | Existing relational loaders and generated definitions favor PG/JSONB |
| Map routes/safe areas/links | Small graph-like catalog; long-lived by content revision | Source/target/link constraints and compatible map release matter | Source map, destination map, route/address-point indexes | PG backup is sufficient; Mongo adds no recovery advantage | SQL joins and constraints are clearer than document duplication |
| Monster/world-boss definitions | Bounded catalog plus separate durable world-control rows | World-control/player reward transactions remain in PG; definitions must match revision | Map/template/type indexes; JSONB indexes only if queried | Splitting definitions into Mongo risks version skew during restore/rollout | Current hydrators and Npgsql loaders favor PG |
| Pet templates/growth policy | Small/medium catalog; retain versions needed to explain owned pets | Owned pets, templates, and policy version need relational consistency | Template/species/rarity/version indexes | One PG backup preserves player-to-template integrity; Mongo complicates incident recovery | Current migrations, constraints, and code are relational |
| Item/pet audit payloads | Append growth; long retention determined by security/economy policy | Audit must commit atomically with the authoritative PG mutation | Player/operation/time B-tree indexes; JSONB only for exceptional investigation predicates | PG partitions/archive can manage growth; cross-store audit dual write is unsafe and Mongo needs separate retention/restore | Existing JSONB before/after payloads are straightforward |
| Protocol packet captures | Potentially largest and append-heavy; retention is unresolved, not necessarily permanent | No transaction with live player value; capture batch consistency only | Session/time/opcode metadata indexes; payload search should be evidence-driven | A separate research PG database plus object/archive storage gives clearer lifecycle; Mongo still requires its own backup/restore and binary archive strategy | Existing capture/import SQL works; a purpose-built archive may eventually be better than either JSONB or Mongo |
| Generated content seed source | Large source artifacts but released runtime data is bounded; source retained with build | Build/content manifest must identify one immutable release | Runtime indexes follow the target content tables; source files need no DB index | Source control/artifact storage plus PG release backup is simpler; Mongo does not solve reproducible builds | Embedded resources or generated chunks integrate with current build better than a new document client |

## 8.2 Why JSONB is sufficient today

All discovered flexible structures have stable ownership IDs and are read with relational context. PostgreSQL can:

- enforce catalog identity and foreign keys;
- atomically publish compatible metadata with related rows;
- query selected JSONB properties;
- index only demonstrated predicates;
- back up player state and content together;
- avoid cross-database version skew.

The repository has no implemented player-generated content, GM collaborative document editor, unbounded dialogue tree, independently scaled content-read service, or document-level write pattern that requires MongoDB.

## 8.3 Reconsideration evidence

A future MongoDB ADR must show:

1. independent documents with substantially varying schemas;
2. dominant whole-document reads/writes or nested updates;
3. document sizes/cardinality and measured PostgreSQL JSONB limitations;
4. indexes and query plans that cannot be served cleanly by PG;
5. no need for atomic mutation with authoritative player/economy rows;
6. an owner service, document version field, validation schema, migration process, backup/restore plan, retention, and outage behavior;
7. operational staffing/cost that is justified by the workload.

Player-generated content may become a candidate, but large binary assets belong in object storage, search belongs in a search platform, and telemetry archives belong in an analytics/archive platform. MongoDB is not a generic miscellaneous-data sink.
