using System.Buffers.Binary;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Zodiac;

internal readonly record struct ZodiacSkillGridUpgradeCommand(
    Guid ClientOperationId,
    int GridIndex);

internal static class ZodiacSkillGridUpgradeCommandEnvelope
{
    public const int MinimumGridIndex = 0;
    public const int MaximumGridIndex = 15;
    public const byte MinimumActiveLevel = 1;
    public const byte MaximumGridLevel = 50;
    public const int NoSelectedSkillId = -1;
    public const ushort CanonicalRequestVersion = 1;

    private const int OperationScopeBytes = 16;
    private const int CanonicalRequestBytes =
        sizeof(ushort) + sizeof(byte);

    public static bool TryCreateCommand(
        Guid clientOperationId,
        int gridIndex,
        out ZodiacSkillGridUpgradeCommand command)
    {
        command = default;
        if (clientOperationId == Guid.Empty ||
            gridIndex is < MinimumGridIndex or > MaximumGridIndex)
        {
            return false;
        }

        command = new ZodiacSkillGridUpgradeCommand(
            clientOperationId,
            gridIndex);
        return true;
    }

    public static CommandEnvelope<ZodiacSkillGridUpgradeCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        ZodiacSkillGridUpgradeCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The Zodiac skill-grid upgrade command is invalid.",
                nameof(command));
        }
        if (!IsTrustedTransport(connection.Transport))
        {
            throw new ArgumentException(
                "Zodiac skill-grid upgrade requires authenticated secure " +
                "command provenance.",
                nameof(connection));
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(command.ClientOperationId, operationScope);
        Span<byte> canonicalRequest =
            stackalloc byte[CanonicalRequestBytes];
        WriteCanonicalRequest(command, canonicalRequest);
        return CommandEnvelopeContract.Create(
            CommandFamily.ZodiacSkillGridUpgrade,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            operationScope,
            canonicalRequest,
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<ZodiacSkillGridUpgradeCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsValidCommand(envelope.Command))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }
        if (!IsTrustedTransport(envelope.Connection.Transport))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(
            envelope.Command.ClientOperationId,
            operationScope);
        Span<byte> canonicalRequest =
            stackalloc byte[CanonicalRequestBytes];
        WriteCanonicalRequest(envelope.Command, canonicalRequest);
        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.ZodiacSkillGridUpgrade,
            CommandIdentityStrength.ClientOperationId,
            operationScope,
            canonicalRequest);
    }

    public static string CreateOperationId(
        CommandSubject subject,
        Guid clientOperationId)
    {
        if (clientOperationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty operation UUID is required.",
                nameof(clientOperationId));
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(clientOperationId, operationScope);
        return CommandEnvelopeContract.DeriveOperationId(
            CommandFamily.ZodiacSkillGridUpgrade,
            subject,
            operationScope);
    }

    private static bool IsValidCommand(
        ZodiacSkillGridUpgradeCommand command) =>
        command.ClientOperationId != Guid.Empty &&
        command.GridIndex is >= MinimumGridIndex and <= MaximumGridIndex;

    private static bool IsTrustedTransport(
        CommandTransportKind transport) =>
        transport is CommandTransportKind.SecureTlsLegacy or
            CommandTransportKind.SecureCommand;

    private static void WriteCanonicalRequest(
        ZodiacSkillGridUpgradeCommand command,
        Span<byte> destination)
    {
        if (destination.Length != CanonicalRequestBytes)
        {
            throw new ArgumentException(
                "The canonical request buffer has an invalid size.",
                nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(
            destination,
            CanonicalRequestVersion);
        destination[^1] = checked((byte)command.GridIndex);
    }

    private static void WriteOperationScope(
        Guid clientOperationId,
        Span<byte> destination)
    {
        if (!clientOperationId.TryWriteBytes(
                destination,
                bigEndian: true,
                out var bytesWritten) ||
            bytesWritten != OperationScopeBytes)
        {
            throw new ArgumentException(
                "The operation UUID could not be encoded.",
                nameof(clientOperationId));
        }
    }
}
