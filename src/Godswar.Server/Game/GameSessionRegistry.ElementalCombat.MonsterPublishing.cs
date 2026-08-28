using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task PublishPveElementalCommitAsync(
        ClientSession sourceSession,
        PveElementalCommitResult commit,
        CancellationToken cancellationToken,
        GameSessionContext? capturedSource = null,
        IReadOnlyList<PreparedPveMonsterKillReward>?
            preparedRewards = null)
    {
        GameSessionContext? source;
        lock (_gate)
        {
            _sessions.TryGetValue(sourceSession, out source);
        }

        source ??= capturedSource is not null &&
            ReferenceEquals(capturedSource.Session, sourceSession)
            ? capturedSource
            : null;
        if (source is null)
        {
            return;
        }

        var selector = source.Character.Profession is 2 or 3
            ? (byte)5
            : (byte)3;
        preparedRewards ??=
            await PreparePveElementalKillRewardsAsync(source, commit);
        foreach (var terminal in commit.DamageCommits)
        {
            var damage = terminal.DamageResult;
            if (damage.HealthMutation is not { } mutation)
            {
                continue;
            }

            var applied = damage.BeforeHealth - damage.AfterHealth;
            try
            {
                await DeliverMonsterHealthPacketToViewerAsync(
                    source.Session,
                    source.MapId,
                    damage.ObjectId,
                    BuildPveElementalDamagePacket(
                        LocalPlayerObjectId,
                        source,
                        damage.ObjectId,
                        applied,
                        selector,
                        damage.Killed),
                    mutation,
                    cancellationToken,
                    "PveElementalDamageSelf");
            }
            catch (Exception ex) when (
                ex is IOException or ObjectDisposedException)
            {
                Remove(source.Session);
            }

            await BroadcastToMonsterViewersAsync(
                source.MapId,
                damage.ObjectId,
                BuildPveElementalDamagePacket(
                    source.ObjectId,
                    source,
                    damage.ObjectId,
                    applied,
                    selector,
                    damage.Killed),
                cancellationToken,
                source.Session,
                "PveElementalDamageWorld",
                healthMutation: mutation);
        }

        if (commit.SourceRecovery.Applied)
        {
            UpdateCharacter(
                source.Session,
                source.Character,
                advanceWorldRevision: false);
            try
            {
                await PersistRoutineVitalsAsync(
                    source,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    "[elemental] PvE recovery persistence deferred " +
                    $"character={source.DisplayName}: {ex.Message}");
            }

            var currentHealth = source.Character.CurrentHp;
            var currentMana = source.Character.CurrentMp;
            try
            {
                await source.Session.SendAsync(
                    PacketBuilder.PlayerVitalsUpdate(
                        LocalPlayerObjectId,
                        currentHealth,
                        currentMana),
                    cancellationToken,
                    "PveElementalRecoverySelf");
            }
            catch (Exception ex) when (
                ex is IOException or ObjectDisposedException)
            {
                Remove(source.Session);
            }

            await BroadcastToMapAsync(
                source.MapId,
                PacketBuilder.PlayerVitalsUpdate(
                    source.ObjectId,
                    currentHealth,
                    currentMana),
                cancellationToken,
                source.Session,
                "PveElementalRecoveryWorld");
        }

        foreach (var preparedReward in preparedRewards)
        {
            try
            {
                await preparedReward.PublishAsync(cancellationToken);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    "[elemental] monster reward packet publication deferred " +
                    $"character={source.DisplayName}: {ex.Message}");
            }
        }
    }

    internal async Task<
        IReadOnlyList<PreparedPveMonsterKillReward>>
        PreparePveElementalKillRewardsAsync(
            GameSessionContext source,
            PveElementalCommitResult commit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(commit);
        var preparedRewards =
            new List<PreparedPveMonsterKillReward>();
        foreach (var terminal in commit.DamageCommits)
        {
            var damage = terminal.DamageResult;
            if (!damage.Killed)
            {
                continue;
            }

            try
            {
                var prepared =
                    await PrepareClaimedMonsterKillRewardAsync(
                        source.Session,
                        damage);
                if (prepared is not null)
                {
                    preparedRewards.Add(prepared);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[elemental] monster reward settlement deferred " +
                    $"character={source.DisplayName} " +
                    $"monster={damage.ObjectId}: {ex.Message}");
            }
        }

        return preparedRewards.AsReadOnly();
    }

    private static byte[] BuildPveElementalDamagePacket(
        uint attackerObjectId,
        GameSessionContext source,
        uint targetObjectId,
        uint damage,
        byte selector,
        bool killed) =>
        PacketBuilder.PhysicalDamage(
            attackerObjectId,
            source.Character.PositionX,
            0f,
            source.Character.PositionZ,
            targetObjectId,
            damage,
            killed ? (byte)5 : selector,
            (byte)CombatHitOutcome.Normal);
}
