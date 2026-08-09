# Client character speed statistics

## Outcome

The original character-stat dialog now has two server-authoritative entries
in the native two-column combat-stat grid:

- **Movement Speed** is the character's current locomotion multiplier. `1.00`
  is rendered as `100%`; a mounted `1.50` is rendered as `150%`.
- **Riding Speed** was designed to show the equipped mount's quality/grade
  bonus, but its original wire source has been disabled pending a safe
  client-owned side channel. It must not reuse opcode 10166 offset 60.

The English labels are compact `M.Speed` and `R.Speed`; the Chinese labels
are `移速` and `骑速`. Hovering either label opens the game's standard helper
tooltip with the full localized name (`Movement Speed` / `Riding Speed`, or
`移动速度` / `骑乘速度`).

`M.Speed` sits below Healing in the left column and `R.Speed` sits below
Absorb in the right column. Both use Y `517..533`, preserving the native
26-pixel row cadence. The left and right stat backgrounds now end at Y 536,
three pixels below the row; there is no separate `SpeedBack`.

`PersonalInfo` is extended from `100,100,363,626` to
`100,100,363,652`. UI rectangles are `left,top,right,bottom`; the resulting
552-pixel height leaves the stock 16-pixel bottom inset after the backgrounds.
Existing statistics and buttons do not move.

| Entry | Label | Value | Percent suffix |
| --- | --- | --- | --- |
| Movement | `24,517,78,533` | `85,517,111,533` | `113,517,125,533` |
| Riding | `137,517,200,533` | `210,517,234,533` | `236,517,246,533` |

The patch updates both `en_us` and `zh_cn` layouts and installs one owned
`PersonalInfoSpeedStats.lua` file per locale. The XML uses the client's
established `CanHovered`, `OnHovered`, and `OnLeft` callbacks; the Lua binds
`local uiapi=UIAPI`, displays through `uiapi:Helper`, and clears the helper
when the pointer leaves. This avoids altering the native PersonalInfo hover
dispatcher, whose audited control-ID table ends at 281124.

## Wire and client mapping

`MSG_SYN_GAMEDATA` (`10166`) copies 34 dwords from wire offset 8 into the
client game-data object starting at `+0x25C`.

| Meaning | Wire | Client game data | Display control |
| --- | ---: | ---: | --- |
| Current locomotion multiplier | `f32 @ 56` | `+0x28C` | `spouseText` / controller `+0x180` |
| Reserved native interaction identity | `u32 @ 60` (must be zero) | `+0x290` | Not a display field |
| Credit, preserved | `u32 @ 64` | `+0x294` | Existing Credit row |

The PersonalInfo refresh trampoline is installed at VA `0x005B5B97`. It
rounds each float after multiplying by 100, formats it with the client's
existing wide integer formatter, updates the two reused hidden controls, and
returns through the original epilogue at `0x005B5BD4`.

The current server deliberately reports zero through the old Riding Speed
source. A later client patch must derive the value from equipped-mount state or
consume a separately validated extension before the row is re-enabled.

## Shared executable cave

The final executable `.rdata` cave is explicitly partitioned:

| Owner | File range | VA range | Bytes |
| --- | --- | --- | ---: |
| QuestView frame guard | `0x5C3F00-0x5C3F1F` | `0x009C3F00-0x009C3F1F` | 32 |
| Character speed stats | `0x5C3F20-0x5C3F9F` | `0x009C3F20-0x009C3F9F` | 128 |
| Fashion Show auto-check | `0x5C3FA0-0x5C3FFF` | `0x009C3FA0-0x009C3FFF` | 96 |

Each installer validates and mutates only its owned range. Tests cover both
installation orders, independent reverts, V1/V2-to-V3 layout migration,
idempotence, exact localized Lua ownership, and unknown binary/XML/Lua state
rejection. Upgrading either earlier speed layout does not change the
already-installed executable trampoline or hover scripts.

## Commands

```powershell
# Verify the patch without writing
.\tools\PatchClientCharacterSpeedStats.ps1 -Mode Status

# Install to the development client (Origin.exe must be closed)
.\tools\PatchClientCharacterSpeedStats.ps1 -Mode Apply

# Revert only this patch; QuestView remains untouched
.\tools\PatchClientCharacterSpeedStats.ps1 -Mode Revert

# Isolated fixture coverage
.\tools\TestClientQuestViewFrameGuardPatch.ps1
.\tools\TestClientCharacterSpeedStatsPatch.ps1
```

The installer creates verified backups under `backups/` before every actual
apply or revert. Backups include `Origin.exe`, both localized XML files, and
any existing owned Lua files. It rejects partial binary/XML/Lua states and
does not write from `Status` mode.
