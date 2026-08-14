using System.Buffers.Binary;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal readonly record struct PetToPetMergeCommand(
    PetCommandOperationIdentity Identity,
    long PrimaryPetId,
    long DeputyPetId,
    uint MaterialItemId,
    byte MaterialQuantity)
{
    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

/// <summary>
/// Exact fixed-point increments retained in the durable result so a replay
/// can reproduce native opcode 10269 without recalculating random values.
/// Savvy and rank values use the stock client's hundredths scale.
/// </summary>
internal readonly record struct PetToPetMergeDelta(
    int Agility,
    int Strength,
    int Accuracy,
    int Technique,
    int Wisdom,
    int Luck,
    ushort Rank)
{
    public bool IsValid =>
        Agility >= 0 &&
        Strength >= 0 &&
        Accuracy >= 0 &&
        Technique >= 0 &&
        Wisdom >= 0 &&
        Luck >= 0;
}

internal static class PetToPetMergeCommandEnvelope
{
    private const ushort CanonicalVersion = 1;
    public const uint StandardMaterialItemId = 10103;
    public const uint RestrictedMaterialItemId = 10097;
    public const byte MaximumMaterialQuantity = 5;

    public static CommandEnvelope<PetToPetMergeCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAtUtc,
        PetToPetMergeCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireSecureProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAtUtc, command);
    }

    public static CommandEnvelope<PetToPetMergeCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAtUtc,
        PetToPetMergeCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireRawLocalProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAtUtc, command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<PetToPetMergeCommand> envelope)
    {
        var command = envelope.Command;
        var hasNoMaterial =
            command.MaterialItemId == 0 && command.MaterialQuantity == 0;
        var hasSpiritMaterial =
            command.MaterialItemId is
                StandardMaterialItemId or RestrictedMaterialItemId &&
            command.MaterialQuantity is >= 1 and <= MaximumMaterialQuantity;
        if (!PetDurableCommandContract.IsValidIdentity(command.Identity) ||
            !PetDurableCommandContract.HasMatchingProvenance(
                command.Identity,
                envelope.Connection) ||
            command.PrimaryPetId is <= 0 or > int.MaxValue ||
            command.DeputyPetId is <= 0 or > int.MaxValue ||
            command.PrimaryPetId == command.DeputyPetId ||
            (!hasNoMaterial && !hasSpiritMaterial))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.PetToPetMerge,
            command.Identity.Strength,
            PetDurableCommandContract.OperationScope(command.Identity),
            Canonical(command));
    }

    private static CommandEnvelope<PetToPetMergeCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAtUtc,
        PetToPetMergeCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.PetToPetMerge,
            command.Identity.Strength,
            subject,
            connection,
            receivedAtUtc,
            PetDurableCommandContract.OperationScope(command.Identity),
            Canonical(command),
            command);

    private static byte[] Canonical(PetToPetMergeCommand command)
    {
        var bytes = new byte[
            sizeof(ushort) + sizeof(long) * 2 + sizeof(uint) + 1];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        BinaryPrimitives.WriteInt64BigEndian(
            bytes.AsSpan(sizeof(ushort)),
            command.PrimaryPetId);
        BinaryPrimitives.WriteInt64BigEndian(
            bytes.AsSpan(sizeof(ushort) + sizeof(long)),
            command.DeputyPetId);
        BinaryPrimitives.WriteUInt32BigEndian(
            bytes.AsSpan(sizeof(ushort) + sizeof(long) * 2),
            command.MaterialItemId);
        bytes[^1] = command.MaterialQuantity;
        return bytes;
    }
}
