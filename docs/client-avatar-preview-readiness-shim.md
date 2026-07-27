# Character-selection preview readiness shim

## Status

The first controlled-host candidate is rejected. Five apparent successes were
followed by three persistent blank character models, so it did not satisfy the
Phase 4 first-attempt preview gate. Its campaign remains unchanged under:

`C:\ProgramData\RebornSecureNetworkPhase4Docker`

PreviewReadyV1 and PreviewReadyV2 are also historical and read-only.
PreviewReadyV2 failed with a blank preview and dump `20260728001641.dmp`.
PreviewReadyV3 was then manually rejected after two intermittent failures: the
first was a null-resource `C0000005` dump and the second was an authenticated
pre-world disconnect with no dump. A later retry entered the world, but cannot
invalidate either first-attempt failure. V3 completed exact rollback and is
now frozen read-only.

PreviewReadyV4 added the first static timeout guard but was rejected after TLS
policy completed without a secure preface. PreviewReadyV5 narrowed that guard
and removed the controlled-host client's dependency on the CurrentUser root
store. It was rejected before authentication because live Net sent the stock
Origin identity while the server correctly allowed only the patched Origin.
V5 completed exact rollback at protected `handoff-000024.json`; both
generations are historical and read-only.

The active, incompletely accepted generation is `PreviewReadyV6`. It pairs:

- stock predecessor `Origin.exe`
  `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`;
- guarded candidate `Origin.exe`
  `E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C`;
  and
- deterministic `GWKEY02` `Net.dll`
  `2169589316DE3157F999563F80A3DFE9B73A120F73AFE1723D92338B816CAE97`.

V6's live Baseline is sealed `Pass`. Fallback, ten-minute Soak, and exact
rollback remain pending, so complete V6 acceptance is not claimed.

## Cause and correction

The server sends 63 ordered AfterLogin bootstrap records followed by one
character-preview record. PreviewReadyV2 treated each matching bootstrap as an
initialization trigger, so it invoked the native LOGIN initializer as the first
record was returned, before Origin consumed the remaining 62 records.

The preserved dump
`C:\RebornNetworkAcceptanceClient\Dump\20260728001641.dmp` has SHA-256
`271942FB737D57F24468369114C563ECECE281362FA64D26245A77D5720CDE36`.
It records x86 access violation `C0000005` at `EIP=0x005F58BC`, with `ECX=0`
and `ESI=0`, while avatar resource slot `0x015760A0` remained null. From that
dump and the observed wire order, the failure is attributed to the premature
bootstrap-time initialization leaving a partial native resource set. Changing
server packet timing is not a deterministic readiness contract.

PreviewReadyV3 corrected the ordering contract, and V4 through V6 retain it.
`AvatarPreviewGate`:

- recognizes only the exact AfterLogin and one-character preview records;
- returns every ordered AfterLogin bootstrap record unchanged without invoking
  native initialization;
- treats the following exact character-preview record as the initialization
  barrier;
- calls the original LOGIN object's initializer only on the client update
  thread and only after exact state, object, vtable, function, manager, patched
  runtime, PE, filename, and full-file-hash checks pass;
- accepts only the two manager vtables and dispatch entries recovered from the
  pinned client;
- invokes the native initializer at most once per selection-resource
  lifecycle;
- retains the exact preview pointer until all six native roots are non-null;
- blocks nested legacy polling and preview release while initialization is
  active;
- preserves message order and ownership across re-entrant message pumping,
  disconnect, reset, and same-transport character-selection re-entry; and
- keeps legacy `Process()` and the secure TLS/UDP workers running while a
  preview is held.

Unknown executables and any Origin that does not match the exact guarded V6
runtime and full-file hash remain pass-through and cannot invoke this
host-specific path.

V3 nevertheless exposed a separate stock timeout path. Its protected rejection
folder is:

`C:\Reborn\artifacts\controlled-host-acceptance\20260728-004030-preview-ready-v3\server-evidence\client-failure-20260728-011349`

The first failed start at `2026-07-28T01:13:25+12:00` produced
`20260728011349.dmp`, SHA-256
`18176B45640DADB220EA090D718927CB742029352405ACA71791183B3E280B7A`.
It records `C0000005` at `0x005F58BC` with selection-avatar slot
`0x015760A0` null. The second start at `01:13:59` authenticated but
disconnected before world entry without producing another dump. The retry at
`01:14:27` succeeded. The manual rejection JSON is SHA-256
`F6211A83FFCEDA06E9E634CFF8F83619E5A466B256B234C4D7ECFD06A0E43804`;
the Baseline profile JSON is
`F5234D4C20ECF769822F3E4418A5E20BA75A35D6FBF34F825C77AE02E1393A2A`,
and its privacy-safe evidence log is
`F8A2AC8AC2C8E44AEBAA6AB720184F6C7A616336C975B434DFE48EC11067A3CA`.

V4 adds only a static timeout guard to the audited predecessor Origin:

- the hook at RVA/file offset `0x1F58B6` checks all six roots
  `0x01576088`, `0x0157608C`, `0x01576090`, `0x0157609C`,
  `0x015760A0`, and `0x015760A4`;
- the all-ready branch replays the displaced root load and rejoins the
  untouched stock virtual-call path at `0x005F58BC`;
- the missing-root branch skips both unsafe virtual calls, preserves the stock
  EDI/state-2, retry-flag, and state-write side effects, then rejoins untouched
  cleanup at `0x005F58EA`; and
- the previously rejected preload hook at file offset `0x0C14D6` remains
  absent and its 154-byte cave remains zero.

Those offline checks proved V4's bytes and branches, not live recovery. V4
never completed the secure preface, so the generation supplied no accepted
live proof for that guard.

V5 retained the ordering barrier while narrowing the timeout guard to LOGIN
state 2 and the two roots dereferenced by the stock timeout path,
`0x015760A0` and `0x0157608C`. It also introduced exact embedded-development-
root validation without installing CurrentUser trust. V5's native probe passed
because the command line supplied the patched Origin identity, but live Net
still sent the duplicated stock identity and was correctly rejected.

V6 keeps the V5 guarded Origin and removes that identity split:

- the version-2 `GWKEY02` build contract carries the complete guarded-Origin
  SHA-256;
- both Net runtime and `OriginAvatarPreviewHost` consume that same contract;
  and
- the deterministic build and installer run a paired
  `--offline-origin-contract-probe` before mutation.

While a preview is held, a `NotInvoked` host request may retry; one actual
`InvokedNotReady` initializer call remains the lifecycle bound because partial
allocation is not proven idempotent.

The host-specific native code is split into
`client/network-shim/src/OriginAvatarPreviewHost.cpp`; all changed source and
test files remain below the repository's 20 KB maintainability limit.

## Reproducible public-trust build

Run:

```powershell
.\tools\BuildPhase4PreviewReadyNetworkShim.ps1 `
  -CandidateOriginPath (
    'C:\Reborn\artifacts\controlled-host-acceptance\' +
    '20260728-102640-preview-ready-v6\candidate\Origin.exe')
```

The script temporarily generates the embedded verification-only header from
the already preserved current and next public trust JSON files, performs two
clean deterministic builds, runs the full and offline native suites, verifies
the signed endpoint manifest, embedded build contract, and paired Origin
identity, and restores the checked-in placeholder header exactly. It never
opens, creates, exports, or requires a private signing key.

The historical PreviewReadyV1 fixture is:

`C:\Reborn\artifacts\controlled-host-acceptance\20260727-004151-preview-ready-v1\candidate`

The failed PreviewReadyV2 fixture remains unchanged at:

`C:\Reborn\artifacts\controlled-host-acceptance\20260727-185522-preview-ready-v2\candidate`

Its historical pins remain:

- `Net.dll`:
  `EFFC21D1500C39352ADEFB2B2D6388912A7EF50505BD3AD8CB043D32D7D956CE`
- `Godswar.NetShim.Checks.exe`:
  `237EA0A3B90A4642DADA1170B1A740B966984C8004B99698F752491EC6732187`

The rejected PreviewReadyV3 fixture is frozen at:

`C:\Reborn\artifacts\controlled-host-acceptance\20260728-004030-preview-ready-v3\candidate`

Historical V3 pins:

- `Net.dll`:
  `5FD6A0C37801A393689AF523854AD5BE258616BF52809D8FEA04437D34B7CA85`
- `Godswar.NetShim.Checks.exe`:
  `ABB81E184CA54DD9ECFFDC1F2DB690E122F81A4B394050AF4F7B6095FC34308B`
- signed endpoint manifest:
  `3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C`

The rejected PreviewReadyV4 fixture is frozen at:

`C:\Reborn\artifacts\controlled-host-acceptance\20260728-015732-preview-ready-v4\candidate`

Historical V4 pins:

- guarded `Origin.exe`:
  `1D1AA8768CC42655D4EF000237A301231B629D806FDCE99882C1D5888BBB3A5A`
- public-trust `Net.dll`:
  `D353E9215CE2F2E74A21C4C35FE356C15459FB7C1341FD01CA0618F575367D55`
- `Godswar.NetShim.Checks.exe`:
  `C5C8B7389F68F0C34E24EA2517A276DE912D92FAB9F0536544F15F9592934FB1`
- signed endpoint manifest:
  `3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C`

The rejected PreviewReadyV5 fixture is frozen at:

`C:\Reborn\artifacts\controlled-host-acceptance\20260728-031445-preview-ready-v5\candidate`

Historical V5 pins:

- guarded `Origin.exe`:
  `E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C`
- embedded-development-root `Net.dll`:
  `0A34613ED9E4F6AC82608DA17570D905579F44A37CC6B08CAC8AA75B1A6DAA1A`
- `Godswar.NetShim.Checks.exe`:
  `49FEA163D18F37BFC1C3DD604C15028CDE57B3404C6C3F92A969CA30E0879E52`
- signed endpoint manifest:
  `3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C`

The active PreviewReadyV6 fixture is:

`C:\Reborn\artifacts\controlled-host-acceptance\20260728-102640-preview-ready-v6\candidate`

Active V6 pins:

- guarded `Origin.exe`:
  `E177D94DC70CCF657D190C85B1EBACE5C8E790D52DBC014854E03A57234CC76C`
- `GWKEY02` `Net.dll`:
  `2169589316DE3157F999563F80A3DFE9B73A120F73AFE1723D92338B816CAE97`
- `Godswar.NetShim.Checks.exe`:
  `FD34DD6F8FBD518D55C3833FB7E33C5DC819FD546D6799B201CE43E2A7424F75`
- signed endpoint manifest:
  `3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C`

## Live acceptance and known limitation

The active V6 campaign uses the independent protected root:

`C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV6`

Campaign `5848b53f-24b4-4f11-a4fd-591c0c0e1a36` sealed its live Baseline
`Pass` in `1044.613001` seconds. Its protected log
`secure-server-20260727-224656-0460982.log` has SHA-256
`F8A2AC8AC2C8E44AEBAA6AB720184F6C7A616336C975B434DFE48EC11067A3CA`;
the profile has SHA-256
`F13470A46404401EA1AC7DFCCB0B7CE07CF0E13AB6AE7F1F27D11867491C3B16`.
It recorded listener readiness, TLS policy, accepted preface, TLS client
authentication, authenticated UDP binding, authoritative movement and
snapshot, and graceful stopping.

Baseline does not attest Fallback, Soak, or rollback. V6 must still complete
those gates without a blank preview, relaunch, false server-full or
server-unavailable dialog, crash, TLS/UDP regression, or message-order fault,
then reach exact `Restored` state. The six-root readiness criterion still does
not deep-validate every resource object; any remaining crash, missing preview,
pre-world disconnect, timeout, or reliance on a second launch is an immediate
stop-and-preserve condition.
