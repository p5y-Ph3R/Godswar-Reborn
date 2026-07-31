using System.Text.Json;
using System.Text.Json.Serialization;

namespace Godswar.Server.Networking.SemanticGateway;

internal sealed partial class SemanticGatewayRuntimeOptions
{
    internal const int MaximumConfigurationBytes = 1 * 1_024 * 1_024;
    internal const int MaximumWorkers = 256;
    internal const int MaximumRoutes = 4_096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
        Converters =
        {
            new JsonStringEnumConverter<SemanticGatewayWorkerState>(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    public static Task<SemanticGatewayRuntimeConfiguration> LoadAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        LoadAsync(path, TimeProvider.System, cancellationToken);

    internal static async Task<SemanticGatewayRuntimeConfiguration>
        LoadAsync(
            string path,
            TimeProvider timeProvider,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException(
                "A semantic-gateway configuration path is required.");
        }
        ArgumentNullException.ThrowIfNull(timeProvider);

        var document = new byte[MaximumConfigurationBytes + 1];
        var documentLength = 0;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
            await using var stream = new FileStream(
                fullPath,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read,
                    BufferSize = 4_096,
                    Options =
                        FileOptions.Asynchronous |
                        FileOptions.SequentialScan
                });
            while (documentLength < document.Length)
            {
                var count = await stream.ReadAsync(
                    document.AsMemory(documentLength),
                    cancellationToken);
                if (count == 0)
                {
                    break;
                }

                documentLength += count;
            }
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                NotSupportedException or
                IOException or
                UnauthorizedAccessException)
        {
            Array.Clear(document);
            throw new InvalidDataException(
                "The semantic-gateway configuration could not be read.",
                exception);
        }

        if (documentLength is 0 or > MaximumConfigurationBytes)
        {
            Array.Clear(document);
            throw new InvalidDataException(
                "The semantic-gateway configuration must be between 1 and " +
                $"{MaximumConfigurationBytes} bytes.");
        }

        try
        {
            var options =
                JsonSerializer.Deserialize<SemanticGatewayRuntimeOptions>(
                    document.AsSpan(0, documentLength),
                    JsonOptions);
            if (options is null)
            {
                throw new InvalidDataException(
                    "The semantic-gateway configuration is empty.");
            }

            return options.Validate(fullPath, timeProvider);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The semantic-gateway configuration is not valid JSON.",
                exception);
        }
        finally
        {
            Array.Clear(document);
        }
    }
}
