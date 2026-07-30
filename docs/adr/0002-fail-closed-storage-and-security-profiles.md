# ADR 0002: Fail-closed storage and security profiles

- Status: accepted
- Date: 2026-07-29
- Roadmap ticket: B04
- B14 amendment: 2026-07-31

## Context

The server previously treated every storage-provider value except the exact
word `postgres` as JSON. A blank provider was normalized to JSON, and a
missing options file was generated with JSON/raw defaults. Separately,
`Secure.Enabled = false` selected raw TCP. That raw login path calls
`LoginOrCreateAccountAsync`, while raw game admission associates an account by
username alone.

Those behaviors preserve the original client during development, but they are
not acceptable implicit fallbacks. A provider typo could create a second JSON
authority, and a secure-configuration typo could activate raw account
creation and username-only game binding.

## Decision

Startup must name one of two runtime profiles and one of two storage
providers. Matching is case-insensitive, but aliases, numeric values, blank
values, and unknown values are rejected.

| Runtime profile | Storage | Listener | Result |
| --- | --- | --- | --- |
| `LocalDevelopment` | `Json` or configured `Postgres` | Raw TCP, legacy option omitted/false | Rejected |
| `LocalDevelopment` | `Json` or configured `Postgres` | Raw TCP, legacy option true | Allowed only as explicit local rollback |
| `LocalDevelopment` | `Json` or configured `Postgres` | Secure TLS, legacy option false | Allowed |
| Any | Any | Secure TLS, legacy option true | Rejected |
| `Production` | Configured `Postgres` | Secure TLS, legacy and plaintext-migration options false | Allowed |
| `Production` | `Json` | Any | Rejected |
| `Production` | Configured `Postgres` | Raw TCP | Rejected |
| Missing/unknown profile or provider | Any | Any | Rejected |
| Any profile | `Postgres` without a connection string | Any | Rejected |

`ServerOptions.Load` validates before the composition root creates or seeds a
store. It no longer creates a missing options file. `Program.cs` uses the
validated provider in an exhaustive switch.

Raw compatibility is guarded at four points:

1. the separately configured legacy option, disabled in checked-in settings;
2. the validated startup policy;
3. listener-profile construction; and
4. the raw login and username-only game-binding branches.

The handlers require a `LegacyAuthenticationAccess` capability that can be
created only from a validated, explicitly enabled local raw profile. A secure
game channel without a ticket-bound principal disconnects before username
lookup even if a legacy capability is accidentally supplied.

`Production` also rejects `allowPlaintextMigration=true`; development
credentials must be converted to verifier-backed records before production
activation. `LocalDevelopment` secure mode may retain the migration option.

Malformed integer, unsigned-integer, or Boolean environment overrides in the
server authentication and secure-network option readers reject startup rather
than retaining a fallback.

## Observability

Startup emits only bounded profile, provider, transport, and rejection codes.
Metrics expose:

- `godswar.server.startup.rejections{reason}`; and
- `godswar.server.legacy_auth.attempts{endpoint,outcome}`.

The labels never include usernames, account IDs, addresses, connection
strings, credentials, tickets, or packet data.

## Consequences

- The original client remains usable only through the explicitly activated
  `legacy-raw` local Docker profile.
- A missing file, provider typo, or production secure-mode typo cannot
  silently select JSON or raw authentication.
- `Production` now means secure TLS plus PostgreSQL at server composition.
- Production credential rows must already be verifier-backed; this decision
  does not provide a production migration tool.
- The secure controlled-host/Docker acceptance setup remains
  `LocalDevelopment` because it uses local development trust material. Secure
  transport and production deployment identity are deliberately separate.
- `LocalDevelopment` alone no longer authorizes compatibility behavior.
  `allowLegacyRawAuthentication=true` is also required, and the checked-in
  Docker rollback publishes only loopback host ports.
- B14 completed application/configuration retirement on 2026-07-31. The
  retained rollback does not make legacy password/account semantics secure.

## Rollback

No schema or player-data rollback is required. If compatibility repair is
needed, keep the fail-closed parser and activate the checked-in
`legacy-raw` Docker profile on a controlled loopback host. Never restore
unknown-provider-to-JSON or production-to-raw fallback behavior.
