namespace Godswar.Server.Networking;

internal sealed partial class BoundedByteQueue<T>
    where T : class
{
    private abstract class PendingOperation
    {
        private readonly object _registrationGate = new();
        private CancellationTokenRegistration _registration;
        private bool _hasRegistration;
        private bool _isFinished;

        protected PendingOperation(CancellationToken cancellationToken) =>
            CancellationToken = cancellationToken;

        protected CancellationToken CancellationToken { get; }

        protected void RegisterCancellation(
            object state,
            Action<object?> callback)
        {
            if (!CancellationToken.CanBeCanceled)
            {
                return;
            }

            var registration = CancellationToken.UnsafeRegister(
                callback,
                state);
            var unregister = false;
            lock (_registrationGate)
            {
                if (_isFinished)
                {
                    unregister = true;
                }
                else
                {
                    _registration = registration;
                    _hasRegistration = true;
                }
            }
            if (unregister)
            {
                TryUnregister(registration);
            }
        }

        protected void Finish()
        {
            CancellationTokenRegistration registration = default;
            var unregister = false;
            lock (_registrationGate)
            {
                _isFinished = true;
                if (_hasRegistration)
                {
                    registration = _registration;
                    _hasRegistration = false;
                    unregister = true;
                }
            }
            if (unregister)
            {
                TryUnregister(registration);
            }
        }

        private static void TryUnregister(
            CancellationTokenRegistration registration)
        {
            try
            {
                registration.Unregister();
            }
            catch
            {
            }
        }
    }

    private sealed class PendingEnqueue : PendingOperation
    {
        private readonly BoundedByteQueue<T> _owner;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingEnqueue(
            BoundedByteQueue<T> owner,
            T item,
            int byteCount,
            CancellationToken cancellationToken)
            : base(cancellationToken)
        {
            _owner = owner;
            Item = item;
            ByteCount = byteCount;
        }

        public T Item { get; }
        public int ByteCount { get; }
        public bool IsAdmitted { get; set; }
        public LinkedListNode<PendingEnqueue>? Node { get; set; }
        public Task Task => _completion.Task;

        public void RegisterCancellation() =>
            RegisterCancellation(
                this,
                static state =>
                {
                    var pending = (PendingEnqueue)state!;
                    pending._owner.Cancel(pending);
                });

        public bool SetResult()
        {
            bool completed;
            try
            {
                completed = _completion.TrySetResult();
            }
            catch
            {
                completed = _completion.Task.IsCompleted;
            }
            if (!completed && !_completion.Task.IsCompleted)
            {
                return false;
            }
            Finish();
            return true;
        }

        public bool SetCanceled()
        {
            bool completed;
            try
            {
                completed = _completion.TrySetCanceled(CancellationToken);
            }
            catch
            {
                completed = _completion.Task.IsCompleted;
            }
            if (!completed && !_completion.Task.IsCompleted)
            {
                return false;
            }
            Finish();
            return true;
        }

        public bool SetException(Exception error)
        {
            bool completed;
            try
            {
                completed = _completion.TrySetException(error);
            }
            catch
            {
                completed = _completion.Task.IsCompleted;
            }
            if (!completed && !_completion.Task.IsCompleted)
            {
                return false;
            }
            Finish();
            return true;
        }
    }

    private sealed class PendingDequeue : PendingOperation
    {
        private readonly BoundedByteQueue<T> _owner;
        private readonly TaskCompletionSource<DequeueResult<T>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingDequeue(
            BoundedByteQueue<T> owner,
            CancellationToken cancellationToken)
            : base(cancellationToken) =>
            _owner = owner;

        public LinkedListNode<PendingDequeue>? Node { get; set; }
        public Task<DequeueResult<T>> Task => _completion.Task;

        public void RegisterCancellation() =>
            RegisterCancellation(
                this,
                static state =>
                {
                    var pending = (PendingDequeue)state!;
                    pending._owner.Cancel(pending);
                });

        public bool SetResult(DequeueResult<T> result)
        {
            bool completed;
            try
            {
                completed = _completion.TrySetResult(result);
            }
            catch
            {
                completed = _completion.Task.IsCompleted;
            }
            if (!completed && !_completion.Task.IsCompleted)
            {
                return false;
            }
            Finish();
            return true;
        }

        public bool SetCanceled()
        {
            bool completed;
            try
            {
                completed = _completion.TrySetCanceled(CancellationToken);
            }
            catch
            {
                completed = _completion.Task.IsCompleted;
            }
            if (!completed && !_completion.Task.IsCompleted)
            {
                return false;
            }
            Finish();
            return true;
        }

        public bool SetException(Exception error)
        {
            bool completed;
            try
            {
                completed = _completion.TrySetException(error);
            }
            catch
            {
                completed = _completion.Task.IsCompleted;
            }
            if (!completed && !_completion.Task.IsCompleted)
            {
                return false;
            }
            Finish();
            return true;
        }
    }
}
