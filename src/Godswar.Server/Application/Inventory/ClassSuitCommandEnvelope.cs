using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal enum ClassSuitCommandOperation : short
{
    ExchangeTierI = 100,
    AddAttribute = 101,
    DeleteAttribute = 102,
    ConvertToCommon = 104,
    UpgradeTierII = 105,
    UpgradeTierIII = 106,
    UpgradeTierIV = 108
}

internal readonly record struct ClassSuitOperationIdentity(
    CommandIdentityStrength Strength,
    Guid OperationId,
    Guid RawLocalConnectionId)
{
    public static ClassSuitOperationIdentity SecureClient(Guid value) =>
        new(CommandIdentityStrength.ClientOperationId, value, Guid.Empty);

    public static ClassSuitOperationIdentity RawLocalServer(
        Guid value,
        Guid connectionId) =>
        new(CommandIdentityStrength.ServerOperationId, value, connectionId);

    public bool IsSecureClient =>
        Strength == CommandIdentityStrength.ClientOperationId &&
        OperationId != Guid.Empty &&
        RawLocalConnectionId == Guid.Empty;

    public bool IsRawLocalServer =>
        Strength == CommandIdentityStrength.ServerOperationId &&
        OperationId != Guid.Empty &&
        RawLocalConnectionId != Guid.Empty;
}

internal readonly record struct ClassSuitCommandSelection(
    int KitBagSlot,
    string ExpectedCompactItemState);

internal readonly record struct ClassSuitCommand(
    ClassSuitOperationIdentity Identity,
    ClassSuitCommandOperation Operation,
    int NpcId,
    int DialogIndex,
    ClassSuitCommandSelection Gear,
    ClassSuitCommandSelection? PrimaryMaterial,
    ClassSuitCommandSelection? SecondaryMaterial);

internal static class ClassSuitCommandEnvelope
{
    public const int SpartaNpcId = 5067;
    public const int AthensNpcId = 5209;
    public const int DialogIndex = 37;
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int MaximumStateUtf8Bytes = 512;
    public const ushort CanonicalVersion = 1;

    private const int OperationScopeBytes = 33;
    private const int CanonicalBytes =
        sizeof(ushort) + sizeof(short) + sizeof(int) + sizeof(int) +
        (sizeof(short) * 3) + (SHA256.HashSizeInBytes * 3);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryCreateCommand(
        ClassSuitOperationIdentity identity,
        ClassSuitCommandOperation operation,
        int npcId,
        int dialogIndex,
        ClassSuitCommandSelection gear,
        ClassSuitCommandSelection? primaryMaterial,
        ClassSuitCommandSelection? secondaryMaterial,
        out ClassSuitCommand command)
    {
        command = new ClassSuitCommand(
            identity,
            operation,
            npcId,
            dialogIndex,
            gear,
            primaryMaterial,
            secondaryMaterial);
        if (IsValidCommand(command))
        {
            return true;
        }

        command = default;
        return false;
    }

    public static CommandEnvelope<ClassSuitCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        ClassSuitCommand command)
    {
        if (!IsValidCommand(command) ||
            !HasMatchingProvenance(command.Identity, connection))
        {
            throw new ArgumentException(
                "The Class Suit command or its transport provenance is invalid.",
                nameof(command));
        }

        return CommandEnvelopeContract.Create(
            Family(command.Operation),
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            CreateOperationScope(command.Identity),
            CreateCanonicalRequest(command),
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<ClassSuitCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsValidCommand(envelope.Command))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }
        if (!HasMatchingProvenance(
                envelope.Command.Identity,
                envelope.Connection))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            Family(envelope.Command.Operation),
            envelope.Command.Identity.Strength,
            CreateOperationScope(envelope.Command.Identity),
            CreateCanonicalRequest(envelope.Command));
    }

    public static string CreateOperationId(
        CommandSubject subject,
        ClassSuitCommandOperation operation,
        ClassSuitOperationIdentity identity) =>
        CommandEnvelopeContract.DeriveOperationId(
            Family(operation),
            subject,
            CreateOperationScope(identity));

    public static CommandFamily Family(ClassSuitCommandOperation operation) =>
        operation switch
        {
            ClassSuitCommandOperation.ExchangeTierI =>
                CommandFamily.ClassSuitExchangeTierI,
            ClassSuitCommandOperation.ConvertToCommon =>
                CommandFamily.ClassSuitConvertToCommon,
            ClassSuitCommandOperation.UpgradeTierII =>
                CommandFamily.ClassSuitUpgradeTierII,
            ClassSuitCommandOperation.UpgradeTierIII =>
                CommandFamily.ClassSuitUpgradeTierIII,
            ClassSuitCommandOperation.UpgradeTierIV =>
                CommandFamily.ClassSuitUpgradeTierIV,
            ClassSuitCommandOperation.AddAttribute =>
                CommandFamily.ClassSuitAddAttribute,
            ClassSuitCommandOperation.DeleteAttribute =>
                CommandFamily.ClassSuitDeleteAttribute,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    public static bool IsEndpoint(int npcId, int dialogIndex) =>
        dialogIndex == DialogIndex &&
        npcId is SpartaNpcId or AthensNpcId;

    private static bool IsValidCommand(ClassSuitCommand command)
    {
        if (!Enum.IsDefined(command.Operation) ||
            !IsValidIdentity(command.Identity) ||
            !IsEndpoint(command.NpcId, command.DialogIndex) ||
            !IsSelection(command.Gear))
        {
            return false;
        }

        var expectedMaterialCount = command.Operation switch
        {
            ClassSuitCommandOperation.ConvertToCommon => 0,
            ClassSuitCommandOperation.AddAttribute => 2,
            _ => 1
        };
        var actualMaterialCount =
            (command.PrimaryMaterial.HasValue ? 1 : 0) +
            (command.SecondaryMaterial.HasValue ? 1 : 0);
        if (actualMaterialCount != expectedMaterialCount ||
            command.SecondaryMaterial.HasValue &&
            !command.PrimaryMaterial.HasValue)
        {
            return false;
        }

        var selections = EnumerateSelections(command).ToArray();
        return selections.All(IsSelection) &&
            selections.Select(static value => value.KitBagSlot)
                .Distinct().Count() == selections.Length;
    }

    private static IEnumerable<ClassSuitCommandSelection>
        EnumerateSelections(ClassSuitCommand command)
    {
        yield return command.Gear;
        if (command.PrimaryMaterial is { } primary)
        {
            yield return primary;
        }
        if (command.SecondaryMaterial is { } secondary)
        {
            yield return secondary;
        }
    }

    private static bool IsSelection(ClassSuitCommandSelection selection)
    {
        if (selection.KitBagSlot is
                < MinimumKitBagSlot or > MaximumKitBagSlot ||
            string.IsNullOrWhiteSpace(selection.ExpectedCompactItemState) ||
            selection.ExpectedCompactItemState.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(
                selection.ExpectedCompactItemState) <=
                MaximumStateUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsValidIdentity(ClassSuitOperationIdentity identity) =>
        identity.IsSecureClient || identity.IsRawLocalServer;

    private static bool HasMatchingProvenance(
        ClassSuitOperationIdentity identity,
        CommandConnectionCorrelation connection) =>
        identity.IsSecureClient &&
        connection.Transport is CommandTransportKind.SecureTlsLegacy or
            CommandTransportKind.SecureCommand ||
        identity.IsRawLocalServer &&
        connection.Transport == CommandTransportKind.LegacyTcp &&
        identity.RawLocalConnectionId == connection.ConnectionId;

    private static byte[] CreateOperationScope(
        ClassSuitOperationIdentity identity)
    {
        if (!IsValidIdentity(identity))
        {
            throw new ArgumentException("Invalid Class Suit identity.");
        }

        var bytes = new byte[OperationScopeBytes];
        bytes[0] = (byte)identity.Strength;
        identity.OperationId.TryWriteBytes(bytes.AsSpan(1, 16));
        identity.RawLocalConnectionId.TryWriteBytes(bytes.AsSpan(17, 16));
        return bytes;
    }

    private static byte[] CreateCanonicalRequest(ClassSuitCommand command)
    {
        var canonical = new byte[CanonicalBytes];
        var span = canonical.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, CanonicalVersion);
        BinaryPrimitives.WriteInt16BigEndian(
            span[2..],
            (short)command.Operation);
        BinaryPrimitives.WriteInt32BigEndian(span[4..], command.NpcId);
        BinaryPrimitives.WriteInt32BigEndian(span[8..], command.DialogIndex);
        var offset = 12;
        foreach (var selection in new ClassSuitCommandSelection?[]
                 {
                     command.Gear,
                     command.PrimaryMaterial,
                     command.SecondaryMaterial
                 })
        {
            BinaryPrimitives.WriteInt16BigEndian(
                span[offset..],
                selection.HasValue
                    ? checked((short)selection.Value.KitBagSlot)
                    : (short)-1);
            offset += sizeof(short);
            if (selection.HasValue)
            {
                SHA256.HashData(
                    StrictUtf8.GetBytes(
                        selection.Value.ExpectedCompactItemState),
                    span.Slice(offset, SHA256.HashSizeInBytes));
            }
            offset += SHA256.HashSizeInBytes;
        }

        return canonical;
    }
}
