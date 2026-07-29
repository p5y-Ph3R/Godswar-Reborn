using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorDecomposeGearCommandContractChecks
{
    private static readonly Guid ClientOperationId =
        Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
    private static readonly CommandSubject Subject = new(347, 7);

    public static Task RunAsync()
    {
        CheckCommandBounds();
        CheckSecureProvenanceAndFamily();
        CheckCanonicalEnvelope();
        CheckEnvelopeConflicts();
        CheckNativeResultMapping();
        CheckReceiptAndResultInvariants();
        return Task.CompletedTask;
    }

    private static void CheckCommandBounds()
    {
        var oneSelection = new[]
        {
            Selection(95, "[10001,,,,,,2,5,1,1,0,0]")
        };
        Check.True(
            TryCreate(
                GearMentorDecomposeGearCommandEnvelope
                    .SpartaGearMentorNpcId,
                oneSelection,
                out _),
            "Decompose accepts one selected gear item");

        var orderedSelections = new[]
        {
            Selection(50, "[10001,,,,,,2,5,1,1,0,0]"),
            Selection(2, "[10002,,,,,,3,6,0,1,0,0]"),
            Selection(31, "[10003,,,,,,4,7,1,1,0,0]")
        };
        Check.True(
            TryCreate(
                GearMentorDecomposeGearCommandEnvelope
                    .AthensGearMentorNpcId,
                orderedSelections,
                out var command),
            "Decompose accepts three distinct ordered selections");
        Check.Equal(
            50,
            command.Selections[0].SelectedKitBagSlot,
            "Decompose preserves client selection order");
        Check.Equal(
            2,
            command.Selections[1].SelectedKitBagSlot,
            "Decompose does not sort selections by bag slot");

        orderedSelections[0] = Selection(77, "changed");
        Check.Equal(
            50,
            command.Selections[0].SelectedKitBagSlot,
            "Decompose command owns an immutable selection copy");

        foreach (var selections in new[]
                 {
                     Array.Empty<GearMentorDecomposeSelection>(),
                     Enumerable.Range(0, 4)
                         .Select(index => Selection(index, "state"))
                         .ToArray(),
                     new[]
                     {
                         Selection(7, "state-a"),
                         Selection(7, "state-b")
                     },
                     new[] { Selection(-1, "state") },
                     new[] { Selection(96, "state") }
                 })
        {
            Check.True(
                !TryCreate(
                    GearMentorDecomposeGearCommandEnvelope
                        .AthensGearMentorNpcId,
                    selections,
                    out _),
                "Decompose rejects invalid count, slot, or duplicate slot");
        }

        foreach (var state in new[]
                 {
                     string.Empty,
                     "   ",
                     "line\nbreak",
                     new string('a', 513),
                     "\ud800"
                 })
        {
            Check.True(
                !TryCreate(
                    GearMentorDecomposeGearCommandEnvelope
                        .AthensGearMentorNpcId,
                    [Selection(1, state)],
                    out _),
                "Decompose rejects an invalid compact item snapshot");
        }

        Check.True(
            !TryCreate(
                GearMentorDecomposeGearCommandEnvelope
                    .AthensGearMentorNpcId,
                [
                    Selection(1, new string('a', 400)),
                    Selection(2, new string('b', 400)),
                    Selection(3, new string('c', 400))
                ],
                out _),
            "Decompose bounds the combined canonical snapshot bytes");
        Check.True(
            !GearMentorDecomposeGearCommandEnvelope.TryCreateCommand(
                Guid.Empty,
                GearMentorDecomposeGearCommandEnvelope
                    .AthensGearMentorNpcId,
                oneSelection,
                out _),
            "Decompose requires a client operation UUID");
        Check.True(
            !TryCreate(5000, oneSelection, out _),
            "Decompose rejects a non-physical Gear Mentor");
        Check.True(
            !GearMentorDecomposeGearCommandEnvelope.TryCreateCommand(
                ClientOperationId,
                GearMentorDecomposeGearCommandEnvelope
                    .AthensGearMentorNpcId,
                null,
                out _),
            "Decompose rejects a null selection collection");
    }

    private static void CheckSecureProvenanceAndFamily()
    {
        Check.Equal(
            9,
            (int)CommandFamily.GearMentorDecomposeGear,
            "Decompose command-family wire code");
        Check.Equal(
            (int)CommandIdentityStrength.ClientOperationId,
            (int)LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.GearMentorDecomposeGear),
            "Decompose uses explicit client-operation identity");
        Check.Equal(
            "gear_mentor_decompose_gear",
            CommandMetrics.FamilyCode(
                CommandFamily.GearMentorDecomposeGear),
            "Decompose has a bounded metric family");

        var command = ValidCommand();
        Check.Throws<ArgumentException>(
            () => GearMentorDecomposeGearCommandEnvelope.Create(
                Subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.LegacyTcp),
                DateTimeOffset.UtcNow,
                command),
            "legacy TCP cannot assert a Decompose client UUID");
        Check.Throws<ArgumentException>(
            () => GearMentorDecomposeGearCommandEnvelope.Create(
                Subject,
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.SecureCommand),
                DateTimeOffset.UtcNow,
                default),
            "Decompose cannot envelope a default command");
    }

    private static GearMentorDecomposeSelection Selection(
        int slot,
        string state) =>
        new(slot, state);

    private static bool TryCreate(
        int npcId,
        IReadOnlyList<GearMentorDecomposeSelection> selections,
        out GearMentorDecomposeGearCommand command) =>
        GearMentorDecomposeGearCommandEnvelope.TryCreateCommand(
            ClientOperationId,
            npcId,
            selections,
            out command);

    private static GearMentorDecomposeGearCommand ValidCommand()
    {
        Check.True(
            TryCreate(
                GearMentorDecomposeGearCommandEnvelope
                    .AthensGearMentorNpcId,
                [
                    Selection(12, "[10001,,,,,,2,5,1,1,0,0]"),
                    Selection(21, "[10002,,,,,,3,7,0,1,0,0]")
                ],
                out var command),
            "test Decompose command is valid");
        return command;
    }
}
