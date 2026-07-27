# PreviewReadyV5 controlled-host candidate

PreviewReadyV5 is a rejected Phase 4 candidate. It preserved rejected V4 as
immutable history and removed the native client's dependency on the Windows
CurrentUser root store, but its first live attempt failed before
authentication.

## Pinned artifacts

| Artifact | SHA-256 |
|---|---|
| `Origin.exe` | `E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C` |
| `Net.dll` | `0A34613ED9E4F6AC82608DA17570D905579F44A37CC6B08CAC8AA75B1A6DAA1A` |
| `Godswar.NetShim.Checks.exe` | `49FEA163D18F37BFC1C3DD604C15028CDE57B3404C6C3F92A969CA30E0879E52` |
| `RebornNetwork.gwem` | `3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C` |
| embedded development root | `911E3CF444B631AAB9EDCC5980DF65243CAAC42B9000C5E2410C7DADFEB54DED` |

The immutable candidate is under
`C:\Reborn\artifacts\controlled-host-acceptance\20260728-031445-preview-ready-v5`.
The protected campaign root is
`C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV5`.

## Native TLS policy

Production manifests retain Schannel automatic certificate validation and
chain revocation checks. The controlled-host Development manifest uses manual
validation against the exact embedded public development root. CryptoAPI still
performs chain construction, current-time validation, server-auth EKU policy,
and `login.reborn.test` or `game.reborn.test` hostname validation. The code
also requires the existing RSA/SHA-256 policy and revalidates the remote
certificate after a TLS 1.3 continuation.

The development mode deliberately performs no revocation lookup because this
short-lived localhost certificate has no revocation distribution point.
Changing the Development certificate authority therefore requires rebuilding
the client candidate with the new public root. It never requires installing a
root in a Windows trust store.

The current leaf expires at `2026-08-09T02:21:27Z`; the embedded root expires
at `2026-08-10T02:21:27Z`. This candidate must not be used after those
deadlines. A later reusable candidate must rotate the certificate pair and
embedded public root together.

## Machine proof before Origin

With secure Docker healthy and still allowing the stock predecessor Origin
identity, run:

```powershell
$checks = (
  'C:\Reborn\artifacts\controlled-host-acceptance\' +
  '20260728-031445-preview-ready-v5\candidate\' +
  'Godswar.NetShim.Checks.exe')
& $checks --controlled-host-tls-probe `
  753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79
if ($LASTEXITCODE) {
  throw 'Native TLS and secure-preface probe failed.'
}
```

This passed with no matching certificate in CurrentUser Root or TrustedPeople:
TLS 1.3 (`8192`), `TLS_AES_128_GCM_SHA256` (`4865`), and an accepted secure
preface. The probe injected its Origin identity from the command line instead
of reading the identity used by live Net runtime, so it did not exercise the
failed boundary. The checksummed receipt is
`diagnostics\native-tls-preface-probe.json` in the V5 fixture.

Run the probe again with the V5 candidate Origin hash after starting the
foreground acceptance server and before opening `Launch.exe`. A probe pass
proves the native transport boundary; it does not replace live Origin,
character-selection, world-entry, Fallback, or Soak acceptance.

## Apply and rollback policy

`ManagePhase4SecureDockerClient.ps1` now reports
`TlsTrustMode=EmbeddedDevelopmentRoot`. In both stock and installed V5 states,
`RootState` must remain `Absent`. Apply and Restore may modify only the
receipt-bound disposable client, exact hosts mapping, narrow patcher-log ACL,
and monotonic activation state. They must not modify Norton, firewall rules,
network adapters, routes, or a Windows certificate store.

V4 failed before the secure preface, but its root was installed during that
attempt. Missing Windows trust is therefore not established as V4's cause.
V5 fixed a separately reproduced native trust-store dependency and narrowed
the Origin retry guard to state 2 plus the two stock-dereferenced avatar
roots.

V5 was rejected because live Net runtime sent the stock Origin SHA-256
`753BE49F...ED79`, while the foreground server correctly allowed only patched
Origin `E177D94D...CC76C`. The native probe masked this mismatch through its
command-line identity injection. The disposable client was restored exactly;
protected `handoff-000024.json` is the terminal V5 handoff. V6 moves the
Origin identity into the embedded build contract and validates the paired
files before installation.
