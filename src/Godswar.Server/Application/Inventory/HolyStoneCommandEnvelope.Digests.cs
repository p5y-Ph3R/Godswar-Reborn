using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Godswar.Server.Application.Inventory;

internal static partial class HolyStoneCommandEnvelope
{
    private static byte[] ComputeCombinationStateDigest(
        byte[] targetState,
        byte[] firstMaterialState,
        byte[] secondMaterialState,
        byte[] thirdMaterialState)
    {
        var tagged = new byte[
            sizeof(byte) + sizeof(ushort) + targetState.Length +
            sizeof(byte) + sizeof(ushort) + firstMaterialState.Length +
            sizeof(byte) + sizeof(ushort) + secondMaterialState.Length +
            sizeof(byte) + sizeof(ushort) + thirdMaterialState.Length];
        var offset = WriteTaggedState(
            tagged, 0, TargetStateRole, targetState);
        offset = WriteTaggedState(
            tagged, offset, StoneStateRole, firstMaterialState);
        offset = WriteTaggedState(
            tagged, offset, CatalystStateRole, secondMaterialState);
        WriteTaggedState(
            tagged,
            offset,
            ThirdMaterialStateRole,
            thirdMaterialState);
        return SHA256.HashData(tagged);
    }

    private static byte[] ComputeUpgradeStateDigest(
        byte[] targetState,
        byte[] stoneState,
        byte[] catalystState)
    {
        var tagged = new byte[
            sizeof(byte) + sizeof(ushort) + targetState.Length +
            sizeof(byte) + sizeof(ushort) + stoneState.Length +
            sizeof(byte) + sizeof(ushort) + catalystState.Length];
        var offset = WriteTaggedState(
            tagged, 0, TargetStateRole, targetState);
        offset = WriteTaggedState(
            tagged, offset, StoneStateRole, stoneState);
        WriteTaggedState(
            tagged, offset, CatalystStateRole, catalystState);
        return SHA256.HashData(tagged);
    }

    private static int WriteTaggedState(
        Span<byte> destination,
        int offset,
        byte role,
        byte[] state)
    {
        destination[offset++] = role;
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[offset..],
            checked((ushort)state.Length));
        offset += sizeof(ushort);
        state.CopyTo(destination[offset..]);
        return offset + state.Length;
    }

    private static byte[] ComputeStateDigest(
        byte[] targetState,
        byte[] stoneState)
    {
        var tagged = new byte[
            sizeof(byte) + sizeof(ushort) + targetState.Length +
            sizeof(byte) + sizeof(ushort) + stoneState.Length];
        var destination = tagged.AsSpan();
        var offset = 0;
        destination[offset++] = TargetStateRole;
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[offset..],
            checked((ushort)targetState.Length));
        offset += sizeof(ushort);
        targetState.CopyTo(destination[offset..]);
        offset += targetState.Length;
        destination[offset++] = StoneStateRole;
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[offset..],
            checked((ushort)stoneState.Length));
        offset += sizeof(ushort);
        stoneState.CopyTo(destination[offset..]);
        return SHA256.HashData(tagged);
    }
}
