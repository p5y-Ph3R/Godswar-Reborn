# B14 raw authentication retirement

Status: complete at the application/configuration boundary on 2026-07-31.
This is not a production deployment, capacity, trust, or DDoS-protection
claim.

## Outcome

B14 makes legacy raw authentication a deliberate local rollback instead of a
default server behavior:

- `appsettings.json` and `appsettings.docker.json` set
  `authentication.allowLegacyRawAuthentication` to `false`;
- raw startup requires `LocalDevelopment`, raw TCP, and the explicit
  `GODSWAR_AUTH_ALLOW_LEGACY_RAW_AUTHENTICATION=true` capability;
- default `docker compose up` does not create the raw server;
- the raw Docker server requires `--profile legacy-raw`, is labelled
  `legacy-raw-local-development`, and publishes only loopback host ports;
- the secure Docker overlay requires `--profile secure`, explicitly disables
  legacy raw authentication, and exposes only TLS plus authenticated UDP;
- `Production` rejects raw TCP, JSON storage, and plaintext credential
  migration; and
- login packet credential bytes are cleared after both secure and raw
  attempts.

The rollback path remains unsafe by design. It retains plaintext-compatible
login and username-only game binding for the unmodified client and must never
be internet-facing or represented as production authentication.

## Enforced startup matrix

| Profile and transport | Legacy option | Result |
| --- | --- | --- |
| `LocalDevelopment`, raw TCP | `false` or omitted | Rejected with `legacy_raw_authentication_disabled` |
| `LocalDevelopment`, raw TCP | `true` | Accepted only as the controlled rollback |
| `LocalDevelopment`, secure TLS | `false` | Accepted |
| Any secure TLS profile | `true` | Rejected with `legacy_raw_authentication_scope_invalid` |
| `Production`, configured PostgreSQL, secure TLS | `false`; plaintext migration false | Accepted |
| `Production`, any raw TCP | Any | Rejected with `raw_transport_forbidden` |

Production with `allowPlaintextMigration=true` is rejected with
`plaintext_migration_forbidden`. `LocalDevelopment` secure mode may retain
that option while development credentials are migrated.

`ServerRuntimeProfilePolicy` owns the matrix.
`LegacyAuthenticationAccess.Create` independently verifies that its capability
belongs to a validated local raw profile. `LoginClientHandler` and
`GameClientHandler` retain capability checks around the two legacy store
operations. Secure game admission remains bound to the account identity in a
single-use server ticket; it does not fall back to username lookup.

## Docker operation

The only checked-in raw Docker activation is explicit and loopback-published:

```powershell
docker compose `
  -f docker-compose.yml `
  --profile legacy-raw `
  up --build -d server
```

This path is for controlled local compatibility only. Stop it before starting
secure networking.

The secure profile requires the local certificate/password inputs documented
in the
[secure Docker runbook](network-infrastructure-secure-docker.md):

```powershell
docker compose `
  --env-file .env.secure.local `
  -f docker-compose.yml `
  -f docker-compose.secure.yml `
  --profile secure `
  up --build -d server
```

Default Compose intentionally starts no game-server service:

```powershell
docker compose up --build -d
```

That command may start PostgreSQL, but a game server requires an explicit
`legacy-raw` or `secure` profile.

## Secure-client acceptance evidence

PreviewReadyV6 previously passed the disposable original-client Baseline,
forced one-way TLS Fallback/correction, authoritative UDP movement, and
`661.5843391`-second Soak. Exact rollback restored the stock fixture. Its
protected completion receipt is
`completion-0a73fd79-961b-42c7-82cc-9e4a6f9e3355.json`; see the
[controlled-host acceptance](network-infrastructure-controlled-host-acceptance.md).

The accepted pair is guarded `Origin.exe`
`E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C`
plus `GWKEY02` `Net.dll`
`2169589316DE3157F999563F80A3DFE9B73A120F73AFE1723D92338B816CAE97`.
Separately, B14 produced and offline-verified this exact playable-client pair:

| Artifact | SHA-256 |
| --- | --- |
| playable `Origin.exe` | `7FB43C8D6BBA42CE533EE4CB78075CA88D3D6C11F2F79224C56A8A4F50BA07F9` |
| stock legacy `Net.dll` input | `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C` |
| deterministic secure `Net.dll` | `A26096B038C2C9B01FCB0023FF9E6F4A8FB49598CD6C67C5D287AAA485D50AA4` |
| native checks | `1ADBB0A0029E673A4F5FEBA6EDE87B50BF6B4914AE608A7AAD472094F82E569F` |

Two clean builds matched. The native offline suite, signed-manifest probe,
embedded-contract probe, and exact Origin-identity probe passed. The
build-scoped generated header was restored exactly. Nothing was installed and
the playable client has not yet repeated live original-client acceptance with
this new pair.

An allowlisted Origin hash is compatibility metadata used to reject a
mismatched binary pair. It is not client authentication, anti-cheat, or a
security identity. Account authentication still depends on TLS, the password
verifier, and server-issued tickets.

## Verification

The B14 gate consists of:

```powershell
dotnet build GodswarServer.sln --configuration Release --no-restore --nologo
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll "Fail-closed server runtime"
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll "Local-development-only legacy authentication"
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll "Secure Phase 2"
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll
powershell -NoProfile -File tools/TestSecureDockerProfile.ps1
powershell -NoProfile -File tools/TestPhase4SecureDockerRuntimeBindings.ps1
powershell -NoProfile -File tools/TestPhase4SecureDockerClientBundle.ps1
powershell -NoProfile -File tools/TestPhase4SecureDockerClientCampaign.ps1
powershell -NoProfile -File tools/TestSecureClientOriginIdentity.ps1
powershell -NoProfile -File tools/TestSecureNetworkOriginContractGate.ps1
git diff --check
```

The profile checks cover disabled-by-default raw startup, invalid secure/raw
option combinations, malformed environment values, listener exclusivity,
capability denial before store access, and credential-buffer clearing. Secure
Phase 2 covers bounded TLS framing, authentication, single-use ticket
forgery/replay/expiry behavior, principal binding, and disconnect lifecycle.
The Docker check renders default, raw, and secure Compose profiles and verifies
profile selection, loopback exposure, and absence of raw ports in secure mode.

The PreviewReadyV6 original-client tests are immutable offline/receipt evidence
unless the controlled-host runbook is deliberately executed. No live original
client, production certificate, production database, public listener,
firewall, network adapter, or paid infrastructure is changed by the ordinary
B14 gate.

The exact read-only playable-client pairing workflow was:

```powershell
$origin = 'C:\Godswar Origin\Origin.exe'
$legacyNet = 'C:\Godswar Origin\Net.dll'
if ((Get-FileHash $origin -Algorithm SHA256).Hash -cne `
    '7FB43C8D6BBA42CE533EE4CB78075CA88D3D6C11F2F79224C56A8A4F50BA07F9') {
  throw 'Playable Origin identity changed.'
}
if ((Get-FileHash $legacyNet -Algorithm SHA256).Hash -cne `
    '1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C') {
  throw 'Legacy Net identity changed.'
}
.\tools\TestSecureClientOriginIdentity.ps1
$pair = .\tools\BuildPhase4PreviewReadyNetworkShim.ps1 `
  -LegacyDllPath $legacyNet `
  -CandidateOriginPath $origin
if ($pair.CandidateSha256 -cne `
      'A26096B038C2C9B01FCB0023FF9E6F4A8FB49598CD6C67C5D287AAA485D50AA4' -or
    $pair.NativeChecksSha256 -cne `
      '1ADBB0A0029E673A4F5FEBA6EDE87B50BF6B4914AE608A7AAD472094F82E569F' -or
    $pair.OriginSha256 -cne `
      '7FB43C8D6BBA42CE533EE4CB78075CA88D3D6C11F2F79224C56A8A4F50BA07F9') {
  throw 'Playable secure pair changed.'
}
```

Final verification produced a zero-warning Release build and `258/258`
managed protocol checks. The default sealed native shim rebuilt under
`/W4 /WX` and passed its offline and embedded-contract probes. A bounded
Docker reference client then passed TLS login/authentication/ticket issue,
TLS game binding, authenticated UDP binding, world entry, authoritative
movement, and snapshot acknowledgement against the secure profile. The
server was subsequently recreated as the explicit `legacy-raw` profile and
was healthy with zero restarts on the two fixed loopback publications.

The first whole-suite run exposed a one-time tiered-JIT allocation in the UDP
decoder test harness. Its ratchet now permits at most three bounded warmup
batches but still requires a final 10,000-decode batch with exactly zero
managed allocations. Five focused runs and the final whole-suite run passed.

## Observability

Existing bounded signals remain:

- `godswar.server.startup.rejections{reason}`;
- `godswar.server.legacy_auth.attempts{endpoint,outcome}`; and
- secure authentication, ticket, UDP-validation, replay, and disconnect
  metrics documented by the networking and B13 operational records.

They contain no credential, ticket, packet payload, account ID, username, IP,
or session value as a metric label.

## Rollback

No database migration or durable player row changed. If the secure client pair
is unavailable, stop the secure server and activate the explicit loopback
`legacy-raw` profile shown above. Do not change either checked-in
`allowLegacyRawAuthentication: false` default, permit raw in `Production`, or
publish raw ports on a non-loopback host.

## Known limitations and next work

- TLS tickets and authenticated UDP session state are process-local. B15 adds
  the PostgreSQL player-ownership fence; cross-instance routing remains a B17
  decision after B16.
- The playable `7FB43C8D...BA07F9` pair is implemented and offline-verified,
  but is not installed and has not passed a new live original-client
  acceptance campaign. The raw rollback remains available until that gate.
- A production database must already contain verifier-backed credentials, or
  be deliberately migrated/reset in a controlled environment, before
  plaintext migration is disabled. B14 supplies no production credential
  migration tool or deployment.
- Production certificate issuance/rotation, secret distribution, edge
  routing, origin shielding, capacity testing, and upstream arbitrary
  TCP/UDP DDoS protection are not supplied or claimed here.

The next dependency-ordered roadmap ticket is **B15: PostgreSQL player
ownership fence**.
