using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Npcs;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Maps;

/// <summary>
/// Map membership metadata that is not part of the persisted character
/// projection. Transport/session ownership deliberately remains outside ECS.
/// </summary>
internal readonly record struct MapPlayerPresenceComponent(bool WorldReady);

internal sealed record MapEcsPlayerSnapshot(
    EntityId Entity,
    bool WorldReady,
    PlayerEcsSnapshot Player);

internal sealed record MapEcsNpcSnapshot(
    EntityId Entity,
    NpcEcsSnapshot Npc);

internal sealed record MapEcsShadowSnapshot(
    byte MapId,
    long Revision,
    IReadOnlyList<MapEcsPlayerSnapshot> Players,
    IReadOnlyList<MapEcsNpcSnapshot> Npcs,
    IReadOnlyList<string> ActiveFaults,
    long FaultCount);

internal sealed record MapEcsParityDiagnostics(
    byte MapId,
    long ShadowRevision,
    int LegacyPlayerCount,
    int EcsPlayerCount,
    int AuthoritativeNpcCount,
    int EcsNpcCount,
    IReadOnlyList<uint> MissingPlayerObjectIds,
    IReadOnlyList<uint> UnexpectedPlayerObjectIds,
    IReadOnlyList<uint> MismatchedPlayerObjectIds,
    IReadOnlyList<uint> MismatchedNpcObjectIds,
    IReadOnlyList<string> ActiveFaults,
    long FaultCount)
{
    public bool IsMatch =>
        LegacyPlayerCount == EcsPlayerCount &&
        AuthoritativeNpcCount == EcsNpcCount &&
        MissingPlayerObjectIds.Count == 0 &&
        UnexpectedPlayerObjectIds.Count == 0 &&
        MismatchedPlayerObjectIds.Count == 0 &&
        MismatchedNpcObjectIds.Count == 0 &&
        ActiveFaults.Count == 0;
}
