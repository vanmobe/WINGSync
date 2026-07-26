using WingSync.Core.Abstractions;
using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Persistence;

/// <summary>
/// Adapts live coordinator observations to debounced, identity-partitioned disk snapshots.
/// The type deliberately implements no replay or console-write API.
/// </summary>
public sealed class WingStateCacheSink : IWingStateSink, IAsyncDisposable
{
    private static readonly TimeSpan AssemblyDebounce = TimeSpan.FromMilliseconds(150);
    private readonly object sync = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly WingStateCache cache;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, PartitionState> partitions = new(StringComparer.Ordinal);
    private bool disposed;

    /// <summary>Initializes the sink around the durable cache store.</summary>
    public WingStateCacheSink(WingStateCache cache, TimeProvider? timeProvider = null)
    {
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task BeginEpochAsync(
        string role,
        DiscoveredWing identity,
        long connectionEpoch,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(role);
            ArgumentNullException.ThrowIfNull(identity);

            lock (sync)
            {
                var partition = GetOrCreatePartition(role, identity);
                partition.Identity = identity;
                partition.Epoch = connectionEpoch;
                partition.Revision = 0;
                foreach (var key in partition.Values.Keys.ToArray())
                {
                    var cached = partition.Values[key];
                    partition.Values[key] = cached with
                    {
                        Metadata = cached.Metadata with
                        {
                            ConsoleSerial = identity.SerialNumber,
                            ConsoleModel = identity.Model,
                            FirmwareVersion = identity.FirmwareVersion,
                            ConnectionEpoch = connectionEpoch,
                            Freshness = CacheFreshness.Stale,
                        },
                    };
                }

                ScheduleFlush(role, partition);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StoreAsync(
        string role,
        DiscoveredWing identity,
        long connectionEpoch,
        IReadOnlyList<WingParameter> parameters,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(role);
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(parameters);
            if (parameters.Count == 0)
            {
                return;
            }

            lock (sync)
            {
                var partition = GetOrCreatePartition(role, identity);
                if (partition.Epoch != connectionEpoch)
                {
                    throw new InvalidOperationException(
                        $"Cache epoch for {role} changed during an observation.");
                }

                foreach (var parameter in parameters)
                {
                    if (parameter.TokenPath.Length is < 2 or > 512 ||
                        parameter.TokenPath[0] != '/')
                    {
                        throw new InvalidDataException("Cache accepts only canonical WING token paths.");
                    }

                    var revision = ++partition.Revision;
                    var metadata = new CacheMetadata(
                        identity.SerialNumber,
                        identity.Model,
                        identity.FirmwareVersion,
                        WapiVersion: 0,
                        parameter.ObservedAt,
                        connectionEpoch,
                        revision,
                        CacheFreshness.Fresh);
                    partition.Values[parameter.TokenPath] =
                        new CachedWingValue(parameter.TokenPath, parameter.Value, metadata);
                }

                ScheduleFlush(role, partition);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task MarkStaleAsync(
        string role,
        DiscoveredWing identity,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(role);
            ArgumentNullException.ThrowIfNull(identity);

            lock (sync)
            {
                var partition = GetOrCreatePartition(role, identity);
                foreach (var key in partition.Values.Keys.ToArray())
                {
                    var value = partition.Values[key];
                    partition.Values[key] = value with
                    {
                        Metadata = value.Metadata with
                        {
                            Freshness = CacheFreshness.Stale,
                        },
                    };
                }

                ScheduleFlush(role, partition);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>Loads an exact console partition for offline display only.</summary>
    public Task<WingStateCacheLoadResult> LoadForDisplayAsync(
        DiscoveredWing identity,
        long? activeConnectionEpoch = null,
        CancellationToken cancellationToken = default) =>
        cache.LoadAsync(
            ToCacheIdentity(identity),
            activeConnectionEpoch,
            cancellationToken);

    /// <summary>
    /// Loads the newest display-only cache partition for a persisted serial pin
    /// when the console is not currently discoverable.
    /// </summary>
    public Task<WingStateCacheLoadResult> LoadForDisplayBySerialAsync(
        string consoleSerial,
        CancellationToken cancellationToken = default) =>
        cache.LoadLatestForDisplayBySerialAsync(consoleSerial, cancellationToken);

    /// <summary>Flushes all assembled state and the underlying atomic cache.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            QueueAllSnapshots();
            await cache.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>
    /// Clears assembled in-memory observations and moves durable cache files to a
    /// recoverable quarantine directory.
    /// </summary>
    public async Task ResetAsync(
        string quarantineDirectory,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sync)
            {
                foreach (var partition in partitions.Values)
                {
                    partition.FlushCancellation?.Cancel();
                    partition.FlushCancellation?.Dispose();
                    partition.FlushCancellation = null;
                }

                partitions.Clear();
            }

            await cache.ResetAsync(quarantineDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            // Latch disposal while holding the same operation gate as StoreAsync.
            // Every store already queued ahead of disposal is therefore included,
            // while no later store can schedule a debounce after the final drain.
            lock (sync)
            {
                disposed = true;
            }

            QueueAllSnapshots();
            await cache.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private PartitionState GetOrCreatePartition(string role, DiscoveredWing identity)
    {
        if (partitions.TryGetValue(role, out var existing))
        {
            if (!string.Equals(
                    existing.Identity.SerialNumber,
                    identity.SerialNumber,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.Identity.Model, identity.Model, StringComparison.Ordinal) ||
                !string.Equals(
                    existing.Identity.FirmwareVersion,
                    identity.FirmwareVersion,
                    StringComparison.Ordinal))
            {
                existing.FlushCancellation?.Cancel();
                existing.FlushCancellation?.Dispose();
                existing = new PartitionState(identity);
                partitions[role] = existing;
            }

            return existing;
        }

        var created = new PartitionState(identity);
        partitions.Add(role, created);
        return created;
    }

    private void ScheduleFlush(string role, PartitionState partition)
    {
        partition.Generation++;
        var generation = partition.Generation;
        partition.FlushCancellation?.Cancel();
        partition.FlushCancellation?.Dispose();
        partition.FlushCancellation = new CancellationTokenSource();
        _ = FlushAfterDelayAsync(role, partition, generation, partition.FlushCancellation.Token);
    }

    private void QueueAllSnapshots()
    {
        lock (sync)
        {
            foreach (var partition in partitions.Values)
            {
                partition.FlushCancellation?.Cancel();
                partition.FlushCancellation?.Dispose();
                partition.FlushCancellation = null;
                QueueSnapshot(partition);
            }
        }
    }

    private async Task FlushAfterDelayAsync(
        string role,
        PartitionState partition,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AssemblyDebounce, cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                if (disposed ||
                    !partitions.TryGetValue(role, out var current) ||
                    !ReferenceEquals(current, partition) ||
                    current.Generation != generation)
                {
                    return;
                }

                QueueSnapshot(current);
                current.FlushCancellation?.Dispose();
                current.FlushCancellation = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer observation superseded this assembly.
        }
    }

    private void QueueSnapshot(PartitionState partition)
    {
        var snapshot = new WingStateSnapshot(
            ToCacheIdentity(partition.Identity),
            timeProvider.GetUtcNow(),
            partition.Values.Values
                .OrderBy(static value => value.TokenPath, StringComparer.Ordinal)
                .ToArray());
        cache.QueueWrite(snapshot);
    }

    private static WingCacheIdentity ToCacheIdentity(DiscoveredWing identity) =>
        new(identity.SerialNumber, identity.Model, identity.FirmwareVersion);

    private sealed class PartitionState
    {
        public PartitionState(DiscoveredWing identity)
        {
            Identity = identity;
        }

        public DiscoveredWing Identity { get; set; }

        public long Epoch { get; set; }

        public long Revision { get; set; }

        public long Generation { get; set; }

        public Dictionary<string, CachedWingValue> Values { get; } = new(StringComparer.Ordinal);

        public CancellationTokenSource? FlushCancellation { get; set; }
    }
}
