namespace DogRoute.Server;

public sealed class DogServerOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        RequirePositiveOrInfinite(RequestTimeout, nameof(RequestTimeout));
        RequirePositiveOrInfinite(KeepAliveTimeout, nameof(KeepAliveTimeout));
        RequirePositiveOrInfinite(SendTimeout, nameof(SendTimeout));

        if (ShutdownTimeout != Timeout.InfiniteTimeSpan && ShutdownTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout), ShutdownTimeout, "Must not be negative.");

        if (HeartbeatInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval), HeartbeatInterval, "Must be positive.");
    }

    private static void RequirePositiveOrInfinite(TimeSpan value, string name)
    {
        if (value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(name, value, "Must be positive or Timeout.InfiniteTimeSpan.");
    }
}
