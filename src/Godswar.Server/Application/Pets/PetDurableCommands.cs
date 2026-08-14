using System.Buffers.Binary;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal readonly record struct BagItemActivationCommand(
    PetCommandOperationIdentity Identity,
    int KitBagSlot,
    BagItemActivationExecutionConstraint ExecutionConstraint =
        BagItemActivationExecutionConstraint.None)
{
    public BagItemActivationCommand(
        Guid clientOperationId,
        int kitBagSlot,
        BagItemActivationExecutionConstraint executionConstraint =
            BagItemActivationExecutionConstraint.None)
        : this(
            PetCommandOperationIdentity.SecureClient(clientOperationId),
            kitBagSlot,
            executionConstraint)
    {
    }

    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

/// <summary>
/// Server-observed execution state. This is deliberately excluded from the
/// client request hash: a retry must replay the first durable decision even
/// when the transient runtime observation has since changed.
/// </summary>
internal enum BagItemActivationExecutionConstraint : byte
{
    None = 0,
    RideRuntimeBlocked = 1
}

internal readonly record struct PetLevelUpgradeCommand(
    PetCommandOperationIdentity Identity,
    long PetId)
{
    public PetLevelUpgradeCommand(Guid clientOperationId, long petId)
        : this(
            PetCommandOperationIdentity.SecureClient(clientOperationId),
            petId)
    {
    }

    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal enum PetPresenceCommandOperation : byte
{
    Take = 0,
    CallOut = 1,
    Recall = 2
}

internal readonly record struct PetPresenceTransitionCommand(
    PetCommandOperationIdentity Identity,
    long PetId,
    PetPresenceCommandOperation Operation)
{
    public PetPresenceTransitionCommand(
        Guid clientOperationId,
        long petId,
        PetPresenceCommandOperation operation)
        : this(
            PetCommandOperationIdentity.SecureClient(clientOperationId),
            petId,
            operation)
    {
    }

    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal readonly record struct PetSkillUnlearnCommand(
    PetCommandOperationIdentity Identity,
    int SkillSlot)
{
    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal enum PetGrowthResetOperation : byte
{
    Preview = 1,
    Accept = 2
}

internal readonly record struct PetGrowthResetCommand(
    PetCommandOperationIdentity Identity,
    PetGrowthResetOperation Operation = PetGrowthResetOperation.Preview,
    Guid PreviewOperationId = default)
{
    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal readonly record struct PetOwnerMergeToggleCommand(
    PetCommandOperationIdentity Identity)
{
    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal static class PetDurableCommandContract
{
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const long MaximumPetId = uint.MaxValue;
    private const ushort CanonicalVersion = 1;

    public static byte[] OperationScope(
        PetCommandOperationIdentity identity)
    {
        if (!identity.IsSecureClient &&
            !identity.IsRawLocalServer &&
            !identity.IsServerSessionLifecycle)
        {
            throw new ArgumentException(
                "A valid pet command operation identity is required.",
                nameof(identity));
        }

        if (identity.IsSecureClient)
        {
            var secure = new byte[16];
            WriteGuid(identity.OperationId, secure);
            return secure;
        }

        var raw = new byte[
            identity.IsServerSessionLifecycle ? 34 : 33];
        raw[0] = (byte)identity.Strength;
        var operationOffset = 1;
        if (identity.IsServerSessionLifecycle)
        {
            // Keep server-owned lifecycle identities domain-separated from
            // raw client compatibility identities at the durable inbox.
            raw[1] = 1;
            operationOffset = 2;
        }
        WriteGuid(
            identity.OperationId,
            raw.AsSpan(operationOffset, 16));
        WriteGuid(
            identity.ConnectionId,
            raw.AsSpan(operationOffset + 16, 16));
        return raw;
    }

    public static byte[] CanonicalBagActivation(int kitBagSlot)
    {
        var bytes = new byte[sizeof(ushort) * 2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        BinaryPrimitives.WriteUInt16BigEndian(
            bytes.AsSpan(sizeof(ushort)),
            checked((ushort)kitBagSlot));
        return bytes;
    }

    public static byte[] CanonicalPet(long petId, byte operation)
    {
        var bytes = new byte[sizeof(ushort) + sizeof(long) + 1];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        BinaryPrimitives.WriteInt64BigEndian(
            bytes.AsSpan(sizeof(ushort)),
            petId);
        bytes[^1] = operation;
        return bytes;
    }

    public static byte[] CanonicalSkillSlot(int skillSlot)
    {
        var bytes = new byte[sizeof(ushort) * 2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        BinaryPrimitives.WriteUInt16BigEndian(
            bytes.AsSpan(sizeof(ushort)),
            checked((ushort)skillSlot));
        return bytes;
    }

    public static byte[] CanonicalPetGrowthReset(
        PetGrowthResetOperation operation,
        Guid previewOperationId)
    {
        var bytes = new byte[sizeof(ushort) + 1 + 16];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        bytes[sizeof(ushort)] = checked((byte)operation);
        WriteGuid(
            previewOperationId,
            bytes.AsSpan(sizeof(ushort) + 1, 16));
        return bytes;
    }

    public static byte[] CanonicalPetBasicSavvyReset(
        PetBasicSavvyResetOperation operation,
        Guid previewOperationId)
    {
        var bytes = new byte[sizeof(ushort) + 1 + 16];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        bytes[sizeof(ushort)] = checked((byte)operation);
        WriteGuid(
            previewOperationId,
            bytes.AsSpan(sizeof(ushort) + 1, 16));
        return bytes;
    }

    public static byte[] CanonicalPetOwnerMergeToggle()
    {
        var bytes = new byte[sizeof(ushort) + 1];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        bytes[^1] = 1;
        return bytes;
    }

    public static byte[] CanonicalPetBind()
    {
        var bytes = new byte[sizeof(ushort) + 1];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        bytes[^1] = 1;
        return bytes;
    }

    public static bool HasMatchingProvenance(
        PetCommandOperationIdentity identity,
        CommandConnectionCorrelation connection) =>
        identity.IsSecureClient
            ? connection.Transport is
                CommandTransportKind.SecureTlsLegacy or
                CommandTransportKind.SecureCommand
            : identity.IsRawLocalServer &&
              connection.Transport == CommandTransportKind.LegacyTcp &&
              identity.ConnectionId == connection.ConnectionId ||
              identity.IsServerSessionLifecycle &&
              connection.Transport is
                  CommandTransportKind.LegacyTcp or
                  CommandTransportKind.SecureTlsLegacy &&
              identity.ConnectionId == connection.ConnectionId;

    public static bool IsValidIdentity(
        PetCommandOperationIdentity identity) =>
        identity.IsSecureClient ||
        identity.IsRawLocalServer ||
        identity.IsServerSessionLifecycle;

    private static void WriteGuid(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(
                destination,
                bigEndian: true,
                out var written) ||
            written != destination.Length)
        {
            throw new ArgumentException(
                "The operation UUID could not be encoded.",
                nameof(value));
        }
    }
}
