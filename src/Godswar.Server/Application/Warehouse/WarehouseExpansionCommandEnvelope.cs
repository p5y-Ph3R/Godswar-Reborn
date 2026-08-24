using System.Buffers.Binary;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Warehouse;

internal static class WarehouseExpansionCommandEnvelope
{
    public const int DialogIndex = 106;
    public const int ActionSubId = 100;
    public const ushort CanonicalRequestVersion = 1;

    public static bool TryCreateCommand(
        WarehouseOperationIdentity identity,
        int realmId,
        int npcId,
        int dialogIndex,
        int actionSubId,
        int currentCapacity,
        WarehouseExpansionPolicySnapshot policy,
        out WarehouseExpansionCommand command)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        var targetCapacity = WarehouseCapacityPolicy.IsValidCapacity(
                currentCapacity) &&
            currentCapacity <= policy.MaximumCapacity
                ? policy.NextLevelForCapacity(currentCapacity).Capacity
                : 0;
        command = new(
            identity,
            realmId,
            npcId,
            dialogIndex,
            actionSubId,
            currentCapacity,
            targetCapacity,
            policy.Revision,
            policy.Sha256);
        if (IsValid(command))
        {
            return true;
        }

        command = default;
        return false;
    }

    public static CommandEnvelope<WarehouseExpansionCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        WarehouseExpansionCommand command)
    {
        if (!IsValid(command) ||
            !WarehouseCommandIdentityRules.Matches(
                command.Identity,
                connection))
        {
            throw new ArgumentException(
                "The warehouse expansion command is invalid.",
                nameof(command));
        }

        return CommandEnvelopeContract.Create(
            CommandFamily.WarehouseExpansion,
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            WarehouseCommandIdentityRules.CreateScope(command.Identity),
            CreateCanonicalRequest(command),
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<WarehouseExpansionCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsValid(envelope.Command))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }
        if (!WarehouseCommandIdentityRules.Matches(
                envelope.Command.Identity,
                envelope.Connection))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.WarehouseExpansion,
            envelope.Command.Identity.Strength,
            WarehouseCommandIdentityRules.CreateScope(
                envelope.Command.Identity),
            CreateCanonicalRequest(envelope.Command));
    }

    private static bool IsValid(WarehouseExpansionCommand command) =>
        (command.Identity.IsSecureClient ||
         command.Identity.IsRawLocalServer) &&
        command.RealmId > 0 &&
        command.NpcId > 0 &&
        command.DialogIndex == DialogIndex &&
        command.ActionSubId == ActionSubId &&
        WarehouseCapacityPolicy.IsValidCapacity(
            command.ExpectedCapacity) &&
        WarehouseCapacityPolicy.IsValidCapacity(command.TargetCapacity) &&
        (command.TargetCapacity == command.ExpectedCapacity ||
         command.ExpectedCapacity <
             WarehouseCapacityPolicy.MaximumSupportedCapacity &&
         command.TargetCapacity == WarehouseCapacityPolicy.NextCapacity(
             command.ExpectedCapacity)) &&
        command.PolicyRevision > 0 &&
        command.PolicySha256 is { Length: 64 } sha &&
        sha.All(Uri.IsHexDigit);

    private static byte[] CreateCanonicalRequest(
        WarehouseExpansionCommand command)
    {
        var policyHash = Convert.FromHexString(command.PolicySha256);
        var bytes = new byte[
            sizeof(ushort) + sizeof(int) * 6 + sizeof(long) +
            policyHash.Length];
        BinaryPrimitives.WriteUInt16BigEndian(
            bytes,
            CanonicalRequestVersion);
        var offset = sizeof(ushort);
        WriteInt(command.RealmId);
        // Both capital managers expose the same per-character mutation.
        // Validate the resolved route before this boundary, then normalize
        // the endpoint so a lost-result retry can replay after map transfer.
        WriteInt(0);
        WriteInt(command.DialogIndex);
        WriteInt(command.ActionSubId);
        WriteInt(command.ExpectedCapacity);
        WriteInt(command.TargetCapacity);
        BinaryPrimitives.WriteInt64BigEndian(
            bytes.AsSpan(offset, sizeof(long)),
            command.PolicyRevision);
        offset += sizeof(long);
        policyHash.CopyTo(bytes.AsSpan(offset));
        return bytes;

        void WriteInt(int value)
        {
            BinaryPrimitives.WriteInt32BigEndian(
                bytes.AsSpan(offset, sizeof(int)),
                value);
            offset += sizeof(int);
        }
    }
}
