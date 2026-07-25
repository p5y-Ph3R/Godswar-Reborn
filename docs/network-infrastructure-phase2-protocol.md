# Phase 2 TLS wire protocol and client lifecycle

## Status and ownership

- Protocol version: `1.4`
- Last updated: `2026-07-25`
- Runtime status: Slice 6 TLS/preface/framing primitives and opt-in server
  listeners are implemented; they remain disabled and uninstalled, secure game
  bind rejects before the legacy handler until Slice 7 tickets, and UDP is absent
- Current predecessor Origin:
  `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`
- Current stock Net:
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`
- Current `NetLegacy.dll`: absent
- Parent phase:
  [`network-infrastructure-phase2.md`](network-infrastructure-phase2.md)

Exact V1–V4 rollback references remain in the
[Phase 1 runbook](network-infrastructure-phase1.md).

This is the normative TLS, framing, ticket, and x86 lifecycle contract. V1–V4
are rejected and rolled back; Phase 1 is unaccepted and the avatar issue parked.

## TLS policy

The x86 client uses Windows Schannel/SSPI. The .NET 10 server uses `SslStream`
with `SslServerAuthenticationOptions`.

- ALPN is exactly `godswar-shim/1`.
- Allow TLS 1.2 and TLS 1.3. Windows 10 Schannel does not provide TLS 1.3, so a
  TLS-1.3-only policy would conflict with the documented Windows 10/11 target.
- TLS 1.2 accepts only
  `TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256` or
  `TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384`. TLS 1.3 accepts only
  `TLS_AES_128_GCM_SHA256` or `TLS_AES_256_GCM_SHA384`.
- Use an RSA 2048-bit-or-stronger SHA-256 server certificate for broad Schannel
  compatibility.
- Disable renegotiation and require confidentiality, integrity, the expected
  ALPN, and an accepted negotiated suite before the application preface.
- Schannel receives the exact DNS certificate name as its target and performs
  normal platform-root, hostname, validity, and revocation checks.
- Development uses a deliberately installed development CA and short DNS names
  such as `login.reborn.test` and `game.reborn.test`.
- There is no accept-all callback, validation-ignore flag, writable thumbprint
  pin, or raw-TCP retry after any secure failure.

Production cipher availability is an operating-system policy as well as an
application concern. Startup validation and connection metrics report a finite
policy error when required suites are unavailable rather than weakening the
policy. The primary Phase 2 server profile is the checked-in Linux .NET
container with OpenSSL, where `CipherSuitesPolicy` is set to the four suites
above and every negotiated connection is checked. A Windows Server profile is
not production-supported until deployment applies an exact Schannel
Group/Local Policy and an automated preflight proves it; Windows
`SslStream` still validates the negotiated suite after the handshake.

## Endpoint configuration

The stock game configuration remains unchanged for Phase 1 rollback. Phase 2
uses a separate, versioned endpoint manifest containing:

- environment and format version;
- logical login host/port observed from `Origin.exe`;
- secure login DNS name and port;
- permitted game DNS suffixes, audiences, and server IDs;
- manifest validity interval; and
- minimum secure protocol version.

Production manifests use the bounded binary format below and are signed with
ECDSA P-256/SHA-256. A TLS-authenticated game grant must also match the
allowlist. A writable hostname plus writable SNI is not secure configuration.

Local defaults reserve:

| Purpose | Port |
| --- | ---: |
| Legacy development login TCP | `5999` |
| Legacy development game TCP | `7000` |
| Secure login TLS/TCP | `6599` |
| Secure game TLS/TCP | `7443` |
| Future authenticated UDP, absent in Phase 2 | `7444` |

TLS and legacy traffic always use separate listeners; the server never sniffs
both protocols on one port. Raw listeners require an explicit development
profile and loopback/private binding. Production startup fails if raw listeners
are enabled or if secure certificate/configuration material is absent.

The exact bounded format, signature, rollback protection, loader, and key
rotation contract is maintained separately in the
[signed endpoint-manifest specification](network-infrastructure-phase2-endpoint-manifest.md).

## Encoding rules

All outer integers use unsigned big-endian network order. Byte arrays are
copied exactly. Legacy bytes inside a `LegacyBytes` payload retain their current
XOR state and little-endian framing.

Unknown versions, roles, flags, sizes, reserved fields, payload bounds, or
endpoint-role/direction combinations fail closed. Incremental readers return
`NeedMore`, `Done`, or `Rejected` plus a consumed count; `Done` consumes exactly
one value and leaves any coalesced remainder, while the other states consume
zero. Sources and outputs are caller-owned buffers. There is no generic
owning/copying frame object. Control-ticket temporary/output bytes must be
cleared, and disposed grant/bind objects refuse secret access or encoding.

## Client preface

The first TLS application data sent by the shim is exactly 72 bytes:

| Offset | Size | Field | Version 1 rule |
| ---: | ---: | --- | --- |
| `0` | `4` | Magic | ASCII `GWSC` |
| `4` | `2` | Header size | `72` |
| `6` | `2` | Protocol major | `1` |
| `8` | `2` | Minimum minor | `0` |
| `10` | `2` | Maximum minor | `0` |
| `12` | `1` | Endpoint role | `1=login`, `2=game` |
| `13` | `1` | Flags | `0` |
| `14` | `2` | Reserved | `0` |
| `16` | `4` | Capabilities | `0`; UDP is unadvertised |
| `20` | `4` | Maximum receive payload | `16384` |
| `24` | `16` | Client-instance ID | Nonzero per-process CSPRNG bytes |
| `40` | `32` | `Origin.exe` SHA-256 | Compatibility evidence only |

The client-instance ID is generated outside `DllMain`, shared by the login and
game proxy objects in one process, never persisted, and never treated as proof
that the client is trustworthy.

## Server preface

The server replies with exactly 40 bytes:

| Offset | Size | Field | Version 1 rule |
| ---: | ---: | --- | --- |
| `0` | `4` | Magic | ASCII `GWSS` |
| `4` | `2` | Header size | `40` |
| `6` | `2` | Selected major | `1` for every status |
| `8` | `2` | Selected minor | `0` for every status |
| `10` | `1` | Status | Finite value below |
| `11` | `1` | Echoed role | Must match listener |
| `12` | `4` | Capabilities | Intersection; `0` in Phase 2 |
| `16` | `4` | Maximum receive payload | `16384` |
| `20` | `2` | Heartbeat seconds | Initially `30` |
| `22` | `2` | Idle timeout seconds | Initially `90` |
| `24` | `16` | TLS connection ID | Nonzero on success; zero on rejection |

Status values are `0=ok`, `1=unsupported-version`, `2=wrong-endpoint`,
`3=unsupported-build`, `4=server-busy`, and `5=policy-rejected`.
Every status encodes canonical version `1.0`, capabilities `0`, maximum payload
`16384`, heartbeat `30`, and idle timeout `90`.

## Outer frame

Every later TLS application frame has a 16-byte header:

| Offset | Size | Field | Rule |
| ---: | ---: | --- | --- |
| `0` | `4` | Payload length | `0..16384`, with type-specific limits |
| `4` | `2` | Frame type | Defined below |
| `6` | `2` | Flags | `0` |
| `8` | `8` | Direction-local sequence | Starts at `1`, exact increment |

Sequences are independent per direction. Zero, duplicate, gap, or wrap is a
protocol failure. TLS already guarantees order; the sequence detects state
machine and implementation errors. Close before incrementing
`UInt64.MaxValue`.

| Type | Direction | Payload | Meaning |
| --- | --- | ---: | --- |
| `0x0001` | Server to client | exactly `8` | Ping nonce |
| `0x0002` | Client to server | exactly `8` | Pong nonce |
| `0x0003` | Both | exactly `4` | Numeric close reason |
| `0x0100` | Both | `1..16384` | Opaque legacy XOR byte-stream chunk |
| `0x0200` | Login server to client | `71..408` | Game grant |
| `0x0201` | Client to game server | exactly `52` | Game-ticket bind |
| `0x0202` | Game server to client | exactly `4` | Bind result |

No compression, custom encryption, CRC, arbitrary close text, or unknown flag
is permitted. Frame boundaries may split or combine the legacy stream anywhere
and never reset `PacketCipher`. The existing decrypted inner-packet limit
remains `4..8196`.

The minimum grant is 71 bytes: the 68-byte fixed portion plus one byte for each
of the three required strings.

## Game grant

After successful login and immediately before the legacy game redirect, the
login server sends type `0x0200`:

| Offset | Size | Field |
| ---: | ---: | --- |
| `0` | `1` | Format version, `1` |
| `1` | `1` | Logical route-host length, `1..23` |
| `2` | `1` | TLS DNS-host length, `1..253` |
| `3` | `1` | Audience length, `1..64` |
| `4` | `2` | Logical route port |
| `6` | `2` | Secure TLS game port |
| `8` | `4` | Target server ID |
| `12` | `8` | Expiry as Unix milliseconds, client hint only |
| `20` | `16` | Opaque game-grant ID |
| `36` | `32` | Opaque CSPRNG ticket |
| `68` | `N` | Route host, then TLS DNS host, then audience |

Both hosts are strict ASCII DNS A-label names. Audience is a strict
`[A-Za-z0-9._-]` token. None may contain a NUL or trailing bytes. Both ports,
the target server ID, grant ID, and ticket must be nonzero. The total length is
exactly `68 + route + TLS-host + audience`. The subsequent legacy redirect
contains the same logical route host and port. The shim stores the
authenticated grant before forwarding following legacy bytes to
`NetLegacy.dll`; if storage fails, Origin never sees the redirect.

The route can be a short non-secret synthetic name. `Origin.exe` passes it to a
new proxy object's `SetHost`; the shim atomically matches it to the pending
grant and substitutes the process-local listener. Only the authenticated grant
contains the real TLS DNS endpoint.

A decoded grant is syntax-only. It cannot be used until signed-manifest and
redirect policy validate its hosts, ports, audience, target, and route match.

## Game bind

The first game-channel frame after its successful preface is type `0x0201`:

| Offset | Size | Field |
| ---: | ---: | --- |
| `0` | `1` | Format version, `1` |
| `1` | `3` | Reserved zero bytes |
| `4` | `16` | Game-grant ID |
| `20` | `32` | Ticket |

Grant ID and ticket must be nonzero. The server replies with type `0x0202`: a
two-byte `BindResult` (`0=accepted`, `1=rejected`, `2=server-busy`,
`3=policy-rejected`) followed by two zero reserved bytes. All failures close.
The channel-phase gate is enforced before the legacy game handler. Slice 6
decodes the first bind and returns `policy-rejected`; Slice 7 supplies the
ticket authority that can produce `accepted`.

## Ticket policy

- Generate ticket and connection IDs with the platform CSPRNG.
- Store only `SHA-256(ticket)` server-side and compare in constant time.
- Bind the record to account, issuing login generation, target server,
  audience, protocol, permissions, client-instance ID, accepted client-build
  hash, game-grant ID, and monotonic expiry.
- Default TTL is 60 seconds.
- Default registry capacity is 1024 and at most one outstanding ticket exists
  per account/login generation. These are safety defaults, not scale claims.
- Redeem through one atomic remove. Bit flips, forgery, expiry, wrong scope,
  restart, explicit generation replacement, reissue, or concurrent replay all
  fail generically.
- Never bind a ticket to an IP address or source port.
- Never persist or log raw tickets. Zero client/server raw ticket buffers on
  every terminal path.

Client ticket state is:

```text
Empty -> Pending -> Claimed(proxy ID) -> Presented -> Consumed/Erased
                    |                    |
                    +---- failure -------+
```

A claim may return to `Pending` only if no ticket byte was transmitted. Once
presentation starts, the ticket is never reused. Each successful login creates
a generation ID distinct from the login socket lifetime. A new authenticated
generation erases an older grant. Once the grant and redirect are durably
ordered to the client, the expected login-socket close and release preserve
that generation's one grant until bind or expiry. A protocol failure before
that commit point invalidates it.

## Client lifecycle

Do not infer role from factory-call order; Origin creates and replaces multiple
network objects.

1. `NetClientCreate` creates the verified stock client and registers a unique
   proxy ID with a process coordinator. It starts no thread/socket in
   `DllMain`.
2. `SetHost` copies and bounds-checks the supplied string immediately. An exact
   signed logical-login endpoint means Login. An exact unexpired route with an
   atomically claimed grant means Game. Anything else fails closed.
3. Login `Connect` establishes external TCP, Schannel TLS, ALPN, and preface.
   Only then does it bind/listen on `127.0.0.1:0`, begin one deadline-bound
   asynchronous accept, call stock `SetHost` with that endpoint, call stock
   `Connect`, require the accept to complete, close the listener, and start the
   opaque pumps. Any failed step closes both legs and joins the accept worker.
4. Game `Connect` establishes TLS/preface, sends the claimed bind, and requires
   acceptance before opening the local leg in the same listen/begin-accept/
   stock-connect/accept-complete order. It then wipes the ticket.
5. `SendMsg`, native `Process()`, `PickMsg`, and `GetMsgNum` remain delegated
   with stock ownership every frame. The rejected V4 preview gate is not part
   of Phase 2. A bridge failure may close the loopback socket but may not
   dispose a native message pointer.
6. `DisConnect` and `Release` are idempotent coordinated shutdowns: signal stop,
   close handles, join workers, wipe secrets, unregister, then call the stock
   method. No detached worker may retain a proxy.

Each client-direction queue is bounded by both 128 complete chunks and 512 KiB.
Admission waits at most 250 ms; reliable data is never dropped or reordered, so
overflow closes the connection. Reads use fixed 16 KiB buffers and every socket
write uses a complete-send loop.

## Downgrade and ordering rules

- The shim never contacts raw external ports after TLS, certificate, ALPN,
  preface, authentication, grant, or bind failure.
- The secure game endpoint comes from the authenticated grant and signed
  allowlist, not from the legacy redirect alone.
- One outer writer serializes the grant before the matching legacy redirect.
- The client commits the grant before exposing subsequent legacy bytes.
- Control frames never enter the legacy XOR stream.
- TCP/TLS and future UDP have no shared ordering. Phase 2 advertises no UDP
  capability and creates no UDP socket.

## Heartbeat state machine

Heartbeat starts only after login authentication or successful game bind. The
server is the sole Ping initiator.

- After 30 seconds without sending another valid application frame, the server
  sends one Ping containing a fresh unpredictable eight-byte nonce.
- Only one Ping may be outstanding. The client sends a Pong with the exact
  nonce as its next available control frame within 10 seconds.
- A matching Pong clears the outstanding state. Unsolicited, duplicate,
  wrong-nonce, or wrong-direction Ping/Pong is a protocol failure.
- Each valid received frame advances that side's 90-second receive-idle
  timestamp, but it does not satisfy an outstanding Ping.
- A side that receives no valid peer frame for 90 seconds closes. The server
  also closes immediately when the 10-second Pong deadline expires.
- Heartbeat deadlines are monotonic and do not extend from partial frame bytes.

## Primary platform references

- [Microsoft Schannel protocol-version support](https://learn.microsoft.com/en-us/windows/win32/secauthn/protocols-in-tls-ssl--schannel-ssp-)
- [Microsoft Windows 10 Schannel cipher-suite changes](https://learn.microsoft.com/en-us/windows-server/security/tls/tls-schannel-ssp-changes-in-windows-10-and-windows-server)
- [.NET 10 `SslServerAuthenticationOptions`](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslserverauthenticationoptions?view=net-10.0)
- [.NET `SslStream` platform and cipher troubleshooting](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting)
- [.NET 10 `CipherSuitesPolicy` platform support](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.ciphersuitespolicy?view=net-10.0)
- [NIST SP 800-63B password-verifier guidance](https://pages.nist.gov/800-63-4/sp800-63b.html)
