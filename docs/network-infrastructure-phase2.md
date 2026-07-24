# Secure reliable channel Phase 2 specification

## Status

- Specification version: `1.1`
- Last updated: `2026-07-24`
- Runtime status: not implemented and not enabled
- Predecessor status: blocked pending avatar-preview loading-gate V2 live
  acceptance
- Stable installed rollback shim SHA-256:
  `528913E66888D5C070C39949D2FC1AE439B8414B15152312D4E093A29D17A6DD`
- Stable Apply backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-151248244`
- Current V2 candidate SHA-256 (not installed):
  `73E65FBFA3EA9809AF597DA3D25D1E0963B0A4A467549191BAFB4FAE9F2902FD`

Loading-gate V1
`2D819908BEE2FA7D8BE4957E18358DEFFB5FD65D01AC26D6F73F29F4C71E2AE0`
failed the 2026-07-24 live account-switch gate and was rolled back. It is
historical failed evidence, not a rollback target. This document is the
reviewed contract for the next networking milestone. It does not claim that
the current raw protocol is secure, and it does not authorize enabling UDP.

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
legacy `CMsg`. The pending V2 loading gate is the one narrow ownership
exception. It delegates native `Process()` every frame, may hold exactly one
audited opcode-`10002` native pointer while avatar resources load, and prevents
polling past it so queue order is preserved. It returns that exact pointer on
readiness or after a guarded five-second fallback. The fallback does not
dispose the pointer or invoke native disconnect, and it may still produce a
blank preview if resources never become ready. A pointer still held is
destroyed only on an explicit `Connect`, `DisConnect`, `Release`, or proxy
destruction lifecycle reset. TLS supplies confidentiality and integrity; the
XOR is retained only to minimize compatibility risk.

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
- The stable installed pass-through proxy preserves the nine-slot ABI and
  delegates traffic unchanged. The uninstalled V2 candidate adds only the
  documented preview loading gate, so the bridge can still be added without
  patching `Origin.exe` after that candidate passes live acceptance.

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

1. Accept the avatar-preview loading-gate V2 candidate manually; capture exact
   game `SetHost` host/port and stock `GetStatus` transitions without logging
   credentials or payloads.
2. Add pure preface/frame/grant codecs, golden vectors, boundary tests, and
   fuzz entry points. Nothing listens on a new port.
3. Extract `ILegacyByteTransport`; prove the existing raw protocol tests and
   captured streams remain byte-identical.
4. Add bounded admission, tracked connection tasks, queues, deadlines, and
   metrics to the transport layer while keeping secure listeners disabled.
5. Add the native loopback coordinator/pumps and lifecycle tests in an
   uninstalled test build.
6. Add Schannel/`SslStream`, ALPN, certificate validation, and local test-CA
   automation. Secure ports remain opt-in development endpoints.
7. Add password hashing/migration plus opaque ticket grant/bind. Remove
   username-only authority from the secure game path.
8. Run automated negative/fuzz/slow-client/load gates, install through a new
   guarded backup, perform original-client parity, then disable raw external
   access in the secure profile.

## Verification contract

### Required loading-gate V2 manual acceptance

Phase 2 remains blocked while the V2 candidate is uninstalled and lacks a live
acceptance record. After installing it through a guarded backup:

1. Record the starting stable shim hash and backup, the installed candidate
   hash, and the exact restoration path.
2. Start the existing server and run the candidate client normally.
3. Log in to account 7, enter the world, move/map-transition, fight, use a
   skill, manipulate inventory/equipment, and exercise one NPC/forge action.
4. Fully exit `Origin.exe`, log in to account 13, and repeat.
5. Alternate accounts for five complete close/relaunch cycles.
6. Confirm a late selection preview remains responsive and renders when avatar
   readiness is observed. Exercise the guarded five-second fallback separately:
   it must return the exact pointer without gate-initiated disposal or
   disconnect, but does not guarantee a model if resources never become ready.
7. Confirm no new dump, crash, or error-log entry.
8. Restore the stable pass-through shim, verify its recorded hash, and repeat
   one world entry.

Failure of the intended loading behavior, or any other unintended behavioral
difference, fails the gate and restores
`C:\Reborn\backups\client-network-shim-v1-Apply-20260724-151248244`.

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
- Native lifecycle tests: native `Process()` delegation on every frame, exact
  opcode-`10002` pointer retention, preserved queue order, exact-pointer return
  on readiness and at the guarded five-second fallback, no fallback disposal
  or disconnect, and exact-once cleanup only on `Connect`, `DisConnect`,
  `Release`, or destruction. Also cover repeated/failed connect, double
  disconnect, release without disconnect, concurrent grant claim, ticket
  zeroization, no use-after-free, and stock-compatible `CMsg` allocation.
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

- Phase 2 installation first backs up the exact installed stable rollback
  `Net.dll`, `NetLegacy.dll`, endpoint manifest, and hashes.
- Restore must be artifact-independent, idempotent, interruption-recoverable,
  and return the shim to
  `528913E66888D5C070C39949D2FC1AE439B8414B15152312D4E093A29D17A6DD`.
- Restore must never select the historical failed loading-gate V1 hash
  `2D819908BEE2FA7D8BE4957E18358DEFFB5FD65D01AC26D6F73F29F4C71E2AE0`.
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
closed without raw downgrade, and can be restored exactly to the stable shim.
It remains blocked until loading-gate V2 has a successful live acceptance
record.

Still required before implementation:

- Avatar-preview loading-gate V2 live acceptance record; the failed V1 record
  does not satisfy this requirement.
- Observed game `SetHost` string/port and stock `GetStatus` transitions.
- Account password audit and an explicit reset plan for blank credentials.
- Development CA, signed endpoint-manifest key custody, and certificate
  rotation procedure.
- Capacity inputs before changing conservative limits or claiming scale.
