# QuestView frame/update crash guard

## Diagnosis

`C:\Godswar Origin\Dump\20260809203841.dmp` records an x86 access
violation at `0x005DA4C3`: the QuestView frame/update function dereferenced a
null `+8` UI root. An earlier 2026-07-26 dump has the same instruction and
call stack, so the crash predates the Zephyr item definitions. Those item XML
records and their shared TGA icon also pass structural validation.

The existing target-change guard protects a different caller at `0x00493A44`.
It cannot protect the main-frame call into `0x005DA4C0`.

## Guard

[`tools/PatchClientQuestViewFrameGuard.ps1`](../tools/PatchClientQuestViewFrameGuard.ps1)
hooks the QuestView function entry at VA `0x005DA4C0` (file `0x1DA4C0`). It
first validates the owning QuestView pointer (`ESI`), then both lifecycle-owned
UI roots, and returns while any required pointer is unavailable. When all
three exist, it replays the exact displaced instructions and resumes at
`0x005DA4C5`.

The trampoline occupies 25 bytes and owns the first 32 bytes of a pinned
256-byte executable `.rdata` shared-cave region at VA `0x009C3F00` (file
`0x5C3F00`). Its owned range must be zero before apply and exact after apply;
the remaining bytes are deliberately left available to audited client patches.
This avoids the already reserved
`0x009C341F..0x009C347E` timeout/retry cave. The guard deliberately does not
load UI XML from the render/frame path.

Status is read-only. Apply and revert are idempotent, refuse partial or foreign
states, verify a full executable backup, and allow changes only in the five-byte
hook and 32-byte owned cave range.

```powershell
.\tools\PatchClientQuestViewFrameGuard.ps1 -Mode Status
.\tools\PatchClientQuestViewFrameGuard.ps1 -Mode Apply
.\tools\PatchClientQuestViewFrameGuard.ps1 -Mode Revert
```

## Automated verification

The test copies the supplied executable under `C:\Reborn\artifacts`, never
patches the source fixture, and verifies exact branches, all three null paths,
displaced bytes, mutation allowlists, idempotence, partial-state refusal, and
an exact apply/revert round trip.

```powershell
.\tools\TestClientQuestViewFrameGuardPatch.ps1
```

The live acceptance check remains: apply with the client closed, then repeat
login, character selection, world entry, and the action that previously
crashed. Confirm no new dump at `0x005DA4C3`.
