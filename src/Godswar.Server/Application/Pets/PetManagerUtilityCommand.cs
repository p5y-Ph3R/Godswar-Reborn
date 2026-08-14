using System.Buffers.Binary;

namespace Godswar.Server.Application.Pets;

internal enum PetManagerUtilityOperation : byte
{
    CheckGrowth = 1,
    Seal = 2,
    Unseal = 3,
    ClaimPetCall = 4,
    ClaimMerge = 5,
    ChangeGender = 6
}

/// <summary>
/// Pet Manager utility intent. The server resolves the one summoned pet.
/// Only Unseal carries a selected, absolute kit-bag slot.
/// </summary>
internal readonly record struct PetManagerUtilityCommand(
    PetCommandOperationIdentity Identity,
    PetManagerUtilityOperation Operation,
    int KitBagSlot)
{
    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal static class PetManagerUtilityCommandContract
{
    private const ushort CanonicalVersion = 1;

    public static byte[] CanonicalRequest(
        PetManagerUtilityOperation operation,
        int kitBagSlot)
    {
        var bytes = new byte[sizeof(ushort) + 1 + sizeof(short)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        bytes[sizeof(ushort)] = checked((byte)operation);
        BinaryPrimitives.WriteInt16BigEndian(
            bytes.AsSpan(sizeof(ushort) + 1),
            checked((short)kitBagSlot));
        return bytes;
    }
}
