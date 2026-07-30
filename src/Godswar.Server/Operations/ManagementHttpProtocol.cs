using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Operations;

internal static class ManagementHttpProtocol
{
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    public static async Task<ManagementHttpRequestReadResult> ReadRequestAsync(
        NetworkStream stream,
        int maximumHeaderBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = ArrayPool<byte>.Shared.Rent(maximumHeaderBytes + 1);
        try
        {
            using var deadline =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            deadline.CancelAfter(timeout);

            var count = 0;
            var headerEnd = -1;
            while (count <= maximumHeaderBytes)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(
                        count,
                        maximumHeaderBytes + 1 - count),
                    deadline.Token);
                if (read == 0)
                {
                    return ManagementHttpRequestReadResult.BadRequest;
                }

                count += read;
                if (count > maximumHeaderBytes)
                {
                    return ManagementHttpRequestReadResult.HeadersTooLarge;
                }
                headerEnd = FindHeaderEnd(buffer.AsSpan(0, count));
                if (headerEnd >= 0)
                {
                    break;
                }
            }

            if (headerEnd < 0)
            {
                return ManagementHttpRequestReadResult.HeadersTooLarge;
            }
            if (headerEnd != count)
            {
                // Request bodies and HTTP pipelining are intentionally absent.
                return ManagementHttpRequestReadResult.BadRequest;
            }

            var request = ParseRequest(buffer.AsSpan(0, headerEnd));
            return request is null
                ? ManagementHttpRequestReadResult.BadRequest
                : new ManagementHttpRequestReadResult(
                    ManagementHttpReadStatus.Success,
                    request.Value);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return ManagementHttpRequestReadResult.RequestTimeout;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task WriteResponseAsync(
        NetworkStream stream,
        ManagementHttpResponse response,
        int maximumResponseBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateResponse(response);

        var header = Encoding.ASCII.GetBytes(BuildHeader(response));
        var totalBytes = checked(header.Length + response.Body.Length);
        if (totalBytes > maximumResponseBytes)
        {
            throw new ManagementResponseTooLargeException();
        }

        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        deadline.CancelAfter(timeout);
        await stream.WriteAsync(header, deadline.Token);
        if (!response.Body.IsEmpty)
        {
            await stream.WriteAsync(response.Body, deadline.Token);
        }
        await stream.FlushAsync(deadline.Token);
    }

    private static ManagementHttpRequest? ParseRequest(
        ReadOnlySpan<byte> header)
    {
        if (!IsStrictAsciiHeader(header))
        {
            return null;
        }

        var requestLineEnd = header.IndexOf("\r\n"u8);
        if (requestLineEnd <= 0)
        {
            return null;
        }

        var requestLine = header[..requestLineEnd];
        var firstSpace = requestLine.IndexOf((byte)' ');
        if (firstSpace <= 0)
        {
            return null;
        }

        var afterMethod = requestLine[(firstSpace + 1)..];
        var relativeSecondSpace = afterMethod.IndexOf((byte)' ');
        if (relativeSecondSpace <= 0)
        {
            return null;
        }

        var secondSpace = firstSpace + 1 + relativeSecondSpace;
        var methodBytes = requestLine[..firstSpace];
        var pathBytes = requestLine[(firstSpace + 1)..secondSpace];
        var versionBytes = requestLine[(secondSpace + 1)..];
        var method = methodBytes.SequenceEqual("GET"u8)
            ? "GET"
            : methodBytes.SequenceEqual("POST"u8)
                ? "POST"
                : null;
        if (method is null ||
            !versionBytes.SequenceEqual("HTTP/1.1"u8) ||
            pathBytes.Length is < 1 or > 64 ||
            pathBytes[0] != (byte)'/' ||
            pathBytes.IndexOf((byte)'?') >= 0 ||
            pathBytes.IndexOf((byte)'#') >= 0 ||
            !IsVisibleAscii(pathBytes))
        {
            return null;
        }

        var hostCount = 0;
        var authorizationCount = 0;
        var contentLengthCount = 0;
        byte[]? bearerToken = null;
        try
        {
            var cursor = requestLineEnd + 2;
            var terminated = false;
            while (cursor < header.Length)
            {
                var relativeLineEnd =
                    header[cursor..].IndexOf("\r\n"u8);
                if (relativeLineEnd < 0)
                {
                    return null;
                }

                var line = header.Slice(cursor, relativeLineEnd);
                cursor += relativeLineEnd + 2;
                if (line.IsEmpty)
                {
                    terminated = cursor == header.Length;
                    break;
                }

                var separator = line.IndexOf((byte)':');
                if (separator <= 0)
                {
                    return null;
                }

                var name = line[..separator];
                var value = TrimOptionalWhitespace(
                    line[(separator + 1)..]);
                if (!IsHeaderName(name))
                {
                    return null;
                }

                if (EqualsAsciiIgnoreCase(name, "Host"u8))
                {
                    hostCount++;
                    if (hostCount != 1 || value.IsEmpty)
                    {
                        return null;
                    }
                    continue;
                }
                if (EqualsAsciiIgnoreCase(
                        name,
                        "Content-Length"u8))
                {
                    contentLengthCount++;
                    if (contentLengthCount != 1 ||
                        !value.SequenceEqual("0"u8))
                    {
                        return null;
                    }
                    continue;
                }
                if (EqualsAsciiIgnoreCase(
                        name,
                        "Transfer-Encoding"u8))
                {
                    return null;
                }
                if (EqualsAsciiIgnoreCase(
                        name,
                        "Authorization"u8))
                {
                    authorizationCount++;
                    if (authorizationCount != 1 ||
                        !value.StartsWith("Bearer "u8) ||
                        value.Length == "Bearer "u8.Length)
                    {
                        return null;
                    }

                    bearerToken = value["Bearer "u8.Length..].ToArray();
                }
            }

            if (!terminated || hostCount != 1)
            {
                return null;
            }

            var request = new ManagementHttpRequest(
                method,
                Encoding.ASCII.GetString(pathBytes),
                bearerToken);
            bearerToken = null;
            return request;
        }
        finally
        {
            if (bearerToken is not null)
            {
                CryptographicOperations.ZeroMemory(bearerToken);
            }
        }
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> bytes)
    {
        var index = bytes.IndexOf(HeaderTerminator);
        return index < 0 ? -1 : index + HeaderTerminator.Length;
    }

    private static bool IsStrictAsciiHeader(ReadOnlySpan<byte> header)
    {
        for (var index = 0; index < header.Length; index++)
        {
            var value = header[index];
            if (value == (byte)'\r')
            {
                if (index + 1 >= header.Length ||
                    header[index + 1] != (byte)'\n')
                {
                    return false;
                }
                index++;
                continue;
            }
            if (value == (byte)'\n' ||
                value != (byte)'\t' &&
                value is < 0x20 or > 0x7e)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsVisibleAscii(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
        {
            if (character is < 0x21 or > 0x7e)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsHeaderName(ReadOnlySpan<byte> value)
    {
        if (value.Length is < 1 or > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= (byte)'a' and <= (byte)'z') and
                not (>= (byte)'A' and <= (byte)'Z') and
                not (>= (byte)'0' and <= (byte)'9') and
                not ((byte)'!' or (byte)'#' or (byte)'$' or (byte)'%' or
                    (byte)'&' or (byte)'\'' or (byte)'*' or (byte)'+' or
                    (byte)'-' or (byte)'.' or (byte)'^' or (byte)'_' or
                    (byte)'`' or (byte)'|' or (byte)'~'))
            {
                return false;
            }
        }
        return true;
    }

    private static ReadOnlySpan<byte> TrimOptionalWhitespace(
        ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length &&
            value[start] is (byte)' ' or (byte)'\t')
        {
            start++;
        }

        var end = value.Length;
        while (end > start &&
            value[end - 1] is (byte)' ' or (byte)'\t')
        {
            end--;
        }
        return value[start..end];
    }

    private static bool EqualsAsciiIgnoreCase(
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> expected)
    {
        if (value.Length != expected.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (ToLowerAscii(value[index]) !=
                ToLowerAscii(expected[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static byte ToLowerAscii(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(value + ((byte)'a' - (byte)'A'))
            : value;

    private static string BuildHeader(ManagementHttpResponse response)
    {
        var challenge = response.IncludeBearerChallenge
            ? "WWW-Authenticate: Bearer\r\n"
            : string.Empty;
        return "HTTP/1.1 " +
            response.StatusCode.ToString(CultureInfo.InvariantCulture) +
            " " +
            response.ReasonPhrase +
            "\r\nContent-Type: " +
            response.ContentType +
            "\r\nContent-Length: " +
            response.Body.Length.ToString(CultureInfo.InvariantCulture) +
            "\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            challenge +
            "Connection: close\r\n\r\n";
    }

    private static void ValidateResponse(ManagementHttpResponse response)
    {
        if (response.StatusCode is < 100 or > 599 ||
            response.ReasonPhrase.Length is < 1 or > 32 ||
            response.ReasonPhrase.Any(static character =>
                character != ' ' &&
                (character is < 'A' or > 'Z') &&
                (character is < 'a' or > 'z')) ||
            response.ContentType.Length is < 1 or > 96 ||
            response.ContentType.Any(static character =>
                character is '\r' or '\n'))
        {
            throw new InvalidOperationException(
                "A management response contained invalid fixed metadata.");
        }
    }
}

internal enum ManagementHttpReadStatus : byte
{
    Success = 1,
    BadRequest = 2,
    HeadersTooLarge = 3,
    RequestTimeout = 4
}

internal readonly record struct ManagementHttpRequestReadResult(
    ManagementHttpReadStatus Status,
    ManagementHttpRequest Request)
{
    public static ManagementHttpRequestReadResult BadRequest =>
        new(ManagementHttpReadStatus.BadRequest, default);

    public static ManagementHttpRequestReadResult HeadersTooLarge =>
        new(ManagementHttpReadStatus.HeadersTooLarge, default);

    public static ManagementHttpRequestReadResult RequestTimeout =>
        new(ManagementHttpReadStatus.RequestTimeout, default);
}

internal sealed class ManagementResponseTooLargeException : Exception
{
    public ManagementResponseTooLargeException()
        : base("The bounded management response limit was exceeded.")
    {
    }
}
