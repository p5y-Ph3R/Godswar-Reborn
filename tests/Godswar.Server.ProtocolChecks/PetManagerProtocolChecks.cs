using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static class PetManagerProtocolChecks
{
    public static Task RunAsync()
    {
        Check.Equal(31, PetManagerProtocol.DialogIndex,
            "stock Pet Manager dialogue index");
        Check.Equal(36, PetManagerProtocol.PointResetDialogIndex,
            "stock Pet Manager point-reset dialogue index");
        Check.True(
            PetManagerProtocol.InitialMenuSubIds.SequenceEqual(
                Enumerable.Range(1, 11)),
            "stock Pet Manager exposes all eleven root choices");
        Check.True(
            PetManagerProtocol.PointResetInitialMenuSubIds.SequenceEqual(
                new[] { 100, 101 }),
            "point-reset dialogue exposes savvy and growth reset choices");
        Check.True(
            PetManagerProtocol.IsEndpoint("Athens_088", 5227) &&
            PetManagerProtocol.IsEndpoint("Sparta_088", 5085) &&
            PetManagerProtocol.IsEndpoint("Sparta_088", 5087),
            "published and source Pet Manager endpoints remain compatible");
        Check.True(
            !PetManagerProtocol.IsEndpoint("Athens_088", 5085) &&
            !PetManagerProtocol.IsEndpoint("Sparta_086", 5085),
            "unrelated NPC identities cannot acquire Pet Manager behavior");

        (int MenuSubId, int[] ResponseSubIds)[] expectedPages =
        [
            (1, [11, 101]),
            (2, [12, 102]),
            (3, [13, 103]),
            (4, [14, 104]),
            (5, [15, 105]),
            (7, [17, 112]),
            (8, [113])
        ];
        foreach (var (menuSubId, expectedPage) in expectedPages)
        {
            Check.True(
                PetManagerProtocol.TryGetInformationPage(
                    PetManagerProtocol.DialogIndex,
                    menuSubId,
                    out var actual) &&
                actual.SequenceEqual(expectedPage),
                $"Pet Manager choice {menuSubId} uses its captured page");
        }
        Check.True(
            !PetManagerProtocol.TryGetInformationPage(0, out _) &&
            !PetManagerProtocol.TryGetInformationPage(6, out _) &&
            !PetManagerProtocol.TryGetInformationPage(9, out _) &&
            !PetManagerProtocol.TryGetInformationPage(12, out _),
            "dynamic or uncaptured mutation pages are not exposed as static pages");
        Check.True(
            PetManagerProtocol.TryBuildSkillUnlearnPage(
                [11, 0, 6],
                out var sparseSkillPage) &&
            sparseSkillPage.SequenceEqual(
                new[] { 16, 106, 114, 119 }),
            "skill removal page sorts and maps only authoritative learned slots");
        Check.True(
            PetManagerProtocol.TryBuildSkillUnlearnPage(
                Enumerable.Range(0, 12).ToArray(),
                out var fullSkillPage) &&
            fullSkillPage.SequenceEqual(
                new[]
                {
                    16,
                    106, 107, 108, 109, 110, 111,
                    114, 115, 116, 117, 118, 119
                }),
            "skill removal page retains the complete stock twelve-slot mapping");
        Check.True(
            !PetManagerProtocol.TryBuildSkillUnlearnPage([], out _) &&
            !PetManagerProtocol.TryBuildSkillUnlearnPage([0, 0], out _) &&
            !PetManagerProtocol.TryBuildSkillUnlearnPage([-1], out _) &&
            !PetManagerProtocol.TryBuildSkillUnlearnPage([12], out _),
            "empty, duplicate, and out-of-range skill projections fail closed");
        Check.True(
            PetManagerProtocol.TryGetInformationPage(
                PetManagerProtocol.PointResetDialogIndex,
                100,
                out var savvyResetPage) &&
            savvyResetPage.SequenceEqual(new[] { 111, 116 }) &&
            PetManagerProtocol.TryGetInformationPage(
                PetManagerProtocol.PointResetDialogIndex,
                101,
                out var growthResetPage) &&
            growthResetPage.SequenceEqual(new[] { 112, 117 }),
            "point-reset choices use the stock informational pages");
        Check.True(
            !PetManagerProtocol.TryGetInformationPage(
                PetManagerProtocol.DialogIndex,
                100,
                out _) &&
            !PetManagerProtocol.TryGetInformationPage(
                PetManagerProtocol.PointResetDialogIndex,
                1,
                out _) &&
            !PetManagerProtocol.TryGetInformationPage(
                PetManagerProtocol.PointResetDialogIndex,
                116,
                out _),
            "cross-dialog and reset-confirm mutations fail closed");

        var exactArguments = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        Check.True(
            PetManagerProtocol.IsExactNavigationArguments(exactArguments),
            "Pet Manager navigation padding is exactly eighteen -1 values");
        var nestedMutationArguments = (int[])exactArguments.Clone();
        nestedMutationArguments[0] = 106;
        Check.True(
            PetManagerProtocol.TryResolveSkillUnlearnMutation(
                PetManagerProtocol.SkillUnlearnMenuSubId,
                nestedMutationArguments,
                out var nestedSlot) &&
            nestedSlot == 0,
            "stock nested skill removal appends the selected slot choice");
        var nonExactArguments = (int[])exactArguments.Clone();
        nonExactArguments[0] = 0;
        Check.True(
            !PetManagerProtocol.IsExactNavigationArguments(
                nonExactArguments) &&
            !PetManagerProtocol.TryResolveSkillUnlearnMutation(
                PetManagerProtocol.SkillUnlearnMenuSubId,
                nonExactArguments,
                out _) &&
            !PetManagerProtocol.IsExactNavigationArguments(
                exactArguments[..^1]),
            "Pet Manager navigation and mutations reject loose padding");

        for (var slot = 0; slot < 12; slot++)
        {
            var subId = slot < 6
                ? 106 + slot
                : 108 + slot;
            Check.True(
                PetManagerProtocol.TryResolveSkillUnlearnSlot(
                    subId,
                    out var actualSlot) &&
                actualSlot == slot,
                $"Pet Manager erase choice {subId} maps slot {slot + 1}");
        }
        Check.True(
            !PetManagerProtocol.TryResolveSkillUnlearnSlot(112, out _) &&
            !PetManagerProtocol.TryResolveSkillUnlearnSlot(113, out _) &&
            !PetManagerProtocol.TryResolveSkillUnlearnSlot(120, out _),
            "non-erase Pet Manager choices cannot select a skill slot");
        Check.Equal(1011, PetManagerProtocol.NoSummonedPetResultSubId,
            "native no-summoned-pet result");
        Check.Equal(1061,
            PetManagerProtocol.MissingStrongPurgePotionResultSubId,
            "native missing-purge-potion result");
        Check.Equal(1062, PetManagerProtocol.EmptySkillSlotResultSubId,
            "native empty-skill-slot result");
        Check.Equal(1063, PetManagerProtocol.SkillUnlearnedResultSubId,
            "native skill-unlearned result");
        Check.Equal(127,
            PetManagerProtocol.GrowthResetMissingFeatherResultSubId,
            "NpcFunPett native missing-feather result");
        Check.Equal(128,
            PetManagerProtocol.GrowthResetNoPetResultSubId,
            "NpcFunPett native no-summoned-pet result");
        Check.Equal(129,
            PetManagerProtocol.GrowthResetPreviewUnavailableResultSubId,
            "NpcFunPett native missing-preview result");
        Check.Equal(130,
            PetManagerProtocol.GrowthResetSucceededResultSubId,
            "NpcFunPett native Growth-reset success page");
        Check.True(
            PetManagerProtocol.BuildGrowthResetSuccessPage(
                [0.01m, 0.02m, 1.23m, 2.34m, 3.45m, 4.56m],
                [4.56m, 3.45m, 2.34m, 1.23m, 0.02m, 0.01m])
                .SequenceEqual(
                    new[]
                    {
                        130, 108, 209, 12_310, 23_411, 34_512, 45_613,
                        45_620, 34_521, 23_422, 12_323, 224, 125
                    }),
            "Growth reset encodes rolled 08-13 and current 20-25 rows");
        Check.Equal(127,
            PetManagerProtocol.BasicSavvyResetMissingFeatherResultSubId,
            "Basic/Savvy reset missing-feather result");
        Check.Equal(128,
            PetManagerProtocol.BasicSavvyResetNoPetResultSubId,
            "Basic/Savvy reset no-summoned-pet result");
        Check.Equal(129,
            PetManagerProtocol.BasicSavvyResetPreviewUnavailableResultSubId,
            "Basic/Savvy reset missing-preview result");
        Check.Equal(120,
            PetManagerProtocol.BasicSavvyResetSucceededResultSubId,
            "NpcFunPett native Basic/Savvy reset success page");
        Check.True(
            PetManagerProtocol.BuildBasicSavvyResetSuccessPage(
                [0.01m, 0.02m, 12.34m, 23.45m, 34.56m, 45.67m])
                .SequenceEqual(
                    new[] { 120, 102, 203, 123_404, 234_505,
                        345_606, 456_707 }),
            "Basic/Savvy result encodes six hundredth-precision rows with native suffixes 02-07");

        var directGrowthReset = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        var nestedGrowthReset = directGrowthReset.ToArray();
        nestedGrowthReset[0] =
            PetManagerProtocol.GrowthResetActionSubId;
        Check.True(
            PetManagerProtocol.TryResolveGrowthResetMutation(
                PetManagerProtocol.PointResetDialogIndex,
                PetManagerProtocol.GrowthResetMenuSubId,
                nestedGrowthReset) &&
            PetManagerProtocol.TryResolveGrowthResetMutation(
                PetManagerProtocol.PointResetDialogIndex,
                PetManagerProtocol.GrowthResetActionSubId,
                directGrowthReset),
            "NpcFunPett Phoenix Growth reset accepts strict nested and direct action-117 shapes");
        nestedGrowthReset[1] = 0;
        Check.True(
            PetManagerProtocol.TryResolveGrowthResetMutation(
                PetManagerProtocol.PointResetDialogIndex,
                PetManagerProtocol.GrowthResetMenuSubId,
                nestedGrowthReset,
                out var nestedAccept) &&
            nestedAccept == PetGrowthResetRequestOperation.Accept &&
            !PetManagerProtocol.TryResolveGrowthResetMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.GrowthResetActionSubId,
                directGrowthReset),
            "Phoenix Growth OK has an exact discriminator and rejects dialogue confusion");
        nestedGrowthReset[2] = 0;
        Check.True(
            !PetManagerProtocol.TryResolveGrowthResetMutation(
                PetManagerProtocol.PointResetDialogIndex,
                PetManagerProtocol.GrowthResetMenuSubId,
                nestedGrowthReset),
            "Phoenix Growth reset rejects extra zero padding");

        var directBasicReset = Enumerable.Repeat(
            -1,
            PetManagerProtocol.FunctionArgumentCount).ToArray();
        var nestedBasicReset = directBasicReset.ToArray();
        nestedBasicReset[0] =
            PetManagerProtocol.BasicSavvyResetActionSubId;
        Check.True(
            PetManagerProtocol.TryResolveBasicSavvyResetMutation(
                PetManagerProtocol.PointResetDialogIndex,
                PetManagerProtocol.BasicSavvyResetMenuSubId,
                nestedBasicReset,
                out var nestedBasicPreview) &&
            nestedBasicPreview ==
                PetBasicSavvyResetRequestOperation.Preview &&
            PetManagerProtocol.TryResolveBasicSavvyResetMutation(
                PetManagerProtocol.PointResetDialogIndex,
                PetManagerProtocol.BasicSavvyResetActionSubId,
                directBasicReset,
                out var directBasicPreview) &&
            directBasicPreview ==
                PetBasicSavvyResetRequestOperation.Preview,
            "Fairy's Feather reset accepts exact nested and direct action-116 preview shapes");
        nestedBasicReset[1] = 0;
        directBasicReset[0] = 0;
        Check.True(
            PetManagerProtocol.TryResolveBasicSavvyResetMutation(
                PetManagerProtocol.PointResetDialogIndex,
                PetManagerProtocol.BasicSavvyResetMenuSubId,
                nestedBasicReset,
                out var nestedBasicAccept) &&
            nestedBasicAccept ==
                PetBasicSavvyResetRequestOperation.Accept &&
            PetManagerProtocol.TryResolveBasicSavvyResetMutation(
                PetManagerProtocol.PointResetDialogIndex,
                PetManagerProtocol.BasicSavvyResetActionSubId,
                directBasicReset,
                out var directBasicAccept) &&
            directBasicAccept ==
                PetBasicSavvyResetRequestOperation.Accept,
            "Fairy's Feather OK accepts exact nested and direct action-116 accept shapes");
        nestedBasicReset[2] = 0;
        Check.True(
            !PetManagerProtocol.TryResolveBasicSavvyResetMutation(
                PetManagerProtocol.PointResetDialogIndex,
                PetManagerProtocol.BasicSavvyResetMenuSubId,
                nestedBasicReset) &&
            !PetManagerProtocol.TryResolveBasicSavvyResetMutation(
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.BasicSavvyResetActionSubId,
                directBasicReset) &&
            !PetManagerProtocol.TryResolveBasicSavvyResetMutation(
                PetManagerProtocol.PointResetDialogIndex,
                PetManagerProtocol.BasicSavvyResetActionSubId,
                directBasicReset[..^1]),
            "Fairy's Feather reset rejects padding, dialogue, and length confusion");

        var open = PacketBuilder.NpcDialogOpenAck(
            PetManagerProtocol.AthensNpcId,
            [
                PetManagerProtocol.DialogIndex,
                PetManagerProtocol.PointResetDialogIndex
            ],
            "Athens_088");
        Check.Equal((ushort)48, ReadUInt16(open, 0),
            "Pet Manager two-route open frame length");
        Check.Equal((ushort)Opcodes.NpcDialogOpen, ReadUInt16(open, 2),
            "Pet Manager two-route open opcode");
        Check.Equal(PetManagerProtocol.AthensNpcId, ReadUInt32(open, 4),
            "Pet Manager two-route open NPC identity");
        Check.Equal(0x200, ReadInt32(open, 8),
            "Pet Manager two-route extended-function flag");
        Check.Equal(36_031, ReadInt32(open, 12),
            "Pet Raising is advertised before Reset Pet's Points");
        Check.Equal("Athens_088",
            Encoding.ASCII.GetString(open, 16, 10),
            "Pet Manager two-route client script key");
        Check.True(
            open.AsSpan(26, 22).ToArray().All(static value => value == 0),
            "Pet Manager two-route open has exact zero padding");

        var menu = PacketBuilder.NpcFunctionActionResponse(
            PetManagerProtocol.AthensNpcId,
            PetManagerProtocol.DialogIndex,
            PetManagerProtocol.InitialMenuSubIds.ToArray());
        Check.Equal((ushort)56, ReadUInt16(menu, 0),
            "eleven-choice Pet Manager packet length");
        Check.Equal((ushort)Opcodes.NpcFunctionActionResponse,
            ReadUInt16(menu, 2), "Pet Manager menu opcode");
        Check.Equal(PetManagerProtocol.AthensNpcId,
            ReadUInt32(menu, 4), "Pet Manager menu NPC identity");
        Check.Equal(PetManagerProtocol.DialogIndex,
            ReadInt32(menu, 8), "Pet Manager menu dialogue index");
        for (var index = 0;
             index < PetManagerProtocol.InitialMenuSubIds.Count;
             index++)
        {
            Check.Equal(
                index + 1,
                ReadInt32(menu, 12 + (index * sizeof(int))),
                $"Pet Manager menu choice {index + 1}");
        }

        return Task.CompletedTask;
    }

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static int ReadInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
}
