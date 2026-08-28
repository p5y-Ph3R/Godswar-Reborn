using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed record TrainingDummyHostileClientPresentation(
    ClientStatusEffect Effect,
    HostileStatusEffectDefinition Definition);

internal sealed record TrainingDummyHostileStatusClientOverlay(
    IReadOnlyList<TrainingDummyHostileClientPresentation> Presentations,
    string Fingerprint)
{
    public static TrainingDummyHostileStatusClientOverlay Empty { get; } =
        new(
            [],
            TrainingDummyHostileStatusClientProjection.EmptyFingerprint);

    public IReadOnlyList<ClientStatusEffect> Effects { get; } =
        Presentations.Select(static presentation => presentation.Effect)
            .ToArray();
}

/// <summary>
/// Projects authoritative exact-dummy hostile statuses into the stock
/// complete 0x27B7 status snapshot. Landing chance, duration, controls, and
/// incoming-damage authority remain server-side.
/// </summary>
internal static class TrainingDummyHostileStatusClientProjection
{
    internal const string EmptyFingerprint =
        "training-dummy-hostile-client:none";

    public static TrainingDummyHostileStatusClientOverlay Create(
        TrainingDummyHostileStatusSnapshot snapshot,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var active = snapshot.ActiveStatuses
            .Where(status => status.ExpiresAt > now)
            .OrderBy(static status => status.Definition.StatusId)
            .ToArray();
        if (active.Length == 0)
        {
            return TrainingDummyHostileStatusClientOverlay.Empty;
        }

        var presentations = active.Select(status =>
                new TrainingDummyHostileClientPresentation(
                    new ClientStatusEffect(
                        status.Definition.StatusId,
                        Math.Max(1u, status.RemainingSeconds(now))),
                    status.Definition))
            .ToArray();
        var fingerprint = string.Join(
            '|',
            active.Select(static status =>
                $"{status.Definition.StatusId}:" +
                $"{status.Definition.Kind}:" +
                $"{status.Definition.Priority}:" +
                $"{status.ExpiresAt.UtcTicks}:" +
                $"{status.Revision}"));
        return new(
            presentations,
            $"training-dummy-hostile-client:{snapshot.Revision}:" +
            fingerprint);
    }

    public static PlayerStatusSnapshot Merge(
        PlayerStatusSnapshot baseline,
        TrainingDummyHostileStatusClientOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(overlay);
        if (overlay.Effects.Count == 0)
        {
            return baseline;
        }

        // Exact-dummy hostile icons are mandatory presentation. They describe
        // server-enforced controls and incoming-damage modifiers, so reserve
        // their slots before fallback baseline presentation. This never
        // changes baseline authority or aggregate mechanics. At capacity the
        // display-only elemental Burn is displaced first, then baseline IDs
        // are retained in stable ascending order.
        var authoritativeHostile = overlay.Presentations
            .GroupBy(static presentation => presentation.Effect.StatusId)
            .Select(static group => group
                .OrderByDescending(static presentation =>
                    presentation.Definition.Priority)
                .ThenBy(static presentation =>
                    presentation.Definition.SkillId)
                .First())
            .OrderByDescending(static presentation =>
                presentation.Definition.Priority)
            .ThenBy(static presentation => presentation.Effect.StatusId)
            .ToArray();
        var presentations = baseline.Presentations
            .Concat(authoritativeHostile.Select(static presentation =>
                new ClientStatusPresentation(
                    presentation.Effect,
                    Beneficial: false,
                    presentation.Definition.Priority,
                    presentation.Definition.Control ==
                        HostileStatusControlFlags.None
                            ? ClientStatusPresentationClass
                                .AuthoritativeBaseline
                            : ClientStatusPresentationClass
                                .AuthoritativeControl)))
            .ToArray();
        var physicalDefense = authoritativeHostile.Aggregate(
            0L,
            static (sum, presentation) => sum +
                presentation.Definition.PhysicalDefenseModifier);
        var magicDefense = authoritativeHostile.Aggregate(
            0L,
            static (sum, presentation) => sum +
                presentation.Definition.MagicDefenseModifier);
        var control = authoritativeHostile.Aggregate(
            HostileStatusControlFlags.None,
            static (current, presentation) =>
                current | presentation.Definition.Control);
        var aggregate = baseline.Aggregate with
        {
            PhysicalDefense = SaturatingAdd(
                baseline.Aggregate.PhysicalDefense,
                physicalDefense),
            MagicDefense = SaturatingAdd(
                baseline.Aggregate.MagicDefense,
                magicDefense),
            Control = baseline.Aggregate.Control | control
        };
        return PlayerStatusCapacityPolicy.Apply(baseline with
        {
            Presentations = presentations,
            Aggregate = aggregate,
            Fingerprint = $"{baseline.Fingerprint}#{overlay.Fingerprint}:" +
                string.Join(',', authoritativeHostile.Select(
                    static presentation =>
                        presentation.Effect.StatusId))
        });
    }

    private static int SaturatingAdd(int left, long right) =>
        (int)Math.Clamp(
            left + right,
            int.MinValue,
            int.MaxValue);
}
