using System.Net;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Gateway;

/// <summary>
/// Immutable realm selection bound to one authenticated gateway login.
/// The legacy identifier is public routing data, but it must still match the
/// authenticated selection exactly before a game connection is admitted.
/// </summary>
internal sealed record SemanticGatewayRealmGrant
{
    public SemanticGatewayRealmGrant(RealmCatalogEntry realm) :
        this(
            realm?.RealmId ??
                throw new ArgumentNullException(nameof(realm)),
            realm.Identifier,
            realm.Host,
            realm.GamePort)
    {
    }

    public SemanticGatewayRealmGrant(
        RealmId realmId,
        string identifier,
        string host,
        int gamePort)
    {
        if (!realmId.IsValid || realmId.Value > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        if (string.IsNullOrEmpty(identifier) ||
            identifier.Length != RealmCatalogEntry.IdentifierBytes ||
            identifier.Any(static value => value is < '!' or > '~'))
        {
            throw new ArgumentException(
                "The realm identifier must be exact printable ASCII.",
                nameof(identifier));
        }
        if (string.IsNullOrWhiteSpace(host) ||
            host.Length > RealmCatalogEntry.MaximumLegacyHostBytes ||
            host.Any(static value => value is < '!' or > '~') ||
            !(IPAddress.TryParse(host, out _) ||
              Uri.CheckHostName(host) is UriHostNameType.Dns))
        {
            throw new ArgumentException(
                "The realm endpoint host is not legacy-client compatible.",
                nameof(host));
        }
        if (gamePort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(gamePort));
        }

        RealmId = realmId;
        Identifier = identifier;
        Host = host;
        GamePort = gamePort;
    }

    public RealmId RealmId { get; }

    public string Identifier { get; }

    public string Host { get; }

    public int GamePort { get; }

    public bool Matches(RealmCatalogEntry realm) =>
        realm is not null &&
        RealmId == realm.RealmId &&
        GamePort == realm.GamePort &&
        string.Equals(Identifier, realm.Identifier, StringComparison.Ordinal) &&
        string.Equals(Host, realm.Host, StringComparison.Ordinal);
}
