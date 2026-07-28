using System.Text;

namespace Godswar.Server.State;

/// <summary>
/// Loads the immutable SQL used only when initializing a completely empty
/// PostgreSQL database.
/// </summary>
internal static class LegacySchemaBootstrap
{
    private static readonly string[] ResourceNames =
    [
        "Godswar.Server.State.DatabaseMigrations.LegacySchemaBootstrap.001.sql",
        "Godswar.Server.State.DatabaseMigrations.LegacySchemaBootstrap.002.sql",
        "Godswar.Server.State.DatabaseMigrations.LegacySchemaBootstrap.003.sql",
        "Godswar.Server.State.DatabaseMigrations.LegacySchemaBootstrap.004.sql",
        "Godswar.Server.State.DatabaseMigrations.LegacySchemaBootstrap.005.sql",
        "Godswar.Server.State.DatabaseMigrations.LegacySchemaBootstrap.006.sql"
    ];

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async ValueTask<string> LoadAsync(CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(capacity: 105_000);
        foreach (var resourceName in ResourceNames)
        {
            await using var stream =
                typeof(LegacySchemaBootstrap).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded PostgreSQL bootstrap resource '{resourceName}' was not found.");
            await stream.CopyToAsync(content, cancellationToken);
        }

        return StrictUtf8.GetString(
            content.GetBuffer(),
            index: 0,
            count: checked((int)content.Length));
    }
}
