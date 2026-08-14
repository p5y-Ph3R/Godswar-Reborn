using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    private static readonly TimeSpan BasicAttackShockDuration =
        TimeSpan.FromMilliseconds(150);

    private static async Task CheckShockBasicAttackAdmissionAsync(
        PlayerRuntimeMode runtimeMode)
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            $"ShockBasicAttack{runtimeMode}",
            runtimeMode);
        fixture.Registry.InitializeMapMonsters(
            fixture.Character.CurrentMap,
            [CreateInterruptionMonster(fixture.Character)],
            TestTime);
        await using (var visibility =
            await fixture.Registry.BeginMonsterVisibilityTransitionAsync(
                fixture.Socket.Session,
                fixture.Character.CurrentMap,
                fixture.Character.PositionX,
                fixture.Character.PositionZ,
                CancellationToken.None) ??
            throw new InvalidOperationException(
                "Shock basic-attack visibility was unavailable."))
        {
            visibility.Commit();
        }

        var firstAppliedAt = DateTimeOffset.UtcNow;
        ApplyBasicAttackShock(
            fixture,
            firstAppliedAt,
            eventId: 91_001);
        Check.True(
            fixture.Registry.GetPlayerSkillCastControl(
                fixture.Socket.Session,
                firstAppliedAt) == PlayerSkillCastControl.Stunned,
            $"{runtimeMode} current fenced Shock maps to Stunned control");
        await fixture.BeginCastAsync();
        var blocked = await fixture.Socket.ReadPacketAsync();
        Check.True(
            ReadUInt16(blocked, 2) == Opcodes.SkillCastInterrupt &&
            !HasPendingSkillCast(fixture.Handler),
            $"{runtimeMode} Shock blocks new skill-cast admission");

        await Task.Delay(BasicAttackShockDuration +
            TimeSpan.FromMilliseconds(100));
        Check.True(
            fixture.Registry.GetPlayerSkillCastControl(
                fixture.Socket.Session,
                DateTimeOffset.UtcNow) == PlayerSkillCastControl.None,
            $"{runtimeMode} Shock control expires authoritatively");
        await fixture.BeginCastAsync();
        await AssertCastStartedAsync(
            fixture,
            $"{runtimeMode} post-Shock cast recovery");

        ApplyBasicAttackShock(
            fixture,
            DateTimeOffset.UtcNow,
            eventId: 91_002);
        await InvokePacketAsync(
            fixture.Handler,
            CreateInterruptionAttackPacket(fixture.Character));
        await AssertBasicAttackDidNotInterruptAsync(
            fixture,
            $"{runtimeMode} Shock-rejected basic attack");
        Check.True(
            !fixture.Registry.TryGetLatestAdmittedCombatRevision(
                fixture.Character.AccountId,
                fixture.Character.Id,
                out _),
            $"{runtimeMode} Shock rejection consumes no combat revision");

        await Task.Delay(BasicAttackShockDuration +
            TimeSpan.FromMilliseconds(100));
        await InvokePacketAsync(
            fixture.Handler,
            CreateInterruptionAttackPacket(fixture.Character));
        await AssertInterruptedAsync(
            fixture,
            $"{runtimeMode} expired-Shock basic attack recovery");
        Check.True(
            fixture.Registry.TryGetLatestAdmittedCombatRevision(
                fixture.Character.AccountId,
                fixture.Character.Id,
                out var admittedRevision) &&
            admittedRevision == 1,
            $"{runtimeMode} post-Shock admission consumes one revision");
    }

    private static void ApplyBasicAttackShock(
        InterruptFixture fixture,
        DateTimeOffset appliedAt,
        ulong eventId)
    {
        var character = fixture.Character;
        var sourceCharacterId = checked(character.Id + 100_000);
        var appliedAtMilliseconds = appliedAt.ToUnixTimeMilliseconds();
        var combatEvent = new DeterministicCombatEventContext(
            eventId,
            character.CurrentMap,
            sourceCharacterId,
            character.Id,
            appliedAtMilliseconds,
            CombatEventProvenance.DirectBasicAttack,
            Committed: true,
            IsPvp: false,
            default);
        var application = new ElementalEffectApplication(
            ElementKind.Lightning,
            ElementalEffectKind.Shock,
            sourceCharacterId,
            character.Id,
            eventId,
            appliedAtMilliseconds,
            checked(appliedAtMilliseconds +
                (long)BasicAttackShockDuration.TotalMilliseconds),
            EffectivePotencyBasisPoints: 1_000,
            ApplicationChanceBasisPoints: 10_000,
            TargetResistanceBasisPoints: 0,
            PeriodicDamageTotal: 0,
            PeriodicTickCount: 0,
            CombatEventProvenance.ElementalStatus);
        var fence = new ElementalCombatSessionFence(
            character.Id,
            character.CurrentMap,
            new PlayerOwnershipFence(
                character.CheckpointOwnerId,
                character.CheckpointOwnerGeneration));
        Check.True(
            fixture.Registry.TryApplyElementalApplication(
                fixture.Socket.Session,
                fence,
                combatEvent,
                application),
            "Shock basic-attack fixture applies one target-owned status");
    }
}
