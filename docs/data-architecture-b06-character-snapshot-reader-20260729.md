# B06 consistent character snapshot reader

Date: 2026-07-29
Roadmap dependency: B01B, B02, B03, and the pet schema baseline
Next roadmap ticket: B07 - legacy operation identity and command envelope

## Outcome

Character selection and initial world entry now consume one versioned
`ICharacterSnapshotReader` result instead of assembling a character from
independent `IGameStore` calls made at different times.

The snapshot contains the character's identity, appearance, location,
progression, vitals, wallet, equipment projection, zodiac state, calculated
stats, skills, talents, owned pets and their child rows, and active personal
progression boosts. `CharacterSnapshotContract.Validate` applies explicit
field, text, collection, ownership, uniqueness, and cross-projection bounds
before mutable game-session objects are hydrated.

This is primarily an application/read-boundary change, with a matching
single-character-slot guard at the existing character-create mutation
boundary. B06 adds no database migration and does not serialize an ECS world.

## Slot and failure semantics

Contract version 1 uses `CharacterSlotPolicy.SingleCharacterV1`:

| Rows owned by the authenticated account | Result |
| --- | --- |
| 0 | Valid account snapshot with `Character = null` |
| 1 | One complete, validated character snapshot |
| More than 1 | Fail closed with `AmbiguousCharacterSlot` |

The account ID comes from the authenticated server principal, not from a
client-selected character record. A missing account, ownership mismatch,
missing calculated-stat projection, invalid or over-limit data, provider
failure, or unsupported contract version produces a typed
`CharacterSnapshotUnavailableException`. The handler logs only bounded
phase/reason codes and disconnects before sending a partial character
selection response.

Create-role handling now requires a previously validated empty snapshot
before it reaches the store. `JsonGameStore` checks the slot while holding
its shared per-path semaphore. `PostgresGameStore` locks the exact account
row with `SELECT ... FOR UPDATE`, then performs a second `READ COMMITTED`
existence check so a waiter observes the preceding creator's commit. An
occupied slot produces `CharacterSlotOccupiedException`; concurrent creators
therefore produce one committed character and one rejection.

This is an application mutation guard, not a database uniqueness constraint.
Direct SQL or another writer that ignores the account-row lock can still
produce a legacy multi-row account. The snapshot reader deliberately retains
the `AmbiguousCharacterSlot` fail-closed check for that condition. A durable
schema-level cardinality and lifecycle policy remains B11 work.

## PostgreSQL consistency boundary

`PostgresCharacterSnapshotReader` performs one short, read-only
`REPEATABLE READ` transaction:

```text
authenticated account ID
  -> transaction timestamp + bounded snapshot fingerprint + account check
  -> bounded character query (LIMIT 2)
  -> one related-result batch for stats, skills, talents, boosts and pets
  -> contract validation
  -> commit/dispose transaction
  -> hydrate legacy session projections
```

The related batch includes owned pets plus their stat values, character
bonuses, and pet skills. Every query is constrained by the server-derived
account and character IDs. Collection readers enforce the contract limits
before returning.

The provider token is a server-side SHA-256 fingerprint of
`pg_current_snapshot()::text`, not the raw PostgreSQL snapshot string. It is
always 83 characters: `pg-snapshot-sha256:` followed by 64 uppercase
hexadecimal characters. Potentially long in-progress transaction-ID lists
therefore never cross the process boundary or exceed the contract bound.

Hydration occurs only after the transaction closes. Gameplay code therefore
cannot retain an open database reader or transaction, and it cannot observe
core character state from one commit with talent, boost, or pet state from
another commit.

The concurrency proof deliberately pauses a reader after its core row is
loaded, commits coordinated changes to core progression, talent, boost, pet,
and pet-stat rows, then resumes the reader. The first result is entirely old;
the next transaction is entirely new.

## JSON fallback

`JsonGameStore` also implements `ICharacterSnapshotReader`. It acquires the
existing per-path semaphore, deserializes `state.json` once, resolves the
0/1/>1 slot, maps all supported facets, validates the contract, and only then
releases the lock.

The JSON model has no durable pet collections, so its snapshot returns an
explicit empty pet array. This is a documented local-development limitation,
not invented pet persistence. Skills and talents are derived from the
existing seed catalog with saved talent ranks overlaid; personal boosts are
projected from the same loaded file image.

The concurrent JSON check alternates paired position values through another
store instance and verifies that snapshot readers never observe a torn X/Z
pair.

## Login, preview, and entry integration

`Program.cs` selects the PostgreSQL or JSON implementation from the validated
storage profile, wraps it with `MeasuredCharacterSnapshotReader`, and injects
that application contract into `GameClientHandler`.

The initial flow is now:

```text
authenticate server-derived account
  -> register this session as the account owner
  -> finalize the prior boost tail, remove prior world state, disconnect prior session
  -> read and validate one account/character snapshot
  -> hydrate GameCharacter + skills + talents + pets
  -> send AfterLogin and character preview
  -> reuse the same hydrated snapshot for EnterMain and initial bag/pet lists
  -> reuse it for post-enter pet presence, talent ranks and skill list
  -> close the bootstrap boundary after ClientReady + PlayerDetail + UI ready
```

Repeated preview requests and the first entry do not repeat the query.
Character creation and deletion perform their mutation through the existing
store, then refresh the snapshot and fail closed if the observed slot does
not match the mutation result. An occupied create request is rejected before
the store when the loaded slot is already populated, while the store guard
closes the concurrent-create race after an empty read.

Session replacement happens before snapshot I/O. The preceding session is
disconnected before the replacement starts its snapshot read. If the new
read is cancelled or fails, handler cleanup removes the replacement from the
account registry and marks the account offline only when that session is
still the registered owner.

The initial player-detail path does not refresh stats while the consistent
bootstrap is pending. Later live player-detail, map-transition, pet mutation,
and gameplay paths retain their existing current-state reads; B06 does not
turn a login snapshot into a session-long cache.

The snapshot captures and validates personal progression boosts and retains
them in the hydrated bootstrap projection. The post-enter status publication
still asks the live registry/store for the current combined personal, area,
weekend, and other runtime boost state at UI readiness. That deliberately
live combined status query is not represented as snapshot-backed.

## Observability

`MeasuredCharacterSnapshotReader` publishes:

- `godswar_character_snapshot_queries_total`
- `godswar_character_snapshot_query_duration_ms`

The only labels are the bounded `provider` (`postgresql` or `json`) and
`outcome` codes such as `loaded`, `empty`, `ambiguous_slot`,
`provider_unavailable`, or `cancelled`. Account IDs, character IDs, session
IDs, provider tokens, and player-controlled strings are not metric labels.

## B02 boundary reduction

B06 removes nine allowed broad-store reads from the handler bootstrap:

- `GameClientHandler.LoginWorldEntry.cs`: two
  `GetFirstCharacterAsync`, one `GetOwnedPetsAsync`, two
  `GetSkillStatesAsync`, and two `GetTalentStatesAsync` calls;
- `GameClientHandler.PlayerVisibility.cs`: one
  `GetSkillStatesAsync` and one `GetTalentStatesAsync` call.

The corresponding `_store` field-reference allowances fall from 12 to 5 in
`LoginWorldEntry.cs` and from 4 to 2 in `PlayerVisibility.cs`. The remaining
allowances cover mutations or later live-state behavior; they were not
hidden behind the new read contract.

## Repository evidence

| Concern | Repository location |
| --- | --- |
| Application contract, slot policy, limits, failures | `src/Godswar.Server/Application/Characters/ICharacterSnapshotReader.cs`; `CharacterSnapshotFailures.cs`; `CharacterAccountSnapshot.cs`; `CharacterPetSnapshot.cs` |
| Contract validation | `src/Godswar.Server/Application/Characters/CharacterSnapshotContract.cs` |
| PostgreSQL transaction and core read | `src/Godswar.Server/Infrastructure/Characters/PostgresCharacterSnapshotReader.cs`; `PostgresCharacterSnapshotReader.Core.cs` |
| Bounded PostgreSQL snapshot fingerprint | `src/Godswar.Server/Infrastructure/Characters/PostgresCharacterSnapshotToken.cs` |
| Related progression and pet batch | `src/Godswar.Server/Infrastructure/Characters/PostgresCharacterSnapshotReader.Related.cs`; `PostgresCharacterSnapshotReader.Progression.cs`; `PostgresCharacterSnapshotReader.Pets.cs` |
| JSON atomic fallback | `src/Godswar.Server/State/JsonGameStore.CharacterSnapshots.cs` |
| Single-slot mutation guards | `src/Godswar.Server/State/CharacterSlotOccupiedException.cs`; `PostgresGameStore.Characters.Persistence.cs`; `JsonGameStore.cs` |
| Post-transaction hydration | `src/Godswar.Server/Game/CharacterLoadSnapshotHydrator.cs`; `CharacterLoadSnapshotHydrator.Pets.cs` |
| Handler fail-closed integration | `src/Godswar.Server/Game/GameClientHandler.CharacterSnapshot.cs`; `GameClientHandler.LoginWorldEntry.cs`; `GameClientHandler.PlayerVisibility.cs` |
| Composition and provider selection | `src/Godswar.Server/Program.cs` |
| Low-cardinality metrics | `src/Godswar.Server/Application/Characters/MeasuredCharacterSnapshotReader.cs` |
| Contract and hydration checks | `tests/Godswar.Server.ProtocolChecks/CharacterSnapshotContractChecks*.cs` |
| JSON consistency checks | `tests/Godswar.Server.ProtocolChecks/JsonCharacterSnapshotReaderChecks.cs` |
| Handler no-fan-out, fail-closed, and lifecycle checks | `tests/Godswar.Server.ProtocolChecks/CharacterSnapshotHandlerChecks.cs`; `CharacterSnapshotHandlerChecks.Lifecycle.cs` |
| JSON single-slot concurrency check | `tests/Godswar.Server.ProtocolChecks/CharacterSlotMutationChecks.cs` |
| PostgreSQL parity, consistent-read, and single-slot proofs | `tests/Godswar.Server.ProtocolChecks/PostgresCharacterSnapshotReaderIntegrationChecks*.cs`, including `PostgresCharacterSnapshotReaderIntegrationChecks.SingleSlot.cs` |
| Boundary ratchet | `tests/Godswar.Server.ProtocolChecks/DataBoundaryArchitectureBaseline.cs` |

## Verification

```text
Release build                                      PASS (0 warnings, 0 errors)
Versioned contract and hydration                   PASS
Bounded PostgreSQL fingerprint and null-pet guard  PASS
JSON empty/single/ambiguous and atomic reads       PASS
JSON concurrent single-slot mutation guard         PASS
Snapshot metrics and privacy-safe labels           PASS
Handler single-read/no-fan-out bootstrap           PASS
Handler invalid-snapshot fail-closed behavior      PASS
Handler session replacement/cancellation cleanup   PASS
PostgreSQL legacy projection parity                PASS
PostgreSQL all-old/all-new concurrency proof       PASS
PostgreSQL concurrent single-slot mutation guard   PASS
Full protocol suite                                PASS (191/191)
B03 mandatory PostgreSQL gate                      PASS (14/14 checks)
B03 migration scenarios                            PASS (3/3 scenarios)
B03 disposable-resource cleanup                    PASS (0 residual databases)
```

The B03 script now includes
`PostgreSQL consistent character snapshot reader` in its mandatory repository
smoke set. Each mutable repository check runs against its own clone of the
fully migrated PostgreSQL 17 baseline, so content publication and transaction
checks cannot pass or fail because of test order. The confirmed run completed
all checks in 158,431 ms and left no `godswar_b03_%` databases behind.
The ignored receipt is
`artifacts/b03/postgres-ci-result-b06-final.json`; its SHA-256 is
`FA604B2D997F4D027BBCEF0A7266DED62B96DC1A50E8E79A9BC6C2BC46F51E88`.
It records `c10cd6e` as the committed B05C base beneath the validated B06
working tree.

## Rollback and limitations

B06 has no schema rollback because it adds no migration. It does change
runtime behavior by rejecting occupied character creation. The previous
`IGameStore` methods remain available for other mutations and later
live-state paths. An application rollback can restore the legacy bootstrap
fan-out, but must also restore the exact B02 allowances rather than weakening
the architecture ratchet. Reverting the create guard would reopen the
duplicate-slot race and must be an explicit rollback decision.

Known limits:

- `SingleCharacterV1` intentionally does not support multiple character
  slots; that requires a versioned policy and client-flow decision.
- Slot cardinality is not yet protected by a PostgreSQL uniqueness
  constraint, so out-of-band writers must follow the same account-row lock
  until B11 establishes the durable lifecycle rule.
- JSON cannot represent owned pets and is not the production pet authority.
- `PostgresGameStore` and `PostgresCharacterSnapshotReader` currently own
  separate Npgsql data sources and connection pools. Their combined
  connection budget must be measured and consolidated or explicitly sized
  before production capacity claims.
- The snapshot reader is not a write repository, session owner fence, cache,
  or cross-reconnect idempotency mechanism.
- B06 prevents mixed reads during bootstrap; it does not make valuable
  mutations duplicate-safe.

## Next dependency

B07 should establish a server-derived command envelope and the strongest
legacy-compatible operation identity available for one valuable command. It
must define duplicate, reconnect, request-hash conflict, and weaker legacy
retry behavior explicitly. That identity boundary is required before B08 can
add PostgreSQL inbox/outbox processing safely.
