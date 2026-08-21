# Progressive talent tooltip projection

The client talent dialog historically multiplies the stored rank byte by the
per-rank scalar in `Skill.ini`. The server progression is piecewise, so the old
Champion-only `2.6x` resource workaround was exact only at rank 100 and
overstated ranks 1-99. The supported patch keeps every gameplay scalar at its
stock value and changes only the rank used by the two current-value and two
next-value tooltip formatters.

## Display contract

For a stored rank `r`, clamped to `0..100`, the display rank is:

```text
E(r) = r                 r <= 40
       2r - 40           41 <= r <= 60
       3r - 100          61 <= r <= 80
       5r - 260          81 <= r <= 90
       7r - 440          91 <= r <= 100
```

The current value uses `E(r)`. The next-level value uses
`E(min(r + 1, 100))`. The native formatter continues to multiply that display
rank by the unchanged stock scalar, including its original integer or percent
formatting path.

This is presentation-only. It does not change stored talent rank, rank-up
requests/costs, network packets, generated server content, database gameplay
revisions, or runtime stat formulas.

## Audited native seams

The supported `Origin.exe` is the 6,676,480-byte x86 PE32 build with source
SHA-256
`FB634307517770ED8C677503C7D6F9E0E51A5995AFAF1A9D19631F1EFE1B6683`.
Four complete rank-load/store sequences are replaced with near calls:

| Display path | VA | File offset | Helper entry |
|---|---:|---:|---:|
| next flat | `0x00608E29` | `0x00208E29` | `0x0093E380` |
| next percent | `0x00608F02` | `0x00208F02` | `0x0093E380` |
| current flat | `0x006090F4` | `0x002090F4` | `0x0093E38C` |
| current percent | `0x006091C8` | `0x002091C8` | `0x0093E38C` |

The 86-byte helper occupies an audited 96-byte zero range at file offset
`0x0053E380`, VA `0x0093E380`, in an executable section. The source binary has
no relative or absolute pointer-shaped reference into that range. The patched
binary has exactly the four calls above and SHA-256
`8FC6FB26B36227836B9C468083C07B331640DB216578387C6F27670F96F5DEDF`.

`CALL` adds four bytes to the stack, so the helper writes `[esp+0x18]`, which
is the caller's original `[esp+0x14]` temporary. EAX remains the talent source
pointer, ECX remains the replaced code's scratch/output register, all other
general-purpose registers and the stack are preserved. Each native continuation
performs FPU work or `sub esp,8` before any conditional use, so the replaced
instructions do not require flags preservation.

## Resource state

Apply restores English Champion talents 50-68 from the former tooltip-scaled
values to their stock scalars. The other 54 English talents are already stock
and remain byte-identical. The primary English file then contains all 73 stock
talent scalars and has SHA-256
`B837AF9450AC7130B64650E2302820336DB03FEF67B927C23818F9D9008C9A34`.

The installed Chinese resource has 72 native talent sections (it has no section
68), all already stock, with SHA-256
`25B8C5A4CB7F679769245241DD89295428C9F2F3B437E8B5F477162E9A8A8C4D`.
It is backed up and recorded in the receipt but is not rewritten, preserving
its bytes, timestamps, owner, and ACL.

Both resources remain strict UTF-16LE with BOM and CRLF. Unknown hashes,
missing sections, mixed Champion vectors, changed non-Champion values, partial
binary hooks, and occupied caves fail closed.

## Tooling

Run from the repository:

```powershell
Set-Location C:\Reborn
.\tools\PatchClientProgressiveTalentTooltips.ps1 -Mode Check
```

Before Apply or Rollback, close `Origin.exe`, `Launch.exe`, and `Patcher.exe`.
Mutation checks this twice and again before each changed file. The protected
`C:\Godswar Origin B20H` tree and any reparse-traversed client root are always
rejected. Backups must be outside the client tree.

```powershell
$result = .\tools\PatchClientProgressiveTalentTooltips.ps1 `
    -Mode Apply `
    -ClientRoot 'C:\Godswar Origin'
$result | Format-List
```

Apply backs up `Origin.exe` and both locale resources, stages exact outputs,
verifies hashes, and records a receipt. Any failure after a write triggers a
reverse-order, hash-fenced rollback. Reapplying the exact state is a no-op.

Rollback requires the exact receipt returned by Apply. It pins every receipt
before/after hash and length to the reviewed binary/resource profiles, verifies
all backups and current targets, skips the unchanged Chinese resource, and is
idempotent:

```powershell
.\tools\PatchClientProgressiveTalentTooltips.ps1 `
    -Mode Rollback `
    -ClientRoot 'C:\Godswar Origin' `
    -ReceiptPath $result.Receipt
```

For development acceptance, start `C:\Godswar Origin\Launch.exe` with
`C:\Godswar Origin` as its working directory only after Apply completes and
the development target is verified. No client launch is part of the patch
tool.

The disposable regression suite never writes the installed client:

```powershell
.\tools\TestClientProgressiveTalentTooltipsPatch.ps1
```

It covers all raw rank bytes `0..255` through both helper entries, exact
current/next formulas, the four call targets, cave xrefs, allowed-byte deltas,
all reviewed locale scalars, no-op Chinese metadata preservation, process and
B20H fences, receipt pinning, idempotence, injected multi-file failures, and
byte-exact Apply/Rollback recovery.
