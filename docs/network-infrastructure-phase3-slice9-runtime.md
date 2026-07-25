# Phase 3 Slice 9 guarded UDP runtime

## Status

Slice 9 now has an opt-in server runtime for authenticated protected UDP
control traffic. Both checked-in profiles keep `Secure.Udp.Enabled=false`.
No live listener was started or deployed while implementing this slice; socket
verification binds only an ephemeral loopback port.

The [protected-datagram document](network-infrastructure-phase3-slice9c-protected-datagrams.md)
is the canonical wire specification, while the
[protected-UDP ADR](network-infrastructure-phase3-slice9c-protected-udp.md)
records the overall Slice 9 decision and closeout. This document is the
runtime, admission, and lifecycle reference. No client shim was installed, and
gameplay remains on TLS.

Enabling UDP requires secure TLS and all compiled capability gates:

- protected AES-GCM datagrams and replay/key-epoch handling;
- the native UDP worker;
- loopback end-to-end verification; and
- verified TLS-only fallback.

The TLS `UdpBindingGrant` is the client capability signal. When UDP is
disabled, its bounded authority is full, or the native socket cannot operate,
the game session remains on TLS. UDP failure does not reject an otherwise
valid TLS game bind.

## Runtime ownership and ordering

`SecureUdpRuntime` owns, in order:

1. the TLS-authenticated session authority and protected session state;
2. rotating stateless cookie material and address validation;
3. the three-class bounded admission controller;
4. the UDP endpoint and its single receive loop; and
5. periodic idle cleanup and protected send-key rotation.

Startup binds UDP and publishes readiness before either TLS TCP listener can
accept a game session and issue a grant. Endpoint and maintenance tasks are
tracked explicitly; the runtime no longer assumes that the final two tasks are
the TCP listeners. Any listener or cleanup-loop fault cancels the shared host
lifetime.

Shutdown first cancels the receive and cleanup loops. TCP transports then
release their authority leases. Runtime disposal finally zeroes proof secrets,
traffic keys, cookie secrets, replay state, and session maps.

## Datagram dispatch

Ingress is separated into finite admission classes:

| Class | Cheap classification | Default budget |
| --- | --- | ---: |
| Unvalidated | Hello or malformed datagram | 3,072 packets/s |
| Binding proof | Structurally exact type-4 proof | 512 packets/s |
| Protected candidate | Structurally valid protected header before AEAD | 512 packets/s global; 256 packets/s/prefix |
| Established | AEAD-authenticated packet for the current binding | reserved priority; 256 packets/s/session |

The global limit is 4,096 packets/s. Unvalidated, binding-proof, and protected
candidate paths use distinct bounded IPv4 `/24` or IPv6 `/64` maps. A Hello
flood cannot consume the proof-prefix counter. Merely guessing a connection ID
or endpoint cannot consume established-session priority: a protected candidate
must pass AEAD and current-binding checks before it is trusted, admitted as
established, or allowed to refresh activity.

A forged type-4 flood can compete with real proofs inside the finite proof
budget. This application bound is not upstream arbitrary-UDP DDoS scrubbing.

## Binding, rebinding, and liveness

A successful initial type-4 proof starts binding revision `1`. The server
immediately returns an encrypted `BindingConfirm` containing the exact
client nonce, revision, and server Unix time.

A different endpoint cannot send protected traffic directly. It must perform a
fresh Hello/challenge/type-4 exchange. The authority enforces a two-second
minimum endpoint-change interval and retains 16 accepted proof fingerprints.
A proof issued before the next allowed rebind time or a previously accepted
proof cannot roll the endpoint back. A successful rebind increments the
revision without replacing the protected session, so key epochs, send
sequences, receive replay windows, and acknowledgements continue.

The client sends encrypted 16-byte `Ping` messages. The server authenticates
and replay-checks them, refreshes activity, and returns a 32-byte encrypted
`Pong` containing the echo plus server receive/send times. Bound sessions idle
for 30 seconds are removed; gameplay remains on TLS. Cleanup runs every five
seconds. Send keys rotate after 1,000,000 packets or 300 seconds, retaining the
previous receive epoch for ten seconds.
Outbound control sends do not refresh inbound liveness.

## Configuration

All settings have environment-variable equivalents documented in
`.env.example`.

| Setting | Default |
| --- | ---: |
| `GlobalPacketsPerSecond` | 4,096 |
| `UnvalidatedPacketsPerSecond` | 3,072 |
| `BindingProofPacketsPerSecond` | 512 |
| `ProtectedCandidatePacketsPerSecond` | 512 |
| `ProtectedCandidatePrefixPacketsPerSecond` | 256 |
| `AuthenticatedSessionPacketsPerSecond` | 256 |
| `SessionCapacity` | 1,024 |
| `KeepAliveIntervalSeconds` | 5 |
| `BoundSessionIdleTimeoutSeconds` | 30 |
| `SessionCleanupIntervalSeconds` | 5 |
| `MinimumRebindIntervalMilliseconds` | 2,000 |
| `PreviousKeyEpochOverlapSeconds` | 10 |
| `KeyRotationSeconds` | 300 |
| `KeyRotationPacketLimit` | 1,000,000 |

Runtime health snapshots expose only bounded state: lifecycle state, local
endpoint, pending/bound session counts, admission counters, and a failure type.
Metrics use finite outcome tags for challenges, binds, rebinds, proof/replay
rejections, protected Pong delivery, rate limits, lifecycle, expiry, and key
rotation. IDs, endpoints, accounts, packet bodies, and secrets are not labels.

The default `SessionCapacity` of 1,024 bounds cleanup and rotation work.
Staggered rotation at maximum configured capacity remains a future production
scalability item; the local admission baseline does not validate that workload.

## Verification

```powershell
dotnet build .\GodswarServer.sln --configuration Release

dotnet run `
  --project .\tests\Godswar.Server.ProtocolChecks\Godswar.Server.ProtocolChecks.csproj `
  --configuration Release -- `
  "Secure Phase 2 TLS mux transport" `
  "Secure Phase 3 UDP"

.\tools\RunSecureUdpLoopbackBaseline.ps1
```

Coverage includes checked-in disablement, incomplete capability rejection,
listener readiness and deterministic shutdown, grant-capacity TLS fallback,
independent admission reserves, encrypted confirmation/Ping/Pong, idle expiry,
fresh NAT rebinding, rollback replay rejection, protected sequence continuity,
key epochs, and bounded state.

The local in-process loopback baseline is hard-capped at 16,000 attempts and
two seconds. The latest run took `5.094 ms`: 16,000 attempted, 14,000
accepted, and 2,000 rejected. Reported limiter usage/caps were global
`10000/10000`, unvalidated `6000/6000`, proof `2000/2000`,
protected-candidate `2000/2000`, and authenticated-session `128/128`; all maps
remained bounded. The host was Windows 11 Pro `10.0.26200` x64, an AMD Ryzen 9
9950X3D (16 cores/32 logical processors), about 64 GiB RAM, and .NET SDK
`10.0.100-rc.2.25502.107`.

Slice 9 closeout also passed the full managed protocol suite (`121/121`), a
Win32 Release native build with `/W4 /WX`, and five consecutive native offline
passes. All results are local/offline observations, not production capacity or
DDoS-mitigation claims.

Protected gameplay movement/snapshots remain a later migration step. Reliable
inventory, account, chat, and administrative operations remain on TLS.
