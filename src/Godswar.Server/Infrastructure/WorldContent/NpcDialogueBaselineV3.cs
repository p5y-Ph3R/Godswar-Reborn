using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class NpcDialogueBaselineV3
{
    public const int ExpectedTextCount = 383;
    public const int ExpectedProfileCount = 5;
    public const int ExpectedRouteCount = 10;
    public const int ExpectedMenuEntryCount = 33;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV2.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "4DEC472E8ECDC9398C542C87A0A7E5AC8B474B5B8D51BD3115FA0A6F7DD00C81";
    public const string Source = "reviewed-published-npc-dialogue-v3";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles
    {
        get;
    } = NpcDialogueBaselineV2.Profiles
        .Select(static profile =>
            string.Equals(
                profile.ProfileKey,
                "holy_stone",
                StringComparison.Ordinal)
                ? profile with
                {
                    InitialMenuSubIds =
                    [101, 201, 301, 401, 501, 601, 701, 801]
                }
                : profile)
        .ToImmutableArray();

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV2.Bindings;

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
