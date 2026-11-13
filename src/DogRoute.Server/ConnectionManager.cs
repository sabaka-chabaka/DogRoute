using System.Collections.Concurrent;

namespace DogRoute.Server;

internal sealed class ConnectionManager
{
    private readonly ConcurrentDictionary<DogConnection, byte> _connections = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;
    private volatile bool _draining;

    public bool IsDraining => _draining;

    public void Add(DogConnection connection)
    {
        _connections[connection] = 0;
        Interlocked.Increment(ref _count);
    }

    public void Remove(DogConnection connection)
    {
        if (!_connections.TryRemove(connection, out _)) return;

        if (Interlocked.Decrement(ref _count) == 0 && _draining)
            _drained.TrySetResult();
    }

    public async Task RunHeartbeatAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                long now = Environment.TickCount64;
                bool draining = _draining;

                foreach (var entry in _connections)
                {
                    entry.Key.CheckTimeout(now);
                    if (draining) entry.Key.CloseIfIdle();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task DrainAsync(TimeSpan timeout)
    {
        _draining = true;

        foreach (var entry in _connections)
            entry.Key.CloseIfIdle();

        if (Volatile.Read(ref _count) == 0)
            _drained.TrySetResult();

        try
        {
            await _drained.Task.WaitAsync(timeout);
            return;
        }
        catch (TimeoutException) { }

        foreach (var entry in _connections)
            entry.Key.Abort();

        try { await _drained.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (TimeoutException) { }
    }
}
