# Secure reliable channel Phase 2 specification

## Status

- Specification version: `1.1`
- Last updated: `2026-07-24`
- Runtime status: not implemented and not enabled
- Required predecessor: Phase 1 interactive parity and rollback acceptance
- Phase 1 installed shim SHA-256:
  `2D819908BEE2FA7D8BE4957E18358DEFFB5FD65D01AC26D6F73F29F4C71E2AE0`

The SHA-256 above is intentionally repeated from the Phase 1 runbook as the
rollback target for the next client build. This document is the reviewed
contract for the next networking milestone. It does not claim that the current
raw protocol is secure, and it does not authorize enabling UDP.

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
legacy `CMsg`. The existing Phase 1 loading gate is the one narrow ownership
exception: it may retain the exact one-character opcode-`10002` pointer while
avatar resources load and dispose an undelivered pointer through its stock
virtual scalar-deleting destructor on disconnect, reconnect, release, or
timeout. TLS supplies confidentiality and integrity; the XOR is retained only
to minimize compatibility risk.

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
- The Phase 1 proxy preserves the nine-slot ABI. It delegates ordinary traffic
  unchanged and applies only the documented preview loading gate, so the
  bridge can still be added without patching `Origin.exe`.

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

1. Accept Phase 1 manually; capture exact game `SetHost` host/port and stock
   `GetStatus` transitions without logging credentials or payloads.
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

### Current Phase 1 manual gate

Before Phase 2 code:

1. Start the existing server and run the installed client normally.
2. Log in to account 7, enter the world, move/map-transition, fight, use a
   skill, manipulate inventory/equipment, and exercise one NPC/forge action.
3. Fully exit `Origin.exe`, log in to account 13, and repeat.
4. Alternate accounts for five complete close/relaunch cycles.
5. Confirm a late selection preview remains responsively loading and its model
   appears automatically; no blank preview or relaunch is accepted.
6. Confirm no new dump, crash, or error-log entry.
7. Run the Phase 1 stock Restore smoke, reapply the shim, and repeat one world
   entry. Record the final backup and hashes in the Phase 1 runbook.

Failure of the intended loading behavior, or any other unintended behavioral
difference, fails the gate and restores the exact Phase 1 backup.

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
- Native lifecycle tests: repeated/failed connect, double disconnect, release
  without disconnect, concurrent grant claim, ticket zeroization, no
  use-after-free, stock-compatible `CMsg` allocation, and exact-once cleanup of
  the bounded preview-gate exception.
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

- Phase 2 installation first backs up the exact installed Phase 1 `Net.dll`,
  `NetLegacy.dll`, endpoint manifest, and hashes.
- Restore must be artifact-independent, idempotent, interruption-recoverable,
  and return the shim to the Phase 1 hash recorded at the top of this file.
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
closed without raw downgrade, and can be restored exactly to Phase 1.

Still required before implementation:

- Phase 1 interactive acceptance record.
- Observed game `SetHost` string/port and stock `GetStatus` transitions.
- Account password audit and an explicit reset plan for blank credentials.
- Development CA, signed endpoint-manifest key custody, and certificate
  rotation procedure.
- Capacity inputs before changing conservative limits or claiming scale.
