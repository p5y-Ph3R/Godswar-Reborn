using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Godswar.Server.Operations;

namespace Godswar.Server.ProtocolChecks;

internal static class ManagementHttpServerChecks
{
    private const string Token =
        "management-check-token-32-bytes!";

    public static async Task RunAsync()
    {
        Check.Equal(
            32,
            Encoding.ASCII.GetByteCount(Token),
            "management fixture token length");

        var port = ReserveLoopbackPort();
        var options = new ManagementOptions
        {
            Port = port,
            MaximumConcurrentRequests = 1,
            MaximumHeaderBytes = 512,
            MaximumResponseBytes = 1_024,
            RequestTimeoutMilliseconds = 150,
            ResponseTimeoutMilliseconds = 250
        };
        var state = new ServerOperationalState(
            ServerReadinessDependency.None);
        Check.True(state.TryMarkRunning(), "management fixture runs");
        var observations =
            new ConcurrentQueue<ManagementRequestObservation>();
        var suppliedBearerBuffers =
            new ConcurrentQueue<ReadOnlyMemory<byte>>();
        var drainStarted = 0;
        using var authenticator = new ManagementTokenAuthenticator(
            Encoding.ASCII.GetBytes(Token));
        var server = new ManagementHttpServer(
            options,
            state.GetSnapshot,
            _ => ValueTask.FromResult(new ManagementPayload(
                ManagementContentType.OpenMetricsText,
                "fixture_metric 1\n"u8.ToArray())),
            _ => ValueTask.FromResult(new ManagementPayload(
                ManagementContentType.Json,
                "{\"spans\":[]}\n"u8.ToArray())),
            suppliedToken =>
            {
                suppliedBearerBuffers.Enqueue(suppliedToken);
                return authenticator.Authenticate(suppliedToken);
            },
            _ =>
            {
                if (Interlocked.Exchange(ref drainStarted, 1) == 0)
                {
                    state.TryBeginDrain();
                    return ValueTask.FromResult(
                        ManagementDrainResult.Accepted);
                }
                return ValueTask.FromResult(
                    ManagementDrainResult.AlreadyDraining);
            },
            observations.Enqueue);
        using var stop = new CancellationTokenSource();
        var runTask = server.RunAsync(stop.Token);
        var endpoint = await server.WaitUntilStartedAsync()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Check.True(
            endpoint.Address.Equals(IPAddress.Loopback),
            "management server binds exact loopback");
        await AssertStatusAsync(
            port,
            Request("GET", "/livez"),
            200,
            "\"status\":\"live\"",
            "live alias");
        await AssertStatusAsync(
            port,
            Request("GET", "/ready"),
            200,
            "\"status\":\"ready\"",
            "ready alias");
        await AssertStatusAsync(
            port,
            Request("GET", "/metrics"),
            200,
            "fixture_metric 1",
            "bounded metrics provider");
        await AssertStatusAsync(
            port,
            Request("GET", "/traces"),
            200,
            "\"spans\":[]",
            "bounded trace provider");
        await AssertStatusAsync(
            port,
            Request("GET", "/missing-secret-value"),
            404,
            "\"status\":\"not_found\"",
            "unknown path receives fixed response",
            forbiddenText: "missing-secret-value");

        await AssertStatusAsync(
            port,
            Request("POST", "/drain"),
            401,
            "\"status\":\"unauthorized\"",
            "drain requires bearer authentication");
        await AssertStatusAsync(
            port,
            Request(
                "POST",
                "/drain",
                "Authorization: Bearer wrong-token\r\n"),
            401,
            "\"status\":\"unauthorized\"",
            "wrong drain token is rejected",
            forbiddenText: "wrong-token");
        AssertNextBearerBufferCleared(
            suppliedBearerBuffers,
            "rejected bearer buffer");
        await AssertStatusAsync(
            port,
            Request(
                "POST",
                "/drain",
                $"Authorization: Bearer {Token}\r\n"),
            202,
            "\"status\":\"draining\"",
            "authenticated drain is accepted");
        AssertNextBearerBufferCleared(
            suppliedBearerBuffers,
            "accepted bearer buffer");
        await AssertStatusAsync(
            port,
            Request("GET", "/readyz"),
            503,
            "\"reason\":\"draining\"",
            "drain removes readiness before a later probe");
        await AssertStatusAsync(
            port,
            Request(
                "POST",
                "/drain",
                $"Authorization: Bearer {Token}\r\n"),
            200,
            "\"status\":\"already_draining\"",
            "authenticated drain is idempotent");
        AssertNextBearerBufferCleared(
            suppliedBearerBuffers,
            "idempotent bearer buffer");

        await AssertStatusAsync(
            port,
            Request(
                "POST",
                "/drain",
                $"Content-Length: 1\r\n" +
                $"Authorization: Bearer {Token}\r\n",
                body: "X"),
            400,
            "\"status\":\"bad_request\"",
            "request bodies are rejected");
        await AssertStatusAsync(
            port,
            Request("GET", "/livez?detail=1"),
            400,
            "\"status\":\"bad_request\"",
            "query text is rejected rather than reflected");
        await AssertStatusAsync(
            port,
            Request("GET", "/" + new string('p', 63)),
            404,
            "\"status\":\"not_found\"",
            "maximum-length path remains finite");
        await AssertStatusAsync(
            port,
            Request("GET", "/" + new string('p', 64)),
            400,
            "\"status\":\"bad_request\"",
            "overlong path is rejected");
        await AssertStatusAsync(
            port,
            Request("HEAD", "/livez"),
            400,
            "\"status\":\"bad_request\"",
            "unsupported method is rejected");
        await AssertStatusAsync(
            port,
            Request("GET", "/livez") + Request("GET", "/readyz"),
            400,
            "\"status\":\"bad_request\"",
            "HTTP pipelining is rejected");

        var oversized =
            "GET /livez HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Fill: " +
            new string('A', 600) +
            "\r\n\r\n";
        await AssertOversizedRejectedAsync(port, oversized);

        await CheckSlowAndOverloadedClientsAsync(port);
        Check.True(
            observations.Any(observation =>
                observation.Route == ManagementRoute.Drain &&
                observation.Outcome ==
                    ManagementRequestOutcome.Unauthorized),
            "observation hook records finite drain/auth outcome");
        Check.True(
            observations.Any(observation =>
                observation.Route == ManagementRoute.Unknown &&
                observation.Outcome ==
                    ManagementRequestOutcome.Overloaded),
            "observation hook records overload without raw client data");
        Check.True(
            observations.Any(observation =>
                observation.Route == ManagementRoute.Unknown &&
                observation.Outcome ==
                    ManagementRequestOutcome.HeadersTooLarge),
            "observation hook records bounded oversized-header rejection");

        stop.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
        await CheckThrowsAsync<InvalidOperationException>(
            server.RunAsync(CancellationToken.None),
            "management listener owns one lifecycle");
        Check.True(
            suppliedBearerBuffers.IsEmpty,
            "every captured bearer buffer was checked and released");
    }

    private static void AssertNextBearerBufferCleared(
        ConcurrentQueue<ReadOnlyMemory<byte>> suppliedBearerBuffers,
        string description)
    {
        Check.True(
            suppliedBearerBuffers.TryDequeue(out var suppliedToken),
            $"{description} was captured");
        Check.True(
            !suppliedToken.IsEmpty,
            $"{description} retains its bounded allocation");
        foreach (var value in suppliedToken.Span)
        {
            Check.Equal(
                (byte)0,
                value,
                $"{description} is zeroed after request handling");
        }
    }

    private static async Task CheckSlowAndOverloadedClientsAsync(int port)
    {
        using var slow = new TcpClient();
        await slow.ConnectAsync(
            IPAddress.Loopback,
            port,
            CancellationToken.None);
        await Task.Delay(40);

        using var overloaded = new TcpClient();
        await overloaded.ConnectAsync(
            IPAddress.Loopback,
            port,
            CancellationToken.None);
        using var overloadedDeadline =
            new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var closed = await overloaded.GetStream().ReadAsync(
            new byte[1],
            overloadedDeadline.Token);
        Check.Equal(
            0,
            closed,
            "connection above management concurrency is closed");

        var slowResponse = await ReadResponseAsync(
            slow,
            TimeSpan.FromSeconds(2));
        Check.Equal(
            408,
            slowResponse.StatusCode,
            "silent management request reaches bounded deadline");
    }

    private static string Request(
        string method,
        string path,
        string extraHeaders = "",
        string body = "") =>
        $"{method} {path} HTTP/1.1\r\n" +
        "Host: 127.0.0.1\r\n" +
        extraHeaders +
        "Connection: close\r\n\r\n" +
        body;

    private static async Task AssertStatusAsync(
        int port,
        string request,
        int expectedStatus,
        string expectedText,
        string description,
        string? forbiddenText = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(
            IPAddress.Loopback,
            port,
            CancellationToken.None);
        using var deadline =
            new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var bytes = Encoding.ASCII.GetBytes(request);
        await client.GetStream().WriteAsync(bytes, deadline.Token);
        await client.GetStream().FlushAsync(deadline.Token);
        var response = await ReadResponseAsync(
            client,
            TimeSpan.FromSeconds(2));

        Check.Equal(
            expectedStatus,
            response.StatusCode,
            $"{description} status");
        Check.True(
            response.Text.Contains(
                expectedText,
                StringComparison.Ordinal),
            $"{description} fixed content");
        if (forbiddenText is not null)
        {
            Check.True(
                !response.Text.Contains(
                    forbiddenText,
                    StringComparison.Ordinal),
                $"{description} does not reflect input");
        }
        Check.True(
            response.Bytes.Length <= 1_024,
            $"{description} respects response cap");
        Check.True(
            response.Text.Contains(
                "Connection: close\r\n",
                StringComparison.Ordinal),
            $"{description} closes after one request");
    }

    private static async Task AssertOversizedRejectedAsync(
        int port,
        string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(
            IPAddress.Loopback,
            port,
            CancellationToken.None);
        using var deadline =
            new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var bytes = Encoding.ASCII.GetBytes(request);
        await client.GetStream().WriteAsync(bytes, deadline.Token);
        await client.GetStream().FlushAsync(deadline.Token);
        try
        {
            var response = await ReadResponseAsync(
                client,
                TimeSpan.FromSeconds(2));
            Check.Equal(
                431,
                response.StatusCode,
                "oversized header fixed status when transport permits ACK");
            Check.True(
                response.Bytes.Length <= 1_024,
                "oversized header response remains bounded");
        }
        catch (IOException)
        {
            // Windows may reset a socket that still has oversized unread
            // request bytes. A prompt close is also an accepted rejection.
        }
    }

    private static async Task<RawResponse> ReadResponseAsync(
        TcpClient client,
        TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var buffer = new MemoryStream();
        var chunk = new byte[512];
        while (true)
        {
            var read = await client.GetStream().ReadAsync(
                chunk,
                deadline.Token);
            if (read == 0)
            {
                break;
            }
            buffer.Write(chunk, 0, read);
            if (buffer.Length > 1_024)
            {
                throw new InvalidOperationException(
                    "Management fixture response exceeded its bound.");
            }
        }

        var bytes = buffer.ToArray();
        var text = Encoding.UTF8.GetString(bytes);
        var firstLineEnd = text.IndexOf(
            "\r\n",
            StringComparison.Ordinal);
        if (firstLineEnd < 0)
        {
            throw new InvalidOperationException(
                "Management fixture response had no status line.");
        }
        var statusParts = text[..firstLineEnd].Split(' ');
        if (statusParts.Length < 2 ||
            !int.TryParse(statusParts[1], out var statusCode))
        {
            throw new InvalidOperationException(
                "Management fixture response had an invalid status.");
        }
        return new RawResponse(statusCode, text, bytes);
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task CheckThrowsAsync<TException>(
        Task task,
        string description)
        where TException : Exception
    {
        try
        {
            await task;
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{typeof(TException).Name}.");
    }

    private sealed record RawResponse(
        int StatusCode,
        string Text,
        byte[] Bytes);
}
