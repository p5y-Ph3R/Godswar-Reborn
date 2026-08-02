using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class NpcDialogueBaselineV2
{
    public const int ExpectedTextCount = 383;
    public const int ExpectedProfileCount = 5;
    public const int ExpectedRouteCount = 10;
    public const int ExpectedMenuEntryCount = 32;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV1.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "C2B8D9F08439677D846EB8EB7801AB56D3B5DC6A469B6ABBCFB185211B496BB4";
    public const string Source = "reviewed-published-npc-dialogue-v2";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles { get; } =
    [
        .. NpcDialogueBaselineV1.Profiles,
        new(
            "class_suit",
            37,
            NpcDialogueBehavior.ClassSuit,
            -1,
            [100, 101, 102, 103, 104, 105, 106, 107, 108])
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings { get; } =
    [
        new("Athens_070", "Athens_070", "gear_mentor", 0),
        new("Athens_070", "Athens_070", "class_suit", 1),
        new("Athens_085", "Athens_085", "holy_suit_design", 0),
        new("Athens_086", "Athens_086", "holy_stone", 0),
        new("Athens_143", "Athens_143", "origin_enhancer", 0),
        new("Sparta_070", "Sparta_070", "gear_mentor", 0),
        new("Sparta_070", "Sparta_070", "class_suit", 1),
        new("Sparta_085", "Sparta_085", "holy_suit_design", 0),
        new("Sparta_086", "Sparta_086", "holy_stone", 0),
        new("Sparta_143", "Sparta_143", "origin_enhancer", 0)
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
