# ADR 0001: Data ownership and dependency boundaries

- Status: Accepted
- Date: 2026-07-29
- Decision owner: Godswar server maintainers
- Roadmap ticket: B02

## Context

At B02 acceptance, the server was one .NET 10 process and one project.
`Program.cs` manually
constructs either `PostgresGameStore` or `JsonGameStore`, then passes the broad
`IGameStore` into login, game, authentication, and session-registry code.
`IGameStore` currently exposes 48 unrelated operations. Thirty-two production
files make 81 direct `_store` calls to 44 distinct members.

`State` is not a clean architectural layer. It currently combines mutable
game models, rules, PostgreSQL and JSON implementations, migrations, content,
and persistence DTOs. Nineteen `State` files also import the `Game` namespace.
Renaming
that directory or splitting every contract in one change would create
substantial risk without improving transaction semantics.

The custom ECS kernel and World code do not directly reference `IGameStore`,
concrete stores, or Npgsql. The primary data coupling is in packet handlers,
authentication, and runtime orchestration.

## Decision

The target dependency direction is:

```text
untrusted transport / protocol adapters
                 |
                 v
       application commands and queries
                 |
                 v
       domain rules and ECS boundaries

infrastructure adapters
        |
        +---- implement application persistence contracts
        `---- depend on application/domain types
```

`Program.cs` remains the composition root. No dependency-injection framework
is introduced by this decision.

New feature boundaries use intent-specific contracts such as
`IWorldContentReader`, `ICharacterSnapshotReader`,
`IInventoryTransactions`, or `IPlayerCheckpointWriter`. Contracts expose
consistency and transaction outcomes. They do not expose Npgsql types, packet
DTOs, sockets, or a generic CRUD repository.

The `Application`, `Domain`, and `Infrastructure` directories were established
as a source layout by B02. At that point, B05 (`IWorldContentReader`) was the
first planned runtime extraction; the B05 amendment below records its later
implementation. B02 added no empty marker, service bag, universal repository,
runtime adapter, or behavior change.

## Authoritative ownership

| Data class | Authoritative owner | Other copies |
| --- | --- | --- |
| Durable player identity, ownership, inventory, equipment, currency, progression, pets, and entitlements | PostgreSQL | ECS/session copies are runtime projections |
| Online combat, movement, AI, AOI, transient status, and transport replay state | Single owning ECS/session runtime | Checkpoints are bounded durable records, not competing authorities |
| Login tickets, reconnect windows, admission and UDP binding | In-process secure session services initially | Disposable; a future Redis copy requires a separate approved ADR |
| World/content definitions | One versioned content revision per family | Runtime projections are immutable for that revision |
| JSON store | Explicit local-development compatibility only | It is not equivalent production authority |
| Audit/economy history | PostgreSQL | Derived operational projections may be rebuilt |

Each future field must have exactly one authoritative owner. PostgreSQL and a
cache/document store must not be independently dual-written.

## Enforced rules

`DataBoundaryArchitectureChecks` is a source-level ratchet suitable for the
current single-project layout:

1. The reviewed legacy `_store` calls are recorded by path, member, and count.
2. All reviewed `_store` identifier occurrences are recorded, so aliasing or
   null-conditional syntax cannot silently bypass the call baseline.
3. The 19 bare `store` parameter/composition-root occurrences are recorded,
   so constructor-parameter calls cannot bypass the field baseline.
4. The six production files that mention `IGameStore` outside `State` are
   recorded with exact counts.
5. The existing `IGameStore` methods and their canonical signature
   fingerprint are frozen.
6. All current syntactic Npgsql references across legacy files are
   frozen. Comments mentioning Npgsql do not count. New provider code belongs
   under `Infrastructure`.
7. Existing `State -> Game` using directives are frozen.
8. Redis and MongoDB drivers are permitted only in future `Infrastructure`.
9. Concrete legacy stores are confined to `State` and the `Program.cs`
   composition root.
10. New `Application`, `Domain`, and `Infrastructure` source files must follow
   bidirectional namespace/directory rules. Game and Security can depend on
   Application contracts, but not concrete Infrastructure.

Adding or increasing debt fails the gate. Removing debt also requires deleting
the corresponding stale allowance in the same change, so the baseline can
only shrink deliberately. Any exceptional increase requires explicit owner
review and a superseding ADR; changing only the allowance is not an
architectural justification.

The check emits bounded metrics:

```text
calls baseline/current
caller files and members
_store identifier baseline/current
store-parameter baseline/current
IGameStore reference baseline/current
legacy Npgsql baseline/current
State-to-Game using baseline/current
new, stale, and rule-violation counts
```

It runs in the protocol-check executable and explicitly in the existing
pull-request workflow.

## Transitional exceptions

- The exact Npgsql-bearing `State/**` files may continue as frozen baseline
  debt while feature slices move to `Infrastructure`.
- `Operations/ControlledHostValidationCommand.cs` may parse an Npgsql
  connection string for the controlled-host validation command.
- `Program.cs` may construct the selected concrete store.
- Existing `State -> Game` using directives are baseline debt, not permission
  for new imports. This lexical metric does not claim to count every referenced
  Game type beneath an existing import.
- Existing World-to-State model dependencies are not classified as direct
  database coupling. They will be resolved with focused domain extraction,
  not a misleading blanket rename.

## B05 ratchet amendment

B05 performed the first deliberate shrink. World content now crosses
`IWorldContentReader`; `GameClientHandler` no longer loads NPCs, monsters, or
enter-bootstrap templates through `IGameStore`. Pure NPC and monster spawn
models moved to `Domain/World/Content`, and Application-to-State references
are now forbidden.

The reviewed baseline after B05 is 78 broad-store calls, 103 `_store`
occurrences, 44 `IGameStore` methods, 323 legacy Npgsql references, and 18
`State -> Game` imports. New PostgreSQL content-loading code is owned by
`Infrastructure/WorldContent`, so the legacy provider baseline did not grow.

## Consequences

New direct persistence coupling becomes visible and fails CI. Each later
feature extraction has a measurable completion signal: remove direct calls
and shrink the baseline. The design stays a modular monolith and preserves
the current protocol and persistence behavior.

This does not make the broad store safe, add command idempotency, create a
transactional outbox, or resolve online ownership. Those remain later roadmap
work.

## Verification and rollback

The analyzer has synthetic checks for a new consumer, aliased and bare
parameter calls in an allowlisted file, stale allowances, provider leakage,
provider words in comments, Game-to-Infrastructure leakage, reversed
application dependencies, misplaced namespaces, and concrete-store leakage.
The source ratchet is intentionally conservative; the signature fingerprint
supplies a compiled reflection check for the broad interface itself.

Rollback means restoring the last reviewed baseline and check together. Do
not remove the gate merely because legacy debt remains.
