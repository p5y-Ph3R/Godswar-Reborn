using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

internal sealed record NpcDialogueProfileBaseline(
    string ProfileKey,
    int DialogIndex,
    NpcDialogueBehavior Behavior,
    int InitialRequestSubId,
    ImmutableArray<int> InitialMenuSubIds);

internal sealed record NpcDialogueBindingBaseline(
    string NpcKey,
    string ClientScriptKey,
    string ProfileKey);

internal static class NpcDialogueBaselineV1
{
    public const int ExpectedTextCount = 383;
    public const int ExpectedProfileCount = 4;
    public const int ExpectedRouteCount = 8;
    public const int ExpectedMenuEntryCount = 23;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        "06BCC3DD4665BB5F3F3AE0843B1AA2A1B6C211DDA07DB0381B5EA663068040C7";
    public const string ExpectedRevision =
        "CC1CE5D182C68C728AD824D04F87F29DC66B0446D959C0EA08B7DD2712C6908D";
    public const string Source = "reviewed-published-npc-dialogue-v1";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles { get; } =
    [
        new(
            "gear_mentor",
            4,
            NpcDialogueBehavior.GearMentor,
            -1,
            [1, 2, 3, 4, 5, 6, 7, 8, 9]),
        new(
            "holy_suit_design",
            29,
            NpcDialogueBehavior.HolySuitDesign,
            -1,
            [101, 201, 301, 401]),
        new(
            "holy_stone",
            30,
            NpcDialogueBehavior.HolyStone,
            -1,
            [101, 201, 301, 401, 501, 601, 701]),
        new(
            "origin_enhancer",
            118,
            NpcDialogueBehavior.OriginEnhancer,
            -1,
            [2, 3, 6])
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings { get; } =
    [
        new("Athens_070", "Athens_070", "gear_mentor"),
        new("Athens_085", "Athens_085", "holy_suit_design"),
        new("Athens_086", "Athens_086", "holy_stone"),
        new("Athens_143", "Athens_143", "origin_enhancer"),
        new("Sparta_070", "Sparta_070", "gear_mentor"),
        new("Sparta_085", "Sparta_085", "holy_suit_design"),
        new("Sparta_086", "Sparta_086", "holy_stone"),
        new("Sparta_143", "Sparta_143", "origin_enhancer")
    ];

    public static NpcDialogueRouteDefinition[] CreateRoutes()
    {
        var profiles = Profiles.ToDictionary(
            static profile => profile.ProfileKey,
            StringComparer.Ordinal);
        return Bindings
            .OrderBy(static binding => binding.NpcKey, StringComparer.Ordinal)
            .Select(binding =>
            {
                if (!profiles.TryGetValue(
                        binding.ProfileKey,
                        out var profile))
                {
                    throw new InvalidDataException(
                        $"Unknown NPC dialogue profile '{binding.ProfileKey}'.");
                }

                return new NpcDialogueRouteDefinition(
                    binding.NpcKey,
                    binding.ClientScriptKey,
                    profile.DialogIndex,
                    profile.Behavior,
                    profile.InitialMenuSubIds);
            })
            .ToArray();
    }
}
