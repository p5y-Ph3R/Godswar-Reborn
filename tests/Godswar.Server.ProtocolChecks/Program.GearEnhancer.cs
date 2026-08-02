using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static Task CheckGearEnhancerInitialProtocolAsync()
    {
        const uint capturedOriginEnhancerId = 5140;
        const int originEnhancerDialogIndex = 118;

        Check.True(
            GearEnhancerProtocol.IsEnhancerNpcKey("Sparta_070"),
            "Sparta enhancer script key is routed");
        Check.True(
            GearEnhancerProtocol.IsEnhancerNpcKey("Athens_070"),
            "Athens enhancer script key is routed");
        Check.True(
            !GearEnhancerProtocol.IsEnhancerNpcKey("Sparta_143"),
            "Origin Enhancer is not repurposed as the Gear Mentor");
        Check.True(
            !GearEnhancerProtocol.IsEnhancerNpcKey("sparta_070"),
            "enhancer script-key matching remains exact");
        Check.True(
            GearEnhancerProtocol.IsOriginEnhancerNpcKey("Sparta_143") &&
            GearEnhancerProtocol.IsOriginEnhancerNpcKey("Athens_143"),
            "Origin Enhancer keys have their own exact route");
        Check.True(
            GearEnhancerProtocol.IsOriginEnhancerEndpoint(
                "Sparta_143",
                GearEnhancerProtocol.SpartaOriginEnhancerNpcId) &&
            GearEnhancerProtocol.IsOriginEnhancerEndpoint(
                "Athens_143",
                GearEnhancerProtocol.AthensOriginEnhancerNpcId),
            "only the two physical NPC-143 endpoints enter the Origin Enhancer route");
        Check.True(
            !GearEnhancerProtocol.IsOriginEnhancerEndpoint("Sparta_143", 0),
            "virtual NPC zero cannot enter the physical Origin Enhancer route");
        Check.True(
            !GearEnhancerProtocol.IsOriginEnhancerNpcKey("Sparta_070"),
            "Gear Mentor cannot enter the Origin Enhancer route");
        Check.Equal(originEnhancerDialogIndex, GearEnhancerProtocol.OriginDialogIndex, "physical Origin Enhancer owns dialog 118");

        var spartaEndpoint = GearEnhancerProtocol.ResolveEndpoint(GameDefaults.SpartaCamp);
        Check.Equal(GearEnhancerProtocol.SpartaEnhancerNpcId, spartaEndpoint.NpcId, "physical Sparta Gear Mentor id");
        Check.Equal("Sparta_070", spartaEndpoint.NpcKey, "physical Sparta Gear Mentor script");
        var athensEndpoint = GearEnhancerProtocol.ResolveEndpoint(GameDefaults.AthensCamp);
        Check.Equal(GearEnhancerProtocol.AthensEnhancerNpcId, athensEndpoint.NpcId, "physical Athens Gear Mentor id");
        Check.Equal("Athens_070", athensEndpoint.NpcKey, "physical Athens Gear Mentor script");
        Check.Equal(
            athensEndpoint,
            GearEnhancerProtocol.ResolveEndpoint(byte.MaxValue),
            "invalid faction uses the same safe Athens fallback as character creation");

        var spartaDefinition = NpcSpawnDefinitionFactory.Create(
                0,
                [],
                [],
                [])
            .Single(definition => definition.NpcKey == spartaEndpoint.NpcKey);
        Check.Equal(spartaEndpoint.NpcId, spartaDefinition.InteractionId, "Sparta protocol id matches physical factory id");
        Check.Equal("Sparta_070_Male22", spartaDefinition.TemplateKey, "Sparta Gear Mentor uses the stock client appearance");
        Check.Equal(142f, spartaDefinition.X, "Sparta Gear Mentor captured x coordinate");
        Check.Equal(-165f, spartaDefinition.Z, "Sparta Gear Mentor actor-table z coordinate");
        var athensDefinition = NpcSpawnDefinitionFactory.Create(
                1,
                [],
                [],
                [])
            .Single(definition => definition.NpcKey == athensEndpoint.NpcKey);
        Check.Equal(athensEndpoint.NpcId, athensDefinition.InteractionId, "Athens protocol id matches physical factory id");
        Check.Equal("Athens_070_Male22", athensDefinition.TemplateKey, "Athens Gear Mentor uses the stock client appearance");
        Check.Equal(142f, athensDefinition.X, "Athens Gear Mentor actor-table x coordinate");
        Check.Equal(-165f, athensDefinition.Z, "Athens Gear Mentor actor-table z coordinate");
        Check.Equal(1.7f, athensDefinition.Facing, "Athens Gear Mentor actor-table facing");

        var dialogOpen = PacketBuilder.NpcDialogOpenAck(
            spartaEndpoint.NpcId,
            GearEnhancerProtocol.DialogIndex,
            spartaEndpoint.NpcKey);
        Check.Equal((ushort)48, ReadUInt16(dialogOpen, 0), "Sparta enhancer dialog-open packet length");
        Check.Equal((ushort)Opcodes.NpcDialogOpen, ReadUInt16(dialogOpen, 2), "Sparta enhancer dialog-open opcode");
        Check.Equal(spartaEndpoint.NpcId, ReadUInt32(dialogOpen, 4), "Sparta enhancer dialog-open NPC id");
        Check.Equal(0x200, ReadInt32(dialogOpen, 8), "Sparta enhancer uses the extended-dialog flag");
        Check.Equal(4, GearEnhancerProtocol.DialogIndex, "Gear Mentor uses NPC_FLAG_SYS_BREAK dialog 4");
        Check.Equal(GearEnhancerProtocol.DialogIndex, ReadInt32(dialogOpen, 12), "Sparta enhancer dialog-open index");
        Check.Equal(spartaEndpoint.NpcKey, ReadFixedAscii(dialogOpen, 16, 32), "Sparta enhancer dialog-open script key");

        var combinedDialogOpen = PacketBuilder.NpcDialogOpenAck(
            spartaEndpoint.NpcId,
            [GearEnhancerProtocol.DialogIndex, ClassSuitProtocol.DialogIndex],
            spartaEndpoint.NpcKey);
        Check.Equal(
            37_004,
            ReadInt32(combinedDialogOpen, 12),
            "stock client encoding advertises Gear Enhancement before Class Suit");
        Check.Throws<ArgumentException>(
            () => PacketBuilder.NpcDialogOpenAck(
                spartaEndpoint.NpcId,
                [4, 4],
                spartaEndpoint.NpcKey),
            "duplicate top-level NPC dialogs are rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.NpcDialogOpenAck(
                spartaEndpoint.NpcId,
                [1, 2, 3, 4],
                spartaEndpoint.NpcKey),
            "more than three packed NPC dialogs are rejected");

        var dialogueRoutes = NpcDialogueBaselineV1.CreateRoutes();
        var spartaRoute = dialogueRoutes.Single(
            route => route.NpcKey == spartaEndpoint.NpcKey);
        Check.True(
            NpcDialogueBehaviorRegistry.IsAllowed(
                spartaDefinition,
                spartaRoute),
            "database baseline binds the physical Sparta Gear Mentor");
        Check.Equal(
            GearEnhancerProtocol.DialogIndex,
            spartaRoute.DialogIndex,
            "database baseline keeps Gear Mentor dialog 4");
        var spartaMenu = PacketBuilder.NpcFunctionActionResponse(
            spartaEndpoint.NpcId,
            spartaRoute.DialogIndex,
            spartaRoute.InitialMenuSubIds.ToArray());
        Check.Equal((ushort)48, ReadUInt16(spartaMenu, 0), "Sparta original Gear Mentor menu packet length");
        Check.Equal(spartaEndpoint.NpcId, ReadUInt32(spartaMenu, 4), "Sparta enhancer menu NPC id");
        for (var menuId = GearEnhancerProtocol.FirstGearMentorMenuSubId;
             menuId <= GearEnhancerProtocol.LastGearMentorMenuSubId;
             menuId++)
        {
            Check.Equal(
                menuId,
                ReadInt32(spartaMenu, 12 + ((menuId - 1) * sizeof(int))),
                $"Sparta original Gear Mentor menu includes position {menuId}");
        }

        var athensEnhancerId = athensEndpoint.NpcId;
        var athensRoute = dialogueRoutes.Single(
            route => route.NpcKey == athensEndpoint.NpcKey);
        Check.True(
            NpcDialogueBehaviorRegistry.IsAllowed(
                athensDefinition,
                athensRoute),
            "database baseline binds the physical Athens Gear Mentor");
        var athensMenu = PacketBuilder.NpcFunctionActionResponse(
            athensEnhancerId,
            athensRoute.DialogIndex,
            athensRoute.InitialMenuSubIds.ToArray());
        Check.Equal((ushort)48, ReadUInt16(athensMenu, 0), "Athens original Gear Mentor menu packet length");
        Check.Equal((ushort)Opcodes.NpcFunctionActionResponse, ReadUInt16(athensMenu, 2), "Athens enhancer menu opcode");
        Check.Equal(athensEnhancerId, ReadUInt32(athensMenu, 4), "Athens enhancer NPC id");
        Check.Equal(GearEnhancerProtocol.DialogIndex, ReadInt32(athensMenu, 8), "Athens enhancer dialog index");
        for (var menuId = GearEnhancerProtocol.FirstGearMentorMenuSubId;
             menuId <= GearEnhancerProtocol.LastGearMentorMenuSubId;
             menuId++)
        {
            Check.Equal(
                menuId,
                ReadInt32(athensMenu, 12 + ((menuId - 1) * sizeof(int))),
                $"Athens original Gear Mentor menu includes position {menuId}");
        }
        Check.True(
            GearEnhancerProtocol.IsGearMentorMenuSubId(1) &&
            GearEnhancerProtocol.IsGearMentorMenuSubId(9) &&
            !GearEnhancerProtocol.IsGearMentorMenuSubId(0) &&
            !GearEnhancerProtocol.IsGearMentorMenuSubId(10),
            "only original Gear Mentor menu IDs 1 through 9 are recognized");
        Check.True(
            new[] { 5, 7 }.All(GearEnhancerProtocol.IsUnavailableGearMentorMenuSubId) &&
            new[] { 1, 2, 3, 4, 6, 8, 9 }.All(static subId => !GearEnhancerProtocol.IsUnavailableGearMentorMenuSubId(subId)),
            "only Instructions and Wash Dust remain temporarily disabled");
        Check.Equal(999, GearEnhancerProtocol.TemporarilyDisabledResultSubId, "unimplemented original operations use native Temporarily Disabled result");

        var originDialogOpen = PacketBuilder.NpcDialogOpenAck(
            capturedOriginEnhancerId,
            GearEnhancerProtocol.OriginDialogIndex,
            "Sparta_143");
        Check.Equal(capturedOriginEnhancerId, ReadUInt32(originDialogOpen, 4), "physical Origin Enhancer dialog-open NPC id");
        Check.Equal(GearEnhancerProtocol.OriginDialogIndex, ReadInt32(originDialogOpen, 12), "physical Origin Enhancer opens dialog 118");
        Check.Equal("Sparta_143", ReadFixedAscii(originDialogOpen, 16, 32), "physical Origin Enhancer keeps its own script key");
        var originDefinition = NpcSpawnDefinitionFactory.Create(
                0,
                [],
                [],
                [])
            .Single(definition => definition.NpcKey == "Sparta_143");
        var originRoute = dialogueRoutes.Single(
            route => route.NpcKey == "Sparta_143");
        Check.True(
            NpcDialogueBehaviorRegistry.IsAllowed(
                originDefinition,
                originRoute),
            "database baseline binds the physical Origin Enhancer");
        var originMenu = PacketBuilder.NpcFunctionActionResponse(
            capturedOriginEnhancerId,
            originRoute.DialogIndex,
            originRoute.InitialMenuSubIds.ToArray());
        Check.Equal(capturedOriginEnhancerId, ReadUInt32(originMenu, 4), "physical Origin menu keeps object 5140");
        Check.Equal(GearEnhancerProtocol.OriginDialogIndex, ReadInt32(originMenu, 8), "physical Origin menu keeps dialog 118");
        Check.Equal(GearEnhancerProtocol.EnhanceAttributeSubId, ReadInt32(originMenu, 12), "physical Origin captured first menu id");
        Check.Equal(GearEnhancerProtocol.AddAttributeSubId, ReadInt32(originMenu, 16), "physical Origin captured second menu id");
        Check.Equal(GearEnhancerProtocol.DeleteAttributesSubId, ReadInt32(originMenu, 20), "physical Origin captured third menu id");

        var physicalOperationPage = GearEnhancerProtocol.BuildOperationPageResponse(
            spartaEndpoint.NpcId,
            GearEnhancerProtocol.DialogIndex,
            GearEnhancerProtocol.EnhanceAttributeSubId);
        Check.Equal(GearEnhancerProtocol.DialogIndex, ReadInt32(physicalOperationPage, 8), "physical operation page remains dialog 4");
        var originOperationPage = GearEnhancerProtocol.BuildOperationPageResponse(
            capturedOriginEnhancerId,
            GearEnhancerProtocol.OriginDialogIndex,
            GearEnhancerProtocol.EnhanceAttributeSubId);
        Check.Equal(capturedOriginEnhancerId, ReadUInt32(originOperationPage, 4), "physical Origin operation page keeps object 5140");
        Check.Equal(GearEnhancerProtocol.OriginDialogIndex, ReadInt32(originOperationPage, 8), "physical Origin operation page remains dialog 118");

        var selectionArgs = Enumerable.Repeat(
                -1,
                GearEnhancerProtocol.FunctionActionArgumentCount)
            .ToArray();
        Check.True(
            GearEnhancerProtocol.ReadSelection(selectionArgs, out _, out _, out _) ==
                GearEnhancerSelectionShape.MenuSelection,
            "empty fixed slots open the selected operation page");
        selectionArgs[GearEnhancerProtocol.GearArgumentIndex] = 100;
        selectionArgs[GearEnhancerProtocol.CatalystArgumentIndex] = 195;
        selectionArgs[GearEnhancerProtocol.AttributeStoneArgumentIndex] = 142;
        Check.True(
            GearEnhancerProtocol.ReadSelection(
                selectionArgs,
                out var gearSlot,
                out var catalystSlot,
                out var stoneSlot) == GearEnhancerSelectionShape.Commit,
            "fixed native enhancer records are accepted as a commit");
        Check.Equal(0, gearSlot, "native gear bag reference decodes exactly");
        Check.Equal(95, catalystSlot, "native catalyst bag reference decodes exactly");
        Check.Equal(42, stoneSlot, "native stone bag reference decodes exactly");
        selectionArgs[0] = 100;
        Check.True(
            GearEnhancerProtocol.ReadSelection(selectionArgs, out _, out _, out _) ==
                GearEnhancerSelectionShape.MenuSelection,
            "a scratch-tail lookalike cannot mutate inventory");

        var emptyItem = CompactItemEntry.Empty;
        var missingCatalystRequest = new GearEnhancementRequest(
            GearEnhancementOperation.Add,
            new GearEnhancementSlotSelection(0, emptyItem with { Id = 1000, Stack = 1 }),
            new GearEnhancementSlotSelection(1, emptyItem with { Id = 9930, Stack = 1 }),
            new GearEnhancementSlotSelection(2, emptyItem));
        var missingCatalystResult = new GearEnhancementResult(
            GearEnhancementStatus.SelectionMissing,
            GearEnhancementOperation.Add,
            GameDefaults.EmptyKitBag,
            GameDefaults.EmptyKitBag,
            missingCatalystRequest.Gear.ExpectedItem,
            missingCatalystRequest.Gear.ExpectedItem,
            []);
        Check.Equal(
            GearEnhancerProtocol.MissingFlameSparkResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Add,
                missingCatalystResult,
                missingCatalystRequest),
            "missing Add catalyst maps to the native Flame Spark message");
        Check.Equal(
            GearEnhancerProtocol.QuartzLevelMismatchResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Enhance,
                missingCatalystResult with
                {
                    Status = GearEnhancementStatus.QuartzLevelMismatch,
                    Operation = GearEnhancementOperation.Enhance
                }),
            "wrong Quartz level maps to the native mismatch message");
        Check.Equal(
            GearEnhancerProtocol.DeleteSucceededResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Delete,
                missingCatalystResult with
                {
                    Status = GearEnhancementStatus.Succeeded,
                    Operation = GearEnhancementOperation.Delete
                }),
            "Delete success maps to the native result page");

        Check.True(
            !NpcDialogueBehaviorRegistry.IsAllowed(
                spartaDefinition,
                spartaRoute with
                {
                    DialogIndex = GearEnhancerProtocol.DialogIndex + 1
                }),
            "a valid Gear Mentor cannot use a mismatched database dialog");
        Check.True(
            !NpcDialogueBehaviorRegistry.IsAllowed(
                spartaDefinition,
                spartaRoute with
                {
                    InitialMenuSubIds = [1, 2, 3, 4, 5, 6, 7, 8]
                }),
            "a valid Gear Mentor cannot use a mismatched database menu");
        Check.True(
            !NpcDialogueBehaviorRegistry.IsAllowed(
                spartaDefinition,
                spartaRoute with
                {
                    Behavior = NpcDialogueBehavior.OriginEnhancer,
                    DialogIndex =
                        GearEnhancerProtocol.OriginDialogIndex,
                    InitialMenuSubIds = [2, 3, 6]
                }),
            "a valid Gear Mentor cannot be rebound to another behavior");
        Check.True(
            !NpcDialogueBehaviorRegistry.IsAllowed(
                originDefinition with { InteractionId = 0 },
                originRoute),
            "removed virtual NPC zero cannot use the Origin Enhancer route");

        return Task.CompletedTask;
    }
}
