# Mount Speed tooltip

The equipped-mount hover already follows the native generic `ItemView` path.
Control `110021` resolves the real equipped item, and the base-stat renderer
indexes the template's `Speed` vector by the item's quality. The stock
`MountFunc()` callback is therefore retained.

The missing Boundless Speed line was a client parser defect, not a tooltip
layout defect. The audited client parsed comma-delimited `Speed` values before
the last comma as floating point, but parsed the final value as an integer.
For Erebus Lion `16204`, the former XML's final `0.25` became runtime `0`;
`ItemView` correctly suppresses zero-valued stats. The redesigned Boundless
value is `0.54` and uses the same fixed decimal terminal-token path.

A read-only live-process check confirmed:

- equipped item `16204` was quality `20`, grade `25`;
- its resolved template was `16204`, with twenty Speed entries;
- entries 1-19 matched XML, while entry 20 was `0`;
- the same template's Q20 MaxHP loaded as `4000`;
- mount-head `14504` loaded its Q20 Hit value as `43`.

`tools/PatchClientMountSpeedTooltip.ps1` changes only the final Speed token to
use the same floating-point converter as the preceding tokens and removes the
now-invalid integer materialization. It does not hard-code a speed value, so
each mount continues to use its authored XML data at every quality.

Apply while `Origin.exe` is closed, then verify:

```powershell
.\tools\PatchClientMountSpeedTooltip.ps1
.\tools\PatchClientMountSpeedTooltip.ps1 -Check
.\tools\TestClientMountSpeedTooltipPatch.ps1
```

The installer validates the audited parser and renderer byte sequences,
refuses partial or unknown states, creates a timestamped executable backup,
and is idempotent.
