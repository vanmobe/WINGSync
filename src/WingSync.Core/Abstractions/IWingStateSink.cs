using WingSync.Core.Domain;

namespace WingSync.Core.Abstractions;

/// <summary>
/// Receives live observations for durable offline display. Implementations must never
/// replay these values to a console; only the synchronization coordinator may write.
/// </summary>
public interface IWingStateSink
{
    /// <summary>Begins a fresh connection epoch after identity verification.</summary>
    Task BeginEpochAsync(
        string role,
        DiscoveredWing identity,
        long connectionEpoch,
        CancellationToken cancellationToken);

    /// <summary>Stores freshly observed typed parameters.</summary>
    Task StoreAsync(
        string role,
        DiscoveredWing identity,
        long connectionEpoch,
        IReadOnlyList<WingParameter> parameters,
        CancellationToken cancellationToken);

    /// <summary>Marks all values for this physical console stale after an event gap or disconnect.</summary>
    Task MarkStaleAsync(
        string role,
        DiscoveredWing identity,
        CancellationToken cancellationToken);
}
/// <summary>Safe no-op state sink.</summary>
public sealed class NullWingStateSink : IWingStateSink
{
    /// <inheritdoc />
    public Task BeginEpochAsync(
        string role,
        DiscoveredWing identity,
        long connectionEpoch,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoreAsync(
        string role,
        DiscoveredWing identity,
        long connectionEpoch,
        IReadOnlyList<WingParameter> parameters,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task MarkStaleAsync(
        string role,
        DiscoveredWing identity,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
