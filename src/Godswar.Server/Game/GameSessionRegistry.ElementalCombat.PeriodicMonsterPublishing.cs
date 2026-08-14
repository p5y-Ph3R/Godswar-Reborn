using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task PublishMonsterElementalBurnAsync(
        MonsterElementalBurnCommit commit,
        CancellationToken cancellationToken)
    {
        if (commit.Source is { } source &&
            IsCurrentWorldSessionSnapshot(source.Session, source))
        {
            await PublishPveElementalCommitAsync(
                source.Session,
                new(
                    [],
                    [new PveElementalDamageCommit(
                        ResonanceDamageKind.ElementalBurnTick,
                        commit.Intent.SourceEventId,
                        commit.DamageResult)],
                    [],
                    commit.SourceRecovery),
                cancellationToken);
            return;
        }

        if (commit.DamageResult.HealthMutation is not { } mutation)
        {
            if (commit.DamageResult.Killed)
            {
                Console.WriteLine(
                    "[elemental] monster Burn kill reward failed closed " +
                    $"source={commit.SourceCharacterId} " +
                    $"monster={commit.DamageResult.ObjectId}");
            }

            return;
        }

        await PublishMonsterBurnHealthReconciliationAsync(
            commit,
            mutation,
            cancellationToken);
        if (commit.DamageResult.Killed)
        {
            Console.WriteLine(
                "[elemental] monster Burn kill reward failed closed " +
                $"source={commit.SourceCharacterId} " +
                $"monster={commit.DamageResult.ObjectId}");
        }
    }

    private async Task PublishMonsterBurnHealthReconciliationAsync(
        MonsterElementalBurnCommit commit,
        MonsterHealthMutation mutation,
        CancellationToken cancellationToken)
    {
        if (!WorldInstances.TryFind(
                commit.WorldInstanceId,
                out var runtime))
        {
            return;
        }

        var monster = commit.DamageResult.Monster;
        foreach (var viewer in GetWorldInstanceSessions(
                     commit.WorldInstanceId))
        {
            try
            {
                await using var lease = await runtime.Map
                    .AcquireMonsterViewerHealthDeliveryLeaseAsync(
                        viewer.Session,
                        [mutation],
                        cancellationToken);
                if (lease is null)
                {
                    continue;
                }

                await viewer.Session.SendAsync(
                    PacketBuilder.RemoveWorldObjects([monster.ObjectId]),
                    cancellationToken,
                    "MonsterElementalBurnReconcileRemove");
                if (monster.IsSpawned && monster.IsAlive)
                {
                    await viewer.Session.SendAsync(
                        PacketBuilder.CapturedMonsterSpawns(
                            [monster.Appearance]),
                        cancellationToken,
                        "MonsterElementalBurnReconcileSpawn",
                        framed: false);
                    if (monster.IsMoving)
                    {
                        await viewer.Session.SendAsync(
                            PacketBuilder.MonsterMovementStart(
                                monster.ObjectId,
                                monster.X,
                                monster.Y,
                                monster.Z,
                                monster.VelocityX,
                                monster.VelocityY,
                                monster.VelocityZ),
                            cancellationToken,
                            "MonsterElementalBurnReconcileMovement");
                    }
                }

                lease.Commit();
            }
            catch (Exception ex) when (
                ex is IOException or ObjectDisposedException)
            {
                Remove(viewer.Session);
            }
        }
    }
}
