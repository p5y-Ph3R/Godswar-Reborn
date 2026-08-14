using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class NpcDialogueBaselineV4
{
    public const int ExpectedTextCount = 383;
    public const int ExpectedProfileCount = 6;
    public const int ExpectedRouteCount = 12;
    public const int ExpectedMenuEntryCount = 44;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV3.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "0CEF29B870A4E419A3477427192A1D45EE28B8E8E0AC8C4B7541B44F061A6EFD";
    public const string Source = "reviewed-published-npc-dialogue-v4";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles { get; } =
    [
        .. NpcDialogueBaselineV3.Profiles,
        new(
            "pet_manager",
            PetManagerProtocol.DialogIndex,
            NpcDialogueBehavior.PetManager,
            -1,
            PetManagerProtocol.InitialMenuSubIds.ToImmutableArray())
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings { get; } =
    [
        .. NpcDialogueBaselineV3.Bindings,
        new("Athens_088", "Athens_088", "pet_manager"),
        new("Sparta_088", "Sparta_088", "pet_manager")
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
