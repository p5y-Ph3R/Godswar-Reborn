using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Godswar.Server.State;

/// <summary>
/// One explicitly registered, forward-only PostgreSQL schema migration.
/// Legacy numbered bootstrap scripts are intentionally not represented here.
/// </summary>
internal sealed class PostgresSchemaMigration
{
    private static readonly Regex IdPattern = new(
        @"^\d{8}_\d{3}_[a-z0-9_]+$",
        RegexOptions.CultureInvariant);

    public PostgresSchemaMigration(string id, string description, string sql)
    {
        Id = RequireId(id);
        Description = RequireDescription(description);
        Sql = RequireSql(sql);
        Checksum = ComputeChecksum(Sql);
    }

    public string Id { get; }

    public string Description { get; }

    public string Sql { get; }

    public string Checksum { get; }

    internal static string ComputeChecksum(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var normalized = NormalizeLineEndings(sql).Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string RequireId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id))
        {
            throw new ArgumentException(
                "Migration IDs must use YYYYMMDD_NNN_lowercase_name format.",
                nameof(id));
        }

        if (id.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Migration IDs are limited to 128 characters.");
        }

        return id;
    }

    private static string RequireDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("A migration description is required.", nameof(description));
        }

        if (description.Length > 255)
        {
            throw new ArgumentOutOfRangeException(
                nameof(description),
                "Migration descriptions are limited to 255 characters.");
        }

        return description;
    }

    private static string RequireSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("Migration SQL is required.", nameof(sql));
        }

        return NormalizeLineEndings(sql);
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
