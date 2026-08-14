using System.Buffers.Binary;

namespace Godswar.Server.Application.Pets;

/// <summary>
/// A native Samsara/rebirth request. The stock client identifies the active
/// pet implicitly and supplies only one material template plus a quantity.
/// Pet identity, eligibility, inventory rows, and the resulting growth are
/// resolved authoritatively by the server.
/// </summary>
internal readonly record struct PetRebirthCommand(
    PetCommandOperationIdentity Identity,
    int MaterialTemplateId,
    int Quantity)
{
    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal static class PetRebirthCommandContract
{
    private const ushort CanonicalVersion = 1;

    public static byte[] CanonicalRequest(
        int materialTemplateId,
        int quantity)
    {
        var bytes = new byte[sizeof(ushort) + sizeof(int) + 1];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        BinaryPrimitives.WriteInt32BigEndian(
            bytes.AsSpan(sizeof(ushort)),
            materialTemplateId);
        bytes[^1] = checked((byte)quantity);
        return bytes;
    }
}
