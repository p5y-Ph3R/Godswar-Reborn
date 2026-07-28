# Secure hybrid network infrastructure goal

## Version and status

- Document version: `1.34`
- Last updated: `2026-07-28`
- Project: Godswar Origin MMORPG emulator
- Chosen client approach: in-process modification through an application-local
  x86 `Net.dll` compatibility shim
- Long-term transport: TLS-protected TCP plus authenticated, encrypted UDP
- Current milestone: Phase 5A's bounded local replay/load/observability
  baseline is complete; production exporter/live-load gates remain.
- Map, traversal, and AOI optimization may proceed only as a separate slice.
- Production capacity and hosting inputs remain open.

This is the durable migration reference; do not weaken its security or
compatibility gates.

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

V6 pairs Origin `E177D94D...CC76C` with `GWKEY02` Net
`21695893...CAE97`. See
[`network-infrastructure-preview-ready-v6.md`](network-infrastructure-preview-ready-v6.md).

Conservative defaults until measurements replace them:

- Windows 10/11 client support first.
- Existing gameplay remains reliable TCP by default.
- UDP datagrams stay at or below 1,200 bytes.
- UDP-blocked networks fall back to authenticated TLS, never raw TCP.
- No microservices, Kubernetes, Kafka, Redis, or service mesh without measured
  need.
- No production scale or DDoS capacity claim without a provider and workload.

## Threat model and target trust boundaries

The following is the target architecture. The secure path has been accepted
on a disposable original client and is available through a mutually exclusive
Docker profile, but checked-in defaults remain off after exact rollback. The
legacy path still accepts absolute client position samples when raw mode is
selected.

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
| Mature game-networking library | Can supply sequencing, reliability, congestion, and testing | Must match x86 Windows, .NET server, security, DDoS, license, and legacy translation requirements; no library has yet been validated against all of them | Reconsider only through a new ADR |

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

The selected DLL will host the TLS/UDP bridge in-process. Phase 1 V1–V4 were
rejected and rolled back; their exact behavior and evidence remain in the
[Phase 1 runbook](network-infrastructure-phase1.md).

### Phase 2 client bridge contract

The shim will run a loopback bridge inside the `Origin.exe` process. It will
point the verified `NetLegacy.dll` at that private listener, preserve the stock
XOR/framing parser and proprietary `CMsg` allocation, and carry the unwrapped
stream externally over TLS. Phase 2 starts from predecessor Origin plus stock
Net; Phase 1's rejected V4 `PickMsg` preview gate is not installed or accepted.
Phase 2 must preserve stock `PickMsg` ownership unless a separately reviewed
and accepted correction supersedes this parked issue. There is no launcher or
separate gateway process.

The two client objects used for login and game connections need one
process-local coordinator. The login TLS connection supplies an account-,
server-, audience-, protocol-, and expiry-scoped game ticket; the coordinator
hands it only to the matching redirected game connection and erases it on use,
expiry, logout, or failure. It is never written to logs or disk.

The initial secure login endpoint comes from guarded local configuration. A
versioned authenticated redirect selects the game endpoint. Both use a DNS
certificate name for SNI and platform-root validation; local development uses
an explicitly installed development CA, never a validation bypass. Certificate
rotation and endpoint/config signing remain runtime prerequisites; outer
preface/framing syntax is implemented. TLS failure cannot downgrade to raw TCP.

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

Status: not accepted; V1–V4 are rejected and rolled back.

The exact V1–V4 hashes, backups, manifests, and evidence IDs live in the
[Phase 1 runbook](network-infrastructure-phase1.md). Final V4 evidence is
sealed `Fail`; current state is predecessor Origin `753BE49F...9ED79`, stock
Net `1CC3F9AA...BCA00C`, and no `NetLegacy.dll`.

Its native proxy, preview experiment, hardening, compatibility tests, and
guarded rollback tooling are preserved in the Phase 1 record.

Exit gate:

- Automated build, export, hardening, all-slot delegation, and 32-cycle
  stock-object checks pass.
- Installer Apply and Restore pass on a disposable client copy.
- Installed client completes the manual parity test below.
- Rollback is proven.

Phase 1 intentionally made no TLS, UDP, server, database, config, or
gameplay-state change. Its final smoke is sealed `Fail`; the mandatory Net-first
rollback completed. Retain the record and continue Phase 2 without claiming
Phase 1 acceptance.

### Phase 2 — framing bridge, TLS, and real authentication

Normative design and verification:
[`docs/network-infrastructure-phase2.md`](network-infrastructure-phase2.md).
Exact wire protocol and client lifecycle:
[`docs/network-infrastructure-phase2-protocol.md`](network-infrastructure-phase2-protocol.md).

- Golden-test current login/game streams.
- Slice 2 pure codecs are implemented with bounded incremental parsing,
  caller-owned buffers, secret disposal/clearing, and role/direction checks.
- Slice 3 extracted `ILegacyByteTransport` and the raw adapter. Synthetic and
  captured-clear bootstrap hashes, boundary tests, handler dispatch, loopback,
  and concurrent sends prove raw parity; full credential-bearing capture
  replay remains an uncommitted final Phase 2 gate.
- Slice 4 bounded admission, tracked tasks, reliable egress, deadlines, and
  metrics is implemented; see
  [`network-infrastructure-phase2-runtime.md`](network-infrastructure-phase2-runtime.md).
- Slice 5's uninstalled native coordinator and bounded client pumps are
  implemented; see
  [`network-infrastructure-phase2-client-runtime.md`](network-infrastructure-phase2-client-runtime.md).
- Slice 6's signed manifest, Schannel/`SslStream`, separate TLS ports, outer
  framing, deadlines, bounded handshakes/queues, and development-certificate
  workflow are implemented but disabled and uninstalled; see
  [`network-infrastructure-phase2-secure-transport.md`](network-infrastructure-phase2-secure-transport.md).
- Slice 7's bounded PBKDF2 authentication, atomic plaintext migration,
  grant-before-redirect lease, hash-only single-use tickets, accepted game
  bind/principal, and offline native grant registry/bind I/O are implemented.
  They remain disabled/uninstalled.
- Slice 8 source/offline work is complete: signed Login/Game routing and secure
  sessions are exported behind guarded monotonic activation and exact restore.
  Controlled-host acceptance proved original-client TLS authentication and
  world entry, then restored the stock client and temporary trust exactly; see
  the [acceptance record](network-infrastructure-controlled-host-acceptance.md).
- Slice 9 source/offline work is complete; see its
  [overview](network-infrastructure-phase3-slice9c-protected-udp.md).

Exit gate: legacy parity, TLS authentication/control, ticket forgery/expiry/
replay tests, frame boundary tests, and authenticated TLS-only fallback pass.

### Phase 3 — authenticated UDP binding without gameplay

Records: [9A](network-infrastructure-phase3-slice9a-udp-foundation.md),
[9B](network-infrastructure-phase3-slice9b-authenticated-binding.md), canonical
[wire](network-infrastructure-phase3-slice9c-protected-datagrams.md),
[completion ADR](network-infrastructure-phase3-slice9c-protected-udp.md), and
[runtime](network-infrastructure-phase3-slice9-runtime.md).

- Completed: TLS binding, AES-GCM, replay/key epochs, authenticated rebinding,
  native keepalive/pacing/fallback, bounded runtime, and telemetry.
- Verified locally: managed `121/121`; native Release `/W4 /WX` plus five
  offline passes; and a two-second-capped loopback run (`16,000` attempted,
  `14,000` accepted, `2,000` rejected; latest `5.094 ms`). Limiter usage/caps:
  global `10000/10000`, unvalidated `6000/6000`, proof `2000/2000`,
  protected-candidate `2000/2000`, authenticated-session `128/128`. This is
  not a production or DDoS-capacity claim.
- Checked-in UDP remains disabled after rollback. Controlled-host evidence
  proves authenticated UDP endpoint binding through the original client.
  Phase 4 owns live gameplay migration.

Exit gate: offline/loopback security, MTU, bounds, rebinding, emulation,
admission, and TLS-fallback checks are covered. Staggered maximum-capacity key
rotation remains Phase 5 production scalability work.

### Phase 4 — first hybrid authoritative slice

- Offline implementation and verification are complete: protected UDP
  movement/snapshots, TLS fallback, fixed-step authority, bounded queues,
  dedupe, correction, and world-transition protection. See the
  [Phase 4 record](network-infrastructure-phase4-authoritative-movement.md).
- Release is warning-free; managed `131/131`, native `/W4 /WX`, reproducible
  clean builds, and five offline passes are green. Controlled-host TLS, UDP
  binding, and world entry passed; defaults were restored off. The bounded
  Docker reference client also passes TLS login, ticket redemption,
  authenticated UDP binding, world entry, movement, and snapshot
  acknowledgement against the live container.

Exit gate: accepted for the local controlled host. Campaign
`0a73fd79-961b-42c7-82cc-9e4a6f9e3355` passed original-client Baseline,
forced Fallback, and `661.5843391`-second Soak profiles on one fixed build,
then restored the exact stock client. Protected completion receipt
`completion-0a73fd79-961b-42c7-82cc-9e4a6f9e3355.json` has SHA-256
`5EB6E369...F4A6F`; viewer parity was recorded `Unavailable`. V3 through V5
remain rejected, restored history.

### Phase 5 — extension, hardening, and operations

- Extend realtime traffic only with explicit recovery semantics.
- Phase 5A established fixed-step replay, bounded targetless load/soak,
  reproducible local baselines, metrics, a documented alert policy, and runbooks
  ([record](network-infrastructure-phase5a-replay-load-observability.md)).
- Keep the deferred map/AOI work behind Phase 4 and Phase 5A.
- Produce provider-neutral deployment controls until hosting is selected.
- Integrate an upstream TCP/arbitrary-UDP protection provider only after
  approval.

Exit gate: security, overload/recovery, observability, local benchmark, rollback,
and incident-response gates pass. Local results are not production guarantees.

## Known limitations and unresolved decisions

- The shim sees absolute positions, not keyboard intent. True input-level
  prediction may require a later `Origin.exe` hook.
- Phase 4 AOI writes remain bounded but can delay fixed-step movement; a later
  slice must move ordered effects behind a bounded single-owner queue.
- Client signing/distribution remain undecided.
- Production manifest signing/trust, blank-account reset policy, and upstream
  controls remain activation gates.
- Original-client Phase 4 movement/fallback/soak and rollback pass. Viewer
  parity remains unmeasured because a second client was unavailable.
- Hosting region/provider, expected concurrency, tick/snapshot rates, latency
  target, packet-loss target, and budget remain open capacity inputs.
- Provider-specific infrastructure and paid deployment require approval.
- The implemented Phase 3 application-layer cryptographic construction has its
  repository ADR and tests, but still requires independent security review
  before production activation.

## Version history

The version ledger is maintained separately in
[`network-infrastructure-history.md`](network-infrastructure-history.md) so
this design reference stays below the repository size limit.
