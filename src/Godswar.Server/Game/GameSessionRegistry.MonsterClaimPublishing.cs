using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal async Task<int> PublishMonsterClaimStateAsync(
        ClientSession routingSession,
        byte mapId,
        MonsterDamageResult damage,
        CancellationToken cancellationToken)
    {
        if (!damage.ClaimEstablished ||
            damage.FirstHitCharacterId is not { } firstHitCharacterId)
        {
            return 0;
        }

        var recipients = CaptureMonsterClaimRecipients(
            routingSession,
            mapId,
            firstHitCharacterId);
        var sent = 0;
        foreach (var recipient in recipients.Where(
                     static recipient => recipient.OwnsClaim))
        {
            try
            {
                if (await DeliverMonsterPacketToViewerAsync(
                        recipient.Session,
                        mapId,
                        damage.ObjectId,
                        PacketBuilder.MonsterClaimState(
                            damage.ObjectId),
                        damage.Monster.SpawnGeneration,
                        cancellationToken,
                        "MonsterClaimEstablished",
                        framed: false))
                {
                    sent++;
                }
            }
            catch (Exception error)
                when (error is IOException or ObjectDisposedException)
            {
                Remove(recipient.Session);
            }
        }

        return sent;
    }

    private IReadOnlyList<MonsterClaimRecipient>
        CaptureMonsterClaimRecipients(
            ClientSession routingSession,
            byte mapId,
            int firstHitCharacterId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(
                    routingSession,
                    out var route) ||
                !route.WorldReady ||
                route.MapId != mapId)
            {
                return [];
            }

            HashSet<int> owners = [firstHitCharacterId];
            if (_partiesByCharacter.TryGetValue(
                    firstHitCharacterId,
                    out var party))
            {
                NormalizePartyLocked(party);
                if (_partiesByCharacter.TryGetValue(
                        firstHitCharacterId,
                        out party))
                {
                    owners.UnionWith(party.MemberCharacterIds);
                }
            }

            return _sessions.Values
                .Where(candidate =>
                    candidate.WorldReady &&
                    candidate.MapId == mapId &&
                    candidate.WorldInstanceId ==
                        route.WorldInstanceId)
                .Select(candidate => new MonsterClaimRecipient(
                    candidate.Session,
                    owners.Contains(candidate.CharacterId)))
                .ToArray();
        }
    }

    private readonly record struct MonsterClaimRecipient(
        ClientSession Session,
        bool OwnsClaim);
}
