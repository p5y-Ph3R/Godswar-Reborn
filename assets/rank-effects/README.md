# Rank-effect package (rejected v1 history)

This directory records the rejected `reborn-greek-rank-effects-v1` package.
Its transactional mechanics and rollback checks worked, but the visual design
mixed animated-mesh roles and ignored the native UV-atlas semantics. The development
client was restored after review. Do not install or extend this package.

V2 is currently prototype-first and is defined by
[`../../docs/client-rank-effect-v2-role-map.md`](../../docs/client-rank-effect-v2-role-map.md).
Its first gate is one AR14 and one native Warrior WR10 prototype. That gate is
installed transactionally only in the normal development client for visual
review; the B20H client remains untouched. The separate gear text palette
remains active and unaffected.

## Historical commands

Run the deterministic builder from the repository root:

```powershell
python tools/BuildRankEffectPackage.py --client-root "C:\Godswar Origin"
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" --validate
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" --preflight
```

These commands do not install anything. The historical install command below
is retained only to document rollback mechanics; v1 is not approved to install.

The builder expects the protected stock state recorded before installation.
Do not rebuild from an already patched client: use the checked package as-is,
or restore the install backup first. This prevents an installed AR9
compatibility remap from being mistaken for pristine source data.

Historical install and verify sequence (do not run for v2):

```powershell
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" --install
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" --verify-installed
```

Restore a specific transactional backup with:

```powershell
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" `
  --restore "C:\Reborn\backups\rank-effects-<timestamp>"
```

## Rejected geometry source plan

Every entry below lists `(legacy rank/component)` in target slot order. This
mixing strategy is exactly what v2 rejects: the slots have semantic roles and
cannot be treated as interchangeable merely because their files parse.

| Target | Identity | Source slots |
|---|---|---|
| AR10 | Helios Aegis | `10/0`, `10/1`, `10/2` |
| AR11 | Hecate's Veil | `7/0`, `7/1`, `5/2` |
| AR12 | Gaia's Laurel | `6/0`, `6/1`, `2/2` |
| AR13 | Ares' Eclipse | `5/0`, `5/1`, `5/2` |
| AR14 | Olympian Apotheosis | `2/0`, `4/0`, `2/2` |

Rejected v1 WR10 uses the matching class family's rank-7 geometry: `0007` for Warrior
and Champion, `0207` for Priest, and `0057` for Mage. It is renamed to the
client's existing WR10 effect IDs (`0009`, `0209`, or `0059`). All referenced
textures are copied to private `reborn_*` names; missing legacy references use
the validated rank-7 canonical texture as a deterministic fallback.

The package covers both `Characters` and `Characters_New`, and both genders.
Generated manifests are sharded so every maintained file remains below the
repository's 20 KiB guideline.
