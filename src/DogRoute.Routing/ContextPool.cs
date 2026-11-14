using System.Collections.Concurrent;

namespace DogRoute.Routing;

internal sealed class ContextPool
{
    private const int MaxRetained = 1024;

    private readonly ConcurrentQueue<DogContext> _items = new();
    private int _count;

    public DogContext Rent()
    {
        if (_items.TryDequeue(out DogContext? context))
        {
            Interlocked.Decrement(ref _count);
            return context;
        }

        return new DogContext();
    }

    public void Return(DogContext context)
    {
        context.Clear();

        if (Interlocked.Increment(ref _count) <= MaxRetained)
            _items.Enqueue(context);
        else
            Interlocked.Decrement(ref _count);
    }
}
