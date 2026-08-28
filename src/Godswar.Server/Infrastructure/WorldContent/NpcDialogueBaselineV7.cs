using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Publishes the stock Online Award Admin at both city endpoints. The
/// top-level request is a durable once-per-realm-day claim; this release only
/// grants its finite dialog-49 capability.
/// </summary>
internal static class NpcDialogueBaselineV7
{
    public const int ExpectedTextCount = 383;
    public const int ExpectedProfileCount = 9;
    public const int ExpectedRouteCount = 18;
    public const int ExpectedMenuEntryCount = 52;
    public const int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;
    public const string ExpectedSpawnRevision =
        NpcDialogueBaselineV6.ExpectedSpawnRevision;
    public const string ExpectedRevision =
        "9D7AC95A538CA5599A36CA46D11036D01B701B4A32BB75C05CEC368DAF757083";
    public const string Source = "reviewed-published-npc-dialogue-v7";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles { get; } =
    [
        .. NpcDialogueBaselineV6.Profiles,
        new(
            "online_award",
            OnlineAwardProtocol.DialogIndex,
            NpcDialogueBehavior.OnlineAward,
            OnlineAwardProtocol.InitialRequestSubId,
            OnlineAwardProtocol.InitialMenuSubIds.ToImmutableArray())
    ];

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings { get; } =
    [
        .. NpcDialogueBaselineV6.Bindings,
        new("Athens_132", "Athens_132", "online_award"),
        new("Sparta_132", "Sparta_132", "online_award")
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
