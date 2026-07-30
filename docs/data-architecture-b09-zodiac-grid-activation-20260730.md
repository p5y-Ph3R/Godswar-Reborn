# B09 durable Zodiac skill-grid activation increment

Date: 2026-07-30

Source base: `8a0b8b3b96db59526b954f5135389fbd4faeaf73`

Status: implemented and verified; B09 remains open

## Outcome

Zodiac skill-grid activation now has a PostgreSQL command boundary for both
raw legacy TCP and the TLS-wrapped legacy transport:

```text
stock SID 100 request
  -> authenticated account and selected character
  -> bounded family-19 command envelope
  -> stable inactive-to-active transition identity
  -> locked PostgreSQL character/grid transaction
  -> grid mutation plus Gold ledger when paid
  -> permanent audit, inbox receipt, and strict outbox event
  -> commit
  -> current authoritative in-memory projection
  -> stock client acknowledgement and full sync
```

The client chooses only a zero-based grid index. It does not choose the
account, character, existing level, selected skill, activation cost, Gold
balance, wallet revision, or outcome.

This increment adds no database migration. It deliberately reuses the B08
`command_audit`, `command_inbox`, and `outbox_events` foundation and the B09
`wallet_revision` and `character_currency_ledger` foundation. PostgreSQL
remains the sole durable owner of the grid and Gold balance.

## Native intent and server-owned policy

The stock client calls
`GameAPI:ConsEventRequest(0, 100, index, -1)`. The managed golden request for
grid 1 is:

```text
18003928000000000000640001000000FFFFFFFF00000000
```

It is a 24-byte opcode-10297 packet with module `0`, SID `100`,
`v1=gridIndex`, `v2=-1`, and `v3=0`. The command boundary accepts grid
indices `0..15`. The `v2` and `v3` values are not trusted as state, cost, or
balance.

`ZodiacSkillGridCatalog` derives the shipped `UnlockG` premium-Gold costs:

```text
grids  0..3: 0, 2300, 7200, 14400
grids  4..7: 0, 2300, 7200, 14400
grids 8..11: 0, 0, 920, 920
grids 12..15: 0, 0, 920, 920
```

Premium Gold is `character_base."Stone"`, not Silver
(`character_base."Money"`). Grid state is owned by
`character_zodiac_skill_grids`. Only an inactive grid may transition from
level `0` to `1`; its selected-skill value is retained.

The client protocol call and costs are corroborated by the shipped files
`C:\Godswar Origin\Localization\en_us\UI\XML\SkillTrainProc.lua` and
`SkillTrainConfig.lua`. The packet vector is a managed protocol golden, not a
claim that a retail activation request appears in the stored traffic
captures.

## Why legacy retry identity is sufficient here

The stock request has no operation UUID. This command nevertheless has a
truthful stable identity because one grid has only one legitimate
inactive-to-active transition.

`ZodiacSkillGridActivationCommandEnvelope` binds:

- command family 19;
- the server-authenticated account and character;
- the requested grid index; and
- expected level `0`.

The operation scope and canonical request encode `gridIndex` and
`expectedLevel` as bounded big-endian integers. The domain-separated SHA-256
operation ID therefore survives reconnect and transport changes while
remaining distinct for another account, character, family, grid, or expected
state. The command is explicitly classified as
`LegacyAggregateVersion`, not as a fabricated client-operation UUID.

This identity is intentionally limited to activation. Grid upgrades and
skill selection are repeatable operations and must not reuse the same
one-shot aggregate identity.

## Atomic PostgreSQL transaction

`PostgresZodiacSkillGridActivationCommandExecutor` performs one transaction:

1. Validate the bounded command envelope and its digests.
2. Lock the owning `character_base` row by account and character with
   `FOR UPDATE`.
3. Look for an existing family-19 inbox result before evaluating current
   state.
4. Ensure the character economy baseline exists.
5. Read the selected grid and derive the outcome from authoritative Gold,
   level, selected skill, and the server cost catalog.
6. For success, insert permanent audit and inbox evidence.
7. Insert the grid row or update it only when the current level is still
   zero.
8. For a paid grid, compare-and-set Gold and increment `wallet_revision`
   once, then append one negative `gold` ledger entry.
9. Insert one strict outbox event and commit.

Every mutation requires exactly one affected row. The owning character lock
serializes concurrent exact requests, while the grid update predicate guards
the transition itself.

Free activations still receive audit, inbox, and outbox evidence, but do not
invent a zero-delta currency ledger row or advance `wallet_revision`.

Already-active and insufficient-Gold outcomes roll back without persisting a
terminal inbox rejection. This is required for correctness: insufficient
Gold is transient, so the same stable command must be allowed to succeed
after an authoritative top-up. Wrong-owner requests fail without inventing a
Gold or grid projection.

## Replay, uncertainty, and response ordering

An exact reconnect retry validates the stored request hash, canonical receipt
hash, audit reference, character, and grid. It increments a bounded duplicate
counter and returns:

- the original immutable receipt as commit evidence; and
- the current authoritative Gold, wallet revision, grid level, and selected
  skill as the client projection.

Using the current projection prevents an old activation receipt from
overwriting legitimate later Gold spending or grid upgrades. Concurrent
identical commands produce one commit and one replay; they cannot debit or
publish twice. Envelope hash tampering fails before database work. If a
stored operation ever presents a different valid request hash, replay fails
closed and records a bounded conflict count.

Fault injection covers every transaction stage. A fault before commit rolls
back grid, wallet, ledger, audit, inbox, and outbox together. A fault reported
after commit is recovered by the same stable command identity and returns the
stored receipt without repeating the mutation.

The packet handler sends the native SID `100` success packet only for a newly
committed activation. It deliberately suppresses SID `100` for a duplicate,
because the stock client treats every such packet as a new success
transition. Committed results send:

```text
SID 100 -> authoritative Player Status Gold -> full Zodiac sync
```

Duplicates and authoritative precondition failures send the current Player
Status projection followed by the full Zodiac sync, without a false success
animation. Invalid intent, missing ownership, cancellation, and provider
failure cannot fabricate success. A full sync remains the final Zodiac
packet so stale client state is repaired.

## Outbox contract and runtime composition

The event contract is:

| Field | Value |
| --- | --- |
| consumer | `zodiac_grid_activation_v1` |
| aggregate type | `zodiac_grid_activation` |
| aggregate key | `character:{characterId}:grid:{gridIndex}` |
| aggregate revision | `1` |
| event type | `zodiac.skill_grid_activated` |
| contract version | `1` |
| ordering | strict |

Revision `1` is truthful because this aggregate represents exactly one
activation transition. Later grid upgrades need their own versioned command
and event model rather than pretending they are more activation revisions.

`ZodiacSkillGridActivationOutboxConsumer` currently validates the event,
payload, event ID, aggregate identity, contract version, and revision. It
does not become another state owner. Future caches or projections must use a
separate consumer and remain reconstructable from PostgreSQL.

`PostgresApplicationDataRuntime` owns the executor and registers the outbox
consumer. Both raw and secure `GameClientHandler` compositions receive the
same application contract. A non-PostgreSQL/local runtime retains the
serialized compatibility store path; it does not claim durable inbox,
ledger, or cross-process replay semantics.

## Repository evidence

- `src/Godswar.Server/Application/Zodiac/` defines bounded family-19 intent,
  identity, result, receipt, and executor contracts.
- `src/Godswar.Server/Infrastructure/Zodiac/` owns transaction, evidence,
  codec, probe, and outbox-consumer implementation.
- `src/Godswar.Server/Game/GameClientHandler.Zodiac.cs` performs transport
  decoding, invokes the application contract, applies only the returned
  authoritative projection, and enforces response ordering.
- `src/Godswar.Server/State/ZodiacSkillGridActivation.cs` remains the shared
  authoritative activation policy and shipped cost catalog.
- `tests/Godswar.Server.ProtocolChecks/ZodiacSkillGridActivationCommandContractChecks.cs`
  covers stable identity, bounds, digest conflicts, and receipt invariants.
- `tests/Godswar.Server.ProtocolChecks/ZodiacSkillGridActivationDurableHandlerChecks*.cs`
  cover commit, replay, transient/missing-owner rejection, fallback, packet
  ordering, and failure-without-success behavior.
- `tests/Godswar.Server.ProtocolChecks/PostgresZodiacSkillGridActivationCommandIntegrationChecks*.cs`
  cover ownership, paid/free success, reload, exact and concurrent replay,
  top-up retry, envelope tampering, injected rollback, and after-commit
  recovery.

## Verification and remaining work

Final frozen-tree verification passed:

- Release solution build: **0 warnings, 0 errors**;
- complete managed protocol harness: **227 passed, 0 failed**;
- focused command, handler, persistence/outbox, and disposable-PostgreSQL
  activation checks: **passed**;
- strict Win32 Release network-shim build: **passed with `/W4 /WX`**;
- native offline and complete suites: **passed**;
- mandatory B03 PostgreSQL 17 gate: **32 required checks and three migration
  scenarios passed in 376,957 ms**;
- migration proof: **30 migrations applied through
  `20260730_029_holy_stone_material_templates`**;
- cleanup proof: **passed**, with no `godswar_b03_*` or `godswar_b09_*`
  database remaining; and
- three independent implementation, handler, and documentation reviews:
  **no blockers remain**.

The B03 machine-readable artifact is
`artifacts/b03/b09-zodiac-grid-activation-result.json`, exactly 13,978 bytes
with SHA-256
`FA43B43D3D0DD48913CE64FC9C92B1FABD1AC718A826F2A6FF4B77187D45BA98`.

Frozen native artifact hashes:

- `Net.dll`:
  `9FA074F7EED1052DBA3841C6C335F4C81BB1C462FBAB6D6980E960566461FB62`;
  and
- `Godswar.NetShim.Checks.exe`:
  `56AC1FEDC7F07ED051B0FF4518926C5B20D6709DD0DC957281EE0267F8C671FF`.

This is one B09 increment, not completion of B09. Remaining valuable paths
include tokenless and right-click equipment actions, advanced Holy Stone
drilling, repeatable Zodiac grid upgrades, skill selection, and other
inventory, reward, progression, and currency mutations. Repeatable commands
need truthful client or server event identity; they must not copy this
one-shot activation identity.

Rollback is composition-level: remove the PostgreSQL executor from
`GameClientHandler` construction to restore the existing compatibility path.
The durable rows remain valid historical evidence and must not be deleted or
rewritten during rollback.
