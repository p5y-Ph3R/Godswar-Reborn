using System.Text;

namespace Godswar.Server;

internal sealed partial class ServerOptions
{
    internal const string PostgresConnectionStringEnvironmentVariable =
        "GODSWAR_POSTGRES_CONNECTION_STRING";

    internal const string PostgresConnectionStringFileEnvironmentVariable =
        "GODSWAR_POSTGRES_CONNECTION_STRING_FILE";

    private const int MaximumPostgresConnectionStringFileBytes = 4_096;

    private static string ResolvePostgresConnectionString(string fallback)
    {
        var direct = Environment.GetEnvironmentVariable(
            PostgresConnectionStringEnvironmentVariable);
        var filePath = Environment.GetEnvironmentVariable(
            PostgresConnectionStringFileEnvironmentVariable);
        if (filePath is not null && string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidDataException(
                $"{PostgresConnectionStringFileEnvironmentVariable} must " +
                "not be blank when configured.");
        }
        if (!string.IsNullOrWhiteSpace(direct) &&
            filePath is not null)
        {
            throw new InvalidDataException(
                "PostgreSQL connection-string environment and secret-file " +
                "sources are mutually exclusive.");
        }

        if (filePath is null)
        {
            return direct ?? fallback;
        }
        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new InvalidDataException(
                $"{PostgresConnectionStringFileEnvironmentVariable} must " +
                "contain an absolute secret-file path.");
        }

        return ReadPostgresConnectionStringSecret(filePath);
    }

    private static string ReadPostgresConnectionStringSecret(string filePath)
    {
        var bytes = new byte[MaximumPostgresConnectionStringFileBytes + 1];
        try
        {
            var byteCount = 0;
            using (var stream = new FileStream(
                       filePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: MaximumPostgresConnectionStringFileBytes,
                       FileOptions.SequentialScan))
            {
                while (byteCount < bytes.Length)
                {
                    var read = stream.Read(
                        bytes,
                        byteCount,
                        bytes.Length - byteCount);
                    if (read == 0)
                    {
                        break;
                    }
                    byteCount += read;
                }
            }

            if (byteCount is < 1 or
                > MaximumPostgresConnectionStringFileBytes)
            {
                throw InvalidPostgresSecretSize();
            }

            var value = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetString(bytes, 0, byteCount);
            if (value.EndsWith("\r\n", StringComparison.Ordinal))
            {
                value = value[..^2];
            }
            else if (value.EndsWith('\n'))
            {
                value = value[..^1];
            }

            if (string.IsNullOrWhiteSpace(value) ||
                value.Any(char.IsControl))
            {
                throw new InvalidDataException(
                    "PostgreSQL secret file contains invalid " +
                    "connection-string content.");
            }

            return value;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or
                DirectoryNotFoundException)
        {
            throw InvalidPostgresSecretSize(exception);
        }
        catch (Exception exception)
            when (exception is IOException or
                UnauthorizedAccessException or
                DecoderFallbackException or
                NotSupportedException)
        {
            throw new InvalidDataException(
                "PostgreSQL connection-string secret file could not be read.",
                exception);
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    private static InvalidDataException InvalidPostgresSecretSize(
        Exception? innerException = null) =>
        new(
            "PostgreSQL connection-string secret file must exist and " +
            $"contain between 1 and " +
            $"{MaximumPostgresConnectionStringFileBytes} bytes.",
            innerException);
}
