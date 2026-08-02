from __future__ import annotations

from dataclasses import dataclass


GENERATOR_VERSION = "1.0.0"
TRANSFORM_ID = "class-suit-iv-dual-tone-v1"
ASSET_ROOTS = ("Characters", "Characters_New")
GENDERS = ("female", "male")

# These hashes pin the reviewed palette algorithm and its source inputs.
# Intentional visual changes require a transform/version update.
EXPECTED_OUTPUT_HASHES = {
    "Characters:1035": "baa8ed02a2c572d43e86712646374abf39b4da7c3c4d7ed06cc5bb844f15c45b",
    "Characters_New:1035": "1144aa3a23449a3fc595facfcf48a8250ee7c05fe0f52cf2bb458ed82004d71b",
    "Characters:1435": "6b5d3bdb3ec8238d0e5a9be246e6616436c14a99b27e4c8fd8e4febffc5a81aa",
    "Characters_New:1435": "3f8b42c0b14df24faf7815240f4ece9ca0b0c2923a790a4efb7259f04d165caf",
    "Characters:1835": "faba65aa95a341af84a5f2967ed90954f74e7c7eae1760993f508d5ff26920e3",
    "Characters_New:1835": "036f5dce48ffb766e40d2298c3573bc1b1374429ced43d3c73d0c93d98c513ab",
    "Characters:1735": "46219b48a5a7326a5f183fe36543ad1f4b986abb0c0172bfe1e78e38be1012dc",
    "Characters_New:1735": "4d622f363ec6826e401b0664cabba1f3d9c9bf96941dd03d416f1d05ade7352d",
}


@dataclass(frozen=True)
class Palette:
    dark: tuple[int, int, int]
    mid: tuple[int, int, int]
    bright: tuple[int, int, int]
    metal: tuple[int, int, int]
    highlight: tuple[int, int, int]


@dataclass(frozen=True)
class WeaponSpec:
    class_name: str
    tier_three_id: int
    tier_four_id: int
    family: str
    palette: Palette
    source_hashes: dict[str, str]
    dimensions: dict[str, tuple[int, int]]

    def texture_name(self, gender: str, item_id: int) -> str:
        return f"{gender}_{self.family}_{item_id}.gwo"

    def model_name(self, gender: str, item_id: int) -> str:
        suffix = "_right" if self.family == "weapononehand" else ""
        return f"{gender}_{self.family}_{item_id}{suffix}.jcs"


WEAPONS = (
    WeaponSpec(
        class_name="Warrior",
        tier_three_id=1034,
        tier_four_id=1035,
        family="weapononehand",
        palette=Palette(
            dark=(68, 7, 16),
            mid=(196, 26, 35),
            bright=(255, 102, 48),
            metal=(247, 205, 91),
            highlight=(255, 243, 184),
        ),
        source_hashes={
            "Characters": "22e16ac82ea3d6e009dd649d04c4b18a9bb77c22ed5d1c159d802c842e92b3f5",
            "Characters_New": "fb76c242d802baba0011cbca3540373c7694db77dd31ccc1b6eb8f6e5a34525e",
        },
        dimensions={
            "Characters": (32, 32),
            "Characters_New": (64, 64),
        },
    ),
    WeaponSpec(
        class_name="Champion",
        tier_three_id=1434,
        tier_four_id=1435,
        family="weapontwohand",
        palette=Palette(
            dark=(0, 31, 70),
            mid=(0, 133, 204),
            bright=(40, 226, 255),
            metal=(242, 201, 82),
            highlight=(244, 253, 255),
        ),
        source_hashes={
            "Characters": "39bb647b8c9d168d1fc121b8a7be9c0874088f8b75257b4c1ed154b840c5ce2f",
            "Characters_New": "69cd7bfe6187172fac6536872d13e527984b2c5fdfb1383ae5e63c0b915c1fac",
        },
        dimensions={
            "Characters": (32, 64),
            "Characters_New": (64, 128),
        },
    ),
    WeaponSpec(
        class_name="Mage",
        tier_three_id=1834,
        tier_four_id=1835,
        family="weapontwohand",
        palette=Palette(
            dark=(35, 7, 84),
            mid=(113, 37, 205),
            bright=(238, 65, 255),
            metal=(177, 214, 255),
            highlight=(248, 238, 255),
        ),
        source_hashes={
            "Characters": "b767204d13013283025cecd15d1178e55e9238e4143299de929c38ed99d68738",
            "Characters_New": "643ef7753ac70752d59c03591a87c7dd29bc78ad8517e69f58a06bdf3f01921e",
        },
        dimensions={
            "Characters": (32, 64),
            "Characters_New": (64, 128),
        },
    ),
    WeaponSpec(
        class_name="Priest",
        tier_three_id=1734,
        tier_four_id=1735,
        family="weapononehand",
        palette=Palette(
            dark=(4, 58, 39),
            mid=(13, 161, 100),
            bright=(85, 255, 172),
            metal=(250, 217, 112),
            highlight=(250, 255, 218),
        ),
        source_hashes={
            "Characters": "350d54ad9af96aed88a20b0049731c41b5377b27584bddc0975ab3b56e599f4d",
            "Characters_New": "ee1ac803a225374cf1d9041e9c92c9dfb684dd414c715d79b784876e2cc0b55b",
        },
        dimensions={
            "Characters": (32, 32),
            "Characters_New": (64, 64),
        },
    ),
)
