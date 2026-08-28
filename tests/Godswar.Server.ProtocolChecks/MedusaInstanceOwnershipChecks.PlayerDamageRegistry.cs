using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckRegistryMembershipCommitRaceAsync()
    {
        await using var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs);
        var authored = CreateAttachmentFixture();
        var definition = authored.Inputs.Definitions[0];
        var mapId = checked((byte)definition.MapId);
        Check.True(
            registry.InitializeMapMonsters(
                mapId,
                [definition],
                StartedAt) == 1,
            "registry race fixture initializes one ordinary target");

        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport());
        var character = CreateRegistryDamageCharacter(
            characterId: 101,
            mapId);
        registry.JoinMap(
            session,
            character.AccountId,
            character,
            objectId: 0x7B01,
            joinedAt: StartedAt);
        Check.True(registry.TryGetSessionWorldInstanceId(
                session,
                out var instanceId),
            "registry race resolves the exact session instance");
        Check.True(TryGetRegistryRuntime(
                registry,
                instanceId,
                out var runtime),
            "registry race resolves the exact runtime");
        Check.True(registry.TryCapturePlayerMonsterTarget(
                session,
                mapId,
                definition.ObjectId,
                out var target,
                out var authority),
            "registry race resolves the exact session instance and target");

        var monsterGate = RequiredMapGate(
            runtime.Map,
            "_monsterRuntimeGate");
        var membershipGate = RequiredMapGate(
            runtime.Map,
            "_membershipGate");
        var source = Resolution(
            CombatDamageChannel.Physical,
            damage: 1);
        Task<(bool Applied, MedusaPlayerMonsterDamageCommit Commit)>?
            damageTask = null;
        Task? egressTask = null;

        Monitor.Enter(monsterGate);
        try
        {
            damageTask = Task.Run(() =>
            {
                var applied = registry
                    .TryCommitPlayerMonsterDamageGuarded(
                        session,
                        mapId,
                        target.ObjectId,
                        target.RuntimeInstanceId,
                        character.Id,
                        target.SpawnGeneration,
                        target.HealthRevision,
                        authority,
                        StartedAt.AddSeconds(1),
                        source,
                        out var commit);
                return (applied, commit);
            });
            Check.True(
                SpinWait.SpinUntil(
                    () => GateIsHeldByAnotherThread(membershipGate),
                    TimeSpan.FromSeconds(2)),
                "registry damage enters its exact map membership before mailbox release");

            using var egressStarted = new ManualResetEventSlim();
            egressTask = Task.Run(() =>
            {
                egressStarted.Set();
                registry.Remove(session);
            });
            Check.True(
                egressStarted.Wait(TimeSpan.FromSeconds(2)) &&
                !egressTask.Wait(TimeSpan.FromMilliseconds(100)),
                "registry egress waits while damage retains identity authority across its mailbox call");
        }
        finally
        {
            Monitor.Exit(monsterGate);
        }

        var committed = await damageTask!.WaitAsync(
            TimeSpan.FromSeconds(2));
        await egressTask!.WaitAsync(TimeSpan.FromSeconds(2));
        Check.True(
            committed.Applied &&
            committed.Commit.Outcome ==
                MedusaPlayerMonsterDamageOutcome.AppliedUnbound &&
            committed.Commit.DamageResult is { } applied &&
            applied.BeforeHealth - applied.AfterHealth == 1 &&
            !registry.IsSessionInWorldInstance(session, instanceId),
            "damage linearizes before egress without a registry/mailbox deadlock");

        Check.True(
            runtime.Map.TryGetMonsterSnapshot(
                target.ObjectId,
                out var afterCommit),
            "old instance target remains inspectable after egress");
        var staleApplied = registry.TryCommitPlayerMonsterDamageGuarded(
            session,
            mapId,
            afterCommit.ObjectId,
            afterCommit.RuntimeInstanceId,
            character.Id,
            afterCommit.SpawnGeneration,
            afterCommit.HealthRevision,
            authority,
            StartedAt.AddSeconds(2),
            source,
            out var staleCommit);
        Check.True(
            !staleApplied &&
            staleCommit == default &&
            runtime.Map.TryGetMonsterSnapshot(
                afterCommit.ObjectId,
                out var unchanged) &&
            unchanged.CurrentHealth == afterCommit.CurrentHealth &&
            unchanged.HealthRevision == afterCommit.HealthRevision,
            "the stale session cannot route through map-id fallback or mutate its old instance");
    }

    private static bool TryGetRegistryRuntime(
        GameSessionRegistry registry,
        Godswar.Server.Domain.World.Instances.WorldInstanceId instanceId,
        out WorldInstanceRuntime runtime)
    {
        var directory = typeof(GameSessionRegistry).GetField(
                "_worldInstanceDirectory",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(registry) as LocalWorldInstanceRuntimeDirectory;
        if (directory is not null &&
            directory.TryFind(instanceId, out runtime!))
        {
            return true;
        }

        runtime = default!;
        return false;
    }

    private static GameCharacter CreateRegistryDamageCharacter(
        int characterId,
        byte mapId) => new()
    {
        Id = characterId,
        AccountId = checked(20_000 + characterId),
        Name = $"MedusaRegistry{characterId}",
        CreatedUtc = StartedAt.UtcDateTime,
        Camp = GameDefaults.SpartaCamp,
        CurrentMap = mapId,
        PositionX = 1,
        PositionZ = 1,
        Level = 120,
        CurrentHp = 10_000,
        MaxHp = 10_000,
        CurrentMp = 10_000,
        MaxMp = 10_000,
        Equipment = string.Empty,
        KitBag = string.Empty
    };
}
