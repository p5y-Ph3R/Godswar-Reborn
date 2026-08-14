using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerCombatEcsLiveAdapterChecks
{
    private static async Task CheckReconnectSafeCombatAdmissionAsync()
    {
        await using var firstSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var replacementSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var monster = CreateLiveMonster(9_740, x: 1f);
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
                out var initialTarget),
            "combat-admission fixture exposes its monster");

        var character = CreateLiveCharacter();
        character.CalculatedStats = CreateLiveResolutionStats();
        var profile = registry.GameplayCatalogs.MonsterCombatProfiles
            .Resolve(initialTarget.Definition);
        character.Id = FindCharacterIdForOutcome(
            CombatHitOutcome.Miss,
            initialTarget,
            profile,
            character);
        character.AccountId = character.Id + 20_000;
        character.Name = "ReconnectCombatAdmission";

        registry.JoinMap(
            firstSocket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: Start);
        await CommitLiveMonsterVisibilityAsync(
            registry,
            firstSocket.Session,
            character);
        var first = registry.ResolvePlayerCombatEcs(
            firstSocket.Session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.BasicAttack(
                Start,
                initialTarget.ObjectId,
                character.PositionX,
                character.PositionZ));
        Check.True(
            first.IntentAccepted &&
            first.BasicAttackResolution is { } firstResolution &&
            firstResolution.Resolution.Outcome == CombatHitOutcome.Miss,
            "an admitted miss consumes the first process revision");
        Check.True(
            registry.TryGetLatestAdmittedCombatRevision(
                character.AccountId,
                character.Id,
                out var firstRevision) &&
            firstRevision == 1,
            "process authority records the first admitted attempt");

        var rejected = registry.ResolvePlayerCombatEcs(
            firstSocket.Session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            first.NextBasicAttackAt,
            PlayerCombatEcsRequest.BasicAttack(
                Start + TimeSpan.FromMilliseconds(1),
                initialTarget.ObjectId,
                character.PositionX,
                character.PositionZ));
        Check.True(
            !rejected.IntentAccepted &&
            rejected.RejectionReason ==
                PlayerCombatRejectionReason.CooldownActive,
            "cooldown spam is rejected before combat admission");
        Check.True(
            registry.TryGetLatestAdmittedCombatRevision(
                character.AccountId,
                character.Id,
                out var afterRejectedRevision) &&
            afterRejectedRevision == firstRevision,
            "rejected cooldown spam cannot advance deterministic rolls");

        registry.Remove(firstSocket.Session);
        registry.JoinMap(
            replacementSocket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: Start + TimeSpan.FromSeconds(2));
        await CommitLiveMonsterVisibilityAsync(
            registry,
            replacementSocket.Session,
            character);
        Check.True(
            registry.TryGetMonsterSnapshot(
                character.CurrentMap,
                initialTarget.ObjectId,
                out var unchangedTarget) &&
            unchangedTarget.HealthRevision ==
                initialTarget.HealthRevision,
            "the admitted miss leaves the target revision unchanged");
        var replacement = registry.ResolvePlayerCombatEcs(
            replacementSocket.Session,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            DateTimeOffset.MinValue,
            PlayerCombatEcsRequest.BasicAttack(
                Start + TimeSpan.FromSeconds(2),
                unchangedTarget.ObjectId,
                character.PositionX,
                character.PositionZ));
        Check.True(
            replacement.IntentAccepted &&
            replacement.BasicAttackResolution is not null,
            "same-character replacement admits the next attempt");
        var replacementResolution =
            replacement.BasicAttackResolution ??
            throw new InvalidOperationException(
                "The replacement attack lost its resolution.");
        var expectedEventId =
            CombatEventIdentity.ForPlayerMonsterBasicAttack(
                character.Id,
                unchangedTarget.ObjectId,
                unchangedTarget.SpawnGeneration,
                unchangedTarget.HealthRevision,
                admittedCombatRevision: 2);
        Check.Equal(
            expectedEventId,
            replacementResolution.Resolution.EventId,
            "session replacement cannot reset or fish the combat roll");
        Check.True(
            registry.TryGetLatestAdmittedCombatRevision(
                character.AccountId,
                character.Id,
                out var replacementRevision) &&
            replacementRevision == 2,
            "legacy, ECS, and PvP share the registry-owned sequence source");
        registry.Remove(replacementSocket.Session);
    }
}
