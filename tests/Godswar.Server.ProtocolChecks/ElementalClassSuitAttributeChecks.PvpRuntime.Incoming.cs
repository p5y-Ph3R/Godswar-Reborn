using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private static async Task CheckPvpIncomingCadencesAsync()
    {
        await CheckPvpWaterGuardAsync();
        await CheckPvpWindEvasionAsync();
        await CheckPvpApolloLethalBarrierAsync();
    }

    private static async Task CheckPvpWaterGuardAsync()
    {
        await using var sourceSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = ElementalPvpRegistry();
        var source = ElementalPvpCharacter(
            1_521, 71, GameDefaults.SpartaCamp, ElementalPvpOwnership(21));
        var target = ElementalPvpCharacter(
            1_522, 72, GameDefaults.AthensCamp, ElementalPvpOwnership(22));
        target.CurrentHp = 90_000;
        target.MaxHp = 100_000;
        SetElementalProfile(
            target,
            LiveProfile((ElementKind.Water, 10, default)));
        BindPvpFixture(registry, sourceSocket.Session, source);
        BindPvpFixture(registry, targetSocket.Session, target);
        var at = new DateTimeOffset(
            2026, 8, 14, 3, 0, 0, TimeSpan.Zero);
        PvpBasicAttackDecision fifth = null!;
        for (var ordinal = 1; ordinal <= 5; ordinal++)
        {
            var revision = FindElementalPvpRevision(
                source, target, null, static value => value.Hit);
            fifth = await registry.ResolvePvpBasicAttackAsync(
                sourceSocket.Session,
                WorldObjectIds.ForPlayer(target.Id),
                source.PositionX,
                source.PositionZ,
                revision,
                at.AddMilliseconds(ordinal),
                CancellationToken.None);
        }

        Check.True(
            fifth.Resolution.Hit &&
            fifth.ElementalHealthRecovery > 0 &&
            fifth.AppliedDamage <
                (uint)fifth.Resolution.Evidence.DamageAfterAbsorption,
            "fifth admitted incoming PvP hit activates Poseidon guard and recovery");
        RemoveElementalPvpPlayers(
            registry,
            (sourceSocket.Session, source),
            (targetSocket.Session, target));
    }

    private static async Task CheckPvpWindEvasionAsync()
    {
        await using var sourceSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = ElementalPvpRegistry();
        var source = ElementalPvpCharacter(
            1_531, 81, GameDefaults.SpartaCamp, ElementalPvpOwnership(31));
        var target = ElementalPvpCharacter(
            1_532, 82, GameDefaults.AthensCamp, ElementalPvpOwnership(32));
        SetElementalProfile(
            target,
            LiveProfile((ElementKind.Wind, 10, default)));
        BindPvpFixture(registry, sourceSocket.Session, source);
        BindPvpFixture(registry, targetSocket.Session, target);
        var at = new DateTimeOffset(
            2026, 8, 14, 4, 0, 0, TimeSpan.Zero);
        PvpBasicAttackDecision sixth = null!;
        for (var ordinal = 1; ordinal <= 6; ordinal++)
        {
            var revision = FindElementalPvpRevision(
                source, target, null, static value => value.Hit);
            sixth = await registry.ResolvePvpBasicAttackAsync(
                sourceSocket.Session,
                WorldObjectIds.ForPlayer(target.Id),
                source.PositionX,
                source.PositionZ,
                revision,
                at.AddMilliseconds(ordinal),
                CancellationToken.None);
        }

        Check.True(
            sixth.Resolution.Outcome == CombatHitOutcome.Miss &&
            sixth.AppliedDamage == 0,
            "sixth otherwise-hitting PvP attack is authoritatively evaded by Aeolus");
        RemoveElementalPvpPlayers(
            registry,
            (sourceSocket.Session, source),
            (targetSocket.Session, target));
    }

    private static async Task CheckPvpApolloLethalBarrierAsync()
    {
        await using var sourceSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = ElementalPvpRegistry();
        var source = ElementalPvpCharacter(
            1_541, 91, GameDefaults.SpartaCamp, ElementalPvpOwnership(41),
            physicalAttack: 250_000);
        var target = ElementalPvpCharacter(
            1_542, 92, GameDefaults.AthensCamp, ElementalPvpOwnership(42));
        SetElementalProfile(
            target,
            LiveProfile((ElementKind.Light, 10, default)));
        var joinedAt = new DateTimeOffset(
            2026, 8, 14, 5, 0, 0, TimeSpan.Zero);
        BindPvpFixture(registry, sourceSocket.Session, source, joinedAt);
        BindPvpFixture(registry, targetSocket.Session, target, joinedAt);
        await registry.AdvancePlayerRecoveryOnceAsync(
            joinedAt.AddSeconds(6),
            CancellationToken.None);
        var revision = FindElementalPvpRevision(
            source, target, null, static value => value.Hit);
        var protectedHit = await registry.ResolvePvpBasicAttackAsync(
            sourceSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            source.PositionX,
            source.PositionZ,
            revision,
            joinedAt.AddSeconds(6).AddMilliseconds(1),
            CancellationToken.None);
        Check.True(
            protectedHit.Resolution.Hit &&
            target.CurrentHp == 1 &&
            !protectedHit.TargetKilled,
            "Apollo recovery barrier is consumed by live PvP lethal protection");
        RemoveElementalPvpPlayers(
            registry,
            (sourceSocket.Session, source),
            (targetSocket.Session, target));
    }

    private static async Task CheckPvpCreditedKillRecoveryAsync()
    {
        await using var sourceSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = ElementalPvpRegistry();
        var source = ElementalPvpCharacter(
            1_551, 101, GameDefaults.SpartaCamp, ElementalPvpOwnership(51));
        var target = ElementalPvpCharacter(
            1_552, 102, GameDefaults.AthensCamp, ElementalPvpOwnership(52));
        source.CurrentHp = 5_000;
        source.CurrentMp = 500;
        target.CurrentHp = 500;
        SetElementalProfile(
            source,
            LiveProfile((ElementKind.Dark, 10, default)));
        BindPvpFixture(registry, sourceSocket.Session, source);
        BindPvpFixture(registry, targetSocket.Session, target);
        var at = new DateTimeOffset(
            2026, 8, 14, 6, 0, 0, TimeSpan.Zero);
        var revision = FindElementalPvpRevision(
            source, target, null, static value => value.Hit);
        var killed = await registry.ResolvePvpBasicAttackAsync(
            sourceSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            source.PositionX,
            source.PositionZ,
            revision,
            at,
            CancellationToken.None);
        Check.True(
            killed.TargetKilled &&
            target.CurrentHp == 0 &&
            killed.ElementalHealthRecovery == 8_010 &&
            killed.ElementalManaRecovery == 80 &&
            source.CurrentHp == 13_010 &&
            source.CurrentMp == 580,
            "credited PvP kill commits Hades lifesteal and bounded HP/MP restoration exactly once");
        RemoveElementalPvpPlayers(
            registry,
            (sourceSocket.Session, source),
            (targetSocket.Session, target));
    }
}
