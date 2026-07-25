using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureTlsTransportChecks
{
    public static async Task RunAsync()
    {
        CheckOptionsAndCertificatePolicy();
        await CheckLoadedCertificateRoundTripAsync();
        CheckPrefacePolicy();
        // The gate is exercised after live TLS checks so Schannel setup is not
        // coupled to a preceding cancellation test on this process.
        await CheckLoginTlsMuxRoundTripAsync();
        await CheckAuthenticatedHeartbeatAsync();
        await CheckHeartbeatWriteDeadlineAsync();
        await CheckUnsupportedBuildFailsClosedAsync();
        await CheckWrongAlpnFailsClosedAsync();
        await CheckHandshakeDeadlineAsync();
        await CheckGameFailsClosedBeforeHandlerAsync();
        await CheckGameTicketBindRoundTripAsync();
        await CheckHandshakeGateAsync();
    }

    private static void CheckOptionsAndCertificatePolicy()
    {
        var disabled = new SecureNetworkOptions();
        disabled.NormalizeAndValidate("appsettings.json", 5999, 7000);
        Check.True(!disabled.Enabled, "secure listeners default disabled");
        Check.Equal(6599, disabled.Login.Port, "secure login uses a separate default port");
        Check.Equal(7443, disabled.Game.Port, "secure game uses a separate default port");
        Check.True(
            disabled.CertificatePassword.Length == 0,
            "certificate password is absent from serialized defaults");
        Check.Equal(
            "rejected",
            SecureFrameOutcome.Rejected.ToMetricTag(),
            "rejected secure binds have a distinct telemetry outcome");
        Check.Throws<InvalidDataException>(
            () => SecureNetworkOptions.ValidateSecureRuntime(
                new NetworkRuntimeOptions
                {
                    IngressQueueBytes =
                        SecureProtocolConstants.MaximumPayloadBytes - 1
                }),
            "secure ingress accepts one maximum outer payload");
        Check.Throws<InvalidDataException>(
            () => SecureNetworkOptions.ValidateSecureRuntime(
                new NetworkRuntimeOptions
                {
                    ControlQueueBytes = 7
                }),
            "secure control queue accepts one Pong");

        var inertDisabled = new SecureNetworkOptions
        {
            Login = new SecureEndpointOptions
            {
                BindHost = "  ",
                Port = 5999,
                DnsHost = " NOT A DNS NAME "
            },
            Game = new SecureEndpointOptions
            {
                BindHost = " public.example.invalid ",
                Port = 7000,
                DnsHost = string.Empty
            },
            CertificatePath = " missing-development-certificate.pfx ",
            AllowedOriginSha256 = ["not-a-sha256"]
        };
        inertDisabled.NormalizeAndValidate(
            "appsettings.json",
            rawLoginPort: 5999,
            rawGamePort: 7000);
        Check.Equal(
            5999,
            inertDisabled.Login.Port,
            "disabled secure profile tolerates a raw-login port collision");
        Check.Equal(
            7000,
            inertDisabled.Game.Port,
            "disabled secure profile tolerates a raw-game port collision");
        Check.Equal(
            "not-a-sha256",
            inertDisabled.AllowedOriginSha256[0],
            "disabled secure profile does not validate unused build hashes");

        using var certificate = SecureTlsTestCertificate.Create();
        SecureServerCertificate.ValidateLeaf(
            certificate.Server,
            "login.reborn.test",
            "game.reborn.test",
            TimeProvider.System);
        Check.Throws<InvalidDataException>(
            () => SecureServerCertificate.ValidateLeaf(
                certificate.Server,
                "login.reborn.test",
                "missing.reborn.test",
                TimeProvider.System),
            "certificate SAN must cover both secure roles");

        var tlsOptions = SecureTlsPolicy.CreateServerOptions(
            certificate.Context);
        Check.True(!tlsOptions.AllowRenegotiation, "TLS renegotiation is disabled");
        Check.Equal(
            1,
            tlsOptions.ApplicationProtocols?.Count ?? 0,
            "TLS policy offers exactly one ALPN");
        Check.True(
            OperatingSystem.IsWindows()
                ? tlsOptions.CipherSuitesPolicy is null
                : tlsOptions.CipherSuitesPolicy is not null,
            "cipher offer policy follows platform support");

        var fakeCertificate = Path.GetTempFileName();
        try
        {
            var missingPassword = new SecureNetworkOptions
            {
                Enabled = true,
                CertificatePath = fakeCertificate
            };
            Check.Throws<InvalidDataException>(
                () => missingPassword.NormalizeAndValidate(
                    "appsettings.json",
                    5999,
                    7000),
                "enabled secure profile requires a private certificate password");

            var publicBind = new SecureNetworkOptions
            {
                Enabled = true,
                CertificatePath = fakeCertificate,
                CertificatePassword = "private-test-password"
            };
            publicBind.Login.BindHost = "0.0.0.0";
            Check.Throws<InvalidDataException>(
                () => publicBind.NormalizeAndValidate(
                    "appsettings.json",
                    5999,
                    7000),
                "development TLS listener cannot bind publicly");
        }
        finally
        {
            File.Delete(fakeCertificate);
        }
    }

    private static async Task CheckLoadedCertificateRoundTripAsync()
    {
        using var fixture = SecureTlsTestCertificate.Create();
        const string password = "slice-6-loader-test-only";
        var path = Path.Combine(
            Path.GetTempPath(),
            $"reborn-slice6-{Guid.NewGuid():N}.pfx");
        var pfx = fixture.ExportPfx(password);
        try
        {
            await File.WriteAllBytesAsync(path, pfx);
            CryptographicOperations.ZeroMemory(pfx);

            var secureOptions = new SecureNetworkOptions
            {
                Enabled = true,
                CertificatePath = path,
                CertificatePassword = password
            };
            using var loaded = SecureServerCertificate.Load(secureOptions);
            Check.True(
                loaded.Context.TargetCertificate.RawData
                    .SequenceEqual(fixture.Server.RawData),
                "PKCS#12 loader selects the sole private leaf");

            var runtimeOptions = CreateRuntimeOptions();
            using var gate = new TlsHandshakeGate(1);
            var factory = new TlsMuxLegacyTransportFactory(
                secureOptions,
                runtimeOptions,
                loaded.Context,
                gate);
            await using var pair = await StartPairAsync(
                factory,
                NetworkEndpointRole.Login);
            var preface = await AuthenticateAndPrefaceAsync(
                pair.ClientStream,
                fixture,
                SecureEndpointRole.Login);
            Check.Equal(
                (int)SecureServerPrefaceStatus.Ok,
                (int)preface.Status,
                "loaded PKCS#12 chain completes a TLS login preface");
            _ = await pair.TransportTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
            File.Delete(path);
        }
    }

    private static void CheckPrefacePolicy()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            SecureNetworkOptions.PredecessorOriginSha256
        };
        var bytes = EncodeClientPreface(SecureEndpointRole.Login);
        var outcome = SecurePrefacePolicy.Evaluate(
            bytes,
            SecureEndpointRole.Login,
            allowed,
            out var preface);
        Check.Equal(
            (int)SecurePrefaceOutcome.Accepted,
            (int)outcome,
            "known build and correct role pass preface policy");
        Check.True(preface is not null, "accepted preface is decoded");

        bytes[12] = (byte)SecureEndpointRole.Game;
        Check.Equal(
            (int)SecurePrefaceOutcome.WrongEndpoint,
            (int)SecurePrefacePolicy.Evaluate(
                bytes,
                SecureEndpointRole.Login,
                allowed,
                out _),
            "wrong preface role has a finite outcome");

        bytes = EncodeClientPreface(
            SecureEndpointRole.Login,
            RandomNumberGenerator.GetBytes(
                SecureProtocolConstants.BuildHashBytes));
        Check.Equal(
            (int)SecurePrefaceOutcome.UnsupportedBuild,
            (int)SecurePrefacePolicy.Evaluate(
                bytes,
                SecureEndpointRole.Login,
                allowed,
                out _),
            "unknown build has a finite outcome");
    }

    private static async Task CheckHandshakeGateAsync()
    {
        using var gate = new TlsHandshakeGate(1);
        using var first = await gate.AcquireAsync(CancellationToken.None);
        Check.Equal(0, gate.AvailableSlots, "handshake gate consumes its only slot");
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));
        await ExpectExceptionAsync<OperationCanceledException>(
            async () =>
            {
                using var ignored = await gate.AcquireAsync(timeout.Token);
            },
            "second TLS handshake cannot bypass the global gate");
        first.Dispose();
        Check.Equal(1, gate.AvailableSlots, "handshake slot returns exactly once");
    }

    private static async Task CheckLoginTlsMuxRoundTripAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var options = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(
            options.MaxConcurrentTlsHandshakes);
        var factory = CreateFactory(certificate, options, gate);
        await using var pair = await StartPairAsync(
            factory,
            NetworkEndpointRole.Login);

        SecureServerPreface serverPreface;
        try
        {
            serverPreface = await AuthenticateAndPrefaceAsync(
                pair.ClientStream,
                certificate,
                SecureEndpointRole.Login);
        }
        catch (Exception clientError)
        {
            var serverError = await CaptureFailureAsync(
                async () => await pair.TransportTask);
            throw new AggregateException(
                $"TLS login fixture failed: client={clientError}; server={serverError}",
                [clientError, serverError ?? new Exception("Server completed without error.")]);
        }
        Check.Equal(
            (int)SecureServerPrefaceStatus.Ok,
            (int)serverPreface.Status,
            "valid login TLS preface is accepted");
        var transport = await pair.TransportTask;
        Check.True(
            transport is ISecureLegacyByteTransport,
            "TLS mux is marked secure for diagnostic suppression");

        var inbound = RandomNumberGenerator.GetBytes(300);
        await WriteFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Login,
            SecureFrameType.LegacyBytes,
            sequence: 1,
            inbound);
        var actual = await ReadExactlyFromTransportAsync(
            transport,
            inbound.Length);
        Check.True(
            inbound.SequenceEqual(actual),
            "TLS mux exposes framed XOR bytes as one unchanged stream");

        var outbound = RandomNumberGenerator.GetBytes(20_000);
        var writeTask = transport
            .WriteAsync(outbound, CancellationToken.None)
            .AsTask();
        var firstFrame = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 1);
        var secondFrame = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 2);
        await writeTask;
        Check.Equal(
            SecureProtocolConstants.MaximumPayloadBytes,
            firstFrame.Payload.Length,
            "oversized legacy write is split at outer payload ceiling");
        Check.True(
            firstFrame.Payload
                .Concat(secondFrame.Payload)
                .SequenceEqual(outbound),
            "split outer frames retain exact byte order");

        await using var session = new ClientSession(transport);
        Check.True(
            !session.AllowsPayloadDiagnostics,
            "secure ClientSession suppresses endpoint and payload diagnostics");
    }

    private static NetworkRuntimeOptions CreateRuntimeOptions()
    {
        return new NetworkRuntimeOptions
        {
            TlsHandshakeTimeoutMilliseconds = 5_000,
            SecurePrefaceTimeoutMilliseconds = 1_000,
            GameBindTimeoutMilliseconds = 1_000,
            QueueAdmissionTimeoutMilliseconds = 250
        };
    }

    private static async Task CheckAuthenticatedHeartbeatAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var timeProvider = new ManualTimeProvider();
        var options = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(1);
        var factory = CreateFactory(
            certificate,
            options,
            gate,
            timeProvider);
        await using var pair = await StartPairAsync(
            factory,
            NetworkEndpointRole.Login,
            timeProvider: timeProvider);
        await AuthenticateAndPrefaceAsync(
            pair.ClientStream,
            certificate,
            SecureEndpointRole.Login);
        var transport = (TlsMuxLegacyTransport)await pair.TransportTask;
        Check.Equal(
            options.ControlQueueItems,
            transport.ControlSnapshot.CapacityItems,
            "heartbeat control queue uses configured item capacity");
        Check.Equal(
            (long)options.ControlQueueBytes,
            transport.ControlSnapshot.CapacityBytes,
            "heartbeat control queue uses configured byte capacity");

        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Login,
            timeProvider);
        session.MarkAuthenticated();
        await WaitForScheduledTimerAsync(timeProvider);
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        var ping = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 1);
        Check.Equal(
            (int)SecureFrameType.Ping,
            (int)ping.Header.Type,
            "authenticated send-idle produces the sole-server Ping");
        await WriteFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Login,
            SecureFrameType.Pong,
            sequence: 1,
            ping.Payload);
        await WaitForPongProcessedAsync(transport);

        await WaitForScheduledTimerAsync(timeProvider);
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        var secondPing = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 2);
        Check.Equal(
            (int)SecureFrameType.Ping,
            (int)secondPing.Header.Type,
            "matching Pong clears the outstanding heartbeat");

        secondPing.Payload[0] ^= 0x80;
        await WriteFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Login,
            SecureFrameType.Pong,
            sequence: 2,
            secondPing.Payload);
        Check.True(
            await WaitForTlsCloseAsync(pair.ClientStream),
            "wrong Pong fails the secure connection");
    }

    private static async Task WaitForScheduledTimerAsync(
        ManualTimeProvider timeProvider)
    {
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        while (timeProvider.ScheduledTimerCount == 0)
        {
            await Task.Delay(1, deadline.Token);
        }
    }

    private static async Task WaitForPongProcessedAsync(
        TlsMuxLegacyTransport transport)
    {
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        while (transport.PingOutstanding)
        {
            await Task.Delay(1, deadline.Token);
        }
    }

    private static async Task CheckHeartbeatWriteDeadlineAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var timeProvider = new ManualTimeProvider();
        var options = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(1);
        var factory = CreateFactory(
            certificate,
            options,
            gate,
            timeProvider);
        await using var pair = await StartPairAsync(
            factory,
            NetworkEndpointRole.Login,
            timeProvider: timeProvider);
        await AuthenticateAndPrefaceAsync(
            pair.ClientStream,
            certificate,
            SecureEndpointRole.Login);
        var transport = (TlsMuxLegacyTransport)await pair.TransportTask;
        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Login,
            timeProvider);
        session.MarkAuthenticated();
        await Task.Yield();

        var blockedWrite = transport.WriteAsync(
            new byte[2 * 1024 * 1024],
            CancellationToken.None).AsTask();
        await Task.Delay(25);
        Check.True(
            !blockedWrite.IsCompleted,
            "flow-controlled TLS peer blocks the serialized writer");

        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(25);
        timeProvider.Advance(options.ReliableWriteTimeout);

        var completed = await Task.WhenAny(
            blockedWrite,
            Task.Delay(TimeSpan.FromSeconds(5)));
        Check.True(
            ReferenceEquals(completed, blockedWrite),
            "heartbeat write admission deadline closes a stalled TLS writer");
        try
        {
            await blockedWrite;
            throw new InvalidOperationException(
                "Assertion failed: stalled TLS writer unexpectedly succeeded.");
        }
        catch (InvalidOperationException error)
            when (!error.Message.StartsWith(
                "Assertion failed:",
                StringComparison.Ordinal))
        {
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }
}
