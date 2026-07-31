# B20C focused gameplay-state persistence cutover

Status: implemented and locally verified on 2026-07-31; no live database,
container, player data, or schema migration was changed

## Outcome

B20C removes ten live calls across seven operations from `IGameStore`:

- calculated character stats and learned-skill authorization;
- owned-pet projections;
- active experience-boost resolution;
- world-boss area-control activation and respawn reads; and
- ownership-fenced Zodiac-level upgrades.

The five raw/JSON compatibility mutation fallbacks for pet hatch, pet
presence, pet level, online-only boost duration, and Zodiac online accrual
remain visible and instrumented. They belong to B20D and were deliberately
not hidden by this slice.

## Focused boundaries

Application contracts now own the storage-neutral shapes:

- `ICharacterRuntimeProjectionReader` reads one calculated-stat projection
  and answers scalar learned-skill authorization;
- `IOwnedPetSnapshotReader` returns the bounded immutable pet projection;
- `IExperienceBoostStateReader` returns one validated boost snapshot;
- `IWorldBossAreaControlStore` and `IWorldBossRespawnReader` own durable
  world-boss control and respawn state; and
- `IZodiacLevelStore` owns the fenced Zodiac-level mutation.

`ServerGameplayPersistenceComposition` supplies those exact dependencies to
`GameSessionRegistry` and `GameClientHandler`. PostgreSQL composition fails
closed when a required focused provider is absent. JSON implements the same
contracts only as the existing local-development compatibility provider.
Temporary mappings in `FocusedGameplayProjectionCompatibility` isolate the
legacy mutable State models from Application contracts.

The JSON development provider applies the same active-character checks,
global world-boss token rule, and a process-local ownership registry for
Zodiac mutations. That registry is deliberately not a distributed fence;
PostgreSQL remains required for durable production ownership.

## PostgreSQL behavior

`PostgresCharacterSnapshotReader` also implements the runtime character and
pet readers. Stats and scalar skill checks use targeted active-character
queries, including Warrior skill ID zero. Pet reads retain a read-only
`RepeatableRead` transaction, close their multi-result reader before commit,
and use the same bounded relational child-row parser as login snapshots.

`PostgresExperienceBoostStateReader` reads personal, faction-area, and VIP
state through one read-only `RepeatableRead` transaction. It requires active
character ownership, bounds rows and strings, canonicalizes PostgreSQL
timestamps, and validates the immutable result before returning it.

`PostgresWorldBossAreaControlStore` locks the configured map policy before
changing control. It distinguishes committed, duplicate, stale,
not-configured, and invalid requests; a delayed death event cannot replace a
newer control row. Death tokens remain globally unique, and active respawn
reads validate their bounded projection. Concurrent same-map ordering and
cross-map death-token contention are covered with independent data sources.

`PostgresZodiacLevelStore` preserves the previous ownership lock, character
row lock, policy application, rollback behavior, commit, and post-transaction
ownership revalidation. The registry still serializes upgrades with online
interval settlement through the existing per-session gate.

The broad PostgreSQL store retains concrete delegating methods only for
temporary compatibility tests and callers. Those methods are no longer part
of `IGameStore`.

## Ratchet reduction

| Measure | B20B | B20C |
| --- | ---: | ---: |
| Broad `IGameStore` calls | 43 | 33 |
| Concrete JSON checkpoint calls | 1 | 1 |
| Total tracked legacy invocations | 44 | 34 |
| Read calls | 8 | 1 |
| Mutation or mixed calls | 35 | 32 |
| Bootstrap calls | 1 | 1 |
| Broad `IGameStore` methods | 36 | 29 |
| Broad caller files | 20 | 18 |
| Invoked broad members | 34 | 27 |
| External `IGameStore` type references | 7 | 7 |
| `_store` identifier references | 64 | 52 |
| Tracked `store` parameter references | 12 | 12 |
| Legacy-State Npgsql references | 329 | 323 |

Legacy metric coverage shrinks with the source inventory: 34 remaining
invocations map to 28 finite operation names. Removed enum values were not
renumbered, preserving the numeric identity of remaining operations.

## Verification

- Release solution build: passed with zero warnings and zero errors.
- Data-boundary ratchet: 33/33 broad calls and 29/29 methods, with no new or
  stale debt and no layering violation.
- B20 retirement ratchet: 34/34 tracked calls, one read, 32 mutation/mixed,
  one bootstrap, and no new or stale debt.
- Legacy telemetry: 34/34 calls instrumented across 28 finite operations.
- Managed protocol suite: 300 passed and zero failed.
- Mandatory disposable PostgreSQL 17 gate: 45 required checks and five
  migration scenarios passed, including focused projection parity,
  ownership rejection, concurrent world-boss ordering, and cross-map token
  contention.
- Disposable gate duration: 460,236 ms. Its loopback-only container, roles,
  and databases were removed; the live game and PostgreSQL containers were
  not changed.

## Limits and next slice

This slice does not make world-boss control atomic with monster reward
settlement; a durable combined command or outbox follow-up remains a future
correctness improvement. It does not remove the five compatibility mutation
fallbacks, JSON authority, `LegacyCharacterCheckpointStore`, legacy bootstrap
SQL, loadout projection, or compiled/capture content consumers. It does not
claim a production zero-use observation window.

B20D should next remove the fail-closed compatibility mutation branches,
convert their tests to focused fakes, and retire the legacy checkpoint
adapter without weakening retry, ownership, or online-duration semantics.
