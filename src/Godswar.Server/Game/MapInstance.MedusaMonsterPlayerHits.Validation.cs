using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private bool IsCurrentBoundTarget(
        ClientSession session,
        GameCharacter expectedCharacter,
        in PlayerMonsterCombatAuthority route,
        in MedusaMonsterPlayerTargetAuthority target) =>
        route.IsValid &&
        route.Ownership.IsValid &&
        target.IsValid &&
        route.WorldInstanceId == target.WorldInstanceId &&
        route.WorldRevision == target.WorldRevision &&
        route.WorldMembershipEpoch == target.WorldMembershipEpoch &&
        route.Ownership == target.Ownership &&
        route.LifeRevision == target.LifeRevision &&
        target.WorldInstanceId == WorldInstanceId &&
        _sessions.TryGetValue(session, out var context) &&
        ReferenceEquals(context.Character, expectedCharacter) &&
        context.Session == session &&
        context.WorldReady &&
        context.WorldInstanceId == target.WorldInstanceId &&
        context.WorldRevision == target.WorldRevision &&
        context.WorldMembershipEpoch == target.WorldMembershipEpoch &&
        context.Ownership == target.Ownership &&
        context.CharacterId == target.CharacterId &&
        context.Character.Id == target.CharacterId &&
        context.ObjectId == target.ObjectId &&
        context.MapId == MapId &&
        context.Character.CurrentMap == MapId &&
        _ecsShadow.ContainsPlayer(session);

    private bool TryValidateBoundSource(
        MedusaInstanceOwnerBoundAggregate owner,
        MedusaMonsterAttachmentSnapshot attachment,
        MonsterRuntimeSnapshot eventSource,
        out MedusaOwnedMonsterBinding binding,
        out MedusaMonsterPlayerHitCaptureOutcome outcome)
    {
        binding = default;
        if (!_monsterRuntime!.TryGetSnapshot(
                eventSource.ObjectId,
                out var current))
        {
            outcome = MedusaMonsterPlayerHitCaptureOutcome.UnknownMonster;
            return false;
        }
        if (current.SpawnGeneration != eventSource.SpawnGeneration)
        {
            outcome = MedusaMonsterPlayerHitCaptureOutcome
                .StaleMonsterGeneration;
            return false;
        }
        if (current.RuntimeInstanceId != eventSource.RuntimeInstanceId ||
            current.RuntimeInstanceId != attachment.RuntimeInstanceId)
        {
            outcome = MedusaMonsterPlayerHitCaptureOutcome
                .StaleMonsterRuntime;
            return false;
        }
        if (current.HealthRevision != eventSource.HealthRevision)
        {
            outcome = MedusaMonsterPlayerHitCaptureOutcome
                .StaleMonsterHealthRevision;
            return false;
        }
        if (!current.IsAlive ||
            !current.IsSpawned ||
            !eventSource.IsAlive ||
            !eventSource.IsSpawned ||
            current.CombatPhase != MonsterCombatPhase.Attacking ||
            eventSource.CombatPhase != MonsterCombatPhase.Attacking)
        {
            outcome = MedusaMonsterPlayerHitCaptureOutcome
                .MonsterNotAttackable;
            return false;
        }
        if (!owner.TryGetBinding(
                current.ObjectId,
                current.SpawnGeneration,
                out binding) ||
            binding.Identity.ObjectId != current.ObjectId ||
            binding.Identity.SpawnGeneration !=
                current.SpawnGeneration ||
            !string.Equals(
                binding.TemplateKey,
                current.Definition.TemplateKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                binding.TemplateKey,
                eventSource.Definition.TemplateKey,
                StringComparison.Ordinal) ||
            !CapturedMonsterDefinitionsEqual(
                current.Definition,
                eventSource.Definition) ||
            binding.Difficulty != attachment.Difficulty ||
            binding.ContentMapId != attachment.ContentMapId)
        {
            outcome = MedusaMonsterPlayerHitCaptureOutcome
                .RosterBindingMismatch;
            return false;
        }

        outcome = MedusaMonsterPlayerHitCaptureOutcome.Captured;
        return true;
    }

    private static bool CapturedMonsterDefinitionsEqual(
        CapturedMonsterSpawn current,
        CapturedMonsterSpawn eventSource) =>
        current.MapId == eventSource.MapId &&
        string.Equals(
            current.SceneKey,
            eventSource.SceneKey,
            StringComparison.Ordinal) &&
        string.Equals(
            current.TemplateKey,
            eventSource.TemplateKey,
            StringComparison.Ordinal) &&
        string.Equals(
            current.DisplayName,
            eventSource.DisplayName,
            StringComparison.Ordinal) &&
        current.ObjectId == eventSource.ObjectId &&
        current.X == eventSource.X &&
        current.Z == eventSource.Z &&
        current.Packet is not null &&
        eventSource.Packet is not null &&
        current.Packet.AsSpan().SequenceEqual(
            eventSource.Packet.AsSpan());

    private bool MatchesCurrentSourceAuthority(
        MedusaInstanceOwnerBoundAggregate owner,
        MedusaMonsterAttachmentSnapshot attachment,
        in MedusaMonsterPlayerSourceAuthority source)
    {
        var descriptor = _descriptor;
        if (descriptor.LifecycleState !=
                WorldInstanceLifecycleState.Active ||
            descriptor.InstanceId != source.Route.WorldInstanceId ||
            descriptor.Revision != source.WorldDescriptorRevision ||
            attachment.WorldInstanceId != source.Route.WorldInstanceId ||
            attachment.RuntimeInstanceId !=
                source.AttachmentRuntimeInstanceId ||
            attachment.StartedAt.ToUniversalTime() !=
                source.AttachmentStartedAt ||
            !string.Equals(
                attachment.Fingerprint,
                source.AttachmentFingerprint,
                StringComparison.Ordinal) ||
            !_monsterRuntime!.TryGetSnapshot(
                source.ObjectId,
                out var current) ||
            current.SpawnGeneration != source.SpawnGeneration ||
            current.HealthRevision != source.HealthRevision ||
            current.RuntimeInstanceId !=
                source.AttachmentRuntimeInstanceId ||
            !current.IsAlive ||
            !current.IsSpawned ||
            current.CombatPhase != MonsterCombatPhase.Attacking ||
            !owner.TryGetBinding(
                source.ObjectId,
                source.SpawnGeneration,
                out var binding))
        {
            return false;
        }

        return string.Equals(
                   binding.RosterSpawnId,
                   source.RosterSpawnId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   binding.TemplateKey,
                   source.TemplateKey,
                   StringComparison.Ordinal) &&
               string.Equals(
                   current.Definition.TemplateKey,
                   source.TemplateKey,
                   StringComparison.Ordinal) &&
               binding.Role == source.Role &&
               binding.Difficulty == source.Difficulty &&
               binding.ContentMapId.Value == MapId;
    }

    private static MedusaMonsterPlayerHitCapture RejectedCapture(
        MedusaMonsterPlayerHitCaptureOutcome outcome,
        in MonsterCombatProfile profile,
        in MedusaMonsterPlayerTargetAuthority target) => new(
        outcome,
        profile,
        SourceAuthority: null,
        target,
        AuthoredEffectKind: null);

    private static MedusaMonsterPlayerHitCommit RejectedCommit(
        MedusaMonsterPlayerHitCommitOutcome outcome) => new(
        outcome,
        VitalsDecision: default,
        MechanicsResult: null);
}
