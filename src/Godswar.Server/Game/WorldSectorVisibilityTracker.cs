namespace Godswar.Server.Game;

internal sealed class WorldSectorVisibilityTracker<TDefinition>
{
    // The working server divides the world into 32-unit cells and exposes the
    // player's cell plus its eight neighbors.
    internal const int CellSize = 32;
    internal const int NeighborRadius = 1;

    private readonly PositionedObject[] _objects;
    private readonly Func<TDefinition, uint> _objectIdSelector;
    private readonly HashSet<uint> _visibleObjectIds = [];
    private WorldGridCell? _playerCell;

    public WorldSectorVisibilityTracker(
        IEnumerable<TDefinition> definitions,
        Func<TDefinition, uint> objectIdSelector,
        Func<TDefinition, float> xSelector,
        Func<TDefinition, float> zSelector,
        string objectKind)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(objectIdSelector);
        ArgumentNullException.ThrowIfNull(xSelector);
        ArgumentNullException.ThrowIfNull(zSelector);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKind);

        _objectIdSelector = objectIdSelector;
        _objects = definitions
            .Select(definition => new PositionedObject(
                definition,
                objectIdSelector(definition),
                GetRequiredCell(
                    xSelector(definition),
                    zSelector(definition),
                    objectKind,
                    objectIdSelector(definition))))
            .OrderBy(worldObject => worldObject.ObjectId)
            .ToArray();

        var duplicateObjectId = _objects
            .GroupBy(worldObject => worldObject.ObjectId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateObjectId is not null)
        {
            throw new ArgumentException(
                $"{objectKind} object ID {duplicateObjectId.Key} is defined more than once.",
                nameof(definitions));
        }
    }

    public bool TryCalculate(
        float playerX,
        float playerZ,
        out WorldVisibilityDelta<TDefinition> delta)
    {
        delta = default!;
        if (!TryGetCell(playerX, playerZ, out var playerCell))
        {
            return false;
        }

        if (_playerCell == playerCell)
        {
            delta = new WorldVisibilityDelta<TDefinition>(playerCell, [], []);
            return true;
        }

        var desired = _objects
            .Where(worldObject => IsNeighbor(playerCell, worldObject.Cell))
            .ToArray();
        var desiredObjectIds = desired
            .Select(worldObject => worldObject.ObjectId)
            .ToHashSet();
        var entering = desired
            .Where(worldObject => !_visibleObjectIds.Contains(worldObject.ObjectId))
            .Select(worldObject => worldObject.Definition)
            .ToArray();
        var leaving = _visibleObjectIds
            .Where(objectId => !desiredObjectIds.Contains(objectId))
            .OrderBy(objectId => objectId)
            .ToArray();

        delta = new WorldVisibilityDelta<TDefinition>(playerCell, entering, leaving);
        return true;
    }

    public void Commit(WorldVisibilityDelta<TDefinition> delta)
    {
        foreach (var objectId in delta.Leaving)
        {
            _visibleObjectIds.Remove(objectId);
        }

        foreach (var definition in delta.Entering)
        {
            _visibleObjectIds.Add(_objectIdSelector(definition));
        }

        _playerCell = delta.PlayerCell;
    }

    public bool IsVisible(uint objectId)
    {
        return _visibleObjectIds.Contains(objectId);
    }

    internal static bool TryGetCell(float x, float z, out WorldGridCell cell)
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

        cell = new WorldGridCell((int)cellX, (int)cellZ);
        return true;
    }

    private static WorldGridCell GetRequiredCell(
        float x,
        float z,
        string objectKind,
        uint objectId)
    {
        if (TryGetCell(x, z, out var cell))
        {
            return cell;
        }

        throw new ArgumentException(
            $"{objectKind} object {objectId} has invalid coordinates ({x}, {z}).");
    }

    private static bool IsNeighbor(WorldGridCell playerCell, WorldGridCell objectCell)
    {
        return Math.Abs((long)playerCell.X - objectCell.X) <= NeighborRadius &&
               Math.Abs((long)playerCell.Z - objectCell.Z) <= NeighborRadius;
    }

    private sealed record PositionedObject(
        TDefinition Definition,
        uint ObjectId,
        WorldGridCell Cell);
}

internal readonly record struct WorldGridCell(int X, int Z);

internal sealed record WorldVisibilityDelta<TDefinition>(
    WorldGridCell PlayerCell,
    IReadOnlyList<TDefinition> Entering,
    IReadOnlyList<uint> Leaving);
