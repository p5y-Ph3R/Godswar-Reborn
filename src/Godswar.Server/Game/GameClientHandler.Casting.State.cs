namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private sealed class PendingSkillCast(
        long generation,
        uint skillId,
        string label,
        PendingSkillCastContext context,
        CancellationTokenSource cancellation,
        Func<CancellationToken, Task> completeAsync,
        Func<bool>? additionalCompletionValidation)
    {
        private readonly object _cancellationSync = new();
        private readonly CancellationToken _cancellationToken =
            cancellation.Token;
        private readonly TaskCompletionSource _lifecycleStart =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<StartPublicationResult>
            _startPublication =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool>
            _interruptionNotificationAdmission =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _cancellation = cancellation;

        public long Generation { get; } = generation;

        public uint SkillId { get; } = skillId;

        public string Label { get; } = label;

        public PendingSkillCastContext Context { get; } = context;

        public CancellationToken CancellationToken =>
            _cancellationToken;

        public Func<CancellationToken, Task> CompleteAsync { get; } =
            completeAsync;

        public Func<bool>? AdditionalCompletionValidation { get; } =
            additionalCompletionValidation;

        public Task LifecycleTask { get; set; } =
            Task.CompletedTask;

        public Task<StartPublicationResult> StartPublication =>
            _startPublication.Task;

        public Task<bool> InterruptionNotificationAdmission =>
            _interruptionNotificationAdmission.Task;

        public bool CompletionClaimed { get; set; }

        public bool CompletionSucceeded { get; set; }

        public bool InterruptionClaimed { get; set; }

        public bool PreparedInterruptionClaimed { get; set; }

        public bool InterruptionNotificationClaimed { get; set; }

        public int PreparedInterruptionReservations { get; set; }

        public void CompleteStartPublication(Exception? error) =>
            _startPublication.TrySetResult(
                new StartPublicationResult(error));

        public void CompleteInterruptionNotificationAdmission(
            bool admitted) =>
            _interruptionNotificationAdmission.TrySetResult(admitted);

        public void DisposeCancellation()
        {
            lock (_cancellationSync)
            {
                _cancellation?.Dispose();
                _cancellation = null;
            }
        }

        public void ReleaseLifecycleStart() =>
            _lifecycleStart.TrySetResult();

        public void RequestCancellation()
        {
            lock (_cancellationSync)
            {
                _cancellation?.Cancel();
            }
        }

        public Task WaitForLifecycleStartAsync() =>
            _lifecycleStart.Task;
    }

    private readonly record struct StartPublicationResult(
        Exception? Error);

    private readonly record struct PendingSkillCastContext(
        int CharacterId,
        uint ObjectId,
        byte MapId,
        float StartX,
        float StartZ,
        long LifeRevision);
}
