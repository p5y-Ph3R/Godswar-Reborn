# Holy Spirit Icon Set

This directory contains the reviewed artwork for the four Aether effects and
the thirteen additional Spirits proposed in
`docs/holy-spirit-combat-roadmap.md`.

The original sixteen offensive and defensive Spirits keep their stock icons
in `Icon2.gwo`. The new art is isolated in `Icon5.gwo` so it does not overwrite
the stock atlas, the locally patched elemental-stone cells in `Icon2.gwo`, or
the Level-5 forging assets in `Icon4.gwo`.

## Contents

- `source/`: 1024x1024 generation masters.
- `icons/`: authoritative 36x36 client sprites.
- `contact-sheet.png`: all sprites enlarged with nearest-neighbor scaling for
  small-size visual review.
- `manifest.json`: names, source hashes, atlas coordinates, existing item IDs,
  and proposed future item-ID reservations.

The four existing Aether item IDs are 9068, 9069, 9088, and 9089. The thirteen
currently unused client IDs 9070-9079 and 9090-9092 are recorded only as
proposed reservations. They must not be published as obtainable items until
their authoritative combat behavior and activation gates are complete.

## Rebuild and verify

```powershell
.\tools\PrepareHolySpiritIcons.ps1
.\tools\PrepareHolySpiritIcons.ps1 -Check
python .\tools\InstallHolySpiritIcons.py --dry-run
python .\tools\InstallHolySpiritIcons.py
python .\tools\InstallHolySpiritIcons.py --check
```

The installer clones the verified `Icon3.gwo` container, changes only the
manifest-owned cells, writes byte-identical `Icon5.gwo` files for `en_us` and
`zh_cn`, stages replacements atomically, records hashes, and refuses an
unexpected existing atlas unless `--force` is explicitly supplied after
inspection.

## Shared art direction and prompt

The set was generated with the built-in image-generation tool. Every icon used
the following shared production brief:

> A square MMORPG inventory icon intended to downscale cleanly to 36x36. Use a
> circular silver-and-antique-gold Greek-key talisman rim, dark navy-black
> vignette, hand-painted 2000s fantasy MMORPG finish, one centered dominant
> symbol, generous dark margin, minimal secondary detail, strong silhouette,
> and controlled glow. Do not include text, letters, numbers, watermarks,
> characters, scenery, a mockup, modern app-icon styling, or an external UI
> frame.

The stock Holy Spirit contact sheet was used only as loose stylistic context.
`Aether Spirit of Renewal` became the consistency anchor for the other sixteen
new images.

## Per-icon visual identity

| Spirit | Central symbol | Primary palette |
|---|---|---|
| Aether Renewal | Emerald life droplet rising from an amphora between laurel leaves | Emerald, teal, mint |
| Aether Ichor | Greek shield catching a crimson life droplet over a heartbeat spark | Crimson, emerald, bronze |
| Aether Flow | Continuous sapphire mana helix rising from an amphora | Sapphire, cyan, violet |
| Aether Serenity | Violet mana droplet settling into calm ripples above a small shield | Indigo, violet, cyan |
| Ares Prowess | Crossed spear and xiphos above three ascending chevrons | Crimson, orange, bronze |
| Hecate Nullification | Triple moon splitting one enchanted halo | Violet, magenta, moon-silver |
| Tyche Fortune | Eight-spoke Wheel of Fortune aligned with one lucky star | Gold, turquoise, ivory |
| Aegis Anchoring | Hoplon shield fused to a rooted stone anchor | Steel blue, stone, bronze |
| Nemesis Reckoning | Scales, stored red shard, spear, and one return arrow | Ruby, black iron, bronze |
| Aether Conservation | Sealed amphora preserving mana inside one return arrow | Sapphire, cyan, violet |
| Gaia Stability | Rooted Doric column breaking two control rings | Moss, ochre, marble, violet |
| Zephyr Reprieve | Wing sweeping around a wind-filled hourglass | Turquoise, white-silver, gold |
| Helios Purity | Solar disk dissolving one cursed droplet | Solar gold, ivory, violet-black |
| Nyx Exhaustion | Waning moon draining green and blue recovery wisps from a chalice | Indigo, muted green and blue |
| Nyx Lethargy | Bound sword and wand beneath a crescent moon | Violet, midnight blue, silver |
| Astrape Disruption | Lightning bolt splitting an active spell circle | Electric blue, cyan, violet |
| Chronos Delay | Hourglass suspending a red damage shard over delayed sparks | Bronze, crimson, cyan |

The images deliberately communicate mechanics rather than stone level. Holy
Stone level remains data and tooltip state; it does not require ten duplicate
icon variants per Spirit.
