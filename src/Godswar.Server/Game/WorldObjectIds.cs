namespace Godswar.Server.Game;

internal static class WorldObjectIds
{
    private const uint PlayerObjectIdBase = 0x6000;
    private const uint PlayerObjectIdMask = 0x1FFF;

    public static uint ForPlayer(int characterId)
    {
        return PlayerObjectIdBase + ((uint)Math.Max(characterId, 0) & PlayerObjectIdMask);
    }
}
