using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Grants the stock Instance Caller a finite Medusa Island dialogue shell at
/// both capital endpoints. This release advertises navigation only; dungeon
/// admission remains server-owned and unavailable until implemented.
/// </summary>
internal static class NpcDialogueBaselineV9
{
    public const int ExpectedTextCount = 383;
    public const int ExpectedProfileCount = 11;
    public const int ExpectedRouteCount = 22;
    public const int ExpectedMenuEntryCount = 54;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV8.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "8704C74A8DF399D2700B8FF984E2AF05E451430E1F8463E75C302758121DE67E";
    public const string Source = "reviewed-published-npc-dialogue-v9";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles { get; } =
    [
        .. NpcDialogueBaselineV8.Profiles,
        new(
            "instance_caller",
            InstanceCallerProtocol.DialogIndex,
            NpcDialogueBehavior.InstanceCaller,
            InstanceCallerProtocol.InitialRequestSubId,
            InstanceCallerProtocol.InitialMenuSubIds.ToImmutableArray())
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings { get; } =
    [
        .. NpcDialogueBaselineV8.Bindings,
        new("Athens_060", "Athens_060", "instance_caller"),
        new("Sparta_060", "Sparta_060", "instance_caller")
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
