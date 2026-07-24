# Phase 2 bounded network runtime

## Status and scope

Phase 2 slice 4 is implemented on the existing raw development endpoints.
It adds bounded lifecycle controls before the secure listener exists; it does
not enable TLS, UDP, the native client bridge, or a production endpoint.

The raw protocol remains a pull-based stream. A handler reads at most one
legacy packet of `8196` bytes at a time, so adding a second buffered ingress
queue now would duplicate data without improving isolation. The configured
ingress and control-queue limits are reserved for the framed secure transport
in later slices.

## Runtime ownership

- Login and game listeners share one `ConnectionAdmission` budget.
- Accepted sockets reserve global active, global unauthenticated, per-IP, and
  IPv4 `/24` or IPv6 `/64` unauthenticated capacity.
- Successful legacy login marks the reservation authenticated. This releases
  only unauthenticated/address/prefix capacity; the active reservation remains
  until disconnect.
- Every admitted connection has one registered task. Shutdown stops accepts,
  cancels and disconnects active sessions, then waits up to the configured
  drain deadline.
- Each session owns one item-and-byte bounded reliable egress FIFO. A slow
  session cannot occupy another session's queue.
- Producers waiting for FIFO admission have a separate item-and-byte
  reservation bound; payloads are validated and reserved before copying.
- `ClientSession.SendAsync` still completes only after the physical transport
  write succeeds. Reliable data is never dropped or reported successful while
  queued.
- Cipher advancement occurs in dequeue/write order. A write timeout,
  cancellation after admission, or queue-admission deadline terminates that
  session because continuing a rolling cipher stream would be unsafe.

## Absolute deadlines

Partial bytes do not reset a deadline:

- accept to first legacy packet byte: `10 s`;
- later idle wait to the next packet byte: `90 s`;
- remaining packet header: `5 s`;
- complete packet body: `10 s`;
- reliable queue admission: `2 s`;
- physical reliable write: `5 s`; and
- graceful endpoint drain: `5 s`.

The legacy EOF behavior is intentionally unchanged, including the original
null result when EOF occurs immediately after a complete decrypted header.

## Configuration

`appsettings.json` and `appsettings.docker.json` contain a `network` object.
Startup rejects zero, negative, inconsistent, or excessively large limits.
Raw ingress and reliable egress byte capacities must each fit at least one
maximum `8196`-byte legacy packet.

| Setting | Default |
| --- | ---: |
| `listenBacklog` | `512` |
| `maxActiveConnections` | `512` |
| `maxConcurrentTlsHandshakes` | `64` (reserved) |
| `maxUnauthenticatedConnections` | `128` |
| `maxUnauthenticatedConnectionsPerIp` | `4` |
| `maxUnauthenticatedConnectionsPerPrefix` | `32` |
| `ingressQueueItems` / `ingressQueueBytes` | `128` / `524288` (reserved) |
| `reliableEgressQueueItems` / `reliableEgressQueueBytes` | `128` / `524288` |
| `reliableEgressPendingItems` / `reliableEgressPendingBytes` | `512` / `2097152` |
| `controlQueueItems` / `controlQueueBytes` | `32` / `65536` (reserved) |
| `queueAdmissionTimeoutMilliseconds` | `2000` |
| `firstPacketTimeoutMilliseconds` | `10000` |
| `packetHeaderTimeoutMilliseconds` | `5000` |
| `packetBodyTimeoutMilliseconds` | `10000` |
| `reliableWriteTimeoutMilliseconds` | `5000` |
| `idleTimeoutMilliseconds` | `90000` |
| `gracefulDrainTimeoutMilliseconds` | `5000` |

The TLS-handshake limit is validated now but cannot be consumed before the TLS
listener is added.

## Metrics

The meter is `Godswar.Server.Networking`. It reports connection
accepted/rejected/active counts, tracked tasks, timeout stages, reliable queue
items/bytes/overflow, transport bytes, drain outcomes, and disconnect reasons.
Tags are closed enums for endpoint, direction, rejection, timeout, drain, and
disconnect reason.

No metric API accepts an IP address, prefix, username, account/session ID,
opcode, payload, or attacker-controlled string. Existing raw-path diagnostic
logs are not presented as production-safe secure-path logging; structured
secure-path logging remains a later slice.

## Verification

Run the focused slice:

```powershell
dotnet run --project tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj --configuration Release -- "Secure Phase 2 bounded network lifecycle"
```

The checks cover both admission dimensions, IPv4/IPv6 prefix accounting,
authentication transitions, concurrent release, dual queue bounds, FIFO
backpressure, cancellation and completion, physical-write completion,
independent slow sessions, absolute read/write deadlines, finite metric tags,
tracked loopback connections, and bounded shutdown. Slice 3 byte/cipher parity
remains a mandatory regression gate.

## Remaining Phase 2 work

Slice 5's uninstalled native coordinator and bounded client pumps are complete;
see
[`network-infrastructure-phase2-client-runtime.md`](network-infrastructure-phase2-client-runtime.md).
Slice 6 adds Schannel/`SslStream` and consumes the reserved handshake, ingress,
and control limits. Slice 7 replaces permissive legacy account upsert with
password verification and single-use game tickets. Until those slices pass,
raw `5999/7000` is a local development protocol and is not secure.
