namespace Godswar.Server.State;

internal readonly record struct AppliedPostgresSchemaMigration(
    string Id,
    string Checksum);

/// <summary>
/// Validates immutable history and selects migrations that have not run.
/// Kept independent of Npgsql so the safety rules can be tested without a database.
/// </summary>
internal static class PostgresSchemaMigrationPlan
{
    public static IReadOnlyList<PostgresSchemaMigration> Build(
        IReadOnlyList<PostgresSchemaMigration> registered,
        IReadOnlyList<AppliedPostgresSchemaMigration> applied)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(applied);
        ValidateRegisteredOrder(registered);

        if (applied.Count > registered.Count)
        {
            throw new InvalidOperationException(
                "Migration history is ahead of this server. " +
                $"The database records {applied.Count} migrations, but this release registers " +
                $"{registered.Count}.");
        }

        var seenAppliedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < applied.Count; index++)
        {
            var entry = applied[index];
            if (!seenAppliedIds.Add(entry.Id))
            {
                throw new InvalidOperationException(
                    $"Migration history contains duplicate ID '{entry.Id}'.");
            }

            var expected = registered[index];
            if (!string.Equals(entry.Id, expected.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Migration history must be an exact ordered prefix of the registered " +
                    $"migrations. Position {index + 1} expects '{expected.Id}', but the " +
                    $"database records '{entry.Id}'.");
            }

            if (!string.Equals(expected.Checksum, entry.Checksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Migration '{expected.Id}' was modified after it was applied. " +
                    $"Database checksum is {entry.Checksum}; registered checksum is {expected.Checksum}.");
            }
        }

        return registered.Skip(applied.Count).ToArray();
    }

    private static void ValidateRegisteredOrder(IReadOnlyList<PostgresSchemaMigration> registered)
    {
        for (var index = 1; index < registered.Count; index++)
        {
            var previous = registered[index - 1];
            var current = registered[index];
            if (string.CompareOrdinal(previous.Id, current.Id) >= 0)
            {
                throw new InvalidOperationException(
                    "Registered migrations must have unique IDs in ascending order. " +
                    $"'{current.Id}' follows '{previous.Id}'.");
            }
        }
    }
}
