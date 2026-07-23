from __future__ import annotations


LOCALES = ("en_us", "zh_cn")
MOUNT_NAME = "Erebus Lion"
MOUNT_DESCRIPTION = (
    "A lion born from the darkness of Erebus, black as a moonless night."
)
ITEM_BASE_ID = 16200
ITEM_COUNT = 10
RIDE_SECTION_ID = 117
# Custom status in an unused native-range gap. The server synchronizes the
# actual local movement multiplier through PlayerStatusUpdate opcode 10166.
RIDE_STATUS_ID = 1390
LEGACY_RIDE_STATUS_IDS = (1505,)
SOURCE_MODEL_NAME = "Ride_Lion_002.jcs"
SOURCE_TEXTURE_NAME = "Ride_Lion_004.gwo"
TARGET_MODEL_NAME = "Ride_ErebusLion_001.jcs"
TARGET_TEXTURE_NAME = "Ride_ErebusLion_001.gwo"
SOURCE_ICON_X = 756
SOURCE_ICON_Y = 756
TARGET_ICON_X = 396
TARGET_ICON_Y = 0
ICON_SIZE = 36

EXPECTED_SOURCE_MODEL_SHA256 = (
    "80186f8ef998296e6a37c21783dbce4746b1b0284e0a8a71027da9bac402364a"
)
EXPECTED_TARGET_MODEL_SHA256 = (
    "1e5ae1dc596ae69a659b55dd52839fe842946353536b4258ea75f73657fb84ac"
)
EXPECTED_TEXTURE_SHA256 = (
    "07566337e4e44d4b3860caee969b10b9030b9bda6e2fee7a3c907462a6952e0f"
)
EXPECTED_ICON2_SHA256 = (
    "cec386236e973302a82ca61fa2ad62304cb09759b1dd18a8b71d5923a3427ca7"
)

MOUNT_LEVELS = (40, 50, 60, 70, 80, 90, 100, 110, 120, 120)
MOUNT_SPEEDS = (0.20, 0.21, 0.22, 0.23, 0.24, 0.25, 0.26, 0.27, 0.28, 0.50)
MOUNT_MAX_HP = (2500, 2800, 3100, 3400, 3700, 4000, 4300, 4650, 5000, 5000)
MODEL_UNIFORM_SCALE = 1.40
