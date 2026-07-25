using System.Net.Security;
using System.IO.Pipes;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureTlsTransportChecks
{
    private static TlsMuxLegacyTransportFactory CreateFactory(
        SecureTlsTestCertificate certificate,
        NetworkRuntimeOptions options,
        TlsHandshakeGate gate,
        TimeProvider? timeProvider = null)
    {
        return new TlsMuxLegacyTransportFactory(
            new SecureNetworkOptions(),
            options,
            certificate.Context,
            gate,
            timeProvider);
    }

    private static async Task<TlsPair> StartPairAsync(
        TlsMuxLegacyTransportFactory factory,
        NetworkEndpointRole role,
        TimeProvider? timeProvider = null)
    {
        var pipeName = $"reborn-slice6-{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        var acceptTask = server.WaitForConnectionAsync();
        await client.ConnectAsync(CancellationToken.None);
        await acceptTask;
        var acceptedTimestamp =
            (timeProvider ?? TimeProvider.System).GetTimestamp();
        var transportTask = Task.Run(
            async () => await factory.CreateForStreamAsync(
                server,
                role,
                acceptedTimestamp,
                CancellationToken.None));
        var clientStream = new SslStream(
            client,
            leaveInnerStreamOpen: false);
        return new TlsPair(
            client,
            clientStream,
            transportTask);
    }

    private static async Task<SecureServerPreface>
        AuthenticateAndPrefaceAsync(
            SslStream clientStream,
            SecureTlsTestCertificate certificate,
            SecureEndpointRole role,
            string targetHost = "login.reborn.test")
    {
        await clientStream.AuthenticateAsClientAsync(
            certificate.CreateClientOptions(targetHost));
        Check.True(clientStream.IsEncrypted, "client TLS is encrypted");
        Check.True(clientStream.IsSigned, "client TLS has integrity");
        Check.True(
            SecureTlsPolicy.IsCipherSuiteAllowed(
                clientStream.NegotiatedCipherSuite),
            "client negotiated an allowed cipher suite");
        await clientStream.WriteAsync(EncodeClientPreface(role));
        await clientStream.FlushAsync();
        var response = await ReadExactlyAsync(
            clientStream,
            SecureProtocolConstants.ServerPrefaceBytes);
        Check.True(
            SecurePrefaceCodec.TryDecodeServer(
                response,
                role,
                out var preface),
            "server returns canonical secure preface");
        return preface!;
    }

    private static byte[] EncodeClientPreface(
        SecureEndpointRole role,
        byte[]? buildHash = null)
    {
        buildHash ??= Convert.FromHexString(
            SecureNetworkOptions.PredecessorOriginSha256);
        var instanceId = Enumerable.Range(
                1,
                SecureProtocolConstants.ClientInstanceIdBytes)
            .Select(static value => checked((byte)value))
            .ToArray();
        var preface = new SecureClientPreface(
            role,
            instanceId,
            buildHash);
        var bytes = new byte[SecureProtocolConstants.ClientPrefaceBytes];
        Check.True(
            SecurePrefaceCodec.TryEncodeClient(
                preface,
                bytes,
                out var written) &&
            written == bytes.Length,
            "test client preface encodes");
        return bytes;
    }

    private static async Task WriteFrameAsync(
        SslStream stream,
        SecureEndpointRole role,
        SecureFrameType type,
        ulong sequence,
        byte[] payload)
    {
        var bytes = new byte[
            SecureProtocolConstants.FrameHeaderBytes +
            payload.Length];
        Check.True(
            SecureFrameCodec.TryEncode(
                new SecureFrameHeader(
                    checked((uint)payload.Length),
                    type,
                    sequence),
                payload,
                role,
                SecureFrameDirection.ClientToServer,
                bytes,
                out var written) &&
            written == bytes.Length,
            "test secure frame encodes");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<DecodedFrame> ReadFrameAsync(
        SslStream stream,
        SecureEndpointRole role,
        SecureFrameDirection direction,
        ulong expectedSequence)
    {
        var headerBytes = await ReadExactlyAsync(
            stream,
            SecureProtocolConstants.FrameHeaderBytes);
        Check.True(
            SecureFrameCodec.TryDecodeHeader(
                headerBytes,
                role,
                direction,
                expectedSequence,
                out var header),
            "received secure frame header decodes");
        var payload = await ReadExactlyAsync(
            stream,
            checked((int)header.PayloadLength));
        return new DecodedFrame(header, payload);
    }

    private static async Task<byte[]> ReadExactlyAsync(
        Stream stream,
        int count)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var result = new byte[count];
        var offset = 0;
        while (offset < result.Length)
        {
            var read = await stream.ReadAsync(
                result.AsMemory(offset),
                timeout.Token);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"TLS test peer closed after {offset} of {count} bytes.");
            }
            offset += read;
        }
        return result;
    }

    private static async Task<byte[]> ReadExactlyFromTransportAsync(
        ILegacyByteTransport transport,
        int count)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < result.Length)
        {
            var read = await transport.ReadAsync(
                result.AsMemory(offset),
                CancellationToken.None);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"TLS mux closed after {offset} of {count} bytes.");
            }
            offset += read;
        }
        return result;
    }

    private static async Task<TException> ExpectExceptionAsync<TException>(
        Func<Task> action,
        string description)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException error)
        {
            return error;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected {typeof(TException).Name}.");
    }

    private static async Task<bool> WaitForTlsCloseAsync(SslStream stream)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        try
        {
            return await stream.ReadAsync(
                new byte[1],
                timeout.Token) == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (System.Security.Authentication.AuthenticationException)
        {
            return true;
        }
    }

    private readonly record struct DecodedFrame(
        SecureFrameHeader Header,
        byte[] Payload);

    private sealed class TlsPair(
        IDisposable client,
        SslStream clientStream,
        Task<ILegacyByteTransport> transportTask) :
        IAsyncDisposable
    {
        public SslStream ClientStream { get; } = clientStream;

        public Task<ILegacyByteTransport> TransportTask { get; } =
            transportTask;

        public async ValueTask DisposeAsync()
        {
            await ClientStream.DisposeAsync();
            client.Dispose();
            if (TransportTask.IsCompletedSuccessfully)
            {
                await TransportTask.Result.DisposeAsync();
            }
            else
            {
                try
                {
                    await TransportTask;
                }
                catch
                {
                }
            }
        }
    }
}
