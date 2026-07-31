using System.Buffers.Binary;
using System.Security.Cryptography;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureTlsTransportChecks
{
    private static async Task CheckCommandOperationAssociationAsync()
    {
        await CheckSplitCommandOperationAssociationAsync();
        await CheckCoalescedCommandOperationAssociationAsync();
        await CheckRawTransportHasNoCommandOperationIdentityAsync();

        await using var fixture = await StartBoundGamePairAsync();
        await using var session = new ClientSession(fixture.Transport);
        var operationId =
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var clearPacket = MakeLegacyPacket(0x3344, 0xA1, 0xB2);
        var encryptedPacket = (byte[])clearPacket.Clone();
        var clientCipher = new PacketCipher();
        clientCipher.Transform(encryptedPacket);

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
            encryptedPacket);

        var packet = await session.ReadPacketAsync(
            CancellationToken.None);
        Check.True(packet is not null, "operation-associated packet exists");
        Check.True(
            packet!.ClientOperationId == operationId,
            "operation UUID attaches to exactly the next packet");
        Check.True(
            packet.Buffer.SequenceEqual(clearPacket),
            "operation metadata does not alter legacy packet bytes");

        var secondClear = MakeLegacyPacket(0x3345, 0xC3);
        var secondEncrypted = (byte[])secondClear.Clone();
        clientCipher.Transform(secondEncrypted);
        await WriteFrameAsync(
            fixture.Pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameType.LegacyBytes,
            sequence: 4,
            secondEncrypted);
        var second = await session.ReadPacketAsync(
            CancellationToken.None);
        Check.True(second is not null, "unmarked packet exists");
        Check.True(
            second!.ClientOperationId is null,
            "operation UUID is consumed by only one packet");
    }

    private static async Task CheckCommandOperationFailuresAsync()
    {
        await CheckDuplicateCommandMetadataFailsClosedAsync();
        await CheckCommandMetadataMismatchFailsClosedAsync();
        await CheckMidPacketCommandMetadataFailsClosedAsync();
        await CheckAbandonedCommandMetadataFailsClosedAsync();
    }

    private static async Task
        CheckDuplicateCommandMetadataFailsClosedAsync()
    {
        await using var fixture = await StartBoundGamePairAsync();
        var operation = Guid.NewGuid();
        var packet = MakeLegacyPacket(0x4401);
        await WriteCommandOperationAsync(
            fixture.Pair.ClientStream,
            sequence: 2,
            operation,
            packet);
        await WriteCommandOperationAsync(
            fixture.Pair.ClientStream,
            sequence: 3,
            operation,
            packet);
        Check.True(
            await WaitForTlsCloseAsync(fixture.Pair.ClientStream),
            "duplicate pending operation metadata closes the channel");
    }

    private static async Task
        CheckCommandMetadataMismatchFailsClosedAsync()
    {
        await using var fixture = await StartBoundGamePairAsync();
        await using var session = new ClientSession(fixture.Transport);
        var packet = MakeLegacyPacket(0x4402, 1);
        var encrypted = (byte[])packet.Clone();
        new PacketCipher().Transform(encrypted);
        var operation = new SecureLegacyCommandOperation(
            Guid.NewGuid(),
            checked((ushort)packet.Length),
            Opcode: 0x9999);
        var payload = EncodeCommandOperation(operation);
        await WriteFrameAsync(
            fixture.Pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameType.LegacyCommandOperation,
            sequence: 2,
            payload);
        await WriteFrameAsync(
            fixture.Pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameType.LegacyBytes,
            sequence: 3,
            encrypted);

        await ExpectExceptionAsync<SecureTransportException>(
            async () =>
            {
                _ = await session.ReadPacketAsync(
                    CancellationToken.None);
            },
            "operation metadata mismatch fails the packet boundary");
    }

    private static async Task
        CheckMidPacketCommandMetadataFailsClosedAsync()
    {
        await using var fixture = await StartBoundGamePairAsync();
        await using var session = new ClientSession(fixture.Transport);
        var packet = MakeLegacyPacket(0x4403, 1, 2, 3);
        var encrypted = (byte[])packet.Clone();
        new PacketCipher().Transform(encrypted);
        await WriteFrameAsync(
            fixture.Pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameType.LegacyBytes,
            sequence: 2,
            encrypted[..2]);
        await WriteCommandOperationAsync(
            fixture.Pair.ClientStream,
            sequence: 3,
            Guid.NewGuid(),
            packet);
        await WriteFrameAsync(
            fixture.Pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameType.LegacyBytes,
            sequence: 4,
            encrypted[2..]);

        await ExpectExceptionAsync<SecureTransportException>(
            async () =>
            {
                _ = await session.ReadPacketAsync(
                    CancellationToken.None);
            },
            "operation metadata inside a packet fails closed");
    }

    private static async Task WriteCommandOperationAsync(
        Stream stream,
        ulong sequence,
        Guid operationId,
        byte[] clearPacket)
    {
        var operation = new SecureLegacyCommandOperation(
            operationId,
            BinaryPrimitives.ReadUInt16LittleEndian(clearPacket),
            BinaryPrimitives.ReadUInt16LittleEndian(
                clearPacket.AsSpan(2)));
        await WriteFrameAsync(
            (System.Net.Security.SslStream)stream,
            SecureEndpointRole.Game,
            SecureFrameType.LegacyCommandOperation,
            sequence,
            EncodeCommandOperation(operation));
    }

    private static byte[] EncodeCommandOperation(
        SecureLegacyCommandOperation operation)
    {
        var payload = new byte[
            SecureProtocolConstants.LegacyCommandOperationBytes];
        Check.True(
            SecureLegacyCommandOperationCodec.TryEncode(
                operation,
                payload,
                out var written) &&
            written == payload.Length,
            "command operation test payload encodes");
        return payload;
    }

    private static byte[] MakeLegacyPacket(
        ushort opcode,
        params byte[] payload)
    {
        var packet = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            opcode);
        payload.CopyTo(packet.AsSpan(4));
        return packet;
    }

    private static async Task<BoundGamePairFixture>
        StartBoundGamePairAsync()
    {
        var certificate = SecureTlsTestCertificate.Create();
        var ticketStore = new InMemoryGameTicketStore();
        TlsPair? pair = null;
        try
        {
            var options = CreateRuntimeOptions();
            using var gate = new TlsHandshakeGate(1);
            var target = new SecureGameTarget(
                "game.reborn.test",
                "game.reborn.test",
                "reborn-game",
                routePort: 7000,
                tlsPort: 7443,
                serverId: 100);
            var clientInstanceId = Enumerable.Range(
                    1,
                    SecureProtocolConstants.ClientInstanceIdBytes)
                .Select(static value => checked((byte)value))
                .ToArray();
            var buildHash = Convert.FromHexString(
                SecureNetworkOptions.PredecessorOriginSha256);
            var loginContext = new SecureConnectionContext(
                SecureEndpointRole.Login,
                SecureProtocolConstants.ProtocolMajor,
                SecureProtocolConstants.ProtocolMinor,
                clientInstanceId,
                clientInstanceId,
                buildHash);
            var generation = await ticketStore.BeginLoginAsync(
                7,
                "test2",
                SecureTicketOperationDeadline.Default);
            var issued = await ticketStore.IssueAsync(
                generation.Generation!,
                loginContext,
                target,
                SecureTicketOperationDeadline.Default);
            await using var grantLease = issued.Lease!;
            Check.True(
                await grantLease.CommitAsync(
                    SecureTicketOperationDeadline.Default),
                "command-operation game grant commits");

            var factory = new TlsMuxLegacyTransportFactory(
                new SecureNetworkOptions(),
                options,
                certificate.Context,
                gate,
                timeProvider: null,
                ticketStore: ticketStore,
                gameTarget: target);
            pair = await StartPairAsync(
                factory,
                NetworkEndpointRole.Game);
            _ = await AuthenticateAndPrefaceAsync(
                pair.ClientStream,
                certificate,
                SecureEndpointRole.Game,
                targetHost: "game.reborn.test");

            var grantId = new byte[
                SecureProtocolConstants.GrantIdBytes];
            var ticket = new byte[
                SecureProtocolConstants.TicketBytes];
            try
            {
                Check.True(
                    grantLease.Grant.TryCopySecrets(grantId, ticket),
                    "command-operation grant secrets copy");
                using var bind = new SecureGameBind(grantId, ticket);
                var bindPayload = new byte[
                    SecureProtocolConstants.GameBindBytes];
                Check.True(
                    SecureGameControlCodec.TryEncodeBind(
                        bind,
                        bindPayload,
                        out var written) &&
                    written == bindPayload.Length,
                    "command-operation game bind encodes");
                await WriteFrameAsync(
                    pair.ClientStream,
                    SecureEndpointRole.Game,
                    SecureFrameType.GameBind,
                    sequence: 1,
                    bindPayload);
                CryptographicOperations.ZeroMemory(bindPayload);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(grantId);
                CryptographicOperations.ZeroMemory(ticket);
            }

            var result = await ReadFrameAsync(
                pair.ClientStream,
                SecureEndpointRole.Game,
                SecureFrameDirection.ServerToClient,
                expectedSequence: 1);
            Check.True(
                SecureGameControlCodec.TryDecodeBindResult(
                    result.Payload,
                    out var bindResult) &&
                bindResult.IsAccepted,
                "command-operation game bind accepted");
            var transport = await pair.TransportTask;
            return new BoundGamePairFixture(
                certificate,
                ticketStore,
                pair,
                transport);
        }
        catch
        {
            if (pair is not null)
            {
                await pair.DisposeAsync();
            }
            ticketStore.Dispose();
            certificate.Dispose();
            throw;
        }
    }

    private sealed class BoundGamePairFixture(
        SecureTlsTestCertificate certificate,
        InMemoryGameTicketStore ticketStore,
        TlsPair pair,
        ILegacyByteTransport transport) :
        IAsyncDisposable
    {
        public TlsPair Pair { get; } = pair;

        public ILegacyByteTransport Transport { get; } = transport;

        public async ValueTask DisposeAsync()
        {
            await Pair.DisposeAsync();
            ticketStore.Dispose();
            certificate.Dispose();
        }
    }
}
