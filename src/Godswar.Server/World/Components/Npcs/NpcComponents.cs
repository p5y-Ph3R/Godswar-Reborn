using System.Collections.Immutable;

namespace Godswar.Server.World.Components.Npcs;

internal readonly record struct NpcIdentityComponent(
    short MapId,
    string SceneKey,
    string NpcKey,
    string TemplateKey,
    uint ObjectId);

internal readonly record struct NpcTransformComponent(
    float X,
    float Z,
    float Facing);

internal readonly record struct NpcAppearanceComponent(uint AppearanceType);

/// <summary>
/// The current server routes NPC actions by interaction ID. Semantic function
/// flags are not linked to individual spawn actors yet, so this component keeps
/// the known routing value rather than guessing a function.
/// </summary>
internal readonly record struct NpcFunctionComponent(uint InteractionId);

internal readonly record struct NpcDialogComponent(
    ImmutableArray<byte> Detail10077,
    ImmutableArray<byte> Detail10080);
