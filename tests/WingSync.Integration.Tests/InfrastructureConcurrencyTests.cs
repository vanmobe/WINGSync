using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using WingSync.Core.Abstractions;
using WingSync.Core.Domain;
using WingSync.Infrastructure.Persistence;
using WingSync.Infrastructure.Wapi;

namespace WingSync.Integration.Tests;

internal static class InfrastructureConcurrencyTests
{
    private const string WapiHelperRole = "WINGSYNC-INTEGRATION-GENERATION-HELPER";
    private static readonly DiscoveredWing CacheIdentity = new(
        "10.123.20.10",
        "CACHE-DISPOSE-TEST",
        "wing-fullsize",
        "CACHE-DISPOSE-SERIAL",
        "3.1-test",
        DateTimeOffset.UtcNow);

    public static bool IsWapiGenerationHelper =>
        string.Equals(
            Environment.GetEnvironmentVariable("WINGSYNC_ROLE"),
            WapiHelperRole,
            StringComparison.Ordinal);

    public static async Task StaleWapiReaderIsSuppressedAsync()
    {
        var executablePath = Path.ChangeExtension(
            typeof(InfrastructureConcurrencyTests).Assembly.Location,
            OperatingSystem.IsWindows() ? ".exe" : null);
        AssertEx.True(
            File.Exists(executablePath),
            $"The integration-test apphost is missing at {executablePath}.");

        var observer = new RecordingObserver();
        await using var session = new WapiProcessSession(
            WapiHelperRole,
            executablePath,
            observer);
        var firstCallbackEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCallback = new ManualResetEventSlim(initialState: false);
        var staleMutations = new ConcurrentQueue<string>();
        session.ParameterChanged += (_, parameter) =>
        {
            if (parameter.TokenPath == "/test/old/first")
            {
                firstCallbackEntered.TrySetResult(true);
                releaseFirstCallback.Wait(TimeSpan.FromSeconds(20));
                return;
            }

            if (parameter.TokenPath == "/test/old/second")
            {
                staleMutations.Enqueue(parameter.TokenPath);
            }
        };
        session.StateChanged += (_, change) =>
        {
            if (change.Reason == "stale helper state")
            {
                staleMutations.Enqueue(change.Reason);
            }
        };

        var endpoint = new WingEndpoint("127.0.0.1");
        try
        {
            await session.ConnectAsync(endpoint, CancellationToken.None).ConfigureAwait(false);
            await session.SetAsync(
                    "/test/trigger",
                    WingValue.FromString("emit"),
                    CancellationToken.None)
                .ConfigureAwait(false);
            await firstCallbackEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);

            // ConnectAsync must be able to replace the helper after its bounded
            // reader wait even though the first generation remains inside a user
            // callback. Its two already-buffered messages must then be discarded.
            await session.ConnectAsync(endpoint, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None)
                .ConfigureAwait(false);
            AssertEx.Equal(WingSessionState.Connected, session.State);

            releaseFirstCallback.Set();
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

            AssertEx.Equal(WingSessionState.Connected, session.State);
            AssertEx.Equal(
                0,
                staleMutations.Count,
                "An output reader from the stopped helper mutated the replacement generation.");
        }
        finally
        {
            releaseFirstCallback.Set();
        }
    }

    public static async Task CanceledSnapshotRecyclesHelperAsync()
    {
        var executablePath = Path.ChangeExtension(
            typeof(InfrastructureConcurrencyTests).Assembly.Location,
            OperatingSystem.IsWindows() ? ".exe" : null);
        AssertEx.True(File.Exists(executablePath));

        await using var session = new WapiProcessSession(
            WapiHelperRole,
            executablePath,
            new RecordingObserver());
        var endpoint = new WingEndpoint("127.0.0.1");
        await session.ConnectAsync(endpoint, CancellationToken.None).ConfigureAwait(false);

        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
                    session.SnapshotAsync("/test/hang", cancellation.Token))
                .ConfigureAwait(false);
        }

        AssertEx.Equal(
            WingSessionState.Faulted,
            session.State,
            "A canceled post-dispatch snapshot left its native helper generation reusable.");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await session.ConnectAsync(endpoint, CancellationToken.None).ConfigureAwait(false);
        var fresh = await session.SnapshotAsync("/test/fast", CancellationToken.None)
            .ConfigureAwait(false);
        stopwatch.Stop();
        AssertEx.Equal(1, fresh.Count);
        AssertEx.Equal("/test/fast", fresh[0].TokenPath);
        AssertEx.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(4),
            "An orphaned snapshot from the killed helper delayed the replacement generation.");
    }

    public static async Task CanceledSnapshotNeverDispatchesQueuedWriteAsync()
    {
        var executablePath = Path.ChangeExtension(
            typeof(InfrastructureConcurrencyTests).Assembly.Location,
            OperatingSystem.IsWindows() ? ".exe" : null);
        AssertEx.True(File.Exists(executablePath));

        using var temporary = new TemporaryDirectory();
        var markerPath = Path.Combine(temporary.Path, "setmany-executed.marker");
        const string markerVariable = "WINGSYNC_TEST_WRITE_MARKER";
        var previousMarker = Environment.GetEnvironmentVariable(markerVariable);
        Environment.SetEnvironmentVariable(markerVariable, markerPath);
        try
        {
            await using var session = new WapiProcessSession(
                WapiHelperRole,
                executablePath,
                new RecordingObserver());
            var endpoint = new WingEndpoint("127.0.0.1");
            await session.ConnectAsync(endpoint, CancellationToken.None).ConfigureAwait(false);

            using var cancellation = new CancellationTokenSource();
            var snapshot = session.SnapshotAsync("/test/slow", cancellation.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            var queuedWrite = session.SetManyAsync(
                [
                    new WingWriteRequest(
                        "/test/write",
                        WingValue.FromInt32(1)),
                ],
                CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);

            cancellation.Cancel();
            await AssertEx.ThrowsAsync<OperationCanceledException>(() => snapshot)
                .ConfigureAwait(false);
            await AssertEx.ThrowsAsync<IOException>(() => queuedWrite).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(700)).ConfigureAwait(false);

            AssertEx.False(
                File.Exists(markerPath),
                "A SETMANY queued behind the canceled snapshot reached the native helper.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(markerVariable, previousMarker);
        }
    }

    public static async Task TimedOutCommandNeverDispatchesQueuedWriteAsync()
    {
        var executablePath = Path.ChangeExtension(
            typeof(InfrastructureConcurrencyTests).Assembly.Location,
            OperatingSystem.IsWindows() ? ".exe" : null);
        AssertEx.True(File.Exists(executablePath));

        using var temporary = new TemporaryDirectory();
        var markerPath = Path.Combine(temporary.Path, "queued-setmany-executed.marker");
        const string markerVariable = "WINGSYNC_TEST_WRITE_MARKER";
        var previousMarker = Environment.GetEnvironmentVariable(markerVariable);
        Environment.SetEnvironmentVariable(markerVariable, markerPath);
        try
        {
            await using var session = new WapiProcessSession(
                WapiHelperRole,
                executablePath,
                new RecordingObserver(),
                commandTimeout: TimeSpan.FromMilliseconds(250));
            await session.ConnectAsync(
                    new WingEndpoint("127.0.0.1"),
                    CancellationToken.None)
                .ConfigureAwait(false);

            var timedOutWrite = session.SetManyAsync(
                [
                    new WingWriteRequest(
                        "/test/slowwrite",
                        WingValue.FromInt32(1)),
                ],
                CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            var queuedWrite = session.SetManyAsync(
                [
                    new WingWriteRequest(
                        "/test/queuedwrite",
                        WingValue.FromInt32(1)),
                ],
                CancellationToken.None);

            await AssertEx.ThrowsAsync<TimeoutException>(() => timedOutWrite)
                .ConfigureAwait(false);
            await AssertEx.ThrowsAsync<IOException>(() => queuedWrite).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

            AssertEx.False(
                File.Exists(markerPath),
                "A SETMANY queued behind a command with no terminal ACK reached the helper.");
            AssertEx.Equal(
                WingSessionState.Faulted,
                session.State,
                "An indeterminate helper command did not fault its complete generation.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(markerVariable, previousMarker);
        }
    }

    public static async Task CacheDisposeDrainsQueuedStoreAsync()
    {
        using var temporary = new TemporaryDirectory();
        var options = new WingStateCacheOptions(temporary.Path)
        {
            WriteDebounce = TimeSpan.FromSeconds(10),
            MaximumFreshAge = TimeSpan.FromMinutes(1),
        };
        var cache = new WingStateCache(options);
        var sink = new WingStateCacheSink(cache);
        var operationGate = (SemaphoreSlim?)typeof(WingStateCacheSink)
            .GetField("operationGate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(sink);
        AssertEx.True(operationGate is not null, "The cache sink operation gate is unavailable.");

        await sink.BeginEpochAsync("FOH", CacheIdentity, 7, CancellationToken.None)
            .ConfigureAwait(false);
        await operationGate!.WaitAsync().ConfigureAwait(false);
        var gateHeld = true;
        Task? storeTask = null;
        Task? disposeTask = null;
        try
        {
            storeTask = sink.StoreAsync(
                "FOH",
                CacheIdentity,
                7,
                [
                    new WingParameter(
                        "/ch/1/eq/g",
                        WingValue.FromFloat(7.5F),
                        DateTimeOffset.UtcNow),
                ],
                CancellationToken.None);
            disposeTask = sink.DisposeAsync().AsTask();

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            AssertEx.False(
                disposeTask.IsCompleted,
                "Dispose bypassed a StoreAsync operation already queued ahead of shutdown.");
        }
        finally
        {
            if (gateHeld)
            {
                operationGate.Release();
                gateHeld = false;
            }
        }

        try
        {
            await storeTask!.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);
            await disposeTask!.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            await sink.DisposeAsync().ConfigureAwait(false);
        }

        await using var verificationCache = new WingStateCache(options);
        var loaded = await verificationCache.LoadAsync(
                new WingCacheIdentity(
                    CacheIdentity.SerialNumber,
                    CacheIdentity.Model,
                    CacheIdentity.FirmwareVersion),
                activeConnectionEpoch: 7,
                CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(WingStateCacheLoadStatus.Loaded, loaded.Status);
        AssertEx.True(loaded.Snapshot is not null);
        var persisted = loaded.Snapshot!.Values.Single(static item =>
            item.TokenPath == "/ch/1/eq/g");
        AssertEx.Equal(WingValue.FromFloat(7.5F), persisted.Value);
    }

    public static async Task<int> RunWapiGenerationHelperAsync()
    {
        await WriteHelperLineAsync("READY").ConfigureAwait(false);
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var parts = line.Split('|');
            if (parts.Length < 2)
            {
                continue;
            }

            switch (parts[0])
            {
                case "CONNECT":
                case "PING":
                case "DISCONNECT":
                    await WriteHelperLineAsync($"OK|{parts[1]}").ConfigureAwait(false);
                    break;

                case "SET":
                    await WriteHelperLineAsync($"OK|{parts[1]}").ConfigureAwait(false);
                    await WriteHelperLineAsync(
                            $"EVENT|test.old.first|S|{Encode("first")}")
                        .ConfigureAwait(false);
                    await WriteHelperLineAsync(
                            $"EVENT|test.old.second|S|{Encode("second")}")
                        .ConfigureAwait(false);
                    await WriteHelperLineAsync(
                            $"STATE|ignored|FAULTED|{Encode("stale helper state")}")
                        .ConfigureAwait(false);
                    break;

                case "SNAPSHOT":
                    if (parts.Length >= 3 &&
                        parts[2].Equals("test.hang", StringComparison.Ordinal))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    }

                    if (parts.Length >= 3 &&
                        parts[2].Equals("test.slow", StringComparison.Ordinal))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                    }

                    if (parts.Length >= 3)
                    {
                        await WriteHelperLineAsync(
                                $"ITEM|{parts[1]}|{parts[2]}|S|{Encode("fresh")}")
                            .ConfigureAwait(false);
                        await WriteHelperLineAsync($"END|{parts[1]}|1").ConfigureAwait(false);
                    }

                    break;

                case "SETMANY":
                    var token = parts.Length >= 3 ? parts[2] : string.Empty;
                    if (token.Equals("test.slowwrite", StringComparison.Ordinal))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }

                    var markerPath = Environment.GetEnvironmentVariable(
                        "WINGSYNC_TEST_WRITE_MARKER");
                    if (!string.IsNullOrWhiteSpace(markerPath) &&
                        (token.Equals("test.write", StringComparison.Ordinal) ||
                         token.Equals("test.queuedwrite", StringComparison.Ordinal)))
                    {
                        await File.WriteAllTextAsync(markerPath, "executed")
                            .ConfigureAwait(false);
                    }

                    await WriteHelperLineAsync($"OK|{parts[1]}").ConfigureAwait(false);
                    break;

                case "QUIT":
                    return 0;

                default:
                    await WriteHelperLineAsync(
                            $"ERROR|{parts[1]}|TEST_UNSUPPORTED|{Encode(parts[0])}")
                        .ConfigureAwait(false);
                    break;
            }
        }

        return 0;
    }

    private static async Task WriteHelperLineAsync(string line)
    {
        await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WingSync-InfraConcurrencyTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
