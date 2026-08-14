using Godswar.Server.Ecs;

namespace Godswar.Server.World.Systems.Combat;

internal readonly record struct PetHealingAppliedEvent(
    EntityId Player,
    ulong AttackEventId,
    int CharacterId,
    uint PlayerObjectId,
    long PetId,
    int PolicyVersion,
    int ResolvedHealing,
    int AppliedHealing,
    int BeforeHealth,
    int AfterHealth,
    long BeforeVitalsRevision,
    long AfterVitalsRevision,
    DateTimeOffset AppliedAt,
    DateTimeOffset CooldownReadyAt);
