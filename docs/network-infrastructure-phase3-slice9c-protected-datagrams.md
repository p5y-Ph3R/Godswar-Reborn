# Phase 3 Slice 9C protected UDP datagrams

Document revision: 1.1

Wire revision: 1.0

Status: implemented and verified offline/loopback; checked-in activation
remains disabled

## Scope

This document is the canonical Slice 9 protected-datagram wire specification.
The [protected-UDP ADR](network-infrastructure-phase3-slice9c-protected-udp.md)
records the overall decision and completion status, and the
[runtime document](network-infrastructure-phase3-slice9-runtime.md) records
runtime ownership and admission behavior.

Slice 9C defines the protected datagram used only after Slice 9B has bound a
UDP endpoint to an authenticated game-TLS session. The construction provides:

- AES-256-GCM confidentiality and authentication;
- independent client-to-server and server-to-client traffic keys;
- deterministic, unique nonces under a session-owned sequence counter;
- authenticated acknowledgements;
- a 128-packet receive replay window;
- current, previous, and exactly-next receive-key epoch handling;
- bounded previous-key overlap and explicit send-key rotation; and
- three control messages needed to validate the protected channel.

The codec is not an alternative authentication mechanism. The 32-byte input
key comes from the existing TLS UDP-binding grant, and the 16-byte connection
ID must identify that exact TLS session.

No gameplay input, snapshot, inventory operation, damage outcome, or other
gameplay message is defined by this revision.

## Datagram format

Every integer uses network byte order. The exact 64-byte header is AEAD
associated data and is therefore authenticated but not encrypted. Ciphertext
immediately follows the header, followed by the 16-byte GCM tag.

```text
datagram = header64 || ciphertext[payloadBytes] || tag16
```

Datagrams are 80 through 1,200 bytes. This revision's control messages are
96 or 112 bytes. IP fragmentation is never required or permitted by the
application contract.

| Offset | Size | Field |
| ---: | ---: | --- |
| `0` | `4` | Magic, ASCII `GWSP` (`0x47575350`) |
| `4` | `2` | Header bytes, `64` |
| `6` | `1` | Protocol major, `1` |
| `7` | `1` | Protocol minor, `0` |
| `8` | `1` | Packet type, `1=Protected` |
| `9` | `1` | Flags, zero |
| `10` | `2` | Exact total datagram bytes |
| `12` | `16` | Exact nonzero game-TLS connection ID |
| `28` | `4` | Nonzero traffic-key epoch |
| `32` | `8` | Packet sequence |
| `40` | `4` | Acknowledged key epoch, or zero |
| `44` | `8` | Acknowledged high-water sequence |
| `52` | `8` | Previous-sequence acknowledgement mask |
| `60` | `1` | Protected message type |
| `61` | `1` | Reserved, zero |
| `62` | `2` | Ciphertext/plaintext payload bytes |
| `64` | variable | Ciphertext |
| `64 + payloadBytes` | `16` | AES-GCM authentication tag |

If the acknowledged epoch is zero, acknowledged sequence and mask must both
be zero. Otherwise the high-water sequence acknowledges itself. Mask bit
`N` acknowledges `highWater - (N + 1)`. Bits that would underflow sequence
zero must be clear. ACK fields do not imply ordering with TLS.

## Key derivation and nonce

The implementation uses the platform `HKDF` and `AesGcm` implementations with
SHA-256, a 32-byte output, AES-256, and a 16-byte tag. It does not implement a
cryptographic primitive.

For each direction and epoch:

```text
IKM  = 32-byte TLS UDP-binding proof key
salt = connectionId16 || serverIdBE32
info = ASCII("GWSU-PROTECTED-DATAGRAM-V1")
       || directionByte
       || keyEpochBE32
key  = HKDF-SHA256(IKM, salt, info, 32)
```

The ASCII domain has no terminating NUL. Direction `1` is client-to-server;
direction `2` is server-to-client. The TLS proof key remains memory-only for
the TLS session lifetime so future epoch keys can be derived. Owned secret and
traffic-key copies are zeroed on replacement and disposal.

The 12-byte GCM nonce is:

```text
keyEpochBE32 || sequenceBE64
```

Epoch starts at one and sequence starts at zero. Sequence
`0xffffffffffffffff` may be used once, after which that send epoch is
exhausted. Epoch `0xffffffff` cannot advance. Neither value ever wraps.
Because direction and epoch produce different keys, a nonce is unique under
each traffic key while the session-owned sequence rules are followed.

## Receive epochs and replay

The receiver accepts packets for:

- its current epoch;
- its one previous epoch until the configured monotonic overlap expires; or
- exactly `current + 1`.

An exactly-next key is derived as a candidate. It becomes current only after
the GCM tag, bounded message shape, message content, and direction policy all
validate. The displaced current key becomes previous, and any older previous
key is zeroed. Arbitrary future epochs and epochs older than the retained
previous epoch reject.

Each receive epoch owns a fixed 128-bit replay bitmap. A sequence is checked
without mutation before decryption and committed only after successful
authentication and semantic validation. Duplicates and values at least 128
behind the high-water sequence reject. Reordering inside the window is
accepted once. Forged high sequences cannot advance the high-water mark.

## Control messages

| ID | Name | Direction | Bytes | Payload |
| ---: | --- | --- | ---: | --- |
| `1` | `Ping` | client to server | `16` | nonzero `pingId` u64, untrusted sender monotonic milliseconds u64 |
| `2` | `Pong` | server to client | `32` | exact Ping16, server receive Unix milliseconds u64, server send Unix milliseconds u64 |
| `3` | `BindingConfirm` | server to client | `32` | exact binding-challenge client nonce16, binding revision u64, server Unix milliseconds u64 |

All integer payload fields use network byte order. Server timestamps and
binding revision are positive. Binding revision starts at one and increments
only after an authenticated fresh endpoint rebind. The same protected session,
traffic keys, epochs, sequences, and replay windows survive a rebind.

The server sends an encrypted `BindingConfirm` after a valid authenticated
binding proof. Its initial ACK is zero when no client protected packet has
been committed. The 112-byte response remains below the 128-byte binding
proof, preserving the pre-existing anti-amplification boundary.

## Golden vector

Canonical client-to-server epoch-one Ping:

```text
IKM        000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F
connId     101112131415161718191A1B1C1D1E1F
serverId   01020304
salt       101112131415161718191A1B1C1D1E1F01020304
info       475753552D50524F5445435445442D444154414752414D2D56310100000001
key        C27A8E9BF928AE027A3915F49E942F9273CE975F27CD775CC2E7ED894A00D5FA
nonce      000000010000000000000000
header     475753500040010001000060101112131415161718191A1B1C1D1E1F000000010000000000000000000000000000000000000000000000000000000001000010
plaintext  000000000000000100000000075BCD15
ciphertext 36486AB35FD8E6650AB613A49B881EDD
tag        7D174FF3A7946AA12C991108036242C6
```

The complete 96-byte datagram is:

```text
475753500040010001000060101112131415161718191A1B1C1D1E1F00000001000000000000000000000000000000000000000000000000000000000100001036486AB35FD8E6650AB613A49B881EDD7D174FF3A7946AA12C991108036242C6
```

Managed and native implementations must consume this same vector.

## Verification

```powershell
dotnet build .\GodswarServer.sln --configuration Release

dotnet run `
  --project .\tests\Godswar.Server.ProtocolChecks\Godswar.Server.ProtocolChecks.csproj `
  --configuration Release -- `
  "Secure Phase 3 UDP"
```

Coverage includes the golden vector, byte order, directional/epoch key
separation, every truncation, path-MTU overflow, structural mutations, every
header/ciphertext/tag byte mutation, wrong keys, random malformed input,
allocation-free warmed header decoding, replay boundaries and reordering,
sequence and epoch exhaustion, automatic ACKs, next-epoch promotion, previous
overlap expiry, forged future epochs, skipped epochs, packet/time rotation,
wrong-direction authenticated messages, invalid authenticated payloads,
wrong TLS binding secrets, and disposal.

Slice 9 closeout passed the full managed protocol suite (`121/121`), a Win32
Release native build with `/W4 /WX`, and five consecutive native offline
passes. The bounded runtime baseline is recorded in the
[runtime document](network-infrastructure-phase3-slice9-runtime.md). These are
local/offline results, not a production-capacity claim. Checked-in UDP remains
disabled, no client shim was installed, and gameplay remains on TLS.
