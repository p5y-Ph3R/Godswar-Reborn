using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly record struct MonsterAttackEcsTransaction(
        GameSessionContext? TargetContext,
        PlayerMonsterDamageEcsDecision Decision,
        CombatResolution Resolution,
        uint Damage,
        uint ReboundDamage,
        bool ReplayRejected,
        bool AuthorityRejected,
        MonsterIncomingElementalPostCommit ElementalPostCommit,
        Task DeathInterruptionTask,
        MedusaMonsterPlayerHitCommitOutcome? MedusaOutcome,
        MedusaMonsterPlayerSourceAuthority? MedusaSourceAuthority,
        MedusaMechanicHitResult? MedusaMechanicsResult,
        RegistryMedusaCapturedEffectInterruption? MedusaEffectInterruption,
        bool RideStatusRemoved,
        Exception? ElementalPostCommitError,
        MedusaRunTerminalClearWorkItem? TerminalClear,
        WorldInstanceId TimedOutMedusaInstance,
        bool MedusaOwnerInvariantFault);

    private sealed class MonsterAttackTargetUnavailableException(
        int targetCharacterId) : Exception
    {
        public int TargetCharacterId { get; } = targetCharacterId;
    }

    private sealed record MonsterAttackEventIdentity(ulong Value);
}
