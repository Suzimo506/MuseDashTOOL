using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MdModManager.Helpers;

public sealed class AsyncExclusiveLock
{
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();
    private bool _isLocked;
    private readonly object _syncRoot = new();

    public Task AcquireAsync()
    {
        TaskCompletionSource<bool> tcs;
        lock (_syncRoot)
        {
            if (!_isLocked)
            {
                _isLocked = true;
                return Task.CompletedTask;
            }
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(tcs);
        }
        return tcs.Task;
    }

    public void Release()
    {
        TaskCompletionSource<bool>? next = null;
        lock (_syncRoot)
        {
            if (_waiters.Count > 0)
            {
                next = _waiters.Dequeue();
            }
            else
            {
                _isLocked = false;
            }
        }
        next?.TrySetResult(true);
    }

    public Task StealAsync(string reason)
    {
        List<TaskCompletionSource<bool>> waitersToCancel;
        TaskCompletionSource<bool>? tcs = null;

        lock (_syncRoot)
        {
            waitersToCancel = new List<TaskCompletionSource<bool>>(_waiters);
            _waiters.Clear();

            if (!_isLocked)
            {
                _isLocked = true;
            }
            else
            {
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(tcs);
            }
        }

        var exception = new OperationCanceledException($"锁已被抢占，原因: {reason}");
        foreach (var waiter in waitersToCancel)
        {
            waiter.TrySetException(exception);
        }

        return tcs != null ? tcs.Task : Task.CompletedTask;
    }
}
