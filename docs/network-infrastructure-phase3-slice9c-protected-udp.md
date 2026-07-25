# Phase 3 Slice 9C protected UDP control channel

Document revision: 1.1

Wire revision: 1.0

Status: implemented and verified offline/loopback; checked-in activation
remains disabled, no client shim is installed, and gameplay remains on TLS

This is the overall Slice 9 ADR and completion overview. The
[protected-datagram document](network-infrastructure-phase3-slice9c-protected-datagrams.md)
is the canonical wire specification, and the
[runtime document](network-infrastructure-phase3-slice9-runtime.md) is the
runtime, admission, and lifecycle reference.

## Decision

Slice 9C completes the secure TCP-to-UDP session foundation selected in the
main network ADR. The game continues to use TLS for all legacy gameplay and
control messages. An optional native worker binds one UDP endpoint to the
authenticated game-TLS session and exchanges only encrypted binding
confirmation and keepalive messages.

The selected construction is:

- TLS 1.3-capable Schannel for authentication, tickets, grants, and all
  existing gameplay;
- the exact Slice 9A/9B stateless cookie and TLS proof exchange for UDP
  return-path validation;
- AES-256-GCM protected UDP datagrams;
- SHA-256 HKDF traffic-key derivation through platform cryptography;
- independent client-to-server and server-to-client keys;
- monotonic key epochs and packet sequences; and
- a bounded 128-packet replay window.

This deliberately preserves raw UDP plus TLS rather than silently substituting
QUIC. QUIC streams plus DATAGRAM would provide a mature integrated transport,
but it would add a large native x86 dependency and replace the requested
TCP/UDP architecture. DTLS was also considered. Its secure record layer is
attractive, but introducing and safely shipping a compatible DTLS stack inside
the recovered x86 client is materially more complex than the three
fixed-message control slice. A mature game-networking library has the same ABI,
binary-size, and integration risks and would not remove the legacy protocol.

The custom part is limited to fixed framing, state machines, and key
separation. AES-GCM, SHA-256, random generation, and TLS use the .NET and
Windows platform cryptographic implementations. No gameplay reliability layer
or general-purpose transport is built over UDP.

## Scope and non-scope

Implemented scope:

- one protected UDP session owned by one authenticated game-TLS lease;
- encrypted `Ping`, `Pong`, and `BindingConfirm` messages;
- replay rejection, duplicate suppression, reordering tolerance, key rotation,
  and bounded previous-epoch overlap;
- authenticated endpoint rebinding through a fresh stateless challenge and TLS
  proof without resetting traffic keys or sequences;
- a single bounded native worker with keepalive, pacing, handshake deadlines,
  and TLS-only fallback;
- guarded server runtime activation, bounded admission, cleanup, metrics, and
  deterministic shutdown; and
- offline managed/native golden vectors, adversarial checks, loopback checks,
  and bounded network-emulation checks.

Not implemented in Slice 9C:

- movement, combat, spells, snapshots, or any other gameplay over UDP;
- client prediction, interpolation, reconciliation, lag compensation, or
  interest-management snapshot encoding;
- reliable gameplay events over UDP;
- production deployment or installation into the live game client;
- public listener exposure, provider infrastructure, or a production capacity
  claim; and
- upstream L3/L4 DDoS scrubbing.

Gameplay remains on the TLS bridge if UDP is blocked, unavailable, malformed,
timed out, or not granted.

## Protected datagram format

All integers use network byte order. A protected datagram is an exact 64-byte
authenticated header, ciphertext, and a 16-byte AES-GCM tag. Its total size is
between 80 and 1,200 bytes. Plaintext is therefore capped at 1,120 bytes, and
IP fragmentation is never required.

| Offset | Size | Field |
| ---: | ---: | --- |
| `0` | `4` | Magic `GWSP` / `0x47575350` |
| `4` | `2` | Header bytes, exactly `64` |
| `6` | `1` | Protocol major, `1` |
| `7` | `1` | Protocol minor, `0` |
| `8` | `1` | Packet type, `1=Protected` |
| `9` | `1` | Flags, zero |
| `10` | `2` | Exact total datagram bytes |
| `12` | `16` | Nonzero opaque TLS connection ID |
| `28` | `4` | Nonzero traffic-key epoch |
| `32` | `8` | Packet sequence |
| `40` | `4` | ACK key epoch |
| `44` | `8` | ACK high-water sequence |
| `52` | `8` | ACK mask for the preceding 64 sequences |
| `60` | `1` | Protected message type |
| `61` | `1` | Reserved, zero |
| `62` | `2` | Exact plaintext/ciphertext bytes |
| `64` | variable | Ciphertext |
| end - `16` | `16` | AES-256-GCM authentication tag |

ACK epoch zero requires both ACK sequence and mask to be zero. Otherwise, the
ACK sequence acknowledges itself and mask bit `N` acknowledges
`ackSequence-(N+1)`. Bits that would underflow sequence zero are invalid.
ACKs are authenticated metadata, but Slice 9C does not use them to introduce a
retransmission protocol.

The complete 64-byte header is AES-GCM additional authenticated data. The
12-byte nonce is:

```text
keyEpochBE32 || sequenceBE64
```

Epoch zero is invalid. Each direction starts at epoch 1 and sequence 0.
Sequences do not wrap. A key epoch increments before resetting its sequence to
zero. Epoch wrap is terminal for UDP and does not downgrade or interrupt TLS.

## Traffic keys

The 32-byte proof key delivered inside the authenticated TLS grant is the
input keying material for the lifetime of that game-TLS lease. It is not
persisted or logged.

```text
salt = connectionId16 || serverIdBE32
info = ASCII("GWSU-PROTECTED-DATAGRAM-V1")
       || directionByte
       || keyEpochBE32
key  = HKDF-SHA256(ikm=proofKey32, salt=salt, info=info, length=32)
```

The ASCII domain has no terminating NUL. Direction `1` is client-to-server and
direction `2` is server-to-client. This gives different keys for direction,
server, connection, and epoch.

Canonical client-to-server Ping vector:

```text
IKM:
000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F
connection ID:
101112131415161718191A1B1C1D1E1F
server ID: 01020304
epoch: 00000001
sequence: 0000000000000000
derived key:
C27A8E9BF928AE027A3915F49E942F9273CE975F27CD775CC2E7ED894A00D5FA
nonce:
000000010000000000000000
plaintext:
000000000000000100000000075BCD15
full datagram:
475753500040010001000060101112131415161718191A1B1C1D1E1F00000001000000000000000000000000000000000000000000000000000000000100001036486AB35FD8E6650AB613A49B881EDD7D174FF3A7946AA12C991108036242C6
```

Managed and native checks must both reproduce this exact vector.

## Protected messages

Only these wire types exist:

| Type | Direction | Payload |
| ---: | --- | --- |
| `1` | client to server | `pingIdBE64 || senderMonotonicMsBE64` |
| `2` | server to client | exact Ping payload, then `serverReceiveUnixMsBE64 || serverSendUnixMsBE64` |
| `3` | server to client | exact client nonce from the challenge, then `bindingRevisionBE64 || serverUnixMsBE64` |

Ping IDs, binding revisions, and server timestamps must be nonzero. Client
monotonic time is untrusted and is only echoed. A server session rejects
anything except Ping inbound. A client session rejects anything except Pong
or BindingConfirm inbound. Direction or payload failures do not commit replay
state, promote a key epoch, refresh liveness, or change binding state.

Pong is tied byte-for-byte to the outstanding Ping. BindingConfirm is tied
byte-for-byte to the fresh client nonce. These rules prevent unrelated valid
control packets from advancing the wrong state machine.

## Replay and rotation

Each receive epoch has a fixed 128-packet replay bitmap. The receiver:

1. performs strict bounded structural parsing;
2. rejects a packet outside the current replay window;
3. authenticates and decrypts it;
4. validates message direction and exact payload semantics; and only then
5. commits the sequence and any epoch promotion.

The receiver accepts its current epoch, its immediately previous epoch during
a bounded overlap, or exactly `current+1`. Skipped, zero, wrapped, or expired
epochs are rejected. A successfully authenticated next-epoch packet promotes
that epoch; failed authentication never does.

The sender rotates on a bounded packet count or maximum epoch age. Rotation
uses a fresh derived traffic key and resets only that direction's sequence.
NAT rebinding never resets an epoch, sequence, or replay window.

## Binding and NAT rebinding

Initial binding retains Slice 9B's exact flow:

1. TLS authenticates the game session and delivers a short-lived grant.
2. The client sends a 128-byte stateless Hello with a fresh nonce.
3. The server returns a 128-byte stateless address-cookie challenge.
4. The client returns a 128-byte authenticated proof combining that cookie
   with possession of the TLS-delivered proof key.
5. The server binds the observed canonical endpoint and returns an encrypted
   112-byte BindingConfirm.

The prevalidation response is never larger than its request. BindingConfirm is
sent only after cookie and TLS proof validation and is also smaller than the
proof request.

Endpoint changes require the complete flow again with a fresh nonce, challenge,
cookie, and TLS proof. A successful fresh endpoint change increments the
binding revision. The current endpoint may repeat its current proof
idempotently. A bounded recent-proof fingerprint history prevents a captured
old proof from moving the session back to a prior endpoint. A minimum endpoint
change interval bounds churn.

After a rebind, protected packets from the old endpoint are dropped. The new
endpoint continues the same cryptographic session, so replaying an old
protected packet cannot become valid merely because the address changed.

The TLS lease owns the authority entry. TLS close, grant expiry before initial
binding, bound-idle expiry, or runtime shutdown removes the entry and zeros all
proof and traffic keys.

## Admission, amplification, and bounded work

The listener has hard global, unauthenticated, binding-proof,
protected-candidate, prefix, and validated-session packet budgets. Protected
candidates default to 512 packets/s globally and 256 packets/s per IPv4 `/24`
or IPv6 `/64`. Every limiter map is capacity bounded and reset on a bounded
cadence.

Unauthenticated Hello/malformed traffic cannot consume the reserved
established-session budget. Binding-proof candidates use a distinct bounded
budget so a Hello flood cannot starve all valid proofs. A strict protected
header enters the separate candidate budget; a guessed connection ID or
endpoint cannot consume established-session priority. Only successful AEAD and
current-binding checks make the packet eligible for established priority or
liveness refresh.

The server:

- allocates no session state before return-path validation;
- performs no database, decompression, simulation, or asymmetric-crypto work
  in the UDP ingress path;
- silently drops malformed, stale, unauthenticated, replayed, and
  rate-limited datagrams;
- uses fixed maximum buffers and exact field limits;
- responds to unauthenticated traffic at no more than a 1:1 byte ratio;
- emits only low-cardinality metrics; and
- never logs packets, proof keys, cookies, connection IDs, endpoints, or
  player identifiers.

These controls resist application-level spoofing, amplification, parsing, and
state-exhaustion attacks. They cannot absorb a volumetric packet-per-second or
bandwidth flood. Production still requires an upstream provider explicitly
supporting arbitrary protected TCP and UDP game ports, with the origin
restricted to that trusted path.

## Native worker and TLS fallback

The x86 shim creates at most one UDP worker for an authenticated game session
and never for the login session. The worker:

- takes single ownership of the grant and clears it after initialization;
- uses the successfully connected TLS peer address with the granted UDP port;
- owns one nonblocking connected UDP socket and one cancellable thread;
- caps receive draining, buffers, retries, send cadence, and stop time;
- sends a keepalive approximately every five seconds;
- measures authenticated Pong round-trip behavior;
- attempts a fresh bounded rebind after peer timeout or network/socket change;
  and
- clears all keying material on stop or failure.

The worker exposes no gameplay API and changes no stock `Net.dll` ABI. Failure
to allocate, resolve an endpoint, open a socket, bind, authenticate, receive a
Pong, or rebind transitions only the UDP worker to TLS fallback. The existing
TLS bridge remains connected and continues carrying every game message.

Checked-in UDP and secure-network settings remain disabled. Source completion
allows an explicit secure-profile operator setting to start the listener, but
this change does not install the native shim, trust a certificate, open a
public listener, alter firewall/antivirus settings, or deploy infrastructure.

## Verification and acceptance

Closeout verification covered:

- managed and native reproduction of the canonical vector;
- encode/decode round trips and byte-order boundaries;
- truncated, oversized, malformed, random, and tag/header tampering;
- duplicate, reordered, stale, and 128-window boundary sequences;
- current, previous, next, skipped, expired, and exhausted epochs;
- wrong-direction and invalid-payload packets with no state mutation;
- initial binding, idempotent proof, fresh rebind, stale-proof rollback
  rejection, old-endpoint rejection, and preserved sequences across rebind;
- global, prefix, proof, and authenticated-session admission exhaustion;
- Hello-flood resistance for valid proofs and established sessions;
- loopback authentication, binding confirmation, Ping/Pong, cancellation, and
  TLS survival when UDP is blocked;
- bounded loss, duplication, reordering, jitter, latency, and MTU emulation;
- bounded loopback-only load/soak results with environment and limitations;
- Release managed build and full protocol checks;
- native Win32 Release build with warnings treated as errors and the complete
  offline suite; and
- formatting, JSON/XML parsing, line-ending, file-size, secret, and diff
  hygiene.

The full managed protocol suite passed `121/121`. The Win32 Release native
build passed with `/W4 /WX`, followed by five consecutive complete native
offline passes. The loopback-only admission baseline was hard-capped at 16,000
attempts and two seconds. The latest `5.094 ms` run accepted 14,000 and
rejected 2,000. Reported limiter usage/caps were global `10000/10000`,
unvalidated `6000/6000`, proof `2000/2000`, protected-candidate `2000/2000`,
and authenticated-session `128/128`; all maps remained bounded. The
environment was Windows 11 Pro build 26200 x64, an AMD Ryzen 9 9950X3D, about
64 GiB RAM, and .NET SDK `10.0.100-rc.2.25502.107`.

These results are a reproducible local baseline only, not production-capacity
or DDoS-capacity guarantees. No live/non-loopback listener was tested, no
client shim was installed, checked-in UDP remains disabled, and gameplay
migration is outside Slice 9. Cleanup and rotation are bounded by the default
1,024-session capacity; staggered maximum-capacity rotation remains future
production scalability work.
