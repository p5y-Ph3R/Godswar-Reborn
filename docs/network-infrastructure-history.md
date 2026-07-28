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
- `1.13` (`2026-07-24`): Sealed V4 smoke
  `20260724T095739213Z-db16daa7` as `Fail`. Origin PID `64928` connected to
  redirected TCP `127.1.1.110:7000`, but the server received no
  `LoginGameServer`; CharacterSelection, AfterLogin, and V4 preload never ran,
  and no dump appeared. Completed the enforced Net-first rollback. Current
  client is predecessor Origin `753BE49F...9ED79`, stock Net
  `1CC3F9AA...BCA00C`, and no `NetLegacy.dll`. Phase 1 remains unaccepted; the
  avatar issue is parked and Phase 2 codec slice 2 is next.
- `1.14` (`2026-07-24`): Extracted `ILegacyByteTransport` and the owned raw TCP
  adapter without moving legacy framing, XOR state, handler dispatch, or send
  serialization out of `ClientSession`. Added a fixed synthetic golden across
  the XOR wrap, a captured-clear game-bootstrap raw hash, and fragmented,
  coalesced, EOF, bounds, handler-loop, loopback, ownership, and concurrent
  parity checks.
  Existing raw listeners are unchanged; no TLS, UDP, admission, queue,
  deadline, or security runtime was enabled. Phase 2 slice 4 is next.
- `1.15` (`2026-07-24`): Added shared bounded connection admission, explicit
  authentication transitions, tracked accepted tasks and bounded shutdown,
  per-session item/byte reliable egress with physical-write completion,
  non-resetting packet/write deadlines, validated configuration, and finite
  low-cardinality metrics. Raw stream/cipher parity remains intact. TLS, UDP,
  secure control, and native client pumps remain disabled; slice 5 is next.
- `1.16` (`2026-07-25`): Added the uninstalled x86 native route coordinator,
  ephemeral-loopback bridge, dual-bounded opaque byte pumps, cancellable
  WinSocket adapter, generation-safe proxy lifecycle, and concurrency/failure
  tests. The process policy remains disabled/pass-through; no client was
  installed and Schannel/`SslStream` is slice 6.
- `1.17` (`2026-07-25`): Added strict signed endpoint-manifest validation,
  bounded external TCP and native Schannel/outer-frame primitives, opt-in
  default-disabled server `SslStream` listeners, finite handshake/frame/queue
  policy, heartbeat handling, and guarded development-certificate tooling.
  The client candidate remains uninstalled, no trust was installed, secure
  game binding fails closed pending Slice 7 tickets, and UDP remains absent.
- `1.18` (`2026-07-25`): Added secure-path bounded PBKDF2 authentication,
  atomic plaintext migration, grant-before-redirect ordering, hash-only
  single-use tickets, accepted game bind with authoritative principals, and
  offline native grant-registry/bind primitives. The secure profile remains
  disabled and the client remains uninstalled/pass-through; the default profile
  starts only raw `5999/7000`, while enabling secure mode suppresses both raw
  compatibility listeners. No live database or trust was changed, and UDP
  remains absent. Slice 8 is controlled activation and route wiring behind
  production manifest, backup/reset, authorized-trust, socket-test, and
  rollback gates.
- `1.19` (`2026-07-25`): Wired signed-manifest Login/Game routing and secure
  sessions into the exported x86 proxy, withheld secure external routes from
  the stock DLL, added fail-closed one-shot activation, grant-gated game
  routing, Origin-PID loopback ownership, handle-held stock-DLL loading,
  candidate-bound manifest trust, coherent raw-or-TLS startup, and guarded
  monotonic-floor Apply/Restore with interruption and forgery tests.
  Source/offline checks pass. No client, HKLM state, trust, account store,
  listener, or UDP changed live; controlled-host acceptance remains pending
  and Slice 9 is next.
- `1.20` (`2026-07-25`): Added the inert Slice 9A server-side UDP binding
  foundation: an exact 128-byte big-endian Hello/Challenge/Proof codec,
  allocation-free hostile-input parsing, full HMAC-SHA256 stateless
  return-path cookies, canonical IPv4/IPv6 endpoint binding, monotonic
  lifetime and current/previous key rotation, fail-closed configuration, and
  adversarial tests. No UDP socket, capability, session, native-client change,
  or gameplay migration exists; Slice 9B must retain the TLS connection ID and
  add authenticated binding after the protected-datagram ADR.
- `1.21` (`2026-07-26`): Added Slice 9B authenticated endpoint binding: an
  exact 72-byte game-TLS grant, retained connection ID, CSPRNG proof key,
  type-4 cookie-plus-TLS proof, fixed-capacity generation-safe authority,
  lease-owned cleanup, endpoint conflict handling, initial rate limits and
  metrics, native grant retention, and a loopback-tested listener. Activation
  remains fail-closed; AEAD, replay windows, key epochs, NAT rebinding,
  keepalive, and the native UDP worker remain required.
- `1.22` (`2026-07-26`): Completed Slice 9C's protected UDP foundation with
  AES-GCM direction separation, replay windows, key epochs and rotation,
  authenticated NAT rebinding, native keepalive/pacing, bounded endpoint
  runtime, low-cardinality telemetry, adversarial/loopback tests, and a
  provider-neutral activation boundary. Managed `121/121`, native Release
  `/W4 /WX`, five offline passes, and the capped local UDP baseline passed.
  UDP stayed disabled, no shim was installed, and gameplay stayed on TLS.
- `1.23` (`2026-07-26`): Implemented the default-off first authoritative
  movement slice. Added exact 52-byte input and 64-byte snapshot envelopes,
  protected UDP types `4/5`, TLS fallback frame `0x0300`, capability-gated
  native interception, capacity-one ingress/egress, one-way UDP-to-TLS epoch
  handoff, global input dedupe, 20 Hz server authority, 10 Hz snapshots,
  one-second keyframes, reliable stock-client corrections, deferred bounded
  persistence, and world/revive sequence preservation. Deterministic
  latency/jitter/loss/duplication/reordering/UDP-blocking checks, handler
  integration, full managed `126/126`, two identical native Release builds,
  and five offline passes succeeded. The shim remains uninstalled and every
  secure/gameplay setting remains disabled pending controlled-host acceptance.
- `1.24` (`2026-07-27`): Completed the guarded disposable-client acceptance
  for the selected in-process shim. The original client negotiated the pinned
  TLS policy, received the secure preface, authenticated, bound its encrypted
  UDP endpoint, and entered the world. The accepted candidate SHA-256 is
  `0328D7EA84B68DD8D5A1DF7B0A291B9DC17EF3337C0114A7A396283FC4EF852B`.
  Mandatory rollback restored the stock DLL, exact hosts bytes, development
  trust, manifest keys, and checked-in public-key placeholder. Added an
  isolated secure Docker profile with read-only certificate secrets, durable
  database selection, loopback-only TLS/UDP publication, and a dual-transport
  healthcheck. A live reference probe exposed and fixed a dual-network Docker
  forwarding defect, then passed TLS login/authentication, ticket redemption,
  authenticated UDP binding, world entry, authoritative movement, and
  snapshot acknowledgement with zero retained test accounts. Original-client
  Phase 4 movement/fallback/soak evidence remains open.
- `1.25` (`2026-07-27`): Rejected the legacy repeat-entry campaign after five
  apparent successful preview cycles were followed by three persistent
  blank-model retries; the later failures invalidate the earlier cycles.
  Preserved that campaign and its evidence as historical, read-only, and not
  accepted. Added the independent PreviewReadyV1/schema-2 campaign root,
  deterministic avatar-preview readiness candidate
  `A3D042C6BC73AF4E9CAAA3B1BC1B5EE9EC9BD47E002B1A5BAE781A6AD43CFC75`,
  native-check pin
  `294BE833851FB89468ECB011D01AE1A9B476DA25EB18A68D6B0544FC5374242F`,
  immutable evidence root, and bounded same-user graceful-stop control. A
  fresh Baseline/Fallback/Soak campaign remains required.
- `1.26` (`2026-07-27`): Superseded the unaccepted PreviewReadyV1 campaign
  with receipt-bound PreviewReadyV2. Pinned candidate
  `EFFC21D1500C39352ADEFB2B2D6388912A7EF50505BD3AD8CB043D32D7D956CE`
  and native checks
  `237EA0A3B90A4642DADA1170B1A740B966984C8004B99698F752491EC6732187`
  under the independent PreviewReadyV2 campaign/evidence roots. Preserved
  both PreviewReadyV1 and LegacyV1 through generation-specific read-only
  accessors; neither can receive new production campaign or completion
  receipts. Live acceptance remains required.
- `1.27` (`2026-07-27`): Made the Phase 4 original-client foreground
  Baseline, forced-Fallback, and ten-minute Soak campaign the immediate exit
  milestone. Defined Phase 5A as deterministic replay, bounded load/soak,
  reproducible resource/tick/network baselines, and operational telemetry.
  Explicitly deferred map expansion, traversal optimization, and AOI egress
  optimization until both gates are complete.
- `1.28` (`2026-07-28`): Rejected PreviewReadyV2 after its manual preview gate
  produced dump `20260728001641.dmp`, SHA-256
  `271942FB737D57F24468369114C563ECECE281362FA64D26245A77D5720CDE36`.
  Dump analysis observed x86 `C0000005` at `EIP=0x005F58BC`, `ECX=0`,
  `ESI=0`, dereferencing null avatar resource slot `0x015760A0`. From that
  state and wire order, attributed the partial resource set to V2 invoking
  native initialization on the first of 63 AfterLogin records. Superseded it
  with PreviewReadyV3's following-preview barrier, candidate
  `5FD6A0C37801A393689AF523854AD5BE258616BF52809D8FEA04437D34B7CA85`,
  and native checks
  `ABB81E184CA54DD9ECFFDC1F2DB690E122F81A4B394050AF4F7B6095FC34308B`
  under fixture `20260728-004030-preview-ready-v3` and an independent protected
  root. V2, V1, and LegacyV1 remain immutable, readable failed history.
- `1.29` (`2026-07-28`): Manually rejected PreviewReadyV3 after two
  intermittent first-attempt failures. Start `01:13:25+12:00` produced
  null-slot dump `20260728011349.dmp`, SHA-256
  `18176B45640DADB220EA090D718927CB742029352405ACA71791183B3E280B7A`;
  start `01:13:59` authenticated but disconnected before world entry without
  another dump. A retry at `01:14:27` entered the world but did not cure either
  failure. Preserved rejection record
  `F6211A83FFCEDA06E9E634CFF8F83619E5A466B256B234C4D7ECFD06A0E43804`
  and Baseline profile
  `F5234D4C20ECF769822F3E4418A5E20BA75A35D6FBF34F825C77AE02E1393A2`,
  then reached exact V3 handoff revision 13 `Restored`: stock Origin/Net,
  absent legacy/manifest, restored hosts, and removed trust. Froze V3
  read-only and introduced PreviewReadyV4 under
  `C:\ProgramData\RebornSecureNetworkPhase4DockerPreviewReadyV4` and fixture
  `20260728-015732-preview-ready-v4`. V4 pairs guarded Origin
  `1D1AA876...B3A5A` with public-trust Net `D353E921...67D55` and checks
  `C5C8B738...34FB1`. Its isolated six-root timeout guard preserves stock
  state-2 retry side effects and excludes the rejected preload hook. Offline
  gates pass; no live V4 acceptance is claimed.
- `1.30` (`2026-07-28`): Rejected V4 after its first live attempt completed
  server-side TLS policy but never sent the secure preface. Static analysis
  proved the Origin timeout guard runs after the disconnect and narrowed it to
  state 2 plus the two stock-dereferenced avatar roots. Added a bounded native
  TLS/preface probe and scoped Development-only CryptoAPI validation against
  the exact embedded public root; production remains on automatic Schannel
  trust and revocation policy. The native probe passed TLS 1.3 and secure
  preface with CurrentUser trust absent. Introduced PreviewReadyV5 under
  `20260728-031445-preview-ready-v5`, pairing Origin
  `E177D94D...CC76C`, Net `0A34613E...DAA1A`, and checks
  `49FEA163...879E52`. V4 had its root installed during failure, so its exact
  historical cause remained unproven; V5 still required live acceptance. The
  V5 certificate deadline is `2026-08-09/10` UTC.
- `1.31` (`2026-07-28`): Rejected PreviewReadyV5 after its first live attempt
  failed immediately before authentication. Live Net runtime sent stock
  Origin SHA-256 `753BE49F...ED79`, while the foreground server correctly
  allowed only patched Origin `E177D94D...CC76C`; the native probe had masked
  the defect by injecting the latter identity from its command line. Restored
  V5 exactly at protected `handoff-000024.json`. Introduced PreviewReadyV6 at
  `20260728-102640-preview-ready-v6`, with Origin
  `E177D94D...CC76C`, Net `21695893...CAE97`, and checks
  `FD34DD6F...4F75`. V6's `GWKEY02` contract carries the Origin identity;
  runtime and preview guard consume it, and deterministic build/installer
  gates now verify the paired Origin and Net before mutation. No V6 live
  acceptance is claimed.
- `1.32` (`2026-07-28`): Sealed PreviewReadyV6's live Baseline `Pass` for
  campaign `5848b53f-24b4-4f11-a4fd-591c0c0e1a36`. The nine-event protected
  evidence `secure-server-20260727-224656-0460982.log` has SHA-256
  `F8A2AC8A...A3CA`; its profile has SHA-256 `F13470A4...3B16` and records
  `1044.613001` seconds. TLS authentication, authenticated UDP binding,
  authoritative movement/snapshot, and graceful stopping all completed.
  Fallback, Soak, and exact rollback remain pending; V6 is not fully accepted.
- `1.33` (`2026-07-28`): Completed PreviewReadyV6 Phase 4 acceptance under
  one final campaign, `0a73fd79-961b-42c7-82cc-9e4a6f9e3355`. Baseline,
  forced Fallback, and Soak passed in `94.6714105`, `112.2090199`, and
  `661.5843391` seconds on server `8B3E3134...F8E3` and release set
  `0460C408...9EC5`. Fallback proved one-way TLS correction with no switchback;
  commit `fc26223` retains TLS-owned authority through UDP idle cleanup, and
  the final Soak proved it survived the former 30-second expiry.
  Exact stock rollback, absent activation artifacts/keys, and healthy
  zero-restart Docker passed. Completion receipt
  `completion-0a73fd79-961b-42c7-82cc-9e4a6f9e3355.json` has SHA-256
  `5EB6E369...F4A6F`; viewer parity is recorded `Unavailable`.
