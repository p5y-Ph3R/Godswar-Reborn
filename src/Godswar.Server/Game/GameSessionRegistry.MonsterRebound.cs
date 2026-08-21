using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal readonly record struct PveMonsterReboundCommit(
    bool Claimed,
    ulong CombatEventId,
    uint AppliedPlayerDamage,
    uint RequestedReboundDamage,
    MonsterDamageResult? DamageResult)
{
    public bool Applied => DamageResult is not null;

    public bool Killed => DamageResult?.Killed == true;
}

internal sealed partial class GameSessionRegistry
{
    private readonly CombatSecondaryEffectCommitLedger
        _pveMonsterReboundLedger = new();

    internal void RegisterPveMonsterKillRewardPreparer(
        ClientSession session,
        Func<MonsterDamageResult,
            Task<PreparedPveMonsterKillReward?>> preparer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(preparer);
        lock (_gate)
        {
            if (_sessions.TryGetValue(session, out var context))
            {
                _sessions[session] = context with
                {
                    PreparePveMonsterKillReward = preparer
                };
            }
        }
    }

    internal PveMonsterReboundCommit CommitMonsterReboundForSession(
        ClientSession session,
        MonsterRuntimeSnapshot monster,
        ulong combatEventId,
        uint appliedPlayerDamage,
        uint requestedReboundDamage)
    {
        ArgumentNullException.ThrowIfNull(session);
        GameSessionContext? source;
        lock (_gate)
        {
            _sessions.TryGetValue(session, out source);
        }

        return source is null
            ? default
            : CommitMonsterRebound(
                source,
                monster,
                combatEventId,
                appliedPlayerDamage,
                requestedReboundDamage);
    }

    private PveMonsterReboundCommit CommitMonsterRebound(
        GameSessionContext source,
        MonsterRuntimeSnapshot monster,
        ulong combatEventId,
        uint appliedPlayerDamage,
        uint requestedReboundDamage)
    {
        if (requestedReboundDamage == 0 || appliedPlayerDamage == 0)
        {
            return default;
        }

        var key = new CombatSecondaryEffectCommitKey(
            CombatSecondaryEffectCommitKind.MonsterRebound,
            source.CharacterId,
            monster.ObjectId,
            monster.SpawnGeneration,
            combatEventId);
        if (!_pveMonsterReboundLedger.TryClaim(key))
        {
            return new PveMonsterReboundCommit(
                false,
                combatEventId,
                appliedPlayerDamage,
                requestedReboundDamage,
                null);
        }

        var applied = TryApplyMonsterDamage(
            source.MapId,
            monster.ObjectId,
            requestedReboundDamage,
            source.CharacterId,
            monster.SpawnGeneration,
            out var damageResult);
        if (!applied)
        {
            _pveMonsterReboundLedger.Release(key);
        }

        return new PveMonsterReboundCommit(
            applied,
            combatEventId,
            appliedPlayerDamage,
            requestedReboundDamage,
            applied ? damageResult : null);
    }

    private async Task PublishMonsterReboundAsync(
        WorldInstanceRuntime runtime,
        GameSessionContext source,
        PveMonsterReboundCommit commit,
        PreparedPveMonsterKillReward? preparedReward,
        CancellationToken cancellationToken)
    {
        if (commit.DamageResult is not { } damageResult ||
            damageResult.HealthMutation is not { } mutation)
        {
            return;
        }

        var character = source.Character;
        var selector = character.Profession is 2 or 3
            ? (byte)5
            : (byte)3;
        try
        {
            await DeliverMonsterHealthPacketToViewerAsync(
                source.Session,
                source.MapId,
                damageResult.ObjectId,
                BuildMonsterReboundPacket(
                    LocalPlayerObjectId,
                    character.PositionX,
                    character.PositionZ,
                    damageResult.ObjectId,
                    commit.RequestedReboundDamage,
                    selector),
                mutation,
                cancellationToken,
                "MonsterReboundSelf");
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            Remove(source.Session);
        }

        var viewers = await BroadcastToMonsterViewersAsync(
            source.MapId,
            damageResult.ObjectId,
            BuildMonsterReboundPacket(
                source.ObjectId,
                character.PositionX,
                character.PositionZ,
                damageResult.ObjectId,
                commit.RequestedReboundDamage,
                selector),
            cancellationToken,
            source.Session,
            "MonsterReboundWorld",
            healthMutation: mutation);

        if (preparedReward is not null)
        {
            try
            {
                await preparedReward.PublishAsync(cancellationToken);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    "[monster] rebound reward packet publication deferred " +
                    $"character={source.DisplayName} " +
                    $"monster={damageResult.ObjectId}: {ex.Message}");
            }
        }

        Console.WriteLine(
            "[monster] rebound " +
            $"character={source.DisplayName} " +
            $"monster={damageResult.ObjectId} " +
            $"event={commit.CombatEventId} " +
            $"incoming={commit.AppliedPlayerDamage} " +
            $"requested={commit.RequestedReboundDamage} " +
            $"applied={damageResult.BeforeHealth - damageResult.AfterHealth} " +
            $"killed={damageResult.Killed} viewers={viewers}");
    }

    private static async Task<PreparedPveMonsterKillReward?>
        PrepareMonsterReboundRewardAsync(
            GameSessionContext source,
            PveMonsterReboundCommit commit)
    {
        if (commit.DamageResult is not { Killed: true } damageResult ||
            source.PreparePveMonsterKillReward is not { } prepare)
        {
            return null;
        }

        try
        {
            // This phase owns durable settlement and must run before any
            // cancellable rebound transport. Packet publication remains
            // deferred until after the terminal damage packet.
            return await prepare(damageResult);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                "[monster] rebound reward settlement deferred " +
                $"character={source.DisplayName} " +
                $"monster={damageResult.ObjectId}: {ex.Message}");
            return null;
        }
    }

    internal static byte[] BuildMonsterReboundPacket(
        uint attackerObjectId,
        float attackerX,
        float attackerZ,
        uint monsterObjectId,
        uint reboundDamage,
        byte attackSelector) =>
        PacketBuilder.PhysicalDamage(
            attackerObjectId,
            attackerX,
            0f,
            attackerZ,
            monsterObjectId,
            reboundDamage,
            attackSelector,
            (byte)CombatHitOutcome.Normal);
}
