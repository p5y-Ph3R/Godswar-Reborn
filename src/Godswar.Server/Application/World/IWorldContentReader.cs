using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.World;

/// <summary>
/// Reads one process-pinned, immutable projection of published world content.
/// Implementations must not switch revisions during the process lifetime.
/// </summary>
internal interface IWorldContentReader
{
    WorldContentManifest Manifest { get; }

    GameplayContentCatalog Gameplay { get; }

    ValueTask<WorldMapContent> ReadMapAsync(
        short mapId,
        CancellationToken cancellationToken = default);

    ValueTask<EnterWorldBootstrapContent> ReadEnterBootstrapAsync(
        CancellationToken cancellationToken = default);

    ValueTask<NpcDialogueContent> ReadNpcDialogueAsync(
        string npcKey,
        CancellationToken cancellationToken = default);
}

internal sealed record WorldContentFamilyRevision(
    string Family,
    string Sha256,
    int EntryCount);

internal sealed record WorldContentManifest(
    string Source,
    string Revision,
    DateTimeOffset LoadedAtUtc,
    WorldContentFamilyRevision Maps,
    WorldContentFamilyRevision Npcs,
    WorldContentFamilyRevision NpcDialogues,
    WorldContentFamilyRevision Monsters,
    WorldContentFamilyRevision EnterBootstrap,
    WorldContentFamilyRevision Gameplay);

internal sealed record WorldMapContent(
    short MapId,
    WorldContentFamilyRevision MapRevision,
    WorldContentFamilyRevision NpcRevision,
    WorldContentFamilyRevision MonsterRevision,
    IReadOnlyList<NpcSpawnDefinition> Npcs,
    IReadOnlyList<CapturedMonsterSpawn> Monsters);

internal sealed record EnterWorldBootstrapContent(
    WorldContentFamilyRevision Revision,
    IReadOnlyList<byte[]> Packets);

internal sealed record NpcDialogueContent(
    WorldContentFamilyRevision Revision,
    NpcTextDefinition Text,
    IReadOnlyList<NpcDialogueRouteDefinition> Routes)
{
    // Compatibility accessor for callers that only understand the primary
    // NPC function. New routing code must enumerate Routes in RouteOrder.
    public NpcDialogueRouteDefinition? Route => Routes.Count == 0
        ? null
        : Routes[0];
}

internal enum WorldContentFailureReason
{
    Missing,
    Invalid,
    RevisionMismatch
}

internal sealed class WorldContentUnavailableException : Exception
{
    public WorldContentUnavailableException(
        string family,
        WorldContentFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Family = family;
        Reason = reason;
    }

    public string Family { get; }

    public WorldContentFailureReason Reason { get; }
}
