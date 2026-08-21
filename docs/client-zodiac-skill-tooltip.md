# Client Zodiac skill-slot tooltip

## Outcome

The installed English and Chinese client scripts safely render Type-2
`Improve ATK Skill` slots through level 50.

The shipped `SkillTrainConfig.lua` has fifty `SkillEff` values but only
forty-five Type-2 `MP` display values. Its final MP value is `300%`. The stock
hover code indexes `MP[level]` and `MP[level + 1]` directly, so level 45 fails
while building its next-level section and levels 46 through 50 fail while
building their current-level section. Lua stops before `UIAPI:Helper`, leaving
no popup.

The patch changes only `SkillTrainProc.lua` in `en_us` and `zh_cn`:

- Type-2 grids 4 through 7 cap MP display lookup at authored level 45.
- Levels 45 through 50 therefore display `300%` MP without a nil lookup.
- `SkillEff` continues to use the real level, including `118%` at level 49
  and `120%` at level 50.
- Other grid types and Type-2 levels below 45 keep their original lookup.
- The current-level label uses `/50` instead of the stock `/40` typo.

This is a client display repair. It does not change Zodiac persistence,
packets, upgrade costs, selection state, or server combat projection.

## Installer

Status is read-only:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  .\tools\PatchClientZodiacSkillTooltip.ps1 `
  -ClientRoot 'C:\Godswar Origin' -Mode Status
```

Apply the repair with the client, launcher, and patcher closed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  .\tools\PatchClientZodiacSkillTooltip.ps1 `
  -ClientRoot 'C:\Godswar Origin' -Mode Apply
```

Revert to the pinned shipped scripts:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  .\tools\PatchClientZodiacSkillTooltip.ps1 `
  -ClientRoot 'C:\Godswar Origin' -Mode Revert
```

Apply and Revert each create a verified two-locale backup and `manifest.json`
under `backups/client-zodiac-skill-tooltip-*`. Writes are staged, both locale
states must agree, and any catchable install error restores the verified
predecessors. Repeated Apply or Revert is idempotent and creates no extra
backup.

## Verification

The isolated fixture test covers both locales, byte-exact Apply/Revert,
idempotency, mixed and foreign state rejection, UTF-8 BOM and CRLF
preservation, backup contents, and the authored level 45-50 MP/effect tables:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  .\tools\TestClientZodiacSkillTooltipPatch.ps1 `
  -FixtureRoot 'C:\Godswar Origin'
```
