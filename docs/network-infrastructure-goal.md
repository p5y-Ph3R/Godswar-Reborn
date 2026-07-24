# Secure hybrid network infrastructure goal

## Version and status

- Document version: `1.10`
- Last updated: `2026-07-24`
- Project: Godswar Origin MMORPG emulator
- Chosen client approach: in-process modification through an application-local
  x86 `Net.dll` compatibility shim
- Long-term transport: TLS-protected TCP plus authenticated, encrypted UDP
- Current milestone: Phase 1 compatibility shim. Loading-gate v1 failed live
  validation and was rolled back; v2 is `InstalledExact` with automated gates
  passing and live acceptance pending. Phase 2 remains blocked and no secure
  listener or client bridge is enabled.
- Production-capacity guarantees: none; player count, regions, latency budget,
  hosting provider, and peak concurrency remain unspecified

This document is the durable reference for the networking migration. Update its
phase ledger and version history as work is accepted. Do not silently weaken the
security or compatibility gates.

## Goal

Build an authoritative, production-minded MMORPG networking foundation in
small reversible steps:

1. Preserve the original client behavior through an audited binary seam,
   except for explicitly documented compatibility fixes with their own gates.
2. Move reliable control traffic to TLS without changing gameplay semantics.
3. Bind a low-latency UDP channel to the authenticated TCP session.
4. Move only explicitly classified realtime messages to UDP.
5. Enforce server authority, bounded work, overload behavior, observability,
   and safe fallback.

The original client has no source and currently speaks only a proprietary raw
TCP protocol. The selected solution modifies the networking DLL loaded inside
`Origin.exe`; it does not require a launcher or separate gateway process.

## Repository assessment and assumptions

The server is a .NET 10 modular monolith. Its raw TCP defaults/container ports
are `5999` and `7000`; the current local Docker test binding is
`127.1.1.110:5998 -> 5999` and `127.1.1.110:7000 -> 7000`, and the installed
client uses host port `5998`. `ClientSession` owns the legacy length-prefixed
framing and rolling XOR stream. Game handlers already use `ClientSession` as a
common send/receive facade, which is the intended server transport seam.

The client is a 32-bit Windows executable. The installed stock networking DLL
has the following pinned contract:

- `Net.dll` SHA-256:
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`
- PE machine: `0x14C` / x86
- Export ordinal 1: `NetClientCreate`
- Export ordinal 2: `NetServiceCreate`
- `Origin.exe` imports `NetClientCreate` by name
- `INetClient` has exactly nine Microsoft x86 C++ virtual slots:
  `Release`, `SetHost`, `Connect`, `DisConnect`, `Process`, `GetStatus`,
  `PickMsg`, `SendMsg`, and `GetMsgNum`

The currently supported patched `Origin.exe` SHA-256 is
`753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`.
An unknown client or legacy DLL is a new compatibility target and must be
audited instead of forced through the installer.

Conservative defaults until measurements replace them:

- Windows 10/11 client support first.
- Existing gameplay remains reliable TCP by default.
- UDP datagrams stay at or below 1,200 bytes.
- UDP-blocked networks fall back to authenticated TLS, never raw TCP.
- No microservices, Kubernetes, Kafka, Redis, or service mesh without measured
  need.
- No production scale or DDoS capacity claim without a provider and workload.

## Threat model and target trust boundaries

The following is the target, not the current security state. In particular,
the legacy server still accepts absolute client position samples and has not
completed authoritative movement validation or bounded transport queues.

```text
Untrusted player / network
        |
        | TLS control + authenticated UDP
        v
Protected L3/L4 edge (future provider)
        |
        | allowlisted/authenticated origin path
        v
Transport ingress -> bounded session queues -> fixed-step simulation
                                               |
                                               | commands / immutable results
                                               v
                                      persistence workers

Origin.exe (untrusted) -> Net.dll shim -> verified NetLegacy.dll
        |
        +-- player inputs are requests, never authoritative outcomes
```

Trust boundaries:

- The client and all values it sends are untrusted, even after authentication.
- TLS authenticates the server and protects reliable traffic; it does not make
  the client trustworthy.
- A UDP endpoint is not identified by IP address or port alone.
- The finished simulation owns position, damage, inventory, rewards, and
  progression.
- Database, logging, sockets, and external calls may not block the simulation
  tick.
- Administrative, profiling, and metrics endpoints are private.

Primary threats include credential theft, account impersonation, malformed
frames, slow clients, replay, UDP spoofing, reflection/amplification, endpoint
takeover, queue/state exhaustion, client manipulation, origin exposure, and
volumetric TCP/UDP floods.

## Architecture decisions

### Transport alternatives

| Option | Advantages | Costs and compatibility | Decision |
| --- | --- | --- | --- |
| Raw UDP plus TLS TCP | Fits the legacy DLL seam; gives deliberate delivery semantics; mature Windows and .NET crypto/TLS APIs | Requires a reviewed UDP envelope, replay protection, pacing, NAT rebinding, and two-transport reconciliation | Selected |
| QUIC streams plus DATAGRAM | One cryptographic connection and modern migration/congestion behavior | Adds a substantial native x86 client dependency and DATAGRAM/platform compatibility risk; would silently replace the requested TCP/UDP architecture | Reconsider only through a new ADR |
| Mature game-networking library | Can supply sequencing, reliability, congestion, and testing | Must match x86 Windows, .NET server, security, DDoS, license, and legacy translation requirements; no library has yet been validated against all of them | Evaluate before Phase 3 |

TLS TCP is used for authentication, account/character control, inventory,
forging, chat, spawn/despawn, map transfer, damage, progression, and other
durable operations. UDP is used only for high-frequency data where stale
information is less useful than missing information.

### Client integration alternatives

1. Patch or replace the application-local networking DLL: selected.
2. Separate launcher/gateway process: rejected as the primary path because it
   adds deployment and lifecycle complexity.
3. New compatible client: possible, but equivalent to a long-term client
   rewrite because the original source is unavailable.

The selected DLL will eventually host the TLS/UDP bridge in-process. Phase 1
does not change endpoints, bytes, framing, or transport. Its sole proposed
delivery-timing exception is the audited one-character preview. Failed v1
suppressed native `Process`, held the exact opcode-`10002` pointer for up to 30
seconds, then disposed it and disconnected. Installed v2 always delegates
`Process`, preserves pointer identity and order, and returns
the pointer on readiness or after a five-second guarded fallback. That fallback
can still yield a blank model when resources never become ready.

### Phase 2 client bridge contract

The shim will run a loopback bridge inside the `Origin.exe` process. It will
point the verified `NetLegacy.dll` at that private listener, preserve the stock
XOR/framing parser and proprietary `CMsg` allocation, and carry the unwrapped
stream externally over TLS. Once accepted, the Phase 1 preview gate is the only
approved `PickMsg`-lifetime exception: one exact native pointer may be retained
until readiness or the five-second fallback. The same pointer is returned to
Origin in either case. It is destroyed by the shim only if an explicit
`Connect`, `DisConnect`, `Release`, or destruction reset still owns it. There
is no launcher or separate gateway process.

The two client objects used for login and game connections need one
process-local coordinator. The login TLS connection supplies an account-,
server-, audience-, protocol-, and expiry-scoped game ticket; the coordinator
hands it only to the matching redirected game connection and erases it on use,
expiry, logout, or failure. It is never written to logs or disk.

The initial secure login endpoint comes from guarded local configuration. A
versioned authenticated redirect selects the game endpoint. Both use a DNS
certificate name for SNI and platform-root validation; local development uses
an explicitly installed development CA, never a validation bypass. Certificate
rotation, endpoint/config signing, and the versioned outer TLS preface/framing
are blocking Phase 2 protocol decisions. TLS failure cannot downgrade to raw
TCP.

### Server seam

Keep `ClientSession` as the compatibility facade used by existing handlers.
Later phases add behind it:

- an opaque stable `ConnectionId`;
- `IReliableTransport` and optional `IRealtimeTransport`;
- one bounded, single-consumer ingress queue;
- a bounded reliable egress queue;
- a replace-stale bounded snapshot queue;
- explicit `Reliable`, `UnreliableSequenced`, and narrowly scoped
  `ReliableExpiring` delivery policies.

Existing `SendAsync` calls remain reliable. UDP requires explicit opt-in.
TCP and UDP never share implicit ordering; ticks, event IDs, transport epochs,
idempotency, and reconciliation define cross-channel behavior.

## Initial traffic classification

Reliable TLS/TCP:

- Login/control opcodes `1`, `4`, `6`, and `10000` through `10008`.
- Character lifecycle and readiness.
- Chat `10035`.
- Inventory/equipment/storage `10022`, `10023`, `10048` through `10053`,
  and `10056`.
- Basic attack `10026` and skill cast `10040` initially.
- NPC interactions `10067` through `10070`.
- Forging `10109` through `10117`, Gear Mentor `10193`.
- Inspection/detail, Zodiac `10297`, time, heartbeat, damage, death, rewards,
  and progression.
- Spawn/despawn, map transfer, and animation start/end events.

First UDP candidate:

- A new versioned and sequenced movement-sample envelope carrying the meaning
  of legacy player movement opcode `10194`.
- Periodic authoritative position snapshots and full keyframes.

Remain on TCP until recovery semantics exist:

- Player walk begin/end `10013` and `10014`.
- Monster movement start/end `0x2720` and `0x2721`.
- Attack and skill events.

## Secure session-binding target

1. Authenticate through TLS.
2. Issue a short-lived ticket scoped to account, server, audience, protocol,
   permissions, and expiry, plus an opaque connection ID.
3. Require a stateless UDP challenge/cookie before allocating meaningful state.
4. Protect validated datagrams with reviewed AEAD, sequence numbers, a bounded
   replay window, and short-lived key epochs.
5. Revalidate authenticated NAT rebinding without permitting endpoint takeover.
6. Rotate secrets with a documented overlap; expire tickets, logout sessions,
   and clean state deterministically.

Cryptographic primitives will come from established platform or reviewed
libraries. The exact DTLS versus audited AEAD construction is a blocking ADR
before Phase 3 implementation.

## DDoS responsibility matrix

| Layer | Responsibilities | Cannot provide |
| --- | --- | --- |
| Application | Stateless UDP validation, anti-amplification, strict parsing limits, replay rejection, bounded queues/maps/workers, token buckets, authenticated-session priority, admission control, load shedding, low-cardinality telemetry | Absorb a link-saturating volumetric flood |
| OS/network | SYN cookies/backlog tuning, connection tracking, socket buffers, firewall policy, process/file-descriptor limits, private administration path | Replace upstream clean bandwidth and packet scrubbing |
| Upstream provider | Anycast or protected edge, L3/L4 TCP and arbitrary-UDP scrubbing, clean bandwidth/PPS, health/failover, authenticated client-IP forwarding, mitigation telemetry and SLA | Validate game rules or replace bounded application work |

The origin must eventually accept public game traffic only from the protected
edge or authenticated tunnel. An ordinary HTTP CDN/WAF, autoscaling, IP bans,
or rate limiting alone is not volumetric DDoS protection.

## Phase ledger

### Phase 1 — reversible client compatibility seam

Status: not accepted. V1 failed and was rolled back. V2 is `InstalledExact`;
automated gates pass and fresh live account-switch evidence is pending.

Recorded local state:

- Historical failed v1 shim SHA-256:
  `2D819908BEE2FA7D8BE4957E18358DEFFB5FD65D01AC26D6F73F29F4C71E2AE0`
- Historical network-stable pass-through shim SHA-256:
  `528913E66888D5C070C39949D2FC1AE439B8414B15152312D4E093A29D17A6DD`
- Historical pass-through Apply evidence:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-151248244`
- Installed v2 shim SHA-256:
  `73E65FBFA3EA9809AF597DA3D25D1E0963B0A4A467549191BAFB4FAE9F2902FD`
- Installed `2026-07-24T03:55:57Z`; current Apply/stock-restore backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-155531621`
- Current Apply manifest SHA-256:
  `9A92451A6786EBBCBA65EA27B09A0EFDA0115754CCE73408CA717FC3CE4B8DFC`
- V1 evidence run `20260724T030417842Z-94e2c5f4`: `Fail`.

V1 passed account 7, then account 13 disconnected after `14.633832034`
seconds, showed server-full, and dumped at `0x005F58BC`; the server stayed up.
Under the rollback shim, a later account-7 baseline received a valid preview
and kept its TCP connection established for more than 142 seconds but still
showed a blank model with no new dump. See
[`client-avatar-preview-loading-gate-incident-20260724.md`](client-avatar-preview-loading-gate-incident-20260724.md).

Deliverables:

- Win32/x86 `Net.dll` with the exact two named exports and ordinals.
- Nine-slot proxy that delegates to the pinned `NetLegacy.dll`.
- Installed exact-pointer character-preview loading gate with continuous
  native processing, preserved order, a five-second handoff, and
  lifecycle-only cleanup, documented in
  [`client-avatar-preview-loading-gate.md`](client-avatar-preview-loading-gate.md).
- Legacy hash verification before loading.
- No work under `DllMain` beyond recording the module and disabling thread
  notifications.
- ASLR, NX, Control Flow Guard, static runtime, and a preferred image base
  distinct from stock `NetLegacy.dll`'s preferred base.
- Strict proxy/ABI tests, repeated stock factory lifecycle probe, and guarded
  Apply/Restore tooling with exact backups.

Exit gate:

- Automated build, export, hardening, all-slot delegation, and 32-cycle
  stock-object checks pass.
- Installer Apply and Restore pass on a disposable client copy.
- Installed client completes the manual parity test below.
- Rollback is proven.

Phase 1 intentionally has no TLS, UDP, server, database, `Origin.exe`, config,
or gameplay-state change. V2 changes only one audited preview's delivery
timing. It aims to let a late model load automatically, but the bounded
fallback does not guarantee that outcome.

### Phase 2 — framing bridge, TLS, and real authentication

Normative design and verification:
[`docs/network-infrastructure-phase2.md`](network-infrastructure-phase2.md).
Exact wire protocol and client lifecycle:
[`docs/network-infrastructure-phase2-protocol.md`](network-infrastructure-phase2-protocol.md).

- Golden-test current login/game streams.
- Add the server transport seam and bounded queues without byte changes.
- Add separate TLS ports; never sniff raw and TLS on one port.
- Specify a versioned outer preface, compatibility/error behavior, maximum
  fields, SNI/hostname validation, development trust, and certificate rotation.
- Add maximum frames, deadlines, slow-client protection, bounded handshakes,
  password hashing/migration, and short-lived game tickets.
- Define and test bounded queue overflow, backpressure, and rejection metrics.
- Add golden, boundary, partial/coalesced, malformed, fuzz/property, timeout,
  and slow-client tests for every new TCP decoder in this phase.
- Keep raw `5999/7000` only behind an explicit loopback/private development
  flag.
- Reject username-only game login.

Exit gate: legacy parity, TLS authentication/control, ticket forgery/expiry/
replay tests, frame boundary tests, and authenticated TLS-only fallback pass.

### Phase 3 — authenticated UDP binding without gameplay

- Specify the versioned binary UDP envelope and limits.
- Add stateless cookies, AEAD, replay windows, key epochs, NAT rebinding,
  cleanup, pacing, rate limits, and observability.
- Negotiate and exercise UDP keepalive while gameplay stays on TLS.
- Add fuzz/property tests and low-cardinality rejection/rate-limit/session
  metrics with the UDP decoder rather than deferring them.

Exit gate: forgery, tamper, replay, wraparound, duplication, reordering, loss,
rotation, NAT rebinding, 1,200-byte MTU, amplification, and bounded-state tests
pass.

### Phase 4 — first hybrid authoritative slice

- Carry sequenced `10194` movement meaning over UDP.
- Publish authoritative position snapshots plus periodic keyframes.
- Add authenticated TLS fallback and a transport epoch so one logical input is
  never applied twice.
- Reject impossible movement cadence, speed, distance, and map transitions;
  send correction.

Exit gate: network-emulation tests and stock-client end-to-end parity pass under
latency, jitter, burst loss, duplication, reordering, and UDP blocking.

### Phase 5 — extension, hardening, and operations

- Extend realtime traffic only with explicit recovery semantics.
- Add fixed-step deterministic replay, bounded bot load/soak tests, dashboards,
  alerts, and runbooks; extend the decoder fuzzing and metrics introduced in
  their owning phases.
- Produce provider-neutral deployment controls until hosting is selected.
- Integrate an upstream TCP/arbitrary-UDP protection provider only after
  approval.

Exit gate: security, overload/recovery, observability, local benchmark, rollback,
and incident-response gates pass. Local results are not production guarantees.

## Phase 1 verification and rollback

The exact build prerequisites, automated suites, guarded Apply/Restore commands,
interactive parity checklist, and pending acceptance record live in
[`docs/network-infrastructure-phase1.md`](network-infrastructure-phase1.md).
Phase 1 remains unaccepted until that record passes.

## Known limitations and unresolved decisions

- The shim sees legacy absolute position samples, not keyboard intent. It can
  authenticate, sequence, validate, and reconcile movement, but true input-level
  prediction may require a later targeted `Origin.exe` hook.
- Client signing and distribution remain undecided.
- Hosting region/provider, expected concurrency, tick/snapshot rates, latency
  target, packet-loss target, and budget remain open capacity inputs.
- Provider-specific infrastructure and paid deployment require approval.
- The application-layer cryptographic construction requires a separate reviewed
  ADR before implementation.

## Version history

The version ledger is maintained separately in
[`network-infrastructure-history.md`](network-infrastructure-history.md) so
this design reference stays below the repository size limit.
