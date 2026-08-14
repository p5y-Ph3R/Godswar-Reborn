# Phoenix Growth comparison client

`tools/PatchClientPetGrowthResetDialog.ps1` installs the guarded Phoenix
Growth preview in both `en_us` and `zh_cn`. It does not read Pet Detail or
guess the current values from client memory.

## Result contract

The server sends page `130`, followed in exact stat order
Agility/Strength/Accuracy/Technique/Wisdom/Luck by:

- six proposed Growth values with suffixes `08..13`;
- six authoritative current Growth values with suffixes `20..25`.

Each value uses the native encoding
`round(value * 100) * 100 + suffix`. Suffixes `14..19` remain reserved for
the stock Added Value page `140`. The twelve data records map to the twelve
existing `FirstWin_Button1..12` controls, so the patch adds no invented UI
element and never addresses a row beyond the native layout. The stock callback
also counts the page heading as `BtnID=1`, so the patch deliberately ignores
that ordinal for data placement: suffixes `08..13` map to buttons `1..6` and
suffixes `20..25` map to buttons `7..12`. Otherwise current Luck would attempt
to use nonexistent `FirstWin_Button13`, and the total calculation would never
run.

## Layout

- proposed values: left column at `x=25`, existing orange value color;
- current values: right column at `x=340`, teal value color;
- proposed and current totals: existing `FirstWin_Text2` at `y=205`;
- Reset: bottom-left at `(25,240)`;
- OK and Cancel: bottom-right at `(440,240)` and `(515,240)`.

The client totals the same six decoded two-decimal values it displays. This
keeps each total equal to the visible rows without requiring records thirteen
and fourteen. English and Chinese labels are selected from the loaded Growth
label; Chinese UTF-8 labels are assembled from explicit byte values so the
PowerShell installer remains encoding-safe on Windows PowerShell.

The Fairy/Savvy result remains Reset-only at `(400,100)` and has no Phoenix
comparison state.

## Guards and verification

The installer accepts only known dialogue hashes and the exact 600-by-290
`NpcFun.xml` layout hash. Both locales must have the same state. Apply and
Revert stage both locale files, verify their exact target hashes, and restore
verified predecessors if either install fails. `Origin.exe` must be closed
for a mutating operation; Status is read-only.

Run the focused check with:

```powershell
& .\tools\TestClientPetGrowthResetDialogPatch.ps1
```
