# B04 fail-closed storage and security profiles

Status: complete

- Implementation commit:
  `10289ede04c9f43689da80d954c655698ab1497f`
- Implementation tree:
  `7cb193d2001efbdd9c8ead9027b1f3720cb4f3eb`
- Base documentation commit: `8505ffc`
- Date: 2026-07-29
- Local result: passed
- Database/schema change: none
- Client/wire-protocol change: none

## Outcome

B04 removes the server's silent JSON and raw-authentication fallbacks.
Startup now requires an explicit `LocalDevelopment` or `Production` runtime
profile and an explicit `Json` or `Postgres` storage provider.

`Production` accepts only configured PostgreSQL plus secure TLS listeners.
JSON and raw TCP are allowed only by `LocalDevelopment`. A missing options
file is rejected without generating one, a PostgreSQL selection requires a
nonempty connection string, and malformed security-related environment values
reject rather than retaining a default.

The original client remains supported by the checked-in explicit
`LocalDevelopment` profile. This preserves wire behavior; it does not present
the legacy authentication semantics as secure.

## Decision and startup matrix

The owning decision is
[`ADR 0002`](adr/0002-fail-closed-storage-and-security-profiles.md).

| Runtime profile | Storage | Listener | Result |
| --- | --- | --- | --- |
| `LocalDevelopment` | `Json` | Raw TCP | Accepted; legacy capability issued |
| `LocalDevelopment` | Configured `Postgres` | Raw TCP | Accepted; legacy capability issued |
| `LocalDevelopment` | `Json` or configured `Postgres` | Secure TLS | Accepted; no legacy capability |
| `Production` | Configured `Postgres` | Secure TLS | Accepted; no legacy capability |
| `Production` | `Json` | Any | Rejected |
| `Production` | Configured `Postgres` | Raw TCP | Rejected |
| Missing/unknown profile or provider | Any | Any | Rejected |
| Any profile | `Postgres` without connection string | Any | Rejected |

The profile is selected with `runtimeProfile` or
`GODSWAR_RUNTIME_PROFILE`. It is not derived from
`DOTNET_ENVIRONMENT`/`ASPNETCORE_ENVIRONMENT`.

## Enforcement boundaries

### Configuration and composition

`ServerOptions.Load` no longer creates a missing configuration or normalizes a
blank provider to JSON. It binds environment overrides, normalizes the
remaining options, and applies `ServerRuntimeProfilePolicy` before
`Program.cs` creates or seeds a store.

`Program.cs` selects the validated provider with an exhaustive switch. The
former "PostgreSQL if exact, otherwise JSON" ternary no longer exists.

`ServerListenerProfile.Build` independently validates the policy, so a future
composition caller cannot create a production raw listener by bypassing the
main startup path.

### Authentication

`LegacyAuthenticationAccess` is issued only for a validated
`LocalDevelopment` raw profile. Both insecure compatibility operations require
that capability:

- raw login's `LoginOrCreateAccountAsync`; and
- game login's username-only `FindAccountByUsernameAsync`.

Without it, the handler disconnects before a store call. A secure game
session with no ticket-bound principal also disconnects before username
lookup, even if a legacy capability is supplied accidentally. Existing secure
login remains password-verifier based, and secure game binding remains tied to
the single-use ticket's account ID and username.

### Checked-in profiles

The following now name `LocalDevelopment` explicitly:

- `appsettings.json`;
- `appsettings.docker.json`;
- `docker-compose.yml`;
- `docker-compose.secure.yml`;
- `.env.example`;
- `.env.secure.example`; and
- controlled-host script environment maps.

The secure controlled-host profile remains local-development because it uses
local development trust material. Secure transport and production deployment
identity are separate decisions.

## Observability

Startup emits a bounded selected-profile event or a stable rejection code.
It never emits the provider connection string or an invalid raw value.

`ServerProfileMetrics` exposes:

- `godswar.server.startup.rejections{reason}`; and
- `godswar.server.legacy_auth.attempts{endpoint,outcome}`.

The accepted label values are bounded constants. Usernames, account IDs,
addresses, credentials, tickets, connection strings, and packet payloads are
not labels.

## Files

| File or area | Responsibility |
| --- | --- |
| `src/Godswar.Server/ServerRuntimeProfilePolicy.cs` | Strict profile/provider parsing, startup matrix, rejection codes, and legacy capability |
| `src/Godswar.Server/ServerOptions.cs` | Required config loading and strict environment parsing |
| `src/Godswar.Server/Program.cs` | Pre-store validation, exhaustive provider composition, startup event/warning |
| `src/Godswar.Server/Networking/ServerListenerProfile.cs` | Defense-in-depth listener guard |
| `src/Godswar.Server/Game/LoginClientHandler.cs` | Capability-gated raw account upsert |
| `src/Godswar.Server/Game/GameClientHandler.LoginWorldEntry.cs` | Capability-gated username bind and secure no-principal rejection |
| `src/Godswar.Server/Operations/ServerProfileMetrics.cs` | Low-cardinality startup/auth counters |
| `tests/Godswar.Server.ProtocolChecks/ServerRuntimeProfileChecks.cs` | Startup/config/environment/metrics matrix |
| `tests/Godswar.Server.ProtocolChecks/LegacyAuthenticationProfileChecks.cs` | Zero-store-call negative tests and explicit-local positive tests |
| appsettings, Compose, environment examples, controlled-host tools | Explicit local profile activation |

No migration, table, seed, durable player record, client executable, shim, or
packet codec changed.

## Verification

Commands and results:

| Verification | Result |
| --- | --- |
| Release solution build | Passed, 0 warnings and 0 errors |
| Focused profile/listener/auth/ticket/runtime suite | 9 passed, 0 failed |
| Complete Release protocol suite | 180 passed, 0 failed |
| Missing-options process smoke | Exit code 2, `options_file_missing`, no file created |
| Controlled-host exact server validation | Passed |
| Secure Docker rendered-profile contract | Passed |
| Secure Docker client-campaign policy fixtures | 30 passed |
| `git diff --check` | Passed |

The complete suite includes the fixed raw byte/cipher/bootstrap parity check,
the explicit-local raw login/game handler checks, secure ticket/principal
flow, listener exclusivity, configuration fixtures, ECS/runtime checks, and
the data-boundary ratchet.

The controlled Docker checks rendered and inspected configuration only. They
did not start, stop, or replace the user's live Docker server. No interactive
original-client process was launched for B04; the server-side wire path is
unchanged and its captured-byte compatibility checks passed.

The B03 disposable PostgreSQL migration gate was not repeated because B04
changes no schema, migration, repository transaction, or PostgreSQL query. Its
separate local and CI evidence remains in the B03 report.

## Security limitations

- Raw authentication is still intrinsically unsafe. B04 contains it in an
  explicit profile; B14 owns retirement.
- `LocalDevelopment` is an authorization choice, not a host firewall.
  Direct-run raw binds must remain on a controlled machine/network.
- The `Production` profile enforces storage/listener composition, but it does
  not by itself supply production certificates, upstream L3/L4 DDoS
  protection, secret management, capacity guarantees, or live operational
  approval.
- Existing raw-path diagnostic logs remain legacy debt for B13/B14. The new
  profile metrics add no player or network identifiers.

## Rollback

B04 has no schema rollback. Revert implementation commit `10289ed` as one
unit only if the policy itself must be repaired. The operational compatibility
rollback is the checked-in explicit `LocalDevelopment` profile on a controlled
host.

Never restore:

- missing/unknown provider to JSON;
- a missing options file to generated insecure defaults;
- malformed security values to silent fallback; or
- `Production` to raw authentication.

## Next roadmap dependency

B05 is next: extract `IWorldContentReader` as the first real application/data
boundary vertical slice while preserving reviewed world-content packets and
definitions.
