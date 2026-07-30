# B09 durable Zodiac skill-grid upgrade increment

Date: 2026-07-30

Source base: `653a678e08420340152cd95b8da13b6228604b88`

Status: implemented and verified; B09 remains open

## Outcome

Repeatable Zodiac skill-grid upgrades now have a truthful client operation ID
and an atomic PostgreSQL command boundary:

```text
stock SID 101 request
  -> native family-20 UUID marker
  -> authenticated TLS legacy transport
  -> bounded command envelope
  -> live-session Zodiac serialization gate
  -> locked PostgreSQL character transaction
  -> inbox-first exact replay or server-derived outcome
  -> grid and progression-resource mutation
  -> permanent audit and inbox receipt
  -> latest-wins per-grid outbox event on success
  -> commit
  -> authoritative live projection and stock-client refresh
  -> authenticated terminal operation result
```

The client chooses only a zero-based grid index. It cannot choose the account,
character, current level, Zodiac-level gate, upgrade costs, balances, selected
skill, or result.

This increment adds no database migration. It reuses the B08 permanent
`command_audit`, `command_inbox`, and `outbox_events` foundation and the
existing `character_base` and `character_zodiac_skill_grids` owners.
PostgreSQL remains the sole durable owner.

## Wire evidence and compatibility boundary

The accepted 24-byte opcode-10297 request shape is:

| Offset | Type | Rule |
| ---: | --- | --- |
| 0 | little-endian `u16` | packet length `24` |
| 2 | little-endian `u16` | opcode `10297` |
| 4 | little-endian `u32` | ignored client player value |
| 8 | little-endian `u16` | native module `255`, or supported module `0` |
| 10 | little-endian `u16` | SID `101` |
| 12 | little-endian `i32` | grid index `0..15` |
| 16 | little-endian `i32` | exact placeholder `-1` |
| 20 | little-endian `i32` | exact trailing value `0` |

The existing managed golden for grid 1 is:

```text
1800392800000000FF00650001000000FFFFFFFF00000000
```

There is no stored retail client-to-server SID-101 capture in this repository.
The exact request is supported by the existing managed golden, shipped client
behavior, and native reverse-derived evidence. This document does not
misrepresent it as captured traffic. The module-0 form is retained as a
managed compatibility shape.

The native classifier marks only an exact SID-101 mutation. SID 100
activation and SID 102 skill selection remain unrelated. Invalid module,
grid, placeholder, tail, or exact-length combinations fail closed and do not
allocate pending-operation state.

Raw legacy TCP has no operation UUID and therefore retains the serialized
compatibility store path with explicit `UnsupportedLegacyRetry` telemetry. It
does not claim disconnect-after-commit replay safety. This compatibility path
is raw-only. Secure SID-101 traffic without a UUID fails closed before store
access and receives no fabricated response. A secure packet carrying a UUID
also never falls back when the durable provider is unavailable.

## Repeatable operation identity

Family `20` is `ZodiacSkillGridUpgrade` with
`ClientOperationId` identity strength. The native pending registry binds an
operation UUID to:

- the authenticated principal;
- the selected character;
- family 20; and
- the requested grid index.

The same unresolved click keeps its UUID across a same-runtime reconnect.
After any authenticated terminal result settles it, a later click receives a
fresh UUID. Pending and resolved entries remain bounded and expire under the
existing ten-minute registry policy.

The application operation scope contains the UUID. Its canonical request is
only version 1 plus the normalized grid byte. Account and character are
server-authenticated envelope subjects, while module aliases, client player
value, placeholders, connection ID, and current state are excluded.

Reusing one UUID for another grid keeps the same operation scope but changes
the request hash. The command inbox uses a character-scoped key so this reuse
finds the original row and returns a request-hash conflict rather than
creating a second operation under another grid key.

## Server-owned policy

`ZodiacSkillGridUpgrade.Apply` remains the authoritative policy. It derives:

- active-grid requirement;
- maximum grid level 50;
- required Zodiac level from the shipped `Starlv` table;
- Zodiac energy cost from `UpdateE`;
- Talent Point cost from `UpdateS`;
- exact integer energy spending while preserving the centi-energy remainder;
- exactly one grid-level increment; and
- preservation of the selected skill.

The shipped UI contains an inconsistent `/40` label while its policy data and
server domain use maximum level 50. This increment retains the already-tested
level-50 policy; it does not silently reinterpret content data.

## Atomic PostgreSQL transaction

`PostgresZodiacSkillGridUpgradeCommandExecutor` performs one transaction:

1. Validate family, authenticated provenance, UUID scope, and request hash.
2. Lock the owning `character_base` row by account and character.
3. Read the character-scoped family-20 inbox before mutable grid state.
4. On exact replay, validate the permanent receipt and load the current
   authoritative character/grid projection.
5. Otherwise read the chosen grid and derive the result with server policy.
6. Insert a permanent audit row containing the exact level, gate, cost,
   Zodiac energy/remainder, Talent Point, and selected-skill evidence.
7. Insert the canonical permanent inbox receipt for both success and
   deterministic business rejection.
8. On success, compare-and-set Zodiac energy, its remainder, and Talent
   Points; compare-and-set the grid from its exact previous level to the next;
   and insert one outbox event.
9. Commit before any stock-client or secure terminal response.

Every mutation requires exactly one affected row. A fault before commit rolls
back state, audit, inbox, and outbox together. A fault observed after commit
is recovered by retrying the same UUID and reading the stored result.

Deterministic state outcomes are permanent receipts:

- inactive grid;
- maximum level reached;
- Zodiac level too low;
- insufficient Zodiac energy; and
- insufficient Talent Points.

This is correct for a repeatable UUID. A lost rejection replays the original
terminal outcome, while a new click after state changes receives a new UUID
and is evaluated afresh. These rejections do not mutate resources or grids
and do not publish an outbox event.

Wrong ownership and invalid envelopes are proven pre-mutation failures and do
not fabricate durable character state. Cancellation, database failure, or an
uncertain provider outcome emits no terminal result, leaving the native UUID
pending for safe retry.

## Resource evidence boundary

Zodiac energy and Talent Points are progression resources, not Silver or
Gold. This slice does not mislabel them in
`character_currency_ledger`, whose database contract permits only those two
currencies.

The permanent family-20 receipt, audit detail, and successful outbox payload
record their exact before, cost, and after values, including the fractional
Zodiac energy remainder. A generalized progression-resource ledger remains a
later B12 design decision. Until other energy and Talent Point writers move
behind that future boundary, adding a SID-101-only table would imply false
global completeness.

## Replay, live serialization, and response ordering

Online energy accrual, Zodiac level changes, grid activation, compatibility
grid upgrades, and durable grid upgrades share the existing per-session
Zodiac gate. The durable executor runs inside that gate, and the returned
projection is applied to both live character mirrors before release. This
prevents a completed online-energy update from being replaced by stale
post-command memory.

The stock client increments its displayed grid unconditionally when it
receives SID 101. Consequently:

```text
new committed success:
  SID 101 -> Player Status -> 328-byte Zodiac full sync -> Applied

exact committed replay:
  Player Status -> 328-byte Zodiac full sync -> Replayed

stored business rejection:
  Player Status -> 328-byte Zodiac full sync -> Rejected

request hash conflict:
  Conflict
```

SID 101 is never sent for replay or rejection. The authenticated terminal
result is written last through the TLS write gate, after the stock-client
projection, so settling the native UUID cannot race ahead of usable client
state. A request-hash conflict, invalid envelope, or wrong-owner result has no
authoritative durable projection and therefore emits no fabricated Player
Status or Zodiac full sync before its terminal result.

The secure result wire field at offset 8 is now documented as the
**authoritative revision**. Existing inventory families continue to place
their inventory revision there. A successful family-20 result places its
nonzero resulting grid level there. This is a source/documentation
generalization only; the version-1 frame bytes and compatibility alias remain
unchanged.

Stable family-20 result codes are:

| Code | Meaning |
| ---: | --- |
| `0` | envelope, identity, or request conflict |
| `1` | upgrade succeeded |
| `2` | invalid grid |
| `3` | grid inactive |
| `4` | grid already at maximum level |
| `5` | Zodiac-level gate not met |
| `6` | insufficient Zodiac energy |
| `7` | insufficient Talent Points |
| `8` | wrong owner |

## Outbox contract

Only successful upgrades publish:

| Field | Value |
| --- | --- |
| consumer | `zodiac_grid_upgrade_v1` |
| aggregate type | `zodiac_grid_upgrade` |
| aggregate key | `character:{characterId}:zodiac-grid:{gridIndex}` |
| aggregate revision | resulting grid level |
| event type | `zodiac.skill_grid_upgraded` |
| contract version | `1` |
| ordering | latest wins / versioned state |

The resulting level is a truthful monotonic per-grid revision. Versioned-state
ordering permits a pre-existing legacy grid above level 1 to publish its first
durable event without waiting for historical events that never existed. The
consumer validates the successful receipt, event ID, key, contract version,
and revision; it does not become another source of truth.

## Repository evidence

- `client/network-shim/src/SecureZodiacSkillGridUpgradeIdentity.*` owns the
  exact native classifier.
- `client/network-shim/src/SecurePendingOperationRegistry.Zodiac.cpp` owns
  bounded repeatable UUID retention.
- `src/Godswar.Server/Application/Zodiac/ZodiacSkillGridUpgrade*` defines the
  intent, envelope, receipt, result, and executor contracts.
- `src/Godswar.Server/Infrastructure/Zodiac/PostgresZodiacSkillGridUpgrade*`
  owns transaction, evidence, replay, fault probe, codec, and outbox consumer.
- `src/Godswar.Server/Game/GameSessionRegistry.DurableZodiacSkillGridUpgrade.cs`
  owns live-session serialization and projection.
- `src/Godswar.Server/Game/GameClientHandler*Zodiac*` owns routing, stock
  response ordering, and terminal result emission.
- Native and managed protocol checks cover parser bounds, identity,
  reconnect, capacity, expiry, settlement, contract hashes, result evidence,
  projection ordering, raw-only compatibility, secure tokenless fail-closed
  behavior, provider uncertainty,
  PostgreSQL replay, conflicts, concurrency, and transaction faults.

## Verification and remaining work

Final frozen-tree verification passed:

- Release solution build: **0 warnings, 0 errors**;
- complete managed protocol harness: **231 passed, 0 failed**;
- strict Win32 Release network-shim build: **passed with `/W4 /WX`**;
- native offline and complete suites: **passed**;
- native `Net.dll` SHA-256:
  `0775CA2D673E95C288427361206F3493AF9B86D4869F38889F66E455573FA38A`;
- native check-runner SHA-256:
  `72E347D4C6D783512C4C7DE439440888417313FF0CB0FBEF9F74B38A2F8E636C`;
- mandatory PostgreSQL 17 B03 gate: **33 required checks and 3 migration
  scenarios passed in 391,431 ms**;
- migration proof: **30 migrations**, head
  `20260730_029_holy_stone_material_templates`;
- gate artifact:
  `artifacts/b03/b09-zodiac-grid-upgrade-result.json`, **14,344 bytes**,
  SHA-256
  `806AAC97B3E185AFB724377E937D33EBB3E4B1C17415CC87F1A9C458319F98C9`;
  and
- database cleanup proof: **passed**, with zero `godswar_b03_*` databases
  remaining.

This is one B09 increment, not completion of B09. Tokenless and right-click
equipment actions, advanced Holy Stone drilling, Zodiac skill selection, and
remaining inventory, reward, progression, and currency mutations still need
truthful retry identity and durable transaction boundaries.

Rollback is composition-level: remove the family-20 executor from
`GameClientHandler` construction to retain the raw compatibility path. Secure
SID-101 mutations then remain fail closed until the executor is restored.
Permanent audit/inbox/outbox rows remain valid historical evidence and must
not be deleted or rewritten during rollback.
