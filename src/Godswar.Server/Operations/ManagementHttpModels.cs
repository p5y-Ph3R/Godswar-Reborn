namespace Godswar.Server.Operations;

internal enum ManagementContentType : byte
{
    Json = 1,
    OpenMetricsText = 2
}

internal readonly record struct ManagementPayload(
    ManagementContentType ContentType,
    ReadOnlyMemory<byte> Content);

internal enum ManagementDrainResult : byte
{
    Accepted = 1,
    AlreadyDraining = 2,
    Rejected = 3
}

internal delegate ValueTask<ManagementPayload> ManagementPayloadProvider(
    CancellationToken cancellationToken);

internal delegate bool ManagementDrainAuthenticator(
    ReadOnlyMemory<byte> suppliedToken);

internal delegate ValueTask<ManagementDrainResult> ManagementDrainHandler(
    CancellationToken cancellationToken);

internal enum ManagementRoute : byte
{
    Unknown = 0,
    Live = 1,
    Ready = 2,
    Metrics = 3,
    Traces = 4,
    Drain = 5
}

internal enum ManagementRequestOutcome : byte
{
    Success = 1,
    NotReady = 2,
    Unauthorized = 3,
    Rejected = 4,
    Unavailable = 5,
    BadRequest = 6,
    HeadersTooLarge = 7,
    Timeout = 8,
    NotFound = 9,
    Overloaded = 10,
    NotLive = 11
}

internal readonly record struct ManagementRequestObservation(
    ManagementRoute Route,
    ManagementRequestOutcome Outcome);

internal delegate void ManagementRequestObserver(
    ManagementRequestObservation observation);

internal static class ManagementObservationCodes
{
    public static string ToProtocolValue(this ManagementRoute route) =>
        route switch
        {
            ManagementRoute.Unknown => "unknown",
            ManagementRoute.Live => "live",
            ManagementRoute.Ready => "ready",
            ManagementRoute.Metrics => "metrics",
            ManagementRoute.Traces => "traces",
            ManagementRoute.Drain => "drain",
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };

    public static string ToProtocolValue(
        this ManagementRequestOutcome outcome) =>
        outcome switch
        {
            ManagementRequestOutcome.Success => "success",
            ManagementRequestOutcome.NotReady => "not_ready",
            ManagementRequestOutcome.Unauthorized => "unauthorized",
            ManagementRequestOutcome.Rejected => "rejected",
            ManagementRequestOutcome.Unavailable => "unavailable",
            ManagementRequestOutcome.BadRequest => "bad_request",
            ManagementRequestOutcome.HeadersTooLarge =>
                "headers_too_large",
            ManagementRequestOutcome.Timeout => "timeout",
            ManagementRequestOutcome.NotFound => "not_found",
            ManagementRequestOutcome.Overloaded => "overloaded",
            ManagementRequestOutcome.NotLive => "not_live",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
}

internal sealed class ManagementTokenAuthenticator : IDisposable
{
    private readonly byte[] _expectedDigest;
    private int _disposed;

    public ManagementTokenAuthenticator(ReadOnlySpan<byte> expectedToken)
    {
        if (expectedToken.Length is < 32 or > 256 ||
            !IsVisibleAscii(expectedToken))
        {
            throw new ArgumentException(
                "A management token must contain 32..256 visible ASCII bytes.",
                nameof(expectedToken));
        }

        _expectedDigest =
            System.Security.Cryptography.SHA256.HashData(expectedToken);
    }

    public bool Authenticate(ReadOnlyMemory<byte> suppliedToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        Span<byte> suppliedDigest = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(
            suppliedToken.Span,
            suppliedDigest);
        var authenticated =
            System.Security.Cryptography.CryptographicOperations
            .FixedTimeEquals(
                suppliedDigest,
                _expectedDigest);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(
            suppliedDigest);
        return authenticated;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(
            _expectedDigest);
    }

    private static bool IsVisibleAscii(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
        {
            if (character is < (byte)'!' or > (byte)'~')
            {
                return false;
            }
        }

        return true;
    }
}

internal readonly struct ManagementHttpRequest
{
    private readonly byte[]? _bearerToken;

    public ManagementHttpRequest(
        string method,
        string path,
        byte[]? bearerToken)
    {
        Method = method;
        Path = path;
        _bearerToken = bearerToken;
    }

    public string Method { get; }

    public string Path { get; }

    public ReadOnlyMemory<byte> BearerToken =>
        _bearerToken is null
            ? ReadOnlyMemory<byte>.Empty
            : _bearerToken;

    public void ClearBearerToken()
    {
        if (_bearerToken is not null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                _bearerToken);
        }
    }
}

internal readonly record struct ManagementHttpResponse(
    int StatusCode,
    string ReasonPhrase,
    string ContentType,
    ReadOnlyMemory<byte> Body,
    bool IncludeBearerChallenge = false);
