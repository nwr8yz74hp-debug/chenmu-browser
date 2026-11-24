using System;
using System.Threading;
using System.Threading.Tasks;
namespace ChenmuBrowser
{
    internal sealed class DebouncedWorkQueue<T> : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Action<T> _action;
        private readonly Action<Exception> _onError;
        private readonly int _delayMilliseconds;
        private readonly Timer _timer;
        private Task _workTask = Task.CompletedTask;
        private T _pending;
        private bool _hasPending;
        private bool _disposed;
        public DebouncedWorkQueue(Action<T> action, Action<Exception> onError, int delayMilliseconds)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));
            if (delayMilliseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(delayMilliseconds));
            _action = action;
            _onError = onError;
            _delayMilliseconds = delayMilliseconds;
            _timer = new Timer(DrainPending, null, Timeout.Infinite, Timeout.Infinite);
        }
        public void Queue(T work)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _pending = work;
                _hasPending = true;
                _timer.Change(_delayMilliseconds, Timeout.Infinite);
            }
        }
        public void Flush()
        {
            Task workTask;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                if (_hasPending)
                    SchedulePendingLocked();
                workTask = _workTask;
            }
            workTask.GetAwaiter().GetResult();
        }
        private void DrainPending(object state)
        {
            lock (_gate)
            {
                if (_disposed || !_hasPending)
                    return;
                SchedulePendingLocked();
            }
        }
        private void SchedulePendingLocked()
        {
            var work = _pending;
            _pending = default(T);
            _hasPending = false;
            var previous = _workTask;
            _workTask = Task.Run(async delegate
            {
                try { await previous.ConfigureAwait(false); }
                catch { }
                try
                {
                    _action(work);
                }
                catch (Exception ex)
                {
                    if (_onError != null)
                    {
                        try { _onError(ex); }
                        catch { }
                    }
                }
            });
        }
        public void Dispose()
        {
            Task workTask;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                if (_hasPending)
                    SchedulePendingLocked();
                workTask = _workTask;
            }
            workTask.GetAwaiter().GetResult();
            lock (_gate)
            {
                _disposed = true;
                _timer.Dispose();
            }
        }
    }
}
