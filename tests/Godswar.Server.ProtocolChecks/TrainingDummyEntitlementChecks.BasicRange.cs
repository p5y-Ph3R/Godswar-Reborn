using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class TrainingDummyEntitlementChecks
{
    private static async Task CheckBasicAttackCollisionAllowanceAsync()
    {
        const float warriorWeaponRange = 1.7f;
        const float observedPlayerStandOff = 2.066f;
        var acceptedAttacker = Player(
            20,
            20,
            "WarriorAtPlayerStandOff",
            GameDefaults.SpartaCapitalMap,
            GameDefaults.SpartaCamp);
        acceptedAttacker.PositionX = 148f + observedPlayerStandOff;

        var accepted = await ResolveAsync(
            acceptedAttacker,
            Dummy(7001),
            Policy());
        Check.True(
            accepted.Accepted &&
            accepted.Eligibility.EntitlementKind ==
                PvpEntitlementKind.TrainingDummy,
            "an exact dummy admits a Warrior basic attack at the native " +
            "player-collision stand-off");

        var beyondAttacker = Player(
            21,
            21,
            "WarriorBeyondDummyAllowance",
            GameDefaults.SpartaCapitalMap,
            GameDefaults.SpartaCamp);
        beyondAttacker.PositionX = 148f + warriorWeaponRange +
            SkillCombatResolver.TargetCollisionAllowance + 0.01f;

        var beyond = await ResolveAsync(
            beyondAttacker,
            Dummy(7001),
            Policy());
        Check.True(
            !beyond.Accepted &&
            beyond.RejectionReason ==
                PvpBasicAttackRejectionReason.OutOfRange &&
            beyond.Eligibility.EntitlementKind ==
                PvpEntitlementKind.TrainingDummy,
            "exact-dummy collision allowance remains bounded");

        var ordinaryAttacker = Player(
            22,
            22,
            "OrdinaryWarriorAtStandOff",
            7,
            GameDefaults.SpartaCamp);
        ordinaryAttacker.PositionX = observedPlayerStandOff;
        ordinaryAttacker.PositionZ = 0f;
        var ordinaryTarget = Player(
            23,
            23,
            "OrdinaryAthenianTarget",
            7,
            GameDefaults.AthensCamp);
        ordinaryTarget.PositionX = 0f;
        ordinaryTarget.PositionZ = 0f;

        var ordinary = await ResolveAsync(
            ordinaryAttacker,
            ordinaryTarget,
            Policy());
        Check.True(
            !ordinary.Accepted &&
            ordinary.RejectionReason ==
                PvpBasicAttackRejectionReason.OutOfRange &&
            ordinary.Eligibility.EntitlementKind !=
                PvpEntitlementKind.TrainingDummy,
            "ordinary PvP receives no training-dummy collision allowance");
    }
}
