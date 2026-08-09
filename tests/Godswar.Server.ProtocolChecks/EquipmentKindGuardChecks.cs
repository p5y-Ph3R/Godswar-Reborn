using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentKindGuardChecks
{
    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const uint StrengthStoneItemId = 9930;

    public static async Task RunAsync()
    {
        CheckKindCatalog();
        CheckFashionSlotConsistency();
        CheckMountSnapshotProjection();
        await CheckJsonStoreAsync();
        await CheckPostgresStoreAsync();
    }

    private static void CheckKindCatalog()
    {
        string[] equipmentKinds =
        [
            "head",
            "amulet",
            "glove",
            "armor",
            "cloth",
            "cuff",
            "girdle",
            "shoes",
            "leggins",
            "ring",
            "weapon",
            "shield",
            "stylish",
            "mounthead",
            "mountarmor",
            "mountsoul",
            "mountornament",
            "mountamulet",
            "mount"
        ];

        foreach (var kind in equipmentKinds)
        {
            Check.True(
                EquipmentSlots.IsEquipmentKind(kind),
                $"equipment kind '{kind}' is accepted");
        }

        Check.True(
            !EquipmentSlots.IsEquipmentKind("consume item"),
            "consume-item templates cannot use their placeholder slot as equipment");
        Check.True(
            !EquipmentSlots.IsEquipmentKind(string.Empty),
            "empty template kind is not equipment");

        (uint ItemId, int Slot, string Description)[] mountSlotMappings =
        [
            (14500, EquipmentSlots.MountHead, "mount head"),
            (14600, EquipmentSlots.MountArmor, "mount armor"),
            (14700, EquipmentSlots.MountSoul, "mount soul"),
            (14800, EquipmentSlots.MountOrnament, "mount ornament"),
            (14900, EquipmentSlots.MountAmulet, "mount amulet"),
            (6000, EquipmentSlots.Mount, "mount")
        ];

        foreach (var (itemId, expectedSlot, description) in mountSlotMappings)
        {
            Check.True(
                EquipmentSlots.TryGetAuthoritativeSlot(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, itemId, out var actualSlot),
                $"{description} template has an authoritative equipment slot");
            Check.Equal(expectedSlot, actualSlot, $"{description} uses its native slot");
            Check.Equal(
                expectedSlot,
                EquipmentSlots.ResolveSlotForItem(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, itemId, expectedSlot),
                $"{description} accepts its native slot");
            Check.Equal(
                -1,
                EquipmentSlots.ResolveSlotForItem(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, itemId, EquipmentSlots.Weapon),
                $"{description} rejects a normal gear slot");
        }

        Check.True(EquipmentSlots.IsEquipmentSlot(EquipmentSlots.Mount), "mount slot is equipment");
        Check.True(!EquipmentSlots.IsEquipmentSlot(EquipmentSlots.Mount + 1), "slot after mount is not equipment");

        var emptyEquipment = string.Join('#', Enumerable.Repeat("[]", EquipmentSlots.Mount + 1)) + "#";
        var lowLevelMount = EquipmentEligibility.ValidateEquip(Godswar.Server.ProtocolChecks.TestItemContent.Content,
            profession: 0,
            characterLevel: 39,
            equipment: emptyEquipment,
            itemId: 14220,
            equipmentSlot: EquipmentSlots.Mount);
        Check.True(!lowLevelMount.Allowed, "level-40 mount rejects a level-39 character");

        var mountAllowed = EquipmentEligibility.ValidateEquip(Godswar.Server.ProtocolChecks.TestItemContent.Content,
            profession: 0,
            characterLevel: 40,
            equipment: emptyEquipment,
            itemId: 14220,
            equipmentSlot: EquipmentSlots.Mount);
        Check.True(mountAllowed.Allowed, "level-40 Greek Steed accepts a level-40 character");

        foreach (var mount in TestItemContent.Content.DeveloperMounts.Grantable)
        {
            Check.True(
                TestItemContent.Content.Mounts.TryGetRideDefinition(mount.ItemId, out _),
                $"grantable client mount {mount.ItemId} has a Ride.ini status mapping");
        }
        Check.True(
            !TestItemContent.Content.Mounts.TryGetRideDefinition(DeveloperMountCatalog.OrphanedMountItemId, out _),
            "orphaned client mount is not advertised as rideable");
        Check.True(
            TestItemContent.Content.Mounts.TryGetRideDefinition(6000, out var legacyRide) &&
            legacyRide.StatusId == 1100,
            "legacy Greek Steed maps to Ride.ini status 1100");
        Check.True(
            TestItemContent.Content.Mounts.TryGetRideDefinition(14425, out var timedLeatherback) &&
            timedLeatherback.StatusId == 1210,
            "three-day Atlantic Leatherback maps to Ride.ini status 1210");
        Check.True(
            TestItemContent.Content.Mounts.TryGetRideDefinition(16199, out var specialOwl) &&
            specialOwl.StatusId == 1431,
            "special Owl maps to the upgraded Ride.ini visual");
        Check.True(
            TestItemContent.Content.Mounts.TryGetRideDefinition(16204, out var erebusLion) &&
            erebusLion.StatusId == 1390 &&
            erebusLion.MountLevel == 80 &&
            erebusLion.SpeedBonus == 0.24f,
            "level-80 Erebus Lion maps to the custom animation-safe visual");
        Check.True(
            EquipmentEligibility.ValidateEquip(Godswar.Server.ProtocolChecks.TestItemContent.Content,
                profession: 0,
                characterLevel: 80,
                equipment: emptyEquipment,
                itemId: 14464,
                equipmentSlot: EquipmentSlots.Mount).Allowed,
            "developer-catalog Asian Urus can be equipped at its authored level");

        var gearWithoutMount = EquipmentEligibility.ValidateEquip(Godswar.Server.ProtocolChecks.TestItemContent.Content,
            profession: 0,
            characterLevel: 40,
            equipment: emptyEquipment,
            itemId: 14500,
            equipmentSlot: EquipmentSlots.MountHead);
        Check.True(!gearWithoutMount.Allowed, "mount gear requires an equipped mount");

        var level40MountEquipment = EquipmentSlots.SetSlot(
            emptyEquipment,
            0,
            EquipmentSlots.Mount,
            "[14220,,,,,,1,1,0,1,0]");
        Check.True(
            EquipmentEligibility.ValidateEquip(Godswar.Server.ProtocolChecks.TestItemContent.Content,
                0,
                40,
                level40MountEquipment,
                14500,
                EquipmentSlots.MountHead).Allowed,
            "level-40 mount accepts level-40 mount gear");
        Check.True(
            !EquipmentEligibility.ValidateEquip(Godswar.Server.ProtocolChecks.TestItemContent.Content,
                0,
                50,
                level40MountEquipment,
                14501,
                EquipmentSlots.MountHead).Allowed,
            "level-40 mount rejects level-50 mount gear even for a high-enough player");

        var mountWithGear = EquipmentSlots.SetSlot(
            level40MountEquipment,
            0,
            EquipmentSlots.MountHead,
            "[14500,,,,,,1,1,0,1,0]");
        Check.True(
            !EquipmentEligibility.ValidateUnequip(
                0,
                mountWithGear,
                EquipmentSlots.Mount).Allowed,
            "mount removal requires all mount gear to be removed first");
    }

    private static void CheckMountSnapshotProjection()
    {
        var character = new GameCharacter
        {
            Profession = 0,
            Equipment = EquipmentSlots.SetSlot(
                GameDefaults.DefaultEquipment(0),
                0,
                EquipmentSlots.Mount,
                "[6000,,,,,,1,1,0,1,0]")
        };

        var snapshot = PacketBuilder.EquipmentItemSnapshot(character, EquipmentSlots.Mount);
        Check.True(snapshot.Length > 20, "mounted item has an equipment snapshot");
        Check.Equal(
            (ushort)EquipmentSlots.Mount,
            BinaryPrimitives.ReadUInt16LittleEndian(snapshot.AsSpan(14, 2)),
            "mounted item snapshot carries native slot 20");
        Check.Equal(
            6000u,
            BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(20, 4)),
            "mounted item snapshot carries the mount template ID");

        var oneClear = PacketBuilder.EquipmentItemClearSnapshot(EquipmentSlots.Mount, 0x1234u);
        var allClears = PacketBuilder.EquipmentItemClearSnapshots(0x1234u);
        Check.Equal(
            oneClear.Length * (EquipmentSlots.Mount + 1),
            allClears.Length,
            "equipment clear projection covers slots zero through mount");
        Check.Equal(
            (ushort)EquipmentSlots.Mount,
            BinaryPrimitives.ReadUInt16LittleEndian(allClears.AsSpan(allClears.Length - oneClear.Length + 14, 2)),
            "final equipment clear snapshot targets native mount slot");
    }
}
