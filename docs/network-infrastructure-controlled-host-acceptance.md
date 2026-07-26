# Controlled-host secure-network acceptance

## Purpose and status

This is the guarded original-client acceptance gate for the application-local
x86 network shim, TLS control channel, authenticated encrypted UDP, and the
first authoritative-movement slice.

The `20260727-011921` run accepted TLS authentication, authenticated UDP
binding, and world entry, then completed exact rollback. Phase 4 movement,
forced fallback, and soak scenarios remain open. The executable command
sequence is in the
[controlled-host command reference](network-infrastructure-controlled-host-commands.md).

This exercise is a local compatibility/security result. It is not a production
capacity claim and does not replace upstream L3/L4 DDoS protection.

## Fixed scope

- Client: `C:\RebornNetworkAcceptanceClient` only.
- Database: `godswar_secure_acceptance_20260727_011921` only.
- Fixture: `artifacts\controlled-host-acceptance\20260727-011921`.
- Protected runtime:
  `C:\ProgramData\RebornSecureNetworkRuntime\20260727-011921` (removed after
  the mandatory rollback).
- DNS: `login.reborn.test` and `game.reborn.test` to literal loopback.
- Secure endpoints: TCP `127.0.0.1:6599`, TCP `127.0.0.1:7443`, and
  UDP `127.0.0.1:7444`.
- Raw Docker server: stopped during acceptance; PostgreSQL remains healthy.
- Server: foreground process under the issued ordinary user.
- Evidence:
  `artifacts\controlled-host-acceptance\20260727-011921\server-evidence`.

The procedure does not alter Norton, Windows Firewall, network adapters,
routes, or internet connectivity. It does not mutate `C:\Godswar Origin` or
the live `godswar` database. It does not expose a non-loopback listener or
deploy paid infrastructure.

## Pinned fixture

| Artifact | SHA-256 |
| --- | --- |
| database dump | `7EC9775B2F6F08361F606FEC2968623573A632D2FCD02EBDD12327B6407F4AAE` |
| stock `Origin.exe` | `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79` |
| stock predecessor `Net.dll` | `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C` |
| accepted candidate `Net.dll` | `0328D7EA84B68DD8D5A1DF7B0A291B9DC17EF3337C0114A7A396283FC4EF852B` |
| accepted native checks | `D69DE85B47C7BC1E954DD1CBBC725A0EC9566E2004EFCDCDD132CC6B14FF5EAF` |
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
and hashed immediately before protected staging. Those generated hashes are
recorded; older values are never reused.

Secrets are CurrentUser DPAPI `SecureString` files. They are never passed on a
command line, printed, transcribed, or committed. PostgreSQL scope validation
uses redirected standard input to a pinned local .NET validator and confirms a
literal-loopback host plus the exact disposable database name.

## Gate sequence

1. Run all offline gates from the command reference. Require a zero-warning
   Release build, `131/131` managed checks including every environment-gated
   PostgreSQL integration against the exact disposable database, native
   reproducibility/offline checks, all controlled-host PowerShell suites,
   clean parsers/diff/size checks, and exact fixture hashes. A normal run
   without the DPAPI-backed test connection reports PostgreSQL `SKIP` lines
   and does not satisfy this gate.
2. In one elevated console, protect the database backup, harden and inventory
   the disposable client, and stage the complete protected server runtime.
3. Close the elevated console and reboot. The reboot is mandatory because a
   predecessor process handle could otherwise survive the ACL transition.
4. From a fresh ordinary token, revalidate every protected object, receipt,
   key, certificate, database boundary, client hash, and free port.
5. From a fresh elevated console, stop only Docker `server`, apply the hosts
   transaction, and apply the bundle transaction. Retain their
   ACL-protected/checksummed receipts and exact rollback authority.
6. Close every elevated console. From a fresh ordinary token, run launcher
   preflight and then the server in the foreground. The launcher refuses an
   elevated or SYSTEM token.
7. Run the baseline, forced-fallback, and normal-soak scenarios.
8. Perform mandatory rollback even if every scenario passes.

No operation may be skipped or run concurrently. If any gate differs from its
pinned state, stop and roll back completed operations.

## Baseline scenarios

Run with Phase 4 acceptance faults disabled:

1. Alternate five complete login/world-entry cycles between the two issued
   acceptance accounts.
2. Confirm character selection displays its 3D model without a timeout,
   crash, or false "server unavailable/full" result.
3. Confirm world entry, movement, map transition, mount, dismount, death,
   revive, logout, reconnect, and duplicate-login replacement.
4. With two clients when available, confirm viewer movement and correction
   remain coherent.
5. Confirm one fixed TLS-authentication event and one fixed authenticated-UDP
   binding event in privacy-safe evidence.
6. Confirm only the disposable database migrates accepted legacy credentials
   to the versioned password format.

Any crash, missing preview, raw listener, unexpected database mutation,
unbounded/repeating error, or non-allowlisted evidence fails the gate.

## Forced fallback and correction

Stop the ordinary server gracefully and restart it once with the explicit
one-shot Phase 4 acceptance-fault switch. It remains Development,
loopback-only, TLS-enabled, UDP-enabled, and authoritative-movement-enabled.

Move the first selected character continuously. The server suppresses the
matching epoch-one snapshot acknowledgement for 1.5 seconds. The client must
fall back once to the adjacent TLS epoch, receive one authoritative `NotReady`
correction, continue movement on TLS, and never switch back in that session.

Required fixed evidence is:

```text
[secure-acceptance] phase4 fault campaign enabled
[secure-acceptance] snapshot ACK drop started window_ms=1500 max_recorded_drops=32
[secure-acceptance] one-way TLS fallback observed
[secure-acceptance] authoritative correction forced reason=not_ready
[secure-acceptance] post-fallback TLS movement observed no_switchback=true
```

Restart without the fault switch and run a ten-minute normal movement soak.
No fault state may survive the process restart.

## Privacy-safe evidence

Controlled-host mode installs its evidence sink before any game/store/network
work. Ordinary `Console.Out` and `Console.Error` are sent to a non-buffering
discard writer. Only trusted enum callsites can append one of twelve fixed
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
3. Bundle Restore using the retained exact Apply backup.
4. Verify stock `Origin.exe`/`Net.dll`, no `NetLegacy.dll`/manifest, activation
   Mode `0`, development Environment `1`, and retained sequence floor.
5. Hosts Restore from its checked receipt; verify exact original bytes and
   absence of the receipt/managed block.
6. Remove only the development root named by the staged schema-2 receipt.
7. Remove only the two development CNG keys named by the staged key receipt.
8. Verify both cleanup receipts say `Removed` and the root/keys are absent.
9. Restore the checked-in public placeholder header with a preimage-guarded
   Codex `apply_patch`; never use `git checkout` or commit machine coordinates.
10. Remove the protected runtime **last** through its cleanup tool.
11. Restart Docker `server` and verify only its original raw loopback bindings.
12. Verify `C:\Godswar Origin`, the live database, and original hosts bytes are
    unchanged.

Keep the protected dump and external runtime-cleanup receipt until evidence
review completes. Dropping the exact disposable database/client is a separate
destructive cleanup requiring explicit approval.

## Known limitations

- The full-client inventory is drift protection/self-attestation. Independent
  known-good provenance exists for `Origin.exe`, stock `Net.dll`, the reviewed
  candidate, and staged runtime; not for every one of roughly 21,000 client
  files.
- The writable `Log`, `Dump`, `ScreensHot`, and per-user settings islands are
  treated as data-only. The stock executable and shim must not load executable
  code from them. The gate is not WDAC, AppLocker, or a defense against
  compromise of the issued current user.
- The local loopback result does not measure internet loss, jitter, regional
  latency, provider mitigation, packet-per-second capacity, or production
  concurrency.

## Acceptance record

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
