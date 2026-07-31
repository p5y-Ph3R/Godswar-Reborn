# B20B account, authentication, and session persistence cutover

Status: completed and locally verified on 2026-07-31; no production database,
live container, player data, or schema migration was changed

## Outcome

B20B removes the broad `IGameStore` dependency from account authentication,
raw-login compatibility, game-session account lookup/presence, and semantic
gateway authentication/routing. PostgreSQL remains authoritative. JSON remains
an explicit local-development compatibility provider until B20E.

The slice also makes the remaining legacy persistence inventory observable.
Every reviewed legacy invocation records one finite operation before storage is
attempted, including failed and cancelled attempts. This is instrumentation for
a later zero-use decision; it is not itself proof of a production observation
window.

## Focused contracts and ownership

`Application/Accounts` now owns four narrow contracts:

- `IAccountCredentialStore` owns exact username lookup, conflict-safe account
  creation, and password-verifier compare-and-swap;
- `IAccountDirectory` returns credential-free `AccountIdentity` values;
- `IAccountPresenceWriter` maintains the legacy `accounts.login_status` and
  online-time compatibility projection; and
- `ILegacyAccountLoginStore` isolates original raw-protocol login/create from
  secure versioned-verifier authentication.

`login_status` is not authentication, connection ownership, distributed
presence, or a player-routing lease. B15/B17 ownership and coordination remain
the authoritative session controls.

The PostgreSQL implementation is
`Infrastructure/Accounts/PostgresAccountStore.cs`. It uses an injected pooled
`NpgsqlDataSource`, parameterized SQL, exact-case username semantics, a
conflict-safe insert, and an exact expected-verifier update predicate. Raw
compatibility login cannot replace an existing versioned verifier.

`Application/Accounts/AccountUsername.cs` validates the normalized legacy
username before either PostgreSQL or JSON can mutate durable state. Blank raw
usernames retain the historical `player` fallback; malformed, overlong, or
non-printable names fail without creating an account or temporary JSON write.

`AccountAuthenticationService`, `LoginClientHandler`, `GameClientHandler`, and
`GameClientHandlerFactory` now depend on the focused contracts. The broad
PostgreSQL store temporarily delegates its retained compatibility methods to
the same focused adapter; those public helpers are no longer part of
`IGameStore`.

## Semantic gateway boundary

The semantic gateway no longer opens `PostgresGameStore` or exposes
`IGameStore` through its data session.

- `PostgresSemanticGatewayDataSession` owns a gateway-scoped pooled data source,
  focused account authentication, and a focused character-route reader.
- `PostgresSemanticGatewayCharacterRouteReader` reads at most two active rows
  and fails closed if durable state contains an ambiguous active route.
- `JsonSemanticGatewayDataSession` retains only the explicit local-development
  compatibility path and uses the same focused application interfaces.
- `PostgresSchemaMigrationRunner.InitializeGodswarSchemaAsync` provides schema
  initialization without coupling gateway composition to the broad store or
  to gameplay/content seeding.

TCP/UDP session binding, player ownership fencing, and authenticated backhaul
routing are unchanged by this slice.

## Legacy-use telemetry

`State/LegacyPersistenceMetrics.cs` defines a closed enum of 35 operations and
publishes:

- `godswar_legacy_persistence_invocations_total{operation=...}`; and
- `godswar_legacy_persistence_observer_ready`.

Labels contain only server-defined operation names. They contain no account,
character, session, endpoint, provider, or attacker-controlled values. The
observer-ready gauge is initialized even when no legacy operation occurs, so a
missing observer can be distinguished from a genuine zero.

The architecture check derives its expected metric coverage directly from the
B20 and data-boundary baselines. It masks comments and literals and requires
each record to precede its matching persistence call. A missing, extra, moved,
late, unknown, or obsolete record fails the managed gate. Current coverage is
exactly 44 records for 44 remaining invocations.

A future zero-use claim must require all of the following over the approved
window: target process `up`, observer-ready equal to one, no increase in the
legacy invocation counter, and the static B20 ratchet at zero. Counter silence
alone is insufficient.

## Ratchet reduction

| Measure | B20A | B20B |
| --- | ---: | ---: |
| Broad `IGameStore` calls | 59 | 43 |
| Concrete JSON checkpoint calls | 1 | 1 |
| Total tracked legacy invocations | 60 | 44 |
| Read calls | 15 | 8 |
| Mutation or mixed calls | 43 | 35 |
| Bootstrap calls | 2 | 1 |
| Broad `IGameStore` methods | 44 | 36 |
| Broad caller files | 24 | 20 |
| Invoked broad members | 42 | 34 |
| External `IGameStore` type references | 11 | 7 |
| `_store` identifier references | 82 | 64 |
| Tracked `store` parameter references | 21 | 12 |
| Legacy-State Npgsql references | 330 | 329 |

The 16 removed broad invocations comprise 14 account/authentication/session
operations and two semantic-gateway seed/route operations. The gateway now
uses focused schema initialization and route projection instead.

## Verification

- Release solution build: passed, 0 warnings and 0 errors.
- Data-boundary ratchet: 43/43 calls, 36/36 methods, no new debt, stale debt,
  or rule violations.
- B20 retirement ratchet: 44/44 total tracked calls, no new debt, stale debt,
  or capture-authority violations.
- Legacy telemetry architecture: 44 required, 44 instrumented, 35 finite
  operations, with no missing, orphaned, or misordered records.
- Legacy metric behavior/cardinality check: passed.
- Full managed protocol suite: 299 passed, 0 failed.
- Focused JSON authentication checks: passed in the managed protocol suite.
- Focused JSON semantic-gateway session lifecycle: passed.
- Focused PostgreSQL account adapter, concurrent registration/CAS, invalid
  pre-write rejection, and semantic-gateway session lifecycle: registered as
  one mandatory disposable PostgreSQL smoke check.
- Mandatory disposable PostgreSQL 17 gate: 44 required checks and 5 migration
  scenarios passed; migration head
  `20260731_035_tempest_realm_authority`; cleanup passed.

## Limits and next slice

B20B does not remove `IGameStore`, JSON authority, compatibility mutation
fallbacks, legacy bootstrap SQL, `character_item_loadout`, compiled seed
consumers, or capture-backed content tables. It does not claim a production
zero-use window.

The next recommended slice is B20C: move the remaining live PostgreSQL reads
and writes for character stats/skills, pets, boost state, world-boss state, and
Zodiac level behind feature-specific application contracts while shrinking
the same ratchets and metric coverage in lockstep.
