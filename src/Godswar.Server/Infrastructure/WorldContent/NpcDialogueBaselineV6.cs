using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Publishes the stock Faction Crier at both city endpoints. Stateful
/// actions remain server-authoritative durable commands; this release only
/// grants the finite dialog-15 capability and its captured root menu.
/// </summary>
internal static class NpcDialogueBaselineV6
{
    public const int ExpectedTextCount = 383;
    public const int ExpectedProfileCount = 8;
    public const int ExpectedRouteCount = 16;
    public const int ExpectedMenuEntryCount = 51;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV5.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "7AD4FBC9FE0801C95C8A4F122A9C6FD443F477C09422AF18D62625FFC657D06C";
    public const string Source = "reviewed-published-npc-dialogue-v6";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles { get; } =
    [
        .. NpcDialogueBaselineV5.Profiles,
        new(
            "faction_crier",
            FactionCrierProtocol.DialogIndex,
            NpcDialogueBehavior.FactionCrier,
            -1,
            FactionCrierProtocol.InitialMenuSubIds.ToImmutableArray())
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings { get; } =
    [
        .. NpcDialogueBaselineV5.Bindings,
        new("Athens_055", "Athens_055", "faction_crier"),
        new("Sparta_055", "Sparta_055", "faction_crier")
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
