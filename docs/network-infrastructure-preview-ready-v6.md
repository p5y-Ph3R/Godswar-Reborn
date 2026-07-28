# PreviewReadyV6 controlled-host candidate

PreviewReadyV6 is the accepted Phase 4 controlled-host candidate. It preserves
rejected V5 and its exact `handoff-000024.json` restore as immutable evidence.
Final campaign `0a73fd79-961b-42c7-82cc-9e4a6f9e3355` passed Baseline,
forced Fallback, the ten-minute Soak, exact rollback, and the protected
completion-receipt gate. Viewer parity was recorded `Unavailable`.

## Pinned artifacts

| Artifact | SHA-256 |
|---|---|
| `Origin.exe` | `E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C` |
| `Net.dll` | `2169589316DE3157F999563F80A3DFE9B73A120F73AFE1723D92338B816CAE97` |
| `Godswar.NetShim.Checks.exe` | `FD34DD6F8FBD518D55C3833FB7E33C5DC819FD546D6799B201CE43E2A7424F75` |
| `RebornNetwork.gwem` | `3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C` |
| embedded development root | `911E3CF444B631AAB9EDCC5980DF65243CAAC42B9000C5E2410C7DADFEB54DED` |

The immutable candidate is under
`C:\Reborn\artifacts\controlled-host-acceptance\20260728-102640-preview-ready-v6`.
The protected campaign root is
`C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV6`.

## V5 rejection and V6 boundary

V5 reached the live foreground server but failed immediately before
authentication. Its Net runtime sent the stock Origin identity
`753BE49F...ED79`; the server correctly accepted only the installed patched
Origin identity `E177D94D...CC76C` and rejected the secure preface as an
unsupported build. The native TLS/preface probe passed because it received
the patched identity as a command-line argument, bypassing the runtime
constant. That probe therefore proved TLS policy but masked the live
client-to-preface binding defect.

V6 removes the duplicated runtime identity:

- the version-2 `GWKEY02` embedded contract carries the complete 32-byte
  allowed Origin SHA-256;
- Net runtime and the preview guard consume that contract identity;
- the deterministic public-trust build can verify a supplied candidate
  Origin; and
- the installer runs the paired
  `--offline-origin-contract-probe <Net.dll> <Origin.exe>` gate before
  mutation.

The server allowlist remains narrow. It is not widened to admit the stock
Origin as a workaround.

## Native TLS policy

The manifest and embedded development root are unchanged from V5. Production
manifests retain Schannel automatic certificate validation and chain
revocation checks. The controlled-host Development manifest validates the
exact embedded public root without installing it in a Windows trust store.
CryptoAPI still enforces chain construction, current-time validity,
server-auth EKU, hostname, RSA, and SHA-256 policy.

The leaf expires at `2026-08-09T02:21:27Z`; the embedded root expires at
`2026-08-10T02:21:27Z`. V6 must not be used after those deadlines. A later
candidate must rotate the certificate pair and embedded public root together.

## Required machine proof before Origin

Run the paired offline contract probe against the immutable V6 fixture:

```powershell
$root = (
  'C:\Reborn\artifacts\controlled-host-acceptance\' +
  '20260728-102640-preview-ready-v6\candidate')
$checks = Join-Path $root 'Godswar.NetShim.Checks.exe'
$net = Join-Path $root 'Net.dll'
$origin = Join-Path $root 'Origin.exe'

& $checks --offline-contract-probe $net
if ($LASTEXITCODE) {
  throw 'Embedded Net build-contract probe failed.'
}
& $checks --offline-origin-contract-probe $net $origin
if ($LASTEXITCODE) {
  throw 'Paired Origin/Net build-identity probe failed.'
}
```

After the foreground acceptance server starts, run the bounded TLS/preface
probe using the V6 Origin hash. A pass proves the native transport boundary;
it does not replace live Origin, character selection, world entry, Fallback,
Soak, or exact rollback.

## Sealed live acceptance

All paths below are beneath the fixture's `server-evidence` directory.

| Profile | Duration | Events | Profile result and SHA-256 |
|---|---:|---:|---|
| Baseline | `94.6714105` s | 9 | `secure-server-20260728-020955-8076984.profile.json` — `709FFA78D5C3ED0DA11417BBC70ACDE66BD0CC718FD55B15E871983C939CA066` |
| Fallback | `112.2090199` s | 14 | `secure-server-20260728-021202-8420702.profile.json` — `DEACA3589559D54E9C038B432AF76657188F7FF0253F7E1F6AD3721A89EFFDAD` |
| Soak | `661.5843391` s | 9 | `secure-server-20260728-021422-2616795.profile.json` — `94C877DAF402ED0AEC61C5F1AC7CCE042CC3F61EE1DFC7676C979368475193B6` |

Baseline and Soak evidence have SHA-256
`F8A2AC8AC2C8E44AEBAA6AB720184F6C7A616336C975B434DFE48EC11067A3CA`;
Fallback evidence has
`FB387D34A0EC59A4029BCA10F5FA9B4BA6350CE222CDA15DEF252351CDE36F56`.
Every profile pins server
`8B3E313475E4EB9FE60E8917AF6BB8E7416F809C8B17150D8C3B21357F2EF8E3`
and managed release set
`0460C408F92F3817478E71A9FD6EA1C17E10C4AC4BF3624D7236295940499EC5`.

Fallback recorded logical ACK loss, one-way TLS fallback, authoritative
`not_ready` correction, and post-fallback TLS movement with
`no_switchback=true`. The fixed Soak remained secure through `661.5843391`
seconds, beyond the former 30-second idle-expiry defect.

## Apply and rollback policy

`ManagePhase4SecureDockerClient.ps1` must report
`TlsTrustMode=EmbeddedDevelopmentRoot`, and `RootState` must remain `Absent`
in stock and installed states. Apply and Restore may modify only the
receipt-bound disposable client, exact hosts mapping, narrow patcher-log ACL,
and monotonic activation state. They must not modify Norton, firewall rules,
network adapters, routes, or a Windows certificate store.

The client finished at exact `Restored`: stock Origin/Net, absent
NetLegacy/manifest/root/private keys, original hosts restored with managed
mappings absent, activation mode `0`, and healthy Docker with zero restarts.
The protected receipt is
`completion-0a73fd79-961b-42c7-82cc-9e4a6f9e3355.json`, SHA-256
`5EB6E369652605CA58A0D5CE2F01604268FAA2CE9A1323A4346F7DBFA15F4A6F`.
This closes the local Phase 4 gate; it is not a production capacity or
upstream-DDoS-readiness claim.
