# Client armor and weapon rank-effect redesign

## Active design

The local development client now has distinct armor effects above the stock
AR9 butterfly effect and a class-specific effect at the WR10 cap. Rank score
thresholds and server effect IDs are unchanged.

| Rank | Score | Identity | Visual direction |
|---|---:|---|---|
| AR9 | 5250 | Stock butterfly | Preserved exactly through private legacy texture references |
| AR10 | 8000 | Helios Aegis | Compact white-gold solar rings |
| AR11 | 12000 | Hecate's Veil | Violet/cyan arcane orbits |
| AR12 | 17000 | Gaia's Laurel | Emerald spiral and laurel energy |
| AR13 | 22000 | Ares' Eclipse | Broken crimson eclipse aura |
| AR14 | 25300 | Olympian Apotheosis | White-blue and gold crown/pillar aura |

WR8 and WR9 remain unchanged. WR10 keeps the native IDs already selected by
`ItemBaseAttribute.xml`, but each class now has its own self-contained package:

| Class | Family and effect ID | WR10 identity |
|---|---|---|
| Warrior | one-hand `0009` | Ares' Emberblade |
| Champion | two-hand `0009` | Zeus' Stormlance |
| Priest | one-hand `0209` | Apollo's Radiance |
| Mage | two-hand `0059` | Hecate's Aether |

## Why AR9 needs compatibility handling

The stock AR9 JCS geometry refers to texture names also used by later armor
ranks. Directly replacing those shared files changes AR9 as a side effect.
During installation, the tool copies the original dependencies to
`legacy_body_effect_0010.tga` and `legacy_body_effect_0011.tga`, rewrites only
complete binary-X string tokens in AR9, and verifies that its structural
fingerprint did not change. AR10 through AR14 then use private `reborn_*`
textures. Lower weapon ranks receive no compatibility rewrite and remain
byte-for-byte protected.

The client formats are:

- `.jcs`: MSZIP-compressed binary XOF emitter/geometry data.
- `.gwo` and `.tga`: 24-bit or 32-bit TGA image payloads.

The package covers both `Characters\effect` and `Characters_New\effect`, and
both male and female variants. Each generated JCS may only refer to textures
owned by its effect record. Protected files, package hashes, model structure,
TGA bounds, target names, and active-client state are checked before a
transactional install.

## Source and tooling

- `assets/rank-effects/rank-effect-manifest.json` is the package entry point.
- `assets/rank-effects/README.md` records the reviewed geometry source plan.
- `assets/rank-effects/concepts/` contains the generated visual references.
- `assets/rank-effects/generated/` contains deterministic texture masters,
  previews, protected-stock shards, and effect manifests.
- `assets/rank-effects/package/` contains the exact install payload.
- `tools/GenerateRankEffectTextures.py` regenerates texture masters.
- `tools/BuildRankEffectPackage.py` builds from a pristine protected client.
- `tools/RankEffectPackages.py` validates, preflights, installs, verifies, and
  restores the package.

Validation commands:

```powershell
python tools/TestRankEffectTextures.py
python tools/TestRankEffectPackageFramework.py
python tools/RankEffectPackages.py --validate
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" --preflight
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" --verify-installed
```

The 2026-08-08 local development install has rollback backup
`C:\Reborn\backups\rank-effects-20260808T060956Z`. This backup is workstation
state and is intentionally excluded from Git. The B20H client and observation
containers were not changed.

## Visual acceptance

Automated checks prove package integrity, structural distinction, palette
distinction, lower-rank protection, dual-tree coverage, and rollback behavior.
They cannot prove that particle scale and readability look ideal in motion.
Final acceptance therefore requires an in-game pass at AR9 through AR14 and
WR8 through WR10 for each class, using `C:\Godswar Origin\Launch.exe`.
