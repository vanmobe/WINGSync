namespace WingSync.Core.Abstractions;

/// <summary>Injectable time source used to make backoff, coalescing, and echo tests deterministic.</summary>
public interface IClock
{
    /// <summary>Gets the current UTC timestamp.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Waits for the requested duration.</summary>
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}
/// <summary>Production wall-clock implementation.</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
