using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed class NpcVisibilityTracker
{
    // The working server divides the world into 32-unit cells and exposes the
    // player's cell plus its eight neighbors.
    internal const int CellSize = 32;
    internal const int NeighborRadius = 1;

    private readonly PositionedNpc[] _npcs;
    private readonly HashSet<uint> _visibleObjectIds = [];
    private NpcGridCell? _playerCell;

    public NpcVisibilityTracker(IEnumerable<NpcSpawnDefinition> definitions)
    {
        _npcs = definitions
            .OrderBy(definition => definition.ObjectId)
            .Select(definition => new PositionedNpc(
                definition,
                GetRequiredCell(definition.X, definition.Z, definition.NpcKey)))
            .ToArray();

        var duplicateObjectId = _npcs
            .GroupBy(npc => npc.Definition.ObjectId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateObjectId is not null)
        {
            throw new ArgumentException(
                $"NPC object ID {duplicateObjectId.Key} is defined more than once.",
                nameof(definitions));
        }
    }

    public bool TryCalculate(
        float playerX,
        float playerZ,
        out NpcVisibilityDelta delta)
    {
        delta = default!;
        if (!TryGetCell(playerX, playerZ, out var playerCell))
        {
            return false;
        }

        if (_playerCell == playerCell)
        {
            delta = new NpcVisibilityDelta(playerCell, [], []);
            return true;
        }

        var desired = _npcs
            .Where(npc => IsNeighbor(playerCell, npc.Cell))
            .Select(npc => npc.Definition)
            .ToArray();
        var desiredObjectIds = desired
            .Select(definition => definition.ObjectId)
            .ToHashSet();
        var entering = desired
            .Where(definition => !_visibleObjectIds.Contains(definition.ObjectId))
            .ToArray();
        var leaving = _visibleObjectIds
            .Where(objectId => !desiredObjectIds.Contains(objectId))
            .OrderBy(objectId => objectId)
            .ToArray();

        delta = new NpcVisibilityDelta(playerCell, entering, leaving);
        return true;
    }

    public void Commit(NpcVisibilityDelta delta)
    {
        foreach (var objectId in delta.Leaving)
        {
            _visibleObjectIds.Remove(objectId);
        }

        foreach (var definition in delta.Entering)
        {
            _visibleObjectIds.Add(definition.ObjectId);
        }

        _playerCell = delta.PlayerCell;
    }

    public bool IsVisible(uint objectId)
    {
        return _visibleObjectIds.Contains(objectId);
    }

    internal static bool TryGetCell(float x, float z, out NpcGridCell cell)
    {
        cell = default;
        if (!float.IsFinite(x) || !float.IsFinite(z))
        {
            return false;
        }

        var cellX = Math.Floor((double)x / CellSize);
        var cellZ = Math.Floor((double)z / CellSize);
        if (cellX is < int.MinValue or > int.MaxValue ||
            cellZ is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        cell = new NpcGridCell((int)cellX, (int)cellZ);
        return true;
    }

    private static NpcGridCell GetRequiredCell(float x, float z, string npcKey)
    {
        if (TryGetCell(x, z, out var cell))
        {
            return cell;
        }

        throw new ArgumentException($"NPC {npcKey} has invalid coordinates ({x}, {z}).");
    }

    private static bool IsNeighbor(NpcGridCell playerCell, NpcGridCell npcCell)
    {
        return Math.Abs((long)playerCell.X - npcCell.X) <= NeighborRadius &&
               Math.Abs((long)playerCell.Z - npcCell.Z) <= NeighborRadius;
    }

    private sealed record PositionedNpc(
        NpcSpawnDefinition Definition,
        NpcGridCell Cell);
}

internal readonly record struct NpcGridCell(int X, int Z);

internal sealed record NpcVisibilityDelta(
    NpcGridCell PlayerCell,
    IReadOnlyList<NpcSpawnDefinition> Entering,
    IReadOnlyList<uint> Leaving);
