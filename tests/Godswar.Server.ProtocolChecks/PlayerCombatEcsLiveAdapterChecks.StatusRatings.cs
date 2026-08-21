using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerCombatEcsLiveAdapterChecks
{
    private static async Task CheckLiveRuntimeStatusRatingsAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var observedAt = DateTimeOffset.UtcNow;
        var monster = CreateLiveMonster(objectId: 9_750, x: 1f);
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs,
            gameplayCatalogs: GameplayContentTestFixtures.Runtime);
        registry.InitializeMapMonsters(
            checked((byte)monster.MapId),
            [monster],
            observedAt);
        Check.True(
            registry.TryGetMonsterSnapshot(
                checked((byte)monster.MapId),
                monster.ObjectId,
                out var target),
            "runtime-rating target exists");

        var character = CreateLiveCharacter();
        character.Id = 29_500;
        character.AccountId = 39_500;
        character.Name = "RuntimeRatingEcs";
        character.CalculatedStats = new CharacterStats
        {
            PhysicalAttack = 1,
            Hit = 1_000,
            Critical = 500,
            BasicAttackIntervalMilliseconds = 1_500,
            BasicAttackRange = 2.5f
        };
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: observedAt);
        await CommitLiveMonsterVisibilityAsync(
            registry,
            socket.Session,
            character);

        if (!SkillStatusEffectCatalog.TryGet(344, out var sacredZeal))
        {
            throw new InvalidOperationException(
                "Sacred Zeal rating fixture is missing.");
        }

        Check.True(
            await registry.ApplyRuntimeStatusAndPublishAsync(
                socket.Session,
                sacredZeal,
                observedAt,
                "pve-rating-sacred-zeal",
                CancellationToken.None),
            "ECS installs Sacred Zeal before combat hydration");
        var profile = registry.GameplayCatalogs.MonsterCombatProfiles
            .Resolve(target.Definition);
        var runtime = new ClientStatusAggregate(
            sacredZeal.HitBonus,
            sacredZeal.CriticalAppendBonus,
            0f);
        var expectedAttacker =
            CombatCharacterStatsAdapter.ApplyRuntimeAttackerModifiers(
                CombatCharacterStatsAdapter.FromCharacter(character),
                runtime);
        var expectedTarget = profile.ToTargetStats();

        var active = registry.ResolvePlayerCombatEcs(
            socket.Session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.BasicAttack(
                observedAt,
                target.ObjectId,
                character.PositionX,
                character.PositionZ));
        Check.True(
            active.BasicAttackResolution is { } activeResult &&
            activeResult.Resolution.FormulaVersion ==
                AuthoredCombatV1.Version &&
            activeResult.Resolution.Rolls.HitChanceBasisPoints ==
                AuthoredCombatV1.CalculateHitChanceBasisPoints(
                    expectedAttacker,
                    expectedTarget) &&
            activeResult.Resolution.Rolls.CriticalChanceBasisPoints ==
                AuthoredCombatV1.CalculateCriticalChanceBasisPoints(
                    expectedAttacker,
                    expectedTarget),
            "live ECS PvE combat consumes Sacred Zeal Hit and Critical");

        var expired = registry.ResolvePlayerCombatEcs(
            socket.Session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.BasicAttack(
                observedAt + sacredZeal.Duration,
                target.ObjectId,
                character.PositionX,
                character.PositionZ));
        var baseAttacker = CombatCharacterStatsAdapter.FromCharacter(
            character);
        Check.True(
            expired.BasicAttackResolution is { } expiredResult &&
            expiredResult.Resolution.Rolls.HitChanceBasisPoints ==
                AuthoredCombatV1.CalculateHitChanceBasisPoints(
                    baseAttacker,
                    expectedTarget) &&
            expiredResult.Resolution.Rolls.CriticalChanceBasisPoints ==
                AuthoredCombatV1.CalculateCriticalChanceBasisPoints(
                    baseAttacker,
                    expectedTarget),
            "live ECS excludes Sacred Zeal exactly at expiry");

        registry.Remove(socket.Session);
    }
}
