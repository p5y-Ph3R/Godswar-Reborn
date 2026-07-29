using System.Collections.Immutable;

namespace Godswar.Server.Domain.World.Content;

internal sealed record NpcTextDefinition(
    string NpcKey,
    string SceneKey,
    string DisplayName,
    string Description);

internal sealed record NpcDialogueRouteDefinition(
    string NpcKey,
    string ClientScriptKey,
    int DialogIndex,
    NpcDialogueBehavior Behavior,
    ImmutableArray<int> InitialMenuSubIds);

internal enum NpcDialogueBehavior
{
    GearMentor = 1,
    OriginEnhancer = 2,
    HolySuitDesign = 3,
    HolyStone = 4
}
