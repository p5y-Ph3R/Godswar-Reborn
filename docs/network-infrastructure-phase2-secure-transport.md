# Phase 2 secure transport through Slice 8 activation

## Status and boundary

- Implementation checkpoint: Slice 8 complete in source/offline tests;
  controlled-host activation pending
- Last updated: `2026-07-25`
- Default state: disabled
- Installed game-client state: unchanged
- UDP state: absent

Slice 6 supplied TLS and signed-endpoint prerequisites; Slice 7 added
authentication and one-use grant/ticket binding. Slice 8 wires the exported
client route/session path, candidate-bound manifest probing, guarded activation
and coherent listener startup. It does not install `Net.dll`, create operational
keys, trust a CA, run controlled-host original-client smoke, or authorize UDP.

Listener profiles are mutually exclusive. The checked-in `secure.enabled=false`
profile starts only raw development listeners `5999/7000`; enabling secure mode
instead starts only TLS `6599/7443` and suppresses both raw compatibility
listeners. Secure TLS never sniffs or downgrades to raw traffic.

## Implemented shape

```text
candidate x86 shim                    .NET server

signed GWEM policy
        |
external TCP -> Schannel              SslStream <- separate TCP listener
        |                                 |
TLS preface + bounded frames  <------->  preface/build/role policy
        |                                 |
opaque legacy XOR byte stream            ClientSession
```

The server has two opt-in development listeners:

| Role | Default | Current source behavior |
| --- | --- | --- |
| Secure login | `127.0.0.1:6599` | TLS, preface, framed opaque legacy bytes |
| Secure game | `127.0.0.1:7443` | TLS/preface, then ticket bind before legacy bytes |

Secure login now authenticates against a versioned password verifier, creates
one login generation, sends one `GameGrant` before the matching redirect, and
activates the ticket after the grant is physically written. The activated
ticket is redeemable but its lease remains revocable until the redirect is
physically written; redirect success commits it. The client stores the grant
but must not expose or use its route before that matching redirect. Secure game
requires and atomically consumes the scoped ticket before it can construct a
legacy game handler. The bound account ID is authoritative; opcode `10000`
supplies only a username compatibility check.

## TLS policy

Both sides enforce the normative
[wire protocol](network-infrastructure-phase2-protocol.md):

- TLS 1.2 or TLS 1.3;
- exact ALPN `godswar-shim/1`;
- encryption and integrity;
- only the four documented ECDHE-RSA/TLS 1.3 AES-GCM suites;
- a currently valid RSA certificate with a key of at least 2048 bits, an
  RSA/SHA-256 leaf signature, server-authentication EKU, and both configured
  login/game DNS names in SAN;
- normal Windows chain, name, and revocation validation in the Schannel
  candidate; and
- no certificate callback, trust bypass, plaintext retry, or raw fallback.

Windows does not expose an exact per-process cipher offer through
`SslStream`. The server therefore validates the negotiated suite immediately
after the handshake; Linux and macOS also constrain the offer. A production
Windows deployment still requires the documented host Schannel cipher policy.

Accepted TCP connections share the existing global and address/prefix
admission budgets. A separate semaphore caps simultaneous TLS handshakes.
Server handshake, preface, frame header/body, queue admission, write,
game-bind, and idle work all have finite deadlines. Native socket
resolution/connect, frame I/O, and application handshake steps also carry
finite budgets, subject to the Schannel platform exception below.

The native candidate keeps Windows automatic chain, DNS-name, and revocation
validation enabled. Schannel may perform synchronous OS revocation retrieval
inside `InitializeSecurityContextW`; that platform call is not preemptible by
the shim's socket deadline. The candidate is therefore not eligible for live
activation until a controlled-host test bounds this behavior or a reviewed
bounded validation worker/manual-chain design replaces it. Server-side
`SslStream` admission remains independently bounded.

## Framed stream behavior

After the fixed preface, both sides use the 16-byte big-endian frame header and
the maximum 16,384-byte payload in the protocol specification. The login
transport exposes only `LegacyBytes` payloads to `ClientSession`; TLS framing
never changes the existing rolling XOR cipher or packet boundaries.

Sequence numbers start at one and may not wrap. Unknown, wrong-direction,
wrong-phase, malformed, truncated, oversized, duplicate, and out-of-sequence
frames terminate the connection. Item-and-byte bounded ingress and control
queues prevent a peer from creating unbounded work.

After login authentication, the server is the sole heartbeat initiator. It
sends one unpredictable eight-byte `Ping` after 30 seconds of send-idle,
allows only one outstanding nonce, requires the exact `Pong` within 10
seconds, and enforces 90 seconds of receive-idle. Unsolicited, duplicate, or
incorrect Pongs close the session.

Secure-path logs suppress packet hex, usernames, and remote endpoint values.
Metrics use only closed, low-cardinality endpoint/outcome/stage tags.

## Configuration

Secure listeners remain off unless all required settings validate:

```powershell
$env:GODSWAR_SECURE_ENABLED = 'true'
$env:GODSWAR_SECURE_LOGIN_BIND_HOST = '127.0.0.1'
$env:GODSWAR_SECURE_LOGIN_PORT = '6599'
$env:GODSWAR_SECURE_LOGIN_DNS_HOST = 'login.reborn.test'
$env:GODSWAR_SECURE_GAME_BIND_HOST = '127.0.0.1'
$env:GODSWAR_SECURE_GAME_PORT = '7443'
$env:GODSWAR_SECURE_GAME_DNS_HOST = 'game.reborn.test'
$env:GODSWAR_SECURE_CERTIFICATE_PATH = 'C:\private\reborn-development-server.pfx'
$env:GODSWAR_SECURE_CERTIFICATE_PASSWORD = '<private process value>'
```

The enabled secure development profile permits only literal loopback/private bind
addresses. Secure ports must be distinct from each other and both raw ports.
The certificate password is process-environment-only and is excluded from
JSON configuration. Startup loads and validates the complete certificate
policy before opening a secure listener.

`GODSWAR_SECURE_ALLOWED_ORIGIN_SHA256` is a comma-separated allowlist. Its
development default is the verified predecessor Origin:

```text
753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79
```

This allowlist is a build-compatibility gate, not player authentication.

Checked-in Slice 7 defaults are:

| Setting | Default |
| --- | --- |
| Secure enabled | `false` |
| Game route / TLS endpoint | `game.reborn.test:7000` / `game.reborn.test:7443` |
| Audience / server / permission | `reborn-game` / `100` / `EnterWorld (1)` |
| Ticket TTL / capacity | `60 s` / `1024` |
| PBKDF2 iterations / accepted stored range | `600000` / `100000..2000000` |
| KDF workers / queue / copied bytes | `min(CPU,16)` / `64` / `8192` |
| KDF admission / complete operation | `250 ms` / `5 s` |
| Registration / plaintext migration | `false` / `true` |

Ticket settings use `GODSWAR_SECURE_GAME_ROUTE_HOST`,
`GODSWAR_SECURE_GAME_ROUTE_PORT`, `GODSWAR_SECURE_GAME_AUDIENCE`,
`GODSWAR_SECURE_GAME_SERVER_ID`, `GODSWAR_SECURE_GAME_PERMISSIONS`,
`GODSWAR_SECURE_TICKET_TTL_SECONDS`, and
`GODSWAR_SECURE_TICKET_CAPACITY`. Authentication settings use the corresponding
`GODSWAR_AUTH_*` environment variables declared by `ServerOptions`.

The raw compatibility profile retains legacy login upsert and username-only
game admission, with versioned verifiers protected from raw overwrite. It is
available only while secure mode is disabled. Enabling secure mode does not
start either raw listener, so the secure profile has no raw authentication
bypass or downgrade path. The secure candidate route is now wired but remains
uninstalled, so secure mode must stay disabled until controlled Slice 8
acceptance verifies the complete client path.

## Development certificate workflow

Set a private process password, then generate into a new directory:

```powershell
$env:GODSWAR_SECURE_CERTIFICATE_PASSWORD = '<temporary private value>'
.\tools\NewDevelopmentNetworkCertificates.ps1 `
  -OutputDirectory 'C:\private\reborn-network-tls'
```

The generator:

- runs only on Windows;
- refuses to overwrite files or alter an existing directory/ACL;
- restricts the new directory to the current Windows identity;
- creates a short-lived RSA 3072 development root and RSA 2048 server leaf;
- places both required DNS SANs and the server-authentication EKU on the leaf;
- writes an encrypted PFX containing exactly one private server leaf and the
  public root chain, plus separate public root/leaf certificates; and
- records the exact root thumbprint and raw-certificate SHA-256.

It does **not** trust the root by default. A test that genuinely requires
Schannel platform trust must explicitly add `-InstallCurrentUserTrust`, must
retain the generated receipt, and must remove that exact root afterward:

```powershell
.\tools\RemoveDevelopmentNetworkTrust.ps1 `
  -ReceiptPath 'C:\private\reborn-network-tls\current-user-trust-receipt.json'
```

Cleanup refuses malformed receipts, unsupported stores, multiple matches, a
subject/hash mismatch, or a root the generator did not install. It never
targets LocalMachine trust.

Do not commit PFX/private-key material. `*.pfx`, `*.p12`, and `*.key` are
ignored as a second guard.

## Signed endpoint policy

The native candidate parses, verifies, and loads the bounded GWEM format in
the [signed-manifest specification](network-infrastructure-phase2-endpoint-manifest.md).
It uses Windows CNG ECDSA P-256/SHA-256 verification, strict network byte
order, injected current/next public keys, both compiled and installed sequence
floors, an injected clock, and a one-shot module-relative loader.

Candidate-bound public trust, 64-bit HKLM state handling, the guarded installer,
and exported route policy are implemented. Operational production/staging keys,
signed manifests, installed state, and private signing material remain absent.

## Verification

Server build and focused TLS checks:

```powershell
dotnet build GodswarServer.sln --configuration Release
dotnet run `
  --project tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj `
  --configuration Release -- `
  'Secure Phase 2 TLS mux transport'
dotnet run `
  --project tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj `
  --configuration Release -- `
  'Secure Phase 2 authenticated grant and principal flow'
dotnet run `
  --project tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj `
  --configuration Release -- `
  'Mutually exclusive raw or secure listener profile'
```

Native x86 build and checks:

```powershell
.\tools\BuildClientNetworkShim.ps1 -Configuration Release
.\client\network-shim\bin\Release\Win32\Godswar.NetShim.Checks.exe --offline
.\tools\TestSecureNetworkBundleTransaction.ps1
```

Offline mode performs no WinSock initialization, DNS lookup, listener,
connection, or Schannel socket handshake. It retains manifest, framing,
queue/pump, policy, and state-machine checks. The server TLS fixture uses a
flow-controlled named-pipe duplex stream and does not open a network listener.

On a controlled host, the full guarded shim test additionally performs two
deterministic clean builds, PE/export/dependency checks, loopback socket checks,
and isolated stock-delegation probes:

```powershell
.\tools\TestClientNetworkShim.ps1 -Configuration Release
```

Do not disable endpoint security to run the complete suite. If a host network
filter interferes with arbitrary loopback TLS, run the socket suite in a
disposable VM or dedicated test machine and keep offline mode as the local
gate.

Coverage includes protocol/manifest boundaries, candidate-bound trust,
activation interruption/path/floor forgery, TLS/ALPN/preface rejection, PFX
loading, bounded admission, authentication migration, ticket forgery/replay,
grant ordering, accepted game bind/principal attachment, exported secure
session lifecycle, and coherent listener startup.

These are local functional/security checks, not a production capacity claim or
proof of upstream DDoS protection.

## Rollback and remaining gates

There is no current live rollback action because secure listeners default off
and the candidate is uninstalled. Future Apply/Restore must use the guarded,
receipt-bound transaction in the
[Slice 8 runbook](network-infrastructure-phase2-slice8-activation.md).

Before any original-client activation:

1. supply reviewed operational manifest keys/floors and signed manifest;
2. take and verify an account-store backup, audit/reset blank credentials,
   and rehearse plaintext migration on a restored copy;
3. test the exact native Schannel-to-`SslStream` path with authorized trust and
   remove all temporary trust afterward;
4. run guarded Apply through a new backup/evidence checkpoint and complete the
   original-client parity/soak matrix; and
5. prove guarded Restore and that the enabled
   secure profile exposes no raw compatibility ingress.

Slice 9B's binding foundation now passes offline while gameplay remains on
TLS. The remaining Slice 9 work protects datagrams; Slice 10 moves movement
and snapshots with authenticated TLS fallback.
