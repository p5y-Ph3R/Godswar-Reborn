using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class CapitalNpcServiceProtocolChecks
{
    public const string CheckName =
        "Captured capital NPC dialogue and shop catalogs";

    public static Task RunAsync()
    {
        CheckEndpointsAndExchangePages();
        CheckOpenPackets();
        CheckShopCatalogs();
        CheckPurchaseIntentAndOfferAuthority();
        CheckBindingGoldMigration();
        return Task.CompletedTask;
    }

    private static void CheckEndpointsAndExchangePages()
    {
        (string Key, uint Id, CapitalNpcServiceKind Service)[] endpoints =
        [
            ("Sparta_052", 5049, CapitalNpcServiceKind.ExchangeMentor),
            ("Athens_052", 5191, CapitalNpcServiceKind.ExchangeMentor),
            ("Sparta_069", 5066, CapitalNpcServiceKind.TeachingManager),
            ("Athens_069", 5208, CapitalNpcServiceKind.TeachingManager),
            ("Sparta_087", 5084, CapitalNpcServiceKind.BoundGoldVendor),
            ("Athens_087", 5226, CapitalNpcServiceKind.BoundGoldVendor),
            ("Sparta_068", 5065, CapitalNpcServiceKind.BindingGoldShop),
            ("Athens_068", 5207, CapitalNpcServiceKind.BindingGoldShop)
        ];

        var published = NpcContentBaselineV1.LoadDefinitions();

        foreach (var (key, id, expected) in endpoints)
        {
            var npc = published.Single(candidate =>
                candidate.NpcKey == key);
            Check.True(
                npc.InteractionId == id &&
                CapitalNpcServiceProtocol.TryResolve(
                    npc,
                    out var actual) &&
                actual == expected,
                $"published capital endpoint {key}/{id} resolves to {expected}");
        }

        Check.True(
            !CapitalNpcServiceProtocol.TryResolve(
                Npc("Sparta_052", 5191),
                out _) &&
            !CapitalNpcServiceProtocol.TryResolve(
                Npc("Sparta_053", 5049),
                out _),
            "mixed or unrelated NPC identities cannot acquire capital behavior");

        var route = CapitalNpcServiceProtocol.ExchangeRoute(
            Npc("Sparta_052", 5049));
        Check.True(
            route.DialogIndex == 2 &&
            route.Behavior == NpcDialogueBehavior.CreditExchange &&
            route.InitialMenuSubIds.SequenceEqual([49, 50, 51]) &&
            CapitalNpcServiceProtocol.TryGetExchangePage(50, out var ethics) &&
            ethics.SequenceEqual([311, 312, 313]) &&
            CapitalNpcServiceProtocol.TryGetExchangePage(51, out var reputation) &&
            reputation.SequenceEqual([314, 315, 316]) &&
            !CapitalNpcServiceProtocol.TryGetExchangePage(49, out _),
            "Exchange Mentor follows the captured root and branch pages");
    }

    private static void CheckOpenPackets()
    {
        var description = PacketBuilder.NpcDescriptionDialogOpenAck(
            5066,
            "Sparta_069");
        var shop = PacketBuilder.NpcShopDialogOpenAck(
            5084,
            "Sparta_087");

        Check.True(
            IsOpen(description, 5066, flags: 0, packedDialog: 0) &&
            IsOpen(shop, 5084, flags: 4, packedDialog: 0),
            "Teaching Manager and vendor advertise their captured window types");
    }

    private static void CheckShopCatalogs()
    {
        var bound = PacketBuilder.CapitalNpcShopCatalog(
            5084,
            12_345,
            CapitalNpcServiceKind.BoundGoldVendor);
        var boundFrames = ReadCatalogFrames(bound);
        Check.True(
            bound.Length == 18_424 &&
            boundFrames.Count == 13 &&
            boundFrames.Sum(static frame => frame[10]) == 132 &&
            boundFrames.All(frame => IsCatalogHeader(
                frame,
                npcId: 5084,
                shopType: 2,
                balance: 12_345)) &&
            boundFrames.SelectMany(ReadItemIds).All(static itemId =>
                itemId is not (4064 or 4177 or 4515 or 10059)) &&
            ReadItemId(boundFrames[0], 0) == 1002 &&
            ReadPrice(boundFrames[0], 0) == 10_000 &&
            ReadItemId(boundFrames[^1], 3) == 9024 &&
            ReadPrice(boundFrames[^1], 3) == 2_073,
            "Bound Gold Vendor retains 132 client-compatible captured records");

        var binding = PacketBuilder.CapitalNpcShopCatalog(
            5065,
            54_321,
            CapitalNpcServiceKind.BindingGoldShop);
        var bindingFrames = ReadCatalogFrames(binding);
        Check.True(
            binding.Length == 12_816 &&
            bindingFrames.Count == 9 &&
            bindingFrames.Sum(static frame => frame[10]) == 115 &&
            bindingFrames.All(frame => IsCatalogHeader(
                frame,
                npcId: 5065,
                shopType: 4,
                balance: 54_321)) &&
            ReadItemId(bindingFrames[0], 0) == 4230 &&
            ReadPrice(bindingFrames[0], 0) == 12 &&
            ReadItemId(bindingFrames[^1], 13) == 5618 &&
            ReadPrice(bindingFrames[^1], 13) == 3_200,
            "B-GOLD Shop reproduces all 115 captured stock records");

        var replay = ReadCatalogFrames(PacketBuilder.CapitalNpcShopCatalog(
            5226,
            7,
            CapitalNpcServiceKind.BoundGoldVendor));
        Check.True(
            replay.All(frame => IsCatalogHeader(
                frame,
                npcId: 5226,
                shopType: 2,
                balance: 7)) &&
            boundFrames.All(frame => IsCatalogHeader(
                frame,
                npcId: 5084,
                shopType: 2,
                balance: 12_345)),
            "catalog reuse patches a clone and cannot corrupt prior responses");
    }

    private static bool IsOpen(
        byte[] packet,
        uint npcId,
        int flags,
        int packedDialog) =>
        packet.Length == 48 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet) == 48 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) ==
            Opcodes.NpcDialogOpen &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == npcId &&
        BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)) == flags &&
        BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12)) ==
            packedDialog;

    private static bool IsCatalogHeader(
        byte[] packet,
        uint npcId,
        byte shopType,
        int balance) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) ==
            Opcodes.NpcShopCatalog &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == npcId &&
        packet[9] == shopType &&
        BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12)) == balance;

    private static IReadOnlyList<byte[]> ReadCatalogFrames(byte[] stream)
    {
        var frames = new List<byte[]>();
        var offset = 0;
        while (offset < stream.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                stream.AsSpan(offset));
            Check.True(
                length >= 16 && offset + length <= stream.Length,
                "capital shop catalog frame is bounded");
            frames.Add(stream.AsSpan(offset, length).ToArray());
            offset += length;
        }

        Check.Equal(stream.Length, offset,
            "capital shop catalog stream ends on a frame boundary");
        return frames;
    }

    private static uint ReadItemId(byte[] packet, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            packet.AsSpan(16 + (index * 88)));

    private static uint ReadPrice(byte[] packet, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            packet.AsSpan(16 + (index * 88) + 68));

    private static IEnumerable<uint> ReadItemIds(byte[] packet)
    {
        for (var index = 0; index < packet[10]; index++)
        {
            yield return ReadItemId(packet, index);
        }
    }

    private static void CheckPurchaseIntentAndOfferAuthority()
    {
        var payload = new byte[CapitalNpcServiceProtocol.PurchasePayloadBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 5084);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), 31);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 3101);

        Check.True(
            CapitalNpcServiceProtocol.TryParsePurchase(
                payload,
                out var intent) &&
            intent == new CapitalNpcShopPurchaseIntent(
                5084,
                0,
                31,
                1,
                3101) &&
            PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.BoundGoldVendor,
                intent.Category,
                intent.ListingIndex,
                intent.ItemId,
                out var offer) &&
            offer.UnitPrice == 5_000 &&
            offer.Currency == CapitalNpcShopCurrency.Gold &&
            offer.Item == new CompactItemEntry(
                3101,
                70,
                103,
                143,
                120,
                null,
                3,
                3,
                0,
                1,
                0,
                0,
                null,
                null,
                null,
                null,
                null,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            "captured purchase resolves its server-owned item and price");

        Check.True(
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                CapitalNpcServiceKind.BoundGoldVendor,
                out var vendorCurrency) &&
            vendorCurrency == CapitalNpcShopCurrency.Gold &&
            CapitalNpcServiceProtocol.TryGetShopCurrency(
                CapitalNpcServiceKind.BindingGoldShop,
                out var shopCurrency) &&
            shopCurrency == CapitalNpcShopCurrency.BindingGold &&
            !CapitalNpcServiceProtocol.TryGetShopCurrency(
                CapitalNpcServiceKind.TeachingManager,
                out _),
            "Bound Gold Vendor spends Gold and B-GOLD Shop spends B-Gold");

        Check.True(
            !PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.BoundGoldVendor,
                category: 1,
                listingIndex: 31,
                expectedItemId: 3101,
                out _) &&
            !PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.BoundGoldVendor,
                category: 0,
                listingIndex: 31,
                expectedItemId: 3102,
                out _) &&
            !PacketBuilder.TryResolveCapitalNpcShopOffer(
                CapitalNpcServiceKind.BindingGoldShop,
                category: 0,
                listingIndex: 31,
                expectedItemId: 3101,
                out _) &&
            !CapitalNpcServiceProtocol.TryParsePurchase(
                payload.AsSpan(0, payload.Length - 1),
                out _),
            "category, listing, item, shop, and framing collisions fail closed");
    }

    private static NpcSpawnDefinition Npc(string key, uint interactionId) =>
        new(
            MapId: 0,
            SceneKey: key,
            NpcKey: key,
            TemplateKey: key,
            ObjectId: interactionId,
            X: 0,
            Z: 0,
            InteractionId: interactionId,
            AppearanceType: 0,
            Facing: 0,
            Detail10077: [],
            Detail10080: []);

    private static void CheckBindingGoldMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id ==
                "20260828_121_capital_npc_binding_gold");
        Check.True(
            migration.Sql.Contains(
                "ADD COLUMN IF NOT EXISTS \"BindingGold\"",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'silver', 'gold', 'binding_gold'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "binding_gold_delta",
                StringComparison.Ordinal),
            "capital shops install B-Gold storage, ledger, and reconciliation support");
    }
}
