namespace Godswar.Server.State;

internal readonly record struct NpcSpawnReferenceDefinition(
    short MapId,
    string SceneKey,
    string NpcKey,
    string TemplateKey,
    float X,
    float Z);
