namespace Godswar.Server.Operations;

internal sealed class CriticalTaskCollection
{
    private readonly CancellationToken _hostCancellation;
    private readonly CriticalTaskSupervisor _supervisor;
    private readonly List<Task> _tasks = new(12);

    public CriticalTaskCollection(
        CriticalTaskSupervisor supervisor,
        CancellationToken hostCancellation)
    {
        _supervisor = supervisor ??
            throw new ArgumentNullException(nameof(supervisor));
        _hostCancellation = hostCancellation;
    }

    public IReadOnlyList<Task> Items => _tasks;

    public void Start(
        CriticalTaskKind kind,
        Func<CancellationToken, Task> operation)
    {
        _tasks.Add(
            _supervisor.RunAsync(
                kind,
                operation,
                _hostCancellation));
    }
}
