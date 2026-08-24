namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// Finite stock endpoints for the character-owned normal warehouse. Source
/// catalog actor IDs are deliberately not accepted here; interaction IDs are
/// the identities published into the authoritative map snapshot.
/// </summary>
internal static class WarehouseNpcProtocol
{
    public const uint AthensWarehouseNpcId = 5164;
    public const uint SpartaWarehouseNpcId = 47750;
    public const uint AthensManagerNpcId = 5273;
    public const uint SpartaManagerNpcId = 5131;

    public const int ManagerDialogIndex = 106;
    public const int ManagerInitialRequestSubId = -1;
    public const int ManagerActionSubId = 100;
    public const int ManagerGenericResultSubId = 999;

    public static IReadOnlyList<int> ManagerInitialMenuSubIds { get; } =
        [ManagerActionSubId];

    public static bool IsWarehouseEndpoint(
        string npcKey,
        uint interactionId) =>
        (npcKey, interactionId) is
            ("Athens_025", AthensWarehouseNpcId) or
            ("Sparta_023", SpartaWarehouseNpcId);

    public static bool IsManagerEndpoint(
        string npcKey,
        uint interactionId) =>
        (npcKey, interactionId) is
            ("Athens_134", AthensManagerNpcId) or
            ("Sparta_134", SpartaManagerNpcId);
}
