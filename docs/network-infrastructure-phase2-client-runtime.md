# Phase 2 native client runtime and Slice 8 activation

## Status and scope

Slices 5-8 implement the bounded native coordinator, signed activation runtime,
Schannel session, and opaque loopback bridge for the Win32/x86 `Net.dll`
candidate. It remains an **uninstalled test build**. No installed client,
server listener, database, trust store, firewall, or live network path changed.

The exported process policy now has two explicit states. Missing activation or
`ActivationMode=0` selects raw `PassThrough`. `SecureRequired` loads one signed
module-relative manifest, selects the exact Login route, admits Game only for a
matching pending authenticated grant, and rejects everything else. Once a
secure route is selected, failure never downgrades to raw TCP.

The client session wires the Slice 6 connector, Schannel and outer framing to
Slice 7 grant/bind state. The server's mutually exclusive secure profile must
still remain disabled until controlled-host Slice 8 acceptance because the
candidate is not installed.

This document complements the parent
[Phase 2 design](network-infrastructure-phase2.md), the
[wire and lifecycle specification](network-infrastructure-phase2-protocol.md),
and the [bounded server runtime](network-infrastructure-phase2-runtime.md).

## Installed-client boundary

The accepted rollback state remains:

- `Origin.exe` SHA-256:
  `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`;
- installed stock `Net.dll` SHA-256:
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`;
- installed `NetLegacy.dll`: absent.

Slice 8 does not authorize an ad-hoc copy into the game directory. Activation
must use the guarded [Slice 8 runbook](network-infrastructure-phase2-slice8-activation.md);
generated `bin` and `obj` files remain test artifacts.

## Runtime ownership

### Legacy ABI proxy

The external binary contract remains two named exports with their original
ordinals and a nine-slot Microsoft x86 C++ client interface. The shim does not
add a virtual destructor or change slot order.

`NetClientCreate` still obtains one client from the hash-verified stock
`NetLegacy.dll`. The proxy owns that stock pointer until `Release`:

1. creation registers a unique nonzero proxy ID;
2. allocation or registration failure releases the stock client;
3. `SetHost` records and classifies the bounded route; only explicit
   `PassThrough` reaches the stock client;
4. `Connect` begins a generation-bound plan;
5. `PassThrough` calls stock `Connect`; Login/Game creates a fail-closed secure
   session and gives stock only an ephemeral loopback endpoint;
6. `Process` polls secure bridge state and resets a terminated session;
7. failure or `DisConnect` stops the session and resets coordinator state; and
8. `Release` joins secure work, unregisters, forwards stock `Release`, and
   deletes the proxy.

`Process`, `GetStatus`, `PickMsg`, `SendMsg`, and `GetMsgNum` retain their stock
delegation and native message ownership. The rejected avatar-loading gate is
disabled in the exported Slice 5 proxy. `NetServiceCreate` remains a direct
stock factory call and is not owned by the coordinator.

No threads, sockets, module loads, or waits are started from `DllMain`.
The proprietary object retains Origin's observed single-owner call contract:
`Release` is the exclusive final call and may not race another virtual method.

### Process-local coordinator

`NativeClientCoordinator` is a fixed-capacity, process-local registry:

- capacity: `64` simultaneously registered proxies;
- proxy IDs: monotonically assigned, unique, nonzero, and not reused when a
  slot is freed;
- host: copied immediately, `1..253` bytes plus a terminating NUL;
- port: nonzero;
- comparison: exact host bytes and exact port, with no case folding, DNS
  lookup, suffix guessing, or factory-order inference;
- decisions: finite `PassThrough`, `Login`, `Game`, or `Reject`;
- states: registered, classifying, host-ready, connecting, and connected.

Classification is injected as a no-throw callback and runs outside the
registry lock. The coordinator revalidates the proxy ID and generation before
committing its result. Reset, unregister, and a newer `SetHost` invalidate an
older connection plan, preventing delayed work from completing against a
reused registry slot.

The process callback comes from the one-shot `SecureClientRuntime`. Disabled
returns `PassThrough`; a ready secure runtime returns Login/Game/Reject under
the signed policy; initialization failure returns `Reject`. A null injected
test callback remains disabled and unknown results normalize to `Reject`. The
coordinator itself stores no password, ticket, grant, certificate, or key.

## Secure loopback bridge

`SecureClientSession` now activates `NativeClientBridge` for exported Login and
Game routes. The bridge accepts:

- the already-created stock `ILegacyNetClient`; and
- an already-established caller-owned `IByteStream` outer leg.

The bridge itself still does not resolve, dial, perform TLS, or validate an
endpoint. `SecureClientSession` owns those steps and supplies the established
outer stream. Both supplied objects remain alive until `StopAndJoin` completes.

The tested startup order is:

1. open an IPv4 listener on literal `127.0.0.1` and ephemeral port `0`;
2. arm one cancellable accept worker;
3. call stock `SetHost("127.0.0.1", ephemeralPort)`;
4. call stock `Connect`;
5. require one loopback peer and refuse startup success after the absolute
   deadline;
6. close the listener and detach its event-selection state;
7. wrap that socket in the bounded nonblocking byte-stream adapter;
8. start the two opaque pumps.

The listener uses `SO_EXCLUSIVEADDRUSE`, backlog `1`, a nonblocking
event-driven accept, and a separate stop event. The stream adapter uses
nonblocking `recv`/`send` with 100 ms readiness polls; `Stop` shuts down once
without closing the descriptor until all workers join. Cancellation therefore
cannot reuse a numeric socket beneath pending I/O. Join ownership is serialized,
and completion/cancellation use one `GetTickCount64` deadline.

The stock ABI's synchronous `Connect` cannot be preempted. If it returns after
the deadline, the bridge refuses success and performs cleanup; the deadline
does not make that proprietary call return earlier. Startup failure owns
cleanup: it stops the outer stream, cancels/joins accept, and issues exactly one
stock `DisConnect` if stock `Connect` was attempted. After successful startup,
the owner stops/joins the bridge and then issues the stock disconnect.

Failure states and accept reasons are closed enums. A stop timeout reports
`JoinPending`, never `Running`, and retains ownership for a later join retry.
A polled snapshot maps terminal pump EOF/read/write/queue outcomes to
`PumpTerminated`/`JoinPending`. Activation code must monitor that state when
the bridge is wired into the process coordinator. Destructors use a bounded
join and fail fast rather than detach a worker or wait forever.

## Opaque byte pumps

The bridge never parses, decrypts, encrypts, compresses, or reframes legacy
bytes. Stock `NetLegacy.dll` retains the continuous rolling XOR state,
little-endian packet framing, and `CMsg` construction.

Each direction owns one reader, one writer, and one single-producer/
single-consumer queue. Defaults and hard maxima are:

| Limit | Value |
| --- | ---: |
| Queue items per direction | `128` |
| Queue bytes per direction | `524288` |
| Read or queued chunk | `16384` bytes |
| Producer admission wait | `250 ms` |
| Pump workers per bridge | `4` |

Both item and byte capacity must be available before a copy is admitted.
Admission uses one absolute deadline. There is no unbounded producer/consumer
waiter list; an unexpected second producer or consumer is rejected. Every
writer loops until the admitted chunk is completely written, preserving FIFO
order across partial socket writes.

EOF, read failure, zero/invalid write progress, allocation failure, queue
failure, or admission timeout is terminal. One `FailOnce` winner cancels both
queues and calls `Stop` on both streams so blocked I/O can exit. Reliable data
is never silently skipped or reordered; data still queued when a terminal
connection failure occurs is securely discarded as part of teardown.

`IByteStream::Stop` must be idempotent, thread-safe, and unblock all current
reads and writes. Slice 5 did not add a transport write deadline; the secure
stream used by Slice 8 supplies finite read/write deadlines.

## Verification

From `C:\Reborn`:

```powershell
.\tools\BuildClientNetworkShim.ps1 -Configuration Release

& .\client\network-shim\bin\Release\Win32\Godswar.NetShim.Checks.exe --offline
```

The build is pinned to MSVC v143 `14.44.35207`, Windows SDK
`10.0.26100.0`, and `Win32`. It uses warnings as errors, SDL checks, no C++
exceptions or RTTI, static runtime linkage, ASLR, NX, Control Flow Guard, and
the existing `0x50000000` preferred image base.

The Slice 5 checks cover:

- route null/empty/unterminated/overlong bounds and immediate copy ownership;
- exact host-and-port classification, unknown decision rejection, generation
  invalidation, capacity, concurrent registration, and non-reused IDs;
- queue item/byte bounds, FIFO, absolute timeout, allocation failure,
  producer/consumer wake-up, cancellation, and waiter bounds;
- two-way opaque parity, partial writes, EOF, read/write/zero-write failure,
  queue overflow, blocked-stop behavior, worker-start failure, and isolation
  between a stalled and healthy pump;
- WinSock one-time concurrent initialization, loopback-only acceptance,
  absolute accept timeout, concurrent open ownership, concurrent
  complete/cancel, reverse-tuple Origin-PID ownership, bounded foreign-peer
  rejection, byte transfer, and repeated lifecycle cleanup;
- exact socket read/write/EOF, bounded nonblocking I/O, pump compatibility, and
  shutdown unblocking;
- complete bridge startup ordering, bidirectional bytes, stock-connect
  failure, expired startup, invalid queue configuration, spontaneous pump
  termination, join-timeout recovery, overlapping Start/Stop, two stop owners,
  exact disconnect ownership, argument rejection, and repeated teardown;
- exported Disabled/SecureRequired/failed-closed route policy, one-shot
  manifest/runtime initialization, exact Login and grant-gated Game selection,
  and proof that secure/rejected logical routes never reach stock `SetHost`;
- secure Login/Game session construction, target validation, Schannel/preface/
  bind-before-loopback ordering, bridge polling, and coordinated teardown;
- grant decoding and field bounds, manifest-scoped host/audience/server policy,
  pending/claimed/presented transitions, expiry, generation invalidation,
  route mismatch, return-before-presentation, and secret erasure;
- first-frame game bind encoding, accepted/rejected result handling, sequence
  transition to `2`, timeout/malformed/wrong-phase failures, and proof that a
  presented ticket is never returned or reused;
- current/next candidate `.gwkey` build-contract parsing and candidate-bound
  signed-manifest probing;
- existing export ordinals, x86 ABI delegation, hardening flags, deterministic
  clean-build hashes, and handle-held legacy-DLL verification including
  missing, tampered, replacement, and reparse rejection.

These are automated candidate tests, not an installed original-client smoke.
The full `TestClientNetworkShim.ps1` wrapper includes socket checks and is
reserved for a controlled test host; it is not the local offline gate.

`Godswar.NetShim.Checks.exe --offline` omits every WinSock listener,
connection, DNS, and Schannel socket-handshake check while retaining manifest,
protocol, queue, pump, coordinator, and secure-stream state-machine coverage.
Use the complete socket suite only on a controlled test host.

## Rollback

There is no current live rollback action because the candidate is uninstalled.
Future activation uses the guarded bundle transaction, not an ad-hoc copy:

- leave `C:\Godswar Origin\Net.dll` at the exact stock hash above;
- leave `NetLegacy.dll` absent;
- leave `RebornNetwork.gwem` and the 64-bit activation key absent; and
- retain ignored build outputs only as test artifacts.

Guarded Apply backs up the exact predecessor, advances the sequence floor while
disabled, installs and verifies all files, then commits `SecureRequired`.
Restore disables first, retains the monotonic floor, and reproduces the exact
receipt-bound files. Full sequencing and interruption recovery are in the
[Slice 8 runbook](network-infrastructure-phase2-slice8-activation.md).

## Explicit exclusions and limitations

The cumulative Slice 5-8 candidate still provides no:

- production/staging manifest keys or signed operational manifest;
- installed/trusted development or production certificate state;
- controlled-host socket, original-client end-to-end, or live-database
  migration acceptance;
- UDP socket, capability, encryption, replay window, or NAT rebinding; or
- live endpoint activation, client installation, or production-readiness
  claim.

The bridge alone is not proof of TLS or identity; `SecureClientSession` must
construct its authenticated outer stream. Registry and queue values are safety
bounds, not player-capacity claims. The loopback listener is IPv4-only and
requires reverse-tuple ownership by the current Origin PID, but local flooding
can still deny one bounded startup attempt. Client observability remains finite
state snapshots and tests; production-safe telemetry remains required. The
synchronous stock `Connect` call is not preemptible, and the nine-slot client
object remains single-owner with exclusive final `Release`.

## Slice 8 result and activation handoff

Slices 6-8 preserve these ownership and bound contracts and provide:

1. strict signed endpoint-manifest parsing, ECDSA verification, sequence
   floors, validity checks, and one-shot module-relative loading;
2. bounded external TCP resolution/connect and Schannel with normal platform
   trust, exact DNS name, SNI, ALPN `godswar-shim/1`, and no raw downgrade;
3. bounded client preface and secure outer-frame state machines;
4. matching opt-in server `SslStream` listeners with absolute deadlines,
   bounded handshake/ingress/control work, heartbeat, and secure telemetry; and
5. guarded development-certificate generation and exact trust cleanup;
6. a noncopyable, zeroing game-grant value and fixed one-grant
   pending/claim/presentation registry; and
7. exact game-bind/result I/O that succeeds before opening the local game leg
   and never reuses a presented ticket;
8. exported fail-closed Login/Game routing and secure-session ownership; and
9. candidate-bound manifest probing plus guarded monotonic activation/restore.

Live account backup/reset rehearsal, operational keys/trust, controlled-host
socket tests, and original-client TLS world entry remain required. Slice 9B's
binding foundation passes offline; the remaining Slice 9 work protects
datagrams while gameplay stays on TLS.
