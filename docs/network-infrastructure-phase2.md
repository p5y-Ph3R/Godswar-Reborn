# Secure reliable channel Phase 2 specification

## Status

- Specification version: `1.10`
- Last updated: `2026-07-25`
- Runtime status: Slice 6 signed endpoint validation, native Schannel/framing
  primitives, and opt-in bounded server `SslStream` listeners are implemented;
  the candidate remains uninstalled and disabled, secure game bind is
  fail-closed, and UDP is absent
- Current next milestone: Slice 7 password migration and single-use game tickets
- Predecessor status: V4 final smoke is sealed `Fail`; ordered rollback is
  complete; Phase 1 remains unaccepted and the avatar issue is parked
- Rejected V3 SHA-256:
  `17A7219868BAC19BA2BDDD2949FCF70884D4FD9F3EC5799455EF944F40D878D1`
- V3 failure evidence:
  `artifacts/network-shim/manual-parity/20260724T043833399Z-2bd75dd7`
- Rejected V4 Origin SHA-256:
  `E0F5BC951C6E37550F4D9CC1E25BFDCB4F020466ADD854DC2E7EA04E0D22F81C`
- Rejected V4 Net SHA-256:
  `EF531F8CB20A4FCA8D1DBA979FD131ECA002383AE862890435426DF948817597`
- V4 evidence/result:
  `artifacts/network-shim/manual-parity/20260724T095739213Z-db16daa7` /
  `Fail`
- Current predecessor Origin:
  `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`
- Current stock Net:
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`
- Current `NetLegacy.dll`: absent

Exact V1/V2 and pass-through recovery hashes/backups remain in the
[Phase 1 runbook](network-infrastructure-phase1.md).

Loading-gate V1
`2D819908BEE2FA7D8BE4957E18358DEFFB5FD65D01AC26D6F73F29F4C71E2AE0`
failed by starving native processing. V2 failed when its timed unready handoff
recreated the blank model. V3 still reached the roughly 15-second native
timeout and `0x005F58BC` null-root crash. All three are historical evidence,
not rollback targets.
This document does not claim that the current raw protocol is secure or
authorize enabling UDP.

## Decision

Phase 2 adds separate TLS login and game listeners and an in-process loopback
bridge to the x86 `Net.dll` shim. The verified stock `NetLegacy.dll` remains
responsible for:

- the proprietary nine-slot client ABI;
- `CMsg` allocation and parsing;
- legacy little-endian framing; and
- the continuous rolling XOR stream.

The bridge carries the exact XOR-protected legacy byte stream as opaque
`LegacyBytes` frames inside TLS. It does not decrypt, parse, or construct a
legacy `CMsg`. Phase 2 starts from predecessor Origin plus stock Net and
preserves stock native-message ownership; the rejected V4 preview gate is not
part of this phase. TLS supplies confidentiality and integrity; XOR remains
only to minimize compatibility risk.

This deliberately chooses compatibility over removing redundant obfuscation in
the first secure phase. A later ADR may remove XOR from secure connections only
after byte-for-byte parity is proven.

Phase 2 also replaces username-only game admission with a short-lived opaque
ticket issued by the authenticated login connection. UDP, movement migration,
prediction, reconciliation, and realtime packet semantics remain Phase 3/4
work.

## Why this seam is necessary

Current repository evidence:

- `TcpEndpointServer` accepts indefinitely and starts one untracked task per
  socket without admission limits.
- `ClientSession` is tied directly to `TcpClient`/`NetworkStream`, has no
  read/write deadlines, and uses an inbound legacy packet ceiling of `8196`.
- `PacketCipher` advances one continuous 256-byte XOR pointer across arbitrary
  read and write boundaries.
- Some outbound calls are packet batches or arbitrary stream chunks. An outer
  TLS frame therefore cannot assume that it contains exactly one legacy packet.
- Login currently upserts credentials rather than authenticating them.
- Both stores overwrite an existing password, and game opcode `10000` calls the
  same operation with an empty password.
- The rejected V4 experiment preserved the nine-slot ABI, but its AfterLogin,
  preview-gate, and Origin lifecycle hooks were rolled back and are not part of
  the future bridge.

The secure transport must therefore expose an ordered byte stream to the
existing `ClientSession`, not replace gameplay handlers or reinterpret packet
boundaries.

## Trust boundaries

```text
untrusted Origin.exe
        |
        | proprietary calls and CMsg pointers
        v
Net.dll proxy -> verified NetLegacy.dll -> 127.0.0.1 ephemeral socket
     |                                      |
     +-------- process-local coordinator ---+
                    |
                    | Schannel TLS, framed opaque legacy bytes
                    v
             secure server listener
                    |
                    v
 bounded transport -> ClientSession -> existing handlers
                         |
                         +-> authenticated principal from game ticket
```

- The client process is untrusted even when its executable hash is recognized.
- A TLS connection authenticates the server and protects transport data; it
  does not authorize inventory, combat, movement, or progression outcomes.
- A game username, IP address, or source port is never an identity proof.
- Ticket bytes, passwords, raw payloads, and cryptographic material never enter
  logs, metrics, or persistent client configuration.
- Blocking persistence and password work remain outside the simulation tick.

## Wire protocol and client lifecycle

The exact TLS policy, signed endpoint configuration, byte layouts, ticket
state, redirect ordering, and proxy lifecycle are maintained in
[`network-infrastructure-phase2-protocol.md`](network-infrastructure-phase2-protocol.md).

The normative summary is:

- Schannel on the x86 shim and .NET `SslStream` on the server.
- TLS 1.2/1.3 with forward-secret AEAD, ALPN `godswar-shim/1`, normal DNS
  certificate validation, and no raw fallback.
- Separate local ports `6599`/`7443`; future UDP `7444` is reserved but absent.
- A 72-byte client preface, 40-byte server preface, and bounded 16-byte frame
  header in network byte order.
- Implemented codecs use caller-owned buffers, tri-state incremental parsing,
  exact consumed counts, role/direction checks, and disposable secret controls;
  decoded grants remain syntax-only until signed policy validates them.
- Opaque legacy XOR stream chunks remain byte-identical inside TLS.
- An authenticated grant is committed before the legacy redirect is exposed.
- A 60-second, server-stored, hashed, single-use ticket binds the game channel
  before any legacy game data or state allocation.
- The process coordinator classifies exact `SetHost` routes rather than factory
  order and erases ticket bytes on every terminal path.

## Server seams and ownership

Introduce small focused types rather than growing existing files:

- `ILegacyByteTransport`: ordered read/write/disconnect/endpoint facade.
- `RawTcpLegacyTransport`: byte-identical current path.
- `TlsMuxLegacyTransport`: TLS frame parser exposing only the opaque legacy
  stream to `ClientSession`.
- `SecureFrameCodec`: allocation-bounded pure codec.
- `ISecureControlChannel`: grant/bind operations invisible to legacy handlers.
- `IGameTicketStore`: bounded issue/atomic-consume/expiry operations.
- `IConnectionAdmission`: global, prefix, IP, and handshake admission.
- `TimeProvider`: deterministic deadlines and ticket tests.

`ClientSession.SendAsync` keeps its current Phase 2 meaning: completion means
the reliable bytes were physically written or failed. A later simulation
egress phase may add nonblocking publication without silently changing existing
handler behavior.

Track every accepted connection task. Graceful shutdown stops acceptance,
cancels handshakes, and drains active tasks within a fixed deadline.

## Authentication migration

Authentication is not an upsert.

- Split storage operations into find, explicit create, authenticate, mark
  login, and mark logout.
- Use a versioned PBKDF2-HMAC-SHA256 record with a random 16-byte salt, 32-byte
  result, constant-time comparison, and an initial 600,000 iterations.
- Benchmark the KDF on deployment hardware and raise, never silently lower, the
  configured cost. Bound KDF concurrency to
  `min(Environment.ProcessorCount, 16)`.
- Existing nonempty plaintext passwords may be verified once and migrated
  atomically. Failed authentication never mutates an account.
- Empty-password accounts require an explicit administrative reset. This is
  important because the current game-login path may already have overwritten
  stored passwords with an empty string.
- Self-registration is an explicit policy, off in production. A temporary
  enrollment mode is permitted only on a private local development listener.
- The game principal comes from the consumed ticket. Legacy opcode `10000`
  username is compatibility data and must match that principal.
- Duplicate-login replacement occurs only after ticket attachment succeeds.
- Do not invent a password pepper until a managed secret/HSM boundary and a
  tested rotation/recovery procedure exist.

Back up and audit the account table before credential migration. Schema changes
are additive and tested against both JSON and PostgreSQL stores. Successfully
migrated plaintext is not retained merely to make rollback easier; old and new
server binaries used during rollback must both understand the versioned hash.

The global KDF admission queue is independently bounded to 64 requests and
8 KiB of copied credential bytes. Admission waits at most 250 ms. The complete
authenticate operation, measured from receipt of the full login packet through
its finite result, has a five-second wall-clock deadline including queue wait,
account lookup, KDF, and persistence. Cancellation removes a queued job and
zeros all copied credentials. A missing account runs the same bounded KDF
against a fixed versioned dummy verifier before returning the same externally
observable failure as a wrong password. Stored work factors outside configured
safe bounds are rejected without attacker-selected KDF work.

## Bounds and deadlines

Initial configurable safety defaults:

| Resource | Default |
| --- | ---: |
| Listen backlog per secure endpoint | `512` |
| Active secure connections | `512` |
| Concurrent TLS handshakes | `64` |
| Unauthenticated connections globally | `128` |
| Unauthenticated connections per IP | `4` |
| Unauthenticated connections per IPv4 `/24` or IPv6 `/64` | `32` |
| Server ingress queue | `128` items and `512 KiB` |
| Server reliable egress queue | `128` items and `512 KiB` |
| Reliable egress pending reservations | `512` items and `2 MiB` |
| Server control queue | `32` items and `64 KiB` |
| Client queue per direction | `128` items and `512 KiB` |
| Ticket registry | `1024` records |
| KDF admission queue | `64` requests and `8 KiB` |
| Outer payload / largest Phase 2 control | `16384` / `408` bytes |
| Decrypted legacy packet | `8196` bytes |

Fixed-size expiring limiter maps must also have explicit capacities. Established
authenticated sessions receive priority over new handshakes during overload.
No reliable queue uses drop-oldest. Queue exhaustion waits at most two seconds
on the server, then disconnects the slow/overloaded session.

| Stage | Absolute deadline |
| --- | ---: |
| Accepted TCP to TLS completion | `5 s` |
| TLS completion to full preface | `2 s` |
| Game preface to ticket bind | `5 s` |
| First login credentials packet | `10 s` |
| KDF queue admission | `250 ms` |
| Complete frame header/body | `5 s` / `10 s` |
| Complete authentication operation | `5 s`, including queue and KDF |
| Individual secure write | `5 s` |
| Heartbeat / authenticated idle | `30 s` / `90 s` |
| Graceful drain | `5 s` |

Receiving one byte does not indefinitely reset an absolute deadline.
The native Schannel revocation exception remains a documented activation
blocker.

## Logging, metrics, and failure behavior

Replace raw packet hex, username, and remote-endpoint console output on the
secure path with sampled structured events. One coordinated `FailOnce` closes
all legs and records one finite reason.

Low-cardinality metrics include active/accepted/rejected connections, pending
handshakes, handshake duration, preface outcomes, frames/bytes, malformed
reason, queue items/bytes, queue overflow, authentication outcome/duration,
plaintext migrations, ticket issue/redeem/expire/replay, heartbeat timeout, and
disconnect reason.

Never use IP, prefix, username, account ID, connection/ticket ID, hostname,
opcode, payload content, or attacker-provided text as a metric label. Never log
credentials, ticket/cookie/key bytes, or raw packet payloads.

## Implementation slices

Each slice is a separate reversible checkpoint. Format, build, test, and fix
failures before continuing.

1. Completed: pin the rolled-back predecessor client. Exact game `SetHost`
   host/port and stock `GetStatus` capture remains a parked/open evidence item;
   it is not a blocker for pure-codec work.
2. Completed: pure preface/frame/grant/bind codecs, golden vectors, boundary
   and fuzz checks. No listener or runtime transport was added.
3. Completed: extracted `ILegacyByteTransport` and `RawTcpLegacyTransport`.
   Framing, rolling XOR state, handler dispatch, and serialized send semantics
   remain in `ClientSession`. A fixed 300-byte synthetic golden frozen before
   refactoring, a captured-clear 2772-byte game bootstrap with fixed raw hash,
   forced boundaries, handler dispatch, loopback, and the existing 512-send
   test prove raw-stream parity. Full credential-bearing capture replay remains
   an open final Phase 2 gate and is not committed.
4. Completed: bounded shared admission, tracked connection tasks, per-session
   reliable egress, absolute raw-stream deadlines, graceful drain, and finite
   metrics. Secure listeners remain disabled. Runtime details:
   [`network-infrastructure-phase2-runtime.md`](network-infrastructure-phase2-runtime.md).
5. Completed: uninstalled native route coordinator, ephemeral-loopback bridge,
   bounded queues/pumps, WinSocket adapter, and lifecycle tests. Details:
   [`network-infrastructure-phase2-client-runtime.md`](network-infrastructure-phase2-client-runtime.md).
6. Completed: Schannel/`SslStream`, exact ALPN/cipher checks, signed endpoint
   policy, bounded outer framing, separate opt-in TLS ports, and guarded
   development-CA automation. Details:
   [`network-infrastructure-phase2-secure-transport.md`](network-infrastructure-phase2-secure-transport.md).
7. Current: add password hashing/migration plus opaque ticket grant/bind. Remove
   username-only authority from the secure game path.
8. Run automated negative/fuzz/slow-client/load gates, install through a new
   guarded backup, perform original-client parity, then disable raw external
   access in the secure profile.

Focused slice-4 check:

```powershell
dotnet run --project tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj --configuration Release -- "Secure Phase 2 bounded network lifecycle"
```

## Verification contract

### Final loading-gate V4 decision

Evidence `20260724T095739213Z-db16daa7` is sealed `Fail`. Origin PID `64928`
established redirected TCP to `127.1.1.110:7000`, but the server received no
`LoginGameServer`; CharacterSelection, AfterLogin, and V4 preload never ran.
No new dump appeared. This does not prove the preload path caused the stall,
but it fails the agreed acceptance branch.

The mandatory Net-first rollback completed. Current state is predecessor
Origin `753BE49F...9ED79`, stock Net `1CC3F9AA...BCA00C`, and no
`NetLegacy.dll`. Phase 1 remains unaccepted. Do not implement another avatar
iteration; proceed with Phase 2. Pass-through `528913...D17A6DD` remains a
separate recovery candidate if later needed.

### Automated Phase 2 gates

- Golden vectors for every preface/frame/control offset and byte order.
- Every truncation and split/coalescing boundary; zero/maximum/oversized lengths,
  unknown types/flags/versions, sequence errors, abrupt EOF, and random input.
- Opaque legacy streaming across every chunk boundary and 256-byte XOR wrap,
  producing byte-identical `GamePacket` values and Origin-visible output.
- TLS 1.2/1.3 policy, ALPN, wrong role, plaintext/XOR on the secure port,
  untrusted/expired/not-yet-valid/wrong-SAN certificate, revocation, trickle,
  timeout, and no-downgrade tests.
- Password corruption/work-factor bounds, wrong/empty password, enumeration
  parity, concurrent migration, and proof that failure cannot mutate data.
- Ticket forgery, bit flip, expiry boundary, wrong scope/build/instance/server,
  double consume, concurrent replay, reissue/logout invalidation, capacity,
  cleanup, and restart.
- Queue item/byte limits, stalled readers/writers, buffer return, reliable
  ordering, graceful overload/recovery, and proof one slow client cannot block
  another.
- Codec ownership tests: caller-owned buffers, coalesced remainder, no generic
  frame allocation, secret zeroization, disposal refusal, nonzero IDs, canonical
  rejection fields, role/direction rejection, and syntax-only grant handling.
- End-to-end login, grant-before-redirect ordering, game bind, world entry,
  account 7/13 switching, map/gameplay actions, clean shutdown, and long soak.
- Packet capture/ETW proof that external credentials and game bytes are TLS,
  the only legacy XOR socket is `127.0.0.1`, and secure failure never contacts
  raw `5999/7000`.
- Capped loopback load tests reporting environment, CPU, memory, allocations,
  handles, queue depth, bytes/frames per second, and recovery time. Results are
  local baselines, never production guarantees.

Every external decoder is a pure bounded state-machine/fuzz entry point.
Arbitrary bytes may return a finite rejection but may not cause unbounded
allocation, work, logging, an uncaught exception, or a process crash.

## Rollback

- Phase 2 begins from the pinned rolled-back predecessor: Origin
  `753BE49F...9ED79`, stock Net `1CC3F9AA...BCA00C`, no `NetLegacy.dll`.
  The completed V4 rollback does not imply Phase 1 acceptance.
- Restore must be artifact-independent, idempotent, interruption-recoverable,
  and return the client to the exact predecessor selected by that branch.
- Pass-through `528913...D17A6DD` remains an avatar-failure recovery
  candidate; it is not the future Phase 2 direct rollback target.
- Restore must never select the historical failed loading-gate V1 hash
  `2D819908BEE2FA7D8BE4957E18358DEFFB5FD65D01AC26D6F73F29F4C71E2AE0`.
- Restore must also never select rejected V2
  `73E65FBFA3EA9809AF597DA3D25D1E0963B0A4A467549191BAFB4FAE9F2902FD`.
- `NetLegacy.dll` remains at
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`.
- Server listener/config changes remain feature-gated. Restoring Phase 1
  re-enables only the explicitly private legacy-development profile.
- Database migrations are additive. Backups and migration tests precede any
  credential mutation; rollback never requires restoring a plaintext password.
- No production endpoint, paid infrastructure, certificate secret, or live
  firewall rule is changed without separate approval.

## Exit criteria and open evidence

Phase 2 is accepted only when the original client authenticates over TLS,
receives and stores a grant before redirect, binds the game connection with a
single-use ticket, enters the world, completes the parity/soak matrix, fails
closed without raw downgrade, and can be restored exactly to the pinned
pre-Phase-2 client state. Runtime implementation may now begin; V4 and Phase 1
acceptance are not claimed.

Still required during the transport implementation slices:

- Parked/open, non-blocking for slice 4: observed game `SetHost`
  string/port and stock `GetStatus` transitions.
- Account password audit and an explicit reset plan for blank credentials.
- Development CA, signed endpoint-manifest key custody, and certificate
  rotation procedure.
- Capacity inputs before changing conservative limits or claiming scale.
