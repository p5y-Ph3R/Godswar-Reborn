using System.Reflection;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckBoundMedusaElementalSuppressionAsync()
    {
        await using var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs,
            worldInstanceOptions: new WorldInstanceRuntimeOptions
            {
                RealmId = RealmId.Tempest.Value,
                MaximumRuntimes = 2,
                MaximumPlayerAssignments = 5,
                MaximumRetiredInstanceIds = 8,
                DefaultOpenWorldPlayerCapacity = 5,
                MailboxCapacity = 16,
                OwnerInvocationTimeoutMilliseconds = 2_000,
                ShutdownDrainTimeoutMilliseconds = 2_000,
                MaximumFanoutConcurrency = 1
            });
        var preparation = new DamageAuthorityPreparation();
        var created = await registry.CreatePreparedLocalWorldInstanceAsync(
            RealmId.Tempest,
            new WorldMapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            preparation,
            CancellationToken.None);
        var instanceId = created.InstanceId ??
            throw new InvalidOperationException(
                "Elemental suppression Medusa instance was not created.");
        var runtime = RequiredRegistryRuntime(registry, instanceId);
        var committedAt = runtime.Map.Descriptor.CreatedAt.AddSeconds(1);

        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport());
        var character = CreateRegistryDamageCharacter(101, mapId: 200);
        SetGuaranteedFireProfile(character);
        registry.JoinWorldInstance(
            session,
            character.AccountId,
            character,
            objectId: 0x7D01,
            instanceId,
            worldReady: true,
            joinedAt: committedAt);
        using var elementalAuthority =
            registry.CapturePveElementalCommitAuthority(
                session,
                character,
                allowUnownedCompatibility: true) ??
            throw new InvalidOperationException(
                "Medusa elemental authority was not captured.");
        Check.True(registry.TryCapturePlayerMonsterTarget(
                session,
                mapId: 200,
                objectId: preparation.Inputs.RunSpawns[0].ObjectId,
                out var target,
                out var combatAuthority),
            "Medusa elemental fixture captures its bound target");
        Check.True(registry.TryCommitPlayerMonsterDamageGuarded(
                session,
                mapId: 200,
                target.ObjectId,
                target.RuntimeInstanceId,
                character.Id,
                target.SpawnGeneration,
                target.HealthRevision,
                combatAuthority,
                committedAt,
                Resolution(CombatDamageChannel.Physical, damage: 100),
                out var primaryCommit) &&
            primaryCommit.DamageResult is not null,
            "Medusa elemental fixture commits its typed primary damage");
        var primary = primaryCommit.DamageResult ??
            throw new InvalidOperationException(
                "Typed Medusa primary damage was unavailable.");

        var elemental = registry.CommitPveElementalHits(
            elementalAuthority,
            CombatEventProvenance.DirectBasicAttack,
            [new PveElementalCommittedHit(0xB017_0001, 0, primary)],
            committedAt);
        var afterPrimary = RequiredMonster(runtime.Map, target.ObjectId);
        Check.True(
            elemental.Applications.Count == 0 &&
            elemental.DamageCommits.Count == 0 &&
            elemental.ControlCommits.Count == 0 &&
            !elemental.SourceRecovery.Applied &&
            PveElementalStateCount(registry) == 0,
            "bound Medusa suppresses guaranteed Burn and all elemental secondaries before status-ledger mutation");

        await registry.AdvancePlayerRecoveryOnceAsync(
            committedAt.AddSeconds(4),
            CancellationToken.None);
        var afterPeriodicAdvance = RequiredMonster(
            runtime.Map,
            target.ObjectId);
        Check.True(
            SameMonsterHealth(afterPrimary, afterPeriodicAdvance) &&
            PveElementalStateCount(registry) == 0,
            "periodic advancement has no consumed or rejected Burn tick for a bound Medusa target");
    }

    private static void SetGuaranteedFireProfile(GameCharacter character)
    {
        var effects = Enum.GetValues<ElementKind>().ToDictionary(
            static element => element,
            static _ => default(ElementalEffectTotals));
        effects[ElementKind.Fire] = new(
            EffectPotencyBasisPoints: 1_000,
            EffectResistanceBasisPoints: 0,
            ApplicationChanceBasisPoints: 10_000);
        var counts = Enum.GetValues<ElementKind>().ToDictionary(
            static element => element,
            static _ => 0);
        counts[ElementKind.Fire] = 1;
        var resonances = Enum.GetValues<ElementKind>().ToDictionary(
            static element => element,
            element => ElementalResonanceCatalog.ActiveFor(
                element,
                counts[element]));
        var profile = new ElementalEquipmentProfile(
            effects,
            counts,
            resonances);
        var property = typeof(GameCharacter).GetProperty(
            nameof(GameCharacter.ElementalEquipment),
            BindingFlags.Instance | BindingFlags.Public) ??
            throw new InvalidOperationException(
                "Elemental equipment property was not found.");
        property.SetValue(character, profile);
    }

    private static int PveElementalStateCount(
        GameSessionRegistry registry)
    {
        var field = typeof(GameSessionRegistry).GetField(
                "_pveMonsterElementalStates",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "PvE elemental state ledger was not found.");
        var states = field.GetValue(registry) ??
            throw new InvalidOperationException(
                "PvE elemental state ledger was unavailable.");
        return (int)(states.GetType().GetProperty("Count")?.GetValue(states) ??
            throw new InvalidOperationException(
                "PvE elemental state count was unavailable."));
    }
}
