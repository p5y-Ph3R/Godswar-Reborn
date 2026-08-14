namespace Godswar.Server.Application.Pets;

/// <summary>
/// Canonical stock-client material selection carried by a Rebirth command.
/// The durable executor remains responsible for inventory and outcome rules.
/// </summary>
internal static class PetRebirthMaterialContract
{
    public const int RebornHarpyiaItemId = 10098;
    public const int RebirthSpiritItemId = 10104;
    public const int MinimumCount = 0;
    public const int MaximumCount = 5;

    public static bool IsCanonicalSelection(
        int materialTemplateId,
        int spiritCount)
    {
        var isStockTemplate = materialTemplateId is
            0 or RebirthSpiritItemId or RebornHarpyiaItemId;
        if (spiritCount == 0)
        {
            // Removing the final native item retains its stock template while
            // setting count zero; a fresh modal has template zero.
            return isStockTemplate;
        }

        return spiritCount is >= 1 and <= MaximumCount &&
            materialTemplateId is
                RebirthSpiritItemId or RebornHarpyiaItemId;
    }
}
