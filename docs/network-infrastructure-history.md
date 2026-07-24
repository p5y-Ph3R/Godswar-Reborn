# Secure network infrastructure document history

This is the version ledger for
[`network-infrastructure-goal.md`](network-infrastructure-goal.md).

- `1.0` (`2026-07-24`): Captured the selected in-process client approach,
  TCP/UDP target architecture, threat boundaries, transport split, DDoS
  responsibilities, phased gates, and Phase 1 verification/rollback contract.
- `1.1` (`2026-07-24`): Recorded the verified Phase 1 shim hash, installed
  state, exact Apply backup, and completed automated Apply/Restore gates.
- `1.2` (`2026-07-24`): Made native release output reproducible, required two
  matching clean-build hashes, reinstalled the deterministic shim, and updated
  the exact rollback reference.
- `1.3` (`2026-07-24`): Enforced and tested a `0x50000000` preferred image base
  so the shim cannot claim stock `NetLegacy.dll`'s `0x10000000` address, then
  repinned and reinstalled the final deterministic build.
- `1.4` (`2026-07-24`): Specified the in-process Phase 2 bridge and ticket
  handoff, made target/current trust explicit, moved decoder security gates
  into their owning phases, pinned the build toolchain, added negative-path
  test coverage, and made interactive acceptance reproducible.
- `1.5` (`2026-07-24`): Pinned the Phase 2 Schannel/`SslStream` TLS contract,
  opaque legacy-stream framing, authenticated redirect ordering, single-use
  game-ticket binding, bounded resources/deadlines, credential migration,
  verification slices, and exact rollback target. Runtime remains disabled
  until Phase 1 interactive acceptance.
- `1.6` (`2026-07-24`): Added a bounded, read-only Phase 1 evidence recorder
  with checksummed manifests/observations, exact loaded-module verification,
  five-launch alternation, dump comparison, and enforced stock rollback plus
  final-reapply proof. The audit found no post-install client run, so the gate
  remained pending.
- `1.7` (`2026-07-24`): Added a fail-closed elevated-client evidence path:
  limited-information image-path resolution plus per-file Windows Restart
  Manager file-use evidence bound to PID and creation FILETIME. The fallback is
  explicitly recorded as file-use evidence and does not claim unavailable
  module base or memory information.
- `1.8` (`2026-07-24`): Added loading-gate v1, its exact-pointer ownership and
  timeout contract, dedicated tests, and an explicit parity attestation for
  the intended loading behavior.
- `1.9` (`2026-07-24`): Recorded v1 as failed and rolled back after the
  account-13 disconnect/server-full/`0x005F58BC` incident, captured the live
  blank-model baseline under the stable rollback shim, and documented the
  then-uninstalled v2 candidate's continuous processing, five-second
  exact-pointer fallback, and lifecycle-only cleanup contract.
- `1.10` (`2026-07-24`): Recorded v2 as `InstalledExact`, preserved its current
  Apply/stock-restore manifest, and kept Phase 2 blocked until live
  account-switch acceptance.
- `1.11` (`2026-07-24`): Recorded V2 as rejected after its cycle-3 timed
  unready handoff recreated the blank model, installed readiness-only V3, and
  kept Phase 2 blocked pending controlled V3 live acceptance.
- `1.12` (`2026-07-24`): Rejected V3 after immutable run
  `20260724T043833399Z-2bd75dd7` reproduced the roughly 15-second
  server-unavailable path and `0x005F58BC` null-root crash. Installed matched
  V4 Origin/Net with exact AfterLogin state-2 scheduling, synchronous native
  LOGIN initialization, readiness-only preview retention, and timeout guard.
  Automated gates pass; one cold smoke remains. Failure restores Net while
  Origin is V4, verifies stock Net/no `NetLegacy.dll`, then runs
  `PatchClientAvatarPreload.ps1 -Mode Revert` and proceeds to Phase 2 without
  claiming Phase 1 acceptance.
