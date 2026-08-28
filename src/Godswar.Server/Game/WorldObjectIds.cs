namespace Godswar.Server.Game;

internal static class WorldObjectIds
{
    private const uint LocalPlayerObjectId = 0x1448;
    internal const uint FirstMonsterObjectId = 0x1F40;
    internal const uint LastMonsterObjectId = 49_999;
    internal const uint FirstMedusaMonsterObjectId = 40_000;
    internal const uint MedusaBabyRockElfObjectId = 40_136;
    internal const uint SecondMedusaBabyRockElfObjectId = 40_137;
    internal static readonly uint[] MedusaBabyRockElfObjectIds =
    [
        MedusaBabyRockElfObjectId,
        SecondMedusaBabyRockElfObjectId
    ];

    internal static bool IsMedusaBabyRockElf(uint objectId) =>
        objectId is MedusaBabyRockElfObjectId or
            SecondMedusaBabyRockElfObjectId;

    // Authored live monsters must use the stock client's ordinary
    // 8000..49999 monster registry. The client has a separate legacy branch
    // for 95000..99999, but live objects in that branch crash during nearby
    // object processing.
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

    public static bool IsMonster(uint objectId) =>
        objectId is >= FirstMonsterObjectId and <= LastMonsterObjectId;

    public static bool IsReservedForPlayer(uint objectId)
    {
        return objectId == LocalPlayerObjectId ||
            IsRemotePlayer(objectId);
    }
}
