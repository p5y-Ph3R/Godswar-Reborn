using System.Buffers.Binary;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal readonly record struct BagItemActivationCommand(
    Guid ClientOperationId,
    int KitBagSlot,
    BagItemActivationExecutionConstraint ExecutionConstraint =
        BagItemActivationExecutionConstraint.None);

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
    Guid ClientOperationId,
    long PetId);

internal enum PetPresenceCommandOperation : byte
{
    Take = 0,
    CallOut = 1,
    Recall = 2
}

internal readonly record struct PetPresenceTransitionCommand(
    Guid ClientOperationId,
    long PetId,
    PetPresenceCommandOperation Operation);

internal static class PetDurableCommandContract
{
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const long MaximumPetId = uint.MaxValue;
    private const ushort CanonicalVersion = 1;

    public static byte[] OperationScope(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty operation UUID is required.",
                nameof(operationId));
        }

        var bytes = new byte[16];
        if (!operationId.TryWriteBytes(
                bytes,
                bigEndian: true,
                out var written) ||
            written != bytes.Length)
        {
            throw new ArgumentException(
                "The operation UUID could not be encoded.",
                nameof(operationId));
        }

        return bytes;
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

    public static bool IsTrusted(CommandTransportKind transport) =>
        transport is CommandTransportKind.SecureTlsLegacy or
            CommandTransportKind.SecureCommand;
}
