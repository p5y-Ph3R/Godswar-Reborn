# Character-selection preview readiness shim

## Status

The first controlled-host candidate is rejected. Five successful retries were
followed by three persistent blank character models, so it does not satisfy the
Phase 4 first-attempt preview gate. Its historical campaign and evidence remain
unchanged under:

`C:\ProgramData\RebornSecureNetworkPhase4Docker`

PreviewReadyV1 is now a historical, read-only campaign generation. The active
receipt-bound candidate is `PreviewReadyV2`; it retains the preview-readiness
scope for the exact predecessor `Origin.exe` SHA-256:

`753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`

## Cause and correction

The server sends one character-preview record immediately after the login
records. The predecessor client can consume that preview before all six native
male/female selection-avatar resource roots are populated. Its existing
fail-closed builder guards then skip the only preview, leaving a blank model.
Changing server packet timing is not a deterministic readiness contract.

`AvatarPreviewGate` now:

- recognizes only the exact AfterLogin and one-character preview records;
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

Unknown executables and the stock unpatched Origin remain pass-through and
cannot invoke this host-specific path.

The host-specific native code is split into
`client/network-shim/src/OriginAvatarPreviewHost.cpp`; all changed source and
test files remain below the repository's 20 KB maintainability limit.

## Reproducible public-trust build

Run:

```powershell
.\tools\BuildPhase4PreviewReadyNetworkShim.ps1
```

The script temporarily generates the embedded verification-only header from
the already preserved current and next public trust JSON files, performs two
clean deterministic builds, runs the full and offline native suites, verifies
the signed endpoint manifest and embedded build contract, and restores the
checked-in placeholder header exactly. It never opens, creates, exports, or
requires a private signing key.

The historical PreviewReadyV1 fixture is:

`C:\Reborn\artifacts\controlled-host-acceptance\20260727-004151-preview-ready-v1\candidate`

The active fixture is:

`C:\Reborn\artifacts\controlled-host-acceptance\20260727-185522-preview-ready-v2\candidate`

Pinned hashes:

- `Net.dll`:
  `EFFC21D1500C39352ADEFB2B2D6388912A7EF50505BD3AD8CB043D32D7D956CE`
- `Godswar.NetShim.Checks.exe`:
  `237EA0A3B90A4642DADA1170B1A740B966984C8004B99698F752491EC6732187`
- signed endpoint manifest:
  `3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C`

## Live acceptance and known limitation

The new campaign uses the sibling root:

`C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV2`

It must pass five first-attempt alternating-account cold cycles, fallback, and
at least ten minutes of soak without a blank preview, relaunch, server-full or
server-unavailable dialog, crash, TLS/UDP regression, or message-order fault.

The readiness criterion is the established six non-null roots; it does not
deep-validate every resource object. If the native initializer throws or
returns with a root still null, the shim deliberately does not retry a
potentially non-idempotent partial allocation. The preview remains held, and
the predecessor client still has the known later null-root timeout/crash path
at `0x005F58BC`. A live partial-initialization failure is therefore an immediate
stop-and-preserve-dump condition, not a recoverable result. Production
completion still requires a separately audited safe failure transition or
guard if that path is observed.
