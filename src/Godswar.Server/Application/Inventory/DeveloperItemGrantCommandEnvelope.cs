using System.Buffers.Binary;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

/// <summary>
/// One allowlisted developer item grant. The authenticated subject and
/// allowlist decision remain outside this transport-neutral value.
/// </summary>
internal readonly record struct DeveloperItemGrantCommand(
    uint ItemId,
    int Quantity,
    Guid ClientOperationId);

internal static class DeveloperItemGrantCommandEnvelope
{
    public const uint MinimumItemId = 1;
    public const uint MaximumItemId = int.MaxValue;
    public const int MinimumQuantity = 1;
    public const int MaximumQuantity = 999;

    private const int OperationScopeBytes = 16;
    private const int CanonicalRequestBytes = sizeof(uint) + sizeof(int);

    public static bool TryCreateCommand(
        uint itemId,
        int quantity,
        Guid clientOperationId,
        out DeveloperItemGrantCommand command)
    {
        command = default;
        if (itemId is < MinimumItemId or > MaximumItemId ||
            quantity is < MinimumQuantity or > MaximumQuantity ||
            clientOperationId == Guid.Empty)
        {
            return false;
        }

        command = new DeveloperItemGrantCommand(
            itemId,
            quantity,
            clientOperationId);
        return true;
    }

    public static CommandEnvelope<DeveloperItemGrantCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        DeveloperItemGrantCommand command)
    {
        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(command.ClientOperationId, operationScope);
        Span<byte> canonicalRequest = stackalloc byte[CanonicalRequestBytes];
        WriteCanonicalRequest(command, canonicalRequest);

        return CommandEnvelopeContract.Create(
            CommandFamily.DeveloperItemGrant,
            LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.DeveloperItemGrant),
            subject,
            connection,
            receivedAt,
            operationScope,
            canonicalRequest,
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<DeveloperItemGrantCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!TryCreateCommand(
                envelope.Command.ItemId,
                envelope.Command.Quantity,
                envelope.Command.ClientOperationId,
                out _))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(
            envelope.Command.ClientOperationId,
            operationScope);
        Span<byte> canonicalRequest = stackalloc byte[CanonicalRequestBytes];
        WriteCanonicalRequest(envelope.Command, canonicalRequest);
        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.DeveloperItemGrant,
            CommandIdentityStrength.ClientOperationId,
            operationScope,
            canonicalRequest);
    }

    private static void WriteOperationScope(
        Guid operationId,
        Span<byte> destination)
    {
        if (!operationId.TryWriteBytes(
                destination,
                bigEndian: true,
                out var bytesWritten) ||
            bytesWritten != OperationScopeBytes)
        {
            throw new ArgumentException(
                "The operation ID could not be encoded.",
                nameof(operationId));
        }
    }

    private static void WriteCanonicalRequest(
        DeveloperItemGrantCommand command,
        Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(
            destination[..sizeof(uint)],
            command.ItemId);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[sizeof(uint)..],
            command.Quantity);
    }
}
