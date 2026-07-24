# Client avatar-preview login crash

## Result

The V4 candidate passed automated binary/shim tests but failed its final cold
smoke before character selection and is rejected. Origin PID `64928` connected
to redirected game TCP `127.1.1.110:7000`, but the server received no
`LoginGameServer`; CharacterSelection, AfterLogin, and the V4 preload path never
ran. Evidence `20260724T095739213Z-db16daa7` is sealed `Fail`; no dump appeared.
V4 was rolled back and the avatar issue is parked without Phase 1 acceptance.

Installed on 2026-07-22 with:

- Before SHA-256: `1F0AC79175718357590A7354378E808A7F446B763CD05EDF659359FD4D819CC6`
- After SHA-256: `1BBD41D4E148E040B363D2A83D36CD326A2C2CFE1EA44E08DA6B2680CA1BB329`
- Verified backup: `C:\Reborn\backups\origin-avatar-preview-guard-Apply-20260722-135817166\Origin.exe`
- Exact binary impact: 169 changed bytes in three hooks and three reserved
  executable caves

No server or database change is part of this fix.

The V4 extension was temporarily installed on 2026-07-24 with:

- Before SHA-256:
  `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`
- Installed SHA-256:
  `E0F5BC951C6E37550F4D9CC1E25BFDCB4F020466ADD854DC2E7EA04E0D22F81C`
- Apply backup:
  `C:\Reborn\backups\origin-avatar-preload-v4-Apply-20260724-213316596-5256fb25`
- Companion `Net.dll` SHA-256:
  `EF531F8CB20A4FCA8D1DBA979FD131ECA002383AE862890435426DF948817597`
- Net Revert backup:
  `C:\Reborn\backups\client-network-shim-v1-Revert-20260724-221318157`
- Origin Revert backup:
  `C:\Reborn\backups\origin-avatar-preload-v4-Revert-20260724-221319380-aeb5325a`

Current exact predecessor state:

- `Origin.exe`:
  `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`
- stock `Net.dll`:
  `1CC3F9AABBC339300DF06795AB22EAD1ACC7F4CBB47F2F2DBF36F1CF19BCA00C`
- `NetLegacy.dll`: absent

The later companion `Net.dll` loading gate is documented in
[`client-avatar-preview-loading-gate.md`](client-avatar-preview-loading-gate.md).
V1 failed by suppressing native processing. V2 fixed that scheduling defect but
was rejected when its five-second unready handoff recreated the blank model.
V3 retained the exact preview until readiness but is rejected after immutable
run `20260724T043833399Z-2bd75dd7` reproduced the native timeout and
`0x005F58BC` crash. V4 coupled native initialization and timeout guards with
the readiness-only hold, but its live acceptance branch failed before those
paths ran.

## Evidence and root cause

The two 2026-07-22 dumps at `13:08:45` and `13:08:58` both contain an x86
`C0000005` access violation at VA `0x005F4ADD`. `ECX` was zero at:

```text
mov ecx, [0x0157608C]
mov eax, [ecx]
```

The same address appears repeatedly in the historical client error log and
predates the Q20/G25 and global-rank work. Two older dumps independently fault
at VA `0x005F060E`, where the other selection-avatar builder dereferences
`0x015760A0` while the avatar resource set is absent.

The later v1 loading-gate incident produced dump `20260724151147.dmp` at
`0x005F58BC`, where a state-transition routine dereferenced the same null
`0x015760A0` root after calling native network disconnect. That third site is
not either guarded preview builder. Its evidence and the stable-shim
blank-model baseline are recorded in
[`client-avatar-preview-loading-gate-incident-20260724.md`](client-avatar-preview-loading-gate-incident-20260724.md).

Readiness-only V3 independently reproduced the same fault in
`20260724210050.dmp` (SHA-256
`7A5B34B86A2A2E9F8281A1B9F7DDDA9579AAE9AFDC839E4A43D26C7575E993D9`).
The client closed about 14.8 seconds after preview, displayed its native
server-unavailable dialog, and faulted at `0x005F58BC` with `ECX` and root
`0x015760A0` null after the dialog was acknowledged. The server had accepted
the account and remained healthy. See
[`client-avatar-preview-v3-failure-20260724.md`](client-avatar-preview-v3-failure-20260724.md).

The client builds male and female selection resources through the LOGIN
object's native initializer at VA `0x00467280`. It sets byte
`0x01575F70 = 1` when entering the world and intentionally unloads the
selection resources afterward. The flag has no native reset. When state 2
installs LOGIN again, the shared dispatcher sees the stale value and skips the
initializer even though the avatar roots have been cleared. Preview packets
can then reach either builder with null resources.

## Patch design

`tools/PatchClientAvatarPreviewGuard.ps1` applies the historical base three
pieces as one transaction:

1. The LOGIN state arm at VA `0x004C14C5` clears `0x01575F70`, replays its
   displaced `push 0x009E5A04`, and returns to native dispatch. This restores
   the same pre-world initialization lifecycle used by a fresh process without
   changing world-state teardown.
2. The builder at VA `0x005F0590` is guarded after its SEH prologue and before
   any local object construction. If any of the six required male/female
   resources is absent, it uses the routine's untouched raw epilogue at
   `0x005F0DC3`.
3. The builder at VA `0x005F4A20` is guarded after its native argument checks
   and before local object construction. A missing resource uses its untouched
   early epilogue at `0x005F516D`.

Both historical builder guards are fail-closed. A packet that still wins a
narrow initialization race can skip that one preview build instead of
dereferencing null. Neither guard sleeps, re-enters the loader, or runs
initialization from the render path. V2 tried to address the safe skip with a
timed handoff and failed; V3's readiness-only hold could prevent the LOGIN
update from reaching the lifecycle work needed to populate the roots.

`tools/PatchClientAvatarPreload.ps1` adds the matched V4 correction:

1. After native LOGIN state registration at `0x004C14D6`, it invokes the
   existing initializer at `0x00467280` synchronously on the main thread.
2. It marks initialization complete only after all six audited avatar roots
   are non-null; otherwise the stock later call remains available.
3. At `0x005F58BC`, it checks all six roots before either unsafe avatar call.
   Missing roots skip those calls and schedule a clean state-2 transition.
4. The companion shim schedules state 2 on the exact AfterLogin record and
   retains the exact preview pointer until those roots are ready.

The patcher refuses to write unless the client is closed and all of the
following match the audited build: file size, DOS/PE headers, x86 PE32 machine,
image base, executable section mappings, hook bytes, continuations,
epilogues, fault sites, and empty or exact cave state. It verifies a full
backup hash before writing, scans the complete post-write file for mutations
outside the six allowed ranges, and supports idempotent apply/revert.

## Apply or revert

The historical base patch is managed from `C:\Reborn` with:

```powershell
.\tools\PatchClientAvatarPreviewGuard.ps1 -Mode Apply
```

To restore the exact pre-patch executable:

```powershell
.\tools\PatchClientAvatarPreviewGuard.ps1 -Mode Revert
```

The revert command also creates a verified backup of the patched executable
before restoring the audited original bytes. The dated backup above can be
copied back manually if the patch tool is unavailable.

The rejected historical V4 extension is managed independently with:

```powershell
.\tools\PatchClientAvatarPreload.ps1 -Mode Status
.\tools\PatchClientAvatarPreload.ps1 -Mode Apply
.\tools\PatchClientAvatarPreload.ps1 -Mode Revert
```

Apply/Revert refuse a running client and validate the exact predecessor,
hooks, caves, file shape, allowlisted diffs, and hashes. Writes are staged,
hash-verified, and atomically replace the destination. The patcher also requires
sibling `Net.dll` to be exact stock and `NetLegacy.dll` to be absent. Therefore
V4 rollback must restore Net first while Origin is still V4, verify that clean
stock state, and only then run `PatchClientAvatarPreload.ps1 -Mode Revert`.

## Final runtime result

The user-set final cold-smoke boundary was reached. The client did not reach
character selection. Origin PID `64928` established redirected TCP to
`127.1.1.110:7000`, but the server received no `LoginGameServer`, so no
CharacterSelection, AfterLogin, preview, or V4 preload code ran. No dump was
created. The evidence directory
`artifacts/network-shim/manual-parity/20260724T095739213Z-db16daa7` is sealed
`Fail`.

Rollback then completed in the enforced order: Net first, followed by Origin.
The exact Revert backups and current predecessor hashes are recorded above.
Loading gates V1–V4 remain unaccepted. Per the explicit product boundary, do
not iterate on the avatar preload now; continue Phase 2 with the issue parked.
