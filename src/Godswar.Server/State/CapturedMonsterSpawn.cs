namespace Godswar.Server.State;

internal sealed record CapturedMonsterSpawn(
    short MapId,
    string SceneKey,
    string TemplateKey,
    string DisplayName,
    uint ObjectId,
    float X,
    float Z,
    byte[] Packet);
