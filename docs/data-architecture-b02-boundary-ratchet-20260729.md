# B02 data-boundary architecture ratchet

Status: complete

- Implementation commit:
  `22c3d8e14399b3578011b14ac8627873fa40825e`
- Implementation tree:
  `401a8af94be7aff25c2404eff2bba187e011c4a1`
- Base documentation commit: `df704e0`
- Date: 2026-07-29
- Runtime behavior change: none
- Database/schema change: none

## Outcome

B02 establishes an enforceable data-boundary ratchet without prematurely
splitting the 48-member `IGameStore` or changing server composition.

The target dependency direction is recorded in
`docs/adr/0001-data-ownership-and-dependency-boundaries.md`:

```text
Networking/Game -> Application contracts -> Domain/ECS
Infrastructure --------------------------> Application + Domain
```

`Program.cs` remains the manual composition root. PostgreSQL remains the
authoritative owner of durable player value, while the owning online ECS
runtime remains authoritative for transient simulation state. JSON remains a
transitional local-development compatibility provider.

No empty marker interface, generic repository, service bag, DI framework,
Redis client, MongoDB client, or runtime adapter was introduced. The first
real feature contract remains the B05 `IWorldContentReader` slice.

## Reviewed legacy baseline

The gate reports the following exact baseline:

| Debt or boundary | Baseline | Current | New | Stale |
| --- | ---: | ---: | ---: | ---: |
| Direct broad-store calls | 81 | 81 | 0 | 0 |
| Direct caller files | 32 | 32 | 0 | 0 |
| Invoked broad-store members | 44 | 44 | 0 | 0 |
| `_store` identifier occurrences | 106 | 106 | 0 | 0 |
| Bare `store` parameter/composition occurrences | 19 | 19 | 0 | 0 |
| `IGameStore` type references outside `State` | 10 | 10 | 0 | 0 |
| Existing `IGameStore` methods | 48 | 48 | 0 | 0 |
| Syntactic Npgsql references outside future Infrastructure | 331 | 331 | 0 | 0 |
| `State -> Game` using directives | 19 | 19 | 0 | 0 |
| Layer/provider rule violations | 0 | 0 | 0 | 0 |

The broad-store signature fingerprint is:

```text
6D096406E4B6D8845AD0B9815ECDF6D7ABB3B9523A326B671CB76F9A8E518CF8
```

Each store-call allowance records relative source path, invoked member, and
occurrence count. Existing store field, constructor parameter, store type,
Npgsql, and reverse-using debt is also recorded by path and exact count.

An increase or new location fails. A removal without deleting the matching
allowance also fails, forcing every completed extraction to shrink the
reviewed baseline in the same change. Increasing an allowance requires an
explicit architectural review; editing the baseline alone is not a
justification.

## Enforced dependency rules

- New Npgsql code is allowed only under `Infrastructure`; the 26 existing
  legacy Npgsql-bearing files are exact baseline exceptions.
- Redis and MongoDB drivers are allowed only under `Infrastructure`.
- `PostgresGameStore` and `JsonGameStore` remain confined to `State` and the
  `Program.cs` composition root.
- New `IGameStore` consumers and new methods/signature changes fail.
- Game and Security may depend on Application contracts, but not concrete
  Infrastructure.
- Application cannot depend on Infrastructure, Game, transport, protocol,
  packets, sockets, or provider clients.
- Domain cannot depend on Application, Infrastructure, Game, transport,
  protocol, packets, sockets, providers, or the legacy mixed `State`
  namespace.
- Infrastructure cannot depend on handlers, networking, packets, protocol,
  World/ECS, or sockets.
- ECS/World and transport/protocol cannot depend on Infrastructure.
- Application, Domain, and Infrastructure namespaces must live in matching
  source directories, and files in those directories must declare matching
  namespaces.

The source layout skeleton is documented in:

- `src/Godswar.Server/Application/README.md`
- `src/Godswar.Server/Domain/README.md`
- `src/Godswar.Server/Infrastructure/README.md`

## Adversarial verification

The permanent architecture check proves its analyzer rejects:

- an increased call inside an already allowlisted partial file;
- aliasing `_store` to another local variable;
- a direct call on an existing bare `store` constructor parameter;
- a new `IGameStore` consumer;
- a removed call with a stale allowance;
- Npgsql in Networking;
- Game/Security directly referencing Infrastructure;
- Application directly referencing Infrastructure;
- an Application namespace outside the Application directory; and
- a concrete PostgreSQL store outside State/Program.

It also verifies that the word `Npgsql` in a comment is not treated as a
provider dependency. The Npgsql baseline counts using directives and concrete
Npgsql type identifiers rather than prose.

## Files

- `tests/Godswar.Server.ProtocolChecks/DataBoundaryArchitectureBaseline.cs`
  contains the reviewable exact baseline.
- `tests/Godswar.Server.ProtocolChecks/DataBoundaryArchitectureAnalyzer.cs`
  contains the bounded source rules.
- `tests/Godswar.Server.ProtocolChecks/DataBoundaryArchitectureChecks.cs`
  contains repository loading, compiled interface fingerprinting, synthetic
  adversarial checks, metrics, and bounded failure reporting.
- `tests/Godswar.Server.ProtocolChecks/Program.cs` registers the check.
- `.github/workflows/phase5a-network-gate.yml` runs it explicitly on pushes
  and pull requests.
- `docs/adr/0001-data-ownership-and-dependency-boundaries.md` owns the decision.

All changed files remain below 20 KB and 600 lines.

## Verification

Commands:

```powershell
dotnet build GodswarServer.sln `
  --configuration Release `
  --no-restore `
  --nologo

dotnet tests\Godswar.Server.ProtocolChecks\bin\Release\net10.0\Godswar.Server.ProtocolChecks.dll `
  "Data-boundary architecture ratchet"

dotnet tests\Godswar.Server.ProtocolChecks\bin\Release\net10.0\Godswar.Server.ProtocolChecks.dll
```

Results:

- Release solution build: 0 warnings, 0 errors.
- Focused architecture gate: 1 passed, 0 failed.
- Complete protocol-check harness: 178 passed, 0 failed.
- Architecture metrics: exact baseline, 0 new, 0 stale, 0 rule violations.
- Staged secret-pattern scan: 0 matches.
- `git diff --check`: passed.

The local SDK was `10.0.100-rc.2.25502.107`; its `NETSDK1057` preview notice
was informational rather than a build warning.

## Runtime and operational impact

No production `.cs` runtime path changed. There was no database mutation,
migration, server restart, Docker image replacement, listener change, packet
change, or client change.

The current CI workflow now makes the B02 check mandatory, but B03 still needs
to make disposable PostgreSQL 17 migration/integration checks mandatory and
fail closed when their database fixture is unavailable.

## Limitations

The per-file ratchet is intentionally conservative and source-based because
the repository is currently one project and partial classes must remain
attributable to their source files. It is paired with a compiled reflection
fingerprint for the complete `IGameStore` signature.

The `State -> Game` value measures using directives, not every type usage
beneath an existing import. `State` remains documented legacy mixed debt; it
is not presented as a clean Domain or Infrastructure layer.

## Rollback

Revert implementation commit `22c3d8e` as one unit if the gate itself must be
rolled back. Do not delete only the baseline or only the check. Runtime and
schema rollback are unnecessary because B02 changed neither.

## Next roadmap dependency

B03 is next: make disposable PostgreSQL 17 tests mandatory in CI, including
empty bootstrap, representative historical upgrade, current schema, and
fail-on-skip evidence. After B03, B04 can harden storage/security profiles,
and B05 can perform the first real data-boundary extraction.
