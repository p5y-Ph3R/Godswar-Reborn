using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

internal enum MedusaMonsterAttachmentOutcome : byte
{
    Attached = 1,
    AlreadyAttached = 2,
    OwnershipRejected = 3,
    RuntimeNotEmpty = 4,
    MonsterRuntimeModeUnsupported = 5,
    BootstrapRejected = 6,
    ExistingStateConflict = 7,
    FingerprintConflict = 8,
    RuntimeCreationFailed = 9,
    RuntimeVerificationFailed = 10
}

internal sealed record MedusaMonsterAttachmentSnapshot(
    WorldInstanceId WorldInstanceId,
    MedusaEncounterDifficulty Difficulty,
    MapId ContentMapId,
    DateTimeOffset StartedAt,
    MonsterRuntimeMode RuntimeMode,
    MonsterRespawnPolicy RespawnPolicy,
    int MonsterCount,
    Guid RuntimeInstanceId,
    string Fingerprint);

internal readonly record struct MedusaMonsterAttachmentResult(
    MedusaMonsterAttachmentOutcome Outcome,
    MedusaInstanceBindOutcome? OwnershipOutcome,
    MedusaMonsterBootstrapValidationOutcome? BootstrapOutcome,
    MedusaMonsterAttachmentSnapshot? Snapshot)
{
    public bool IsAttached => Outcome is
        MedusaMonsterAttachmentOutcome.Attached or
        MedusaMonsterAttachmentOutcome.AlreadyAttached;
}
