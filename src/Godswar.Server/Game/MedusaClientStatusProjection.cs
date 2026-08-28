using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal readonly record struct MedusaClientStatusTargetFence(
    WorldInstanceId WorldInstanceId,
    long WorldRevision,
    PlayerOwnershipFence Ownership,
    int CharacterId,
    uint ObjectId,
    long LifeRevision,
    long WorldMembershipEpoch);

internal readonly record struct MedusaClientStatusEffectIdentity(
    MedusaEncounterEffectKind Kind,
    uint StatusId,
    long TargetLifeRevision,
    string SourceRosterSpawnId,
    uint SourceObjectId,
    uint SourceSpawnGeneration,
    ulong ApplicationSequence,
    DateTimeOffset ExpiresAt,
    long TargetWorldMembershipEpoch);

internal sealed record MedusaClientStatusPresentation(
    ClientStatusPresentation Presentation,
    MedusaClientStatusEffectIdentity Identity);

internal sealed record MedusaClientStatusOverlay(
    MedusaCharacterEffectAuthorityOutcome AuthorityOutcome,
    MedusaClientStatusTargetFence? Target,
    DateTimeOffset? RunDeadline,
    IReadOnlyList<MedusaClientStatusPresentation> Presentations,
    string Fingerprint)
{
    internal const string UnboundFingerprint = "medusa-client:unbound";

    public static MedusaClientStatusOverlay Unbound { get; } = new(
        MedusaCharacterEffectAuthorityOutcome.Unbound,
        Target: null,
        RunDeadline: null,
        Presentations: [],
        UnboundFingerprint);

    public bool IsBound => AuthorityOutcome !=
        MedusaCharacterEffectAuthorityOutcome.Unbound;

    public bool CanPublish => AuthorityOutcome is
        MedusaCharacterEffectAuthorityOutcome.ResolvedActive or
        MedusaCharacterEffectAuthorityOutcome.RunNotActive;
}

internal static class MedusaClientStatusProjection
{
    private const string FingerprintMarker = "#medusa-overlay:";
    private const HostileStatusControlFlags NativeFullControl =
        HostileStatusControlFlags.HaltIntonate |
        HostileStatusControlFlags.NonMoving |
        HostileStatusControlFlags.NonMagicUsing |
        HostileStatusControlFlags.NonTechniqueUsing |
        HostileStatusControlFlags.NonAttackUsing |
        HostileStatusControlFlags.NonItemUsing;

    public static MedusaClientStatusOverlay Create(
        in MedusaClientStatusTargetFence target,
        MedusaCharacterEffectAuthorityResult authority,
        DateTimeOffset now)
    {
        if (authority.Outcome ==
                MedusaCharacterEffectAuthorityOutcome.Unbound)
        {
            return MedusaClientStatusOverlay.Unbound;
        }
        if (authority.Outcome !=
                MedusaCharacterEffectAuthorityOutcome.ResolvedActive ||
            authority.View is not { } view)
        {
            return new(
                authority.Outcome,
                target,
                RunDeadline: null,
                Presentations: [],
                FingerprintFor(target, authority.Outcome, []));
        }

        var presentations = view.ActiveEffects
            .Select(effect => TryCreatePresentation(
                effect,
                view.EvaluatedAt > now.ToUniversalTime()
                    ? view.EvaluatedAt
                    : now.ToUniversalTime()))
            .Where(static presentation => presentation is not null)
            .Select(static presentation => presentation!)
            .OrderBy(static presentation =>
                presentation.Identity.Kind)
            .ToArray();
        return new(
            authority.Outcome,
            target,
            view.RunDeadline,
            presentations,
            FingerprintFor(
                target,
                authority.Outcome,
                presentations.Select(static presentation =>
                    presentation.Identity)));
    }

    public static PlayerStatusSnapshot Merge(
        PlayerStatusSnapshot baseline,
        MedusaClientStatusOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(overlay);
        if (!overlay.CanPublish || !overlay.IsBound)
        {
            return baseline;
        }

        var presentations = baseline.Presentations
            .Where(static presentation => presentation.Source !=
                ClientStatusPresentationSource.Medusa)
            .Concat(overlay.Presentations.Select(static item =>
                item.Presentation))
            .ToArray();
        var marker = baseline.Fingerprint.IndexOf(
            FingerprintMarker,
            StringComparison.Ordinal);
        var baseFingerprint = marker < 0
            ? baseline.Fingerprint
            : baseline.Fingerprint[..marker];
        var hasFullControl = overlay.Presentations.Any(
            static presentation => presentation.Identity.Kind is
                MedusaEncounterEffectKind.Stun or
                MedusaEncounterEffectKind.Freeze or
                MedusaEncounterEffectKind.Shackle);
        return PlayerStatusCapacityPolicy.Apply(baseline with
        {
            Presentations = presentations,
            Aggregate = baseline.Aggregate with
            {
                Control = hasFullControl
                    ? baseline.Aggregate.Control | NativeFullControl
                    : baseline.Aggregate.Control
            },
            Fingerprint = $"{baseFingerprint}{FingerprintMarker}" +
                overlay.Fingerprint
        });
    }

    private static MedusaClientStatusPresentation? TryCreatePresentation(
        in MedusaActiveEncounterEffectSnapshot effect,
        DateTimeOffset now)
    {
        if (effect.ExpiresAt <= now ||
            !TryCreateEffectIdentity(effect, out var identity))
        {
            return null;
        }

        var remaining = (long)Math.Ceiling(
            (effect.ExpiresAt - now).TotalSeconds);
        var clientEffect = new ClientStatusEffect(
            identity.StatusId,
            checked((uint)Math.Clamp(
                remaining,
                1L,
                uint.MaxValue)));
        var amplifier = effect.Definition.Kind is
            MedusaEncounterEffectKind.OutgoingPhysicalAmplifier or
            MedusaEncounterEffectKind.OutgoingMagicalAmplifier;
        var presentation = new ClientStatusPresentation(
            clientEffect,
            Beneficial: amplifier,
            Priority: 1_000 - (int)effect.Definition.Kind,
            amplifier
                ? ClientStatusPresentationClass.MedusaAmplifier
                : ClientStatusPresentationClass.AuthoritativeControl,
            ClientStatusPresentationSource.Medusa);
        return new(presentation, identity);
    }

    internal static bool TryCreateEffectIdentity(
        in MedusaActiveEncounterEffectSnapshot effect,
        out MedusaClientStatusEffectIdentity identity)
    {
        if (effect.Definition.Kind == MedusaEncounterEffectKind.Bleed ||
            !effect.Definition.ClientProjection
                .MayEmitNativeReferenceStatus ||
            effect.Definition.ClientProjection.EmittableStatusId is not
                { } statusId ||
            !MedusaIslandRosterPolicy.TryGetSpawn(
                effect.SourceRosterSpawnId,
                out var spawn) ||
            spawn.Skill is not { } binding ||
            binding.Mechanic != effect.Definition.Mechanic ||
            binding.StatusId != statusId)
        {
            identity = default;
            return false;
        }

        identity = new(
            effect.Definition.Kind,
            statusId,
            effect.TargetLifeRevision,
            effect.SourceRosterSpawnId,
            effect.SourceObjectId,
            effect.SourceSpawnGeneration,
            effect.ApplicationSequence,
            effect.ExpiresAt,
            effect.TargetWorldMembershipEpoch);
        return true;
    }

    private static string FingerprintFor(
        in MedusaClientStatusTargetFence target,
        MedusaCharacterEffectAuthorityOutcome outcome,
        IEnumerable<MedusaClientStatusEffectIdentity> effects) =>
        $"medusa-client:{(byte)outcome}:" +
        $"{target.WorldInstanceId}:{target.WorldRevision}:" +
        $"{target.Ownership.OwnerId:N}:" +
        $"{target.Ownership.Generation}:" +
        $"{target.CharacterId}:{target.ObjectId}:{target.LifeRevision}:" +
        $"{target.WorldMembershipEpoch}:" +
        string.Join(
            '|',
            effects.Select(static effect =>
                $"{(byte)effect.Kind}:{effect.StatusId}:" +
                $"{effect.SourceRosterSpawnId}:" +
                $"{effect.SourceObjectId}:" +
                $"{effect.SourceSpawnGeneration}:" +
                $"{effect.ApplicationSequence}:" +
                $"{effect.TargetWorldMembershipEpoch}:" +
                $"{effect.ExpiresAt.UtcTicks}"));
}
