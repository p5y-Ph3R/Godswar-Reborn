namespace Godswar.Server.Game;

internal static class WorldObjectIds
{
    private const uint LocalPlayerObjectId = 0x1448;
    // Stock 0x2728 classifies IDs >= 0x1F40 as monsters. Captures place
    // remote players in 1..0x05DB, so this range is required for corpse
    // removal to reach the native player registry.
    internal const uint FirstRemotePlayerObjectId = 1;
    internal const uint LastRemotePlayerObjectId = 0x05DB;
    internal const int RemotePlayerObjectIdCapacity =
        (int)(LastRemotePlayerObjectId -
            FirstRemotePlayerObjectId + 1);

    public static uint ForPlayer(int characterId)
    {
        var positiveCharacterId = Math.Max(characterId, 1);
        return FirstRemotePlayerObjectId +
            ((uint)(positiveCharacterId - 1) %
                (uint)RemotePlayerObjectIdCapacity);
    }

    public static uint AllocateForPlayer(
        int characterId,
        IReadOnlySet<uint> activeObjectIds)
    {
        ArgumentNullException.ThrowIfNull(activeObjectIds);

        var candidate = ForPlayer(characterId);
        for (var attempt = 0;
             attempt < RemotePlayerObjectIdCapacity;
             attempt++)
        {
            if (!activeObjectIds.Contains(candidate))
            {
                return candidate;
            }

            candidate = candidate == LastRemotePlayerObjectId
                ? FirstRemotePlayerObjectId
                : candidate + 1;
        }

        throw new InvalidOperationException(
            "The native remote-player object-ID pool is exhausted.");
    }

    public static bool IsRemotePlayer(uint objectId)
    {
        return objectId >= FirstRemotePlayerObjectId &&
            objectId <= LastRemotePlayerObjectId;
    }

    public static bool IsReservedForPlayer(uint objectId)
    {
        return objectId == LocalPlayerObjectId ||
            IsRemotePlayer(objectId);
    }
}
