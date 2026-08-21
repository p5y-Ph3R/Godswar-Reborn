using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IRealmCatalogReader? _realmCatalog;
    private readonly RealmId _processRealmId;

    private async Task<string?> ResolveGameLoginUsernameAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        var compatibilityUsername = PacketText.ReadFixedAscii(
            packet.Payload,
            0,
            32);

        // Secure tickets and semantic-gateway admissions already bind the
        // authenticated principal and route. Accept older test/client packet
        // shapes there, but reject an explicit cross-realm claim.
        if (_session.BoundGamePrincipal is not null ||
            _session.GatewayWorldAdmission is not null)
        {
            if (LegacyGameLoginPacket.TryRead(packet, out var routed) &&
                routed!.RealmId != _processRealmId)
            {
                _session.Disconnect();
                return null;
            }

            return compatibilityUsername;
        }

        // Constructors without a catalog are retained only for isolated
        // compatibility fixtures. Hosted PostgreSQL workers always receive
        // the catalog through GameClientHandlerFactory.
        if (_realmCatalog is null)
        {
            return compatibilityUsername;
        }

        if (!LegacyGameLoginPacket.TryRead(packet, out var identity) ||
            identity!.RealmId != _processRealmId)
        {
            _session.Disconnect();
            return null;
        }

        try
        {
            var enabled = await _realmCatalog.ReadEnabledAsync(
                cancellationToken);
            if (!enabled.TryFind(_processRealmId, out var realm) ||
                realm is null ||
                !LegacyGameLoginPacket.Matches(identity, realm))
            {
                _session.Disconnect();
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            Console.Error.WriteLine(
                "[realm] enabled realm catalog is unavailable");
            _session.Disconnect();
            return null;
        }

        return identity.Username;
    }
}
