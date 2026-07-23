# Developer mount command

The mount command is part of the existing local, account-allowlisted `/item`
developer command. It uses a separate mount catalog and persistence operation;
it does not widen the forging-material allowlist and it cannot generate an
arbitrary item ID.

## Commands

```text
/item mount list
/item mount list 2
/item mount list greeksteed
/item mount add 14224
/item mount add greeksteed 80
/item mount add greeksteed max
/item mount add greeksteed special
/item mount add erebuslion 80
```

`list` defaults to page 1. Pages contain four families. A family-specific list
shows its tier tokens and exact item IDs.

Every successful `add` creates exactly one bound, non-stackable Q1/G1 mount in
the first authoritative empty kit-bag slot. The item ID is validated again in
the JSON or PostgreSQL store. PostgreSQL writes the inserted row and its
`developer-mount-grant` audit entry in one transaction. A full bag rejects the
whole operation.

Use `/item`, not `/gmitem`, in the stock client. The client masks the word
`gmitem` before transmitting chat; the server retains the masked form only for
compatibility with the older material command.

## Standard mount families

Each standard family contains ten IDs. The first eight tokens are levels
`40,50,60,70,80,90,100,110`. `max` (also accepted as `120`) is the ordinary
level-120 endpoint at base ID + 8. `special` is the separate 50%-speed variant
at base ID + 9. In particular, `greeksteed max` is `14228`, while
`greeksteed special` is `14229`.

This is the complete standard catalog. Every cell is the exact item ID accepted
for that family/tier; it expands all 310 standard client mount templates rather
than hiding them behind ID ranges.

| Family alias | Client family | 40 | 50 | 60 | 70 | 80 | 90 | 100 | 110 | max/120 | special |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `greeksteed` | Greek Steed | 14220 | 14221 | 14222 | 14223 | 14224 | 14225 | 14226 | 14227 | 14228 | 14229 |
| `parnithaboar` | Parnitha Boar | 14240 | 14241 | 14242 | 14243 | 14244 | 14245 | 14246 | 14247 | 14248 | 14249 |
| `nemeanwolf` | Nemean Wolf | 14260 | 14261 | 14262 | 14263 | 14264 | 14265 | 14266 | 14267 | 14268 | 14269 |
| `africanlion` | African Lion | 14280 | 14281 | 14282 | 14283 | 14284 | 14285 | 14286 | 14287 | 14288 | 14289 |
| `reindeer` | Reindeer | 14300 | 14301 | 14302 | 14303 | 14304 | 14305 | 14306 | 14307 | 14308 | 14309 |
| `argentdragon-a` | Argent Armored Dragon A | 14320 | 14321 | 14322 | 14323 | 14324 | 14325 | 14326 | 14327 | 14328 | 14329 |
| `flyingcarpet` | Flying Carpet | 14340 | 14341 | 14342 | 14343 | 14344 | 14345 | 14346 | 14347 | 14348 | 14349 |
| `gw176motorcycle` | GW-176 Motorcycle | 14360 | 14361 | 14362 | 14363 | 14364 | 14365 | 14366 | 14367 | 14368 | 14369 |
| `plumpbirdie` | Plump Birdie | 14380 | 14381 | 14382 | 14383 | 14384 | 14385 | 14386 | 14387 | 14388 | 14389 |
| `argentdragon-b` | Argent Armored Dragon B | 14400 | 14401 | 14402 | 14403 | 14404 | 14405 | 14406 | 14407 | 14408 | 14409 |
| `atlanticleatherback` | Atlantic Leatherback | 14440 | 14441 | 14442 | 14443 | 14444 | 14445 | 14446 | 14447 | 14448 | 14449 |
| `asianurus` | Asian Urus | 14460 | 14461 | 14462 | 14463 | 14464 | 14465 | 14466 | 14467 | 14468 | 14469 |
| `blackbear` | Black Bear | 14480 | 14481 | 14482 | 14483 | 14484 | 14485 | 14486 | 14487 | 14488 | 14489 |
| `yellowgocart` | Yellow Go-Cart | 14490 | 14491 | 14492 | 14493 | 14494 | 14495 | 14496 | 14497 | 14498 | 14499 |
| `kitsune` | Kitsune | 14510 | 14511 | 14512 | 14513 | 14514 | 14515 | 14516 | 14517 | 14518 | 14519 |
| `butterfly` | Butterfly | 14520 | 14521 | 14522 | 14523 | 14524 | 14525 | 14526 | 14527 | 14528 | 14529 |
| `unicorn` | Unicorn | 16000 | 16001 | 16002 | 16003 | 16004 | 16005 | 16006 | 16007 | 16008 | 16009 |
| `magicbroom` | Magic Broom | 16020 | 16021 | 16022 | 16023 | 16024 | 16025 | 16026 | 16027 | 16028 | 16029 |
| `asianelephant` | Asian Elephant | 16040 | 16041 | 16042 | 16043 | 16044 | 16045 | 16046 | 16047 | 16048 | 16049 |
| `cunningcougar` | Cunning Cougar | 16060 | 16061 | 16062 | 16063 | 16064 | 16065 | 16066 | 16067 | 16068 | 16069 |
| `stormdragon` | Storm Dragon | 16080 | 16081 | 16082 | 16083 | 16084 | 16085 | 16086 | 16087 | 16088 | 16089 |
| `kharickylin` | Kharic Kylin | 16100 | 16101 | 16102 | 16103 | 16104 | 16105 | 16106 | 16107 | 16108 | 16109 |
| `phoenix` | Phoenix | 16120 | 16121 | 16122 | 16123 | 16124 | 16125 | 16126 | 16127 | 16128 | 16129 |
| `sakurabunny` | Sakura Bunny | 16130 | 16131 | 16132 | 16133 | 16134 | 16135 | 16136 | 16137 | 16138 | 16139 |
| `littlellama` | Little Llama | 16140 | 16141 | 16142 | 16143 | 16144 | 16145 | 16146 | 16147 | 16148 | 16149 |
| `sabertooth` | Sabertooth | 16150 | 16151 | 16152 | 16153 | 16154 | 16155 | 16156 | 16157 | 16158 | 16159 |
| `meowling` | Meowling | 16160 | 16161 | 16162 | 16163 | 16164 | 16165 | 16166 | 16167 | 16168 | 16169 |
| `scorpionking` | Scorpion King | 16170 | 16171 | 16172 | 16173 | 16174 | 16175 | 16176 | 16177 | 16178 | 16179 |
| `panda` | Panda | 16180 | 16181 | 16182 | 16183 | 16184 | 16185 | 16186 | 16187 | 16188 | 16189 |
| `owl` | Owl | 16190 | 16191 | 16192 | 16193 | 16194 | 16195 | 16196 | 16197 | 16198 | 16199 |
| `erebuslion` | Erebus Lion | 16200 | 16201 | 16202 | 16203 | 16204 | 16205 | 16206 | 16207 | 16208 | 16209 |

The two Argent Armored Dragon ranges have identical shipped English display
names. Their `-a` and `-b` aliases deliberately keep them unambiguous.

`erebuslion` is the locally authored black-lion family. `blacklion` and
`shadowlion` are accepted aliases. It clones the African Lion progression,
while every tier uses the new Erebus Lion ride visual. That visual is uniformly
scaled to `1.40x`, making it 40% larger in every dimension while retaining the
native proportions and rider animations.

## Legacy and timed client entries

The client also contains 32 legacy mount templates:

| Family alias | `base` | `1` | `2` | `3` | `4` | `5` |
|---|---:|---:|---:|---:|---:|---:|
| `legacygreeksteed` | 6000 | 6001 | 6002 | 6003 | 6004 | 6005 |
| `legacyparnithaboar` | 6010 | 6011 | 6012 | 6013 | 6014 | 6015 |
| `legacynemeanwolf` | 6020 | 6021 | 6022 | 6023 | 6024 | 6025 |
| `legacyxmasreindeer` | 6030 | 6031 | 6032 | 6033 | 6034 | 6035 |
| `legacyafricanlion` | 6041 | 6042 | 6043 | 6044 | 6045 | 6046 |

The remaining legacy timed entries are `legacynemeanwolf7d 7d` = `6026` and
`legacyafricanlion7d 7d` = `6040`.

The eight modern special entries are:

| Family alias | ID(s) | Tier token(s) |
|---|---:|---|
| `timedafricanlion` | 14420 | `30d` |
| `timedreindeer` | 14421, 14426 | `30d`, `7d` |
| `timedgw176` | 14422 | `30d` |
| `timedplumpbirdie` | 14423 | `30d` |
| `timedflyingcarpet` | 14424 | `30d` |
| `timedatlanticleatherback` | 14425 | `3d` |
| `orphanride14428` | 14429 | `7d` (list only) |

Item `14429` is malformed in the shipped English client data: its XML node is
named `Ride14428`, it has no English display-name row, and that key is already
used elsewhere. It appears in `list` so the client-data gap is not hidden, but
both alias and numeric `add` reject it.

Timed metadata comes from the client template. The current authoritative item
schema has no per-item expiry timestamp, so developer-generated timed mounts
do not expire yet. They are suitable for local visual/protocol testing, not a
production timed-item economy.

## Database persistence model

There intentionally is no separate `mounts` table. Mounts use the same item
instance model as weapons, armor, materials, and mount gear:

- `item_templates` contains the catalog/definition rows. Mount definitions
  have `kind = 'mount'`; the patched client catalog currently contributes 350
  rows.
- `character_items` contains mounts owned by a character. Its `prop_id` is the
  mount template ID. `user_id` refers to `character_base.id`, not directly to
  `accounts.id`.
- A bagged mount has `item_location = 1`; an equipped mount has
  `item_location = 0` and `slot_index = 20`.
- `/item mount add` inserts a bound Q1/G1 item into the first free bag slot and
  writes a `character_item_audit` row with source `developer-mount-grant`.
- `character_kitbag` is the older compact compatibility representation.
  `character_items` is authoritative, so new mount logic should not introduce
  a second ownership table or write only to the compact row.
- The active Riding/Ride visual is a runtime status selected from the equipped
  mount; it is not another owned-mount database row.

List all mount definitions stored by the server:

```sql
SELECT
    id,
    display_name,
    min_level,
    stats->>'Speed' AS speed,
    stats->>'ExpiredTime' AS expiry
FROM item_templates
WHERE kind = 'mount'
ORDER BY id;
```

List the mounts currently owned by characters and see whether each is bagged
or equipped:

```sql
SELECT
    a.username,
    cb.name AS character_name,
    ci.id AS item_instance_id,
    ci.prop_id AS mount_id,
    it.display_name,
    CASE
        WHEN ci.item_location = 0 AND ci.slot_index = 20 THEN 'equipped'
        WHEN ci.item_location = 1 THEN 'bag'
        ELSE 'other'
    END AS location,
    ci.slot_index,
    ci.item_quality,
    ci.item_grade,
    ci.bound
FROM character_items AS ci
JOIN character_base AS cb ON cb.id = ci.user_id
JOIN accounts AS a ON a.id = cb.account_id
JOIN item_templates AS it ON it.id = ci.prop_id
WHERE it.kind = 'mount'
ORDER BY a.username, cb.name, ci.item_location, ci.slot_index;
```

## Source and alias rules

The catalog is generated from the checked-in server projection of:

```text
C:\Godswar Origin\Localization\en_us\Settings\Sys\ItemBaseAttribute.xml
C:\Godswar Origin\Localization\en_us\Text\EquipName.dat
C:\Godswar Origin\Localization\en_us\Settings\Sys\Ride.ini
```

There are 350 client `Type="mount"` templates after installing Erebus Lion:
310 standard, 32 legacy, and 8 modern special entries. The command can add
349; only orphan `14429` is denied.

Numeric ID is canonical. The client reuses every standard family endpoint's
XML name key for its special variant (for example, both `14228` and `14229`
use `Ride14228`) and repeats several display names. The command therefore uses
an explicit family/range map and never resolves a mount from NameKey or display
name alone.
