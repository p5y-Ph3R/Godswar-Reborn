using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct GearMentorCombineGemPiecesCommand(
    Guid ClientOperationId,
    int NpcId,
    int SelectedKitBagSlot,
    string ExpectedCompactItemState);

internal static class GearMentorCombineGemPiecesCommandEnvelope
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
        out GearMentorCombineGemPiecesCommand command)
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

        command = new GearMentorCombineGemPiecesCommand(
            clientOperationId,
            npcId,
            selectedKitBagSlot,
            expectedCompactItemState!);
        return true;
    }

    public static CommandEnvelope<GearMentorCombineGemPiecesCommand>
        Create(
            CommandSubject subject,
            CommandConnectionCorrelation connection,
            DateTimeOffset receivedAt,
            GearMentorCombineGemPiecesCommand command) =>
        GearMentorSingleMaterialCommandContract.Create(
            CommandFamily.GearMentorCombineGemPieces,
            subject,
            connection,
            receivedAt,
            command.ClientOperationId,
            command.NpcId,
            command.SelectedKitBagSlot,
            command.ExpectedCompactItemState,
            command);

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<GearMentorCombineGemPiecesCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return GearMentorSingleMaterialCommandContract.Validate(
            envelope,
            CommandFamily.GearMentorCombineGemPieces,
            envelope.Command.ClientOperationId,
            envelope.Command.NpcId,
            envelope.Command.SelectedKitBagSlot,
            envelope.Command.ExpectedCompactItemState);
    }

    public static string CreateOperationId(
        CommandSubject subject,
        Guid clientOperationId) =>
        GearMentorSingleMaterialCommandContract.CreateOperationId(
            CommandFamily.GearMentorCombineGemPieces,
            subject,
            clientOperationId);
}
