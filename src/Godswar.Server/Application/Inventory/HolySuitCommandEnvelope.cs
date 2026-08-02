using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal enum HolySuitCommandOperation : byte
{
    StoreExperience = 1,
    TransferExperience = 2,
    ConsumeWare = 3,
    TransformExperience = 4
}

internal readonly record struct HolySuitOperationIdentity(
    CommandIdentityStrength Strength,
    Guid OperationId,
    Guid RawLocalConnectionId)
{
    public static HolySuitOperationIdentity SecureClient(
        Guid clientOperationId) =>
        new(
            CommandIdentityStrength.ClientOperationId,
            clientOperationId,
            Guid.Empty);

    public static HolySuitOperationIdentity RawLocalServer(
        Guid serverOperationId,
        Guid localConnectionId) =>
        new(
            CommandIdentityStrength.ServerOperationId,
            serverOperationId,
            localConnectionId);

    public bool IsSecureClient =>
        Strength == CommandIdentityStrength.ClientOperationId &&
        OperationId != Guid.Empty &&
        RawLocalConnectionId == Guid.Empty;

    public bool IsRawLocalServer =>
        Strength == CommandIdentityStrength.ServerOperationId &&
        OperationId != Guid.Empty &&
        RawLocalConnectionId != Guid.Empty;
}

internal readonly record struct HolySuitCommand(
    HolySuitOperationIdentity Identity,
    HolySuitCommandOperation Operation,
    int NpcId,
    int DialogIndex,
    int PrimaryKitBagSlot,
    string ExpectedPrimaryCompactItemState,
    int SecondaryKitBagSlot,
    string ExpectedSecondaryCompactItemState,
    long ExperienceToStore,
    int PrismsToCreate);

internal static class HolySuitCommandEnvelope
{
    public const int SpartaNpcId = 5082;
    public const int AthensNpcId = 5224;
    public const int DialogIndex = 29;
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int NoKitBagSlot = -1;
    // This is the legacy packet's unsigned 32-bit encoding boundary. The
    // lower content-authored per-operation and daily limits remain enforced
    // by the authoritative executor.
    public const long MaximumExperienceToStore = uint.MaxValue;
    public const int ExperiencePerPrism = 100_000_000;
    public const int MaximumPrismsToCreate = 99;
    public const int MaximumCompactItemStateUtf8Bytes = 512;
    public const int MaximumCombinedStateUtf8Bytes = 900;
    public const ushort CanonicalRequestVersion = 1;

    private const int CanonicalForgerEndpoint = 1;
    private const byte PrimaryStateRole = 1;
    private const byte SecondaryStateRole = 2;
    private const int StateDigestBytes = 32;
    private const int CanonicalRequestBytes =
        sizeof(ushort) + sizeof(byte) + sizeof(int) + sizeof(int) +
        sizeof(short) + sizeof(short) + sizeof(long) + sizeof(int) +
        StateDigestBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryCreateCommand(
        HolySuitOperationIdentity identity,
        HolySuitCommandOperation operation,
        int npcId,
        int dialogIndex,
        int primaryKitBagSlot,
        string? expectedPrimaryCompactItemState,
        int secondaryKitBagSlot,
        string? expectedSecondaryCompactItemState,
        long experienceToStore,
        int prismsToCreate,
        out HolySuitCommand command)
    {
        command = new HolySuitCommand(
            identity,
            operation,
            npcId,
            dialogIndex,
            primaryKitBagSlot,
            expectedPrimaryCompactItemState ?? string.Empty,
            secondaryKitBagSlot,
            expectedSecondaryCompactItemState ?? string.Empty,
            experienceToStore,
            prismsToCreate);
        if (IsValidCommand(command))
        {
            return true;
        }

        command = default;
        return false;
    }

    public static CommandEnvelope<HolySuitCommand> CreateSecure(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        HolySuitCommand command)
    {
        if (!command.Identity.IsSecureClient ||
            !IsSecureTransport(connection.Transport))
        {
            throw new ArgumentException(
                "Secure Holy Suit commands require authenticated client " +
                "operation provenance.",
                nameof(connection));
        }

        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelope<HolySuitCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        HolySuitCommand command)
    {
        if (!command.Identity.IsRawLocalServer ||
            connection.Transport != CommandTransportKind.LegacyTcp ||
            command.Identity.RawLocalConnectionId !=
                connection.ConnectionId)
        {
            throw new ArgumentException(
                "Raw-local Holy Suit commands require a server operation " +
                "identity scoped to the exact legacy connection.",
                nameof(connection));
        }

        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<HolySuitCommand> envelope)
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

        var operationScope = CreateOperationScope(
            envelope.Command.Identity);
        return CommandEnvelopeContract.Validate(
            envelope,
            Family(envelope.Command.Operation),
            envelope.Command.Identity.Strength,
            operationScope,
            CreateCanonicalRequest(envelope.Command));
    }

    public static string CreateOperationId(
        CommandSubject subject,
        HolySuitCommandOperation operation,
        HolySuitOperationIdentity identity)
    {
        if (!Enum.IsDefined(operation) || !IsValidIdentity(identity))
        {
            throw new ArgumentException(
                "A supported operation and bounded identity are required.");
        }

        return CommandEnvelopeContract.DeriveOperationId(
            Family(operation),
            subject,
            CreateOperationScope(identity));
    }

    public static CommandFamily Family(
        HolySuitCommandOperation operation) =>
        operation switch
        {
            HolySuitCommandOperation.StoreExperience =>
                CommandFamily.HolySuitStoreExperience,
            HolySuitCommandOperation.TransferExperience =>
                CommandFamily.HolySuitTransferExperience,
            HolySuitCommandOperation.ConsumeWare =>
                CommandFamily.HolySuitConsumeWare,
            HolySuitCommandOperation.TransformExperience =>
                CommandFamily.HolySuitTransformExperience,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    public static bool IsEndpoint(int npcId, int dialogIndex) =>
        dialogIndex == DialogIndex &&
        npcId is SpartaNpcId or AthensNpcId;

    public static bool AreEquivalentEndpoints(
        int firstNpcId,
        int firstDialogIndex,
        int secondNpcId,
        int secondDialogIndex) =>
        IsEndpoint(firstNpcId, firstDialogIndex) &&
        IsEndpoint(secondNpcId, secondDialogIndex);

    private static CommandEnvelope<HolySuitCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        HolySuitCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The Holy Suit command is invalid.",
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

    private static bool IsValidCommand(HolySuitCommand command)
    {
        if (!IsValidIdentity(command.Identity) ||
            !Enum.IsDefined(command.Operation) ||
            !IsEndpoint(command.NpcId, command.DialogIndex) ||
            !TryGetStateBytes(
                command.ExpectedPrimaryCompactItemState,
                out var primaryState) ||
            !TryGetStateBytes(
                command.ExpectedSecondaryCompactItemState,
                out var secondaryState) ||
            primaryState.Length + secondaryState.Length >
                MaximumCombinedStateUtf8Bytes)
        {
            return false;
        }

        return command.Operation switch
        {
            HolySuitCommandOperation.StoreExperience =>
                IsKitBagSlot(command.PrimaryKitBagSlot) &&
                HasNoSecondaryItem(command) &&
                command.ExperienceToStore is
                    >= 0 and <= MaximumExperienceToStore &&
                command.PrismsToCreate == 0,
            HolySuitCommandOperation.TransferExperience or
                HolySuitCommandOperation.ConsumeWare =>
                IsKitBagSlot(command.PrimaryKitBagSlot) &&
                IsKitBagSlot(command.SecondaryKitBagSlot) &&
                command.PrimaryKitBagSlot !=
                    command.SecondaryKitBagSlot &&
                command.ExperienceToStore == 0 &&
                command.PrismsToCreate == 0,
            HolySuitCommandOperation.TransformExperience =>
                HasNoPrimaryItem(command) &&
                HasNoSecondaryItem(command) &&
                command.ExperienceToStore == 0 &&
                command.PrismsToCreate is
                    > 0 and <= MaximumPrismsToCreate,
            _ => false
        };
    }

    private static bool IsValidIdentity(
        HolySuitOperationIdentity identity) =>
        identity.IsSecureClient || identity.IsRawLocalServer;

    private static bool HasMatchingProvenance(
        HolySuitOperationIdentity identity,
        CommandConnectionCorrelation connection) =>
        identity.IsSecureClient
            ? IsSecureTransport(connection.Transport)
            : identity.IsRawLocalServer &&
              connection.Transport == CommandTransportKind.LegacyTcp &&
              identity.RawLocalConnectionId == connection.ConnectionId;

    private static bool IsSecureTransport(
        CommandTransportKind transport) =>
        transport is
            CommandTransportKind.SecureTlsLegacy or
            CommandTransportKind.SecureCommand;

    private static bool HasNoPrimaryItem(HolySuitCommand command) =>
        command.PrimaryKitBagSlot == NoKitBagSlot &&
        command.ExpectedPrimaryCompactItemState == "[]";

    private static bool HasNoSecondaryItem(HolySuitCommand command) =>
        command.SecondaryKitBagSlot == NoKitBagSlot &&
        command.ExpectedSecondaryCompactItemState == "[]";

    private static bool IsKitBagSlot(int slot) =>
        slot is >= MinimumKitBagSlot and <= MaximumKitBagSlot;

    private static byte[] CreateCanonicalRequest(
        HolySuitCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The Holy Suit command is invalid.",
                nameof(command));
        }

        var primaryState = StrictUtf8.GetBytes(
            command.ExpectedPrimaryCompactItemState);
        var secondaryState = StrictUtf8.GetBytes(
            command.ExpectedSecondaryCompactItemState);
        var canonical = new byte[CanonicalRequestBytes];
        var destination = canonical.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(
            destination,
            CanonicalRequestVersion);
        var offset = sizeof(ushort);
        destination[offset++] = (byte)command.Operation;
        BinaryPrimitives.WriteInt32BigEndian(
            destination[offset..],
            CanonicalForgerEndpoint);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[offset..],
            DialogIndex);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt16BigEndian(
            destination[offset..],
            checked((short)command.PrimaryKitBagSlot));
        offset += sizeof(short);
        BinaryPrimitives.WriteInt16BigEndian(
            destination[offset..],
            checked((short)command.SecondaryKitBagSlot));
        offset += sizeof(short);
        BinaryPrimitives.WriteInt64BigEndian(
            destination[offset..],
            command.ExperienceToStore);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[offset..],
            command.PrismsToCreate);
        offset += sizeof(int);
        ComputeStateDigest(primaryState, secondaryState)
            .CopyTo(destination[offset..]);
        return canonical;
    }

    private static byte[] ComputeStateDigest(
        byte[] primaryState,
        byte[] secondaryState)
    {
        var tagged = new byte[
            sizeof(byte) + sizeof(ushort) + primaryState.Length +
            sizeof(byte) + sizeof(ushort) + secondaryState.Length];
        var destination = tagged.AsSpan();
        var offset = 0;
        destination[offset++] = PrimaryStateRole;
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[offset..],
            checked((ushort)primaryState.Length));
        offset += sizeof(ushort);
        primaryState.CopyTo(destination[offset..]);
        offset += primaryState.Length;
        destination[offset++] = SecondaryStateRole;
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[offset..],
            checked((ushort)secondaryState.Length));
        offset += sizeof(ushort);
        secondaryState.CopyTo(destination[offset..]);
        return SHA256.HashData(tagged);
    }

    private static byte[] CreateOperationScope(
        HolySuitOperationIdentity identity)
    {
        if (!IsValidIdentity(identity))
        {
            throw new ArgumentException(
                "The Holy Suit operation identity is invalid.",
                nameof(identity));
        }

        var rawLocal = identity.IsRawLocalServer;
        var scope = new byte[rawLocal ? 33 : 17];
        scope[0] = (byte)identity.Strength;
        WriteGuid(identity.OperationId, scope.AsSpan(1, 16));
        if (rawLocal)
        {
            WriteGuid(
                identity.RawLocalConnectionId,
                scope.AsSpan(17, 16));
        }
        return scope;
    }

    private static bool TryGetStateBytes(
        string? value,
        out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            value[0] != '[' ||
            value[^1] != ']')
        {
            return false;
        }

        try
        {
            bytes = StrictUtf8.GetBytes(value);
            return bytes.Length <= MaximumCompactItemStateUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static void WriteGuid(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(
                destination,
                bigEndian: true,
                out var bytesWritten) ||
            bytesWritten != 16)
        {
            throw new ArgumentException(
                "The operation UUID could not be encoded.",
                nameof(value));
        }
    }
}
