using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Systems.Combat;

/// <summary>
/// Applies automatic pet Healing after an accepted, nonlethal monster hit.
/// The preceding damage event is the sole trigger, so rejected, duplicate,
/// stale, and zero-damage intents cannot consume the cooldown.
/// </summary>
internal sealed class PetHealingTalentSystem : IEcsSystem
{
    public const int SystemOrder =
        MonsterPlayerDamageSystem.SystemOrder + 10;

    private readonly ProcessPetHealingCooldownStore _cooldowns;

    public PetHealingTalentSystem(
        ProcessPetHealingCooldownStore cooldowns)
    {
        _cooldowns = cooldowns ??
            throw new ArgumentNullException(nameof(cooldowns));
    }

    public int Order => SystemOrder;

    public void Update(EcsSystemContext context)
    {
        foreach (var damage in context.Events
                     .Read<MonsterPlayerDamageAppliedEvent>())
        {
            if (damage.Killed ||
                damage.AfterHealth <= 0 ||
                damage.ResolvedAt == default ||
                !context.World.Has<
                    ActivePetHealingTalentComponent>(damage.Player))
            {
                continue;
            }

            var pet = context.World.Get<
                ActivePetHealingTalentComponent>(damage.Player);
            ref var vitals = ref context.World.Get<
                PlayerVitalsComponent>(damage.Player);
            var identity = context.World.Get<
                PlayerIdentityComponent>(damage.Player);

            if (!pet.IsCarried ||
                !pet.IsSummoned ||
                (pet.TalentMask &
                 PetHealingTalentPolicy.HealingTalentMaskBit) == 0 ||
                pet.PetId <= 0 ||
                pet.Level <= 0 ||
                vitals.CurrentHp != damage.AfterHealth ||
                vitals.Revision != damage.AfterVitalsRevision ||
                !PetHealingTalentPolicy.IsAtOrBelowTriggerThreshold(
                    vitals.CurrentHp,
                    vitals.MaximumHp))
            {
                continue;
            }

            var amount = PetHealingTalentPolicy.ResolveAmount(
                pet.Aptitude,
                pet.Level,
                vitals.CurrentHp,
                vitals.MaximumHp);
            if (amount.Applied <= 0 ||
                !_cooldowns.TryClaim(
                    new PetHealingCooldownKey(
                        identity.CharacterId,
                        pet.PetId),
                    damage.ResolvedAt,
                    PetHealingTalentPolicy.Cooldown,
                    out var cooldownReadyAt))
            {
                continue;
            }

            var beforeHealth = vitals.CurrentHp;
            var beforeRevision = vitals.Revision;
            vitals.CurrentHp = checked(
                vitals.CurrentHp + amount.Applied);
            vitals.Revision = checked(vitals.Revision + 1);
            context.Events.Publish(
                new PetHealingAppliedEvent(
                    damage.Player,
                    damage.AttackEventId,
                    identity.CharacterId,
                    identity.ObjectId,
                    pet.PetId,
                    PetHealingTalentPolicy.Version,
                    amount.Resolved,
                    amount.Applied,
                    beforeHealth,
                    vitals.CurrentHp,
                    beforeRevision,
                    vitals.Revision,
                    damage.ResolvedAt,
                    cooldownReadyAt));
        }
    }
}
