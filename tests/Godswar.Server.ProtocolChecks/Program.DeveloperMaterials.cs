using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task CheckDeveloperForgingMaterialCommandAsync()
    {
        CheckDeveloperForgingMaterialCatalogAndPlanning();
        await CheckDeveloperForgingMaterialPersistenceAsync();
    }

    private static void CheckDeveloperForgingMaterialCatalogAndPlanning()
    {
        var expectedCatalog = new (uint Id, string Name, string Type, short StackCap)[]
        {
            (4200, "Level 1 Ruby", "consume item", 99),
            (4201, "Level 2 Ruby", "consume item", 99),
            (4202, "Level 3 Ruby", "consume item", 99),
            (4210, "Level 1 Sapphire", "consume item", 99),
            (4211, "Level 2 Sapphire", "consume item", 99),
            (4212, "Level 3 Sapphire", "consume item", 99),
            (4213, "Level 4 Sapphire", "consume item", 99),
            (4214, "Level 4 Sapphire Pieces", "consume item", 99),
            (4215, "Level 5 Sapphire", "consume item", 99),
            (4216, "Level 5 Sapphire Pieces", "consume item", 99),
            (4220, "Level 1 Emerald", "consume item", 99),
            (4221, "Level 2 Emerald", "consume item", 99),
            (4222, "Level 3 Emerald", "consume item", 99),
            (4223, "Level 4 Emerald", "consume item", 99),
            (4224, "Level 4 Emerald Pieces", "consume item", 99),
            (4225, "Level 5 Emerald", "consume item", 99),
            (4226, "Level 5 Emerald Pieces", "consume item", 99),
            (4230, "Level 1 Crystal", "consume item", 99),
            (4231, "Level 2 Crystal", "consume item", 99),
            (4232, "Level 3 Crystal", "consume item", 99),
            (4233, "Level 4 Crystal", "consume item", 99),
            (4234, "Level 5 Crystal", "consume item", 99),
            (4235, "Level 5 Crystal Pieces", "consume item", 99)
        };
        Check.Equal(expectedCatalog.Length, ForgingMaterialCatalog.All.Count, "forging-material catalog count");
        foreach (var expected in expectedCatalog)
        {
            Check.True(
                ForgingMaterialCatalog.TryResolve(expected.Id, out var material),
                $"catalogued material {expected.Id} resolves");
            Check.Equal(expected.Name, material.DisplayName, $"catalogued material {expected.Id} display name");
            Check.Equal(expected.Type, material.ItemType, $"catalogued material {expected.Id} item type");
            Check.Equal(expected.StackCap, material.StackCap, $"catalogued material {expected.Id} stack cap");

            var itemTemplate = material.ToItemTemplateSeed();
            Check.Equal(checked((int)expected.Id), itemTemplate.Id, $"native material {expected.Id} template ID");
            Check.Equal(expected.Type, itemTemplate.Kind, $"native material {expected.Id} template kind");
            Check.Equal((short)0, itemTemplate.EquipmentSlot, $"native material {expected.Id} is not equipable");
        }

        var levelTwoCrystalTemplate = ForgingMaterialCatalog.All
            .Single(material => material.ItemId == 4231)
            .ToItemTemplateSeed();
        var levelTwoCrystalStats = JsonNode.Parse(levelTwoCrystalTemplate.StatsJson)
            ?? throw new InvalidOperationException("Level 2 Crystal template stats did not parse.");
        Check.Equal("2", levelTwoCrystalStats["Random"]?.GetValue<string>() ?? string.Empty, "Level 2 Crystal native random table");
        Check.Equal("201,201", levelTwoCrystalStats["Distribution"]?.GetValue<string>() ?? string.Empty, "Level 2 Crystal native distribution");
        Check.Equal("99", levelTwoCrystalStats["Overlap"]?.GetValue<string>() ?? string.Empty, "forging material native stack cap metadata");
        Check.Equal(
            (short)0,
            ForgingMaterialCatalog.All.Single(material => material.ItemId == 4230).GrantedBound,
            "Level 1 Crystal grant preserves its native unbound state");
        Check.Equal(
            (short)1,
            ForgingMaterialCatalog.All.Single(material => material.ItemId == 4231).GrantedBound,
            "Level 2 Crystal grant preserves its native bound state");

        Check.True(
            !ForgingMaterialCatalog.TryResolve("ruby4", out _),
            "nonexistent Ruby level 4 is not synthesized");
        Check.True(
            ForgingMaterialCatalog.TryResolve("crystal5", out var crystalFive) &&
            crystalFive.ItemId == 4234 && !crystalFive.IsPiece,
            "locally authored Crystal level 5 resolves independently");
        Check.True(
            ForgingMaterialCatalog.TryResolve("sapphire5", out var sapphireFive) &&
            sapphireFive.ItemId == 4215 && !sapphireFive.IsPiece,
            "locally authored Sapphire level 5 does not alias level-4 pieces");
        Check.True(
            ForgingMaterialCatalog.TryResolve("emerald5", out var emeraldFive) &&
            emeraldFive.ItemId == 4225 && !emeraldFive.IsPiece,
            "locally authored Emerald level 5 does not alias level-4 pieces");
        Check.Equal(
            "./Localization/en_us/UI/Texture/Icon4.gwo",
            crystalFive.Texture,
            "Level 5 Crystal uses the dedicated icon atlas");
        Check.Equal("0,0", crystalFive.Icon, "Level 5 Crystal icon cell");
        Check.Equal(
            "./Localization/en_us/UI/Texture/Icon4.gwo",
            sapphireFive.Texture,
            "Level 5 Sapphire uses the dedicated icon atlas");
        Check.Equal("36,0", sapphireFive.Icon, "Level 5 Sapphire icon cell");
        Check.Equal(
            "./Localization/en_us/UI/Texture/Icon4.gwo",
            emeraldFive.Texture,
            "Level 5 Emerald uses the dedicated icon atlas");
        Check.Equal("72,0", emeraldFive.Icon, "Level 5 Emerald icon cell");
        Check.True(
            ForgingMaterialCatalog.TryResolve("sapphire4pieces", out var sapphirePieces) &&
            sapphirePieces.ItemId == 4214 && sapphirePieces.IsPiece,
            "native Sapphire pieces have a distinct alias");
        Check.Equal(
            ForgingMaterialCatalog.All.Count +
                GearEnhancementMaterialCatalog.All.Count +
                GearMentorMaterialCatalog.AttributeDusts.Count,
            DeveloperGrantMaterialCatalog.All.Count,
            "developer grant catalog combines forging, enhancement, and Gear Mentor materials");
        Check.True(
            DeveloperGrantMaterialCatalog.TryResolve(9930, out var strengthStoneDefinition) &&
            strengthStoneDefinition.DisplayName == "Strength Stone" &&
            strengthStoneDefinition.StackCap == 99 &&
            strengthStoneDefinition.GrantedBound == 0,
            "gear-enhancement material grant policy is resolved by the unified server catalog");

        Check.True(
            DeveloperItemCommand.TryParse(
                "/gmitem add 4233 17",
                out var numericRequest,
                out _) &&
            numericRequest is
            {
                Operation: DeveloperItemOperation.Add,
                Material.ItemId: 4233,
                Quantity: 17
            },
            "developer item command retains the legacy alias for direct protocol clients");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item clearbag confirm",
                out var clearBagRequest,
                out _) &&
            clearBagRequest is
            {
                Operation: DeveloperItemOperation.ClearBag,
                Material: null,
                Quantity: 0
            },
            "developer item command requires and accepts the explicit clear-bag confirmation");
        Check.True(
            DeveloperItemCommand.TryParse(
                "test2:/****** clearbag confirm",
                out var maskedClearBagRequest,
                out _) &&
            maskedClearBagRequest is { Operation: DeveloperItemOperation.ClearBag },
            "stock-client masking and sender prefixes preserve the guarded clear-bag command");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item clearbag",
                out var unconfirmedClearBagRequest,
                out var unconfirmedClearBagError) &&
            unconfirmedClearBagRequest is null &&
            unconfirmedClearBagError.Contains("clearbag confirm", StringComparison.Ordinal),
            "clear-bag command without confirmation is consumed but rejected");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item clearbag yes",
                out var wronglyConfirmedClearBagRequest,
                out _) &&
            wronglyConfirmedClearBagRequest is null,
            "clear-bag command rejects the wrong confirmation token");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item clearbag confirm now",
                out var overlongClearBagRequest,
                out _) &&
            overlongClearBagRequest is null,
            "clear-bag command rejects trailing arguments after confirmation");
        Check.True(
            DeveloperItemCommand.TryParse(
                "ProtocolHero:/item add crystal1 99",
                out var clientSafeRequest,
                out _) &&
            clientSafeRequest is { Material.ItemId: 4230, Quantity: 99 },
            "developer item command accepts the stock-client-safe alias after a sender prefix");
        Check.True(
            DeveloperItemCommand.TryParse(
                "test2:/****** add crystal1 99",
                out var maskedLegacyRequest,
                out _) &&
            maskedLegacyRequest is { Material.ItemId: 4230, Quantity: 99 },
            "developer item command recognizes the stock client's masked legacy prefix");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/gmitem add crystal 2 99",
                out var splitAliasRequest,
                out _) &&
            splitAliasRequest is { Material.ItemId: 4231, Quantity: 99 },
            "developer item command accepts an unambiguous material and level alias");
        Check.True(
            DeveloperItemCommand.TryParse(
                "ProtocolHero:/gmitem add emerald-l4",
                out var prefixedRequest,
                out _) &&
            prefixedRequest is { Material.ItemId: 4223, Quantity: 1 },
            "developer item command tolerates a captured sender prefix and defaults quantity to one");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/gmitem add sapphire4pieces 5",
                out var pieceRequest,
                out _) &&
            pieceRequest is { Material.ItemId: 4214, Quantity: 5 },
            "developer item command accepts the distinct native pieces alias");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/gmitem add emerald5 12",
                out var levelFiveRequest,
                out _) &&
            levelFiveRequest is { Material.ItemId: 4225, Quantity: 12 },
            "developer item command accepts locally authored level-5 material aliases");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add 9930 7",
                out var numericEnhancementRequest,
                out _) &&
            numericEnhancementRequest is { Material.ItemId: 9930, Quantity: 7 },
            "developer item command accepts an allowlisted gear-enhancement numeric ID");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add strengthstone 8",
                out var strengthStoneRequest,
                out _) &&
            strengthStoneRequest is { Material.ItemId: 9930, Quantity: 8 },
            "developer item command resolves the Strength Stone alias");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add quartzplate1 9",
                out var quartzPlateRequest,
                out _) &&
            quartzPlateRequest is { Material.ItemId: 9960, Quantity: 9 },
            "developer item command resolves the Quartz Plate 1 alias");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add flamespark 10",
                out var flameSparkRequest,
                out _) &&
            flameSparkRequest is { Material.ItemId: 9990, Quantity: 10 },
            "developer item command resolves the Flame Spark alias");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add watergrain 11",
                out var waterGrainRequest,
                out _) &&
            waterGrainRequest is { Material.ItemId: 9991, Quantity: 11 },
            "developer item command resolves the Water Grain alias");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/gmitem add 999999 1",
                out var arbitraryRequest,
                out var arbitraryError) &&
            arbitraryRequest is null && arbitraryError.Contains("not an allowlisted"),
            "arbitrary numeric item IDs are consumed but rejected");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add 9939 1",
                out var catalogGapRequest,
                out _) && catalogGapRequest is null,
            "the deliberately absent gear-enhancement material ID remains rejected");
        Check.True(
            DeveloperItemCommand.TryParse(
                $"/gmitem add crystal1 {DeveloperItemCommand.MaximumQuantity + 1}",
                out var oversizedRequest,
                out _) && oversizedRequest is null,
            "developer item command enforces the strict quantity maximum");
        Check.True(
            !DeveloperItemCommand.TryParse("ordinary map chat", out _, out _),
            "ordinary chat is not consumed as a developer command");

        var talkText = "/item add ruby1 3";
        var talkTextBytes = Encoding.Unicode.GetBytes(talkText);
        var talkPayload = new byte[12 + talkTextBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(
            talkPayload.AsSpan(4, 4),
            checked((uint)talkTextBytes.Length + sizeof(ushort)));
        talkTextBytes.CopyTo(talkPayload.AsSpan(12));
        Check.True(
            GameClientHandler.TryReadTalkText(talkPayload, out var parsedTalkText) &&
            parsedTalkText == talkText,
            "captured Talk payload shape yields the developer command text");

        var capturedMaskedTalkPayload = Convert.FromHexString(
            "481400003C000000100500D5" +
            "740065007300740032003A002F002A002A002A002A002A002A002000" +
            "61006400640020006300720079007300740061006C0031002000390039000000");
        Check.True(
            GameClientHandler.TryReadTalkText(capturedMaskedTalkPayload, out var maskedTalkText) &&
            maskedTalkText == "test2:/****** add crystal1 99" &&
            DeveloperItemCommand.TryParse(maskedTalkText, out var capturedMaskedRequest, out _) &&
            capturedMaskedRequest is { Material.ItemId: 4230, Quantity: 99 },
            "live masked /gmitem Talk payload still reaches the guarded grant command");

        BinaryPrimitives.WriteUInt32LittleEndian(talkPayload.AsSpan(4, 4), uint.MaxValue);
        Check.True(
            !GameClientHandler.TryReadTalkText(talkPayload, out _),
            "malformed Talk text length is rejected");

        var disabledAccess = new DeveloperCommandOptions
        {
            Enabled = false,
            AllowedAccountIds = [3, 7, 13, 347]
        };
        Check.True(!disabledAccess.Allows(3), "developer command defaults can fail closed");
        var allowlistedAccess = new DeveloperCommandOptions
        {
            Enabled = true,
            AllowedAccountIds = [3, 7, 13, 347]
        };
        Check.True(allowlistedAccess.Allows(13), "exact configured account is authorized");
        Check.True(!allowlistedAccess.Allows(14), "unlisted neighboring account is denied");

        var partialStackBag = KitBagSlots.SetSlot(
            GameDefaults.StarterKitBag,
            2,
            "[4230,,,,,,1,1,1,98,0]");
        Check.True(
            KitBagItemGrantPlanner.TryAdd(
                partialStackBag,
                4230,
                quantity: 101,
                stackCap: 99,
                bound: 1,
                out var plannedBag),
            "bag planner can fill one partial stack and allocate additional stacks");
        Check.Equal((short)99, KitBagSlots.GetItem(plannedBag, 2).Stack, "partial native stack is filled first");
        Check.Equal((short)99, KitBagSlots.GetItem(plannedBag, 3).Stack, "new native stack respects cap");
        Check.Equal((short)1, KitBagSlots.GetItem(plannedBag, 4).Stack, "remaining quantity uses the next empty slot");

        var nearlyFullBag = GameDefaults.StarterKitBag;
        for (var slot = 0; slot < KitBagItemGrantPlanner.SlotCount; slot++)
        {
            nearlyFullBag = KitBagSlots.SetSlot(
                nearlyFullBag,
                slot,
                slot == 0
                    ? "[4230,,,,,,1,1,1,98,0]"
                    : "[4000,,,,,,1,1,1,99,0]");
        }

        Check.True(
            !KitBagItemGrantPlanner.TryAdd(
                nearlyFullBag,
                4230,
                quantity: 2,
                stackCap: 99,
                bound: 1,
                out var rejectedBag),
            "bag planner rejects a quantity that cannot fully fit");
        Check.Equal(nearlyFullBag, rejectedBag, "failed capacity plan is atomic and leaves the bag byte-for-byte unchanged");
    }
}
