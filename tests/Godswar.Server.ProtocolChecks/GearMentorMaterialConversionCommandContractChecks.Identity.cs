using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    GearMentorMaterialConversionCommandContractChecks
{
    private static string ExpectedOperationId(
        CommandFamily family,
        CommandSubject subject,
        Guid operationId)
    {
        Span<byte> operationScope = stackalloc byte[16];
        Check.True(
            operationId.TryWriteBytes(
                operationScope,
                bigEndian: true,
                out var bytesWritten) &&
            bytesWritten == operationScope.Length,
            "test operation UUID writes in network order");

        var domain =
            Encoding.ASCII.GetBytes("godswar.command.operation.v1\0");
        var input = new byte[
            domain.Length +
            sizeof(int) +
            sizeof(ushort) +
            (sizeof(int) * 2) +
            operationScope.Length];
        domain.CopyTo(input, 0);
        var offset = domain.Length;
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(offset),
            CommandEnvelopeContract.CurrentVersion);
        offset += sizeof(int);
        BinaryPrimitives.WriteUInt16BigEndian(
            input.AsSpan(offset),
            (ushort)family);
        offset += sizeof(ushort);
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(offset),
            subject.AccountId);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(offset),
            subject.CharacterId);
        offset += sizeof(int);
        operationScope.CopyTo(input.AsSpan(offset));
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
