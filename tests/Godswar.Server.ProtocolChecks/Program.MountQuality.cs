using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static void CheckQualityAwareRideDefinitions(GameCharacter character)
    {
        var originalLevel = character.Level;
        var originalEquipment = character.Equipment;
        character.Level = 80;
        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Mount,
            "[16204,,,,,,1,25,1,1,0]");
        Check.True(
            MountCatalog.TryGetEquippedRideDefinition(character, out var commonErebus),
            "common level-80 Erebus resolves its quality-aware Ride definition");
        Check.Equal(0.24f, commonErebus.SpeedBonus, "common Erebus keeps its native speed");

        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Mount,
            "[16204,,,,,,20,25,1,1,0]");
        Check.True(
            MountCatalog.TryGetEquippedRideDefinition(character, out var boundlessErebus),
            "Boundless level-80 Erebus resolves its quality-aware Ride definition");
        Check.Equal(
            0.25f,
            boundlessErebus.SpeedBonus,
            "Boundless Erebus gains one family-tier speed step");
        Check.True(
            boundlessErebus.SpeedBonus > commonErebus.SpeedBonus,
            "Boundless Erebus moves faster than Common");

        character.Level = originalLevel;
        character.Equipment = originalEquipment;
    }

    private static async Task CheckRideQualityRecheckAsync(
        GameSessionRegistry registry,
        ClientSession session,
        GameCharacter character,
        long lifeRevision,
        MountRideDefinition expectedMount)
    {
        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Mount,
            "[14220,,,,,,20,1,0,1,0]");
        var changedQuality = await registry.TryActivateMountRideAndPublishAsync(
            session,
            character.Id,
            lifeRevision,
            expectedMount,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Check.True(
            changedQuality is null,
            "Ride commit rejects a mount whose quality changed during intonation");
        Check.Equal(
            150,
            character.CurrentMp,
            "quality recheck rejection does not consume MP");

        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Mount,
            "[14220,,,,,,1,1,0,1,0]");
    }
}
