using System.Collections.ObjectModel;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed record MapNpcCatalogSnapshot(
    byte MapId,
    long Revision,
    IReadOnlyList<NpcSpawnDefinition> Definitions);

internal readonly record struct MapNpcCatalogPublication(
    MapNpcCatalogSnapshot Snapshot,
    bool Changed);

internal static class NpcCatalogDefinitions
{
    public static NpcSpawnDefinition[] CloneAndValidate(
        byte mapId,
        IReadOnlyList<NpcSpawnDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var objectIds = new HashSet<uint>();
        var interactionIds = new HashSet<uint>();
        var clones = new NpcSpawnDefinition[definitions.Count];
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            ArgumentNullException.ThrowIfNull(definition);
            if (definition.MapId != mapId)
            {
                throw new ArgumentException(
                    $"NPC {definition.ObjectId} belongs to map " +
                    $"{definition.MapId}, not map {mapId}.",
                    nameof(definitions));
            }

            if (!objectIds.Add(definition.ObjectId))
            {
                throw new ArgumentException(
                    $"NPC object {definition.ObjectId} occurs more than once.",
                    nameof(definitions));
            }

            if (!interactionIds.Add(definition.InteractionId))
            {
                throw new ArgumentException(
                    $"NPC interaction {definition.InteractionId} occurs more than once.",
                    nameof(definitions));
            }

            clones[index] = Clone(definition);
        }

        Array.Sort(
            clones,
            static (left, right) => left.ObjectId.CompareTo(right.ObjectId));
        return clones;
    }

    public static IReadOnlyList<NpcSpawnDefinition> ReadOnlyClone(
        IEnumerable<NpcSpawnDefinition> definitions)
    {
        var clones = definitions
            .OrderBy(static definition => definition.ObjectId)
            .Select(Clone)
            .ToArray();
        return new ReadOnlyCollection<NpcSpawnDefinition>(clones);
    }

    public static bool SetEquals(
        IReadOnlyList<NpcSpawnDefinition> left,
        IReadOnlyList<NpcSpawnDefinition> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!Equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool Equals(
        NpcSpawnDefinition left,
        NpcSpawnDefinition right)
    {
        return left.MapId == right.MapId &&
               string.Equals(
                   left.SceneKey,
                   right.SceneKey,
                   StringComparison.Ordinal) &&
               string.Equals(
                   left.NpcKey,
                   right.NpcKey,
                   StringComparison.Ordinal) &&
               string.Equals(
                   left.TemplateKey,
                   right.TemplateKey,
                   StringComparison.Ordinal) &&
               left.ObjectId == right.ObjectId &&
               left.X.Equals(right.X) &&
               left.Z.Equals(right.Z) &&
               left.InteractionId == right.InteractionId &&
               left.AppearanceType == right.AppearanceType &&
               left.Facing.Equals(right.Facing) &&
               left.Detail10077.SequenceEqual(right.Detail10077) &&
               left.Detail10080.SequenceEqual(right.Detail10080);
    }

    public static NpcSpawnDefinition Clone(
        NpcSpawnDefinition definition) =>
        definition with
        {
            Detail10077 = definition.Detail10077.ToArray(),
            Detail10080 = definition.Detail10080.ToArray()
        };
}
