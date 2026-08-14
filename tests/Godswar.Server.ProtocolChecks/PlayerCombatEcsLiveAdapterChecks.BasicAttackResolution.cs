using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerCombatEcsLiveAdapterChecks
{
    private static async Task CheckLiveBasicAttackResolutionAsync()
    {
        await CheckLiveBasicAttackOutcomeAsync(
            CombatHitOutcome.Normal);
        await CheckLiveBasicAttackOutcomeAsync(
            CombatHitOutcome.Critical);
        await CheckLiveBasicAttackOutcomeAsync(
            CombatHitOutcome.Miss);
    }

    private static async Task CheckLiveBasicAttackOutcomeAsync(
        CombatHitOutcome expectedOutcome)
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var monster = CreateLiveMonster(
            objectId: 9_700u + (uint)expectedOutcome,
            x: 1f);
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs,
            gameplayCatalogs: GameplayContentTestFixtures.Runtime);
        registry.InitializeMapMonsters(
            checked((byte)monster.MapId),
            [monster],
            Start);
        Check.True(
            registry.TryGetMonsterSnapshot(
                checked((byte)monster.MapId),
                monster.ObjectId,
                out var before),
            $"{expectedOutcome} profile fixture exposes its monster");

        var character = CreateLiveCharacter();
        character.CalculatedStats = CreateLiveResolutionStats();
        var profile = registry.GameplayCatalogs.MonsterCombatProfiles
            .Resolve(before.Definition);
        character.Id = FindCharacterIdForOutcome(
            expectedOutcome,
            before,
            profile,
            character);
        character.AccountId = character.Id + 10_000;
        character.Name = $"LiveBasic{expectedOutcome}";

        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: Start);
        await CommitLiveMonsterVisibilityAsync(
            registry,
            socket.Session,
            character);

        var decision = registry.ResolvePlayerCombatEcs(
            socket.Session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.BasicAttack(
                Start,
                before.ObjectId,
                character.PositionX,
                character.PositionZ));
        Check.True(decision.IntentAccepted,
            $"{expectedOutcome} live basic attack is admitted");
        Check.True(
            decision.BasicAttackResolution is { } resolved &&
            resolved.Resolution.Outcome == expectedOutcome,
            $"{expectedOutcome} survives the live ECS adapter");

        var actual = decision.BasicAttackResolution!.Value.Resolution;
        var eventId = CombatEventIdentity.ForPlayerMonsterBasicAttack(
            character.Id,
            before.ObjectId,
            before.SpawnGeneration,
            before.HealthRevision,
            admittedCombatRevision: 1);
        var expected = MonsterCombatResolver.ResolvePlayerBasicAttack(
            character,
            profile.ToTargetStats(),
            eventId);
        Check.Equal(expected, actual,
            $"{expectedOutcome} live ECS result matches shared resolver");
        Check.Equal(
            AuthoredCombatV1.CalculateEffectiveDefense(
                profile.PhysicalDefense,
                character.CalculatedStats.IgnorePhysicalDefense),
            actual.Evidence.EffectiveDefense,
            "live ECS hydrates the pinned monster defense profile");

        Check.True(
            registry.TryGetMonsterSnapshot(
                checked((byte)monster.MapId),
                monster.ObjectId,
                out var after),
            $"{expectedOutcome} target remains queryable");
        if (expectedOutcome == CombatHitOutcome.Miss)
        {
            Check.Equal(0, decision.Hits.Length,
                "live miss produces no applied health hit");
            Check.Equal(before.CurrentHealth, after.CurrentHealth,
                "live miss cannot change monster health");
            Check.Equal(before.HealthRevision, after.HealthRevision,
                "live miss cannot advance monster health revision");
            Check.Equal(uint.MaxValue, actual.CapturedDamageValue,
                "live miss exposes the captured wire sentinel");
        }
        else
        {
            Check.Equal(1, decision.Hits.Length,
                $"live {expectedOutcome} applies one guarded hit");
            Check.Equal(actual.Damage,
                decision.Hits[0].ReportedDamage,
                $"live {expectedOutcome} mutation uses resolved damage");
            Check.Equal(before.HealthRevision + 1,
                after.HealthRevision,
                $"live {expectedOutcome} advances health once");
        }

        registry.Remove(socket.Session);
    }

    private static int FindCharacterIdForOutcome(
        CombatHitOutcome outcome,
        MonsterRuntimeSnapshot target,
        MonsterCombatProfile profile,
        GameCharacter character)
    {
        var attacker = CombatCharacterStatsAdapter.FromCharacter(character);
        for (var characterId = 20_000;
             characterId < 30_000;
             characterId++)
        {
            var eventId = CombatEventIdentity.ForPlayerMonsterBasicAttack(
                characterId,
                target.ObjectId,
                target.SpawnGeneration,
                target.HealthRevision,
                admittedCombatRevision: 1);
            if (PlayerCombatRules.ResolveBasicAttack(
                    attacker,
                    profile.ToTargetStats(),
                    eventId).Outcome == outcome)
            {
                return characterId;
            }
        }

        throw new InvalidOperationException(
            $"No live deterministic {outcome} fixture was found.");
    }

    private static CharacterStats CreateLiveResolutionStats() =>
        new()
        {
            PhysicalAttack = 1_000,
            MagicAttack = 1_100,
            Hit = 0,
            Critical = int.MaxValue,
            PhysicalDamageBonus = 1_000,
            PhysicalAppendDamage = 20,
            IgnorePhysicalDefense = 2_000,
            CriticalDamagePercent = 1_000,
            CriticalDamageFlat = 10
        };
}
