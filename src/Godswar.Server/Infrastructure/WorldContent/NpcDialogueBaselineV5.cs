using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Adds the stock Pet Manager's separate point-reset top-level function.
/// Dialog 36 navigation is published, while its state-changing confirmations
/// remain capture-gated.
/// </summary>
internal static class NpcDialogueBaselineV5
{
    public const int ExpectedTextCount = 383;
    public const int ExpectedProfileCount = 7;
    public const int ExpectedRouteCount = 14;
    public const int ExpectedMenuEntryCount = 46;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV4.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "C964BDC00DBC7DAE4E00FC7DF4E3B02B8636091393552E487481D1612E1AD46D";
    public const string Source = "reviewed-published-npc-dialogue-v5";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles { get; } =
    [
        .. NpcDialogueBaselineV4.Profiles,
        new(
            "pet_point_reset",
            PetManagerProtocol.PointResetDialogIndex,
            NpcDialogueBehavior.PetPointReset,
            -1,
            PetManagerProtocol.PointResetInitialMenuSubIds.ToImmutableArray())
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings { get; } =
    [
        .. NpcDialogueBaselineV4.Bindings,
        new(
            "Athens_088",
            "Athens_088",
            "pet_point_reset",
            RouteOrder: 1),
        new(
            "Sparta_088",
            "Sparta_088",
            "pet_point_reset",
            RouteOrder: 1)
    ];

    public static NpcDialogueRouteDefinition[] CreateRoutes()
    {
        var profiles = Profiles.ToDictionary(
            static profile => profile.ProfileKey,
            StringComparer.Ordinal);
        return Bindings
            .OrderBy(static binding => binding.NpcKey, StringComparer.Ordinal)
            .ThenBy(static binding => binding.RouteOrder)
            .Select(binding =>
            {
                if (!profiles.TryGetValue(binding.ProfileKey, out var profile))
                {
                    throw new InvalidDataException(
                        $"Unknown NPC dialogue profile '{binding.ProfileKey}'.");
                }

                return new NpcDialogueRouteDefinition(
                    binding.NpcKey,
                    binding.ClientScriptKey,
                    profile.DialogIndex,
                    profile.Behavior,
                    profile.InitialMenuSubIds)
                {
                    RouteOrder = binding.RouteOrder
                };
            })
            .ToArray();
    }
}
