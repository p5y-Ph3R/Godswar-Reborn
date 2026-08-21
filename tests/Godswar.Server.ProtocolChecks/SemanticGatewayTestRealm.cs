using Godswar.Server.Application.Gateway;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

internal static class SemanticGatewayTestRealm
{
    public static readonly RealmCatalogEntry Tempest = new(
        RealmId.Tempest,
        "Tempest",
        "KAL3jcIzqGgKvOf1dbYZKC8cS",
        "127.1.1.110",
        gamePort: 7_000,
        serverLimit: 250,
        recommended: true,
        displayOrder: 1);

    public static readonly RealmCatalogEntry Dwargon = new(
        RealmId.Dwargon,
        "Dwargon",
        "DWG3jcIzqGgKvOf1dbYZKC8cS",
        "127.1.1.111",
        gamePort: 7_000,
        serverLimit: 250,
        recommended: false,
        displayOrder: 2);

    public static readonly RealmCatalogSnapshot Catalog =
        new([Tempest, Dwargon]);

    public static readonly SemanticGatewayRealmGrant TempestGrant =
        new(Tempest);

    public static readonly SemanticGatewayRealmGrant DwargonGrant =
        new(Dwargon);
}
