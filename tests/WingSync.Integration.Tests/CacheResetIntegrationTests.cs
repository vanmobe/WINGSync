using WingSync.Core.Abstractions;
using WingSync.Core.Domain;
using WingSync.Infrastructure.Persistence;

namespace WingSync.Integration.Tests;

internal static class CacheResetIntegrationTests
{
    private static readonly DiscoveredWing Identity = new(
        "10.123.10.10",
        "CACHE-TEST",
        "wing-fullsize",
        "CACHE-RESET-SERIAL",
        "3.1-test",
        DateTimeOffset.UtcNow);

    public static async Task PendingStateIsNotRewrittenAsync()
    {
        using var temporary = new CacheTemporaryDirectory();
        var activeDirectory = temporary.CreateSubdirectoryPath("active");
        var quarantine = temporary.CreateUniqueQuarantinePath();
        var cache = new WingStateCache(
            new WingStateCacheOptions(activeDirectory)
            {
                WriteDebounce = TimeSpan.FromMilliseconds(750),
                MaximumFreshAge = TimeSpan.FromMinutes(1),
            });
        await using var sink = new WingStateCacheSink(cache);

        await sink.BeginEpochAsync("FOH", Identity, 1, CancellationToken.None)
            .ConfigureAwait(false);
        await sink.StoreAsync(
                "FOH",
                Identity,
                1,
                [
                    new WingParameter(
                        "/ch/1/eq/g",
                        WingValue.FromFloat(4F),
                        DateTimeOffset.UtcNow),
                ],
                CancellationToken.None)
            .ConfigureAwait(false);

        // Deterministically cover both debounce layers: the sink still owns an assembly
        // timer while the underlying store already owns a delayed pending write.
        cache.QueueWrite(CreateSnapshot("/ch/1/name", WingValue.FromString("pending"), 1));
        await sink.ResetAsync(quarantine, CancellationToken.None).ConfigureAwait(false);

        await Task.Delay(TimeSpan.FromMilliseconds(950)).ConfigureAwait(false);
        AssertDirectoryHasNoFiles(activeDirectory);
        var load = await sink.LoadForDisplayAsync(Identity, 1, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(WingStateCacheLoadStatus.Missing, load.Status);
        AssertEx.True(load.Snapshot is null);

        await sink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        AssertDirectoryHasNoFiles(activeDirectory);
    }

    public static async Task FilesAreQuarantinedAndPartitionsClearedAsync()
    {
        using var temporary = new CacheTemporaryDirectory();
        var activeDirectory = temporary.CreateSubdirectoryPath("active");
        var firstQuarantine = temporary.CreateUniqueQuarantinePath();
        var secondQuarantine = temporary.CreateUniqueQuarantinePath();
        AssertEx.False(
            firstQuarantine.Equals(secondQuarantine, StringComparison.OrdinalIgnoreCase),
            "Every reset must receive a unique quarantine target.");
        var cache = new WingStateCache(
            new WingStateCacheOptions(activeDirectory)
            {
                WriteDebounce = TimeSpan.Zero,
                MaximumFreshAge = TimeSpan.FromMinutes(1),
            });
        await using var sink = new WingStateCacheSink(cache);

        await sink.BeginEpochAsync("FOH", Identity, 10, CancellationToken.None)
            .ConfigureAwait(false);
        await sink.StoreAsync(
                "FOH",
                Identity,
                10,
                [
                    new WingParameter(
                        "/ch/1/eq/g",
                        WingValue.FromFloat(1F),
                        DateTimeOffset.UtcNow),
                ],
                CancellationToken.None)
            .ConfigureAwait(false);
        await sink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        await sink.StoreAsync(
                "FOH",
                Identity,
                10,
                [
                    new WingParameter(
                        "/ch/1/gate/thr",
                        WingValue.FromFloat(-30F),
                        DateTimeOffset.UtcNow),
                ],
                CancellationToken.None)
            .ConfigureAwait(false);
        await sink.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        var durableBeforeReset = SnapshotFiles(activeDirectory);
        AssertEx.True(durableBeforeReset.Count > 0, "The reset fixture did not create a cache file.");
        await sink.ResetAsync(firstQuarantine, CancellationToken.None).ConfigureAwait(false);

        AssertDirectoryHasNoFiles(activeDirectory);
        AssertRecoverableCopy(durableBeforeReset, firstQuarantine);
        var emptyLoad = await sink.LoadForDisplayAsync(Identity, 10, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(WingStateCacheLoadStatus.Missing, emptyLoad.Status);

        await sink.BeginEpochAsync("FOH", Identity, 11, CancellationToken.None)
            .ConfigureAwait(false);
        await sink.StoreAsync(
                "FOH",
                Identity,
                11,
                [
                    new WingParameter(
                        "/ch/1/dyn/thr",
                        WingValue.FromFloat(-12F),
                        DateTimeOffset.UtcNow),
                ],
                CancellationToken.None)
            .ConfigureAwait(false);
        await sink.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        var nextEpoch = await sink.LoadForDisplayAsync(Identity, 11, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(WingStateCacheLoadStatus.Loaded, nextEpoch.Status);
        AssertEx.True(nextEpoch.Snapshot is not null);
        AssertEx.Equal(1, nextEpoch.Snapshot!.Values.Count);
        AssertEx.Equal("/ch/1/dyn/thr", nextEpoch.Snapshot.Values[0].TokenPath);
        AssertEx.False(
            nextEpoch.Snapshot.Values.Any(static value =>
                value.TokenPath is "/ch/1/eq/g" or "/ch/1/gate/thr"),
            "Pre-reset in-memory partition values leaked into the next epoch.");

        var secondGeneration = SnapshotFiles(activeDirectory);
        await sink.ResetAsync(secondQuarantine, CancellationToken.None).ConfigureAwait(false);
        AssertDirectoryHasNoFiles(activeDirectory);
        AssertRecoverableCopy(secondGeneration, secondQuarantine);
        AssertRecoverableCopy(durableBeforeReset, firstQuarantine);

        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        AssertDirectoryHasNoFiles(activeDirectory);
    }

    public static async Task SerialPinLoadsWithoutDiscoveryAsync()
    {
        using var temporary = new CacheTemporaryDirectory();
        var activeDirectory = temporary.CreateSubdirectoryPath("active");
        var cache = new WingStateCache(
            new WingStateCacheOptions(activeDirectory)
            {
                WriteDebounce = TimeSpan.Zero,
                MaximumFreshAge = TimeSpan.FromMinutes(1),
            });
        await using var sink = new WingStateCacheSink(cache);

        await sink.BeginEpochAsync("FOH", Identity, 42, CancellationToken.None)
            .ConfigureAwait(false);
        await sink.StoreAsync(
                "FOH",
                Identity,
                42,
                [
                    new WingParameter(
                        "/ch/1/eq/g",
                        WingValue.FromFloat(2.5F),
                        DateTimeOffset.UtcNow),
                ],
                CancellationToken.None)
            .ConfigureAwait(false);
        await sink.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        var offline = await sink.LoadForDisplayBySerialAsync(
                Identity.SerialNumber.ToLowerInvariant(),
                CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(WingStateCacheLoadStatus.Loaded, offline.Status);
        AssertEx.Equal(CacheFreshness.Stale, offline.Freshness);
        AssertEx.False(offline.CanAutomaticallyReplay);
        AssertEx.True(offline.Snapshot is not null);
        AssertEx.Equal(Identity.SerialNumber, offline.Snapshot!.Identity.ConsoleSerial);
        AssertEx.Equal("/ch/1/eq/g", offline.Snapshot.Values.Single().TokenPath);

        var missing = await sink.LoadForDisplayBySerialAsync(
                "A-DIFFERENT-SERIAL",
                CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(WingStateCacheLoadStatus.Missing, missing.Status);
        AssertEx.True(missing.Snapshot is null);
    }

    private static WingStateSnapshot CreateSnapshot(
        string tokenPath,
        WingValue value,
        long epoch)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var identity = new WingCacheIdentity(
            Identity.SerialNumber,
            Identity.Model,
            Identity.FirmwareVersion);
        return new WingStateSnapshot(
            identity,
            observedAt,
            [
                new CachedWingValue(
                    tokenPath,
                    value,
                    new CacheMetadata(
                        Identity.SerialNumber,
                        Identity.Model,
                        Identity.FirmwareVersion,
                        WapiVersion: 0,
                        observedAt,
                        epoch,
                        Revision: 1,
                        CacheFreshness.Fresh)),
            ]);
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }

        return Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .ToDictionary(
                static path => Path.GetFileName(path)!,
                File.ReadAllBytes,
                StringComparer.Ordinal);
    }

    private static void AssertRecoverableCopy(
        Dictionary<string, byte[]> expected,
        string quarantine)
    {
        AssertEx.True(Directory.Exists(quarantine), "The quarantine directory was not created.");
        var actual = SnapshotFiles(quarantine);
        AssertEx.Equal(expected.Count, actual.Count);
        foreach (var expectedFile in expected)
        {
            AssertEx.True(
                actual.TryGetValue(expectedFile.Key, out var actualBytes),
                $"Quarantine is missing recoverable file {expectedFile.Key}.");
            AssertEx.True(
                expectedFile.Value.AsSpan().SequenceEqual(actualBytes),
                $"Quarantined file {expectedFile.Key} did not retain its exact bytes.");
        }
    }

    private static void AssertDirectoryHasNoFiles(string directory)
    {
        AssertEx.True(Directory.Exists(directory), "The active cache directory must remain present.");
        AssertEx.False(
            Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Any(),
            "The active cache directory is not empty after reset.");
    }

    private sealed class CacheTemporaryDirectory : IDisposable
    {
        public CacheTemporaryDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "WingSync-CacheResetTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        private string RootPath { get; }

        public string CreateSubdirectoryPath(string name) =>
            Path.Combine(RootPath, name);

        public string CreateUniqueQuarantinePath() =>
            Path.Combine(RootPath, "quarantine-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
