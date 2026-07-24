# Client avatar-preview login crash

## Result

The installed `C:\Godswar Origin\Origin.exe` is patched against two audited
null-resource builders in the LOGIN/character-preview path. The patch also
repairs the stale initialization lifecycle that made an account switch fail on
the first launch and then work on a later attempt. A later incident found a
third, separate null-resource fault at `0x005F58BC`; the installed executable
patch does not guard that state-transition path.

Installed on 2026-07-22 with:

- Before SHA-256: `1F0AC79175718357590A7354378E808A7F446B763CD05EDF659359FD4D819CC6`
- After SHA-256: `1BBD41D4E148E040B363D2A83D36CD326A2C2CFE1EA44E08DA6B2680CA1BB329`
- Verified backup: `C:\Reborn\backups\origin-avatar-preview-guard-Apply-20260722-135817166\Origin.exe`
- Exact binary impact: 169 changed bytes in three hooks and three reserved
  executable caves

No server or database change is part of this fix.

The later companion `Net.dll` loading gate is documented in
[`client-avatar-preview-loading-gate.md`](client-avatar-preview-loading-gate.md).
Its v1 build failed live validation and was rolled back. V2 is now installed
with automated gates passing; live account-switch acceptance remains pending.
It retains the exact preview until readiness or a five-second guarded fallback,
but cannot guarantee a model if resources remain unavailable.

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

The client builds male and female selection resources through the LOGIN
object's native initializer at VA `0x00467280`. It sets byte
`0x01575F70 = 1` when entering the world and intentionally unloads the
selection resources afterward. The flag has no native reset. When state 2
installs LOGIN again, the shared dispatcher sees the stale value and skips the
initializer even though the avatar roots have been cleared. Preview packets
can then reach either builder with null resources.

## Patch design

`tools/PatchClientAvatarPreviewGuard.ps1` applies all three pieces as one
transaction:

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

Both guards are fail-closed. A packet that still wins a narrow initialization
race can skip that one preview build instead of dereferencing null. Neither
guard sleeps, re-enters the loader, or runs initialization from the render
path. The installed v2 shim attempts to address the consequence of that safe
skip by retaining only the audited preview message while continuing native
network processing.

The patcher refuses to write unless the client is closed and all of the
following match the audited build: file size, DOS/PE headers, x86 PE32 machine,
image base, executable section mappings, hook bytes, continuations,
epilogues, fault sites, and empty or exact cave state. It verifies a full
backup hash before writing, scans the complete post-write file for mutations
outside the six allowed ranges, and supports idempotent apply/revert.

## Apply or revert

From `C:\Reborn`:

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

## Runtime acceptance check

Repeat this sequence several times because the original symptom was
intermittent:

1. Open the client, log in to account 7, confirm the character preview, and
   enter the world.
2. Leave that session and log in to account 13 without requiring a failed
   first attempt.
3. If the resource race occurs, confirm the screen remains responsive in its
   loading state and the model appears automatically before the candidate's
   five-second fallback. A fallback with a blank model is safe but does not
   pass the desired loading result.
4. Confirm account 13's preview appears and the client enters the world.
5. Repeat the account switch at least five times.
6. Confirm no new file appears in `C:\Godswar Origin\Dump` and no new
   `0x005F4ADD`, `0x005F060E`, or `0x005F58BC` entry is appended to
   `Error.log`.

The executable's two installed guards have static and disposable-binary
validation. Loading-gate v1 failed; v2 is installed with automated gates
passing but remains pending the interactive sequence.
