# Phase 2 Slice 8 guarded secure activation

## Status and boundary

Slice 8 is complete in source and offline verification. The exported x86
client shim now owns signed Login/Game route selection and the complete secure
session lifecycle. The server starts one coherent raw or TLS listener pair,
and guarded bundle tooling provides monotonic activation, exact backup, and
restore.

Nothing was activated live:

- no installed client file was changed;
- no 64-bit HKLM activation state or certificate trust was created;
- no account data, firewall, endpoint-security setting, or live listener was
  changed; and
- UDP remains absent.

The installed baseline remains:

| Artifact | State |
| --- | --- |
| `Origin.exe` | SHA-256 `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79` |
| `Net.dll` | stock SHA-256 `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C` |
| `NetLegacy.dll` | absent |
| `RebornNetwork.gwem` | absent |
| `HKLM\SOFTWARE\Reborn\NetworkManifest` (64-bit view) | absent |
| `%ProgramData%\RebornSecureNetworkBackups` | absent |

Secure server mode still defaults off. Phase 2 is not accepted until a
controlled host completes original-client TLS Login/Game/world entry and exact
restore.

## Activation contract

The native x86 process explicitly reads the 64-bit registry view:

| Item | Contract |
| --- | --- |
| Key | `HKLM\SOFTWARE\Reborn\NetworkManifest` with `KEY_WOW64_64KEY` |
| `ActivationMode` | `REG_DWORD`; `0=Disabled`, `1=SecureRequired` |
| `Environment` | `REG_DWORD`; `1=Development`, `2=Staging`, `3=Production` |
| `HighestAcceptedSequence` | `REG_QWORD`; nonzero in `SecureRequired` |

A missing key means explicit `Disabled`. Mode `0` selects raw pass-through and
does not consume environment or floor in the client. Mode `1` requires a known
environment, a nonzero monotonic floor, and a valid signed module-relative
manifest. Malformed types, values, manifest, key, time, or sequence fail
closed.

Installer writes require elevation and explicit `-AllowHklmWrite`. The key DACL
grants SYSTEM and Administrators full control and Users read-only access.
Restore retains the maximum of the current floor and the freshly verified
signed-manifest sequence; mutable receipt metadata cannot raise or lower it.
Runtime activation and manifest publication are process-lifetime one-shot, so
Origin must be fully restarted after any activation change.

## Exported client behavior

`NetClientCreate` initializes process activation before loading the stock DLL.
`DllMain` still only records the module and disables thread notifications.

In `SecureRequired`:

1. An exact signed logical login host/port becomes `Login`.
2. An exact route matching the pending authenticated grant becomes `Game`.
3. Every other route is rejected.
4. The only exception is the signed development-only legacy-passthrough flag.
   Staging and production contain no placeholder verification key or floor.

Secure and rejected logical external routes are never passed to the stock DLL.
Login establishes external TCP, Schannel with normal platform trust/name
validation, the secure preface, then the private stock loopback bridge. Game
claims the pending grant, connects to its authenticated TLS target, presents
the one-use bind, requires acceptance, and only then starts the stock bridge.
A secure failure never retries raw TCP.

`Process` detects bridge termination. `DisConnect` and `Release` stop and join
secure workers before stock teardown. The per-process client-instance ID comes
from the Windows CSPRNG. The compiled Origin hash is a compatibility assertion,
not remote attestation.

The loopback listener does not trust `127.0.0.1` alone. Before bridging, it
matches the reverse four-tuple in the bounded Windows TCP owner table and
requires the established client row to belong to the current Origin PID.
Foreign peers are closed and acceptance continues with an eight-rejection
work cap. Sustained local-process flooding can still deny one startup attempt;
it cannot take over the authenticated tunnel.

## Candidate and stock-DLL integrity

The candidate contains a read-only `.gwkey` PE section with a versioned,
bounded manifest-key build contract. Runtime verification reads the same
contract. The offline activation probe parses that contract directly from the
exact candidate file and verifies the signed manifest with its current or next
key. A mixed candidate/checks/manifest build therefore fails before install.

`NetLegacy.dll` is opened as a final normal file without write/delete sharing,
reparse points and directories are rejected, and SHA-256 is calculated through
the held handle. That handle remains open through constrained
`LoadLibraryExW` and export verification. Dependency search is limited to the
DLL directory and System32.

These checks do not defend against administrator, kernel, or an already
compromised Origin process.

## Server listener gate

`ServerListenerProfile` produces exactly one pair:

- raw Login and raw Game; or
- TLS Login and TLS Game.

Both roles use the same transport and distinct valid ports. Startup requires
exactly two endpoint servers, both must report ready within ten seconds, and
either endpoint fault cancels and drains the shared server lifetime. Secure
mode exposes no raw compatibility listener.

## Guarded bundle transaction

`InstallSecureNetworkBundle.ps1` reports `Stock`, `InstalledExact`, or
`RecoverablePartial`. Apply:

1. Requires Origin closed and serializes installer mutations.
2. Requires canonical local paths with no reparse component. For an HKLM
   mutation, the client directory and every existing managed client file must
   have a trusted owner and must not be writable, replaceable, or deletable by
   a nonprivileged principal.
3. Uses `%ProgramData%\RebornSecureNetworkBackups` by default. Backup and
   staging directories are created atomically with inheritance disabled and
   Administrators/SYSTEM-only access. Restore accepts only the exact issued
   direct-child backup naming form.
4. Pins exact Origin, stock Net, candidate, checks executable, manifest, and
   trust SHA-256 values.
5. Copies executable inputs into a new Administrators/SYSTEM-only staging
   directory, verifies each copy, and retains no-write/no-delete handles
   through child execution and candidate loading.
6. Runs the native offline suite, candidate-bound manifest probe, and isolated
   stock-delegation probe. Sockets require explicit
   `-ControlledHostSocketChecks`.
7. Creates a checksummed schema-2 receipt and an exact stock `Net.dll` backup.
   Exact file names, policy, manifest metadata, predecessor state, hashes, and
   absence of predecessor `NetLegacy.dll`/manifest are validated on restore.
8. Acquires an exclusive Origin image handle, then rechecks the process while
   holding it.
9. Advances the irreversible floor while activation remains `Disabled`.
10. Atomically installs and verifies the manifest, predecessor DLL, and
   candidate DLL.
11. Commits `SecureRequired` only after every staged file validates.

An exception disables routing, retains the signed monotonic floor, and restores
the exact predecessor. The offline suite injects failures after floor,
manifest, legacy, and candidate stages. It also rejects manifest tampering,
receipt path traversal, recomputed receipt checksums, and forged maximum-floor
metadata. A power loss before the final activation commit leaves routing
disabled and a durable backup receipt for recovery.

Restore disables routing first, retains the monotonic floor from current state
and the freshly verified signed manifest, restores the pinned stock DLL,
removes only the exact absent predecessor files, and validates the result.

## Development signing workflow

The checked-in development coordinates are public-only non-operational
placeholders; their private halves were discarded. The explicit workflow is:

```powershell
.\tools\ManageDevelopmentEndpointManifestKeys.ps1 -Mode Create

.\tools\NewDevelopmentEndpointManifest.ps1 `
  -KeySlot Current `
  -Sequence 1
```

The manager creates current and next non-exportable CurrentUser CNG ECDSA P-256
keys. It writes only a public generated header plus separate current/next trust
descriptors, rolls public artifacts back if creation fails, and inspects
existing key algorithm/usage/export policy in `Status`. Rebuild the candidate
after generating its public contract. The manifest generator can sign with
either `D001` current or `D002` next and verifies its output.

The trust JSON is installer-side validation material; runtime trust is in the
candidate `.gwkey` contract. Manifest-signing trust and TLS certificate trust
are separate.

Temporary custom-named CNG keys were created solely to validate current/next
generation and candidate binding in an isolated copied tree, then explicitly
deleted. No default operational, staging, or production key exists, and no
private key is committed.

## Offline verification

```powershell
.\tools\BuildClientNetworkShim.ps1 -Configuration Release
.\client\network-shim\bin\Release\Win32\Godswar.NetShim.Checks.exe --offline
.\tools\TestSecureNetworkBundleTransaction.ps1

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

Current results:

- native Release `/W4 /WX`: passed;
- native offline and isolated stock-delegation probes: passed;
- candidate `.gwkey` parsing: passed;
- isolated copied-tree candidate plus matching signed-manifest positive probe:
  passed;
- mismatched candidate/manifest rejection: passed;
- retained-handle execution/probe round trip: passed;
- bundle apply/restore/interruption/path-forgery/floor-forgery/tamper suite:
  passed;
- .NET Release build: zero warnings and zero errors; and
- password, TLS mux, authenticated grant/principal, and coherent listener
  checks: passed.

Not executed against the live machine: operational key creation, certificate
trust installation, HKLM Apply/Restore, secure server startup, the full socket
suite, account migration, or original-client smoke.

## Controlled-host acceptance

Use a disposable VM or dedicated host; never disable Norton or other endpoint
security.

1. Take and verify an account-store backup and rehearse restore.
2. Move the client to, or explicitly harden, a canonical non-reparse directory
   owned by SYSTEM, Administrators, or TrustedInstaller. Remove
   nonprivileged write/delete/change-permission rights from the directory and
   managed files. The current `C:\Godswar Origin` ACL deliberately fails this
   gate because `Authenticated Users` has Modify; no ACL was changed here.
3. Close all related processes and reboot the controlled host after the
   separate ACL preparation so no predecessor write/delete handle survives.
   Confirm protected backup-directory creation and Apply fail closed before
   enabling a listener.
4. Audit/reset blank credentials on a restored copy.
5. Create operational manifest keys, regenerate the public contract, rebuild,
   and sign a higher-sequence manifest.
6. Supply a valid TLS PFX/DNS path and explicitly authorized temporary trust.
7. Run the full socket suite, including foreign-loopback rejection.
8. Retain the guarded Apply receipt.
9. Enable the secure server profile and verify only `6599/7443` listen, never
   raw `5999/7000`.
10. Test original-client login, grant-before-redirect, bind, world entry,
   account switching, disconnect/reconnect, parity, and soak.
11. Guarded Restore must reproduce exact predecessor files and remove exact
   temporary trust.

## UDP timeline

Slice 9B's bounded cookie-plus-TLS endpoint-binding foundation now passes
offline, but its listener and capability remain inactive. Remaining Slice 9
work adds reviewed AEAD, sequence/replay windows, key epochs, NAT rebinding,
keepalive, pacing, and production metrics. Gameplay stays on TLS and
UDP-blocked clients fall back to authenticated TLS.

Slice 10 / Phase 4 moves the first gameplay slice: sequenced movement meaning
for opcode `10194`, authoritative snapshots/keyframes, reconciliation,
transport epochs and deduplication, TLS fallback, and network-emulation tests.
