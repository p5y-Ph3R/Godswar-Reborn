# Installed-client pet catalog

This catalog uses the stable numeric type in `Pet.xml` and `Pet_Confect.xml`.
Food values are Herbivore, Carnivore, and Omnivore. Runtime starter-skill IDs
are not skill-book item IDs.

| Type | Pet | Food | Starter runtime skill | Life | Egg | Magic Jade |
|---:|---|---|---|---:|---:|---:|
| 1 | Rock Elf | Omnivore | 405 Life Totem I | 600 | 10150 | 11050 |
| 2 | Flower Pixie | Herbivore | 805 Pixie Dust I | 400 | 10151 | 11051 |
| 3 | Minotaur | Carnivore | 605 Tear I | 400 | 10152 | 11052 |
| 4 | Panda | Omnivore | 2005 Extraction I | 1200 | 10153 | 11053 |
| 5 | Easter Bunny | Herbivore | 1005 Concentration I | 900 | 10154 | 11054 |
| 6 | Puppet | Herbivore | 705 Immortal Kiss I | 500 | 10155 | 11055 |
| 7 | Wing Race | Omnivore | 608 Feather Blade I | 600 | 10156 | 11056 |
| 8 | Ghost | Carnivore | 808 Dark Vengeance I | 1500 | 10157 | 11057 |
| 9 | Merman | Omnivore | 2700 Ocean Sphere I | 1200 | 10158 | 11058 |
| 10 | Loyal Dog | Carnivore | 1205 Guard I | 500 | 10159 | 11059 |
| 11 | Tiger Baby | Carnivore | 2711 Tiger's Roar I | 1500 | 10160 | 11060 |
| 12 | Blue Crystal Dragon | Carnivore | 2800 Iceshot I | 1500 | 10161 | 11061 |
| 13 | Dodo | Herbivore | 2900 Eagle Eye I | 1200 | 10162 | 11062 |
| 14 | Elf Guardian | Omnivore | 3000 Magic Barrier I | 1200 | 10163 | 11063 |
| 15 | Wandering Spirit | Omnivore | 3100 Evasion I | 1200 | 10164 | 11064 |
| 16 | Young Yeti | Herbivore | 454 Frozen Blessing I | 1200 | 10165 | 11065 |
| 17 | Sphinx | Carnivore | 1930 Sphinx's Enigma I | 1200 | 10166 | 11066 |
| 18 | Lil QT | Herbivore | 530 Mind Refresh I | 1200 | 10167 | 11067 |
| 19 | Impi | Omnivore | 3124 Imp Trick I | 1200 | 10168 | 11068 |
| 20 | Hell Hound | Carnivore | 3148 Mean Streak I | 1200 | 10169 | 11069 |
| 21 | Troodon | Carnivore | 3172 Primal Spirit I | 1200 | 10170 | 11070 |
| 22 | Poison Cactus | Omnivore | 3300 Prick I | 1200 | 10171 | 11071 |
| 23 | Angelic | Omnivore | 3500 Penalty of Justice I | 1200 | 10172 | 11072 |
| 24 | Kung-Fu Kenny | Omnivore | 3700 Palm Sweep I | 1200 | 10173 | 11073 |
| 25 | Cretan Bull | Herbivore | 3900 Wild Bump I | 1200 | 10174 | 11074 |
| 26 | Gryphon | Carnivore | 4100 Fury of Justice I | 1200 | 10175 | 11075 |
| 27 | Jungle Boar | Herbivore | 4300 Gnarl I | 1200 | 10176 | 11076 |
| 28 | Spirit Cat | Carnivore | 4400 Spirit Strength I | 1200 | 10177 | 11077 |
| 29 | Totoro | Herbivore | 4500 Wild Strength I | 1200 | 10178 | 11078 |
| 30 | Fox Spirit | Omnivore | 4700 Mesmerise I | 1200 | 10179 | 11079 |
| 31 | Platypus | Carnivore | 4600 Focus I | 1200 | 10180 | 11080 |
| 32 | Hops | Carnivore | 5100 Ward I | 1200 | 10181 | 11081 |
| 33 | Monkey | Omnivore | 4800 Bullseye I | 1200 | 10182 | 11082 |
| 34 | Mouse | Omnivore | 4900 Scurry I | 1200 | 10183 | 11083 |
| 35 | Maneater Flower | Omnivore | 5300 Magic Strength I | 1200 | 10184 | 11084 |
| 36 | Penguin | Herbivore | 5000 Block I | 1200 | 10185 | 11085 |
| 37 | King Lion | Carnivore | 5200 Violent Strength I | 1200 | 10186 | 11086 |
| 38 | Thunder Pixie | Herbivore | 5400 Discharge I | 1200 | 10187* | 11087 |
| 39 | Bloodmoon Fox | Carnivore | 5500 Eclipse I | 1200 | 10188* | 11088 |
| 40 | Kratortle | Carnivore | 5600 Resolute Physique I | 1200 | 10189* | 11089 |
| 41 | Beelzeebub | Carnivore | 6100 Magission I | 1200 | 10190* | 11090 |
| 42 | Billy Bear | Omnivore | 6200 Sacrifice I | 1200 | 10191 | 11091 |
| 43 | Roly Poly | Herbivore | 6300 Lifedrain I | 1200 | 10192 | 11092 |
| 44 | Hedgehog | Carnivore | 6000 Spiky Armor I | 1200 | 10193 | 11093 |
| 45 | Cupid | Carnivore | 6000 Spiky Armor I* | 1200 | none | 11094 |

`Life` is the first configured creation-profile value. Some early species have
additional aptitude-specific lifetime values; instance lifetime remains
persistent and must not be recomputed from this column after creation.

## Client compatibility warnings

- Egg `10187` is named Thunder Pixie but declares type `36`, not `38`.
- Egg `10188` is named Bloodmoon Fox but declares type `37`, not `39`.
- Egg `10189` is named Kratortle but declares type `38`, not `40`.
- Egg `10190` is named Beelzeebub but declares type `39`, not `41`.
- Cupid has no egg. Magic Jade `11094` is its clean acquisition mapping.
- Cupid is configured with Hedgehog's `6000 Spiky Armor I`; this is retained
  as shipped data until a verified correction is chosen.
- `Pet_Alter.xml` repeats the XML element name `Type44` for types 44 and 45.
  Parsers must use the `PetType` attribute.
- `Pet.ini` is obsolete and only covers types 1-5. Use `Pet.xml`.
- Several real asset filenames contain `famale` or embedded spaces. Never
  normalize model or texture filenames.

The server should resolve an egg through an explicit item-to-type table, not
through arithmetic. A compatibility policy for the four defective eggs must be
chosen and tested before egg opening is enabled.
