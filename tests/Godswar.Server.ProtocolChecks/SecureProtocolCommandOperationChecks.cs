using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureProtocolCodecChecks
{
    private static void CheckLegacyCommandOperationCodec()
    {
        var operation = new SecureLegacyCommandOperation(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            PacketLength: 32,
            Opcode: 0x1234);
        var encoded = new byte[
            SecureProtocolConstants.LegacyCommandOperationBytes];
        Check.True(
            SecureLegacyCommandOperationCodec.TryEncode(
                operation,
                encoded,
                out var written),
            "legacy command operation encodes");
        Check.Equal(encoded.Length, written, "operation encoded length");
        var expected = Convert.FromHexString(
            "0100002012340000" +
            "00112233445566778899AABBCCDDEEFF");
        Check.True(
            encoded.SequenceEqual(expected),
            "operation metadata has canonical network byte order");
        Check.True(
            SecureLegacyCommandOperationCodec.TryDecode(
                encoded,
                out var decoded),
            "legacy command operation decodes");
        Check.Equal(
            operation.OperationId,
            decoded.OperationId,
            "operation UUID round trips");
        Check.Equal(
            operation.PacketLength,
            decoded.PacketLength,
            "operation packet length round trips");
        Check.Equal(
            operation.Opcode,
            decoded.Opcode,
            "operation opcode round trips");

        CheckHeaderContext(
            SecureFrameType.LegacyCommandOperation,
            SecureProtocolConstants.LegacyCommandOperationBytes,
            SecureEndpointRole.Game,
            SecureFrameDirection.ClientToServer,
            expected: true);
        CheckHeaderContext(
            SecureFrameType.LegacyCommandOperation,
            SecureProtocolConstants.LegacyCommandOperationBytes,
            SecureEndpointRole.Login,
            SecureFrameDirection.ClientToServer,
            expected: false);
        CheckHeaderContext(
            SecureFrameType.LegacyCommandOperation,
            SecureProtocolConstants.LegacyCommandOperationBytes,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expected: false);

        foreach (var offset in new[] { 0, 1, 6, 7 })
        {
            var malformed = (byte[])encoded.Clone();
            malformed[offset] ^= 1;
            Check.True(
                !SecureLegacyCommandOperationCodec.TryDecode(
                    malformed,
                    out _),
                $"operation reserved/version mutation {offset} rejects");
        }

        var zeroId = (byte[])encoded.Clone();
        zeroId.AsSpan(8, 16).Clear();
        Check.True(
            !SecureLegacyCommandOperationCodec.TryDecode(
                zeroId,
                out _),
            "empty operation UUID rejects");
        var shortPacket = (byte[])encoded.Clone();
        shortPacket[2] = 0;
        shortPacket[3] = 3;
        Check.True(
            !SecureLegacyCommandOperationCodec.TryDecode(
                shortPacket,
                out _),
            "undersized described packet rejects");
        var oversizedPacket = (byte[])encoded.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(
            oversizedPacket.AsSpan(2),
            checked((ushort)(LegacyProtocolLimits.MaxPacketLength + 1)));
        Check.True(
            !SecureLegacyCommandOperationCodec.TryDecode(
                oversizedPacket,
                out _),
            "oversized described packet rejects");
        Check.True(
            !SecureLegacyCommandOperationCodec.TryDecode(
                encoded.AsSpan(0, encoded.Length - 1),
                out _),
            "truncated operation metadata rejects");
    }
}
