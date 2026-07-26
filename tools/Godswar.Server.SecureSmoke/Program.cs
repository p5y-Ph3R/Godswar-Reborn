using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Protocol;

namespace Godswar.Server.SecureSmoke;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var stage = "configuration";
        try
        {
            var options = SmokeOptions.Parse(args);
            using var lifetime = new CancellationTokenSource(
                options.OperationTimeout);
            var cancellationToken = lifetime.Token;
            using var root =
                X509CertificateLoader.LoadCertificateFromFile(
                    options.RootCertificatePath);
            var instanceId = RandomNumberGenerator.GetBytes(
                SecureProtocolConstants.ClientInstanceIdBytes);
            try
            {
                stage = "transient account setup";
                await using var fixture =
                    await TransientAccountFixture.CreateAsync(
                        options.PostgresConnectionString,
                        cancellationToken);
                stage = "secure login";
                using var grant = await CompleteSecureLoginAsync(
                    options,
                    fixture,
                    instanceId,
                    root,
                    cancellationToken);
                Console.WriteLine(
                    "PASS secure login TLS/ALPN/preface/authentication/ticket");

                stage = "secure game and UDP binding";
                await CompleteSecureGameAsync(
                    options,
                    fixture,
                    instanceId,
                    root,
                    grant,
                    cancellationToken);
                Console.WriteLine(
                    "PASS secure game TLS/ALPN/preface/ticket/UDP binding");
                Console.WriteLine(
                    "PASS authoritative UDP movement and snapshot");
                return 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(instanceId);
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"FAIL {stage}: bounded smoke timeout");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"FAIL {stage}: {Describe(error, stage)}");
            return 1;
        }
    }

    private static async Task<SecureGameGrant>
        CompleteSecureLoginAsync(
            SmokeOptions options,
            TransientAccountFixture fixture,
            byte[] instanceId,
            X509Certificate2 root,
            CancellationToken cancellationToken)
    {
        await using var login = await SecureTlsPeer.ConnectAsync(
            options.ServerAddress,
            options.LoginPort,
            "login.reborn.test",
            SecureEndpointRole.Login,
            instanceId,
            root,
            cancellationToken);
        var loginPacket = SmokePackets.Login(
            fixture.LoginName,
            fixture.Password.Span);
        try
        {
            await login.SendLegacyPacketAsync(
                loginPacket,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                loginPacket.AsSpan(36, 32));
        }
        using var serverList = await login.ReadUntilAsync(
            SecureFrameType.LegacyBytes,
            maximumFrames: 4,
            cancellationToken);

        await login.SendLegacyPacketAsync(
            SmokePackets.Opcode(Opcodes.SelectServer),
            cancellationToken);
        using var serverSelection = await login.ReadUntilAsync(
            SecureFrameType.LegacyBytes,
            maximumFrames: 4,
            cancellationToken);

        await login.SendLegacyPacketAsync(
            SmokePackets.Opcode(Opcodes.LoginReturnInfo),
            cancellationToken);
        using var grantFrame = await login.ReadUntilAsync(
            SecureFrameType.GameGrant,
            maximumFrames: 8,
            cancellationToken);
        if (!SecureGameControlCodec.TryDecodeGrant(
                grantFrame.Payload,
                out var grant) ||
            grant is null)
        {
            throw new InvalidDataException(
                "Server sent an invalid secure game grant.");
        }

        try
        {
            using var redirect = await login.ReadUntilAsync(
                SecureFrameType.LegacyBytes,
                maximumFrames: 4,
                cancellationToken);
            return grant;
        }
        catch
        {
            grant.Dispose();
            throw;
        }
    }

    private static async Task CompleteSecureGameAsync(
        SmokeOptions options,
        TransientAccountFixture fixture,
        byte[] instanceId,
        X509Certificate2 root,
        SecureGameGrant gameGrant,
        CancellationToken cancellationToken)
    {
        await using var game = await SecureTlsPeer.ConnectAsync(
            options.ServerAddress,
            options.GamePort,
            "game.reborn.test",
            SecureEndpointRole.Game,
            instanceId,
            root,
            cancellationToken);
        var bindPayload = SmokePackets.GameBind(gameGrant);
        try
        {
            await game.SendFrameAsync(
                SecureFrameType.GameBind,
                bindPayload,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bindPayload);
        }

        using var resultFrame = await game.ReadUntilAsync(
            SecureFrameType.BindResult,
            maximumFrames: 2,
            cancellationToken);
        if (!SecureGameControlCodec.TryDecodeBindResult(
                resultFrame.Payload,
                out var bindResult) ||
            !bindResult.IsAccepted)
        {
            throw new InvalidDataException(
                "Server rejected the secure game ticket.");
        }
        using var udpGrantFrame = await game.ReadUntilAsync(
            SecureFrameType.UdpBindingGrant,
            maximumFrames: 2,
            cancellationToken);
        if (!SecureUdpBindingGrantCodec.TryDecode(
                udpGrantFrame.Payload,
                out var udpGrant) ||
            udpGrant is null ||
            !udpGrant.Capabilities.HasFlag(
                SecureUdpBindingCapabilities
                    .AuthoritativeMovement))
        {
            udpGrant?.Dispose();
            throw new InvalidDataException(
                "Server did not grant authoritative UDP movement.");
        }

        using (udpGrant)
        {
            await using var udp = await SecureUdpPeer.BindAsync(
                options.ServerAddress,
                options.UdpPort,
                udpGrant,
                cancellationToken);
            using var drainStop =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            var drain = game.DrainAsync(drainStop.Token);
            try
            {
                await EnterWorldAsync(
                    game,
                    fixture.Username,
                    cancellationToken);
                var baseline = await udp.ReceiveSnapshotAsync(
                    static snapshot =>
                        snapshot.Flags.HasFlag(
                            SecureRealtimeSnapshotFlags.Keyframe) &&
                        snapshot.AcknowledgedInputId == 0,
                    cancellationToken);
                var targetX = baseline.X + 0.1f;
                var targetZ = baseline.Z;
                var inputId = await udp.SendMovementAsync(
                    baseline,
                    targetX,
                    targetZ,
                    cancellationToken);
                var accepted = await udp.ReceiveSnapshotAsync(
                    snapshot =>
                        snapshot.AcknowledgedInputId == inputId,
                    cancellationToken);
                if (accepted.Rejection !=
                        SecureRealtimeMovementRejection.None ||
                    accepted.PositionRevision == 0 ||
                    Math.Abs(accepted.X - targetX) > 0.0001f ||
                    Math.Abs(accepted.Z - targetZ) > 0.0001f)
                {
                    throw new InvalidDataException(
                        "Authoritative server snapshot rejected or changed the bounded movement.");
                }
                // The authoritative snapshot can reach the client before the
                // handler's bounded persistence effect completes. Give that
                // effect one short, capped drain window before closing TLS.
                await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken);
            }
            finally
            {
                drainStop.Cancel();
                try
                {
                    await drain;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private static async Task EnterWorldAsync(
        SecureTlsPeer game,
        string username,
        CancellationToken cancellationToken)
    {
        await game.SendLegacyPacketAsync(
            SmokePackets.GameLogin(username),
            cancellationToken);
        await game.SendLegacyPacketAsync(
            SmokePackets.Opcode(Opcodes.EnterGame),
            cancellationToken);
        await game.SendLegacyPacketAsync(
            SmokePackets.Opcode(Opcodes.ClientReady),
            cancellationToken);
        await game.SendLegacyPacketAsync(
            SmokePackets.Opcode(Opcodes.PlayerDetailRequest),
            cancellationToken);
        await game.SendLegacyPacketAsync(
            SmokePackets.Opcode(Opcodes.EnterUiReady),
            cancellationToken);
    }

    private static string Describe(Exception error, string stage)
    {
        if (stage == "transient account setup")
        {
            return error.GetType().Name;
        }

        var message = error.Message
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        if (message.Length > 160)
        {
            message = message[..160];
        }
        return string.IsNullOrWhiteSpace(message)
            ? error.GetType().Name
            : $"{error.GetType().Name}: {message}";
    }
}
