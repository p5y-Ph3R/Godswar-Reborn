using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureTlsTransportChecks
{
    private static async Task
        CheckSplitCommandOperationAssociationAsync()
    {
        await using var fixture = await StartBoundGamePairAsync();
        await using var session = new ClientSession(fixture.Transport);
        var operationId =
            Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
        var clearPacket = MakeLegacyPacket(
            0x4501,
            0x11,
            0x22,
            0x33,
            0x44);
        var encryptedPacket = (byte[])clearPacket.Clone();
        new PacketCipher().Transform(encryptedPacket);
        const int splitOffset = 3;

        await WriteCommandOperationAsync(
            fixture.Pair.ClientStream,
            sequence: 2,
            operationId,
            clearPacket);
        await WriteFrameAsync(
            fixture.Pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameType.LegacyBytes,
            sequence: 3,
            encryptedPacket[..splitOffset]);
        await WriteFrameAsync(
            fixture.Pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameType.LegacyBytes,
            sequence: 4,
            encryptedPacket[splitOffset..]);

        var packet = await session.ReadPacketAsync(
            CancellationToken.None);
        Check.True(
            packet is not null &&
            packet.ClientOperationId == operationId,
            "operation UUID survives a legacy packet split across secure frames");
        Check.True(
            packet!.Buffer.SequenceEqual(clearPacket),
            "split marked packet preserves the complete legacy bytes");
    }

    private static async Task
        CheckCoalescedCommandOperationAssociationAsync()
    {
        await using var fixture = await StartBoundGamePairAsync();
        await using var session = new ClientSession(fixture.Transport);
        var operationId =
            Guid.Parse("20314253-6475-8697-a8b9-cadbecfd0e1f");
        var markedClear = MakeLegacyPacket(0x4502, 0x51, 0x52);
        var followingClear = MakeLegacyPacket(
            0x4503,
            0x61,
            0x62,
            0x63);
        var coalescedEncrypted = markedClear
            .Concat(followingClear)
            .ToArray();
        new PacketCipher().Transform(coalescedEncrypted);

        await WriteCommandOperationAsync(
            fixture.Pair.ClientStream,
            sequence: 2,
            operationId,
            markedClear);
        await WriteFrameAsync(
            fixture.Pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameType.LegacyBytes,
            sequence: 3,
            coalescedEncrypted);

        var marked = await session.ReadPacketAsync(
            CancellationToken.None);
        Check.True(
            marked is not null &&
            marked.ClientOperationId == operationId,
            "operation UUID marks the first packet in a coalesced secure frame");
        Check.True(
            marked!.Buffer.SequenceEqual(markedClear),
            "coalesced marked packet preserves its legacy bytes");

        var following = await session.ReadPacketAsync(
            CancellationToken.None);
        Check.True(
            following is not null &&
            following.ClientOperationId is null,
            "coalesced following packet does not inherit the operation UUID");
        Check.True(
            following!.Buffer.SequenceEqual(followingClear),
            "coalesced following packet preserves its legacy bytes");
    }

    private static async Task
        CheckRawTransportHasNoCommandOperationIdentityAsync()
    {
        var clearPacket = MakeLegacyPacket(
            0x4504,
            0x71,
            0x72,
            0x73);
        var encryptedPacket = (byte[])clearPacket.Clone();
        new PacketCipher().Transform(encryptedPacket);
        var transport = new ScriptedLegacyByteTransport(
            encryptedPacket,
            [1, 2, 1]);
        await using var session = new ClientSession(transport);

        var packet = await session.ReadPacketAsync(
            CancellationToken.None);
        Check.True(
            packet is not null &&
            packet.ClientOperationId is null,
            "raw legacy transport cannot manufacture a client operation UUID");
        Check.True(
            packet!.Buffer.SequenceEqual(clearPacket),
            "raw transport still preserves the legacy packet bytes");
    }

    private static async Task
        CheckAbandonedCommandMetadataFailsClosedAsync()
    {
        await using var fixture = await StartBoundGamePairAsync();
        var packet = MakeLegacyPacket(0x4505, 0x81);
        await WriteCommandOperationAsync(
            fixture.Pair.ClientStream,
            sequence: 2,
            Guid.NewGuid(),
            packet);
        await WriteFrameAsync(
            fixture.Pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameType.Close,
            sequence: 3,
            new byte[4]);

        Check.True(
            await WaitForTlsCloseAsync(fixture.Pair.ClientStream),
            "closing with unassociated operation metadata fails closed");
    }
}
