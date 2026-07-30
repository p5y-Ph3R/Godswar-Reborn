using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureProtocolCodecChecks
{
    private static void CheckLegacyCommandResultCodec()
    {
        var operationId =
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var result = new SecureLegacyCommandResult(
            SecureLegacyCommandDisposition.Applied,
            commandFamily: 0x1234,
            resultCode: 0x89ABCDEF,
            authoritativeRevision: 0x0102030405060708,
            operationId);
        var encoded = new byte[
            SecureProtocolConstants.LegacyCommandResultBytes];
        Check.True(
            SecureLegacyCommandResultCodec.TryEncode(
                result,
                encoded,
                out var written),
            "legacy command result encodes");
        Check.Equal(encoded.Length, written, "result encoded length");
        var expected = Convert.FromHexString(
            "0101123489ABCDEF0102030405060708" +
            "00112233445566778899AABBCCDDEEFF");
        Check.True(
            encoded.SequenceEqual(expected),
            "command result has canonical network byte order");
        Check.True(
            SecureLegacyCommandResultCodec.TryDecode(
                encoded,
                out var decoded),
            "legacy command result decodes");
        Check.Equal(
            (int)result.Disposition,
            (int)decoded.Disposition,
            "command disposition round trips");
        Check.Equal(
            result.CommandFamily,
            decoded.CommandFamily,
            "command family round trips");
        Check.Equal(
            result.ResultCode,
            decoded.ResultCode,
            "command result code round trips");
        Check.Equal(
            result.AuthoritativeRevision,
            decoded.AuthoritativeRevision,
            "command authoritative revision round trips");
        Check.Equal(
            decoded.AuthoritativeRevision,
            decoded.InventoryRevision,
            "inventory revision compatibility alias is exact");
        Check.Equal(
            result.OperationId,
            decoded.OperationId,
            "command result UUID round trips");

        CheckLegacyCommandResultBoundaries(operationId);
        CheckLegacyCommandResultRejections(encoded, operationId);
        CheckLegacyCommandResultFrameContext();
    }

    private static void CheckLegacyCommandResultBoundaries(
        Guid operationId)
    {
        var encoded = new byte[
            SecureProtocolConstants.LegacyCommandResultBytes];
        foreach (var disposition in Enum.GetValues<
                     SecureLegacyCommandDisposition>())
        {
            var revision =
                disposition == SecureLegacyCommandDisposition.Applied
                    ? 1UL
                    : 0UL;
            var boundary = new SecureLegacyCommandResult(
                disposition,
                ushort.MaxValue,
                uint.MaxValue,
                revision,
                operationId);
            encoded.AsSpan().Clear();
            Check.True(
                SecureLegacyCommandResultCodec.TryEncode(
                    boundary,
                    encoded,
                    out var written) &&
                written == encoded.Length,
                $"disposition {disposition} boundary encodes");
            Check.True(
                SecureLegacyCommandResultCodec.TryDecode(
                    encoded,
                    out var decoded) &&
                decoded == boundary,
                $"disposition {disposition} boundary round trips");
        }

        var maximum = new SecureLegacyCommandResult(
            SecureLegacyCommandDisposition.Applied,
            ushort.MaxValue,
            uint.MaxValue,
            ulong.MaxValue,
            operationId);
        var maximumBytes = new byte[
            SecureProtocolConstants.LegacyCommandResultBytes];
        Check.True(
            SecureLegacyCommandResultCodec.TryEncode(
                maximum,
                maximumBytes,
                out _),
            "maximum command result values encode");
        Check.True(
            SecureLegacyCommandResultCodec.TryDecode(
                maximumBytes,
                out var maximumDecoded) &&
            maximumDecoded == maximum,
            "maximum command result values round trip");
    }

    private static void CheckLegacyCommandResultRejections(
        byte[] canonical,
        Guid operationId)
    {
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new SecureLegacyCommandResult(
                (SecureLegacyCommandDisposition)0,
                1,
                0,
                1,
                operationId),
            "command result model rejects unknown disposition");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new SecureLegacyCommandResult(
                SecureLegacyCommandDisposition.Rejected,
                0,
                0,
                0,
                operationId),
            "command result model rejects zero family");
        Check.Throws<ArgumentException>(
            () => _ = new SecureLegacyCommandResult(
                SecureLegacyCommandDisposition.Rejected,
                1,
                0,
                0,
                Guid.Empty),
            "command result model rejects empty UUID");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new SecureLegacyCommandResult(
                SecureLegacyCommandDisposition.Applied,
                1,
                0,
                0,
                operationId),
            "applied result model requires a durable revision");
        Check.True(
            !SecureLegacyCommandResultCodec.TryEncode(
                default,
                new byte[
                    SecureProtocolConstants.LegacyCommandResultBytes],
                out _),
            "default command result cannot encode");

        foreach (var length in new[]
                 {
                     0,
                     SecureProtocolConstants.LegacyCommandResultBytes - 1,
                     SecureProtocolConstants.LegacyCommandResultBytes + 1
                 })
        {
            var bytes = new byte[length];
            canonical.AsSpan(0, Math.Min(length, canonical.Length))
                .CopyTo(bytes);
            Check.True(
                !SecureLegacyCommandResultCodec.TryDecode(
                    bytes,
                    out _),
                $"command result non-exact length {length} rejects");
        }

        foreach (var invalidDisposition in new byte[] { 0, 5, byte.MaxValue })
        {
            var malformed = (byte[])canonical.Clone();
            malformed[1] = invalidDisposition;
            Check.True(
                !SecureLegacyCommandResultCodec.TryDecode(
                    malformed,
                    out _),
                $"command result disposition {invalidDisposition} rejects");
        }

        var wrongVersion = (byte[])canonical.Clone();
        wrongVersion[0] = 2;
        Check.True(
            !SecureLegacyCommandResultCodec.TryDecode(
                wrongVersion,
                out _),
            "command result unknown version rejects");
        var zeroFamily = (byte[])canonical.Clone();
        zeroFamily.AsSpan(2, 2).Clear();
        Check.True(
            !SecureLegacyCommandResultCodec.TryDecode(
                zeroFamily,
                out _),
            "command result zero family rejects");
        var zeroRevision = (byte[])canonical.Clone();
        zeroRevision.AsSpan(8, 8).Clear();
        Check.True(
            !SecureLegacyCommandResultCodec.TryDecode(
                zeroRevision,
                out _),
            "applied command result zero revision rejects");
        var zeroId = (byte[])canonical.Clone();
        zeroId.AsSpan(16, 16).Clear();
        Check.True(
            !SecureLegacyCommandResultCodec.TryDecode(
                zeroId,
                out _),
            "command result empty UUID rejects");
    }

    private static void CheckLegacyCommandResultFrameContext()
    {
        CheckHeaderContext(
            SecureFrameType.LegacyCommandResult,
            SecureProtocolConstants.LegacyCommandResultBytes,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expected: true);
        CheckHeaderContext(
            SecureFrameType.LegacyCommandResult,
            SecureProtocolConstants.LegacyCommandResultBytes,
            SecureEndpointRole.Game,
            SecureFrameDirection.ClientToServer,
            expected: false);
        CheckHeaderContext(
            SecureFrameType.LegacyCommandResult,
            SecureProtocolConstants.LegacyCommandResultBytes,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient,
            expected: false);
        CheckHeaderContext(
            SecureFrameType.LegacyCommandResult,
            SecureProtocolConstants.LegacyCommandResultBytes - 1,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expected: false);
        CheckHeaderContext(
            SecureFrameType.LegacyCommandResult,
            SecureProtocolConstants.LegacyCommandResultBytes + 1,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expected: false);
    }
}
