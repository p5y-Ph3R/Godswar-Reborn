using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private static async Task CheckPvpShockControlAuthorityAsync()
    {
        await using var sourceSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = ElementalPvpRegistry();
        var source = ElementalPvpCharacter(
            1_561,
            111,
            GameDefaults.SpartaCamp,
            ElementalPvpOwnership(61));
        var target = ElementalPvpCharacter(
            1_562,
            112,
            GameDefaults.AthensCamp,
            ElementalPvpOwnership(62));
        source.CurrentHp = source.MaxHp = 100_000;
        target.CurrentHp = target.MaxHp = 100_000;
        SetElementalProfile(
            source,
            LiveProfile((ElementKind.Lightning, 10, default)));
        BindPvpFixture(registry, sourceSocket.Session, source);
        BindPvpFixture(registry, targetSocket.Session, target);

        var interruptionRequests = 0;
        var pendingCast = false;
        var pendingCastClaims = 0;
        var interruptionReasons = new List<SkillCastInterruptionReason>();
        registry.RegisterSkillCastInterruptionSink(
            targetSocket.Session,
            (reason, _, _) =>
            {
                interruptionRequests++;
                interruptionReasons.Add(reason);
                if (pendingCast)
                {
                    pendingCast = false;
                    pendingCastClaims++;
                }

                return Task.CompletedTask;
            });
        try
        {
            var at = new DateTimeOffset(
                2026, 8, 14, 3, 0, 0, TimeSpan.Zero);
            PvpBasicAttackDecision fourth = null!;
            for (var ordinal = 1; ordinal <= 4; ordinal++)
            {
                if (ordinal == 4)
                {
                    pendingCast = true;
                }

                var revision = FindElementalPvpRevision(
                    source,
                    target,
                    ElementKind.Lightning,
                    static value => value.Hit);
                fourth = await registry.ResolvePvpBasicAttackAsync(
                    sourceSocket.Session,
                    WorldObjectIds.ForPlayer(target.Id),
                    source.PositionX,
                    source.PositionZ,
                    revision,
                    at.AddMilliseconds(ordinal),
                    CancellationToken.None);
                Check.True(
                    registry.GetPlayerSkillCastControl(
                        targetSocket.Session,
                        at.AddMilliseconds(ordinal)) ==
                            PlayerSkillCastControl.Stunned,
                    "committed PvP Shock blocks new cast admission for its duration");
            }

            Check.True(
                fourth.ElementalControlCommits is
                    [{ StunMilliseconds: 1_000 }] &&
                interruptionRequests == 4 &&
                interruptionReasons.All(static reason =>
                    reason == SkillCastInterruptionReason.Stunned) &&
                pendingCastClaims == 1 &&
                !pendingCast,
                "generic and resonance Shock claim a pending cast once per newly committed hit without duplicate interruption");
            var afterShock = at.AddSeconds(2);
            Check.True(
                registry.GetPlayerSkillCastControl(
                    targetSocket.Session,
                    afterShock) == PlayerSkillCastControl.None,
                "expired PvP Shock restores new cast admission");

            SetElementalProfile(
                source,
                LiveProfile((
                    ElementKind.Earth,
                    0,
                    new ElementalEffectTotals(1_000, 0, 10_000))));
            var beforeNonShock = interruptionRequests;
            var earthRevision = FindElementalPvpRevision(
                source,
                target,
                ElementKind.Earth,
                static value => value.Hit);
            var earth = await registry.ResolvePvpBasicAttackAsync(
                sourceSocket.Session,
                WorldObjectIds.ForPlayer(target.Id),
                source.PositionX,
                source.PositionZ,
                earthRevision,
                afterShock,
                CancellationToken.None);
            Check.True(
                earth.ElementalApplications.Any(static application =>
                    application.Effect == ElementalEffectKind.Fracture),
                "non-Shock PvP fixture commits one Fracture application");
            Check.Equal(
                beforeNonShock,
                interruptionRequests,
                "non-Shock PvP elemental applications never request cast interruption");

            var stunnedAt = afterShock.AddSeconds(1);
            await ApplyPvpControlStatusAsync(
                registry,
                sourceSocket.Session,
                statusId: 331,
                kind: 51,
                stunnedAt);
            var deniedRevisionCalls = 0;
            var beforeDeniedHealth = target.CurrentHp;
            var denied = await registry.ResolvePvpBasicAttackAsync(
                sourceSocket.Session,
                WorldObjectIds.ForPlayer(target.Id),
                source.PositionX,
                source.PositionZ,
                () =>
                {
                    deniedRevisionCalls++;
                    return 999_001;
                },
                stunnedAt,
                CancellationToken.None);
            Check.True(
                !denied.Accepted &&
                denied.RejectionReason ==
                    PvpBasicAttackRejectionReason.ElementalControl &&
                deniedRevisionCalls == 0 &&
                target.CurrentHp == beforeDeniedHealth,
                "serialized standard Stunned PvP rejection consumes no revision or mutation");

            var silencedAt = stunnedAt.AddSeconds(31);
            await ApplyPvpControlStatusAsync(
                registry,
                sourceSocket.Session,
                statusId: 360,
                kind: 52,
                silencedAt);
            var silenceRevisionCalls = 0;
            var allowedWhileSilenced =
                await registry.ResolvePvpBasicAttackAsync(
                    sourceSocket.Session,
                    WorldObjectIds.ForPlayer(target.Id),
                    source.PositionX,
                    source.PositionZ,
                    () =>
                    {
                        silenceRevisionCalls++;
                        return 999_002;
                    },
                    silencedAt,
                    CancellationToken.None);
            Check.True(
                allowedWhileSilenced.Accepted &&
                silenceRevisionCalls == 1,
                "Silenced remains skill-only control and permits one PvP basic admission");
        }
        finally
        {
            registry.UnregisterSkillCastInterruptionSink(
                targetSocket.Session);
            RemoveElementalPvpPlayers(
                registry,
                (sourceSocket.Session, source),
                (targetSocket.Session, target));
        }
    }

    private static async Task ApplyPvpControlStatusAsync(
        GameSessionRegistry registry,
        Godswar.Server.Networking.ClientSession session,
        uint statusId,
        int kind,
        DateTimeOffset now)
    {
        var definition = new SkillStatusEffectDefinition(
            SkillId: 9_999,
            StatusId: statusId,
            Kind: kind,
            Priority: 1,
            Beneficial: false,
            Duration: TimeSpan.FromSeconds(30),
            Cooldown: TimeSpan.Zero,
            HitBonus: 0,
            CriticalAppendBonus: 0);
        Check.True(
            await registry.ApplyRuntimeStatusAndPublishAsync(
                session,
                definition,
                now,
                $"pvp-control-{statusId}",
                CancellationToken.None),
            $"PvP control fixture applies status {statusId}");
    }
}
