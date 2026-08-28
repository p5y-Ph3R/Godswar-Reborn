namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// Stock NpcFunStayReward surface. The top-level dialog itself is the claim;
/// its initial request uses sub-id -1 and the server replies with one terminal
/// result row.
/// </summary>
internal static class OnlineAwardProtocol
{
    public const int DialogIndex = 49;
    public const uint AthensNpcId = 5271;
    public const uint SpartaNpcId = 5129;
    public const int InitialRequestSubId = -1;
    public const int SuccessSubId = 102;
    public const int AlreadyClaimedSubId = 103;
    public const int BagFullSubId = 104;
    public const int UnavailableSubId = 105;

    // A non-empty profile entry is required by the immutable dialogue
    // publication contract. It describes the successful terminal row, but
    // the handler executes the claim directly at InitialRequestSubId.
    public static IReadOnlyList<int> InitialMenuSubIds { get; } =
        Array.AsReadOnly(new[] { SuccessSubId });

    public static bool IsEndpoint(string npcKey, uint npcId) =>
        (npcKey, npcId) is
            ("Athens_132", AthensNpcId) or
            ("Sparta_132", SpartaNpcId);

    public static bool IsEndpoint(uint npcId, int dialogIndex) =>
        dialogIndex == DialogIndex &&
        npcId is AthensNpcId or SpartaNpcId;
}
