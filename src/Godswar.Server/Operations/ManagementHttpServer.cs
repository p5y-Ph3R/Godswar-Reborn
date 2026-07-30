using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Godswar.Server.Operations;

/// <summary>
/// A deliberately small, loopback-only, one-request HTTP/1.1 management
/// surface. It is not a general application HTTP server.
/// </summary>
internal sealed class ManagementHttpServer
{
    private const string JsonContentType =
        "application/json; charset=utf-8";
    private const string MetricsContentType =
        "text/plain; version=0.0.4; charset=utf-8";

    private static readonly ReadOnlyMemory<byte> EmptyJson =
        "{}\n"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> BadRequestJson =
        "{\"status\":\"bad_request\"}\n"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> NotFoundJson =
        "{\"status\":\"not_found\"}\n"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> UnauthorizedJson =
        "{\"status\":\"unauthorized\"}\n"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> UnavailableJson =
        "{\"status\":\"unavailable\"}\n"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> TooLargeJson =
        "{\"status\":\"response_too_large\"}\n"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> AcceptedJson =
        "{\"status\":\"draining\"}\n"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> AlreadyDrainingJson =
        "{\"status\":\"already_draining\"}\n"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> DrainRejectedJson =
        "{\"status\":\"drain_rejected\"}\n"u8.ToArray();

    private readonly ConcurrentDictionary<long, Task> _activeRequests = [];
    private readonly ManagementDrainAuthenticator _authenticateDrain;
    private readonly ManagementDrainHandler _drain;
    private readonly IPAddress _listenAddress;
    private readonly ManagementOptions _options;
    private readonly Func<ServerOperationalSnapshot> _operationalSnapshot;
    private readonly ManagementPayloadProvider _metrics;
    private readonly ManagementRequestObserver? _observeRequest;
    private readonly ManagementPayloadProvider _traces;
    private readonly SemaphoreSlim _requestSlots;
    private readonly TaskCompletionSource<IPEndPoint> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextRequestId;
    private int _runStarted;

    public ManagementHttpServer(
        ManagementOptions options,
        Func<ServerOperationalSnapshot> operationalSnapshot,
        ManagementPayloadProvider metrics,
        ManagementPayloadProvider traces,
        ManagementDrainAuthenticator authenticateDrain,
        ManagementDrainHandler drain,
        ManagementRequestObserver? observeRequest = null)
    {
        _options = options ??
            throw new ArgumentNullException(nameof(options));
        _listenAddress = options.Validate();
        _operationalSnapshot = operationalSnapshot ??
            throw new ArgumentNullException(nameof(operationalSnapshot));
        _metrics = metrics ??
            throw new ArgumentNullException(nameof(metrics));
        _traces = traces ??
            throw new ArgumentNullException(nameof(traces));
        _authenticateDrain = authenticateDrain ??
            throw new ArgumentNullException(nameof(authenticateDrain));
        _drain = drain ??
            throw new ArgumentNullException(nameof(drain));
        _observeRequest = observeRequest;
        _requestSlots = new SemaphoreSlim(
            options.MaximumConcurrentRequests,
            options.MaximumConcurrentRequests);
    }

    public Task<IPEndPoint> WaitUntilStartedAsync(
        CancellationToken cancellationToken = default) =>
        _started.Task.WaitAsync(cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "A management HTTP server can run only once.");
        }

        var listener = new TcpListener(_listenAddress, _options.Port);
        try
        {
            listener.Start(_options.ListenBacklog);
            _started.TrySetResult((IPEndPoint)listener.LocalEndpoint);

            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(
                    cancellationToken);
                if (!_requestSlots.Wait(0))
                {
                    Observe(
                        ManagementRoute.Unknown,
                        ManagementRequestOutcome.Overloaded);
                    client.Dispose();
                    continue;
                }

                var requestId = Interlocked.Increment(
                    ref _nextRequestId);
                var task = HandleClientAsync(
                    client,
                    cancellationToken);
                _activeRequests.TryAdd(requestId, task);
                _ = ObserveRequestAsync(requestId, task);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            throw;
        }
        finally
        {
            listener.Stop();
            await ObserveActiveRequestsAsync();
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            var read = default(ManagementHttpRequestReadResult);
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint remote ||
                    !IPAddress.IsLoopback(remote.Address))
                {
                    return;
                }

                using var stream = client.GetStream();
                read =
                    await ManagementHttpProtocol.ReadRequestAsync(
                        stream,
                        _options.MaximumHeaderBytes,
                        _options.RequestTimeout,
                        cancellationToken);
                var route = read.Status == ManagementHttpReadStatus.Success
                    ? RouteOf(read.Request)
                    : ManagementRoute.Unknown;
                var response = read.Status switch
                {
                    ManagementHttpReadStatus.Success =>
                        await RouteAsync(
                            read.Request,
                            cancellationToken),
                    ManagementHttpReadStatus.HeadersTooLarge =>
                        FixedResponse(
                            431,
                            "Request Header Fields Too Large",
                            BadRequestJson),
                    ManagementHttpReadStatus.RequestTimeout =>
                        FixedResponse(
                            408,
                            "Request Timeout",
                            BadRequestJson),
                    _ => FixedResponse(
                        400,
                        "Bad Request",
                        BadRequestJson)
                };
                var outcome = OutcomeOf(
                    read.Status,
                    response.StatusCode,
                    route);

                try
                {
                    await ManagementHttpProtocol.WriteResponseAsync(
                        stream,
                        response,
                        _options.MaximumResponseBytes,
                        _options.ResponseTimeout,
                        cancellationToken);
                }
                catch (ManagementResponseTooLargeException)
                {
                    response = FixedResponse(
                        503,
                        "Service Unavailable",
                        TooLargeJson);
                    outcome = ManagementRequestOutcome.Unavailable;
                    await ManagementHttpProtocol.WriteResponseAsync(
                        stream,
                        response,
                        _options.MaximumResponseBytes,
                        _options.ResponseTimeout,
                        cancellationToken);
                }
                Observe(route, outcome);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                read.Request.ClearBearerToken();
            }
        }
    }

    private async ValueTask<ManagementHttpResponse> RouteAsync(
        ManagementHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Method == "GET")
        {
            return request.Path switch
            {
                "/live" or "/livez" => LiveResponse(),
                "/ready" or "/readyz" => ReadyResponse(),
                "/metrics" => await PayloadResponseAsync(
                    _metrics,
                    cancellationToken),
                "/traces" => await PayloadResponseAsync(
                    _traces,
                    cancellationToken),
                _ => FixedResponse(
                    404,
                    "Not Found",
                    NotFoundJson)
            };
        }

        if (request.Method == "POST" && request.Path == "/drain")
        {
            if (request.BearerToken.IsEmpty ||
                !_authenticateDrain(request.BearerToken))
            {
                return new ManagementHttpResponse(
                    401,
                    "Unauthorized",
                    JsonContentType,
                    UnauthorizedJson,
                    IncludeBearerChallenge: true);
            }

            try
            {
                var result = await InvokeWithDeadlineAsync(
                    _drain,
                    cancellationToken);
                return result switch
                {
                    ManagementDrainResult.Accepted =>
                        FixedResponse(202, "Accepted", AcceptedJson),
                    ManagementDrainResult.AlreadyDraining =>
                        FixedResponse(200, "OK", AlreadyDrainingJson),
                    _ => FixedResponse(
                        409,
                        "Conflict",
                        DrainRejectedJson)
                };
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException ||
                    !cancellationToken.IsCancellationRequested)
            {
                return FixedResponse(
                    503,
                    "Service Unavailable",
                    UnavailableJson);
            }
        }

        return FixedResponse(
            404,
            "Not Found",
            NotFoundJson);
    }

    private ManagementHttpResponse LiveResponse()
    {
        var snapshot = _operationalSnapshot();
        var body = Encoding.UTF8.GetBytes(
            snapshot.IsLive
                ? $"{{\"status\":\"live\",\"phase\":\"{snapshot.Phase.ToProtocolValue()}\"}}\n"
                : $"{{\"status\":\"not_live\",\"phase\":\"{snapshot.Phase.ToProtocolValue()}\"}}\n");
        return FixedResponse(
            snapshot.IsLive ? 200 : 503,
            snapshot.IsLive ? "OK" : "Service Unavailable",
            body);
    }

    private ManagementHttpResponse ReadyResponse()
    {
        var snapshot = _operationalSnapshot();
        var body = Encoding.UTF8.GetBytes(
            snapshot.IsReady
                ? "{\"status\":\"ready\",\"reason\":\"none\"}\n"
                : $"{{\"status\":\"not_ready\",\"reason\":\"{snapshot.ReadinessReason.ToProtocolValue()}\"}}\n");
        return FixedResponse(
            snapshot.IsReady ? 200 : 503,
            snapshot.IsReady ? "OK" : "Service Unavailable",
            body);
    }

    private async ValueTask<ManagementHttpResponse> PayloadResponseAsync(
        ManagementPayloadProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await InvokeWithDeadlineAsync(
                provider,
                cancellationToken);
            if (!Enum.IsDefined(payload.ContentType))
            {
                return FixedResponse(
                    503,
                    "Service Unavailable",
                    UnavailableJson);
            }
            return new ManagementHttpResponse(
                200,
                "OK",
                payload.ContentType == ManagementContentType.OpenMetricsText
                    ? MetricsContentType
                    : JsonContentType,
                payload.Content.IsEmpty ? EmptyJson : payload.Content);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
        {
            return FixedResponse(
                503,
                "Service Unavailable",
                UnavailableJson);
        }
    }

    private async ValueTask<ManagementPayload> InvokeWithDeadlineAsync(
        ManagementPayloadProvider operation,
        CancellationToken cancellationToken)
    {
        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        deadline.CancelAfter(_options.ResponseTimeout);
        return await operation(deadline.Token)
            .AsTask()
            .WaitAsync(
                _options.ResponseTimeout,
                cancellationToken);
    }

    private async ValueTask<ManagementDrainResult> InvokeWithDeadlineAsync(
        ManagementDrainHandler operation,
        CancellationToken cancellationToken)
    {
        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        deadline.CancelAfter(_options.ResponseTimeout);
        return await operation(deadline.Token)
            .AsTask()
            .WaitAsync(
                _options.ResponseTimeout,
                cancellationToken);
    }

    private static ManagementHttpResponse FixedResponse(
        int statusCode,
        string reasonPhrase,
        ReadOnlyMemory<byte> body) =>
        new(
            statusCode,
            reasonPhrase,
            JsonContentType,
            body);

    private static ManagementRoute RouteOf(
        ManagementHttpRequest request) =>
        (request.Method, request.Path) switch
        {
            ("GET", "/live" or "/livez") => ManagementRoute.Live,
            ("GET", "/ready" or "/readyz") => ManagementRoute.Ready,
            ("GET", "/metrics") => ManagementRoute.Metrics,
            ("GET", "/traces") => ManagementRoute.Traces,
            ("POST", "/drain") => ManagementRoute.Drain,
            _ => ManagementRoute.Unknown
        };

    private static ManagementRequestOutcome OutcomeOf(
        ManagementHttpReadStatus readStatus,
        int statusCode,
        ManagementRoute route)
    {
        if (readStatus == ManagementHttpReadStatus.HeadersTooLarge)
        {
            return ManagementRequestOutcome.HeadersTooLarge;
        }
        if (readStatus == ManagementHttpReadStatus.RequestTimeout)
        {
            return ManagementRequestOutcome.Timeout;
        }
        if (readStatus != ManagementHttpReadStatus.Success)
        {
            return ManagementRequestOutcome.BadRequest;
        }

        return statusCode switch
        {
            >= 200 and < 300 => ManagementRequestOutcome.Success,
            401 => ManagementRequestOutcome.Unauthorized,
            404 => ManagementRequestOutcome.NotFound,
            409 => ManagementRequestOutcome.Rejected,
            503 when route == ManagementRoute.Ready =>
                ManagementRequestOutcome.NotReady,
            503 when route == ManagementRoute.Live =>
                ManagementRequestOutcome.NotLive,
            503 => ManagementRequestOutcome.Unavailable,
            _ => ManagementRequestOutcome.BadRequest
        };
    }

    private void Observe(
        ManagementRoute route,
        ManagementRequestOutcome outcome)
    {
        if (_observeRequest is null)
        {
            return;
        }

        try
        {
            _observeRequest(new ManagementRequestObservation(
                route,
                outcome));
        }
        catch
        {
            // Telemetry cannot influence the management response or lifetime.
        }
    }

    private async Task ObserveRequestAsync(long requestId, Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // A malformed or disconnected management client is isolated.
        }
        finally
        {
            _activeRequests.TryRemove(requestId, out _);
            _requestSlots.Release();
        }
    }

    private async Task ObserveActiveRequestsAsync()
    {
        var tasks = _activeRequests.Values.ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(
                _options.RequestTimeout + _options.ResponseTimeout);
        }
        catch
        {
            // Every request already owns finite read/write/provider deadlines.
        }
    }
}
