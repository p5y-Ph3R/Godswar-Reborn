using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Godswar.Server.Domain.World.Content;

internal enum CapitalNpcServiceKind
{
    ExchangeMentor,
    TeachingManager,
    BoundGoldVendor,
    BindingGoldShop
}

internal enum CapitalNpcShopCurrency
{
    Gold,
    BindingGold
}

internal static class CapitalNpcServiceProtocol
{
    public const int PurchasePayloadBytes = 20;
    public const int ExchangeDialogIndex = 2;
    public const int DescriptionOpenFlags = 0;
    public const int ShopOpenFlags = 4;

    public static ImmutableArray<int> ExchangeInitialMenu { get; } =
        [49, 50, 51];

    public static bool TryResolve(
        NpcSpawnDefinition npc,
        out CapitalNpcServiceKind service)
    {
        var resolved = (npc.NpcKey, npc.InteractionId) switch
        {
            ("Sparta_052", 5049u) or ("Athens_052", 5191u) =>
                (CapitalNpcServiceKind?)CapitalNpcServiceKind.ExchangeMentor,
            ("Sparta_069", 5066u) or ("Athens_069", 5208u) =>
                CapitalNpcServiceKind.TeachingManager,
            ("Sparta_087", 5084u) or ("Athens_087", 5226u) =>
                CapitalNpcServiceKind.BoundGoldVendor,
            ("Sparta_068", 5065u) or ("Athens_068", 5207u) =>
                CapitalNpcServiceKind.BindingGoldShop,
            _ => null
        };

        service = resolved.GetValueOrDefault();
        return resolved.HasValue;
    }

    public static bool TryGetShopCurrency(
        CapitalNpcServiceKind service,
        out CapitalNpcShopCurrency currency)
    {
        currency = service switch
        {
            CapitalNpcServiceKind.BoundGoldVendor =>
                CapitalNpcShopCurrency.Gold,
            CapitalNpcServiceKind.BindingGoldShop =>
                CapitalNpcShopCurrency.BindingGold,
            _ => default
        };
        return service is
            CapitalNpcServiceKind.BoundGoldVendor or
            CapitalNpcServiceKind.BindingGoldShop;
    }

    public static NpcDialogueRouteDefinition ExchangeRoute(
        NpcSpawnDefinition npc)
    {
        if (!TryResolve(npc, out var service) ||
            service != CapitalNpcServiceKind.ExchangeMentor)
        {
            throw new ArgumentException(
                "NPC is not an exact capital Exchange Mentor endpoint.",
                nameof(npc));
        }

        return new NpcDialogueRouteDefinition(
            npc.NpcKey,
            npc.NpcKey,
            ExchangeDialogIndex,
            NpcDialogueBehavior.CreditExchange,
            ExchangeInitialMenu);
    }

    public static bool TryGetExchangePage(
        int subId,
        out int[] pageSubIds)
    {
        pageSubIds = subId switch
        {
            50 => [311, 312, 313],
            51 => [314, 315, 316],
            _ => []
        };
        return pageSubIds.Length != 0;
    }

    public static bool TryParsePurchase(
        ReadOnlySpan<byte> payload,
        out CapitalNpcShopPurchaseIntent intent)
    {
        intent = default;
        if (payload.Length != PurchasePayloadBytes)
        {
            return false;
        }

        var candidate = new CapitalNpcShopPurchaseIntent(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16)));
        if (candidate.NpcId == 0 ||
            candidate.Category is < 0 or > byte.MaxValue ||
            candidate.ListingIndex < 0 ||
            candidate.Quantity is < 1 or > byte.MaxValue ||
            candidate.ItemId == 0)
        {
            return false;
        }

        intent = candidate;
        return true;
    }
}

internal readonly record struct CapitalNpcShopPurchaseIntent(
    uint NpcId,
    int Category,
    int ListingIndex,
    int Quantity,
    uint ItemId);
