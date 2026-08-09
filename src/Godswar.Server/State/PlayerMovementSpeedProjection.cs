namespace Godswar.Server.State;

/// <summary>
/// Projects mount-content speed into the client status contract without
/// making packet code depend on item repositories or XML/JSON parsing.
/// </summary>
internal static class PlayerMovementSpeedProjection
{
    public static float GetEquippedRidingSpeedBonus(
        MountCatalog? mounts,
        GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (mounts is null ||
            !mounts.TryGetEquippedRideDefinition(character, out var mount) ||
            !float.IsFinite(mount.SpeedBonus) ||
            mount.SpeedBonus is < 0f or > 9f)
        {
            return 0f;
        }

        return mount.SpeedBonus;
    }

    public static ClientStatusAggregate WithEquippedRidingSpeed(
        MountCatalog? mounts,
        GameCharacter character,
        ClientStatusAggregate aggregate) =>
        aggregate with
        {
            EquippedRidingSpeedBonus =
                GetEquippedRidingSpeedBonus(mounts, character)
        };
}
