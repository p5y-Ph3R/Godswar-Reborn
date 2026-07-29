using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct GearMentorTransformCrystalCommand(
    Guid ClientOperationId,
    int NpcId,
    int SelectedKitBagSlot,
    string ExpectedCompactItemState);

internal static class GearMentorTransformCrystalCommandEnvelope
{
    public const int SpartaGearMentorNpcId =
        GearMentorSingleMaterialCommandContract.SpartaGearMentorNpcId;
    public const int AthensGearMentorNpcId =
        GearMentorSingleMaterialCommandContract.AthensGearMentorNpcId;
    public const int MinimumKitBagSlot =
        GearMentorSingleMaterialCommandContract.MinimumKitBagSlot;
    public const int MaximumKitBagSlot =
        GearMentorSingleMaterialCommandContract.MaximumKitBagSlot;
    public const int MaximumExpectedStateUtf8Bytes =
        GearMentorSingleMaterialCommandContract
            .MaximumExpectedStateUtf8Bytes;
    public const ushort CanonicalRequestVersion =
        GearMentorSingleMaterialCommandContract.CanonicalRequestVersion;

    public static bool TryCreateCommand(
        Guid clientOperationId,
        int npcId,
        int selectedKitBagSlot,
        string? expectedCompactItemState,
        out GearMentorTransformCrystalCommand command)
    {
        command = default;
        if (!GearMentorSingleMaterialCommandContract.IsValidCommand(
                clientOperationId,
                npcId,
                selectedKitBagSlot,
                expectedCompactItemState))
        {
            return false;
        }

        command = new GearMentorTransformCrystalCommand(
            clientOperationId,
            npcId,
            selectedKitBagSlot,
            expectedCompactItemState!);
        return true;
    }

    public static CommandEnvelope<GearMentorTransformCrystalCommand>
        Create(
            CommandSubject subject,
            CommandConnectionCorrelation connection,
            DateTimeOffset receivedAt,
            GearMentorTransformCrystalCommand command) =>
        GearMentorSingleMaterialCommandContract.Create(
            CommandFamily.GearMentorTransformCrystal,
            subject,
            connection,
            receivedAt,
            command.ClientOperationId,
            command.NpcId,
            command.SelectedKitBagSlot,
            command.ExpectedCompactItemState,
            command);

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<GearMentorTransformCrystalCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return GearMentorSingleMaterialCommandContract.Validate(
            envelope,
            CommandFamily.GearMentorTransformCrystal,
            envelope.Command.ClientOperationId,
            envelope.Command.NpcId,
            envelope.Command.SelectedKitBagSlot,
            envelope.Command.ExpectedCompactItemState);
    }

    public static string CreateOperationId(
        CommandSubject subject,
        Guid clientOperationId) =>
        GearMentorSingleMaterialCommandContract.CreateOperationId(
            CommandFamily.GearMentorTransformCrystal,
            subject,
            clientOperationId);
}
