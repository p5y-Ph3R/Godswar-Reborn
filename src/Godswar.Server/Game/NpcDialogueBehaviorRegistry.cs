using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

/// <summary>
/// Restricts database-authored dialogue routes to the finite NPC capabilities
/// implemented by this server. Content selects behavior; it never names or
/// executes methods, scripts, or arbitrary types.
/// </summary>
internal static class NpcDialogueBehaviorRegistry
{
    private const uint SpartaHolyStoneNpcId = 5083;
    private const uint AthensHolyStoneNpcId = 5225;
    private const int ExpectedHolyStoneDialogIndex = 30;
    private static readonly int[] GearMentorMenu =
        [1, 2, 3, 4, 5, 6, 7, 8, 9];
    private static readonly int[] OriginEnhancerMenu = [2, 3, 6];
    private static readonly int[] HolySuitDesignMenu =
        [101, 201, 301, 401];
    private static readonly int[] LegacyHolyStoneMenu =
        [101, 201, 301, 401, 501, 601, 701];
    private static readonly int[] HolyStoneMenu =
        [101, 201, 301, 401, 501, 601, 701, 801];
    private static readonly int[] ClassSuitMenu =
        [100, 101, 102, 103, 104, 105, 106, 107, 108];
    private static readonly int[] PetManagerMenu =
        PetManagerProtocol.InitialMenuSubIds.ToArray();
    private static readonly int[] PetPointResetMenu =
        PetManagerProtocol.PointResetInitialMenuSubIds.ToArray();

    public static bool IsAllowed(
        NpcSpawnDefinition npc,
        NpcDialogueRouteDefinition route)
    {
        if (!string.Equals(
                npc.NpcKey,
                route.NpcKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                npc.NpcKey,
                route.ClientScriptKey,
                StringComparison.Ordinal))
        {
            return false;
        }

        return route.Behavior switch
        {
            NpcDialogueBehavior.GearMentor =>
                route.DialogIndex == GearEnhancerProtocol.DialogIndex &&
                HasExactMenu(route, GearMentorMenu) &&
                (npc.NpcKey, npc.InteractionId) is
                    ("Sparta_070", GearEnhancerProtocol.SpartaEnhancerNpcId) or
                    ("Athens_070", GearEnhancerProtocol.AthensEnhancerNpcId),
            NpcDialogueBehavior.OriginEnhancer =>
                route.DialogIndex ==
                    GearEnhancerProtocol.OriginDialogIndex &&
                HasExactMenu(route, OriginEnhancerMenu) &&
                GearEnhancerProtocol.IsOriginEnhancerEndpoint(
                    npc.NpcKey,
                    npc.InteractionId),
            NpcDialogueBehavior.HolySuitDesign =>
                route.DialogIndex == HolySuitDesignProtocol.DialogIndex &&
                HasExactMenu(route, HolySuitDesignMenu) &&
                (npc.NpcKey, npc.InteractionId) is
                    ("Sparta_085", HolySuitDesignProtocol.SpartaNpcId) or
                    ("Athens_085", HolySuitDesignProtocol.AthensNpcId),
            NpcDialogueBehavior.HolyStone =>
                route.DialogIndex == ExpectedHolyStoneDialogIndex &&
                (HasExactMenu(route, HolyStoneMenu) ||
                 HasExactMenu(route, LegacyHolyStoneMenu)) &&
                (npc.NpcKey, npc.InteractionId) is
                    ("Sparta_086", SpartaHolyStoneNpcId) or
                    ("Athens_086", AthensHolyStoneNpcId),
            NpcDialogueBehavior.ClassSuit =>
                route.DialogIndex == ClassSuitProtocol.DialogIndex &&
                HasExactMenu(route, ClassSuitMenu) &&
                ClassSuitProtocol.IsNpcKey(npc.NpcKey) &&
                ClassSuitProtocol.IsEndpoint(
                    npc.InteractionId,
                    route.DialogIndex),
            NpcDialogueBehavior.PetManager =>
                route.DialogIndex == PetManagerProtocol.DialogIndex &&
                HasExactMenu(route, PetManagerMenu) &&
                PetManagerProtocol.IsEndpoint(
                    npc.NpcKey,
                    npc.InteractionId),
            NpcDialogueBehavior.PetPointReset =>
                route.DialogIndex ==
                    PetManagerProtocol.PointResetDialogIndex &&
                HasExactMenu(route, PetPointResetMenu) &&
                PetManagerProtocol.IsEndpoint(
                    npc.NpcKey,
                    npc.InteractionId),
            _ => false
        };
    }

    private static bool HasExactMenu(
        NpcDialogueRouteDefinition route,
        IReadOnlyList<int> expected) =>
        route.InitialMenuSubIds.SequenceEqual(expected);
}
