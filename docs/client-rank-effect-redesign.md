# Client armor and weapon rank-effect redesign (v1 rejected)

## Current status

The design documented below was installed for an in-game review and rejected.
It mixed animated-mesh roles and used generic palettes that did not respect the
native UV-atlas layout. The local development client has been restored from
`C:\Reborn\backups\rank-effects-20260808T060956Z`, and all 188 transactional
targets were verified against their pre-install state.

After that clean restoration, the isolated AR14/Warrior-WR10 v2 prototype was
installed transactionally for visual review. Its state and separate rollback
are recorded in [`client-rank-effect-v2-role-map.md`](client-rank-effect-v2-role-map.md).

Do not reinstall or extend this v1 package. The evidence-based replacement is
documented in [`client-rank-effect-v2-role-map.md`](client-rank-effect-v2-role-map.md).
The separate quality/grade/elemental text palette was not part of this rollback
and remains unchanged.

## Rejected v1 design

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

## Historical AR9 compatibility handling

The stock AR9 JCS geometry refers to texture names also used by later armor
ranks. Directly replacing those shared files changes AR9 as a side effect.
During installation, the tool copies the original dependencies to
`legacy_body_effect_0010.tga` and `legacy_body_effect_0011.tga`, rewrites only
complete binary-X string tokens in AR9, and verifies that its structural
fingerprint did not change. AR10 through AR14 then use private `reborn_*`
textures. Lower weapon ranks receive no compatibility rewrite and remain
byte-for-byte protected.

The client formats are:

- `.jcs`: MSZIP-compressed binary-X animated mesh/geometry data.
- `.gwo` and `.tga`: 24-bit or 32-bit TGA image payloads.

The package covers both `Characters\effect` and `Characters_New\effect`, and
both male and female variants. Each generated JCS may only refer to textures
owned by its effect record. Protected files, package hashes, model structure,
TGA bounds, target names, and active-client state are checked before a
transactional install.

## Historical source and tooling

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

These commands describe the historical package mechanics and are retained for
forensics and rollback testing. They are not approval to install v1:

```powershell
python tools/TestRankEffectTextures.py
python tools/TestRankEffectPackageFramework.py
python tools/RankEffectPackages.py --validate
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" --preflight
python tools/RankEffectPackages.py --client-root "C:\Godswar Origin" --verify-installed
```

The rejected 2026-08-08 local development install used rollback backup
`C:\Reborn\backups\rank-effects-20260808T060956Z`. This backup is workstation
state and is intentionally excluded from Git. It has now been restored. The
B20H client and observation containers were not changed.

## Why automated validation was insufficient

Automated checks proved package integrity, structural distinction, palette
distinction, lower-rank protection, dual-tree coverage, and rollback behavior.
They did not prove that a source slot still performed the same visual role or
that a replacement texture respected that slot's UV region. The in-game pass
caught that failure. V2 therefore validates role semantics before expanding
beyond one armor and one weapon prototype.
