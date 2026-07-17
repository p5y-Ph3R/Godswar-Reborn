namespace Godswar.Server.State;

internal sealed record CapturedNpcSpawn(
    short MapId,
    string SceneKey,
    string NpcKey,
    string TemplateKey,
    uint ObjectId,
    float X,
    float Z,
    byte[] Packet,
    byte[] Detail10077,
    byte[] Detail10080);
