# ADR 0002: Fail-closed storage and security profiles

- Status: accepted
- Date: 2026-07-29
- Roadmap ticket: B04

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
| `LocalDevelopment` | `Json` | Raw TCP | Allowed with an explicit legacy-authentication capability |
| `LocalDevelopment` | `Postgres` plus connection string | Raw TCP | Allowed with the same capability |
| `LocalDevelopment` | `Json` or configured `Postgres` | Secure TLS | Allowed; no legacy capability is issued |
| `Production` | Configured `Postgres` | Secure TLS | Allowed |
| `Production` | `Json` | Any | Rejected |
| `Production` | Configured `Postgres` | Raw TCP | Rejected |
| Missing/unknown profile or provider | Any | Any | Rejected |
| Any profile | `Postgres` without a connection string | Any | Rejected |

`ServerOptions.Load` validates before the composition root creates or seeds a
store. It no longer creates a missing options file. `Program.cs` uses the
validated provider in an exhaustive switch.

Raw compatibility is guarded at three points:

1. the validated startup policy;
2. listener-profile construction; and
3. the raw login and username-only game-binding branches.

The handlers require a `LegacyAuthenticationAccess` capability that can be
created only from a validated local raw profile. A secure game channel without
a ticket-bound principal disconnects before username lookup even if a legacy
capability is accidentally supplied.

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

- The original client remains usable under an explicitly reviewed
  `LocalDevelopment` profile.
- A missing file, provider typo, or production secure-mode typo cannot
  silently select JSON or raw authentication.
- `Production` now means secure TLS plus PostgreSQL at server composition.
- The secure controlled-host/Docker acceptance setup remains
  `LocalDevelopment` because it uses local development trust material. Secure
  transport and production deployment identity are deliberately separate.
- `LocalDevelopment` authorizes compatibility behavior; it is not a firewall.
  Raw listeners must still be restricted to a controlled host/network.
- B14 still owns complete retirement of raw authentication. B04 contains it;
  it does not make the legacy password/account semantics secure.

## Rollback

No schema or player-data rollback is required. If compatibility repair is
needed, keep the fail-closed parser and select the checked-in explicit
`LocalDevelopment` profile on a controlled host. Never restore
unknown-provider-to-JSON or production-to-raw fallback behavior.
