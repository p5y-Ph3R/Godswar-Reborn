using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static class SecureUdpRuntimeChecks
{
    private static readonly IPEndPoint ReceiveTemplate =
        new(IPAddress.Any, 0);

    public static async Task RunAsync()
    {
        await CheckActivationAndLifecycleAsync();
        await CheckAdmissionReservesEndToEndAsync();
        await CheckCleanupFaultSupervisionAsync();
    }

    private static async Task CheckActivationAndLifecycleAsync()
    {
        CheckCheckedInProfilesDisabled();
        var target = CreateTarget();
        var secureOptions = new SecureNetworkOptions
        {
            Enabled = true
        };
        secureOptions.Udp.Enabled = false;
        Check.True(
            SecureUdpRuntime.TryCreate(
                secureOptions,
                target,
                default) is null,
            "disabled UDP creates no authority, grant, or listener");

        secureOptions.Udp.Enabled = true;
        Check.Throws<InvalidDataException>(
            () => SecureUdpRuntime.TryCreate(
                secureOptions,
                target,
                new SecureUdpRuntimeCapabilities(
                    ProtectedDatagrams: true,
                    NativeUdpWorker: false,
                    LoopbackEndToEndVerified: true,
                    TlsFallbackVerified: true)),
            "incomplete capability gate fails before live activation");
        Check.True(
            SecureUdpRuntimeCapabilities.Current.IsComplete,
            "compiled Slice 9 runtime capabilities are complete");

        var options = new SecureUdpOptions
        {
            BindHost = "127.0.0.1"
        };
        var runtime = SecureUdpRuntime.CreateForLoopbackTest(
            options,
            target);
        using var lifetime = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var runTask = runtime.RunAsync(lifetime.Token);
        var endpoint = await runtime.WaitUntilReadyAsync(
            lifetime.Token);
        var ready = runtime.GetSnapshot();
        Check.True(
            IPAddress.IsLoopback(endpoint.Address) &&
            endpoint.Port > 0 &&
            ready.IsReady &&
            ready.LocalEndpoint?.Equals(endpoint) == true &&
            ready.Sessions.TrackedSessions == 0,
            "loopback runtime publishes bounded readiness");
        Check.Throws<InvalidOperationException>(
            () => runtime.RunAsync(CancellationToken.None),
            "runtime owns one listener lifecycle");

        lifetime.Cancel();
        await runTask;
        Check.True(
            runtime.GetSnapshot().State ==
                SecureUdpRuntimeState.Stopped,
            "host cancellation stops listener before disposal");
        await runtime.DisposeAsync();
        Check.True(
            runtime.GetSnapshot().State ==
                SecureUdpRuntimeState.Disposed,
            "runtime disposes authority and cookie ownership last");
    }

    private static async Task CheckAdmissionReservesEndToEndAsync()
    {
        var target = CreateTarget();
        var options = new SecureUdpOptions
        {
            BindHost = "127.0.0.1",
            GlobalPacketsPerSecond = 4,
            UnvalidatedPacketsPerSecond = 1,
            PrefixPacketsPerSecond = 1,
            BindingProofPacketsPerSecond = 1,
            BindingProofPrefixPacketsPerSecond = 1,
            ProtectedCandidatePacketsPerSecond = 2,
            ProtectedCandidatePrefixPacketsPerSecond = 2,
            AuthenticatedSessionPacketsPerSecond = 1,
            RateLimitPrefixCapacity = 4,
            SessionCapacity = 4
        };
        await using var runtime =
            SecureUdpRuntime.CreateForLoopbackTest(options, target);
        var connectionId = Enumerable.Range(1, 16)
            .Select(static value => checked((byte)value))
            .ToArray();
        var context = new SecureConnectionContext(
            SecureEndpointRole.Game,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            connectionId,
            Enumerable.Repeat((byte)0x41, 16).ToArray(),
            Convert.FromHexString(
                SecureNetworkOptions.PredecessorOriginSha256));
        var registration = runtime.Authority.Register(
            context,
            new SecureBoundGamePrincipal(
                7,
                "test2",
                SecureGamePermissions.EnterWorld,
                Guid.Parse(
                    "11111111-2222-3333-4444-555555555555")));
        Check.True(
            registration.IsRegistered,
            "reserved-admission fixture registers TLS session");
        using var lease = registration.Lease!;
        var registeredId = new byte[16];
        var proofKey = new byte[32];
        Check.True(
            lease.TryCopyGrantMaterial(
                registeredId,
                proofKey,
                out _),
            "reserved-admission fixture copies binding material");

        using var lifetime = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var runTask = runtime.RunAsync(lifetime.Token);
        var serverEndpoint = await runtime.WaitUntilReadyAsync(
            lifetime.Token);
        using var client = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var nonce = Enumerable.Repeat((byte)0x5A, 16).ToArray();
        var hello = new byte[128];
        Check.True(
            SecureUdpAddressValidation.TryEncodeClientHello(
                registeredId,
                nonce,
                hello,
                out var helloBytes) &&
            helloBytes == hello.Length,
            "reserved-admission hello encodes");
        await client.SendToAsync(
            hello,
            SocketFlags.None,
            serverEndpoint,
            lifetime.Token);
        var challenge = await ReceiveDatagramAsync(
            client,
            lifetime.Token);
        Check.Equal(
            SecureUdpBindingConstants.DatagramBytes,
            challenge.Length,
            "first unvalidated packet receives challenge");

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await client.SendToAsync(
                attempt % 2 == 0 ? hello : new byte[127],
                SocketFlags.None,
                serverEndpoint,
                lifetime.Token);
        }

        var proof = new byte[128];
        Check.True(
            SecureUdpAddressValidation
                .TryCreateAuthenticatedClientProof(
                    challenge,
                    proofKey,
                    proof,
                    out var proofBytes) &&
            proofBytes == proof.Length,
            "valid type-4 proof encodes");
        await client.SendToAsync(
            proof,
            SocketFlags.None,
            serverEndpoint,
            lifetime.Token);
        var confirmation = await ReceiveDatagramAsync(
            client,
            lifetime.Token);
        using var protectedClient = new SecureUdpProtectedSession(
            SecureUdpPeerRole.Client,
            proofKey,
            registeredId,
            target.ServerId,
            TimeSpan.FromSeconds(
                options.PreviousKeyEpochOverlapSeconds));
        var confirmationPayload = new byte[
            SecureUdpProtectedConstants.MaximumPayloadBytes];
        Check.True(
            protectedClient.TryUnprotect(
                confirmation,
                confirmationPayload,
                out var confirmationHeader,
                out var confirmationBytes,
                out _) &&
            confirmationHeader.MessageType ==
                SecureUdpProtectedMessageType.BindingConfirm &&
            confirmationBytes ==
                SecureUdpProtectedConstants
                    .BindingConfirmPayloadBytes &&
            confirmationPayload[..16].SequenceEqual(nonce) &&
            BinaryPrimitives.ReadUInt64BigEndian(
                confirmationPayload[16..]) == 1,
            "proof reserve binds and returns encrypted confirmation");

        var pingPayload = new byte[
            SecureUdpProtectedConstants.PingPayloadBytes];
        BinaryPrimitives.WriteUInt64BigEndian(pingPayload, 9);
        BinaryPrimitives.WriteUInt64BigEndian(
            pingPayload[8..],
            123_456);
        var ping = new byte[128];
        Check.True(
            protectedClient.TryProtect(
                SecureUdpProtectedMessageType.Ping,
                pingPayload,
                ping,
                out var pingBytes,
                out _),
            "established-session Ping protects");
        var invalidTag = ping[..pingBytes].ToArray();
        invalidTag[^1] ^= 0x80;
        await client.SendToAsync(
            invalidTag,
            SocketFlags.None,
            serverEndpoint,
            lifetime.Token);
        await client.SendToAsync(
            ping.AsMemory(0, pingBytes),
            SocketFlags.None,
            serverEndpoint,
            lifetime.Token);
        var pong = await ReceiveDatagramAsync(
            client,
            lifetime.Token);
        var pongPayload = new byte[
            SecureUdpProtectedConstants.MaximumPayloadBytes];
        Check.True(
            protectedClient.TryUnprotect(
                pong,
                pongPayload,
                out var pongHeader,
                out var pongBytes,
                out _) &&
            pongHeader.MessageType ==
                SecureUdpProtectedMessageType.Pong &&
            pongBytes ==
                SecureUdpProtectedConstants.PongPayloadBytes &&
            pongPayload[..16].SequenceEqual(pingPayload) &&
            BinaryPrimitives.ReadUInt64BigEndian(
                pongPayload[16..]) != 0 &&
            BinaryPrimitives.ReadUInt64BigEndian(
                pongPayload[24..]) != 0,
            "invalid AEAD tag cannot poison the post-authenticated session reserve");

        var admission = runtime.GetSnapshot().Admission;
        Check.True(
            admission.CurrentPackets == 4 &&
            admission.UnvalidatedPackets == 1 &&
            admission.BindingProofPackets == 1 &&
            admission.ProtectedCandidatePackets == 2 &&
            admission.ActiveAuthenticatedSessions == 1,
            "admission snapshot keeps three bounded traffic classes");

        lifetime.Cancel();
        await runTask;
        Array.Clear(proofKey);
    }

    private static async Task CheckCleanupFaultSupervisionAsync()
    {
        var time = new ManualTimeProvider();
        var options = new SecureUdpOptions
        {
            BindHost = "127.0.0.1",
            SessionCleanupIntervalSeconds = 1
        };
        await using var runtime =
            SecureUdpRuntime.CreateForLoopbackTest(
                options,
                CreateTarget(),
                time,
                static () => throw new InvalidOperationException(
                    "injected UDP maintenance failure"));
        using var lifetime = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var runTask = runtime.RunAsync(lifetime.Token);
        _ = await runtime.WaitUntilReadyAsync(lifetime.Token);
        for (var attempt = 0;
             attempt < 100 && time.ScheduledTimerCount == 0;
             attempt++)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(1),
                lifetime.Token);
        }
        Check.Equal(
            1,
            time.ScheduledTimerCount,
            "cleanup supervisor schedules one bounded maintenance timer");

        time.Advance(TimeSpan.FromSeconds(1));
        Exception? failure = null;
        try
        {
            await runTask.WaitAsync(
                TimeSpan.FromSeconds(2),
                lifetime.Token);
        }
        catch (Exception error)
        {
            failure = error;
        }

        var snapshot = runtime.GetSnapshot();
        Check.True(
            failure is InvalidOperationException &&
            failure.Message == "injected UDP maintenance failure" &&
            snapshot.State == SecureUdpRuntimeState.Faulted &&
            snapshot.FailureType == nameof(InvalidOperationException) &&
            !snapshot.IsReady,
            "maintenance failure faults the runtime and cancels its listener");
    }

    private static SecureGameTarget CreateTarget()
    {
        return new SecureGameTarget(
            "game.reborn.test",
            "game.reborn.test",
            "reborn-game",
            routePort: 7_000,
            tlsPort: 7_443,
            serverId: 100);
    }

    private static async Task<byte[]> ReceiveDatagramAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[
            SecureUdpProtectedConstants.MaximumDatagramBytes];
        var received = await socket.ReceiveFromAsync(
            buffer,
            SocketFlags.None,
            ReceiveTemplate,
            cancellationToken);
        return buffer[..received.ReceivedBytes];
    }

    private static void CheckCheckedInProfilesDisabled()
    {
        foreach (var path in new[]
        {
            "appsettings.json",
            "appsettings.docker.json"
        })
        {
            using var document = JsonDocument.Parse(
                File.ReadAllBytes(Path.GetFullPath(path)));
            Check.True(
                !document.RootElement
                    .GetProperty("secure")
                    .GetProperty("udp")
                    .GetProperty("enabled")
                    .GetBoolean(),
                $"{path} keeps UDP opt-in disabled");
        }
    }
}
