using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static class GearEnhancementCommandContractChecks
{
    private const string Gear = "[1000,,,,,,1,1,0,1,0,0,,,,,,0,,,,,,,,,,,,]";
    private const string Catalyst =
        "[9990,,,,,,1,1,0,2,0,0,,,,,,0,,,,,,,,,,,,]";
    private const string Stone =
        "[9930,,,,,,1,1,0,3,0,0,,,,,,0,,,,,,,,,,,,]";

    public static Task RunAsync()
    {
        var subject = new CommandSubject(7, 13);
        var connection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        foreach (var operation in Enum.GetValues<
                     GearEnhancementCommandOperation>())
        {
            var uuid = Guid.NewGuid();
            Check.True(
                GearEnhancementCommandEnvelope.TryCreateCommand(
                    uuid,
                    operation,
                    GearEnhancementCommandEnvelope
                        .SpartaGearMentorNpcId,
                    GearEnhancementCommandEnvelope.GearMentorDialogIndex,
                    Selection(
                        GearEnhancementCommandItemRole.Gear,
                        4,
                        Gear),
                    Selection(
                        GearEnhancementCommandItemRole.Catalyst,
                        5,
                        Catalyst),
                    Selection(
                        GearEnhancementCommandItemRole.AttributeStone,
                        6,
                        Stone),
                    out var command),
                $"{operation} creates a bounded command");
            var envelope = GearEnhancementCommandEnvelope.Create(
                subject,
                connection,
                DateTimeOffset.UtcNow,
                command);
            Check.Equal(
                (int)GearEnhancementCommandEnvelope.Family(operation),
                (int)envelope.Family,
                $"{operation} uses its strict command family");
            Check.Equal(
                (int)CommandEnvelopeValidation.Valid,
                (int)GearEnhancementCommandEnvelope.Validate(envelope),
                $"{operation} envelope validates");
            Check.True(
                string.Equals(
                    envelope.OperationId,
                    GearEnhancementCommandEnvelope.CreateOperationId(
                        subject,
                        operation,
                        uuid),
                    StringComparison.Ordinal),
                $"{operation} UUID replay identity is reproducible");
        }

        Check.True(
            !GearEnhancementCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                GearEnhancementCommandOperation.Add,
                GearEnhancementCommandEnvelope.SpartaGearMentorNpcId,
                GearEnhancementCommandEnvelope.GearMentorDialogIndex,
                Selection(
                    GearEnhancementCommandItemRole.Gear,
                    4,
                    Gear),
                Selection(
                    GearEnhancementCommandItemRole.Catalyst,
                    4,
                    Catalyst),
                Selection(
                    GearEnhancementCommandItemRole.AttributeStone,
                    6,
                    Stone),
                out _),
            "duplicate authoritative bag slots are rejected");
        Check.True(
            !GearEnhancementCommandEnvelope.TryCreateCommand(
                Guid.NewGuid(),
                GearEnhancementCommandOperation.Add,
                GearEnhancementCommandEnvelope.SpartaGearMentorNpcId,
                dialogIndex: 5,
                Selection(
                    GearEnhancementCommandItemRole.Gear,
                    4,
                    Gear),
                Selection(
                    GearEnhancementCommandItemRole.Catalyst,
                    5,
                    Catalyst),
                Selection(
                    GearEnhancementCommandItemRole.AttributeStone,
                    6,
                    Stone),
                out _),
            "unknown NPC dialogue endpoints are rejected");
        CheckNativeResultMatrix();
        return Task.CompletedTask;
    }

    private static void CheckNativeResultMatrix()
    {
        foreach (var operation in Enum.GetValues<
                     GearEnhancementCommandOperation>())
        {
            foreach (var status in Enum.GetValues<
                         GearEnhancementCommandResultStatus>())
            {
                if (!GearEnhancementNativeResults.IsReachable(
                        operation,
                        status))
                {
                    continue;
                }

                var expected = ExpectedNativeResult(operation, status);
                Check.Equal(
                    expected,
                    GearEnhancementNativeResults.GetResultSubId(
                        operation,
                        status),
                    $"{operation}/{status} exact native result");
                if (status ==
                    GearEnhancementCommandResultStatus.Succeeded)
                {
                    continue;
                }

                var receipt = new GearEnhancementExecutionReceipt(
                    characterId: 13,
                    operation,
                    GearEnhancementCommandEnvelope
                        .SpartaGearMentorNpcId,
                    GearEnhancementCommandEnvelope
                        .GearMentorDialogIndex,
                    status,
                    expected,
                    mutations: [],
                    inventoryRevision: 0,
                    auditReference:
                        $"contract:{operation}:{status}",
                    outboxEventId: null);
                var decoded = GearEnhancementPersistenceCodec.Decode(
                    GearEnhancementPersistenceCodec.Encode(receipt));
                Check.True(
                    decoded.Operation == operation &&
                    decoded.Status == status &&
                    decoded.NativeResultSubId == expected &&
                    decoded.Mutations.IsEmpty &&
                    decoded.OutboxEventId is null,
                    $"{operation}/{status} rejection round-trips without " +
                    "mutation evidence");
            }
        }
    }

    private static int ExpectedNativeResult(
        GearEnhancementCommandOperation operation,
        GearEnhancementCommandResultStatus status) =>
        status switch
        {
            GearEnhancementCommandResultStatus.Succeeded =>
                operation switch
                {
                    GearEnhancementCommandOperation.Enhance =>
                        GearEnhancementNativeResults
                            .EnhanceSucceededSubId,
                    GearEnhancementCommandOperation.Add =>
                        GearEnhancementNativeResults.AddSucceededSubId,
                    GearEnhancementCommandOperation.Delete =>
                        GearEnhancementNativeResults.DeleteSucceededSubId,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(operation))
                },
            GearEnhancementCommandResultStatus.StaleSelection =>
                GearEnhancementNativeResults.SelectedItemMissingSubId,
            GearEnhancementCommandResultStatus.InvalidEquipment or
                GearEnhancementCommandResultStatus.UnsupportedEquipment =>
                GearEnhancementNativeResults.MissingGearSubId,
            GearEnhancementCommandResultStatus.InvalidAttributeStone =>
                GearEnhancementNativeResults.MissingAttributeStoneSubId,
            GearEnhancementCommandResultStatus.InvalidCatalyst =>
                operation switch
                {
                    GearEnhancementCommandOperation.Enhance =>
                        GearEnhancementNativeResults.MissingQuartzSubId,
                    GearEnhancementCommandOperation.Add =>
                        GearEnhancementNativeResults
                            .MissingFlameSparkSubId,
                    GearEnhancementCommandOperation.Delete =>
                        GearEnhancementNativeResults
                            .MissingWaterGrainSubId,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(operation))
                },
            GearEnhancementCommandResultStatus.InsufficientMaterial =>
                operation switch
                {
                    GearEnhancementCommandOperation.Enhance =>
                        GearEnhancementNativeResults
                            .InsufficientEnhanceMaterialsSubId,
                    GearEnhancementCommandOperation.Add =>
                        GearEnhancementNativeResults
                            .InsufficientAddMaterialsSubId,
                    _ => GearEnhancementNativeResults
                        .InvalidSelectionSubId
                },
            GearEnhancementCommandResultStatus.AttributeNotAllowed =>
                GearEnhancementNativeResults.AttributeNotAllowedSubId,
            GearEnhancementCommandResultStatus.AttributeAlreadyPresent =>
                GearEnhancementNativeResults
                    .AttributeAlreadyPresentSubId,
            GearEnhancementCommandResultStatus.AttributeSlotsFull =>
                GearEnhancementNativeResults.AttributeSlotsFullSubId,
            GearEnhancementCommandResultStatus.AttributeMissing =>
                operation == GearEnhancementCommandOperation.Delete
                    ? GearEnhancementNativeResults
                        .MissingDeleteAttributeSubId
                    : GearEnhancementNativeResults
                        .MissingEnhanceAttributeSubId,
            GearEnhancementCommandResultStatus.AttributeAmbiguous =>
                operation == GearEnhancementCommandOperation.Delete
                    ? GearEnhancementNativeResults
                        .MissingDeleteAttributeSubId
                    : GearEnhancementNativeResults
                        .AttributeNotEnhanceableSubId,
            GearEnhancementCommandResultStatus.AttributeNotEnhanceable or
                GearEnhancementCommandResultStatus.AttributeMaximumLevel =>
                GearEnhancementNativeResults
                    .AttributeNotEnhanceableSubId,
            GearEnhancementCommandResultStatus.AttributeLevelMismatch or
                GearEnhancementCommandResultStatus.QuartzLevelMismatch =>
                GearEnhancementNativeResults.QuartzLevelMismatchSubId,
            _ => GearEnhancementNativeResults.InvalidSelectionSubId
        };

    private static GearEnhancementCommandSelection Selection(
        GearEnhancementCommandItemRole role,
        int slot,
        string state) =>
        new(role, slot, state);
}
