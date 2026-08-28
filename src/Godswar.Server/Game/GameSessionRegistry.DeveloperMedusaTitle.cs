using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal async Task<MedusaCompletionRewardReceipt?>
        GrantDeveloperMedusaTitleTestAsync(
            ClientSession session,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var store = _medusaCompletionRewards;
        if (store is null)
        {
            return null;
        }

        GameSessionContext expected;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out expected!) ||
                !expected.WorldReady ||
                session.IsDisconnected ||
                !IsCurrentAccountSession(
                    expected.AccountId,
                    expected.Session,
                    expected.Ownership))
            {
                return null;
            }
        }

        // Exercise the same database-backed Mythic reward and title settlement
        // used by a real completion without requiring a full developer run.
        var request = new MedusaCompletionRewardRequest(
            WorldInstanceId.New(),
            expected.RealmId,
            MedusaEncounterDifficulty.Mythic,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(10),
            MedusaIslandPolicy.VictoryScore,
            [expected.CharacterId]);
        var receipt = await store.SettleAsync(request, cancellationToken);
        if (!receipt.Succeeded || receipt.Members.Count != 1)
        {
            return receipt;
        }

        GameSessionContext? current = null;
        var reward = receipt.Members[0];
        lock (_gate)
        {
            if (_sessions.TryGetValue(session, out var candidate) &&
                candidate.CharacterId == expected.CharacterId &&
                candidate.WorldInstanceId == expected.WorldInstanceId &&
                candidate.Ownership == expected.Ownership &&
                IsCurrentAccountSession(
                    candidate.AccountId,
                    candidate.Session,
                    candidate.Ownership))
            {
                candidate.Character.MedusaHonorPoints = reward.HonorAfter;
                candidate.Character.MedusaRewardRevision =
                    reward.RewardRevision;
                candidate.Character.AddOwnedTitle(reward.AwardedTitleId);
                candidate.Character.SelectedTitleId = reward.AwardedTitleId;
                current = candidate;
            }
        }

        if (current is not null)
        {
            await current.Session.SendAsync(
                PacketBuilder.MedusaDesignationInfo(
                    current.Character.SelectedTitleId,
                    current.Character.OwnedTitleIds),
                cancellationToken,
                "DeveloperMedusaTitleOwnership");
            var titlePacket = PacketBuilder.PlayerTitleInfo(
                current.Character,
                current.ObjectId);
            await current.Session.SendAsync(
                titlePacket,
                cancellationToken,
                "DeveloperMedusaTitleSelf");
            await BroadcastToWorldInstanceAsync(
                current.WorldInstanceId,
                titlePacket,
                cancellationToken,
                current.Session,
                "DeveloperMedusaTitleObservers");
        }

        var recipients = await PublishMedusaFactionNoticeAsync(
            receipt,
            expected.CharacterName,
            cancellationToken);
        Console.WriteLine(
            "[developer-title] Medusa title test " +
            $"character={expected.CharacterName} " +
            $"title={receipt.Award.Title?.DisplayName ?? "none"} " +
            $"notice-recipients={recipients}");
        return receipt;
    }
}
