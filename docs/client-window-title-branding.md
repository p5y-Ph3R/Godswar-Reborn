# Client window-title branding

The installed client does not embed `Godswar Origin` in `Origin.exe`. At
startup it reads the `AppTitle` localization record from each locale's
`Text/Message.dat`. Stock `Origin.exe` formats that value with the configured
`AreaTitle11`, `AreaTitle12`, or `AreaTitle13` record by using `%s %s`.

`config.ini` selects the active locale through `Locals` and the area record
through `Region`. The last-selected realm remains server data in
`Localization/en_us/Settings/User/LastSelectServer.xml`; it is not a branding
source and this patch never modifies it.

The native client independently appends ` - ` and the selected server/realm
name after realm selection. That behavior must remain dynamic: a Tempest
client displays `Godswar Reborn - Tempest`, while a Dwargon client displays
`Godswar Reborn - Dwargon`. Before selection, the title is exactly
`Godswar Reborn`.

The patch changes `AppTitle` and narrows only the reviewed title format at
`Origin.exe` file offset `0x554FCC` from `%s %s` to `%s`. It leaves every
`AreaTitle` record, the native ` - ` separator at `0x557904`, and server data
unchanged. The patcher validates the adjacent `AppTitle` key, PE header, and
dynamic separator before accepting the executable. The base title is also
bounded to 127 UTF-16 code units because the native destination buffer holds
128 code units including its terminator.

Use the fail-closed patcher from the repository root:

```powershell
./tools/PatchClientWindowTitle.ps1 -Mode Status `
  -ClientRoot 'C:\Godswar Origin'

./tools/PatchClientWindowTitle.ps1 -Mode Apply `
  -ClientRoot 'C:\Godswar Origin' -AllowMutation
```

Apply creates a hash-verified backup under
`artifacts/client-window-title-backups/<UTC timestamp>/` before replacing
the executable or either UTF-16LE localization asset. Its versioned manifest
links the preceding backup directory, preserving the rollback chain. It
reports the exact new directory. Restore chained backups in reverse order:

```powershell
./tools/PatchClientWindowTitle.ps1 -Mode Rollback `
  -ClientRoot 'C:\Godswar Origin' `
  -RollbackFrom '<reported backup directory>' -AllowMutation
```

`Message.dat` and the executable format are loaded during startup. The patcher
refuses to change the executable while any `Origin.exe` process is running; it
never stops or edits a running process. A patched client must be launched
again to show the new title.

Run the focused regression with:

```powershell
./tools/TestClientWindowTitlePatch.ps1
```
