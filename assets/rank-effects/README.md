# Rank-effect package

This package gives AR10 through AR14 and each class's WR10 a private,
self-contained palette while preserving stock AR9 and WR1 through WR9.

Run the deterministic builder from the repository root:

```powershell
python tools/BuildRankEffectPackage.py --client-root "C:\Godswar Origin"
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" --validate
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" --preflight
```

These commands do not install anything. Installation remains a separate,
explicit `RankEffectPackages.py --install` operation.

The builder expects the protected stock state recorded before installation.
Do not rebuild from an already patched client: use the checked package as-is,
or restore the install backup first. This prevents an installed AR9
compatibility remap from being mistaken for pristine source data.

Install and verify with the client closed:

```powershell
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" --install
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" --verify-installed
```

Restore a specific transactional backup with:

```powershell
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" `
  --restore "C:\Reborn\backups\rank-effects-<timestamp>"
```

## Geometry source plan

Every entry below lists `(legacy rank/component)` in target slot order. The
mixtures were selected because the stock AR10-AR14 files repeat geometry.
They produce distinct normalized silhouettes without touching AR9.

| Target | Identity | Source slots |
|---|---|---|
| AR10 | Helios Aegis | `10/0`, `10/1`, `10/2` |
| AR11 | Hecate's Veil | `7/0`, `7/1`, `5/2` |
| AR12 | Gaia's Laurel | `6/0`, `6/1`, `2/2` |
| AR13 | Ares' Eclipse | `5/0`, `5/1`, `5/2` |
| AR14 | Olympian Apotheosis | `2/0`, `4/0`, `2/2` |

WR10 uses the matching class family's rank-7 geometry: `0007` for Warrior
and Champion, `0207` for Priest, and `0057` for Mage. It is renamed to the
client's existing WR10 effect IDs (`0009`, `0209`, or `0059`). All referenced
textures are copied to private `reborn_*` names; missing legacy references use
the validated rank-7 canonical texture as a deterministic fallback.

The package covers both `Characters` and `Characters_New`, and both genders.
Generated manifests are sharded so every maintained file remains below the
repository's 20 KiB guideline.
