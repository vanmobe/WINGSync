using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WingSync.Core.Abstractions;
using WingSync.Core.Domain;
using WingSync.Core.Planning;
using WingSync.Core.Services;
using WingSync.Infrastructure.Discovery;
using WingSync.Infrastructure.Wapi;

namespace WingSync.Integration.Tests;

internal sealed record LiveHardwareOptions(
    bool LiveReadOnly,
    bool LiveWrites,
    bool UnderstandsLiveWrites,
    string FohSerial,
    string StageSerial)
{
    public static LiveHardwareOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var readOnly = false;
        var writes = false;
        var acknowledgement = false;
        string? fohSerial = null;
        string? stageSerial = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!seen.Add(argument))
            {
                throw new LiveWriteSafetyException($"Duplicate option '{argument}' is not allowed.");
            }

            switch (argument)
            {
                case "--live-read-only":
                    readOnly = true;
                    break;
                case "--live-writes":
                    writes = true;
                    break;
                case "--i-understand-live-writes":
                    acknowledgement = true;
                    break;
                case "--foh-serial":
                    fohSerial = ReadValue(arguments, ref index, argument);
                    break;
                case "--stage-serial":
                    stageSerial = ReadValue(arguments, ref index, argument);
                    break;
                default:
                    throw new LiveWriteSafetyException($"Unknown live-test option '{argument}'.");
            }
        }

        if (readOnly == writes)
        {
            throw new LiveWriteSafetyException(
                "Choose exactly one mode: --live-read-only or --live-writes.");
        }

        if (readOnly && acknowledgement)
        {
            throw new LiveWriteSafetyException(
                "Read-only mode does not accept live-write confirmation.");
        }

        if (string.IsNullOrWhiteSpace(fohSerial) || string.IsNullOrWhiteSpace(stageSerial))
        {
            throw new LiveWriteSafetyException(
                "Live hardware tests require --foh-serial and --stage-serial; " +
                "identities are pinned only at runtime.");
        }

        if (string.Equals(fohSerial, stageSerial, StringComparison.Ordinal))
        {
            throw new LiveWriteSafetyException(
                "FOH and stage must have different serial numbers.");
        }

        if (writes && !acknowledgement)
        {
            throw new LiveWriteSafetyException(
                "--live-writes additionally requires exactly --i-understand-live-writes.");
        }

        return new LiveHardwareOptions(readOnly, writes, acknowledgement, fohSerial, stageSerial);
    }

    private static string ReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        if (++index >= arguments.Count ||
            string.IsNullOrWhiteSpace(arguments[index]) ||
            arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new LiveWriteSafetyException($"Optie '{option}' mist een waarde.");
        }

        return arguments[index];
    }
}

internal sealed class LiveWriteSafetyException : InvalidOperationException
{
    public LiveWriteSafetyException(string message)
        : base(message)
    {
    }
}

internal static class LiveHardwareHarness
{
    public static async Task RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var options = LiveHardwareOptions.Parse(arguments);
        var pins = new HardwarePins(options.FohSerial, options.StageSerial);
        var helperPath = LocateNativeHelper();
        if (options.LiveReadOnly)
        {
            await RunReadOnlyAsync(helperPath, pins, cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunLiveWritesAsync(helperPath, pins, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunReadOnlyAsync(
        string helperPath,
        HardwarePins pins,
        CancellationToken cancellationToken)
    {
        var identities = await DiscoverExactPairAsync(pins, cancellationToken).ConfigureAwait(false);
        var observer = new RecordingObserver();
        await using var fohSession = new WapiProcessSession("FOH live read-only", helperPath, observer);
        await using var stageSession = new WapiProcessSession("Stage live read-only", helperPath, observer);
        var cleanDisconnect = false;
        try
        {
            await Task.WhenAll(
                    fohSession.ConnectAsync(identities.Foh.Endpoint, cancellationToken),
                    stageSession.ConnectAsync(identities.Stage.Endpoint, cancellationToken))
                .ConfigureAwait(false);

            await AssertConnectedSerialAsync(
                    fohSession,
                    pins.FohSerial,
                    "FOH",
                    cancellationToken)
                .ConfigureAwait(false);
            await AssertConnectedSerialAsync(
                    stageSession,
                    pins.StageSerial,
                    "Stage",
                    cancellationToken)
                .ConfigureAwait(false);
            await ReadRequiredNodesAsync(fohSession, "FOH", cancellationToken).ConfigureAwait(false);
            await ReadRequiredNodesAsync(stageSession, "Stage", cancellationToken).ConfigureAwait(false);

            var keepaliveStopwatch = Stopwatch.StartNew();
            var pingCount = 0;
            while (keepaliveStopwatch.Elapsed < TimeSpan.FromSeconds(12))
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(
                        fohSession.PingAsync(cancellationToken),
                        stageSession.PingAsync(cancellationToken))
                    .ConfigureAwait(false);
                pingCount++;
            }

            if (keepaliveStopwatch.Elapsed < TimeSpan.FromSeconds(12) || pingCount < 4)
            {
                throw new InvalidOperationException("De keepaliveproef duurde minder dan twaalf seconden.");
            }

            await RequireSnapshotAsync(fohSession, "$STAT", 1, cancellationToken).ConfigureAwait(false);
            await RequireSnapshotAsync(stageSession, "$STAT", 1, cancellationToken).ConfigureAwait(false);
            await DisconnectPairAsync(fohSession, stageSession, cancellationToken).ConfigureAwait(false);
            cleanDisconnect =
                fohSession.State == WingSessionState.Disconnected &&
                stageSession.State == WingSessionState.Disconnected;
        }
        finally
        {
            if (!cleanDisconnect)
            {
                await BestEffortDisconnectAsync(fohSession, stageSession).ConfigureAwait(false);
            }
        }

        if (!cleanDisconnect)
        {
            throw new IOException("The two read-only helpers were not cleanly disconnected.");
        }

        Console.WriteLine(
            "PASS  LIVE READ-ONLY: exact discovery, two helpers, $SYSCFG/$STAT/CH40, " +
            "keepalive >=12s en clean disconnect.");
    }

    private static async Task AssertConnectedSerialAsync(
        IWingSession session,
        string expectedSerial,
        string role,
        CancellationToken cancellationToken)
    {
        const string serialToken = "/$syscfg/$serial";
        var snapshot = await session.SnapshotAsync(serialToken, cancellationToken).ConfigureAwait(false);
        var exact = snapshot
            .Where(parameter =>
                parameter.TokenPath.Equals(serialToken, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exact.Length != 1 ||
            exact[0].Value.Type != WingValueType.S ||
            !exact[0].Value.AsString().Trim().Equals(
                expectedSerial,
                StringComparison.Ordinal))
        {
            throw new LiveWriteSafetyException(
                $"{role}: connected WAPI $SYSCFG serial number differs from the hardware pin.");
        }

        Console.WriteLine($"{role}: connected WAPI identity rebound to the serial pin.");
    }

    private static async Task RunLiveWritesAsync(
        string helperPath,
        HardwarePins pins,
        CancellationToken cancellationToken)
    {
        var identities = await DiscoverExactPairAsync(pins, cancellationToken).ConfigureAwait(false);
        var testChannel = await FindProvablySilentChannelAsync(
                helperPath,
                identities,
                cancellationToken)
            .ConfigureAwait(false);
        var observer = new RecordingObserver();
        var allowedTokens = new HashSet<string>(StringComparer.Ordinal);
        var rawFoh = new WapiProcessSession("FOH live guarded", helperPath, observer);
        var rawStage = new WapiProcessSession("Stage live guarded", helperPath, observer);
        var fohSession = new GuardedLiveSession(rawFoh, allowedTokens, testChannel);
        var stageSession = new GuardedLiveSession(rawStage, allowedTokens, testChannel);
        var observedFohEvents = new ConcurrentQueue<WingParameter>();
        var observedStageEvents = new ConcurrentQueue<WingParameter>();
        fohSession.ParameterChanged += (_, parameter) => observedFohEvents.Enqueue(parameter);
        stageSession.ParameterChanged += (_, parameter) => observedStageEvents.Enqueue(parameter);
        var exactVerifier = new ExactLiveIdentityVerifier(pins);
        var coordinator = new SyncCoordinator(
            new ScriptedSessionFactory(fohSession, stageSession),
            exactVerifier,
            new RecordingStateSink(),
            observer);
        RecoveryJournal? journal = null;
        var recoveryEntries = new List<RecoveryEntry>();
        Exception? testFailure = null;
        Exception? recoveryFailure = null;

        try
        {
            var configuration = new AppConfiguration(
                identities.Foh.Endpoint,
                identities.Stage.Endpoint,
                SyncDirection.FohToMonitor,
                InitialSync.PreviewOnly,
                new SafetySettings(
                    dryRun: false,
                    requireReadback: true,
                    allowHighRiskWrites: false,
                    stopOnVerificationFailure: true),
                [SyncScope.Cust, SyncScope.Eq, SyncScope.Gate, SyncScope.Dyn],
                new ChannelMapping([new InputChannelMapping(testChannel, testChannel)]));

            await coordinator.StartAsync(configuration, true, cancellationToken).ConfigureAwait(false);
            if (coordinator.Status.State != SyncCoordinatorState.RunningLive)
            {
                throw new LiveWriteSafetyException(
                    $"Coordinator did not start live safely: {coordinator.Status.State}.");
            }

            var fohSnapshot = await RequireStableSnapshotAsync(
                    fohSession,
                    $"/ch/{testChannel}",
                    80,
                    "FOH",
                    cancellationToken)
                .ConfigureAwait(false);
            var stageSnapshot = await RequireStableSnapshotAsync(
                    stageSession,
                    $"/ch/{testChannel}",
                    80,
                    "Stage",
                    cancellationToken)
                .ConfigureAwait(false);
            var mutationPlan = Channel40Preflight.ValidateAndPlan(
                fohSnapshot,
                stageSnapshot,
                testChannel);
            foreach (var mutation in mutationPlan)
            {
                allowedTokens.Add(mutation.TokenPath);
            }

            var stimulusIdentities = await DiscoverExactPairAsync(pins, cancellationToken)
                .ConfigureAwait(false);
            using var oscStimulus = new GuardedOscStimulus(
                stimulusIdentities.Foh.IpAddress,
                allowedTokens,
                testChannel);

            journal = RecoveryJournal.Create();
            await journal.AppendHeaderAsync(
                    identities.Foh.IpAddress,
                    pins.FohSerial,
                    identities.Stage.IpAddress,
                    pins.StageSerial,
                    mutationPlan.Select(static item => item.TokenPath).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var mutation in mutationPlan)
            {
                await AssertStillSilentAsync(
                        fohSession,
                        stageSession,
                        testChannel,
                        cancellationToken)
                    .ConfigureAwait(false);
                await AssertConnectedSerialAsync(
                        fohSession,
                        pins.FohSerial,
                        "FOH vlak voor stimulus",
                        cancellationToken)
                    .ConfigureAwait(false);
                var sourceOriginal = await ReadScalarAsync(
                        fohSession,
                        mutation.TokenPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                var targetOriginal = await ReadScalarAsync(
                        stageSession,
                        mutation.TokenPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (sourceOriginal != mutation.SourceOriginal ||
                    targetOriginal != mutation.TargetOriginal)
                {
                    throw new LiveWriteSafetyException(
                        $"Token {mutation.TokenPath} changed after preflight; writes are skipped.");
                }

                var recovery = new RecoveryEntry(
                    mutation.TokenPath,
                    sourceOriginal,
                    targetOriginal,
                    mutation.TestValue);
                await journal.AppendBeforeWriteAsync(recovery, cancellationToken).ConfigureAwait(false);
                recoveryEntries.Add(recovery);

                // OSC is used only as the independent test stimulus. The application
                // itself remains WAPI-only. An OSC mutation enters through the console's
                // remote-control server and must surface as an unsolicited WAPI event,
                // matching a physical surface or Co-Pilot edit.
                await oscStimulus.SetAsync(
                        mutation.TokenPath,
                        mutation.TestValue,
                        cancellationToken)
                    .ConfigureAwait(false);
                await WaitForStableExactScalarAsync(
                        fohSession,
                        mutation.TokenPath,
                        mutation.TestValue,
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromMilliseconds(500),
                        cancellationToken)
                    .ConfigureAwait(false);
                await WaitForStableExactScalarAsync(
                        stageSession,
                        mutation.TokenPath,
                        mutation.TestValue,
                        TimeSpan.FromSeconds(8),
                        TimeSpan.FromMilliseconds(500),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            Console.WriteLine(
                $"LIVE WRITE test synchronized {mutationPlan.Count} allowed " +
                $"CH{testChannel}-tokens; " +
                "exact restore is required in finally.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Live coordinator status on error: {coordinator.Status.State} - " +
                coordinator.Status.Detail);
            foreach (var diagnostic in observer.Diagnostics.TakeLast(12))
            {
                Console.Error.WriteLine(
                    $"DIAG {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
            }

            foreach (var action in observer.Actions.TakeLast(12))
            {
                Console.Error.WriteLine(
                    $"ACTIE {action.Action} {action.SourceToken} -> {action.TargetToken} " +
                    $"({action.Scope}, dryRun={action.DryRun})");
            }
            foreach (var parameter in observedFohEvents.TakeLast(12))
            {
                Console.Error.WriteLine(
                    $"FOH EVENT {parameter.TokenPath} ({parameter.Value.Type})");
            }

            foreach (var parameter in observedStageEvents.TakeLast(12))
            {
                Console.Error.WriteLine(
                    $"STAGE EVENT {parameter.TokenPath} ({parameter.Value.Type})");
            }

            testFailure = exception;
        }
        finally
        {
            try
            {
                await coordinator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                testFailure ??= exception;
            }

            if (recoveryEntries.Count > 0)
            {
                try
                {
                    using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    await RestoreExactlyAsync(
                            helperPath,
                            pins,
                            allowedTokens,
                            recoveryEntries,
                            testChannel,
                            journal!,
                            recoveryTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    recoveryFailure = exception;
                }
            }
        }

        if (recoveryFailure is not null)
        {
            throw new AggregateException(
                "LIVE TEST RECOVERY FAILED. Use the recovery journal before the consoles " +
                $"are used for a show. Journal: {journal?.FilePath ?? "(not created)"}",
                testFailure is null ? [recoveryFailure] : [testFailure, recoveryFailure]);
        }

        if (testFailure is not null)
        {
            throw testFailure;
        }

        Console.WriteLine(
            $"PASS  LIVE WRITES: only CUST and disabled EQ/GATE/DYN scalars; " +
            $"both consoles restored exactly. Journal: {journal!.FilePath}");
    }

    private static async Task<int> FindProvablySilentChannelAsync(
        string helperPath,
        ExpectedPair identities,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            "Read-only safety selection: input channels 40..1 are checked on both consoles " +
            "during this step writes are impossible.");
        var observer = new RecordingObserver();
        await using var foh = new WapiProcessSession("FOH safety scan", helperPath, observer);
        await using var stage = new WapiProcessSession("Stage safety scan", helperPath, observer);
        var rejected = new List<string>();
        var selectedChannel = 0;
        var cleanDisconnect = false;
        try
        {
            await Task.WhenAll(
                    foh.ConnectAsync(identities.Foh.Endpoint, cancellationToken),
                    stage.ConnectAsync(identities.Stage.Endpoint, cancellationToken))
                .ConfigureAwait(false);
            await Task.WhenAll(
                    AssertConnectedSerialAsync(
                        foh,
                        identities.Foh.SerialNumber,
                        "FOH safety scan",
                        cancellationToken),
                    AssertConnectedSerialAsync(
                        stage,
                        identities.Stage.SerialNumber,
                        "Stage safety scan",
                        cancellationToken))
                .ConfigureAwait(false);

            for (var channel = 40; channel >= 1; channel--)
            {
                var node = $"/ch/{channel}";
                var fohTask = RequireSnapshotAsync(foh, node, 80, cancellationToken);
                var stageTask = RequireSnapshotAsync(stage, node, 80, cancellationToken);
                await Task.WhenAll(fohTask, stageTask).ConfigureAwait(false);
                try
                {
                    _ = Channel40Preflight.ValidateAndPlan(
                        await fohTask.ConfigureAwait(false),
                        await stageTask.ConfigureAwait(false),
                        channel);
                    selectedChannel = channel;
                    break;
                }
                catch (LiveWriteSafetyException exception)
                {
                    rejected.Add($"CH{channel}: {exception.Message}");
                }
            }

            await DisconnectPairAsync(foh, stage, cancellationToken).ConfigureAwait(false);
            cleanDisconnect = true;
        }
        finally
        {
            if (!cleanDisconnect)
            {
                await BestEffortDisconnectAsync(foh, stage).ConfigureAwait(false);
            }
        }

        if (selectedChannel == 0)
        {
            var examples = string.Join(" | ", rejected.Take(2));
            throw new LiveWriteSafetyException(
                "No input channel was demonstrably silent on both consoles and suitable for the " +
                $"CUST/EQ/GATE/DYN test. No writes were executed. First rejections: {examples}");
        }

        Console.WriteLine(
            $"Safety selection chose CH{selectedChannel}; all silence conditions apply on " +
            "both consoles. A fresh preflight follows before every write.");
        return selectedChannel;
    }

    private static async Task AssertStillSilentAsync(
        IWingSession fohSession,
        IWingSession stageSession,
        int channel,
        CancellationToken cancellationToken)
    {
        var fohSnapshot = await RequireStableSnapshotAsync(
                fohSession,
                $"/ch/{channel}",
                80,
                "FOH",
                cancellationToken)
            .ConfigureAwait(false);
        var stageSnapshot = await RequireStableSnapshotAsync(
                stageSession,
                $"/ch/{channel}",
                80,
                "Stage",
                cancellationToken)
            .ConfigureAwait(false);
        Channel40Preflight.AssertSilent(fohSnapshot, "FOH", channel);
        Channel40Preflight.AssertSilent(stageSnapshot, "Stage", channel);
    }

    private static async Task RestoreExactlyAsync(
        string helperPath,
        HardwarePins pins,
        ISet<string> allowedTokens,
        IReadOnlyList<RecoveryEntry> entries,
        int channel,
        RecoveryJournal journal,
        CancellationToken cancellationToken)
    {
        var identities = await DiscoverExactPairAsync(pins, cancellationToken).ConfigureAwait(false);
        var observer = new RecordingObserver();
        await using var rawFoh = new WapiProcessSession("FOH recovery", helperPath, observer);
        await using var rawStage = new WapiProcessSession("Stage recovery", helperPath, observer);
        await using var foh = new GuardedLiveSession(rawFoh, allowedTokens, channel);
        await using var stage = new GuardedLiveSession(rawStage, allowedTokens, channel);
        var disconnected = false;
        try
        {
            await Task.WhenAll(
                    foh.ConnectAsync(identities.Foh.Endpoint, cancellationToken),
                    stage.ConnectAsync(identities.Stage.Endpoint, cancellationToken))
                .ConfigureAwait(false);
            await Task.WhenAll(
                    AssertConnectedSerialAsync(
                        foh,
                        pins.FohSerial,
                        "FOH recovery",
                        cancellationToken),
                    AssertConnectedSerialAsync(
                        stage,
                        pins.StageSerial,
                        "Stage recovery",
                        cancellationToken))
                .ConfigureAwait(false);

            foreach (var entry in entries.Reverse())
            {
                // Recovery is still a live write. Rebind both connected sessions and
                // re-prove the complete silence envelope immediately before every
                // mutation; otherwise leave the journal incomplete for manual recovery.
                await Task.WhenAll(
                        AssertConnectedSerialAsync(
                            foh,
                            pins.FohSerial,
                            "FOH recovery vlak voor write",
                            cancellationToken),
                        AssertConnectedSerialAsync(
                            stage,
                            pins.StageSerial,
                            "Stage recovery before write",
                            cancellationToken))
                    .ConfigureAwait(false);
                await AssertStillSilentAsync(
                        foh,
                        stage,
                        channel,
                        cancellationToken)
                    .ConfigureAwait(false);
                await SetAndVerifyExactAsync(
                        foh,
                        entry.TokenPath,
                        entry.SourceOriginal,
                        cancellationToken)
                    .ConfigureAwait(false);
                await SetAndVerifyExactAsync(
                        stage,
                        entry.TokenPath,
                        entry.TargetOriginal,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await DisconnectPairAsync(foh, stage, cancellationToken).ConfigureAwait(false);
            await journal.AppendRecoveryCompleteAsync(cancellationToken).ConfigureAwait(false);
            disconnected = true;
        }
        finally
        {
            if (!disconnected)
            {
                await BestEffortDisconnectAsync(foh, stage).ConfigureAwait(false);
            }
        }
    }

    private static async Task SetAndVerifyExactAsync(
        GuardedLiveSession session,
        string tokenPath,
        WingValue expected,
        CancellationToken cancellationToken)
    {
        var current = await ReadScalarAsync(session, tokenPath, cancellationToken).ConfigureAwait(false);
        if (current != expected)
        {
            await session.SetAsync(tokenPath, expected, cancellationToken).ConfigureAwait(false);
        }

        await WaitForStableExactScalarAsync(
                session,
                tokenPath,
                expected,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(500),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WaitForStableExactScalarAsync(
        IWingSession session,
        string tokenPath,
        WingValue expected,
        TimeSpan timeout,
        TimeSpan stableFor,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        DateTimeOffset? stableSince = null;
        WingValue? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await ReadScalarAsync(session, tokenPath, cancellationToken).ConfigureAwait(false);
            if (WingValueComparer.AreEquivalent(expected, last.Value))
            {
                stableSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - stableSince.Value >= stableFor)
                {
                    return;
                }
            }
            else
            {
                stableSince = null;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new WriteVerificationException(
            tokenPath,
            expected,
            last,
            $"Live readback for {tokenPath} did not remain at least " +
            $"{stableFor.TotalMilliseconds:F0} ms exact stabiel.");
    }

    private static async Task<WingValue> ReadScalarAsync(
        IWingSession session,
        string tokenPath,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.SnapshotAsync(tokenPath, cancellationToken).ConfigureAwait(false);
        var exact = snapshot
            .Where(parameter => parameter.TokenPath.Equals(tokenPath, StringComparison.Ordinal))
            .ToArray();
        if (exact.Length != 1)
        {
            throw new LiveWriteSafetyException(
                $"Expected exactly one fresh scalar for {tokenPath}, received {exact.Length}.");
        }

        return exact[0].Value;
    }

    private static async Task ReadRequiredNodesAsync(
        IWingSession session,
        string role,
        CancellationToken cancellationToken)
    {
        var syscfg = await RequireSnapshotAsync(session, "$SYSCFG", 1, cancellationToken)
            .ConfigureAwait(false);
        var status = await RequireSnapshotAsync(session, "$STAT", 1, cancellationToken)
            .ConfigureAwait(false);
        var channel40 = await RequireSnapshotAsync(session, "/ch/40", 80, cancellationToken)
            .ConfigureAwait(false);
        if (syscfg.Any(static item => !item.TokenPath.StartsWith("/$syscfg", StringComparison.Ordinal)) ||
            status.Any(static item => !item.TokenPath.StartsWith("/$stat", StringComparison.Ordinal)) ||
            channel40.Any(static item => !item.TokenPath.StartsWith("/ch/40/", StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"{role}: a snapshot contained values outside the requested node.");
        }

        Console.WriteLine(
            $"{role}: $SYSCFG={syscfg.Count}, $STAT={status.Count}, CH40={channel40.Count} items.");
    }

    private static async Task<IReadOnlyList<WingParameter>> RequireSnapshotAsync(
        IWingSession session,
        string nodeToken,
        int minimumCount,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.SnapshotAsync(nodeToken, cancellationToken).ConfigureAwait(false);
        if (snapshot.Count < minimumCount)
        {
            throw new InvalidDataException(
                $"Snapshot {nodeToken} is incomplete: {snapshot.Count}, minimum {minimumCount} expected.");
        }

        var duplicate = snapshot
            .GroupBy(static parameter => parameter.TokenPath, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() != 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Snapshot {nodeToken} bevat dubbel token {duplicate.Key}.");
        }

        return snapshot;
    }

    private static async Task<IReadOnlyList<WingParameter>> RequireStableSnapshotAsync(
        IWingSession session,
        string nodeToken,
        int minimumCount,
        string role,
        CancellationToken cancellationToken)
    {
        InvalidDataException? lastFailure = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                return await RequireSnapshotAsync(
                        session,
                        nodeToken,
                        minimumCount,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                lastFailure = exception;
                if (attempt < 4)
                {
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidDataException(
            $"{role}: four fresh snapshots of {nodeToken} remained incomplete; writes are blocked.",
            lastFailure);
    }

    private static async Task<ExpectedPair> DiscoverExactPairAsync(
        HardwarePins pins,
        CancellationToken cancellationToken)
    {
        var result = await new WingDiscoveryService()
            .DiscoverAsync(
                new WingDiscoveryOptions
                {
                    ReplyTimeout = TimeSpan.FromSeconds(2),
                    IncludeDirectedBroadcast = true,
                    IncludeGlobalBroadcast = true,
                },
                cancellationToken)
            .ConfigureAwait(false);
        var foh = RequireExactIdentity(result, "FOH", pins.FohSerial);
        var stage = RequireExactIdentity(result, "Stage", pins.StageSerial);
        if (string.Equals(foh.SerialNumber, stage.SerialNumber, StringComparison.OrdinalIgnoreCase))
        {
            throw new LiveWriteSafetyException("FOH and stage resolved to the same serial number.");
        }
        if (string.Equals(foh.IpAddress, stage.IpAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new LiveWriteSafetyException("FOH and stage resolved to the same IP address.");
        }

        Console.WriteLine(
            $"Fresh discovery confirmed: {foh.Name} {foh.IpAddress}/{foh.SerialNumber}; " +
            $"{stage.Name} {stage.IpAddress}/{stage.SerialNumber}.");
        return new ExpectedPair(foh, stage);
    }

    private static DiscoveredWing RequireExactIdentity(
        WingDiscoveryResult result,
        string role,
        string expectedSerial)
    {
        var exactSerialMatches = result.Wings
            .Where(wing => string.Equals(
                wing.SerialNumber,
                expectedSerial,
                StringComparison.Ordinal))
            .ToArray();
        if (exactSerialMatches.Length != 1)
        {
            throw new LiveWriteSafetyException(
                $"{role}: discovery found {exactSerialMatches.Length} consoles with the exact " +
                "specified serial number.");
        }

        var identity = exactSerialMatches[0];
        if (!IPAddress.TryParse(identity.IpAddress, out _))
        {
            throw new LiveWriteSafetyException(
                $"{role}: discovery did not return a usable IP address for the serial pin.");
        }

        return identity;
    }

    private static async Task DisconnectPairAsync(
        IWingSession foh,
        IWingSession stage,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(
                foh.DisconnectAsync(cancellationToken),
                stage.DisconnectAsync(cancellationToken))
            .ConfigureAwait(false);
        if (foh.State != WingSessionState.Disconnected ||
            stage.State != WingSessionState.Disconnected)
        {
            throw new IOException("Not all helpers reported a clean disconnect.");
        }
    }

    private static async Task BestEffortDisconnectAsync(
        IWingSession foh,
        IWingSession stage)
    {
        try
        {
            await foh.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original failure remains authoritative.
        }

        try
        {
            await stage.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original failure remains authoritative.
        }
    }

    private static string LocateNativeHelper()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "WingSync.slnx")))
            {
                continue;
            }

            var candidates = new[]
            {
                Path.Combine(directory.FullName, "build", "native", "Release", "WingSync.WapiHost.exe"),
                Path.Combine(
                    directory.FullName,
                    "build",
                    "native",
                    "WapiHost",
                    "Release",
                    "WingSync.WapiHost.exe"),
                Path.Combine(directory.FullName, "build", "native", "Debug", "WingSync.WapiHost.exe"),
                Path.Combine(
                    directory.FullName,
                    "build",
                    "native",
                    "WapiHost",
                    "Debug",
                    "WingSync.WapiHost.exe"),
            };
            var helper = candidates.FirstOrDefault(File.Exists);
            if (helper is not null)
            {
                return helper;
            }

            throw new FileNotFoundException(
                "WingSync.WapiHost.exe is missing; build the native helper first.",
                candidates[0]);
        }

        throw new FileNotFoundException("Repository root for the native helper was not found.");
    }

    private sealed record HardwarePins(string FohSerial, string StageSerial);

    private sealed record ExpectedPair(DiscoveredWing Foh, DiscoveredWing Stage);

    private sealed class ExactLiveIdentityVerifier : IWingIdentityVerifier
    {
        private readonly HardwarePins pins;

        public ExactLiveIdentityVerifier(HardwarePins pins)
        {
            this.pins = pins;
        }

        public async Task<DiscoveredWing> VerifyAsync(
            WingEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            var pair = await DiscoverExactPairAsync(pins, cancellationToken).ConfigureAwait(false);
            if (endpoint == pair.Foh.Endpoint)
            {
                return pair.Foh;
            }

            if (endpoint == pair.Stage.Endpoint)
            {
                return pair.Stage;
            }

            throw new LiveWriteSafetyException("Coordinator requested a non-approved endpoint.");
        }
    }
}

internal static class Channel40Preflight
{
    private static readonly string[] EqCandidateLeaves = ["g", "gain", "q", "f"];
    private static readonly string[] GateCandidateLeaves =
        ["thr", "threshold", "range", "attack", "hold", "release"];
    private static readonly string[] DynCandidateLeaves =
        ["thr", "threshold", "ratio", "attack", "hold", "release", "gain"];

    public static IReadOnlyList<LiveMutation> ValidateAndPlan(
        IReadOnlyList<WingParameter> fohSnapshot,
        IReadOnlyList<WingParameter> stageSnapshot,
        int channel = 40)
    {
        ValidateChannel(channel);
        AssertSilent(fohSnapshot, "FOH", channel);
        AssertSilent(stageSnapshot, "Stage", channel);
        var foh = ToMap(fohSnapshot, channel);
        var stage = ToMap(stageSnapshot, channel);
        var prefix = $"/ch/{channel}";
        var result = new List<LiveMutation>
        {
            CreateNameMutation(foh, stage, channel),
            CreateScalarMutation($"{prefix}/eq/", EqCandidateLeaves, foh, stage, "EQ", channel),
            CreateScalarMutation($"{prefix}/gate/", GateCandidateLeaves, foh, stage, "GATE", channel),
            CreateScalarMutation($"{prefix}/dyn/", DynCandidateLeaves, foh, stage, "DYN", channel),
        };
        if (result.Select(static item => item.TokenPath).Distinct(StringComparer.Ordinal).Count() !=
            result.Count)
        {
            throw new LiveWriteSafetyException("Preflight koos hetzelfde token meer dan eenmaal.");
        }

        return result;
    }

    public static void AssertSilent(
        IReadOnlyList<WingParameter> snapshot,
        string role,
        int channel = 40)
    {
        ValidateChannel(channel);
        var values = ToMap(snapshot, channel);
        var prefix = $"/ch/{channel}";
        var failures = new List<string>();
        Check(() => RequireOff(values, $"{prefix}/in/conn/grp", role));
        Check(() => RequireOff(values, $"{prefix}/in/conn/altgrp", role));
        Check(() => RequireInaudibleFader(values, $"{prefix}/fdr", role, channel));
        Check(() => RequireDisabled(values, $"{prefix}/eq/on", role));
        Check(() => RequireDisabled(values, $"{prefix}/gate/on", role));
        Check(() => RequireDisabled(values, $"{prefix}/dyn/on", role));

        // WING input channels are assigned to MAIN 1 by default. That assignment cannot
        // create audio while both input connection groups are exactly OFF and the fader is
        // exactly -infinity; those independent blockers are mandatory above and are checked
        // again immediately before every mutation. We still require MAIN 1 to be present in
        // the fresh snapshot and reject every additional main, bus, or matrix route.
        Check(() => _ = Require(values, $"{prefix}/main/1/on", role));
        for (var main = 2; main <= 4; main++)
        {
            Check(() => RequireDisabled(values, $"{prefix}/main/{main}/on", role));
        }

        for (var send = 1; send <= 16; send++)
        {
            Check(() => RequireDisabled(values, $"{prefix}/send/{send}/on", role));
        }

        for (var matrix = 1; matrix <= 8; matrix++)
        {
            var matrixIndex = matrix;
            Check(() => RequireExactlyOneDisabled(
                values,
                role,
                $"{prefix}/send/mx{matrixIndex}/on",
                $"{prefix}/send/mx.{matrixIndex}/on",
                $"{prefix}/send/mx/{matrixIndex}/on"));
        }

        if (failures.Count > 0)
        {
            throw new LiveWriteSafetyException(string.Join(", ", failures));
        }

        void Check(Action check)
        {
            try
            {
                check();
            }
            catch (LiveWriteSafetyException exception)
            {
                failures.Add(exception.Message);
            }
        }
    }

    private static LiveMutation CreateNameMutation(
        Dictionary<string, WingValue> foh,
        Dictionary<string, WingValue> stage,
        int channel)
    {
        var token = $"/ch/{channel}/name";
        var source = Require(foh, token, "FOH");
        var target = Require(stage, token, "Stage");
        if (source.Type != WingValueType.S || target.Type != WingValueType.S)
        {
            throw new LiveWriteSafetyException(
                $"CH{channel} CUST name is not a string on both consoles.");
        }

        var test = source.AsString().Equals("WSYNC-TST", StringComparison.Ordinal)
            ? WingValue.FromString("WSYNC-T2")
            : WingValue.FromString("WSYNC-TST");
        return new LiveMutation(token, source, target, test);
    }

    private static LiveMutation CreateScalarMutation(
        string prefix,
        IReadOnlyList<string> preferredLeaves,
        Dictionary<string, WingValue> foh,
        Dictionary<string, WingValue> stage,
        string scope,
        int channel)
    {
        var candidates = foh
            .Where(pair =>
                pair.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                pair.Value.Type == WingValueType.F &&
                float.IsFinite(pair.Value.AsFloat()) &&
                IsSafeScalarLeaf(Leaf(pair.Key), preferredLeaves) &&
                stage.TryGetValue(pair.Key, out var target) &&
                target.Type == WingValueType.F &&
                float.IsFinite(target.AsFloat()))
            .OrderBy(pair => ScalarLeafRank(Leaf(pair.Key), preferredLeaves))
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            var observed = string.Join(
                ", ",
                foh.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Take(16)
                    .Select(static pair => $"{pair.Key}={pair.Value.Type}:{pair.Value}"));
            throw new LiveWriteSafetyException(
                $"No unambiguous, finite {scope}-float scalar found on CH{channel}. " +
                $"Observed under {prefix}: {observed}");
        }

        var selected = candidates[0];
        var original = selected.Value.AsFloat();
        // EQ controls accept 0.1 display units on the tested firmware, while GATE/DYN
        // thresholds can quantize a 0.1 write back to the previous whole-dB step. The
        // processors are independently proven disabled before every mutation, so a
        // one-unit GATE/DYN stimulus remains conservative and is unambiguous.
        var testStep = scope.Equals("EQ", StringComparison.Ordinal) ? 0.1F : 1F;
        var delta = MathF.Abs(original) >= testStep
            ? -MathF.CopySign(testStep, original)
            : testStep;
        var testValue = original + delta;
        if (!float.IsFinite(testValue) || testValue.Equals(original))
        {
            throw new LiveWriteSafetyException(
                $"Could not derive a conservative test value for {selected.Key}.");
        }

        return new LiveMutation(
            selected.Key,
            selected.Value,
            stage[selected.Key],
            WingValue.FromFloat(testValue));
    }

    private static Dictionary<string, WingValue> ToMap(
        IReadOnlyList<WingParameter> snapshot,
        int channel)
    {
        var result = new Dictionary<string, WingValue>(StringComparer.Ordinal);
        foreach (var parameter in snapshot)
        {
            if (!result.TryAdd(parameter.TokenPath, parameter.Value))
            {
                throw new LiveWriteSafetyException(
                    $"Duplicate CH{channel} token in preflight: {parameter.TokenPath}.");
            }
        }

        return result;
    }

    private static WingValue Require(
        IReadOnlyDictionary<string, WingValue> values,
        string token,
        string role) =>
        values.TryGetValue(token, out var value)
            ? value
            : throw new LiveWriteSafetyException($"{role}: required preflight token {token} is missing.");

    private static void RequireOff(
        IReadOnlyDictionary<string, WingValue> values,
        string token,
        string role)
    {
        var value = Require(values, token, role);
        if (value.Type != WingValueType.S ||
            !value.AsString().Equals("OFF", StringComparison.OrdinalIgnoreCase))
        {
            throw new LiveWriteSafetyException($"{role}: {token} is not exactly OFF.");
        }
    }

    private static void RequireDisabled(
        IReadOnlyDictionary<string, WingValue> values,
        string token,
        string role)
    {
        var value = Require(values, token, role);
        var disabled = value.Type switch
        {
            WingValueType.I => value.AsInt32() == 0,
            WingValueType.F => value.AsFloat() == 0F,
            WingValueType.S =>
                value.AsString().Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
                value.AsString().Equals("FALSE", StringComparison.OrdinalIgnoreCase) ||
                value.AsString().Equals("NO", StringComparison.OrdinalIgnoreCase) ||
                value.AsString().Equals("0", StringComparison.Ordinal),
            _ => false,
        };
        if (!disabled)
        {
            throw new LiveWriteSafetyException($"{role}: {token} is not disabled.");
        }
    }

    private static void RequireExactlyOneDisabled(
        IReadOnlyDictionary<string, WingValue> values,
        string role,
        params string[] candidateTokens)
    {
        var present = candidateTokens.Where(values.ContainsKey).ToArray();
        if (present.Length != 1)
        {
            throw new LiveWriteSafetyException(
                $"{role}: matrix-sendpreflight vond {present.Length} representaties; exact één vereist.");
        }

        RequireDisabled(values, present[0], role);
    }

    private static void RequireInaudibleFader(
        IReadOnlyDictionary<string, WingValue> values,
        string token,
        string role,
        int channel)
    {
        var value = Require(values, token, role);
        var inaudible = value.Type switch
        {
            WingValueType.F =>
                float.IsNegativeInfinity(value.AsFloat()) ||
                value.AsFloat() <= -120F,
            WingValueType.I => value.AsInt32() <= -120,
            WingValueType.S =>
                value.AsString().Equals("-INF", StringComparison.OrdinalIgnoreCase) ||
                value.AsString().Equals("-INFINITY", StringComparison.OrdinalIgnoreCase) ||
                value.AsString().Equals("-OO", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!inaudible)
        {
            throw new LiveWriteSafetyException(
                $"{role}: CH{channel} fader is not demonstrably at -inf.");
        }
    }

    private static void ValidateChannel(int channel)
    {
        if (channel is < 1 or > 40)
        {
            throw new LiveWriteSafetyException(
                $"Invalid input channel {channel}; only CH1..CH40 are allowed.");
        }
    }

    private static string Leaf(string token) =>
        token[(token.LastIndexOf('/') + 1)..];

    private static bool IsSafeScalarLeaf(
        string leaf,
        IReadOnlyList<string> preferredLeaves) =>
        preferredLeaves.Contains(leaf, StringComparer.OrdinalIgnoreCase) ||
        int.TryParse(
            leaf,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var numericLeaf) && numericLeaf > 0;

    private static int ScalarLeafRank(
        string leaf,
        IReadOnlyList<string> preferredLeaves)
    {
        if (int.TryParse(
                leaf,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var numericLeaf))
        {
            return numericLeaf;
        }

        for (var index = 0; index < preferredLeaves.Count; index++)
        {
            if (preferredLeaves[index].Equals(leaf, StringComparison.OrdinalIgnoreCase))
            {
                return 10_000 + index;
            }
        }

        return int.MaxValue;
    }
}

internal sealed record LiveMutation(
    string TokenPath,
    WingValue SourceOriginal,
    WingValue TargetOriginal,
    WingValue TestValue);

internal sealed record RecoveryEntry(
    string TokenPath,
    WingValue SourceOriginal,
    WingValue TargetOriginal,
    WingValue TestValue);

internal sealed class RecoveryJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private RecoveryJournal(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public static RecoveryJournal Create()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        var directory = Path.Combine(
            localData,
            "WingSync",
            "HardwareTestRecovery");
        Directory.CreateDirectory(directory);
        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss.fffZ}-{Guid.NewGuid():N}.jsonl");
        return new RecoveryJournal(Path.Combine(directory, fileName));
    }

    public Task AppendHeaderAsync(
        string fohIp,
        string fohSerial,
        string stageIp,
        string stageSerial,
        IReadOnlyList<string> allowedTokens,
        CancellationToken cancellationToken) =>
        AppendAsync(
            new
            {
                kind = "header",
                createdAtUtc = DateTimeOffset.UtcNow,
                fohIp,
                fohSerial,
                stageIp,
                stageSerial,
                allowedTokens,
                recoveryComplete = false,
            },
            cancellationToken);

    public Task AppendBeforeWriteAsync(
        RecoveryEntry entry,
        CancellationToken cancellationToken) =>
        AppendAsync(
            new
            {
                kind = "before-write",
                recordedAtUtc = DateTimeOffset.UtcNow,
                tokenPath = entry.TokenPath,
                sourceOriginal = SerializeValue(entry.SourceOriginal),
                targetOriginal = SerializeValue(entry.TargetOriginal),
                requestedTestValue = SerializeValue(entry.TestValue),
            },
            cancellationToken);

    public Task AppendRecoveryCompleteAsync(CancellationToken cancellationToken) =>
        AppendAsync(
            new
            {
                kind = "recovery-complete",
                completedAtUtc = DateTimeOffset.UtcNow,
                recoveryComplete = true,
            },
            cancellationToken);

    private async Task AppendAsync(object value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(json);
        await using var stream = new FileStream(
            FilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4_096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static object SerializeValue(WingValue value) =>
        new
        {
            type = value.Type.ToString(),
            value = value.ToString(),
        };

}

internal sealed class GuardedLiveSession : IWingSession
{
    private readonly IWingSession inner;
    private readonly ISet<string> allowedTokens;
    private readonly int allowedChannel;

    public GuardedLiveSession(
        IWingSession inner,
        ISet<string> allowedTokens,
        int allowedChannel = 40)
    {
        if (allowedChannel is < 1 or > 40)
        {
            throw new ArgumentOutOfRangeException(
                nameof(allowedChannel),
                allowedChannel,
                "The live-write channel must be between 1 and 40.");
        }

        this.inner = inner;
        this.allowedTokens = allowedTokens;
        this.allowedChannel = allowedChannel;
    }

    public event EventHandler<WingParameter>? ParameterChanged
    {
        add => inner.ParameterChanged += value;
        remove => inner.ParameterChanged -= value;
    }

    public event EventHandler<WingSessionStateChange>? StateChanged
    {
        add => inner.StateChanged += value;
        remove => inner.StateChanged -= value;
    }

    public WingSessionState State => inner.State;

    public WingEndpoint? Endpoint => inner.Endpoint;

    public Task ConnectAsync(WingEndpoint endpoint, CancellationToken cancellationToken) =>
        inner.ConnectAsync(endpoint, cancellationToken);

    public Task<IReadOnlyList<WingParameter>> SnapshotAsync(
        string nodeToken,
        CancellationToken cancellationToken) =>
        inner.SnapshotAsync(nodeToken, cancellationToken);

    public Task SetAsync(
        string tokenPath,
        WingValue value,
        CancellationToken cancellationToken)
    {
        ValidateWrite(tokenPath);
        return inner.SetAsync(tokenPath, value, cancellationToken);
    }

    public Task SetManyAsync(
        IReadOnlyList<WingWriteRequest> writes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        foreach (var write in writes)
        {
            ValidateWrite(write.TokenPath);
        }

        return inner.SetManyAsync(writes, cancellationToken);
    }

    public Task PingAsync(CancellationToken cancellationToken) =>
        inner.PingAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        inner.DisconnectAsync(cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private void ValidateWrite(string tokenPath)
    {
        _ = LiveWriteTokenGuard.Validate(tokenPath, allowedTokens, allowedChannel);
    }
}

internal sealed class GuardedOscStimulus : IDisposable
{
    private const int WingOscPort = 2223;
    private readonly UdpClient client;
    private readonly IPEndPoint endpoint;
    private readonly ISet<string> allowedTokens;
    private readonly int allowedChannel;

    public GuardedOscStimulus(
        string ipAddress,
        ISet<string> allowedTokens,
        int allowedChannel)
    {
        if (!IPAddress.TryParse(ipAddress, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new LiveWriteSafetyException(
                "OSC-teststimulus vereist het vers ontdekte IPv4-adres van de gepinde FOH-console.");
        }

        ArgumentNullException.ThrowIfNull(allowedTokens);
        client = new UdpClient(AddressFamily.InterNetwork);
        endpoint = new IPEndPoint(address, WingOscPort);
        this.allowedTokens = allowedTokens;
        this.allowedChannel = allowedChannel;
    }

    public async Task SetAsync(
        string tokenPath,
        WingValue value,
        CancellationToken cancellationToken)
    {
        var normalized = LiveWriteTokenGuard.Validate(
            tokenPath,
            allowedTokens,
            allowedChannel);
        var packet = BuildOscMessage(normalized, value);
        var sent = await client.SendAsync(packet, endpoint, cancellationToken)
            .ConfigureAwait(false);
        if (sent != packet.Length)
        {
            throw new IOException(
                $"OSC-teststimulus verstuurde {sent} van {packet.Length} bytes.");
        }
    }

    public void Dispose() => client.Dispose();

    private static byte[] BuildOscMessage(string address, WingValue value)
    {
        using var stream = new MemoryStream(capacity: 128);
        WriteOscString(stream, address);
        var typeTag = value.Type switch
        {
            WingValueType.I => ",i",
            WingValueType.F => ",f",
            WingValueType.S => ",s",
            _ => throw new LiveWriteSafetyException("Unsupported OSC test value type."),
        };
        WriteOscString(stream, typeTag);
        Span<byte> scalar = stackalloc byte[sizeof(int)];
        switch (value.Type)
        {
            case WingValueType.I:
                BinaryPrimitives.WriteInt32BigEndian(scalar, value.AsInt32());
                stream.Write(scalar);
                break;
            case WingValueType.F:
                BinaryPrimitives.WriteInt32BigEndian(
                    scalar,
                    BitConverter.SingleToInt32Bits(value.AsFloat()));
                stream.Write(scalar);
                break;
            case WingValueType.S:
                if (value.AsString().Contains('\0'))
                {
                    throw new LiveWriteSafetyException(
                        "OSC test string may not contain a NUL character.");
                }

                WriteOscString(stream, value.AsString());
                break;
        }

        return stream.ToArray();
    }

    private static void WriteOscString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        var paddedLength = checked((bytes.Length + 1 + 3) & ~3);
        Span<byte> padding = stackalloc byte[paddedLength - bytes.Length];
        padding.Clear();
        stream.Write(padding);
    }
}

internal static class LiveWriteTokenGuard
{
    public static string Validate(
        string tokenPath,
        ISet<string> allowedTokens,
        int allowedChannel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPath);
        ArgumentNullException.ThrowIfNull(allowedTokens);
        if (allowedChannel is < 1 or > 40)
        {
            throw new LiveWriteSafetyException(
                $"Invalid live-write channel {allowedChannel}; only CH1..CH40 is allowed.");
        }

        var allowedChannelPrefix = $"/ch/{allowedChannel}/";
        var normalized = "/" + tokenPath.Trim().Trim('/').Replace('.', '/').ToLowerInvariant();
        if (!allowedTokens.Contains(normalized) ||
            !normalized.StartsWith(allowedChannelPrefix, StringComparison.Ordinal))
        {
            throw new LiveWriteSafetyException(
                $"Live write guard blocked non-approved token {normalized}.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var leaf = segments[^1];
        if (segments.Any(static segment =>
                segment is "conn" or "in" or "main" or "send" or "fdr" or "mute") ||
            leaf is "on" or "mdl" or "byp" or "bypass")
        {
            throw new LiveWriteSafetyException(
                $"Live-writeguard blokkeerde verboden scope/leaf {normalized}.");
        }

        var isCust = segments.Length == 3 &&
            leaf is "name" or "col" or "icon" or "led";
        var isAllowedProcessing = segments.Length >= 4 &&
            segments[2] is "eq" or "gate" or "dyn";
        if (!isCust && !isAllowedProcessing)
        {
            throw new LiveWriteSafetyException(
                $"Live-writeguard blokkeerde token buiten CUST/EQ/GATE/DYN: {normalized}.");
        }

        return normalized;
    }
}
