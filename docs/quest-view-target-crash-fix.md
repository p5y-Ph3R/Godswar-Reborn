# QuestView target-change crash guard

## Symptom and diagnosis

The 2026-07-23 dump `20260723011507.dmp` records an x86 access violation at
`0x00493A4E`. The active path is a mouse/world-target change:

```text
0x0047E8FA -> 0x00493990 -> 0x004939B0 -> 0x00493A4E
```

The target-reset routine obtains the QuestView singleton at `0x005D9DD0` and
then unconditionally dereferences its UI roots at offsets `+8` and `+0x0C`.
The dump has a valid singleton in `ESI` but a null `+8` root in `ECX`.

This is not an invalid NPC packet. Object `5140` remained continuously inside
the player's streamed area, and the server stayed healthy. It is also separate
from the earlier accepted-quest snapshot crash at `0x005D1CC3`.

An empty opcode-10090 packet is not a fix. The native handler always calls the
separate quest-data refresh routine even when count is zero, and that routine
can itself dereference null controls. QuestView's roots are populated only by
the client UI lifecycle loader at `0x005D9E20`.

## Patch design

`tools/PatchClientQuestViewTargetGuard.ps1` replaces the single call at
`0x00493A44` with a trampoline. The trampoline calls the original singleton
getter, validates the singleton and both roots, then either:

- returns to `0x00493A49` for the untouched native hide/unregister block; or
- skips only that block to `0x00493A74` when a root is unavailable.

The guard deliberately does not invoke XML loading from the input/render path.
It changes one five-byte call site and uses 31 zero bytes in the executable
reserved cave at `0x009C3400`. Apply and revert are idempotent and create a
verified copy of the complete pre-write executable.

Installed on 2026-07-23:

- before SHA-256: `1BBD41D4E148E040B363D2A83D36CD326A2C2CFE1EA44E08DA6B2680CA1BB329`;
- after SHA-256: `753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79`;
- verified backup: `C:\Reborn\backups\origin-quest-view-target-guard-Apply-20260723-013639736\Origin.exe`.

## Apply or revert

Close the client, then run from `C:\Reborn`:

```powershell
.\tools\PatchClientQuestViewTargetGuard.ps1 -Mode Apply
```

To remove only this guard:

```powershell
.\tools\PatchClientQuestViewTargetGuard.ps1 -Mode Revert
```

## Acceptance check

Enter Sparta, repeatedly click the ground, ordinary NPCs, and the physical
Origin Enhancer. Confirm target changes no longer create a new dump at
`0x00493A4E`.
