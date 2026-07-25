# Phase 3 Slice 9A UDP binding foundation

Document revision: 1.0

Wire revision: 1.0

Status: implemented and verified offline; deliberately inactive

## Scope and non-scope

Slice 9A supplies the server-side, allocation-free return-path validation
foundation for later authenticated UDP binding:

- an exact, versioned 128-byte binding datagram;
- strict caller-owned encoding and decoding;
- a stateless address cookie built with platform HMAC-SHA256;
- current/previous in-memory cookie-key rotation;
- inert, validated configuration; and
- golden, boundary, forgery, timing, rotation, concurrency, and randomized
  tests.

It does not create a socket, advertise a UDP capability, retain the TLS
connection ID, modify the native client, allocate a UDP session, or move
gameplay off TLS. Setting `Secure.Udp.Enabled=true` fails configuration
validation explicitly.

The DTLS-versus-reviewed-AEAD decision remains a blocking ADR before protected
session packets are designed. This binding format does not define gameplay,
snapshot, acknowledgement, replay-window, congestion, or AEAD semantics.

## Fixed binding datagram

All integers use network byte order. Every Slice 9A datagram is exactly 128
bytes. The general path-MTU ceiling remains 1,200 bytes, but the Slice 9A
decoder accepts no other size.

| Offset | Size | Field |
| ---: | ---: | --- |
| `0` | `4` | Magic, ASCII `GWSU` |
| `4` | `2` | Header bytes, `48` |
| `6` | `1` | Protocol major, `1` |
| `7` | `1` | Protocol minor, `0` |
| `8` | `1` | Type: `1=ClientHello`, `2=ServerChallenge`, `3=ClientProof` |
| `9` | `1` | Flags, zero |
| `10` | `2` | Total bytes, `128` |
| `12` | `16` | Nonzero TLS connection ID |
| `28` | `4` | Cookie-key epoch; zero only for `ClientHello` |
| `32` | `8` | Sequence, zero throughout prevalidation |
| `40` | `2` | Payload bytes, `48` |
| `42` | `6` | Reserved, zero |
| `48` | `16` | Nonzero client nonce |
| `64` | `8` | Signed Unix seconds; zero only for `ClientHello` |
| `72` | `24` | Padding, zero |
| `96` | `32` | Full HMAC-SHA256 tag; zero only for `ClientHello` |

Unknown versions or types, nonzero flags/reserved/padding/sequence, zero
identifiers, wrong fixed values, truncation, trailing bytes, and invalid
type-specific fields reject without throwing.

## Stateless exchange

1. The client sends one exact `ClientHello` containing the 16-byte connection
   ID received over TLS and a fresh nonzero 16-byte nonce.
2. Only a syntactically valid 128-byte hello can receive a response.
3. The server observes the source endpoint, issues a cookie using the current
   key epoch and logical Unix time, and returns an exact 128-byte
   `ServerChallenge`.
4. The client echoes those authenticated values as an exact 128-byte
   `ClientProof`, changing only the message type.
5. The server verifies syntax, endpoint, time, key ID, and HMAC before
   publishing the connection ID to a future bounded lookup.

Request and challenge are both 128 bytes, so the only unauthenticated response
path has an amplification ratio of exactly 1.0. Invalid input receives no
response. The cookie allocates no session state and performs no database,
decompression, asymmetric-cryptography, or gameplay work.

## Cookie construction

The implementation uses only .NET platform primitives:

- 32-byte keys from `RandomNumberGenerator`;
- full `HMACSHA256.HashData` output;
- `CryptographicOperations.FixedTimeEquals`; and
- explicit zeroing of temporary, displaced, and disposed key material.

The unambiguous HMAC input contains:

1. domain separator `GWSU-COOKIE-PROOF-V1`;
2. protocol major/minor and the `ClientProof` purpose;
3. key epoch and issue time;
4. target server ID, configured UDP destination port, and bounded audience;
5. canonical observed address family and a 16-byte address field;
6. IPv6 scope ID and observed UDP source port;
7. TLS connection ID; and
8. client nonce.

IPv4-mapped IPv6 is canonicalized to IPv4. Unspecified, IPv4 broadcast,
IPv4/IPv6 multicast, zero-port, and unsupported-family endpoints reject.
Changing the address, source port, IPv6 scope, destination port, audience,
server, connection ID, nonce, time, epoch, or any tag bit rejects.

Cookie time advances from a process-start Unix anchor using the monotonic
`TimeProvider` timestamp. A wall-clock rollback therefore cannot extend a
cookie. The default lifetime is 10 seconds with two seconds of future skew.

Exactly current and previous process-local keys are retained. Keys are 32
random bytes with random nonzero epochs. Rotation uses monotonic time, defaults
to 60 seconds, and must be at least twice the cookie lifetime. Displaced and
disposed secrets are zeroed. Restarting the process invalidates every cookie.

## Configuration

The checked-in configuration is inert:

```json
"udp": {
  "enabled": false,
  "bindHost": "127.0.0.1",
  "port": 7444,
  "maximumDatagramBytes": 1200,
  "cookieLifetimeSeconds": 10,
  "cookieFutureSkewSeconds": 2,
  "cookieKeyRotationSeconds": 60
}
```

Environment overrides use:

- `GODSWAR_SECURE_UDP_ENABLED`;
- `GODSWAR_SECURE_UDP_BIND_HOST`;
- `GODSWAR_SECURE_UDP_PORT`;
- `GODSWAR_SECURE_UDP_MAXIMUM_DATAGRAM_BYTES`;
- `GODSWAR_SECURE_UDP_COOKIE_LIFETIME_SECONDS`;
- `GODSWAR_SECURE_UDP_COOKIE_FUTURE_SKEW_SECONDS`; and
- `GODSWAR_SECURE_UDP_COOKIE_KEY_ROTATION_SECONDS`.

No key or cookie secret is accepted from JSON, logged, persisted, or committed.
The development bind must be a literal loopback/private address. Port `7444`
must be distinct from every raw/TLS TCP port. The maximum datagram value must
be `128..1200`, cookie lifetime `5..30` seconds, future skew `0..5` seconds,
and rotation at least twice the lifetime and no more than one hour.

## Verification

```powershell
dotnet build .\GodswarServer.sln --configuration Release

dotnet run `
  --project .\tests\Godswar.Server.ProtocolChecks\Godswar.Server.ProtocolChecks.csproj `
  --configuration Release -- `
  "Secure Phase 3 UDP"
```

Coverage includes:

- exact byte-order and HMAC golden vectors;
- every truncation and every non-binding size through 1,201 bytes;
- magic, version, type, flag, length, reserved, padding, identifier, time,
  epoch, and sequence mutations;
- every individual HMAC tag-bit mutation;
- IPv4, mapped IPv4, IPv6, and scoped-IPv6 binding;
- target audience and destination-port separation;
- exact expiry/future-skew boundaries;
- current/previous rotation, displaced-key rejection, and secret zeroing;
- concurrent issue/validate operations;
- zero-allocation warmed decoding; and
- deterministic randomized malformed input with the amplification invariant.

## Required Slice 9B work

The current TLS server and native client both discard the 16-byte server
connection ID after the preface. Slice 9B must retain the exact game-TLS value
on both sides; it must never regenerate it or identify a player by endpoint.

After successful TLS game binding, 9B must deliver fresh UDP proof material
over TLS, add a bounded connection/session authority, open the separately
owned UDP listener, add cheap admission/rate limiting and low-cardinality
metrics, and make repeat proofs idempotent. A cookie proves only return-path
ownership. It is replayable from the same tuple during its short lifetime, is
not player authentication, does not stop an on-path attacker, and does not
replace upstream arbitrary-UDP DDoS protection.

Gameplay remains on authenticated TLS until a separate ADR selects the
protected datagram construction and its replay, key-epoch, pacing, congestion,
fallback, and reconciliation rules.
