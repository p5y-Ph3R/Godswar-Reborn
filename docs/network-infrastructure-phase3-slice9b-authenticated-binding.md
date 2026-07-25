# Phase 3 Slice 9B authenticated UDP binding

Document revision: 1.1

Wire revision: 1.0

Status: implemented in source and verified offline; checked-in activation
remains disabled

## Scope

Slice 9B binds one validated UDP endpoint to one already authenticated game
TLS connection. It adds:

- a fixed TLS control grant carrying the exact TLS connection ID and a fresh
  proof key;
- a bounded, generation-safe server authority owned by the game TLS
  connection;
- UDP binding type `4`, which combines the Slice 9A return-path cookie with
  proof of possession of the TLS-delivered key;
- cookie-first coordination, idempotent same-endpoint binding, and conflicting
  endpoint rejection;
- bounded prefix/global admission, low-cardinality counters, and a loopback
  listener exercised only by tests; and
- native x86 parsing and single-owner retention of the grant for the subsequent
  Slice 9C UDP worker.

Gameplay remains on TLS. Slice 9B does not provide protected gameplay
datagrams, AEAD, replay windows, UDP key epochs, NAT rebinding, keepalive,
pacing, snapshot delivery, or a native UDP worker. It therefore completes
authenticated endpoint binding only, not all of Phase 3 or Slice 9.

## TLS binding grant

After a game ticket is accepted and the TLS connection has an authoritative
`SecureBoundGamePrincipal`, the server may register the exact 16-byte
connection ID from that TLS preface. A successful registration produces one
server-to-client game control frame:

- outer frame type: `0x0203` / `UdpBindingGrant`;
- direction and role: server-to-client on the game TLS connection only; and
- payload size: exactly 72 bytes.

Every integer in the payload uses network byte order.

| Offset | Size | Field |
| ---: | ---: | --- |
| `0` | `4` | Magic, ASCII `GWUG` |
| `4` | `2` | Protocol major, `1` |
| `6` | `2` | Protocol minor, `0` |
| `8` | `2` | Nonzero UDP destination port |
| `10` | `2` | Reserved, zero |
| `12` | `4` | Nonzero target server ID |
| `16` | `8` | Nonzero binding-offer expiry, Unix milliseconds |
| `24` | `16` | Exact nonzero game-TLS connection ID |
| `40` | `32` | Fresh nonzero TLS proof key |

The frame is invalid at any other role, direction, or payload size. The native
shim accepts it only after successful game binding, only once, and only when
its connection ID exactly matches the retained server-preface value. A
mismatch, duplicate, malformed grant, or invalid ordering fails closed.
Connection IDs and proof keys are cleared on stop; grant ownership is
move-only so each proof-key copy has an explicit owner.

The proof key is generated with the platform CSPRNG and is never persisted or
logged. Grant delivery is conditional on successful bounded registration. If
no authority is supplied or its capacity is full, ordinary TLS gameplay
continues without a UDP grant.

## Authenticated client proof

Slice 9A types `1=ClientHello`, `2=ServerChallenge`, and
`3=ClientProof` retain their exact 128-byte encoding. Type 3 proves only the
stateless address cookie and never binds an authenticated session.

Slice 9B adds `4=AuthenticatedClientProof`. It uses the same exact 128-byte
datagram and changes the former padding at offsets `72..95` into a TLS proof
tag:

| Offset | Size | Type-4 field |
| ---: | ---: | --- |
| `0` | `4` | Magic, ASCII `GWSU` |
| `4` | `2` | Header bytes, `48` |
| `6` | `1` | Protocol major, `1` |
| `7` | `1` | Protocol minor, `0` |
| `8` | `1` | Type, `4` |
| `9` | `1` | Flags, zero |
| `10` | `2` | Total bytes, `128` |
| `12` | `16` | Exact game-TLS connection ID |
| `28` | `4` | Nonzero cookie-key epoch |
| `32` | `8` | Sequence, zero during binding |
| `40` | `2` | Payload bytes, `48` |
| `42` | `6` | Reserved, zero |
| `48` | `16` | Nonzero client nonce |
| `64` | `8` | Positive signed Unix seconds from the challenge |
| `72` | `24` | Truncated TLS proof tag |
| `96` | `32` | Full Slice 9A address-cookie HMAC |

For types 1 through 3, offsets `72..95` must remain zero. For type 4, the
24-byte proof tag must not be all zero.

The type-4 proof tag is the first 24 bytes of:

```text
HMAC-SHA256(
  key = 32-byte proof key delivered over TLS,
  message = ASCII("GWSU-TLS-BIND-PROOF-V1")
            || exact 128-byte ServerChallenge)
```

The domain has no terminating NUL. The challenge input is the byte-exact type
2 datagram returned by the server, including zero bytes at offsets `72..95`.
The type-4 datagram copies its connection ID, epoch, sequence, nonce, issue
time, and cookie tag from that challenge.

## Binding lifecycle

1. The game TLS connection completes ticket validation and owns a nonnull
   authoritative principal.
2. The server registers its exact connection ID in a fixed-capacity authority,
   generates a 32-byte proof key, starts a monotonic pending TTL, and sends the
   72-byte grant over that same TLS connection.
3. The native shim validates and retains the grant. No current production
   worker consumes it.
4. A future client worker sends an exact type-1 hello with the granted
   connection ID and a fresh nonce.
5. The server returns the exact type-2 stateless challenge. The response is
   128 bytes for a 128-byte request, preserving the 1.0 prevalidation
   amplification ratio.
6. The client returns type 4 with the original cookie and the TLS proof tag.
7. The server validates the endpoint cookie before looking up or copying any
   TLS-session proof key. It then validates the proof tag in fixed time and
   binds the observed endpoint to the registered principal.
8. Repeating a valid proof from the same canonical endpoint is idempotent.
   A different endpoint conflicts and cannot replace the original binding.
9. Closing or failing the owning TLS transport disposes its generation-tagged
   lease, removes the entry immediately, and zeros its proof key. Pending
   offers also expire by monotonic time; stale leases cannot remove a later
   generation.

Only authenticated game TLS sessions can register. The table has a fixed
capacity and never evicts an established entry to admit another. Pending
expiry is enforced by the server; after binding, the entry remains owned by
the live TLS lease and is removed when that lease closes.

## Bounds and inactive listener

Checked-in defaults are:

| Limit | Default | Accepted range |
| --- | ---: | ---: |
| Maximum datagram bytes | `1200` | `128..1200` |
| Cookie lifetime | `10s` | `5..30s` |
| Cookie future skew | `2s` | `0..5s`, not above lifetime |
| Cookie-key rotation | `60s` | at least `2x` lifetime, at most `3600s` |
| Session capacity | `1024` | `1..65536` |
| Pending binding-offer TTL | `30s` | `5..120s` |
| Global datagrams per second | `4096` | `1..1000000` |
| Prefix datagrams per second | `256` | `1..global limit` |
| Tracked limiter prefixes | `1024` | `1..65536` |

The limiter uses fixed one-second windows, IPv4 `/24` prefixes, IPv6 `/64`
prefixes, and a fixed-size prefix map. The listener uses one bounded receive
buffer, one fixed 128-byte response buffer, silently drops invalid or
rate-limited datagrams, and exposes only low-cardinality outcome tags.
This provisional limiter does not reserve capacity for authenticated proofs;
live activation requires established/pending-session priority so Hello floods
cannot starve valid binding attempts.

This listener is not part of the runnable server composition. Tests construct
it directly on loopback, commonly with an ephemeral port. Checked-in
`Secure.Udp.Enabled` is `false`, and validation deliberately rejects `true`
with a fail-closed error. The bind host must remain a literal loopback/private
address and the UDP port must not overlap a raw or TLS TCP port.

## Security boundary

Successful type-4 validation proves two facts:

- the sender can receive the server's short-lived cookie at the observed
  endpoint; and
- the sender possesses a key delivered only through the authenticated game
  TLS session identified by the exact connection ID.

It does not encrypt or authenticate later UDP gameplay traffic. The connection
ID is opaque but is not a secret. The cookie is not player authentication, and
the binding protocol does not replace upstream arbitrary-UDP DDoS protection.
An on-path observer can replay a captured proof from the same tuple, but the
operation is idempotent and carries no gameplay command. Endpoint migration is
intentionally rejected until authenticated NAT-rebinding rules exist.

Cookie validation happens before session lookup or proof-key use. Before that
validation the server allocates no per-client session state and sends no
response larger than the request. Parsing, buffers, session maps, rate-limit
maps, and response sizes are finite.

## Verification

Server build and focused binding checks:

```powershell
dotnet build .\GodswarServer.sln --configuration Release

dotnet run `
  --project .\tests\Godswar.Server.ProtocolChecks\Godswar.Server.ProtocolChecks.csproj `
  --configuration Release -- `
  "Secure Phase 3 UDP"

dotnet run `
  --project .\tests\Godswar.Server.ProtocolChecks\Godswar.Server.ProtocolChecks.csproj `
  --configuration Release -- `
  "Secure Phase 2 TLS mux transport"
```

Native grant parsing, state ordering, ownership, and secret cleanup:

```powershell
.\tools\BuildClientNetworkShim.ps1 -Configuration Release
.\client\network-shim\bin\Release\Win32\Godswar.NetShim.Checks.exe --offline
```

Coverage includes exact grant/type-4 encoding, truncation and field
boundaries, frame role/direction policy, connection-ID association, capacity
and duplicate registration, monotonic expiry, generation-safe release,
concurrent capacity, wrong keys and tag-bit tampering, unknown/revoked
sessions, same-endpoint idempotency, endpoint conflict, secret zeroing,
cookie-first rejection, loopback request/challenge/proof flow, listener
shutdown, and global/prefix limiter bounds. The Slice 9A golden and malformed
vectors continue to prove that types 1 through 3 did not change.

These are offline/local results, not a capacity claim or live activation.

## Subsequent Slice 9 closeout

Slice 9C completed the work that was intentionally outside Slice 9B:
AES-256-GCM datagrams, replay/key epochs, authenticated NAT rebinding, a
nonblocking native worker, keepalive/pacing, TLS-only fallback,
authenticated-session priority, guarded activation, and bounded verification.
The [protected-datagram document](network-infrastructure-phase3-slice9c-protected-datagrams.md)
is the canonical wire specification, the
[protected-UDP ADR](network-infrastructure-phase3-slice9c-protected-udp.md) is
the overall completion overview, and the
[runtime document](network-infrastructure-phase3-slice9-runtime.md) records
runtime ownership and admission behavior.

Closeout passed the full managed protocol suite (`121/121`), a Win32 Release
native build with `/W4 /WX`, and five consecutive native offline passes.
Checked-in UDP remains disabled, no client shim was installed, and gameplay
remains on TLS. Gameplay transport migration belongs to Phase 4.
