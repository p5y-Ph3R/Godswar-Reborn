using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private static bool IsExactCapturedVitalsDecision(
        GameCharacter character,
        in MedusaMonsterPlayerTargetAuthority target,
        MedusaCapturedPlayerVitalsCommit capability,
        int beforeHealth,
        long beforeVitalsRevision,
        in PlayerMonsterDamageEcsDecision decision)
    {
        var request = capability.Request;
        if (decision.AttackEventId != request.AttackEventId ||
            decision.MonsterObjectId != request.MonsterObjectId ||
            decision.RequestedDamage != request.ResolvedDamage ||
            decision.DecisionSequence == 0 ||
            decision.BeforeHealth != beforeHealth ||
            decision.BeforeVitalsRevision != beforeVitalsRevision ||
            decision.BeforeLifeRevision != target.LifeRevision)
        {
            return false;
        }

        var actualLifeRevision = capability.CurrentLifeRevision;
        if (!decision.Applied)
        {
            return !decision.Killed &&
                decision.RejectionReason !=
                    MonsterPlayerDamageRejectionReason.None &&
                decision.AppliedDamage == 0 &&
                decision.AfterHealth == beforeHealth &&
                decision.FinalHealth == beforeHealth &&
                decision.AfterVitalsRevision == beforeVitalsRevision &&
                decision.FinalVitalsRevision == beforeVitalsRevision &&
                decision.AfterLifeRevision == target.LifeRevision &&
                actualLifeRevision == target.LifeRevision &&
                character.CurrentHp == beforeHealth &&
                character.VitalsRevision == beforeVitalsRevision &&
                decision.PetHealing is null;
        }

        if (decision.RejectionReason !=
                MonsterPlayerDamageRejectionReason.None ||
            decision.AppliedDamage == 0 ||
            decision.AppliedDamage > decision.RequestedDamage ||
            decision.LastAttackEventId != decision.AttackEventId ||
            (long)decision.AfterHealth !=
                (long)beforeHealth - decision.AppliedDamage ||
            decision.AfterVitalsRevision !=
                checked(beforeVitalsRevision + 1) ||
            character.CurrentHp != decision.FinalHealth ||
            character.VitalsRevision != decision.FinalVitalsRevision ||
            actualLifeRevision != decision.AfterLifeRevision ||
            decision.Killed != (decision.AfterHealth == 0))
        {
            return false;
        }

        if (decision.Killed)
        {
            return decision.AfterLifeRevision ==
                    checked(target.LifeRevision + 1) &&
                decision.PetHealing is null &&
                decision.FinalHealth == 0 &&
                decision.FinalVitalsRevision ==
                    decision.AfterVitalsRevision;
        }

        if (decision.AfterLifeRevision != target.LifeRevision)
        {
            return false;
        }

        if (decision.PetHealing is not { } healing)
        {
            return decision.FinalHealth == decision.AfterHealth &&
                decision.FinalVitalsRevision ==
                    decision.AfterVitalsRevision;
        }

        return healing.BeforeHealth == decision.AfterHealth &&
            healing.BeforeVitalsRevision ==
                decision.AfterVitalsRevision &&
            healing.AppliedHealing > 0 &&
            healing.ResolvedHealing >= healing.AppliedHealing &&
            healing.AppliedHealing ==
                healing.AfterHealth - healing.BeforeHealth &&
            healing.AfterHealth == decision.FinalHealth &&
            healing.AfterHealth <= character.MaxHp &&
            healing.AfterVitalsRevision ==
                decision.FinalVitalsRevision &&
            healing.AfterVitalsRevision ==
                checked(decision.AfterVitalsRevision + 1) &&
            healing.AppliedAt >= capability.Request.ResolvedAt &&
            healing.CooldownReadyAt >= healing.AppliedAt;
    }

    private static PlayerMonsterDamageEcsDecision
        NormalizeIrreversibleVitalsDecision(
            GameCharacter character,
            MedusaCapturedPlayerVitalsCommit capability,
            in MedusaMonsterPlayerSourceAuthority source,
            in MedusaMonsterPlayerTargetAuthority target,
            int beforeHealth,
            long beforeVitalsRevision)
    {
        var appliedDamage = checked((uint)Math.Max(
            0,
            beforeHealth - character.CurrentHp));
        return new PlayerMonsterDamageEcsDecision(
            // This helper is entered only after a proven post-capability
            // state change. Treat that boundary as irreversible even when a
            // malformed decision claimed otherwise, and rebuild every
            // lifecycle field from the actual Character/capability state.
            Applied: true,
            Killed: character.CurrentHp <= 0,
            MonsterPlayerDamageRejectionReason.None,
            DecisionSequence: source.AttackEventId,
            source.AttackEventId,
            source.ObjectId,
            capability.Request.ResolvedDamage,
            appliedDamage,
            beforeHealth,
            character.CurrentHp,
            beforeVitalsRevision,
            character.VitalsRevision,
            target.LifeRevision,
            capability.CurrentLifeRevision,
            LastAttackEventId: source.AttackEventId);
    }
}
