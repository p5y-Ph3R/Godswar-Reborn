namespace Godswar.Server.Domain.World.Content;

internal sealed record NpcSpawnDefinition(
    short MapId,
    string SceneKey,
    string NpcKey,
    string TemplateKey,
    uint ObjectId,
    float X,
    float Z,
    uint InteractionId,
    uint AppearanceType,
    float Facing,
    byte[] Detail10077,
    byte[] Detail10080);
