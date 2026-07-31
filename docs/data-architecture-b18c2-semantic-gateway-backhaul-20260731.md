# B18C2 semantic gateway and authenticated worker backhaul

Status: completed and verified 2026-07-31

## Outcome

B18C2 adds an opt-in semantic gateway mode for the unchanged original
client:

```text
Godswar.Server --semantic-gateway <serverOptionsPath> <gatewayConfigPath>
```

The client-facing compatibility leg is deliberately restricted to local
loopback. Credentials terminate at the gateway and are not sent to a game
worker:

```text
unchanged original client
  | legacy raw TCP, loopback only
  | login credentials + encrypted game stream
  v
semantic gateway
  - verifies credentials
  - creates one bounded login generation
  - selects exact RealmId/MapId/WorldInstanceId/ServerNodeId
  - issues one single-use game admission
  | TLS 1.3 + mutual leaf pinning + ALPN
  | authenticated metadata, then untouched legacy ciphertext
  v
authoritative game worker
  - validates the exact route and replay/account bounds
  - decrypts the original legacy stream once
  - owns GameClientHandler, ECS/map runtime, and durable operations
  v
PostgreSQL in production; JSON only in LocalDevelopment
```

The gateway is now the full account-admission authority for this topology:
it verifies credentials, binds the server-derived account identity, selects
the route, and creates the admission that a worker trusts. It does not own
ECS entities, inventories, currency, progression, or persistence. Workers
remain authoritative for gameplay and continue to use the B15 PostgreSQL
ownership fence for valuable mutations. `ISemanticGatewayDataSession`
deliberately exposes only authentication and the character route projection;
`LegacySemanticGatewayDataSession` keeps the broad legacy store behind that
focused application boundary.

## Trust and protocol boundaries

The original executable has no source modification and speaks only its
legacy TCP protocol. B18C2 therefore keeps that protocol on a loopback-only
edge. It is not safe to publish those raw listener ports.

The worker's mTLS policy authenticates and encrypts the private gateway hop;
it does **not** protect a worker from a compromised gateway that already owns
an accepted pinned client key. Such a gateway can forge account admissions,
so compromise of the semantic-gateway host or pinned private key is a
high-impact account-admission incident. Recovery must isolate and rebuild the
gateway, revoke the compromised pin/key at every worker, invalidate or drain
its admissions, audit affected sessions, and install a rotated key/pin only
after containment. A bounded pin-overlap window is for planned rotation, not
permission to leave a compromised pin active.

After password verification through `AccountAuthenticationService`, the
gateway creates identities that are independent of an IP address:

- `GatewayConnectionId` identifies one accepted gateway connection;
- `GatewayLoginGenerationId` identifies one authenticated login generation;
- `GatewayAdmissionId` identifies one bounded route reservation; and
- `SemanticGatewayPrincipal` carries the server-derived account ID and
  canonical username.

The observed client address is defense-in-depth context only. No account is
selected by an address or source port. The game connection must present the
same canonical username, address context, active generation, principal, and
new connection identity. Username lookup is ordinal case-insensitive so a
client casing change cannot create a second logical identity, while the
server-provided canonical username is retained on the principal and backhaul.

The private worker hop uses a fixed, versioned binary protocol:

- 223-byte `OpenSession` frame;
- 32-byte `AdmissionResponse` frame;
- network byte order;
- fixed maximum username and node fields;
- exact version, type, payload length, reserved-byte, timestamp, address,
  and identifier validation; and
- no attacker-controlled collections or variable-size allocation.

`GatewayWorldAdmission` carries the gateway boot and connection IDs, login
generation, account/character, username, observed endpoint, issue/expiry
times, and the complete route tuple. TCP/TLS establishes transport ordering;
the gateway never assumes ordering relative to the separate secure-UDP
experiment.

## Authentication and single-use lifecycle

`SemanticGatewayLoginHandler` implements the existing login/select/redirect
exchange. It copies the bounded password field, verifies it through the
hardened application boundary, zeroes the password scratch and source packet
field, marks the connection authenticated, and creates a pending login
generation. Pending generations cannot be found or reserved by the game leg.
The redirect path activates the generation, then its ordered generation
sequence lets `SemanticGatewayConnectionCoordinator` cancel only an older
relay before the replacement waits for bounded release. Delayed work from an
older generation cannot cancel a newer relay. If a login connection ends
before completing its game redirect, `CancelLogin` removes its authority and
an exact-generation stop cancels only its own relay.

`LegacyGameLoginProbe` reads exactly one encrypted `LoginGameServer` packet.
It decrypts only a temporary copy to obtain the username. The original
ciphertext remains unchanged, including when the client coalesces the next
packet in the same TCP stream. The decrypted header/body scratch, rejected
ciphertext, and retained first encrypted packet are cleared when their
bounded lifetimes end.

The game listener then:

1. finds the authenticated generation by canonical username plus source
   context;
2. reads the server-owned active character and current map;
3. selects the configured exact route, or the one bootstrap route for an
   account without a character;
4. reserves the route and creates an authenticated worker admission;
5. completes TLS and worker admission before committing the gateway
   reservation;
6. forwards the untouched first encrypted packet, followed by bounded
   bidirectional byte pumps; and
7. releases route and account capacity when either side closes.

The default `MaximumAdmissionsPerGeneration` is one lifetime issuance, not
one concurrent admission. Rollback or disconnect frees capacity but does not
make the generation reusable. Reconnect after a failed worker connection
therefore requires a complete login and a fresh generation. There is no
transparent session migration.

Committed admissions are refreshed at half their configured TTL while the
tunnel remains active. A draining worker rejects new reserve-to-commit
transitions but preserves established sessions. An unavailable worker
invalidates the next refresh. Gateway and worker cleanup are bounded.
During duplicate-login replacement, the old gateway relay can close just
before the worker observes EOF and releases its account lease. The gateway
retries only `AccountAlreadyActive` with the same reserved claim, five
attempts, bounded exponential delay, and the original expiry/cancellation
deadline. Every other worker rejection remains immediate.

## Worker backhaul

`WorkerBackhaulRuntime` changes normal server composition only when
`Backhaul.Enabled` is explicit. That mode:

- exposes exactly one private game listener;
- exposes no public login, raw game, secure public TLS, or secure UDP
  listener;
- requires at least one exact static open-world route;
- requires a private/loopback bind and rejects a management-port collision;
- authenticates the gateway certificate before reading `OpenSession`;
- validates exact node, realm, map, world-instance, account, replay,
  capacity, clock, and drain policy before exposing legacy bytes; and
- begins admission drain for management drain, process signals, and shutdown.

Backhaul mode also requires static open-world ownership. A worker fails
closed instead of allocating or joining an open-world map that is absent from
its configured static route set. Initial player join and the pre-join monster
bootstrap both consume the admitted realm, map, and world-instance identity;
they do not fall back to a Tempest map-only runtime.

`ClientSession` treats the accepted backhaul principal as authenticated.
`GameClientHandler` loads the account by its bound account ID, validates the
durable character and complete admitted route before replacing another
account session, and then enters the existing gameplay path. Invalid
admissions cannot evict a valid player session.

The worker registry permits one reserved or active session per account.
Reserved/active capacity and replay capacity are separate bounds, so
disconnect churn cannot consume live admission slots. Replay tombstones use a
finite configurable budget, expiry index, and deterministic earliest-expiry
eviction. Ticket expiry limits admission setup; an already active worker
session remains owned until its transport lease is disposed.
Under extreme churn, replay retention is therefore bounded by both time and
capacity; evicted tombstones no longer extend the worker-local replay window.
The gateway's single-use generation remains the primary admission guard.

## TLS policy and development material

The worker hop requires:

- TLS 1.3;
- ALPN `godswar-backhaul/1`;
- mutual authentication;
- exact SHA-256 leaf pins with a bounded rotation set;
- client-auth EKU on the gateway leaf;
- server-auth EKU on the worker leaf;
- non-CA leaves, validity checks, digital-signature-capable keys, and
  supported TLS 1.3 cipher suites.

No cryptographic primitive is implemented by the game. The platform TLS and
X.509 libraries provide the handshake and cryptography. Exact leaf pinning
replaces public-PKI name/chain trust for this private hop. Production secret
distribution and rotation remain deployment responsibilities.

`tools/NewDevelopmentBackhaulCertificates.ps1` creates a private development
root, one gateway client leaf, two worker server leaves, PFX/CER files, and a
manifest containing the three exact leaf pins. It refuses an existing output
path, applies a user-only ACL, never installs trust, reads the PFX password
from an environment variable, and defaults to seven-day validity.
`tools/TestDevelopmentBackhaulCertificates.ps1` validates content, EKUs,
pins, ACL, trust-store non-mutation, password rejection, and overwrite
rejection. Windows loads private keys with `DefaultKeySet` for Schannel;
other platforms use ephemeral key storage.

Checked-in examples:

- `appsettings.semantic-gateway.example.json`;
- `appsettings.backhaul-worker.example.json`.

Their all-zero-style pins are placeholders and must be replaced from the
generated manifest. No certificate, private key, password, or production
credential is committed.

## Principal implementation files

| Area | Repository paths and symbols |
| --- | --- |
| Gateway application boundary | `Application/Gateway/ISemanticGatewayDataSession.cs`, `State/LegacySemanticGatewayDataSession.cs` |
| Gateway identity and authority | `Networking/SemanticGateway/SemanticGatewayIdentifiers.cs`, `SemanticGatewayAdmissionAuthority*`, `SemanticGatewayConnectionCoordinator`, `StaticSemanticGatewayRouteDirectory` |
| Gateway configuration | `SemanticGatewayRuntimeOptions*`, `SemanticGatewayRuntimeConfiguration`, the semantic-gateway example |
| Local login edge | `SemanticGatewayCommand`, `SemanticGatewayHost`, `SemanticGatewayLoginHandler` |
| Game routing/tunnel | `LegacyGameLoginProbe`, `SemanticGatewayGameServer`, `SemanticGatewayGameConnection`, `SemanticGatewayGameModels` |
| Backhaul wire/TLS | `Networking/Backhaul/BackhaulModels.cs`, `BackhaulCodec`, `BackhaulTlsPolicy`, `BackhaulStreamIo`, `GatewayBackhaulClient` |
| Worker admission | `WorkerBackhaulAdmissionRegistry`, `WorkerBackhaulTransportFactory`, `BackhaulWorkerRuntimeOptions`, `WorkerBackhaulRuntime` |
| Exact runtime route | `WorldInstanceRuntimeOptions.StaticOpenWorldInstances`, `LocalWorldInstanceRuntimeDirectory.GetOrCreateAssignedOpenWorldAsync`, `GameSessionRegistry.AcceptsGatewayAdmission` |
| Handler boundary | `ClientSession.BoundGamePrincipal`, `ClientSession.GatewayWorldAdmission`, `GameClientHandler.ValidateGatewayAdmission` |

Every new source file remains below the repository's 20 KB limit.

## Verification

Milestone closeout produced the following exact results:

- focused B18C2 catalog: **5/5 passed**;
- repository-wide managed catalog: **280/280 passed**;
- Release solution build: **0 warnings and 0 errors**;
- disposable PostgreSQL catalog: **43/43 checks passed**, including **5/5
  migration scenarios**, followed by successful cleanup; and
- development backhaul-certificate validation: **passed**.

Focused coverage includes:

- codec golden vectors, round trips, truncation, malformed/random data, byte
  order, IPv4/IPv6, and fixed bounds;
- exact route, worker capacity, drain, unavailable, and revision behavior;
- duplicate login, single-use generation, reserve/commit/refresh/rollback,
  expiry, and concurrency;
- worker wrong-node/route/account, replay, expiry, capacity, and drain;
- certificate pin, private-key, EKU, TLS version, cipher, ALPN, missing-client
  certificate, and real loopback mutual-TLS checks;
- segmented/coalesced legacy login probing with exact ciphertext
  preservation;
- two-worker route, drain, failure-isolation, replay, and full-login
  reconnect behavior through the real gateway/worker transport, including a
  forced delayed worker release during replacement;
- exact non-Tempest realm/map/world join and pre-join monster bootstrap,
  plus unchanged direct legacy Tempest routing;
- live-capacity/replay-capacity separation, bounded churn, deterministic
  replay eviction, and expiry-index accounting;
- the focused application data-session boundary, case-insensitive lookup
  with canonical server casing, pending-to-activated redirect admission,
  generation-ordered duplicate-login replacement, exact abandoned-login
  cancellation, credential scratch clearing, and static-map ownership
  fail-closed behavior; and
- the solution build plus file-size and whitespace gates.

## Explicit non-claims

B18C2 does not add:

- a modified game client or production-secure client-facing transport;
- UDP routing, gateway UDP termination, or cross-transport ordering;
- Redis, distributed discovery, shared presence, leases, or high
  availability;
- live cross-worker map/session transfer or transparent reconnect;
- dynamic dungeon/battlefield placement, cross-realm Pindus admission, or
  cross-realm result settlement;
- health-based failover, state migration, or geographically distributed
  gateways;
- public edge DDoS scrubbing, origin hiding, provider tunnels, or production
  certificate/secret deployment;
- a database migration or a change in durable-data ownership; or
- a claim that the local gateway/worker split has production capacity.

Static routes make worker ownership explicit at login. Until controlled
cross-worker transfer exists, connected open-world map groups that permit
direct portal movement must remain on the same worker.

## Rollback and next milestone

Rollback drains B18C2 admissions, omits `--semantic-gateway`, disables worker
backhaul mode, and returns to the verified B18C1 local relay or the direct
single-worker compatibility profile. PostgreSQL rows, ownership generations,
world-instance IDs, and all durable data remain unchanged.

With B18C2 completed and verified, B17 is next. B17 will replace only
disposable in-memory coordination with Redis-backed login/admission tickets,
routes, presence, and PostgreSQL-fenced leases. It must record provider,
latency, timeout, availability, memory, eviction, recovery, region, and cost
budgets before activation. Redis will not own player value.
