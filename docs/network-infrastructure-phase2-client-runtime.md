# Phase 2 Slice 5 native client runtime

## Status and scope

Slice 5 implements the bounded native coordinator and opaque loopback bridge
for the Win32/x86 `Net.dll` candidate. It is an **uninstalled test build**.
Nothing in this slice changes the installed client, server listeners, database,
firewall, or live network path.

The process policy compiled into the exported candidate is deliberately
disabled. It classifies every valid legacy route as explicit `PassThrough`, so
the proxy continues to call the pinned stock client directly. The `Login` and
`Game` bridge decisions exist for injected tests and the Slice 6 handoff, but
the default process policy cannot produce either decision.

Slice 6 now supplies the signed-manifest loader, bounded external connector,
Schannel stream, secure outer-frame stream, and matching server transport as
independently tested candidate components. They are not wired into the
exported process policy or installed client.

Slice 7 adds offline-only native `GameGrant` decoding, signed-policy
validation, a fixed one-grant registry, exact-route claim/presentation, secure
`GameBind` writing, and `BindResult` handling. These primitives are compiled
and tested, but the exported classifier still returns only `PassThrough`.
Accordingly, the checked-in server must remain in its default raw-only profile:
enabling its mutually exclusive secure profile would suppress raw `5999/7000`
before this uninstalled client can select TLS routes.

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

Slice 5 does not authorize running the historical installer or copying its
candidate into the game directory. The generated `bin` and `obj` files are
test artifacts only.

## Runtime ownership

### Legacy ABI proxy

The external binary contract remains two named exports with their original
ordinals and a nine-slot Microsoft x86 C++ client interface. The shim does not
add a virtual destructor or change slot order.

`NetClientCreate` still obtains one client from the hash-verified stock
`NetLegacy.dll`. The proxy owns that stock pointer until `Release`:

1. creation registers a unique nonzero proxy ID;
2. allocation or registration failure releases the stock client;
3. `SetHost` records the bounded route before delegating;
4. `Connect` begins a generation-bound plan;
5. default `PassThrough` calls stock `Connect` and marks the plan connected;
6. connection failure or `DisConnect` resets coordinator state;
7. `Release` unregisters before forwarding stock `Release` and deleting the
   proxy.

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

The default process callback returns only `PassThrough`. A null injected
callback also resolves to that disabled policy. An unknown callback result is
normalized to `Reject`. The coordinator stores no password, ticket, grant,
certificate, key, or other secret.

## Dormant loopback bridge

`NativeClientBridge` is compiled into the candidate and tested independently,
but the disabled proxy does not activate it. It accepts:

- the already-created stock `ILegacyNetClient`; and
- an already-established caller-owned `IByteStream` outer leg.

The bridge itself does not resolve a host, dial an external server, perform
TLS, or validate an endpoint. Slice 6 supplies separate signed-manifest,
connector, Schannel, and outer-frame primitives that can create that stream,
but exported-proxy wiring remains disabled until Slice 8. Both supplied
objects must remain alive until `StopAndJoin` completes.

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
reads and writes. Slice 5 did not add a transport write deadline; Slice 6's
dormant secure stream supplies finite read/write deadlines.

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
  complete/cancel, byte transfer, and repeated lifecycle cleanup;
- exact socket read/write/EOF, bounded nonblocking I/O, pump compatibility, and
  shutdown unblocking;
- complete bridge startup ordering, bidirectional bytes, stock-connect
  failure, expired startup, invalid queue configuration, spontaneous pump
  termination, join-timeout recovery, overlapping Start/Stop, two stop owners,
  exact disconnect ownership, argument rejection, and repeated teardown;
- injected Login/Game/Reject proxy routes proving dormant secure plans never
  invoke raw stock `Connect`, including invalid-route stale-state rejection;
- grant decoding and field bounds, manifest-scoped host/audience/server policy,
  pending/claimed/presented transitions, expiry, generation invalidation,
  route mismatch, return-before-presentation, and secret erasure;
- first-frame game bind encoding, accepted/rejected result handling, sequence
  transition to `2`, timeout/malformed/wrong-phase failures, and proof that a
  presented ticket is never returned or reused;
- existing export ordinals, x86 ABI delegation, hardening flags,
  deterministic clean-build hashes, exact legacy-DLL verification, and
  missing/tampered legacy rejection.

These are automated candidate tests, not an installed original-client smoke.
The full `TestClientNetworkShim.ps1` wrapper includes socket checks and is
reserved for a controlled test host; it is not the local offline gate.

`Godswar.NetShim.Checks.exe --offline` omits every WinSock listener,
connection, DNS, and Schannel socket-handshake check while retaining manifest,
protocol, queue, pump, coordinator, and secure-stream state-machine coverage.
Use the complete socket suite only on a controlled test host.

## Rollback

There is no live rollback action because the candidate is not installed or
enabled:

- leave `C:\Godswar Origin\Net.dll` at the exact stock hash above;
- leave `NetLegacy.dll` absent;
- discard ignored `client\network-shim\bin` and `obj` outputs if desired;
- revert the Slice 6 source checkpoint to remove the candidate implementation.

If a candidate is copied into the game directory outside this runbook, that is
an unapproved state. Fully close Origin and restore the exact stock files using
the existing guarded Phase 1 recovery evidence; do not improvise a mixed
`Net.dll`/`NetLegacy.dll` pair.

## Explicit exclusions and limitations

The cumulative Slice 5-7 candidate still provides no:

- exported-process policy that can select a secure Login or Game route;
- production manifest keys/floors, signed production manifest, hardened
  installed floor, or approved installer state;
- installed/trusted development or production certificate state;
- controlled-host socket, original-client end-to-end, or live-database
  migration acceptance;
- UDP socket, capability, encryption, replay window, or NAT rebinding; or
- live endpoint activation, client installation, or production-readiness
  claim.

The native bridge cannot be used securely by itself: its outer stream is an
injected interface, not proof of TLS or identity. The conservative registry
and queue values are safety bounds, not player-capacity claims. The current
listener is IPv4 loopback only. Client runtime observability is limited to
finite state snapshots and test assertions; activation must poll terminal pump
state, and production-safe structured telemetry remains required. The
synchronous stock `Connect` call is not preemptible, and the nine-slot client
object remains single-owner with exclusive final `Release`.

## Slice 7 result and activation handoff

Slices 6-7 preserve these ownership and bound contracts and provide:

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
   and never reuses a presented ticket.

Slice 8 is the conservative next milestone: reviewed production manifest
keys/floors, validated route wiring through the exported proxy, a guarded
install/rollback checkpoint, live-account backup/reset rehearsal, and
controlled-host client/server socket and original-client smoke tests. Selecting
a secure route must fail closed; the current `PassThrough` policy is the
disabled baseline, not a fallback after secure failure. Activation must also
verify that secure mode starts no raw compatibility listener. UDP remains
absent until the later authenticated session-binding phase.
