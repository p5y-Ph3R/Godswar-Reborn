using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct DeveloperBagClearCommand(
    Guid ClientOperationId);

internal static class DeveloperBagClearCommandEnvelope
{
    private const int OperationScopeBytes = 16;
    private static readonly byte[] CanonicalRequest = [1];

    public static bool TryCreateCommand(
        Guid clientOperationId,
        out DeveloperBagClearCommand command)
    {
        command = default;
        if (clientOperationId == Guid.Empty)
        {
            return false;
        }

        command = new DeveloperBagClearCommand(clientOperationId);
        return true;
    }

    public static CommandEnvelope<DeveloperBagClearCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        DeveloperBagClearCommand command)
    {
        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(command.ClientOperationId, operationScope);
        return CommandEnvelopeContract.Create(
            CommandFamily.DeveloperBagClear,
            LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.DeveloperBagClear),
            subject,
            connection,
            receivedAt,
            operationScope,
            CanonicalRequest,
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<DeveloperBagClearCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!TryCreateCommand(
                envelope.Command.ClientOperationId,
                out _))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(
            envelope.Command.ClientOperationId,
            operationScope);
        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.DeveloperBagClear,
            CommandIdentityStrength.ClientOperationId,
            operationScope,
            CanonicalRequest);
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
}
