# Controlled-host secure-network acceptance

## Purpose and status

This is the guarded original-client acceptance gate for the application-local
x86 network shim, TLS control channel, authenticated encrypted UDP, and the
first authoritative-movement slice.

The historical `20260727-011921` run accepted TLS authentication,
authenticated UDP binding, and world entry, then completed exact rollback.
The repeatable Phase 4 campaign now reuses its reviewed client and certificate
artifacts without recreating the removed protected runtime or private signing
keys. Movement, forced fallback, and soak scenarios remain open until the
manual gate runs. The executable command sequence is in the
[controlled-host command reference](network-infrastructure-controlled-host-commands.md).

This exercise is a local compatibility/security result. It is not a production
capacity claim and does not replace upstream L3/L4 DDoS protection.

## Fixed scope

- Client: `C:\RebornNetworkAcceptanceClient` only.
- Database: Docker-owned `godswar_secure_dev` only.
- Fixture: `artifacts\controlled-host-acceptance\20260727-011921`.
- Campaign handoff:
  `C:\ProgramData\RebornSecureNetworkPhase4Docker`.
- DNS: `login.reborn.test` and `game.reborn.test` to literal loopback.
- Secure endpoints: TCP `127.0.0.1:6599`, TCP `127.0.0.1:7443`, and
  UDP `127.0.0.1:7444`.
- Secure-Docker server: healthy before and after acceptance, then stopped
  only while the foreground controlled-host server owns the same three ports.
  PostgreSQL remains healthy throughout.
- Server: the current Release in a foreground ordinary-user process while
  Docker `server` is stopped; it uses the same Docker PFX/password file and
  `godswar_secure_dev`.
- Evidence:
  `artifacts\controlled-host-acceptance\20260727-011921\server-evidence`.

The procedure does not alter Norton, Windows Firewall, network adapters,
routes, or internet connectivity. It does not mutate `C:\Godswar Origin` or
the live `godswar` database. It does not expose a non-loopback listener,
deploy paid infrastructure, or recreate the removed historical runtime.

## Pinned fixture

| Artifact | SHA-256 |
| --- | --- |
| database dump | `7EC9775B2F6F08361F606FEC2968623573A632D2FCD02EBDD12327B6407F4AAE` |
| stock `Origin.exe` | `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79` |
| stock predecessor `Net.dll` | `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C` |
| accepted candidate `Net.dll` | `0328D7EA84B68DD8D5A1DF7B0A291B9DC17EF3337C0114A7A396283FC4EF852B` |
| accepted native checks | `D583309B921C7AA795F7A044F096762703AA2DB376A1D07B9EEB4F44312208D0` |
| endpoint manifest | `3B82FA5EC445B6546A2168F9E5BD83B6C2EFD57729B94C116B4EF77A2A43622C` |
| current manifest trust | `A32B40917A01D510504528F5D6996F918A6A218991B64C50234ED84C75C75C07` |
| next manifest trust | `582C252D31DE3361157C7625FB21DD104F907EA762FB77044E1CCEF2EA51E571` |
| issued key receipt | `A5C286694AA1361A8A18E9E42594A4D56563F9E4DD0563D5A464DC0941B39B50` |
| TLS PFX | `C498666CC8D6ECF09DF92C217169A6F2CDA788DEDA60E5DD17B1EA9CA6C6BC0F` |
| public development root | `911E3CF444B631AAB9EDCC5980DF65243CAAC42B9000C5E2410C7DADFEB54DED` |
| schema-2 trust receipt | `57FF8F9D9A5701E6AB3E79C243F69D412DE30BA085F9DAD0EED473208748BCF4` |
| certificate secret artifact | `58B26CCF6AE4B3311571B48F9A788B03245D8C11959BCFC79D840C9C74719A9D` |
| PostgreSQL secret artifact | `C38710F43DBD73A164746F6530FF8B556F863D8D393094B8E577E932C84CABEE` |

The server assembly, complete managed Release set, and options file are rebuilt
and hashed immediately before the foreground profiles. The loopback runner
records those generated hashes and reads the exact Docker certificate/password
and PostgreSQL boundary without printing credentials.

## Gate sequence

1. Run all offline gates from the command reference. Require a zero-warning
   Release build, the complete managed and native checks, privacy-evidence and
   repeat-campaign PowerShell suites, clean parsers/diff/size checks, and exact
   artifact hashes.
2. Confirm the healthy secure-Docker reference baseline, including its
   bounded TLS/ticket/UDP/movement smoke. It initially owns `6599`, `7443`,
   and `7444`.
3. In one elevated console, run the repeat-entry campaign Apply. It validates
   the retained floor at manifest sequence 3, installs the exact public
   development root, applies the receipt-bound hosts transaction, and applies
   the hash-pinned client bundle while secure Docker remains healthy. The two
   development CNG signing keys intentionally remain absent: the
   already-signed manifest and compiled public verification keys do not
   require signing authority at runtime.
4. Close every elevated console. Stop only the secure-Docker `server`;
   PostgreSQL remains healthy. From a fresh ordinary token, run the foreground
   Baseline, Fallback, and Soak evidence profiles serially. The runner refuses
   an elevated or SYSTEM token, enables faults only for Fallback, and enforces
   at least ten measured minutes for Soak.
5. Close the client after each profile and stop the foreground server
   gracefully so its bounded evidence can be validated and protected.
6. Restart and revalidate the exact secure-Docker profile.
7. Perform campaign Restore even if every scenario passes. It restores stock
   client files and original hosts bytes, removes only the exact public root
   installed by that campaign, retains the monotonic sequence floor at 3,
   and requires secure Docker to be healthy.

No operation may be skipped or run concurrently. If any gate differs from its
pinned state, stop and roll back completed operations.

## Manual acceptance matrix

Run with Phase 4 acceptance faults disabled:

Every row is required unless it explicitly says that two clients were not
available. Record the operator result next to the immutable server evidence;
the server event alone does not attest a visual client result.

| Scenario | Required operator action | Required evidence/pass condition |
| --- | --- | --- |
| Docker reference baseline | Run the bounded secure-Docker smoke before client Apply. | TLS login, game bind, authenticated UDP movement, and snapshot acknowledgement pass; container remains healthy with zero restarts. |
| Alternating accounts | Complete five login, selection, and world-entry cycles alternating accounts 7 and 13. | Every cycle enters the world; no crash, blank model, false full/unavailable result, or second-launch workaround. |
| Preview readiness | Wait at character selection without dismissing or relaunching. | The 3D model appears automatically before the normal connection deadline. |
| Unmounted movement | Move continuously, stop, turn, and change direction. | Local movement remains responsive and authoritative movement is visible to a second client when available. |
| Mounted movement | Mount, move, turn, stop, and dismount. | Mounted movement uses the server-owned speed multiplier; dismount restores ordinary movement. |
| World-generation changes | Change map, then move before and after the transition. | No old-world correction or stale position is applied; movement resumes on the new baseline. |
| Death and revive | Die, revive, then move. | Dead movement is rejected; revive establishes the new baseline and movement resumes. |
| Viewer parity | Observe the moving character from a second client when available. | Viewer movement and any authoritative correction remain coherent and use the canonical legacy projection. |
| Session lifecycle | Logout, reconnect, and exercise duplicate-login replacement. | No stale UDP endpoint, session takeover, or false server-unavailable result. |
| Logical UDP-loss fallback | Use the one-shot server ACK-drop campaign; do not alter Norton, Windows Firewall, routes, or adapters. | The five fixed fallback/correction events appear once and the character continues moving on TLS without switching back. |
| Normal soak | Under the foreground Soak profile, move normally for at least ten measured minutes, including at least one mount and map transition. | No crash, blank model, server-unavailable regression, repeating error, foreground exit, or unexpected database mutation; measured runner duration is at least ten minutes. |
| Rollback | Close the client and follow the exact rollback sequence. | Stock hashes, original hosts bytes, safe-disabled sequence floor 3, root/key absence, and restored secure-Docker profile all validate. |

The bounded Docker smoke provides machine-verifiable TLS/ticket/UDP/movement
coverage for the reference baseline. Every foreground evidence profile must
also contain one fixed TLS-authentication event, one authenticated-UDP binding
event, one accepted authoritative UDP movement event, and its subsequent
queued authoritative UDP snapshot event. Confirm that credential migration,
if exercised, remains inside `godswar_secure_dev`.

Any crash, missing preview, raw listener, unexpected database mutation,
unbounded/repeating error, or non-allowlisted evidence fails the gate.

## Forced fallback and correction

Stop the ordinary server gracefully and restart it once with the explicit
one-shot Phase 4 acceptance-fault switch. It remains Development,
loopback-only, TLS-enabled, UDP-enabled, and authoritative-movement-enabled.

Move the first selected character continuously. The server logically
suppresses the matching epoch-one snapshot acknowledgement for 1.5 seconds.
The client must fall back once to the adjacent TLS epoch, receive one
authoritative `NotReady` correction, continue movement on TLS, and never
switch back in that session.

This is the approved UDP-loss acceptance mechanism. It exercises application
fallback without changing or disabling Norton, Windows Firewall, network
adapters, routes, DNS outside the receipt-bound hosts transaction, or the
machine's internet connection. Do not add an operating-system UDP block to
this campaign.

Required fixed evidence is:

```text
[secure-acceptance] phase4 fault campaign enabled
[secure-acceptance] authoritative UDP movement accepted
[secure-acceptance] authoritative UDP snapshot queued
[secure-acceptance] snapshot ACK drop started window_ms=1500 max_recorded_drops=32
[secure-acceptance] one-way TLS fallback observed
[secure-acceptance] authoritative correction forced reason=not_ready
[secure-acceptance] post-fallback TLS movement observed no_switchback=true
```

The five fallback/correction events must complete within the campaign's
15-second post-trigger lifetime. Stop gracefully after they appear; the
Fallback profile rejects an expired or incomplete campaign. Restart with the
Soak profile, which forbids fault state and enforces a measured foreground
lifetime of at least ten minutes.

## Privacy-safe evidence

Controlled-host mode installs its evidence sink before any game/store/network
work. Ordinary `Console.Out` and `Console.Error` are sent to a non-buffering
discard writer. Only trusted enum callsites can append one of sixteen fixed
one-shot lines to a `CreateNew`, 1,536-byte-bounded UTF-8 file. Account IDs,
session IDs, character names, IP addresses, packet hex, attacker strings,
exceptions, tickets, cookies, keys, passwords, and payloads cannot enter it.

The stopped file is validated for exact lines, uniqueness, start/stop
boundaries, encoding, and bounds, then made read-only to the issued user. This
is privacy-safe local acceptance evidence, not cryptographic proof against
compromise of that same local user.

## Mandatory rollback order

1. Close disposable `Origin.exe`.
2. Stop the foreground secure server gracefully.
3. Restart and health-check the exact secure-Docker profile.
4. Run campaign Restore from a fresh elevated issued-user console. It uses
   only its protected checksummed handoff.
5. Verify stock `Origin.exe`/`Net.dll`, no `NetLegacy.dll`/manifest, exact
   original hosts bytes, public-root absence, activation Mode `0`,
   development Environment `1`, and retained sequence floor `3`.
6. Verify the two development CNG keys remain absent. They are not recreated
   merely to remove them again.
7. Verify `C:\Godswar Origin`, the live `godswar` database, and network/firewall
   state are unchanged.

Keep the protected campaign handoff and evidence until review completes.
Dropping the disposable database/client is a separate destructive cleanup
requiring explicit approval.

## Known limitations

- The full-client inventory is drift protection/self-attestation. Independent
  known-good provenance exists for `Origin.exe`, stock `Net.dll`, and the
  reviewed candidate; not for every one of roughly 21,000 client files.
- The writable `Log`, `Dump`, `ScreensHot`, and per-user settings islands are
  treated as data-only. The stock executable and shim must not load executable
  code from them. The gate is not WDAC, AppLocker, or a defense against
  compromise of the issued current user.
- The local loopback result does not measure internet loss, jitter, regional
  latency, provider mitigation, packet-per-second capacity, or production
  concurrency.

## Historical acceptance record

The accepted evidence file is
`server-evidence\secure-server-20260726-140216-4696532.log` under the fixed
fixture. It contains exactly the privacy-safe listener-ready, TLS-policy,
preface-written, TLS-authenticated, UDP-bound, and stopping events. The
original client reached the world. It does not contain the Phase 4 movement
or forced-fallback events, so those gates remain open.

Mandatory rollback completed on `2026-07-27`: the disposable client returned
to stock `Net.dll` with no `NetLegacy.dll` or manifest; hosts returned to
SHA-256
`96B8714EAEB906C50EA8282A44C5A0A239BCAC1F723A89B5C4476957B496ADA3`;
the development root and both CNG keys were removed; the generated header
returned to the checked-in placeholder; and the protected runtime was removed.
The external cleanup receipt is
`C:\ProgramData\RebornSecureNetworkCleanupReceipts\runtime-cleanup-20260727-011921.json`.

That accepted record does not close Phase 4. Original-client movement,
forced fallback, parity, and soak remain unaccepted until every applicable
row in the manual acceptance matrix passes and mandatory rollback completes.
