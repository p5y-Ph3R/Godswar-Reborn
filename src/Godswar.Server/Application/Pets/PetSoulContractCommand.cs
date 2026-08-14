using System.Buffers.Binary;

namespace Godswar.Server.Application.Pets;

/// <summary>
/// Stock Soul Contract intent. The active summoned pet is server-resolved;
/// the client supplies only Contract Spirit template 10105 and count 0..5.
/// </summary>
internal readonly record struct PetSoulContractCommand(
    PetCommandOperationIdentity Identity,
    int MaterialTemplateId,
    int Quantity)
{
    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal static class PetSoulContractCommandContract
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
