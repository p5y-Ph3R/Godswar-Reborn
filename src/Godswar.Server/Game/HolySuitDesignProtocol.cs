using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// Captured protocol identity for the original Master Vestment Forger.
/// This NPC owns the stock NpcFunSanLoad "Holy Suit Design" workflow; it is
/// separate from both the Gear Mentor and the Class Suit/Vocation dialog.
/// </summary>
internal static class HolySuitDesignProtocol
{
    // City NPC object IDs follow Sparta=4997+number and Athens=5139+number.
    public const uint SpartaNpcId = 5082;
    public const uint AthensNpcId = 5224;
    public const int DialogIndex = 29;
    public const int InitialMenuRequestSubId = -1;
    public const int StoreExperienceSubId = 101;
    public const int TransferExperienceSubId = 201;
    public const int ConsumeEquipmentSubId = 301;
    public const int TransformExperienceSubId = 401;
    public const int TemporarilyDisabledResultSubId = 999;

    public static bool IsNpcKey(string npcKey)
    {
        return npcKey is "Sparta_085" or "Athens_085";
    }

    public static bool IsMenuSubId(int subId)
    {
        return subId is StoreExperienceSubId or TransferExperienceSubId or
            ConsumeEquipmentSubId or TransformExperienceSubId;
    }

    public static byte[] BuildInitialMenuResponse(uint npcId)
    {
        // capture-proxy-20260514-173331.log records this exact order after
        // object 5082 receives the dialog-29 initial request.
        return PacketBuilder.NpcFunctionActionResponse(
            npcId,
            DialogIndex,
            StoreExperienceSubId,
            TransferExperienceSubId,
            ConsumeEquipmentSubId,
            TransformExperienceSubId);
    }

    public static bool TryBuildInitialMenuResponse(
        string npcKey,
        uint npcId,
        int dialogIndex,
        int subId,
        out byte[] response)
    {
        response = [];
        if (!IsNpcKey(npcKey) ||
            dialogIndex != DialogIndex ||
            subId != InitialMenuRequestSubId)
        {
            return false;
        }

        response = BuildInitialMenuResponse(npcId);
        return true;
    }
}
