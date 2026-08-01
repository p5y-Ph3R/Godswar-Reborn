using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Database;

internal static class PostgresGameplayContentBinding
{
    public const string ParameterName = "gameplayContentRevision";

    public static string ValidateRequired(string revision) =>
        ValidateOptional(revision) ?? throw new ArgumentException(
            "A pinned gameplay content revision is required.",
            nameof(revision));

    public static string? ValidateOptional(string? revision)
    {
        if (revision is null)
        {
            return null;
        }
        if (revision.Length != 64 ||
            revision.Any(static character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "Gameplay content revision must be an uppercase SHA-256.",
                nameof(revision));
        }

        return revision;
    }

    public static void AddParameter(
        NpgsqlCommand command,
        string? revision)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Parameters.AddWithValue(
            ParameterName,
            NpgsqlDbType.Varchar,
            revision is null ? DBNull.Value : revision);
    }
}
