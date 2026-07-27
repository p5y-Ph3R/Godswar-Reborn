# PreviewReadyV6 controlled-host candidate

PreviewReadyV6 is the active Phase 4 candidate. It preserves rejected V5 and
its exact `handoff-000024.json` restore as immutable evidence. The live
Baseline profile is sealed `Pass`. Fallback, Soak, and mandatory rollback
remain open acceptance gates, so V6 is not yet fully accepted.

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

## Sealed live Baseline

Campaign `5848b53f-24b4-4f11-a4fd-591c0c0e1a36` completed its live Baseline
profile in `1044.613001` seconds. Its protected evidence is:

- `server-evidence\secure-server-20260727-224656-0460982.log`, SHA-256
  `F8A2AC8AC2C8E44AEBAA6AB720184F6C7A616336C975B434DFE48EC11067A3CA`;
- `server-evidence\secure-server-20260727-224656-0460982.profile.json`,
  SHA-256
  `F13470A46404401EA1AC7DFCCB0B7CE07CF0E13AB6AE7F1F27D11867491C3B16`.

The profile recorded all nine required Baseline events: privacy-safe evidence
startup, secure-listener readiness, TLS policy, accepted preface response,
TLS client authentication, authenticated UDP binding, authoritative UDP
movement, queued authoritative UDP snapshot, and graceful server stopping.
This seals Baseline only; it does not attest Fallback, Soak, or rollback.

## Apply and rollback policy

`ManagePhase4SecureDockerClient.ps1` must report
`TlsTrustMode=EmbeddedDevelopmentRoot`, and `RootState` must remain `Absent`
in stock and installed states. Apply and Restore may modify only the
receipt-bound disposable client, exact hosts mapping, narrow patcher-log ACL,
and monotonic activation state. They must not modify Norton, firewall rules,
network adapters, routes, or a Windows certificate store.

V6 remains incompletely accepted until Fallback and Soak pass and the campaign
reaches exact `Restored` state. Any failed or interrupted remaining attempt
requires the documented secure-Docker recovery and mandatory receipt-bound
Restore.
