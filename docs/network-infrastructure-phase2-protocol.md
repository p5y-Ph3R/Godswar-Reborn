# Phase 2 TLS wire protocol and client lifecycle

## Status and ownership

- Protocol version: `1.0`
- Last updated: `2026-07-24`
- Runtime status: specified only; not enabled; blocked on V2 live acceptance
- Historical network-stable recovery shim:
  `528913E66888D5C070C39949D2FC1AE439B8414B15152312D4E093A29D17A6DD`
- Historical failed V1 (rolled back after the 2026-07-24 live incident):
  `2D819908BEE2FA7D8BE4957E18358DEFFB5FD65D01AC26D6F73F29F4C71E2AE0`
- Installed V2 (`InstalledExact`; automated gates pass; live acceptance pending):
  `73E65FBFA3EA9809AF597DA3D25D1E0963B0A4A467549191BAFB4FAE9F2902FD`
- Current V2 Apply/stock-restore backup:
  `C:\Reborn\backups\client-network-shim-v1-Apply-20260724-155531621`
- Parent phase:
  [`network-infrastructure-phase2.md`](network-infrastructure-phase2.md)

This is the normative TLS, framing, game-ticket, and x86 client-lifecycle
contract. All bounds are part of the protocol unless explicitly described as
an operational default. The failed V1 is not installed. Phase 2 remains
blocked until installed V2 passes live acceptance.

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

### Signed endpoint manifest

The manifest filename is module-relative `RebornNetwork.gwem`. It is at most
4096 bytes. The 72-byte header is:

| Offset | Size | Field | Version 1 rule |
| ---: | ---: | --- | --- |
| `0` | `4` | Magic | ASCII `GWEM` |
| `4` | `4` | Total bytes | `146..3258` |
| `8` | `2` | Header bytes | `72` |
| `10` | `2` | Format major | `1` |
| `12` | `2` | Format minor | `0` |
| `14` | `1` | Environment | `1=dev`, `2=staging`, `3=production` |
| `15` | `1` | Flags | Bit 0 is dev-only legacy passthrough |
| `16` | `2` | Signature algorithm | `1=ECDSA-P256-SHA256-P1363` |
| `18` | `2` | Public-key ID | Embedded current or next key |
| `20` | `4` | Reserved | Zero |
| `24` | `8` | Manifest sequence | Nonzero, monotonically increasing |
| `32` | `8` | Not-before | Unix seconds |
| `40` | `8` | Not-after | Unix seconds |
| `48` | `2` | Minimum protocol major | `1` |
| `50` | `2` | Minimum protocol minor | `0` initially |
| `52` | `2` | Logical login port | Nonzero |
| `54` | `2` | TLS login port | Nonzero |
| `56` | `2` | Logical-host bytes | `1..253` |
| `58` | `2` | TLS-host bytes | `1..253` |
| `60` | `1` | Game-suffix count | `1..8` |
| `61` | `1` | Audience count | `1..8` |
| `62` | `1` | Server-ID count | `1..16` |
| `63` | `1` | Reserved | Zero |
| `64` | `4` | Signed bytes | `total - 64` |
| `68` | `4` | Reserved | Zero |

The body immediately follows:

1. Exact logical-login host bytes from the header length.
2. Exact TLS-login DNS host bytes from the header length.
3. Each game DNS suffix as one length byte (`1..253`) then bytes.
4. Each audience as one length byte (`1..64`) then bytes.
5. Each permitted nonzero, unique server ID as a four-byte integer.

Hosts/suffixes are canonical lower-case ASCII DNS names without a trailing dot;
the logical host may instead be canonical dotted-decimal IPv4. Audiences match
`[A-Za-z0-9._-]`. Duplicates, NULs, empty labels, unknown flags, and trailing
body bytes are rejected. Production rejects the legacy-passthrough flag and a
manifest for any other environment.

Each suffix entry represents both its apex and its subdomains. A grant TLS host
matches only when it is byte-for-byte equal to the suffix or ends with
`"." + suffix`; raw `EndsWith(suffix)` is forbidden. Thus `game.example.com`
and `example.com` match `example.com`, while `evil-example.com` does not.

The final 64 bytes are the IEEE P1363 `r || s` ECDSA signature. SHA-256 and
signature verification cover bytes `0..SignedBytes-1` exactly; DER signatures
are not accepted. `SignedBytes + 64` must equal `TotalBytes`. The validity
interval must contain current UTC, have `not-after > not-before`, and be no
longer than 31 days.

The shim embeds current and next verification public keys plus a minimum
sequence per environment. The guarded installer maintains the highest accepted
sequence in an administrators/SYSTEM-write, users-read registry value under
`HKLM\Software\Reborn\NetworkManifest`; missing or corrupt state fails closed in
`SecureRequired`. A manifest sequence below either compiled or installed
minimum is rejected. Key rotation first ships a shim that trusts current and
next IDs, then signs a higher-sequence manifest with the next key, and a later
shim removes the old key and advances its compiled minimum.

The loader opens the module-relative file without write sharing, rejects a
reparse point or a final path outside the module directory, reads once into a
fixed 4096-byte buffer, strictly parses, verifies the signature, and only then
copies endpoints into runtime state. It never hot-reloads. The installer
verifies a candidate with the same parser, atomically writes/flushes the higher
registry minimum first, then atomically replaces and flushes the manifest from
the same directory. An interruption between those steps fails closed and is
repaired by installer recovery; it cannot reactivate the lower sequence.

## Encoding rules

All outer integers use unsigned big-endian network order. Byte arrays are
copied exactly. Legacy bytes inside a `LegacyBytes` payload retain their current
XOR state and little-endian framing.

Unknown versions, roles, flags, sizes, reserved fields, payload bounds, or
direction-specific frame types fail closed. A rejected connection receives no
attacker-controlled diagnostic text.

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
| `24` | `16` | Client-instance ID | Per-process CSPRNG bytes |
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
| `6` | `2` | Selected major | `1` on success |
| `8` | `2` | Selected minor | `0` on success |
| `10` | `1` | Status | Finite value below |
| `11` | `1` | Echoed role | Must match listener |
| `12` | `4` | Capabilities | Intersection; `0` in Phase 2 |
| `16` | `4` | Maximum receive payload | `16384` |
| `20` | `2` | Heartbeat seconds | Initially `30` |
| `22` | `2` | Idle timeout seconds | Initially `90` |
| `24` | `16` | TLS connection ID | Opaque; zero on rejection |

Status values are `0=ok`, `1=unsupported-version`, `2=wrong-endpoint`,
`3=unsupported-build`, `4=server-busy`, and `5=policy-rejected`.

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
`[A-Za-z0-9._-]` token. None may contain a NUL or trailing bytes, and both ports
must be nonzero. The total length is exactly
`68 + route + TLS-host + audience`. The subsequent legacy redirect contains
the same logical route host and port. The shim stores the authenticated grant
before forwarding following legacy bytes to `NetLegacy.dll`; if storage fails,
Origin never sees the redirect.

The route can be a short non-secret synthetic name. `Origin.exe` passes it to a
new proxy object's `SetHost`; the shim atomically matches it to the pending
grant and substitutes the process-local listener. Only the authenticated grant
contains the real TLS DNS endpoint.

## Game bind

The first game-channel frame after its successful preface is type `0x0201`:

| Offset | Size | Field |
| ---: | ---: | --- |
| `0` | `1` | Format version, `1` |
| `1` | `3` | Reserved zero bytes |
| `4` | `16` | Game-grant ID |
| `20` | `32` | Ticket |

The server replies with type `0x0202`: a two-byte finite status followed by two
zero reserved bytes. Status `0` means accepted. All failure cases use bounded
generic statuses and close. No `LegacyBytes`, `GameClientHandler`, world state,
or stale-session replacement is allowed before a successful bind.

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
5. `SendMsg` and native `Process()` stay delegated every frame. The V2 gate is
   the only `PickMsg`/`GetMsgNum` exception: it may hold exactly one audited
   opcode-`10002` native pointer and must not poll past it, preserving order.
   It returns that exact pointer on readiness or after a guarded five-second
   fallback. The fallback neither destroys it nor calls native disconnect and
   may still yield a blank preview if resources never become ready. Only an
   explicit `Connect`, `DisConnect`, `Release`, or proxy destruction reset
   invokes the stock virtual destructor for a pointer still held. A bridge
   failure may close the loopback socket but does not dispose that pointer.
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
