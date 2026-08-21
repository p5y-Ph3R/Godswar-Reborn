# Client login-logo branding

The login screen uses `Localization/<locale>/UI/Texture/gamelogo.gwo`.
Despite its extension, the asset is a raw 512-by-512, 32-bit, top-origin TGA.
The Reborn asset preserves the main `GODSWAR` artwork and replaces only the
lower `ORIGIN` wordmark with `REBORN`.

The canonical installed payload is
`assets/client-branding/gamelogo-reborn.gwo`. The transparent source wordmark
is retained as `assets/client-branding/reborn-wordmark.png` so the branding
edit remains reviewable. It was generated with the built-in image generator
using the stock logo as a style reference: a transparent, compact, italic
gold-to-orange `REBORN` wordmark with a dark navy outline and subtle shadow.

Inspect without writing:

```powershell
.\tools\PatchClientLoginLogo.ps1 -Mode Status
```

Install transactionally:

```powershell
.\tools\PatchClientLoginLogo.ps1 -Mode Apply -AllowMutation
```

Apply requires both locale copies to match the reviewed pristine hash, writes
a verified backup under `artifacts/client-login-logo-backups`, and refuses to
run while `Origin.exe` is open. Rollback requires the exact backup directory:

```powershell
.\tools\PatchClientLoginLogo.ps1 -Mode Rollback `
  -RollbackFrom '<verified-backup-directory>' -AllowMutation
```
