using System.Collections.Immutable;
using System.Net;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Realms;

internal interface IRealmCatalogReader
{
    Task<RealmCatalogSnapshot> ReadEnabledAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One enabled logical realm advertised by the login gateway. The legacy
/// client carries the durable realm ID in one byte, so values outside that
/// bridge must fail before packet construction.
/// </summary>
internal sealed record RealmCatalogEntry
{
    public const int MaximumDisplayNameBytes = 35;
    public const int IdentifierBytes = 25;
    public const int MaximumLegacyHostBytes = 23;

    public RealmCatalogEntry(
        RealmId realmId,
        string name,
        string identifier,
        string host,
        int gamePort,
        int serverLimit,
        bool recommended,
        int displayOrder)
    {
        if (!realmId.IsValid || realmId.Value > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realmId),
                realmId,
                "Legacy realm IDs must be between 1 and 255.");
        }

        Name = ValidateDisplayName(name);
        Identifier = ValidateAscii(
            identifier,
            IdentifierBytes,
            requireExactLength: true,
            nameof(identifier));
        Host = ValidateHost(host);
        if (gamePort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(gamePort));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serverLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(displayOrder);

        RealmId = realmId;
        GamePort = gamePort;
        ServerLimit = serverLimit;
        Recommended = recommended;
        DisplayOrder = displayOrder;
    }

    public RealmId RealmId { get; }

    public byte LegacyWireId => checked((byte)RealmId.Value);

    public string Name { get; }

    /// <summary>
    /// The fixed 25-byte value copied from the login redirect into the first
    /// game-server login packet. It is an opaque routing identifier, not a
    /// management slug or a credential.
    /// </summary>
    public string Identifier { get; }

    public string Host { get; }

    public int GamePort { get; }

    public int ServerLimit { get; }

    public bool Recommended { get; }

    public int DisplayOrder { get; }

    private static string ValidateHost(string value)
    {
        var host = ValidateAscii(
            value,
            MaximumLegacyHostBytes,
            requireExactLength: false,
            nameof(value));
        if (IPAddress.TryParse(host, out _) ||
            Uri.CheckHostName(host) is UriHostNameType.Dns)
        {
            return host;
        }

        throw new ArgumentException(
            "The realm host must be a bounded IPv4 address or DNS name.",
            nameof(value));
    }

    private static string ValidateDisplayName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumDisplayNameBytes ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(static character => character is < ' ' or > '~'))
        {
            throw new ArgumentException(
                "Realm names must contain at most 35 printable ASCII bytes " +
                "without leading or trailing whitespace.",
                nameof(value));
        }

        return value;
    }

    private static string ValidateAscii(
        string value,
        int maximumLength,
        bool requireExactLength,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if ((requireExactLength && value.Length != maximumLength) ||
            (!requireExactLength && value.Length > maximumLength) ||
            value.Any(static character => character is < '!' or > '~'))
        {
            var length = requireExactLength
                ? $"exactly {maximumLength}"
                : $"at most {maximumLength}";
            throw new ArgumentException(
                $"The value must contain {length} printable ASCII bytes.",
                parameterName);
        }

        return value;
    }
}

internal sealed record RealmCatalogSnapshot
{
    public const int MaximumEntries = 16;

    public RealmCatalogSnapshot(IEnumerable<RealmCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var supplied = entries.ToImmutableArray();
        if (supplied.Any(static entry => entry is null))
        {
            throw new InvalidDataException(
                "Enabled realm catalog entries cannot be null.");
        }

        var ordered = supplied
            .OrderBy(static entry => entry.DisplayOrder)
            .ThenBy(static entry => entry.RealmId.Value)
            .ToImmutableArray();
        if (ordered.Length > MaximumEntries)
        {
            throw new InvalidDataException(
                $"The enabled realm catalog exceeds {MaximumEntries} entries.");
        }
        if (ordered.Select(static entry => entry.RealmId).Distinct().Count() !=
                ordered.Length ||
            ordered.Select(static entry => entry.Identifier)
                .Distinct(StringComparer.Ordinal)
                .Count() != ordered.Length)
        {
            throw new InvalidDataException(
                "Enabled realm catalog identities must be unique.");
        }
        if (ordered.Count(static entry => entry.Recommended) > 1)
        {
            throw new InvalidDataException(
                "At most one enabled realm may be recommended.");
        }

        Entries = ordered;
    }

    public ImmutableArray<RealmCatalogEntry> Entries { get; }

    public bool TryFind(RealmId realmId, out RealmCatalogEntry? entry)
    {
        foreach (var candidate in Entries)
        {
            if (candidate.RealmId == realmId)
            {
                entry = candidate;
                return true;
            }
        }

        entry = null;
        return false;
    }
}
