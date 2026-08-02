# Class Suit IV weapons

The original client defines independent Class Suit IV item IDs, names, and
stats, but ships their legacy weapon models and textures as exact Class Suit
III duplicates. The high-resolution `Characters_New` tree contains no Class
Suit IV weapon assets. Reborn supplies deterministic models and textures for
all four classes without changing item IDs, packets, or database rows.

## Visual designs

| Class | Tier III to IV | Geometry | Palette |
|---|---:|---|---|
| Warrior | `1034` to `1035` | Broad Olympian leaf blade with strong shoulders and a narrow point | Crimson, hot highlights, bright gold |
| Champion | `1434` to `1435` | Long Celestial spear with a double-diamond head | Cyan, silver-white, bright gold |
| Priest | `1734` to `1735` | Wide Divine sun-crown scepter | Emerald, ivory, bright gold |
| Mage | `1834` to `1835` | Asymmetric Astral crescent wand | Violet, magenta energy, cold platinum |

The models use topology-preserving sculpting. Vertex positions and silhouettes
are substantially different, but face indices, UVs, materials, frame matrices,
and attachment data remain unchanged. Existing normals are recalculated after
the sculpt. The protected grip portion is not reshaped.

## Generate and verify

From `C:\Reborn`:

```powershell
python tools/TestXModelSculpt.py
python tools/GenerateClassSuitIvWeaponModels.py
python tools/GenerateClassSuitIvWeaponModels.py --check
# Run these two texture commands before the first installation. The stock
# duplication audit is a historical pre-install snapshot.
python tools/GenerateClassSuitIvWeaponTextures.py --audit-stock
python tools/GenerateClassSuitIvWeaponTextures.py --check --audit-stock
```

Reproducible outputs are written beneath:

```text
artifacts/class-suit-iv-weapon-models/
artifacts/class-suit-iv-weapons/staged/
```

The model stage contains 16 `.jcs` models and 16 before/after SVG previews.
The texture stage contains 16 `.gwo` textures and eight PNG texture previews.
Both stages include manifests with reviewed source and output hashes.

## Install and rollback

Close `Origin.exe` and `Launch.exe`, then run:

```powershell
python tools/InstallClassSuitIvWeaponAssets.py --install
python tools/GenerateClassSuitIvWeaponTextures.py --check
python tools/InstallClassSuitIvWeaponAssets.py --check
```

Installation writes exactly 32 targets: male and female model/texture pairs for
four classes in both `Characters` and `Characters_New`. Every existing target
is copied to a timestamped directory beneath `C:\Reborn\backups` first. Targets
that did not previously exist are recorded explicitly.

Restore a particular installation with:

```powershell
python tools/InstallClassSuitIvWeaponAssets.py --restore `
  C:\Reborn\backups\class-suit-iv-weapons-YYYYMMDDTHHMMSSZ
```

If any write or post-write verification fails, the installer immediately
restores that backup. It rejects absolute, parent-relative, and unexpected
manifest paths.

## Validation boundary

Offline checks prove:

- every reviewed source and generated output has the pinned SHA-256 hash;
- only model positions and normals change;
- all model topology, UV, material, frame, and attachment bytes are preserved;
- each source contains exactly one rigid mesh;
- texture dimensions, orientation, alpha, and encoding remain valid;
- male and female targets exist for both client render trees; and
- all four model silhouettes and texture palettes are distinct.

The remaining manual checks are both genders and all four classes in character
selection and in-world, including equip/unequip, relog, movement, attacks,
skills, mounts, death/revive, map transfer, and another player's view.
