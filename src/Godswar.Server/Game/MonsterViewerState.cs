using System.Collections.Concurrent;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed record MonsterVisibilityDelta(
    WorldGridCell PlayerCell,
    IReadOnlyList<MonsterRuntimeSnapshot> Entering,
    IReadOnlyList<uint> Leaving);

internal sealed class MonsterVisibilityTransition : IAsyncDisposable
{
    private MonsterViewerState? _viewer;
    private readonly IReadOnlyDictionary<uint, MonsterAppearanceVersion> _desiredVersions;

    public MonsterVisibilityTransition(
        MonsterViewerState viewer,
        MonsterVisibilityDelta delta,
        IReadOnlyDictionary<uint, MonsterAppearanceVersion> desiredVersions)
    {
        _viewer = viewer;
        Delta = delta;
        _desiredVersions = desiredVersions;
    }

    public MonsterVisibilityDelta Delta { get; }

    public bool IsDesiredVisible(uint objectId)
    {
        return _desiredVersions.ContainsKey(objectId);
    }

    public void Commit()
    {
        var viewer = _viewer ??
            throw new ObjectDisposedException(
                nameof(MonsterVisibilityTransition));
        foreach (var objectId in viewer.VisibleMonsterVersions.Keys)
        {
            if (!_desiredVersions.ContainsKey(objectId))
            {
                viewer.VisibleMonsterVersions.TryRemove(objectId, out _);
            }
        }

        // Only an appearance actually sent by this transition may advance the
        // viewer's health revision. Merely observing a newer runtime snapshot
        // during unrelated AOI work must not suppress a pending delta.
        foreach (var monster in Delta.Entering)
        {
            viewer.VisibleMonsterVersions[monster.ObjectId] =
                monster.AppearanceVersion;
        }

        viewer.PlayerCell = Delta.PlayerCell;
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }

    internal void Release()
    {
        var viewer = Interlocked.Exchange(ref _viewer, null);
        viewer?.TransitionGate.Release();
    }
}

internal sealed class MonsterViewerState
{
    public SemaphoreSlim TransitionGate { get; } = new(1, 1);

    public ConcurrentDictionary<uint, MonsterAppearanceVersion>
        VisibleMonsterVersions { get; } = [];

    public WorldGridCell? PlayerCell { get; set; }
}

internal sealed class MonsterViewerDeliveryLease : IAsyncDisposable
{
    private MonsterViewerState? _viewer;
    private readonly IReadOnlyList<MonsterHealthMutation>
        _directHealthMutations;
    private readonly IReadOnlyList<uint> _reconciliationObjectIds;
    private readonly IReadOnlyList<uint> _terminalObjectIds;

    public MonsterViewerDeliveryLease(
        MonsterViewerState viewer,
        IReadOnlyList<MonsterHealthMutation> directHealthMutations,
        IReadOnlyList<uint> reconciliationObjectIds,
        IReadOnlyList<MonsterRuntimeSnapshot> reconciliationMonsters,
        IReadOnlyList<uint> terminalObjectIds)
    {
        _viewer = viewer;
        _directHealthMutations = directHealthMutations;
        _reconciliationObjectIds = reconciliationObjectIds;
        ReconciliationMonsters = reconciliationMonsters;
        _terminalObjectIds = terminalObjectIds;
    }

    public IReadOnlyList<MonsterHealthMutation> DirectHealthMutations =>
        _directHealthMutations;

    public IReadOnlyList<uint> ReconciliationObjectIds =>
        _reconciliationObjectIds;

    public IReadOnlyList<uint> TerminalObjectIds => _terminalObjectIds;

    public IReadOnlyList<MonsterRuntimeSnapshot> ReconciliationMonsters
    {
        get;
    }

    public void Commit()
    {
        var viewer = _viewer ??
            throw new ObjectDisposedException(
                nameof(MonsterViewerDeliveryLease));
        var reconciledVersions = ReconciliationMonsters.ToDictionary(
            monster => monster.ObjectId,
            monster => monster.AppearanceVersion);
        foreach (var objectId in _reconciliationObjectIds)
        {
            if (reconciledVersions.TryGetValue(objectId, out var version))
            {
                viewer.VisibleMonsterVersions[objectId] = version;
            }
            else
            {
                viewer.VisibleMonsterVersions.TryRemove(objectId, out _);
            }
        }

        foreach (var mutation in _directHealthMutations)
        {
            viewer.VisibleMonsterVersions[mutation.ObjectId] =
                mutation.AfterVersion;
        }
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }

    internal void Release()
    {
        var viewer = Interlocked.Exchange(ref _viewer, null);
        viewer?.TransitionGate.Release();
    }
}
