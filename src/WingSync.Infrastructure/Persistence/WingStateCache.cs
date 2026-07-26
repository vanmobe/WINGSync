using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Persistence;

/// <summary>
/// Persists debounced, identity-keyed observational WING state without exposing replay behavior.
/// </summary>
public sealed class WingStateCache : IAsyncDisposable
{
    private const int MaximumSerialLookupFiles = 64;

    /// <summary>
    /// The state-cache schema written by this build.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private readonly object _pendingLock = new();
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly SemaphoreSlim _resetGate = new(1, 1);
    private readonly Dictionary<string, PendingWrite> _pending = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly TimeProvider _timeProvider;
    private readonly string _directoryPath;
    private readonly TimeSpan _writeDebounce;
    private readonly TimeSpan _maximumFreshAge;
    private readonly long _maximumFileSizeBytes;
    private bool _disposed;
    private int _disposeStarted;
    private long _generation;

    /// <summary>
    /// Initializes a new instance of the <see cref="WingStateCache"/> class.
    /// </summary>
    /// <param name="options">Cache directory, debounce, freshness and size options.</param>
    /// <param name="timeProvider">
    /// The clock used for persistence and freshness, or the system clock when omitted.
    /// </param>
    public WingStateCache(
        WingStateCacheOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _directoryPath = Path.GetFullPath(options.DirectoryPath);
        _writeDebounce = options.WriteDebounce;
        _maximumFreshAge = options.MaximumFreshAge;
        _maximumFileSizeBytes = options.MaximumFileSizeBytes;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _serializerOptions = CreateSerializerOptions();
    }

    /// <summary>
    /// Raised when a debounced background persistence operation fails.
    /// </summary>
    public event EventHandler<WingStateCacheWriteFailedEventArgs>? WriteFailed;

    /// <summary>
    /// Coalesces a snapshot into a delayed atomic write for its exact serial/model/firmware partition.
    /// </summary>
    /// <param name="snapshot">The observational snapshot to persist.</param>
    public void QueueWrite(WingStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);

        _resetGate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = GetPartitionKey(snapshot.Identity);
            PendingWrite pending;
            lock (_pendingLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_pending.Remove(key, out var previous))
                {
                    previous.DelayCancellation.Cancel();
                }

                pending = new PendingWrite(
                    snapshot,
                    Interlocked.Increment(ref _generation),
                    new CancellationTokenSource());
                _pending.Add(key, pending);
                pending.Worker = DebounceAndPersistAsync(
                    key,
                    pending,
                    pending.DelayCancellation.Token);
            }
        }
        finally
        {
            _resetGate.Release();
        }
    }

    /// <summary>
    /// Loads one exact identity partition. Without a matching active connection epoch,
    /// a valid disk snapshot is deliberately classified as stale.
    /// </summary>
    /// <param name="identity">The serial/model/firmware partition.</param>
    /// <param name="activeConnectionEpoch">
    /// The currently healthy live epoch, or <see langword="null"/> when no live validation exists.
    /// </param>
    /// <param name="cancellationToken">Cancels file I/O.</param>
    /// <returns>A safe load result that can never authorize automatic replay.</returns>
    public async Task<WingStateCacheLoadResult> LoadAsync(
        WingCacheIdentity identity,
        long? activeConnectionEpoch = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateIdentity(identity);
        var primaryPath = GetPrimaryPath(identity);
        var backupPath = string.Concat(primaryPath, ".bak");

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var primary = await TryLoadFileAsync(
                    primaryPath,
                    identity,
                    cancellationToken)
                .ConfigureAwait(false);
            if (primary.Status == CacheFileStatus.Loaded)
            {
                return CreateSuccessfulLoadResult(
                    WingStateCacheLoadStatus.Loaded,
                    primary.Envelope!,
                    activeConnectionEpoch);
            }

            if (primary.Status == CacheFileStatus.UnsupportedSchema)
            {
                return new WingStateCacheLoadResult(
                    WingStateCacheLoadStatus.UnsupportedSchema,
                    CacheFreshness.Unknown,
                    Message: primary.Message);
            }

            if (primary.Status == CacheFileStatus.Failed)
            {
                return new WingStateCacheLoadResult(
                    WingStateCacheLoadStatus.Failed,
                    CacheFreshness.Unknown,
                    Message: primary.Message);
            }

            string? quarantinedPath = null;
            var mismatch = primary.Status == CacheFileStatus.IdentityMismatch;
            if (primary.Status is CacheFileStatus.Corrupt or CacheFileStatus.IdentityMismatch)
            {
                if (!TryQuarantine(
                        primaryPath,
                        mismatch ? "identity-mismatch" : "corrupt",
                        out quarantinedPath,
                        out var primaryQuarantineError))
                {
                    return new WingStateCacheLoadResult(
                        WingStateCacheLoadStatus.Failed,
                        CacheFreshness.Unknown,
                        Message: primaryQuarantineError);
                }
            }

            var backup = await TryLoadFileAsync(
                    backupPath,
                    identity,
                    cancellationToken)
                .ConfigureAwait(false);
            if (backup.Status == CacheFileStatus.Loaded)
            {
                var recovered = CreateSuccessfulLoadResult(
                    WingStateCacheLoadStatus.RecoveredFromBackup,
                    backup.Envelope!,
                    activeConnectionEpoch);
                return recovered with { QuarantinedPath = quarantinedPath };
            }

            if (backup.Status is CacheFileStatus.Corrupt or CacheFileStatus.IdentityMismatch)
            {
                var backupMismatch = backup.Status == CacheFileStatus.IdentityMismatch;
                if (!TryQuarantine(
                        backupPath,
                        backupMismatch ? "identity-mismatch" : "corrupt",
                        out var backupQuarantine,
                        out var backupQuarantineError))
                {
                    return new WingStateCacheLoadResult(
                        WingStateCacheLoadStatus.Failed,
                        CacheFreshness.Unknown,
                        QuarantinedPath: quarantinedPath,
                        Message: backupQuarantineError);
                }

                quarantinedPath ??= backupQuarantine;
                mismatch |= backupMismatch;
            }

            if (backup.Status == CacheFileStatus.UnsupportedSchema)
            {
                return new WingStateCacheLoadResult(
                    WingStateCacheLoadStatus.UnsupportedSchema,
                    CacheFreshness.Unknown,
                    QuarantinedPath: quarantinedPath,
                    Message: backup.Message);
            }

            if (backup.Status == CacheFileStatus.Failed)
            {
                return new WingStateCacheLoadResult(
                    WingStateCacheLoadStatus.Failed,
                    CacheFreshness.Unknown,
                    QuarantinedPath: quarantinedPath,
                    Message: backup.Message);
            }

            if (quarantinedPath is not null)
            {
                return new WingStateCacheLoadResult(
                    mismatch
                        ? WingStateCacheLoadStatus.IdentityMismatchQuarantined
                        : WingStateCacheLoadStatus.CorruptQuarantined,
                    CacheFreshness.Unknown,
                    QuarantinedPath: quarantinedPath,
                    Message: primary.Message ?? backup.Message);
            }

            return new WingStateCacheLoadResult(
                WingStateCacheLoadStatus.Missing,
                CacheFreshness.Unknown);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// Finds the newest observational partition for an exact serial number when
    /// discovery is unavailable. This display-only lookup never yields replay authority.
    /// </summary>
    /// <param name="consoleSerial">The previously persisted hardware-identity pin.</param>
    /// <param name="cancellationToken">Cancels bounded cache inspection and loading.</param>
    /// <returns>The newest valid partition for the serial, or a missing/failed result.</returns>
    public async Task<WingStateCacheLoadResult> LoadLatestForDisplayBySerialAsync(
        string consoleSerial,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateIdentityPart(consoleSerial, nameof(consoleSerial), 96);

        var search = await FindPartitionsBySerialAsync(
                consoleSerial.Trim(),
                cancellationToken)
            .ConfigureAwait(false);
        WingStateCacheLoadResult? firstFailure = null;
        foreach (var candidate in search.Candidates)
        {
            var result = await LoadAsync(
                    candidate.Identity,
                    activeConnectionEpoch: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Snapshot is not null)
            {
                return result;
            }

            if (result.Status != WingStateCacheLoadStatus.Missing)
            {
                firstFailure ??= result;
            }
        }

        return firstFailure ??
            (search.ErrorMessage is null
                ? new WingStateCacheLoadResult(
                    WingStateCacheLoadStatus.Missing,
                    CacheFreshness.Unknown)
                : new WingStateCacheLoadResult(
                    WingStateCacheLoadStatus.Failed,
                    CacheFreshness.Unknown,
                    Message: search.ErrorMessage));
    }

    /// <summary>
    /// Cancels debounce timers and atomically persists every latest pending snapshot.
    /// </summary>
    /// <param name="cancellationToken">Cancels pending file I/O.</param>
    /// <returns>A task that completes when all snapshots queued before completion are flushed.</returns>
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return FlushSerializedAsync(cancellationToken);
    }

    /// <summary>
    /// Cancels pending writes, moves all durable cache files to a recoverable quarantine
    /// directory, and leaves an empty active cache directory.
    /// </summary>
    public async Task ResetAsync(
        string quarantineDirectory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineDirectory);
        var quarantinePath = Path.GetFullPath(quarantineDirectory);
        var cachePath = Path.GetFullPath(_directoryPath);
        if (quarantinePath.Equals(cachePath, StringComparison.OrdinalIgnoreCase) ||
            quarantinePath.StartsWith(
                string.Concat(cachePath.TrimEnd(Path.DirectorySeparatorChar), Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "De cachequarantaine moet buiten de actieve cachemap liggen.",
                nameof(quarantineDirectory));
        }

        await _resetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingWrite[] pendingWrites;
            lock (_pendingLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                pendingWrites = _pending.Values.ToArray();
                _pending.Clear();
                foreach (var pending in pendingWrites)
                {
                    pending.DelayCancellation.Cancel();
                }
            }

            await Task.WhenAll(pendingWrites.Select(static pending => pending.Worker))
                .ConfigureAwait(false);
            await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(quarantinePath);
                if (Directory.Exists(cachePath))
                {
                    foreach (var sourcePath in Directory.EnumerateFiles(
                                 cachePath,
                                 "*",
                                 SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var destinationPath = Path.Combine(
                            quarantinePath,
                            Path.GetFileName(sourcePath));
                        if (File.Exists(destinationPath))
                        {
                            throw new IOException(
                                $"Cachequarantaine bevat al '{Path.GetFileName(sourcePath)}'.");
                        }

                        File.Move(sourcePath, destinationPath);
                    }
                }

                Directory.CreateDirectory(cachePath);
            }
            finally
            {
                _ioGate.Release();
            }
        }
        finally
        {
            _resetGate.Release();
        }
    }

    /// <summary>
    /// Flushes pending snapshots and releases cache resources.
    /// </summary>
    /// <returns>A task representing asynchronous disposal.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        await _resetGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_pendingLock)
            {
                _disposed = true;
            }

            await FlushCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _resetGate.Release();
        }

        _ioGate.Dispose();
        _resetGate.Dispose();
    }

    private async Task<SerialPartitionSearchResult> FindPartitionsBySerialAsync(
        string consoleSerial,
        CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directoryPath))
            {
                return new SerialPartitionSearchResult([]);
            }

            string[] paths;
            try
            {
                paths = Directory
                    .EnumerateFiles(_directoryPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(static path =>
                        path.EndsWith(".wingstate.json", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".wingstate.json.bak", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(MaximumSerialLookupFiles)
                    .ToArray();
            }
            catch (IOException exception)
            {
                return new SerialPartitionSearchResult([], exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                return new SerialPartitionSearchResult([], exception.Message);
            }

            var candidates = new Dictionary<string, SerialPartitionCandidate>(
                StringComparer.OrdinalIgnoreCase);
            string? firstReadError = null;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CacheEnvelope? envelope;
                try
                {
                    var content = await AtomicJsonFile.ReadBoundedAsync(
                            path,
                            _maximumFileSizeBytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                    envelope = JsonSerializer.Deserialize<CacheEnvelope>(
                        content,
                        _serializerOptions);
                }
                catch (Exception exception) when (
                    exception is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException or
                    JsonException or
                    NotSupportedException)
                {
                    firstReadError ??= exception.Message;
                    continue;
                }

                if (envelope?.Identity is null ||
                    envelope.SchemaVersion != CurrentSchemaVersion ||
                    !envelope.Identity.ConsoleSerial.Trim().Equals(
                        consoleSerial,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    ValidateIdentity(envelope.Identity);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                var primaryPath = Path.GetFullPath(GetPrimaryPath(envelope.Identity));
                var candidatePath = Path.GetFullPath(path);
                if (!candidatePath.Equals(primaryPath, StringComparison.OrdinalIgnoreCase) &&
                    !candidatePath.Equals(
                        string.Concat(primaryPath, ".bak"),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key = string.Concat(
                    envelope.Identity.ConsoleSerial.Trim(),
                    "\0",
                    envelope.Identity.ConsoleModel.Trim(),
                    "\0",
                    envelope.Identity.FirmwareVersion.Trim());
                var candidate = new SerialPartitionCandidate(
                    envelope.Identity,
                    envelope.CapturedAtUtc);
                if (!candidates.TryGetValue(key, out var existing) ||
                    candidate.CapturedAtUtc > existing.CapturedAtUtc)
                {
                    candidates[key] = candidate;
                }
            }

            return new SerialPartitionSearchResult(
                candidates.Values
                    .OrderByDescending(static candidate => candidate.CapturedAtUtc)
                    .ToArray(),
                candidates.Count == 0 ? firstReadError : null);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task FlushSerializedAsync(CancellationToken cancellationToken)
    {
        await _resetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resetGate.Release();
        }
    }

    private async Task DebounceAndPersistAsync(
        string key,
        PendingWrite pending,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_writeDebounce, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            await PersistSnapshotAsync(pending.Snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Background persistence failures are surfaced through WriteFailed.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            RaiseWriteFailed(pending.Snapshot.Identity, exception);
        }
        finally
        {
            lock (_pendingLock)
            {
                if (_pending.TryGetValue(key, out var current)
                    && current.Generation == pending.Generation)
                {
                    _pending.Remove(key);
                }
            }

            pending.DelayCancellation.Dispose();
        }
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            PendingWrite[] writes;
            lock (_pendingLock)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                writes = _pending.Values.ToArray();
                _pending.Clear();
                foreach (var pending in writes)
                {
                    pending.DelayCancellation.Cancel();
                }
            }

            await Task.WhenAll(writes.Select(static pending => pending.Worker))
                .ConfigureAwait(false);

            foreach (var pending in writes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PersistSnapshotAsync(pending.Snapshot, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task PersistSnapshotAsync(
        WingStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var envelope = new CacheEnvelope(
            CurrentSchemaVersion,
            _timeProvider.GetUtcNow(),
            snapshot.Identity,
            snapshot.CapturedAtUtc,
            snapshot.Values);
        var content = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            _serializerOptions);
        if (content.LongLength > _maximumFileSizeBytes)
        {
            throw new InvalidDataException(
                $"The state cache is {content.LongLength} bytes; the limit is {_maximumFileSizeBytes} bytes.");
        }

        var primaryPath = GetPrimaryPath(snapshot.Identity);
        var backupPath = string.Concat(primaryPath, ".bak");

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.WriteAsync(
                    primaryPath,
                    backupPath,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task<CacheFileAttempt> TryLoadFileAsync(
        string path,
        WingCacheIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new CacheFileAttempt(CacheFileStatus.Missing);
        }

        byte[] content;
        try
        {
            content = await AtomicJsonFile.ReadBoundedAsync(
                    path,
                    _maximumFileSizeBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            return new CacheFileAttempt(CacheFileStatus.Corrupt, Message: exception.Message);
        }
        catch (IOException exception)
        {
            return new CacheFileAttempt(CacheFileStatus.Failed, Message: exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new CacheFileAttempt(CacheFileStatus.Failed, Message: exception.Message);
        }

        CacheEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<CacheEnvelope>(
                content,
                _serializerOptions);
        }
        catch (JsonException exception)
        {
            return new CacheFileAttempt(CacheFileStatus.Corrupt, Message: exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return new CacheFileAttempt(CacheFileStatus.Corrupt, Message: exception.Message);
        }

        if (envelope?.Identity is null
            || envelope.Values is null
            || envelope.SchemaVersion <= 0)
        {
            return new CacheFileAttempt(
                CacheFileStatus.Corrupt,
                Message: "The state-cache envelope is incomplete.");
        }

        if (envelope.SchemaVersion != CurrentSchemaVersion)
        {
            return new CacheFileAttempt(
                CacheFileStatus.UnsupportedSchema,
                Message:
                $"Cache schema {envelope.SchemaVersion} is not supported by schema {CurrentSchemaVersion}.");
        }

        if (!IdentityEquals(envelope.Identity, expectedIdentity))
        {
            return new CacheFileAttempt(
                CacheFileStatus.IdentityMismatch,
                Message: "The persisted console identity does not match the requested cache partition.");
        }

        WingStateSnapshot snapshot;
        try
        {
            snapshot = new WingStateSnapshot(
                envelope.Identity,
                envelope.CapturedAtUtc,
                envelope.Values);
            ValidateSnapshot(snapshot);
        }
        catch (ArgumentException exception)
        {
            return new CacheFileAttempt(CacheFileStatus.Corrupt, Message: exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return new CacheFileAttempt(CacheFileStatus.Corrupt, Message: exception.Message);
        }

        return new CacheFileAttempt(
            CacheFileStatus.Loaded,
            envelope with { Values = snapshot.Values });
    }

    private WingStateCacheLoadResult CreateSuccessfulLoadResult(
        WingStateCacheLoadStatus status,
        CacheEnvelope envelope,
        long? activeConnectionEpoch)
    {
        var snapshot = new WingStateSnapshot(
            envelope.Identity,
            envelope.CapturedAtUtc,
            envelope.Values);
        var freshness = EvaluateFreshness(snapshot, activeConnectionEpoch);
        return new WingStateCacheLoadResult(status, freshness, snapshot);
    }

    private CacheFreshness EvaluateFreshness(
        WingStateSnapshot snapshot,
        long? activeConnectionEpoch)
    {
        if (snapshot.Values.Count == 0)
        {
            return CacheFreshness.Unknown;
        }

        if (activeConnectionEpoch is null)
        {
            return CacheFreshness.Stale;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var value in snapshot.Values)
        {
            if (value.Metadata.Freshness != CacheFreshness.Fresh
                || value.Metadata.ConnectionEpoch != activeConnectionEpoch.Value
                || value.Metadata.ObservedAt > now
                || now - value.Metadata.ObservedAt > _maximumFreshAge)
            {
                return CacheFreshness.Stale;
            }
        }

        return CacheFreshness.Fresh;
    }

    private string GetPrimaryPath(WingCacheIdentity identity)
    {
        var key = GetPartitionKey(identity);
        return Path.Combine(_directoryPath, $"{key}.wingstate.json");
    }

    private static string GetPartitionKey(WingCacheIdentity identity)
    {
        var normalized = string.Concat(
            identity.ConsoleSerial.Trim().ToUpperInvariant(),
            "\0",
            identity.ConsoleModel.Trim().ToUpperInvariant(),
            "\0",
            identity.FirmwareVersion.Trim().ToUpperInvariant());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private static void ValidateSnapshot(WingStateSnapshot snapshot)
    {
        ValidateIdentity(snapshot.Identity);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in snapshot.Values)
        {
            if (value is null)
            {
                throw new InvalidDataException("A state snapshot cannot contain a null value.");
            }

            if (value.Metadata is null)
            {
                throw new InvalidDataException(
                    $"Token '{value.TokenPath}' has no cache metadata.");
            }

            if (string.IsNullOrWhiteSpace(value.TokenPath)
                || value.TokenPath[0] != '/'
                || value.TokenPath.Length > 512
                || value.TokenPath.Contains('\0'))
            {
                throw new InvalidDataException(
                    $"'{value.TokenPath}' is not a valid canonical WING token path.");
            }

            if (!paths.Add(value.TokenPath))
            {
                throw new InvalidDataException(
                    $"The snapshot contains duplicate token path '{value.TokenPath}'.");
            }

            var metadataIdentity = new WingCacheIdentity(
                value.Metadata.ConsoleSerial,
                value.Metadata.ConsoleModel,
                value.Metadata.FirmwareVersion);
            if (!IdentityEquals(snapshot.Identity, metadataIdentity))
            {
                throw new InvalidDataException(
                    $"Token '{value.TokenPath}' belongs to a different console identity.");
            }

            if (value.Metadata.ConnectionEpoch < 0 || value.Metadata.Revision < 0)
            {
                throw new InvalidDataException(
                    $"Token '{value.TokenPath}' contains a negative epoch or revision.");
            }
        }
    }

    private static void ValidateIdentity(WingCacheIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateIdentityPart(identity.ConsoleSerial, nameof(identity.ConsoleSerial), 96);
        ValidateIdentityPart(identity.ConsoleModel, nameof(identity.ConsoleModel), 64);
        ValidateIdentityPart(identity.FirmwareVersion, nameof(identity.FirmwareVersion), 160);
    }

    private static void ValidateIdentityPart(
        string value,
        string name,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"The cache identity field '{name}' is missing or invalid.",
                name);
        }
    }

    private static bool IdentityEquals(
        WingCacheIdentity left,
        WingCacheIdentity right)
    {
        return string.Equals(
                   left.ConsoleSerial.Trim(),
                   right.ConsoleSerial.Trim(),
                   StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   left.ConsoleModel.Trim(),
                   right.ConsoleModel.Trim(),
                   StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   left.FirmwareVersion.Trim(),
                   right.FirmwareVersion.Trim(),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter<CacheFreshness>());
        options.Converters.Add(new WingValueJsonConverter());
        return options;
    }

    private bool TryQuarantine(
        string path,
        string category,
        out string? quarantinedPath,
        out string? error)
    {
        try
        {
            quarantinedPath = AtomicJsonFile.Quarantine(
                path,
                _timeProvider.GetUtcNow(),
                category);
            error = null;
            return true;
        }
        catch (IOException exception)
        {
            quarantinedPath = null;
            error = $"The state cache could not be quarantined: {exception.Message}";
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            quarantinedPath = null;
            error = $"The state cache could not be quarantined: {exception.Message}";
            return false;
        }
    }

    private void RaiseWriteFailed(
        WingCacheIdentity identity,
        Exception exception)
    {
        var handlers = WriteFailed;
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new WingStateCacheWriteFailedEventArgs(identity, exception);
        foreach (EventHandler<WingStateCacheWriteFailedEventArgs> handler
                 in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
#pragma warning disable CA1031 // Cache observers cannot repair persistence and must not fault its worker.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Continue notifying independent observers.
            }
        }
    }

    private sealed class PendingWrite
    {
        public PendingWrite(
            WingStateSnapshot snapshot,
            long generation,
            CancellationTokenSource delayCancellation)
        {
            Snapshot = snapshot;
            Generation = generation;
            DelayCancellation = delayCancellation;
        }

        public WingStateSnapshot Snapshot { get; }

        public long Generation { get; }

        public CancellationTokenSource DelayCancellation { get; }

        public Task Worker { get; set; } = Task.CompletedTask;
    }

    private sealed record CacheEnvelope(
        int SchemaVersion,
        DateTimeOffset SavedAtUtc,
        WingCacheIdentity Identity,
        DateTimeOffset CapturedAtUtc,
        IReadOnlyList<CachedWingValue> Values);

    private enum CacheFileStatus
    {
        Missing,
        Loaded,
        Corrupt,
        IdentityMismatch,
        UnsupportedSchema,
        Failed,
    }

    private sealed record CacheFileAttempt(
        CacheFileStatus Status,
        CacheEnvelope? Envelope = null,
        string? Message = null);

    private sealed record SerialPartitionCandidate(
        WingCacheIdentity Identity,
        DateTimeOffset CapturedAtUtc);

    private sealed record SerialPartitionSearchResult(
        IReadOnlyList<SerialPartitionCandidate> Candidates,
        string? ErrorMessage = null);
}
