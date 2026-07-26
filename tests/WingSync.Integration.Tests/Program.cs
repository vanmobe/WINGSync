using WingSync.Core.Abstractions;
using WingSync.Core.Domain;
using WingSync.Core.Services;
using WingSync.Infrastructure.Simulation;

namespace WingSync.Integration.Tests;

internal static class Program
{
    private static readonly DiscoveredWing FohIdentity = new(
        "10.123.0.10",
        "TEST-FOH",
        "wing-fullsize",
        "FOH-SERIAL",
        "3.1",
        DateTimeOffset.UtcNow);

    private static readonly DiscoveredWing MonitorIdentity = new(
        "10.123.0.11",
        "TEST-MON",
        "wing-rack",
        "MON-SERIAL",
        "3.1",
        DateTimeOffset.UtcNow);

    private static readonly TestCase[] Tests =
    [
        new("simulator subtree snapshots", SimulatorReturnsSubtreeSnapshotsAsync),
        new("dry-run previews without writes", DryRunPreviewsWithoutWritesAsync),
        new("live initial sync requires confirmation", LiveInitialSyncRequiresConfirmationAsync),
        new("PreviewOnly defers initial writes but permits subsequent events", PreviewOnlyDefersInitialWritesAsync),
        new("scope selection and channel mapping", ScopeSelectionAndChannelMappingAsync),
        new("dynamic model writes are safely phased", DynamicModelWritesAreSafelyPhasedAsync),
        new(
            "partial processor snapshots fail before every target write",
            PartialProcessorSnapshotFailsBeforeWritesAsync),
        new(
            "initial subtree mirrors are scalar-certified before dispatch",
            InitialSubtreeMirrorIsScalarCertifiedAsync),
        new(
            "a later invalid target token prevents earlier plan writes",
            WholePlanTargetPreflightIsAtomicAsync),
        new("model target drift is corrected behind an exact safety guard", ModelTargetDriftIsSafelyPhasedAsync),
        new("processor safety guards are transaction-local per channel", ProcessorGuardsAreChannelLocalAsync),
        new(
            "unrelated console activity does not strand an active processor guard",
            UnrelatedEventDoesNotStrandGuardAsync),
        new(
            "same-group source interference leaves the processor guard fail-closed",
            SameGroupEventLeavesGuardFailClosedAsync),
        new("EQ model transactions never bypass independent PEQ", EqModelDoesNotBypassPeqAsync),
        new("non-model processor enable waits for parameter readback", ProcessorEnableFollowsParametersAsync),
        new(
            "coalesced same-group processor events all converge",
            CoalescedProcessorEventsAllConvergeAsync),
        new("filter model changes guard active LC, HC, and TF filters", FilterModelWritesAreSafelyPhasedAsync),
        new("delay mode and value synchronize as one guarded tuple", DelayTupleIsSafelyPhasedAsync),
        new(
            "final-off EQ and delay transactions never re-enable",
            FinalOffTransactionsNeverReenableAsync),
        new(
            "model and delay reset side-effects are rewritten before enable",
            ProcessorResetSideEffectsAreRewrittenAsync),
        new("readback mismatch pauses runtime writes", ReadbackMismatchPausesRuntimeAsync),
        new(
            "guarded scalar verification never waits for optional echo events",
            GuardedReadbackDoesNotWaitForEventsAsync),
        new(
            "a pre-dispatch matching event cannot certify a wrong write",
            PredispatchEventCannotCertifyWrongWriteAsync),
        new("target echoes are suppressed", TargetEchoIsSuppressedAsync),
        new("target drift is corrected", TargetDriftIsCorrectedAsync),
        new(
            "periodic exact target polling catches a missed drift event",
            PollingReconciliationCatchesMissedTargetDriftAsync),
        new(
            "periodic WAPI reconciliation catches a missed source event",
            PollingReconciliationCatchesMissedSourceEventAsync),
        new(
            "periodic reconciliation fairly probes every enabled scope",
            PollingReconciliationFairlyProbesEnabledScopesAsync),
        new(
            "periodic reconciliation never blocks a live source event",
            PollingReconciliationDoesNotBlockLiveEventAsync),
        new(
            "periodic reconciliation has bounded WAPI load",
            PollingReconciliationHasBoundedWapiLoadAsync),
        new(
            "periodic reconciliation honors its time budget",
            PollingReconciliationHonorsTimeBudgetAsync),
        new(
            "periodic target reconciliation honors its time budget",
            PollingTargetReconciliationHonorsTimeBudgetAsync),
        new(
            "stale node mirrors can never revert authoritative recovered scalars",
            StaleNodeMirrorNeverFlapsRecoveredScalarAsync),
        new(
            "authoritative missed gate scalar outranks a stale node mirror",
            MissedGateScalarOutranksStaleNodeAsync),
        new(
            "persistent stale EQ model mirrors fail closed",
            PersistentStaleEqModelMirrorFailsClosedAsync),
        new(
            "authoritative gate sidechain scalar blocks a stale mapped mirror",
            GateSidechainScalarBlocksStaleMappedNodeAsync),
        new(
            "AUX dynamics uses authoritative events and sidechain scalars",
            AuxDynamicsUsesAuthoritativeScalarsAsync),
        new(
            "AUX dynamics models use bypass safety semantics",
            AuxDynamicsModelUsesBypassGuardAsync),
        new("reconnect resnapshots and never replays stale events", ReconnectUsesFreshSnapshotAsync),
        new(
            "reconnect catch-up exposes ApplyingLive while writes are active",
            ReconnectReportsApplyingLiveDuringCatchupAsync),
        new(
            "runtime gate changes stay blocked while sidechain mapping is unresolved",
            RuntimeUnresolvedSidechainRemainsBlockedAsync),
        new(
            "runtime AUX dynamics stays blocked while sidechain mapping is unresolved",
            RuntimeAuxUnresolvedSidechainRemainsBlockedAsync),
        new("identity mismatch fails closed", IdentityMismatchFailsClosedAsync),
        new(
            "connected WAPI identity is rebound after TCP connect",
            ConnectedIdentityMismatchFailsClosedAsync),
        new(
            "reconnect rebinds the connected WAPI identity before resnapshot",
            ReconnectConnectedIdentityMismatchFailsClosedAsync),
        new("bounded queue fails closed on overflow", QueueOverflowFailsClosedAsync),
        new("live-write CLI requires an explicit exact acknowledgement", LiveWriteCliFailsClosedAsync),
        new("live-write guard rejects forbidden scopes", LiveWriteGuardRejectsForbiddenScopesAsync),
        new("CH40 live preflight accepts only a provably silent channel", LivePreflightFailsClosedAsync),
        new(
            "cache reset cancels pending and debounced writes",
            CacheResetIntegrationTests.PendingStateIsNotRewrittenAsync),
        new(
            "cache reset quarantines files and clears the next epoch",
            CacheResetIntegrationTests.FilesAreQuarantinedAndPartitionsClearedAsync),
        new(
            "offline cache resolves an exact persisted serial pin without discovery",
            CacheResetIntegrationTests.SerialPinLoadsWithoutDiscoveryAsync),
        new(
            "support bundle redacts configuration, identities, and parameter values",
            SupportBundleTests.SensitiveConfigurationAndLogDataIsRedactedAsync),
        new(
            "support bundle omits malformed private source content",
            SupportBundleTests.MalformedSourceContentIsOmittedRatherThanCopiedAsync),
        new(
            "confirmation never writes a source snapshot invalidated in-flight",
            SourceChangeDuringConfirmationSnapshotAsync),
        new(
            "confirmation never hides target drift behind an older snapshot",
            TargetChangeDuringConfirmationSnapshotAsync),
        new(
            "reconnect never loses a source event behind an older snapshot",
            SourceChangeDuringReconnectSnapshotAsync),
        new(
            "real source event and target drift converge in one worker drain",
            SourceAndTargetDriftSameDrainAsync),
        new(
            "real source event remains authoritative across worker drains",
            SourceAndTargetDriftNextDrainAsync),
        new(
            "interrupted runtime work is requeued without waiting for polling",
            InterruptedRuntimeWorkIsRequeuedAsync),
        new(
            "reconnect waits for an active worker batch without duplicate writes",
            ReconnectDuringWorkerBatchIsSerializedAsync),
        new(
            "reconnect waits for active confirmation without duplicate writes",
            ReconnectDuringConfirmationIsSerializedAsync),
        new(
            "initial generation change before SetMany requires reconfirmation",
            InitialGenerationChangeBeforeSetManyAsync),
        new(
            "a guard interrupted before model dispatch rolls back exactly",
            GuardInterruptedBeforeModelRollsBackAsync),
        new(
            "a guard interrupted after model dispatch pauses with recovery workflow",
            GuardInterruptedAfterModelPausesAsync),
        new(
            "reconnect generation change before SetMany forces resnapshot",
            ReconnectGenerationChangeBeforeSetManyAsync),
        new(
            "duplicate reconnect triggers cannot invalidate an active reconnect generation",
            DuplicateReconnectTriggersDoNotStarveSnapshotAsync),
        new(
            "a real transport loss invalidates an active reconnect snapshot",
            RealTransportLossInvalidatesActiveReconnectAsync),
        new(
            "stop during initial confirmation blocks every later transaction unit",
            StopDuringInitialConfirmationBlocksLaterUnitsAsync),
        new(
            "stop drains a dispatched write through explicit readback",
            StopDrainsDispatchedWriteThroughReadbackAsync),
        new(
            "stop waits for disconnect and dispose before allowing restart",
            StopWaitsForCleanupBeforeRestartAsync),
        new(
            "canceled WAPI snapshots recycle orphaned helper work",
            InfrastructureConcurrencyTests.CanceledSnapshotRecyclesHelperAsync),
        new(
            "a write queued behind a canceled snapshot never reaches WAPI",
            InfrastructureConcurrencyTests.CanceledSnapshotNeverDispatchesQueuedWriteAsync),
        new(
            "a write queued behind an unacknowledged command never reaches WAPI",
            InfrastructureConcurrencyTests.TimedOutCommandNeverDispatchesQueuedWriteAsync),
        new(
            "stopped WAPI helper readers cannot publish into a replacement generation",
            InfrastructureConcurrencyTests.StaleWapiReaderIsSuppressedAsync),
        new(
            "cache disposal drains a store already queued ahead of shutdown",
            InfrastructureConcurrencyTests.CacheDisposeDrainsQueuedStoreAsync),
    ];

    public static async Task<int> Main(string[] args)
    {
        if (InfrastructureConcurrencyTests.IsWapiGenerationHelper)
        {
            return await InfrastructureConcurrencyTests.RunWapiGenerationHelperAsync()
                .ConfigureAwait(false);
        }

        if (args.Length > 0)
        {
            try
            {
                await LiveHardwareHarness.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
                return 0;
            }
            catch (LiveWriteSafetyException exception)
            {
                Console.Error.WriteLine($"LIVE HARDWARE TEST OVERGESLAGEN (fail-closed): {exception.Message}");
                return 2;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("LIVE HARDWARE TEST MISLUKT:");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        var failed = 0;
        var startedAt = DateTimeOffset.UtcNow;
        foreach (var test in Tests)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await test.Body().ConfigureAwait(false);
                Console.WriteLine($"PASS  {test.Name} ({stopwatch.ElapsedMilliseconds} ms)");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL  {test.Name} ({stopwatch.ElapsedMilliseconds} ms)");
                Console.Error.WriteLine(exception);
            }
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        Console.WriteLine(
            $"{Tests.Length - failed}/{Tests.Length} integration tests passed in {elapsed.TotalSeconds:F2}s.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task SimulatorReturnsSubtreeSnapshotsAsync()
    {
        await using var session = new SimulatedWingSession(
            new Dictionary<string, WingValue>
            {
                ["/ch/1/eq/g"] = WingValue.FromFloat(2.5F),
                ["/ch/1/eq/on"] = WingValue.FromInt32(1),
            });
        await session.ConnectAsync(FohIdentity.Endpoint, CancellationToken.None).ConfigureAwait(false);
        var snapshot = await session.SnapshotAsync("/ch/1/eq", CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(2, snapshot.Count, "A node snapshot must return every slash-delimited child.");
    }

    private static async Task DryRunPreviewsWithoutWritesAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(3F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        var configuration = Configuration(
            dryRun: true,
            initialSync: InitialSync.SourceWins);

        await coordinator.StartAsync(configuration, false, CancellationToken.None)
            .ConfigureAwait(false);

        AssertEx.Equal(SyncCoordinatorState.RunningDryRun, coordinator.Status.State);
        AssertEx.Equal(0, target.RequestedWrites.Count);
        AssertEx.True(
            observer.Actions.Any(action =>
                action.Action == "preview" &&
                action.TargetToken == "/ch/2/eq/g" &&
                action.DryRun),
            "The initial difference must be visible as a dry-run preview.");
        AssertEx.True(coordinator.Metrics.PreviewedWrites > 0, "Preview metrics were not updated.");
    }

    private static async Task LiveInitialSyncRequiresConfirmationAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(2F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        var configuration = Configuration(
            dryRun: false,
            initialSync: InitialSync.RequireConfirmation);

        await coordinator.StartAsync(configuration, false, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(SyncCoordinatorState.AwaitingConfirmation, coordinator.Status.State);
        AssertEx.Equal(0, target.RequestedWrites.Count);

        await coordinator.ConfirmInitialSyncAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(SyncCoordinatorState.RunningLive, coordinator.Status.State);
        AssertEx.Equal(WingValue.FromFloat(2F), target.GetValue("/ch/2/eq/g"));
        AssertEx.Equal(1, target.RequestedWrites.Count);
        AssertEx.True(coordinator.Metrics.SynchronizedWrites > 0);
    }

    private static async Task PreviewOnlyDefersInitialWritesAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(2F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        var configuration = Configuration(
            dryRun: false,
            initialSync: InitialSync.PreviewOnly);

        await coordinator.StartAsync(configuration, true, CancellationToken.None)
            .ConfigureAwait(false);

        AssertEx.Equal(SyncCoordinatorState.RunningLive, coordinator.Status.State);
        AssertEx.Equal(WingValue.FromFloat(0F), target.GetValue("/ch/2/eq/g"));
        AssertEx.Equal(0, target.RequestedWrites.Count);

        source.ChangeFromConsole("/ch/1/eq/g", WingValue.FromFloat(4F));
        await AssertEx.EventuallyAsync(
            () => target.GetValue("/ch/2/eq/g") == WingValue.FromFloat(4F),
            TimeSpan.FromSeconds(2),
            "A source event after PreviewOnly startup was not synchronized.").ConfigureAwait(false);
        AssertEx.Equal(1, target.RequestedWrites.Count);
    }

    private static async Task ScopeSelectionAndChannelMappingAsync()
    {
        var source = Session(
            ("/ch/1/eq/g", WingValue.FromFloat(6F)),
            ("/ch/1/gate/thr", WingValue.FromFloat(-30F)),
            ("/ch/1/name", WingValue.FromString("Lead")));
        var target = Session(
            ("/ch/8/eq/g", WingValue.FromFloat(0F)),
            ("/ch/8/gate/thr", WingValue.FromFloat(-50F)),
            ("/ch/8/name", WingValue.FromString("Old")));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        var configuration = Configuration(
            targetChannel: 8,
            scopes: [SyncScope.Eq],
            dryRun: false,
            initialSync: InitialSync.SourceWins);

        await coordinator.StartAsync(configuration, true, CancellationToken.None)
            .ConfigureAwait(false);

        AssertEx.Equal(WingValue.FromFloat(6F), target.GetValue("/ch/8/eq/g"));
        AssertEx.Equal(WingValue.FromFloat(-50F), target.GetValue("/ch/8/gate/thr"));
        AssertEx.Equal(WingValue.FromString("Old"), target.GetValue("/ch/8/name"));
        AssertEx.True(
            target.RequestedWrites.All(write =>
                write.TokenPath.StartsWith("/ch/8/eq/", StringComparison.Ordinal)),
            "A disabled scope or unmapped channel was written.");
        AssertEx.False(target.Contains("/ch/1/eq/g"), "The source channel number leaked to the target.");
    }

    private static async Task DynamicModelWritesAreSafelyPhasedAsync()
    {
        var source = Session(
            ("/ch/1/eq/mdl", WingValue.FromString("SOUL")),
            ("/ch/1/eq/g", WingValue.FromFloat(3F)),
            ("/ch/1/eq/on", WingValue.FromInt32(1)));
        var target = Session(
            ("/ch/2/eq/mdl", WingValue.FromString("STD")),
            ("/ch/2/eq/g", WingValue.FromFloat(0F)),
            ("/ch/2/eq/on", WingValue.FromInt32(1)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);

        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        var writes = target.RequestedWrites;
        AssertEx.Equal(
            6,
            writes.Count,
            "Expected a guard before every model/parameter mutation and one verified restore.");
        AssertWrite(writes[0], "/ch/2/eq/on", WingValue.FromInt32(0));
        AssertWrite(writes[1], "/ch/2/eq/on", WingValue.FromInt32(0));
        AssertWrite(writes[2], "/ch/2/eq/mdl", WingValue.FromString("SOUL"));
        AssertWrite(writes[3], "/ch/2/eq/on", WingValue.FromInt32(0));
        AssertWrite(writes[4], "/ch/2/eq/g", WingValue.FromFloat(3F));
        AssertWrite(writes[5], "/ch/2/eq/on", WingValue.FromInt32(1));
        AssertEx.Equal(
            6,
            target.RequestedBatches.Count,
            "Guard reassertion and mutation must use separate WAPI calls.");
        AssertEx.Equal(1, target.RequestedBatches[1].Count);
        AssertEx.Equal(1, target.RequestedBatches[2].Count);
        AssertEx.Equal("/ch/2/eq/on", target.RequestedBatches[1].Single().TokenPath);
        AssertEx.Equal("/ch/2/eq/mdl", target.RequestedBatches[2].Single().TokenPath);
    }

    private static async Task PartialProcessorSnapshotFailsBeforeWritesAsync()
    {
        var source = Session(
            ("/ch/1/eq/mdl", WingValue.FromString("SOUL")),
            ("/ch/1/eq/g", WingValue.FromFloat(3F)),
            ("/ch/1/eq/on", WingValue.FromInt32(1)));
        source.SnapshotTransform = (node, values) =>
            node == "/ch/1/eq"
                ? values.Where(static parameter =>
                    parameter.TokenPath == "/ch/1/eq/mdl").ToArray()
                : values;
        var target = Session(
            ("/ch/2/eq/mdl", WingValue.FromString("STD")),
            ("/ch/2/eq/g", WingValue.FromFloat(0F)),
            ("/ch/2/eq/on", WingValue.FromInt32(1)));
        await using var coordinator = Coordinator(source, target, new RecordingObserver());

        await AssertEx.ThrowsAsync<IOException>(
                () => coordinator.StartAsync(
                    Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                    true,
                    CancellationToken.None))
            .ConfigureAwait(false);
        AssertEx.Equal(
            0,
            target.RequestedWrites.Count,
            "A mdl-only source snapshot bypassed the canonical enable preflight.");
    }

    private static async Task InitialSubtreeMirrorIsScalarCertifiedAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(5F)));
        source.SnapshotTransform = (node, values) =>
            node == "/ch/1/eq"
                ? ReplaceSnapshotValue(
                    values,
                    "/ch/1/eq/g",
                    WingValue.FromFloat(3F))
                : values;
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        await using var coordinator = Coordinator(source, target, new RecordingObserver());

        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        AssertEx.Equal(WingValue.FromFloat(5F), target.GetValue("/ch/2/eq/g"));
        AssertEx.False(
            target.RequestedWrites.Any(static write =>
                write.Value == WingValue.FromFloat(3F)),
            "A stale subtree mirror was dispatched instead of its exact scalar.");
    }

    private static async Task WholePlanTargetPreflightIsAtomicAsync()
    {
        var source = Session(
            ("/ch/1/eq/g", WingValue.FromFloat(1F)),
            ("/ch/2/eq/g", WingValue.FromFloat(2F)));
        var target = Session(
            ("/ch/11/eq/g", WingValue.FromFloat(0F)));
        await using var coordinator = Coordinator(source, target, new RecordingObserver());
        var configuration = new AppConfiguration(
            FohIdentity.Endpoint,
            MonitorIdentity.Endpoint,
            SyncDirection.FohToMonitor,
            InitialSync.SourceWins,
            new SafetySettings(
                dryRun: false,
                requireReadback: true,
                allowHighRiskWrites: false,
                stopOnVerificationFailure: true),
            [SyncScope.Eq],
            new ChannelMapping(
            [
                new InputChannelMapping(1, 11),
                new InputChannelMapping(2, 12),
            ]));

        await AssertEx.ThrowsAsync<IOException>(
                () => coordinator.StartAsync(configuration, true, CancellationToken.None))
            .ConfigureAwait(false);
        AssertEx.Equal(
            0,
            target.RequestedWrites.Count,
            "An earlier valid unit was written before a later missing target token failed.");
    }

    private static async Task ModelTargetDriftIsSafelyPhasedAsync()
    {
        var source = Session(
            ("/ch/1/eq/mdl", WingValue.FromString("STD")),
            ("/ch/1/eq/g", WingValue.FromFloat(0F)),
            ("/ch/1/eq/on", WingValue.FromInt32(1)));
        var target = Session(
            ("/ch/2/eq/mdl", WingValue.FromString("STD")),
            ("/ch/2/eq/g", WingValue.FromFloat(0F)),
            ("/ch/2/eq/on", WingValue.FromInt32(1)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        target.ChangeFromConsole("/ch/2/eq/mdl", WingValue.FromString("SOUL"));
        await AssertEx.EventuallyAsync(
                () =>
                    target.GetValue("/ch/2/eq/mdl") == WingValue.FromString("STD") &&
                    target.RequestedWrites.Count == 6,
                TimeSpan.FromSeconds(2),
                "Target model drift was not corrected through a complete guarded transaction.")
            .ConfigureAwait(false);

        AssertWrite(target.RequestedWrites[0], "/ch/2/eq/on", WingValue.FromInt32(0));
        AssertWrite(target.RequestedWrites[1], "/ch/2/eq/on", WingValue.FromInt32(0));
        AssertWrite(target.RequestedWrites[2], "/ch/2/eq/mdl", WingValue.FromString("STD"));
        AssertWrite(target.RequestedWrites[3], "/ch/2/eq/on", WingValue.FromInt32(0));
        AssertWrite(target.RequestedWrites[4], "/ch/2/eq/g", WingValue.FromFloat(0F));
        AssertWrite(target.RequestedWrites[5], "/ch/2/eq/on", WingValue.FromInt32(1));
        AssertEx.Equal(6, target.RequestedBatches.Count);
        AssertEx.True(
            source.SnapshotRequests.Any(static token => token == "/ch/1/eq/on"),
            "Model drift did not obtain an authoritative source enable scalar.");
    }

    private static async Task ProcessorGuardsAreChannelLocalAsync()
    {
        var source = Session(
            ("/ch/1/eq/mdl", WingValue.FromString("SOUL")),
            ("/ch/1/eq/g", WingValue.FromFloat(1F)),
            ("/ch/1/eq/on", WingValue.FromInt32(1)),
            ("/ch/2/eq/mdl", WingValue.FromString("SOUL")),
            ("/ch/2/eq/g", WingValue.FromFloat(2F)),
            ("/ch/2/eq/on", WingValue.FromInt32(1)));
        var target = Session(
            ("/ch/11/eq/mdl", WingValue.FromString("STD")),
            ("/ch/11/eq/g", WingValue.FromFloat(0F)),
            ("/ch/11/eq/on", WingValue.FromInt32(1)),
            ("/ch/12/eq/mdl", WingValue.FromString("STD")),
            ("/ch/12/eq/g", WingValue.FromFloat(0F)),
            ("/ch/12/eq/on", WingValue.FromInt32(1)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        var configuration = new AppConfiguration(
            FohIdentity.Endpoint,
            MonitorIdentity.Endpoint,
            SyncDirection.FohToMonitor,
            InitialSync.SourceWins,
            new SafetySettings(
                dryRun: false,
                requireReadback: true,
                allowHighRiskWrites: false,
                stopOnVerificationFailure: true),
            [SyncScope.Eq],
            new ChannelMapping(
            [
                new InputChannelMapping(1, 11),
                new InputChannelMapping(2, 12),
            ]));
        await coordinator.StartAsync(configuration, true, CancellationToken.None)
            .ConfigureAwait(false);

        AssertEx.Equal(12, target.RequestedWrites.Count);
        AssertWrite(target.RequestedWrites[0], "/ch/11/eq/on", WingValue.FromInt32(0));
        AssertWrite(target.RequestedWrites[1], "/ch/11/eq/on", WingValue.FromInt32(0));
        AssertWrite(target.RequestedWrites[2], "/ch/11/eq/mdl", WingValue.FromString("SOUL"));
        AssertWrite(target.RequestedWrites[3], "/ch/11/eq/on", WingValue.FromInt32(0));
        AssertWrite(target.RequestedWrites[4], "/ch/11/eq/g", WingValue.FromFloat(1F));
        AssertWrite(target.RequestedWrites[5], "/ch/11/eq/on", WingValue.FromInt32(1));
        AssertWrite(target.RequestedWrites[6], "/ch/12/eq/on", WingValue.FromInt32(0));
        AssertWrite(target.RequestedWrites[7], "/ch/12/eq/on", WingValue.FromInt32(0));
        AssertWrite(target.RequestedWrites[8], "/ch/12/eq/mdl", WingValue.FromString("SOUL"));
        AssertWrite(target.RequestedWrites[9], "/ch/12/eq/on", WingValue.FromInt32(0));
        AssertWrite(target.RequestedWrites[10], "/ch/12/eq/g", WingValue.FromFloat(2F));
        AssertWrite(target.RequestedWrites[11], "/ch/12/eq/on", WingValue.FromInt32(1));
        AssertEx.Equal(
            12,
            target.RequestedBatches.Count,
            "Each channel must complete its six guard/mutation calls before the next guard opens.");
    }

    private static async Task UnrelatedEventDoesNotStrandGuardAsync()
    {
        var source = Session(
            ("/ch/1/eq/mdl", WingValue.FromString("STD")),
            ("/ch/1/eq/g", WingValue.FromFloat(0F)),
            ("/ch/1/eq/on", WingValue.FromInt32(1)),
            ("/ch/2/eq/mdl", WingValue.FromString("STD")),
            ("/ch/2/eq/g", WingValue.FromFloat(0F)),
            ("/ch/2/eq/on", WingValue.FromInt32(1)));
        var target = Session(
            ("/ch/11/eq/mdl", WingValue.FromString("STD")),
            ("/ch/11/eq/g", WingValue.FromFloat(0F)),
            ("/ch/11/eq/on", WingValue.FromInt32(1)),
            ("/ch/12/eq/mdl", WingValue.FromString("STD")),
            ("/ch/12/eq/g", WingValue.FromFloat(0F)),
            ("/ch/12/eq/on", WingValue.FromInt32(1)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        var configuration = new AppConfiguration(
            FohIdentity.Endpoint,
            MonitorIdentity.Endpoint,
            SyncDirection.FohToMonitor,
            InitialSync.SourceWins,
            new SafetySettings(
                dryRun: false,
                requireReadback: true,
                allowHighRiskWrites: false,
                stopOnVerificationFailure: true),
            [SyncScope.Eq],
            new ChannelMapping(
            [
                new InputChannelMapping(1, 11),
                new InputChannelMapping(2, 12),
            ]));
        await coordinator.StartAsync(configuration, true, CancellationToken.None)
            .ConfigureAwait(false);

        var injected = 0;
        target.BeforeSetManyAsync = (capture, _) =>
        {
            if (capture.RequestedWrites.Any(static write =>
                    write.TokenPath == "/ch/11/eq/mdl") &&
                Interlocked.Exchange(ref injected, 1) == 0)
            {
                source.ChangeFromConsole("/ch/2/eq/g", WingValue.FromFloat(4F));
            }

            return Task.CompletedTask;
        };

        source.ChangeFromConsole("/ch/1/eq/mdl", WingValue.FromString("SOUL"));
        await AssertEx.EventuallyAsync(
                () =>
                    target.GetValue("/ch/11/eq/mdl") == WingValue.FromString("SOUL") &&
                    target.GetValue("/ch/11/eq/on") == WingValue.FromInt32(1) &&
                    target.GetValue("/ch/12/eq/g") == WingValue.FromFloat(4F),
                TimeSpan.FromSeconds(3),
                "An unrelated source event stranded a guard or prevented its queued update.")
            .ConfigureAwait(false);

        AssertEx.Equal(SyncCoordinatorState.RunningLive, coordinator.Status.State);
        AssertEx.False(
            observer.Diagnostics.Any(static diagnostic =>
                diagnostic.Code is "SAFETY_GUARD_RECOVERY_REQUIRED" or "SYNC_BATCH_FAILED"),
            "Unrelated console activity incorrectly triggered manual guard recovery.");
    }

    private static async Task SameGroupEventLeavesGuardFailClosedAsync()
    {
        var source = Session(
            ("/ch/1/eq/mdl", WingValue.FromString("STD")),
            ("/ch/1/eq/g", WingValue.FromFloat(0F)),
            ("/ch/1/eq/on", WingValue.FromInt32(1)));
        var target = Session(
            ("/ch/11/eq/mdl", WingValue.FromString("STD")),
            ("/ch/11/eq/g", WingValue.FromFloat(0F)),
            ("/ch/11/eq/on", WingValue.FromInt32(1)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        var configuration = new AppConfiguration(
            FohIdentity.Endpoint,
            MonitorIdentity.Endpoint,
            SyncDirection.FohToMonitor,
            InitialSync.SourceWins,
            new SafetySettings(
                dryRun: false,
                requireReadback: true,
                allowHighRiskWrites: false,
                stopOnVerificationFailure: true),
            [SyncScope.Eq],
            new ChannelMapping([new InputChannelMapping(1, 11)]));
        await coordinator.StartAsync(configuration, true, CancellationToken.None)
            .ConfigureAwait(false);

        var injected = 0;
        target.BeforeSetManyAsync = (capture, _) =>
        {
            if (capture.RequestedWrites.Any(static write =>
                    write.TokenPath == "/ch/11/eq/mdl") &&
                Interlocked.Exchange(ref injected, 1) == 0)
            {
                source.ChangeFromConsole("/ch/1/eq/g", WingValue.FromFloat(4F));
            }

            return Task.CompletedTask;
        };

        source.ChangeFromConsole("/ch/1/eq/mdl", WingValue.FromString("SOUL"));
        await AssertEx.EventuallyAsync(
                () => coordinator.Status.State == SyncCoordinatorState.Paused,
                TimeSpan.FromSeconds(3),
                "A same-group source change did not stop the in-flight guarded transaction.")
            .ConfigureAwait(false);

        AssertEx.Equal(
            WingValue.FromInt32(0),
            target.GetValue("/ch/11/eq/on"),
            "The processor was re-enabled after same-group interference made its mutation indeterminate.");
        AssertEx.True(
            observer.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "SAFETY_GUARD_RECOVERY_REQUIRED"),
            "The operator did not receive the manual recovery workflow for the stranded guard.");
    }

    private static async Task EqModelDoesNotBypassPeqAsync()
    {
        var source = Session(
            ("/ch/1/eq/mdl", WingValue.FromString("SOUL")),
            ("/ch/1/eq/on", WingValue.FromInt32(1)),
            ("/ch/1/peq/1", WingValue.FromFloat(3F)),
            ("/ch/1/peq/on", WingValue.FromInt32(1)));
        var target = Session(
            ("/ch/2/eq/mdl", WingValue.FromString("STD")),
            ("/ch/2/eq/on", WingValue.FromInt32(1)),
            ("/ch/2/peq/1", WingValue.FromFloat(0F)),
            ("/ch/2/peq/on", WingValue.FromInt32(1)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        AssertEx.Equal(5, target.RequestedWrites.Count);
        AssertWrite(target.RequestedWrites[0], "/ch/2/eq/on", WingValue.FromInt32(0));
        AssertWrite(target.RequestedWrites[1], "/ch/2/eq/on", WingValue.FromInt32(0));
        AssertWrite(target.RequestedWrites[2], "/ch/2/eq/mdl", WingValue.FromString("SOUL"));
        AssertWrite(target.RequestedWrites[3], "/ch/2/eq/on", WingValue.FromInt32(1));
        AssertWrite(target.RequestedWrites[4], "/ch/2/peq/1", WingValue.FromFloat(3F));
        AssertEx.False(
            target.RequestedWrites.Any(static write => write.TokenPath == "/ch/2/peq/on"),
            "An EQ model change unnecessarily bypassed the independent PreSend EQ.");
        AssertEx.Equal(5, target.RequestedBatches.Count);
    }

    private static async Task ProcessorEnableFollowsParametersAsync()
    {
        var source = Session(
            ("/ch/1/eq/g", WingValue.FromFloat(0F)),
            ("/ch/1/eq/on", WingValue.FromInt32(0)));
        var target = Session(
            ("/ch/2/eq/g", WingValue.FromFloat(0F)),
            ("/ch/2/eq/on", WingValue.FromInt32(0)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.ChangeFromConsole("/ch/1/eq/g", WingValue.FromFloat(4F));
        source.ChangeFromConsole("/ch/1/eq/on", WingValue.FromInt32(1));
        await AssertEx.EventuallyAsync(
                () => target.RequestedWrites.Count == 2,
                TimeSpan.FromSeconds(2),
                "The coalesced parameter and enable changes were not synchronized.")
            .ConfigureAwait(false);

        AssertEx.Equal(
            2,
            target.RequestedBatches.Count,
            "A processor parameter and its enable must have separate verified phases.");
        AssertWrite(target.RequestedBatches[0].Single(), "/ch/2/eq/g", WingValue.FromFloat(4F));
        AssertWrite(target.RequestedBatches[1].Single(), "/ch/2/eq/on", WingValue.FromInt32(1));
    }

    private static async Task CoalescedProcessorEventsAllConvergeAsync()
    {
        var source = Session(
            ("/ch/1/gate/mdl", WingValue.FromString("GATE")),
            ("/ch/1/gate/on", WingValue.FromInt32(0)),
            ("/ch/1/gate/thr", WingValue.FromFloat(-20F)),
            ("/ch/1/gate/mix", WingValue.FromFloat(0F)),
            ("/ch/1/gatesc/src", WingValue.FromString("CH.1")));
        var target = Session(
            ("/ch/2/gate/mdl", WingValue.FromString("GATE")),
            ("/ch/2/gate/on", WingValue.FromInt32(0)),
            ("/ch/2/gate/thr", WingValue.FromFloat(-20F)),
            ("/ch/2/gate/mix", WingValue.FromFloat(0F)),
            ("/ch/2/gatesc/src", WingValue.FromString("CH.2")));
        await using var coordinator = Coordinator(source, target, new RecordingObserver());
        await coordinator.StartAsync(
                Configuration(
                    scopes: [SyncScope.Gate],
                    dryRun: false,
                    initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.ChangeFromConsole("/ch/1/gate/thr", WingValue.FromFloat(-8F));
        source.ChangeFromConsole("/ch/1/gate/mix", WingValue.FromFloat(75F));
        await AssertEx.EventuallyAsync(
                () =>
                    target.GetValue("/ch/2/gate/thr") == WingValue.FromFloat(-8F) &&
                    target.GetValue("/ch/2/gate/mix") == WingValue.FromFloat(75F),
                TimeSpan.FromSeconds(2),
                "One of two coalesced gate events was dropped by group refresh.")
            .ConfigureAwait(false);
    }

    private static async Task FilterModelWritesAreSafelyPhasedAsync()
    {
        var source = Session(
            ("/ch/1/flt/lc", WingValue.FromInt32(1)),
            ("/ch/1/flt/hc", WingValue.FromInt32(1)),
            ("/ch/1/flt/tf", WingValue.FromInt32(1)),
            ("/ch/1/flt/mdl", WingValue.FromString("TILT")),
            ("/ch/1/flt/1", WingValue.FromFloat(120F)));
        var target = Session(
            ("/ch/2/flt/lc", WingValue.FromInt32(1)),
            ("/ch/2/flt/hc", WingValue.FromInt32(1)),
            ("/ch/2/flt/tf", WingValue.FromInt32(1)),
            ("/ch/2/flt/mdl", WingValue.FromString("STD")),
            ("/ch/2/flt/1", WingValue.FromFloat(80F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);

        await coordinator.StartAsync(
                Configuration(
                    scopes: [SyncScope.Filter],
                    dryRun: false,
                    initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        AssertEx.Equal(14, target.RequestedWrites.Count);
        AssertEx.Equal(6, target.RequestedBatches.Count);
        AssertEx.True(target.RequestedBatches[0].All(write =>
            write.TokenPath is "/ch/2/flt/lc" or "/ch/2/flt/hc" or "/ch/2/flt/tf" &&
            write.Value == WingValue.FromInt32(0)));
        AssertEx.Equal(3, target.RequestedBatches[1].Count);
        AssertEx.True(target.RequestedBatches[1].All(write =>
            write.TokenPath is "/ch/2/flt/lc" or "/ch/2/flt/hc" or "/ch/2/flt/tf" &&
            write.Value == WingValue.FromInt32(0)));
        AssertWrite(
            target.RequestedBatches[2].Single(),
            "/ch/2/flt/mdl",
            WingValue.FromString("TILT"));
        AssertEx.Equal(3, target.RequestedBatches[3].Count);
        AssertEx.True(target.RequestedBatches[3].All(write =>
            write.TokenPath is "/ch/2/flt/lc" or "/ch/2/flt/hc" or "/ch/2/flt/tf" &&
            write.Value == WingValue.FromInt32(0)));
        AssertWrite(
            target.RequestedBatches[4].Single(),
            "/ch/2/flt/1",
            WingValue.FromFloat(120F));
        AssertEx.True(target.RequestedBatches[5].All(write =>
            write.TokenPath is "/ch/2/flt/lc" or "/ch/2/flt/hc" or "/ch/2/flt/tf" &&
            write.Value == WingValue.FromInt32(1)));
    }

    private static async Task DelayTupleIsSafelyPhasedAsync()
    {
        var source = Session(
            ("/ch/1/in/set/dlymode", WingValue.FromString("M")),
            ("/ch/1/in/set/dly", WingValue.FromFloat(1F)),
            ("/ch/1/in/set/dlyon", WingValue.FromInt32(1)));
        var target = Session(
            ("/ch/2/in/set/dlymode", WingValue.FromString("MS")),
            ("/ch/2/in/set/dly", WingValue.FromFloat(100F)),
            ("/ch/2/in/set/dlyon", WingValue.FromInt32(1)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(
                    scopes: [SyncScope.Delay],
                    dryRun: false,
                    initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        AssertEx.Equal(6, target.RequestedWrites.Count);
        AssertWrite(
            target.RequestedWrites[0],
            "/ch/2/in/set/dlyon",
            WingValue.FromInt32(0));
        AssertWrite(
            target.RequestedWrites[1],
            "/ch/2/in/set/dlyon",
            WingValue.FromInt32(0));
        AssertWrite(
            target.RequestedWrites[2],
            "/ch/2/in/set/dlymode",
            WingValue.FromString("M"));
        AssertWrite(
            target.RequestedWrites[3],
            "/ch/2/in/set/dlyon",
            WingValue.FromInt32(0));
        AssertWrite(
            target.RequestedWrites[4],
            "/ch/2/in/set/dly",
            WingValue.FromFloat(1F));
        AssertWrite(
            target.RequestedWrites[5],
            "/ch/2/in/set/dlyon",
            WingValue.FromInt32(1));

        source.SetSilently("/ch/1/in/set/dly", WingValue.FromFloat(3F));
        source.ChangeFromConsole(
            "/ch/1/in/set/dlymode",
            WingValue.FromString("FT"));
        await AssertEx.EventuallyAsync(
                () =>
                    target.GetValue("/ch/2/in/set/dlymode") == WingValue.FromString("FT") &&
                    target.GetValue("/ch/2/in/set/dly") == WingValue.FromFloat(3F) &&
                    target.RequestedWrites.Count == 12,
                TimeSpan.FromSeconds(2),
                "A runtime delay-mode event did not refresh and phase the complete delay tuple.")
            .ConfigureAwait(false);

        AssertWrite(
            target.RequestedWrites[6],
            "/ch/2/in/set/dlyon",
            WingValue.FromInt32(0));
        AssertWrite(
            target.RequestedWrites[7],
            "/ch/2/in/set/dlyon",
            WingValue.FromInt32(0));
        AssertWrite(
            target.RequestedWrites[8],
            "/ch/2/in/set/dlymode",
            WingValue.FromString("FT"));
        AssertWrite(
            target.RequestedWrites[9],
            "/ch/2/in/set/dlyon",
            WingValue.FromInt32(0));
        AssertWrite(
            target.RequestedWrites[10],
            "/ch/2/in/set/dly",
            WingValue.FromFloat(3F));
        AssertWrite(
            target.RequestedWrites[11],
            "/ch/2/in/set/dlyon",
            WingValue.FromInt32(1));
    }

    private static async Task FinalOffTransactionsNeverReenableAsync()
    {
        var eqSource = Session(
            ("/ch/1/eq/mdl", WingValue.FromString("SOUL")),
            ("/ch/1/eq/g", WingValue.FromFloat(3F)),
            ("/ch/1/eq/on", WingValue.FromInt32(0)));
        var eqTarget = Session(
            ("/ch/2/eq/mdl", WingValue.FromString("STD")),
            ("/ch/2/eq/g", WingValue.FromFloat(0F)),
            ("/ch/2/eq/on", WingValue.FromInt32(0)));
        await using (var coordinator = Coordinator(
                         eqSource,
                         eqTarget,
                         new RecordingObserver()))
        {
            await coordinator.StartAsync(
                    Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                    true,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        AssertEx.Equal(WingValue.FromInt32(0), eqTarget.GetValue("/ch/2/eq/on"));
        AssertEx.False(
            eqTarget.RequestedWrites.Any(write =>
                write.TokenPath == "/ch/2/eq/on" &&
                write.Value == WingValue.FromInt32(1)),
            "A final-off EQ model transaction re-enabled the processor.");
        AssertEx.True(
            eqTarget.RequestedBatches[1].Single().TokenPath == "/ch/2/eq/on" &&
            eqTarget.RequestedBatches[2].Single().TokenPath == "/ch/2/eq/mdl",
            "EQ guard reassertion and model mutation were not separate calls.");

        var delaySource = Session(
            ("/ch/1/in/set/dlymode", WingValue.FromString("M")),
            ("/ch/1/in/set/dly", WingValue.FromFloat(2F)),
            ("/ch/1/in/set/dlyon", WingValue.FromInt32(0)));
        var delayTarget = Session(
            ("/ch/2/in/set/dlymode", WingValue.FromString("MS")),
            ("/ch/2/in/set/dly", WingValue.FromFloat(100F)),
            ("/ch/2/in/set/dlyon", WingValue.FromInt32(0)));
        await using (var coordinator = Coordinator(
                         delaySource,
                         delayTarget,
                         new RecordingObserver()))
        {
            await coordinator.StartAsync(
                    Configuration(
                        scopes: [SyncScope.Delay],
                        dryRun: false,
                        initialSync: InitialSync.SourceWins),
                    true,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        AssertEx.Equal(
            WingValue.FromInt32(0),
            delayTarget.GetValue("/ch/2/in/set/dlyon"));
        AssertEx.False(
            delayTarget.RequestedWrites.Any(write =>
                write.TokenPath == "/ch/2/in/set/dlyon" &&
                write.Value == WingValue.FromInt32(1)),
            "A final-off delay transaction re-enabled the delay.");
    }

    private static async Task ProcessorResetSideEffectsAreRewrittenAsync()
    {
        var eqSource = Session(
            ("/ch/1/eq/mdl", WingValue.FromString("SOUL")),
            ("/ch/1/eq/g", WingValue.FromFloat(3F)),
            ("/ch/1/eq/on", WingValue.FromInt32(1)));
        var eqTarget = Session(
            ("/ch/2/eq/mdl", WingValue.FromString("STD")),
            ("/ch/2/eq/g", WingValue.FromFloat(0F)),
            ("/ch/2/eq/on", WingValue.FromInt32(1)));
        eqTarget.BeforeSetManyAsync = (capture, _) =>
        {
            if (capture.RequestedWrites.Any(static write =>
                    write.TokenPath == "/ch/2/eq/mdl"))
            {
                eqTarget.ChangeFromConsole("/ch/2/eq/g", WingValue.FromFloat(-99F));
            }

            return Task.CompletedTask;
        };
        await using (var coordinator = Coordinator(
                         eqSource,
                         eqTarget,
                         new RecordingObserver()))
        {
            await coordinator.StartAsync(
                    Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                    true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            AssertEx.Equal(SyncCoordinatorState.RunningLive, coordinator.Status.State);
        }

        AssertEx.Equal(WingValue.FromFloat(3F), eqTarget.GetValue("/ch/2/eq/g"));
        AssertEx.Equal(WingValue.FromInt32(1), eqTarget.GetValue("/ch/2/eq/on"));

        var delaySource = Session(
            ("/ch/1/in/set/dlymode", WingValue.FromString("M")),
            ("/ch/1/in/set/dly", WingValue.FromFloat(2F)),
            ("/ch/1/in/set/dlyon", WingValue.FromInt32(1)));
        var delayTarget = Session(
            ("/ch/2/in/set/dlymode", WingValue.FromString("MS")),
            ("/ch/2/in/set/dly", WingValue.FromFloat(100F)),
            ("/ch/2/in/set/dlyon", WingValue.FromInt32(1)));
        delayTarget.BeforeSetManyAsync = (capture, _) =>
        {
            if (capture.RequestedWrites.Any(static write =>
                    write.TokenPath == "/ch/2/in/set/dlymode"))
            {
                delayTarget.ChangeFromConsole(
                    "/ch/2/in/set/dly",
                    WingValue.FromFloat(999F));
            }

            return Task.CompletedTask;
        };
        await using (var coordinator = Coordinator(
                         delaySource,
                         delayTarget,
                         new RecordingObserver()))
        {
            await coordinator.StartAsync(
                    Configuration(
                        scopes: [SyncScope.Delay],
                        dryRun: false,
                        initialSync: InitialSync.SourceWins),
                    true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            AssertEx.Equal(SyncCoordinatorState.RunningLive, coordinator.Status.State);
        }

        AssertEx.Equal(
            WingValue.FromFloat(2F),
            delayTarget.GetValue("/ch/2/in/set/dly"));
        AssertEx.Equal(
            WingValue.FromInt32(1),
            delayTarget.GetValue("/ch/2/in/set/dlyon"));
    }

    private static async Task GuardedReadbackDoesNotWaitForEventsAsync()
    {
        var source = Session(
            ("/ch/1/eq/mdl", WingValue.FromString("SOUL")),
            ("/ch/1/eq/g", WingValue.FromFloat(3F)),
            ("/ch/1/eq/on", WingValue.FromInt32(1)));
        var target = Session(
            ("/ch/2/eq/mdl", WingValue.FromString("STD")),
            ("/ch/2/eq/g", WingValue.FromFloat(0F)),
            ("/ch/2/eq/on", WingValue.FromInt32(1)));
        target.EmitWriteEvents = false;
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);

        var startedAt = DateTimeOffset.UtcNow;
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);
        var elapsed = DateTimeOffset.UtcNow - startedAt;

        AssertEx.True(
            elapsed < TimeSpan.FromMilliseconds(500),
            $"Exact scalar verification waited {elapsed.TotalMilliseconds:F0} ms for optional echo events.");
        AssertEx.Equal(6, target.RequestedBatches.Count);
        AssertEx.Equal(WingValue.FromInt32(1), target.GetValue("/ch/2/eq/on"));
        AssertEx.Equal(WingValue.FromString("SOUL"), target.GetValue("/ch/2/eq/mdl"));
    }

    private static async Task ReadbackMismatchPausesRuntimeAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        target.WriteTransform = static (token, value) =>
            token == "/ch/2/eq/g" ? WingValue.FromFloat(99F) : value;
        source.ChangeFromConsole("/ch/1/eq/g", WingValue.FromFloat(5F));

        await AssertEx.EventuallyAsync(
            () => coordinator.Status.State == SyncCoordinatorState.Paused,
            TimeSpan.FromSeconds(3),
            "A mismatching readback did not pause synchronization.").ConfigureAwait(false);
        AssertEx.True(
            observer.Diagnostics.Any(diagnostic => diagnostic.Code == "READBACK_MISMATCH"),
            "The operator diagnostic did not identify the readback mismatch.");
        AssertEx.Equal(0L, coordinator.Metrics.SynchronizedWrites);

        var attemptedWrites = target.RequestedWrites.Count;
        source.DropConnection();
        target.DropConnection();
        await Task.Delay(TimeSpan.FromSeconds(1.2)).ConfigureAwait(false);
        AssertEx.Equal(
            SyncCoordinatorState.Paused,
            coordinator.Status.State,
            "A verification pause must remain latched until the operator restarts.");
        AssertEx.Equal(
            attemptedWrites,
            target.RequestedWrites.Count,
            "A reconnect must never retry a write after readback verification failed.");
    }

    private static async Task PredispatchEventCannotCertifyWrongWriteAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        var expected = WingValue.FromFloat(5F);
        target.BeforeSetManyAsync = (_, _) =>
        {
            target.EmitWithoutPersisting("/ch/2/eq/g", expected);
            return Task.CompletedTask;
        };
        target.WriteTransform = static (_, _) => WingValue.FromFloat(99F);
        source.ChangeFromConsole("/ch/1/eq/g", expected);

        await AssertEx.EventuallyAsync(
            () => coordinator.Status.State == SyncCoordinatorState.Paused,
            TimeSpan.FromSeconds(3),
            "A matching event queued before dispatch falsely certified an incorrect write.")
            .ConfigureAwait(false);
        AssertEx.True(
            target.SnapshotRequests.Any(static token => token == "/ch/2/eq/g"),
            "Verified live writes must always perform an explicit post-dispatch scalar readback.");
        AssertEx.Equal(0L, coordinator.Metrics.SynchronizedWrites);
    }

    private static async Task TargetEchoIsSuppressedAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(
                    dryRun: false,
                    initialSync: InitialSync.SourceWins,
                    requireReadback: true),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.ChangeFromConsole("/ch/1/eq/g", WingValue.FromFloat(7F));
        await AssertEx.EventuallyAsync(
            () => target.GetValue("/ch/2/eq/g") == WingValue.FromFloat(7F),
            TimeSpan.FromSeconds(2),
            "The source event was not applied.").ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false);

        AssertEx.Equal(1, target.RequestedWrites.Count, "A target echo caused a duplicate write.");
        AssertEx.False(
            observer.Diagnostics.Any(diagnostic => diagnostic.Code == "TARGET_DRIFT"),
            "A verified local echo was misclassified as target drift.");
    }

    private static async Task TargetDriftIsCorrectedAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(4F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(4F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        target.ChangeFromConsole("/ch/2/eq/g", WingValue.FromFloat(-2F));
        await AssertEx.EventuallyAsync(
            () =>
                target.GetValue("/ch/2/eq/g") == WingValue.FromFloat(4F) &&
                target.RequestedWrites.Count == 1,
            TimeSpan.FromSeconds(2),
            "The authoritative source value did not correct target drift.").ConfigureAwait(false);
        AssertEx.True(
            observer.Diagnostics.Any(diagnostic => diagnostic.Code == "TARGET_DRIFT"),
            "Target drift was corrected without an operator-visible diagnostic.");
    }

    private static async Task PollingReconciliationCatchesMissedTargetDriftAsync()
    {
        var authoritative = WingValue.FromFloat(4F);
        var source = Session(("/ch/1/eq/g", authoritative));
        var target = Session(("/ch/2/eq/g", authoritative));
        var observer = new RecordingObserver();
        var clock = new HealthPollGateClock();
        await using var coordinator = Coordinator(source, target, observer, clock: clock);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        await clock.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        target.SetSilently("/ch/2/eq/g", WingValue.FromFloat(-2F));
        clock.Release();

        await AssertEx.EventuallyAsync(
                () =>
                    target.GetValue("/ch/2/eq/g") == authoritative &&
                    target.RequestedWrites.Count == 1,
                TimeSpan.FromSeconds(2),
                "An eventless target drift remained undetected after exact target polling.")
            .ConfigureAwait(false);
        AssertEx.True(
            observer.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "MISSED_TARGET_EVENT_RECOVERED"),
            "The recovered missed target event was not reported.");
        AssertEx.True(
            target.SnapshotRequests.Any(static token => token == "/ch/2/eq/g"),
            "Target reconciliation did not use an authoritative exact scalar read.");
    }

    private static async Task PollingReconciliationCatchesMissedSourceEventAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        var clock = new HealthPollGateClock();
        await using var coordinator = Coordinator(source, target, observer, clock: clock);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        await clock.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var missedValue = WingValue.FromFloat(6F);
        source.SetSilently("/ch/1/eq/g", missedValue);
        clock.Release();

        await AssertEx.EventuallyAsync(
            () =>
                target.GetValue("/ch/2/eq/g") == missedValue &&
                target.RequestedWrites.Count == 1,
            TimeSpan.FromSeconds(2),
            "The fallback reconciliation snapshot did not recover a missed WAPI event.")
            .ConfigureAwait(false);
        AssertEx.True(
            source.SnapshotRequests.Any(static token => token == "/ch/1"),
            "Fallback reconciliation did not snapshot the mapped source channel root.");
    }

    private static async Task PollingReconciliationFairlyProbesEnabledScopesAsync()
    {
        var sourceValues = new Dictionary<string, WingValue>(StringComparer.Ordinal)
        {
            ["/ch/1/col"] = WingValue.FromInt32(1),
            ["/ch/1/eq/1"] = WingValue.FromFloat(0F),
            ["/ch/1/gate/1"] = WingValue.FromFloat(-40F),
        };
        for (var index = 1; index <= 7; index++)
        {
            sourceValues[$"/ch/1/dyn/{index}"] = WingValue.FromFloat(index);
        }

        var targetValues = sourceValues.ToDictionary(
            static pair => pair.Key.Replace("/ch/1/", "/ch/2/", StringComparison.Ordinal),
            static pair => pair.Value,
            StringComparer.Ordinal);
        var source = new ScriptedWingSession(sourceValues);
        var target = new ScriptedWingSession(targetValues);
        var clock = new HealthPollGateClock();
        await using var coordinator = Coordinator(
            source,
            target,
            new RecordingObserver(),
            clock: clock);
        await coordinator.StartAsync(
                Configuration(
                    scopes: [SyncScope.Cust, SyncScope.Eq, SyncScope.Gate, SyncScope.Dyn],
                    dryRun: false,
                    initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        var requestsBeforePoll = source.SnapshotRequests.Count;
        await clock.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        clock.Release();
        await AssertEx.EventuallyAsync(
                () =>
                {
                    var requests = source.SnapshotRequests.Skip(requestsBeforePoll).ToArray();
                    return requests.Contains("/ch/1/col", StringComparer.Ordinal) &&
                           requests.Contains("/ch/1/eq/1", StringComparer.Ordinal) &&
                           requests.Contains("/ch/1/gate/1", StringComparer.Ordinal) &&
                           requests.Contains("/ch/1/dyn/1", StringComparer.Ordinal);
                },
                TimeSpan.FromSeconds(2),
                "A large DYN subtree starved an enabled CUST, EQ, GATE, or DYN scope probe.")
            .ConfigureAwait(false);
    }

    private static async Task PollingReconciliationDoesNotBlockLiveEventAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        var clock = new HealthPollGateClock();
        await using var coordinator = Coordinator(source, target, observer, clock: clock);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        var pollSnapshotGate = new SnapshotGate("/ch/1", invocation: 1);
        source.SnapshotCapturedAsync = pollSnapshotGate.OnCapturedAsync;
        await clock.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        clock.Release();
        await pollSnapshotGate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);

        try
        {
            var liveValue = WingValue.FromFloat(8F);
            source.ChangeFromConsole("/ch/1/eq/g", liveValue);
            await AssertEx.EventuallyAsync(
                    () =>
                        target.GetValue("/ch/2/eq/g") == liveValue &&
                        target.RequestedWrites.Count == 1,
                    TimeSpan.FromMilliseconds(750),
                    "A live source event waited behind the in-flight reconciliation snapshot.")
                .ConfigureAwait(false);
        }
        finally
        {
            pollSnapshotGate.Release();
        }
    }

    private static async Task PollingReconciliationHasBoundedWapiLoadAsync()
    {
        var initialSource = Enumerable.Range(1, 40)
            .ToDictionary(
                static channel => $"/ch/{channel}/eq/g",
                static _ => WingValue.FromFloat(0F),
                StringComparer.Ordinal);
        var initialTarget = Enumerable.Range(1, 40)
            .ToDictionary(
                static channel => $"/ch/{channel}/eq/g",
                static _ => WingValue.FromFloat(0F),
                StringComparer.Ordinal);
        var source = new ScriptedWingSession(initialSource);
        var target = new ScriptedWingSession(initialTarget);
        var observer = new RecordingObserver();
        var clock = new HealthPollGateClock();
        await using var coordinator = Coordinator(source, target, observer, clock: clock);
        var configuration = new AppConfiguration(
            FohIdentity.Endpoint,
            MonitorIdentity.Endpoint,
            SyncDirection.FohToMonitor,
            InitialSync.SourceWins,
            new SafetySettings(
                dryRun: false,
                requireReadback: true,
                allowHighRiskWrites: false,
                stopOnVerificationFailure: true),
            [SyncScope.Eq],
            new ChannelMapping(
                Enumerable.Range(1, 40)
                    .Select(static channel => new InputChannelMapping(channel, channel))
                    .ToArray()));
        await coordinator.StartAsync(configuration, true, CancellationToken.None)
            .ConfigureAwait(false);

        var requestsBeforePoll = source.SnapshotRequests.Count;
        await clock.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        clock.Release();
        await AssertEx.EventuallyAsync(
                () => source.SnapshotRequests
                    .Skip(requestsBeforePoll)
                    .Count(static token => token.Count(static character => character == '/') == 2) >= 2,
                TimeSpan.FromSeconds(2),
                "The bounded reconciliation roots were not polled.")
            .ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);

        var pollRequests = source.SnapshotRequests.Skip(requestsBeforePoll).ToArray();
        AssertEx.Equal(
            2,
            pollRequests.Count(static token =>
                token.Count(static character => character == '/') == 2),
            "A single poll must read exactly its rotating two-root budget.");
        AssertEx.True(
            pollRequests.Length <= 18,
            $"A single bounded poll issued {pollRequests.Length} WAPI reads; expected at most 18. " +
            $"Requests: {string.Join(", ", pollRequests)}");
    }

    private static async Task PollingReconciliationHonorsTimeBudgetAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        var clock = new HealthPollGateClock();
        await using var coordinator = Coordinator(source, target, observer, clock: clock);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        var pollSnapshotGate = new SnapshotGate("/ch/1", invocation: 1);
        source.SnapshotCapturedAsync = pollSnapshotGate.OnCapturedAsync;
        await clock.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        clock.Release();
        await pollSnapshotGate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1_400)).ConfigureAwait(false);
            AssertEx.False(
                observer.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == "RECONCILIATION_BUDGET_EXCEEDED"),
                "An already dispatched source read was canceled at the health budget.");
            AssertEx.Equal(
                SyncCoordinatorState.RunningLive,
                coordinator.Status.State,
                "A slow in-flight fallback read must not recycle a healthy WAPI session.");
            pollSnapshotGate.Release();
            await AssertEx.EventuallyAsync(
                    () => observer.Diagnostics.Any(diagnostic =>
                        diagnostic.Code == "RECONCILIATION_BUDGET_EXCEEDED"),
                    TimeSpan.FromSeconds(2),
                    "The completed slow source poll did not report that later dispatches were skipped.")
                .ConfigureAwait(false);
            AssertEx.Equal(
                SyncCoordinatorState.RunningLive,
                coordinator.Status.State,
                "A bounded fallback poll must not interrupt healthy live synchronization.");
        }
        finally
        {
            pollSnapshotGate.Release();
        }
    }

    private static async Task PollingTargetReconciliationHonorsTimeBudgetAsync()
    {
        const string targetToken = "/ch/2/eq/g";
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session((targetToken, WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        var clock = new HealthPollGateClock();
        await using var coordinator = Coordinator(source, target, observer, clock: clock);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        var nextInvocation = target.SnapshotRequests.Count(token =>
            token.Equals(targetToken, StringComparison.Ordinal)) + 1;
        var pollSnapshotGate = new SnapshotGate(targetToken, nextInvocation);
        target.SnapshotCapturedAsync = pollSnapshotGate.OnCapturedAsync;
        await clock.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        clock.Release();
        await pollSnapshotGate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1_400)).ConfigureAwait(false);
            AssertEx.False(
                observer.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == "TARGET_RECONCILIATION_BUDGET_EXCEEDED"),
                "An already dispatched target read was canceled at the health budget.");
            AssertEx.Equal(
                SyncCoordinatorState.RunningLive,
                coordinator.Status.State,
                "A slow in-flight target read must not recycle a healthy WAPI session.");
            pollSnapshotGate.Release();
            await AssertEx.EventuallyAsync(
                    () => observer.Diagnostics.Any(diagnostic =>
                        diagnostic.Code == "TARGET_RECONCILIATION_BUDGET_EXCEEDED"),
                    TimeSpan.FromSeconds(2),
                    "The completed slow target poll did not report that later dispatches were skipped.")
                .ConfigureAwait(false);
            AssertEx.Equal(
                SyncCoordinatorState.RunningLive,
                coordinator.Status.State,
                "A bounded target-drift poll must not interrupt healthy live synchronization.");
        }
        finally
        {
            pollSnapshotGate.Release();
        }
    }

    private static async Task StaleNodeMirrorNeverFlapsRecoveredScalarAsync()
    {
        var oldValue = WingValue.FromFloat(0F);
        var newValue = WingValue.FromFloat(6F);
        var sourceValues = Enumerable.Range(1, 12)
            .ToDictionary(
                static index => $"/ch/1/eq/{index}",
                static _ => WingValue.FromFloat(0F),
                StringComparer.Ordinal);
        var targetValues = Enumerable.Range(1, 12)
            .ToDictionary(
                static index => $"/ch/2/eq/{index}",
                static _ => WingValue.FromFloat(0F),
                StringComparer.Ordinal);
        var source = new ScriptedWingSession(sourceValues);
        var target = new ScriptedWingSession(targetValues);
        var observer = new RecordingObserver();
        var clock = new HealthPollPulseClock();
        await using var coordinator = Coordinator(source, target, observer, clock: clock);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.SnapshotTransform = (node, values) =>
            node is "/ch/1" or "/ch/1/eq"
                ? ReplaceSnapshotValue(values, "/ch/1/eq/12", oldValue)
                : values;
        source.SetSilently("/ch/1/eq/12", newValue);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await AssertEx.EventuallyAsync(
                () => target.GetValue("/ch/2/eq/12") == newValue,
                TimeSpan.FromSeconds(2),
                "The first authoritative scalar poll did not recover the missed change.")
            .ConfigureAwait(false);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await AssertEx.EventuallyAsync(
                () => source.SnapshotRequests.Count(static token =>
                    token == "/ch/1/eq/12") >= 2,
                TimeSpan.FromSeconds(2),
                "The second cycle did not scalar-confirm the stale node candidate.")
            .ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);

        AssertEx.Equal(newValue, target.GetValue("/ch/2/eq/12"));
        AssertEx.Equal(
            1,
            target.RequestedWrites.Count(write =>
                write.TokenPath == "/ch/2/eq/12"),
            "A root-only stale value caused the recovered scalar to flap.");
        AssertEx.False(
            target.RequestedWrites.Any(write =>
                write.TokenPath == "/ch/2/eq/12" &&
                write.Value == oldValue),
            "A stale node mirror was promoted to an authoritative source event.");
    }

    private static async Task MissedGateScalarOutranksStaleNodeAsync()
    {
        var oldThreshold = WingValue.FromFloat(-20F);
        var newThreshold = WingValue.FromFloat(-8F);
        var source = Session(
            ("/ch/1/gate/thr", oldThreshold),
            ("/ch/1/gate/mdl", WingValue.FromString("GATE")),
            ("/ch/1/gate/on", WingValue.FromInt32(0)),
            ("/ch/1/gatesc/src", WingValue.FromString("CH.1")));
        var target = Session(
            ("/ch/2/gate/thr", oldThreshold),
            ("/ch/2/gate/mdl", WingValue.FromString("GATE")),
            ("/ch/2/gate/on", WingValue.FromInt32(0)),
            ("/ch/2/gatesc/src", WingValue.FromString("CH.2")));
        var observer = new RecordingObserver();
        var clock = new HealthPollGateClock();
        await using var coordinator = Coordinator(source, target, observer, clock: clock);
        await coordinator.StartAsync(
                Configuration(
                    scopes: [SyncScope.Gate],
                    dryRun: false,
                    initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.SnapshotTransform = (node, values) =>
            node is "/ch/1" or "/ch/1/gate"
                ? ReplaceSnapshotValue(values, "/ch/1/gate/thr", oldThreshold)
                : values;
        await clock.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        source.SetSilently("/ch/1/gate/thr", newThreshold);
        clock.Release();

        await AssertEx.EventuallyAsync(
                () =>
                    target.GetValue("/ch/2/gate/thr") == newThreshold &&
                    target.RequestedWrites.Any(write =>
                        write.TokenPath == "/ch/2/gate/thr" &&
                        write.Value == newThreshold),
                TimeSpan.FromSeconds(2),
                "The exact missed gate scalar was replaced by a stale gate-node mirror.")
            .ConfigureAwait(false);
        AssertEx.True(
            observer.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "MISSED_SOURCE_EVENT_RECOVERED"),
            "The recovered missed event was not visible to the operator.");
    }

    private static async Task PersistentStaleEqModelMirrorFailsClosedAsync()
    {
        var oldModel = WingValue.FromString("STD");
        var newModel = WingValue.FromString("SOUL");
        var source = Session(
            ("/ch/1/eq/mdl", oldModel),
            ("/ch/1/eq/g", WingValue.FromFloat(0F)),
            ("/ch/1/eq/on", WingValue.FromInt32(1)));
        var target = Session(
            ("/ch/2/eq/mdl", oldModel),
            ("/ch/2/eq/g", WingValue.FromFloat(0F)),
            ("/ch/2/eq/on", WingValue.FromInt32(1)));
        var observer = new RecordingObserver();
        var clock = new HealthPollGateClock();
        await using var coordinator = Coordinator(source, target, observer, clock: clock);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.SnapshotTransform = (node, values) =>
            node is "/ch/1" or "/ch/1/eq"
                ? ReplaceSnapshotValue(values, "/ch/1/eq/mdl", oldModel)
                : values;
        await clock.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        source.SetSilently("/ch/1/eq/mdl", newModel);
        clock.Release();

        await AssertEx.EventuallyAsync(
                () => coordinator.Status.State == SyncCoordinatorState.Paused,
                TimeSpan.FromSeconds(2),
                "A persistent disagreement between the EQ node mirror and exact scalar did not fail closed.")
            .ConfigureAwait(false);
        AssertEx.Equal(oldModel, target.GetValue("/ch/2/eq/mdl"));
        AssertEx.Equal(
            0,
            target.RequestedWrites.Count,
            "An unstable EQ model mirror reached the target console.");
        AssertEx.True(
            observer.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "MISSED_SOURCE_EVENT_RECOVERED"),
            "The exact missed model change was not visible to the operator.");
        AssertEx.True(
            observer.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "SYNC_BATCH_FAILED"),
            "The fail-closed stale-mirror condition was not visible to the operator.");
    }

    private static async Task GateSidechainScalarBlocksStaleMappedNodeAsync()
    {
        var source = Session(
            ("/ch/1/gate/thr", WingValue.FromFloat(-20F)),
            ("/ch/1/gate/mdl", WingValue.FromString("GATE")),
            ("/ch/1/gate/on", WingValue.FromInt32(0)),
            ("/ch/1/gatesc/src", WingValue.FromString("CH.1")));
        var target = Session(
            ("/ch/2/gate/thr", WingValue.FromFloat(-20F)),
            ("/ch/2/gate/mdl", WingValue.FromString("GATE")),
            ("/ch/2/gate/on", WingValue.FromInt32(0)),
            ("/ch/2/gatesc/src", WingValue.FromString("CH.2")));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(
                    scopes: [SyncScope.Gate],
                    dryRun: false,
                    initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.SnapshotTransform = (node, values) =>
            node is "/ch/1" or "/ch/1/gatesc"
                ? ReplaceSnapshotValue(
                    values,
                    "/ch/1/gatesc/src",
                    WingValue.FromString("CH.1"))
                : values;
        source.SetSilently("/ch/1/gatesc/src", WingValue.FromString("CH.3"));
        source.ChangeFromConsole("/ch/1/gate/thr", WingValue.FromFloat(-9F));

        await AssertEx.EventuallyAsync(
                () => observer.Diagnostics.Any(diagnostic =>
                    diagnostic.Code.Contains(
                        "UNRESOLVEDSIDECHAIN",
                        StringComparison.Ordinal)),
                TimeSpan.FromSeconds(2),
                "An authoritative unmapped gate sidechain was hidden by a stale mapped node.")
            .ConfigureAwait(false);
        AssertEx.Equal(0, target.RequestedWrites.Count);
        AssertEx.Equal(WingValue.FromFloat(-20F), target.GetValue("/ch/2/gate/thr"));
        AssertEx.True(
            source.SnapshotRequests.Any(static token => token == "/ch/1/gatesc/src"),
            "The routing-critical gate sidechain was not read as an exact scalar.");
    }

    private static async Task AuxDynamicsUsesAuthoritativeScalarsAsync()
    {
        var oldThreshold = WingValue.FromFloat(-20F);
        var firstThreshold = WingValue.FromFloat(-10F);
        var source = Session(
            ("/aux/1/dyn/thr", oldThreshold),
            ("/aux/1/dyn/mdl", WingValue.FromString("COMP")),
            ("/aux/1/dyn/byp", WingValue.FromString("ON")),
            ("/aux/1/dynsc/src", WingValue.FromString("OFF")));
        var target = Session(
            ("/aux/2/dyn/thr", oldThreshold),
            ("/aux/2/dyn/mdl", WingValue.FromString("COMP")),
            ("/aux/2/dyn/byp", WingValue.FromString("ON")),
            ("/aux/2/dynsc/src", WingValue.FromString("OFF")));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        var configuration = new AppConfiguration(
            FohIdentity.Endpoint,
            MonitorIdentity.Endpoint,
            SyncDirection.FohToMonitor,
            InitialSync.SourceWins,
            new SafetySettings(
                dryRun: false,
                requireReadback: true,
                allowHighRiskWrites: false,
                stopOnVerificationFailure: true),
            [SyncScope.Dyn],
            new ChannelMapping(
                inputChannels: [],
                auxChannels: [new AuxChannelMapping(1, 2)]));
        await coordinator.StartAsync(configuration, true, CancellationToken.None)
            .ConfigureAwait(false);

        source.SnapshotTransform = (node, values) =>
            node is "/aux/1" or "/aux/1/dyn"
                ? ReplaceSnapshotValue(values, "/aux/1/dyn/thr", oldThreshold)
                : values;
        source.ChangeFromConsole("/aux/1/dyn/thr", firstThreshold);
        await AssertEx.EventuallyAsync(
                () => target.GetValue("/aux/2/dyn/thr") == firstThreshold,
                TimeSpan.FromSeconds(2),
                "An authoritative AUX dynamics event was replaced by a stale node mirror.")
            .ConfigureAwait(false);

        var writesAfterPropagation = target.RequestedWrites.Count;
        source.SnapshotTransform = (node, values) =>
            node is "/aux/1" or "/aux/1/dynsc"
                ? ReplaceSnapshotValue(
                    values,
                    "/aux/1/dynsc/src",
                    WingValue.FromString("OFF"))
                : values;
        source.SetSilently("/aux/1/dynsc/src", WingValue.FromString("CH.3"));
        source.ChangeFromConsole("/aux/1/dyn/thr", WingValue.FromFloat(-5F));
        await AssertEx.EventuallyAsync(
                () => observer.Diagnostics.Any(diagnostic =>
                    diagnostic.Code.Contains(
                        "UNRESOLVEDSIDECHAIN",
                        StringComparison.Ordinal)),
                TimeSpan.FromSeconds(2),
                "An authoritative unmapped AUX sidechain was hidden by a stale OFF node.")
            .ConfigureAwait(false);
        AssertEx.Equal(writesAfterPropagation, target.RequestedWrites.Count);
        AssertEx.Equal(firstThreshold, target.GetValue("/aux/2/dyn/thr"));
        AssertEx.True(
            source.SnapshotRequests.Any(static token => token == "/aux/1/dynsc/src"),
            "The routing-critical AUX dynamics sidechain was not read as an exact scalar.");
    }

    private static async Task AuxDynamicsModelUsesBypassGuardAsync()
    {
        var source = Session(
            ("/aux/1/dyn/mdl", WingValue.FromString("BUSCOMP")),
            ("/aux/1/dyn/thr", WingValue.FromFloat(-8F)),
            ("/aux/1/dyn/byp", WingValue.FromString("OFF")),
            ("/aux/1/dynsc/src", WingValue.FromString("OFF")));
        var target = Session(
            ("/aux/2/dyn/mdl", WingValue.FromString("COMP")),
            ("/aux/2/dyn/thr", WingValue.FromFloat(-20F)),
            ("/aux/2/dyn/byp", WingValue.FromString("OFF")),
            ("/aux/2/dynsc/src", WingValue.FromString("OFF")));
        await using var coordinator = Coordinator(source, target, new RecordingObserver());
        var configuration = new AppConfiguration(
            FohIdentity.Endpoint,
            MonitorIdentity.Endpoint,
            SyncDirection.FohToMonitor,
            InitialSync.SourceWins,
            new SafetySettings(
                dryRun: false,
                requireReadback: true,
                allowHighRiskWrites: false,
                stopOnVerificationFailure: true),
            [SyncScope.Dyn],
            new ChannelMapping(
                inputChannels: [],
                auxChannels: [new AuxChannelMapping(1, 2)]));

        await coordinator.StartAsync(configuration, true, CancellationToken.None)
            .ConfigureAwait(false);

        AssertWrite(
            target.RequestedBatches[0].Single(),
            "/aux/2/dyn/byp",
            WingValue.FromString("ON"));
        AssertWrite(
            target.RequestedBatches[1].Single(),
            "/aux/2/dyn/byp",
            WingValue.FromString("ON"));
        AssertWrite(
            target.RequestedBatches[2].Single(),
            "/aux/2/dyn/mdl",
            WingValue.FromString("BUSCOMP"));
        AssertEx.Equal(WingValue.FromString("OFF"), target.GetValue("/aux/2/dyn/byp"));
        AssertEx.Equal(WingValue.FromFloat(-8F), target.GetValue("/aux/2/dyn/thr"));
    }

    private static async Task ReconnectUsesFreshSnapshotAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        var cache = new RecordingStateSink();
        await using var coordinator = Coordinator(source, target, observer, cache);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.ChangeFromConsole("/ch/1/eq/g", WingValue.FromFloat(5F));
        source.SetSilently("/ch/1/eq/g", WingValue.FromFloat(1F));
        source.DropConnection();

        await AssertEx.EventuallyAsync(
            () =>
                coordinator.Status.State == SyncCoordinatorState.RunningLive &&
                coordinator.Metrics.ReconnectCount == 1 &&
                target.GetValue("/ch/2/eq/g") == WingValue.FromFloat(1F),
            TimeSpan.FromSeconds(4),
            "Reconnect did not rebuild state from a fresh source snapshot.").ConfigureAwait(false);

        AssertEx.False(
            target.RequestedWrites.Any(write => write.Value == WingValue.FromFloat(5F)),
            "An epoch-one queued value was replayed after reconnect.");
        AssertEx.True(source.ConnectCount >= 2 && target.ConnectCount >= 2);
        AssertEx.Equal(2, cache.StaleRoles.Distinct(StringComparer.Ordinal).Count());
        AssertEx.True(
            cache.Epochs.Select(static epoch => epoch.Epoch).Distinct().Count() >= 2,
            "A fresh cache epoch was not opened after reconnect.");
    }

    private static async Task ReconnectReportsApplyingLiveDuringCatchupAsync()
    {
        var initial = WingValue.FromFloat(0F);
        var latest = WingValue.FromFloat(6F);
        var source = Session(("/ch/1/eq/g", initial));
        var target = Session(("/ch/2/eq/g", initial));
        var gate = new SetManyGate();
        target.BeforeSetManyAsync = gate.OnBeforeSetManyAsync;
        await using var coordinator = Coordinator(source, target, new RecordingObserver());

        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.SetSilently("/ch/1/eq/g", latest);
        source.DropConnection("test ApplyingLive during reconnect catch-up");

        try
        {
            await gate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            AssertEx.Equal(
                SyncCoordinatorState.ApplyingLive,
                coordinator.Status.State,
                "Reconnect bleef Snapshotting rapporteren terwijl SetMany actief was.");
            AssertEx.Equal(0, target.RequestedWrites.Count);

            gate.Release();

            await AssertEx.EventuallyAsync(
                    () =>
                        coordinator.Status.State == SyncCoordinatorState.RunningLive &&
                        coordinator.Metrics.ReconnectCount == 1 &&
                        target.GetValue("/ch/2/eq/g") == latest,
                    TimeSpan.FromSeconds(4),
                    "Reconnect catch-up voltooide niet na het vrijgeven van SetMany.")
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        AssertEx.Equal(1, target.RequestedWrites.Count);
        AssertEx.Equal(latest, target.RequestedWrites[0].Value);
    }

    private static async Task RuntimeUnresolvedSidechainRemainsBlockedAsync()
    {
        var source = Session(
            ("/ch/1/gate/thr", WingValue.FromFloat(-20F)),
            ("/ch/1/gate/mdl", WingValue.FromString("GATE")),
            ("/ch/1/gate/on", WingValue.FromInt32(0)),
            ("/ch/1/gatesc/src", WingValue.FromString("CH.2")));
        var target = Session(
            ("/ch/8/gate/thr", WingValue.FromFloat(-30F)),
            ("/ch/8/gate/mdl", WingValue.FromString("GATE")),
            ("/ch/8/gate/on", WingValue.FromInt32(0)),
            ("/ch/8/gatesc/src", WingValue.FromString("OFF")));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        var configuration = Configuration(
            targetChannel: 8,
            scopes: [SyncScope.Gate],
            dryRun: false,
            initialSync: InitialSync.SourceWins);
        await coordinator.StartAsync(configuration, true, CancellationToken.None)
            .ConfigureAwait(false);

        AssertEx.Equal(0, target.RequestedWrites.Count);
        var issueCount = observer.Diagnostics.Count(diagnostic =>
            diagnostic.Code.Contains("UNRESOLVEDSIDECHAIN", StringComparison.Ordinal));

        source.ChangeFromConsole("/ch/1/gate/thr", WingValue.FromFloat(-10F));
        await AssertEx.EventuallyAsync(
            () => observer.Diagnostics.Count(diagnostic =>
                diagnostic.Code.Contains("UNRESOLVEDSIDECHAIN", StringComparison.Ordinal)) > issueCount,
            TimeSpan.FromSeconds(2),
            "A later gate parameter did not re-evaluate the unresolved sidechain.")
            .ConfigureAwait(false);
        AssertEx.Equal(0, target.RequestedWrites.Count);
        issueCount = observer.Diagnostics.Count(diagnostic =>
            diagnostic.Code.Contains("UNRESOLVEDSIDECHAIN", StringComparison.Ordinal));

        source.ChangeFromConsole("/ch/1/gate/mdl", WingValue.FromString("EXP2"));
        await AssertEx.EventuallyAsync(
            () => observer.Diagnostics.Count(diagnostic =>
                diagnostic.Code.Contains("UNRESOLVEDSIDECHAIN", StringComparison.Ordinal)) > issueCount,
            TimeSpan.FromSeconds(2),
            "A model event bypassed the unresolved sidechain block.")
            .ConfigureAwait(false);
        AssertEx.Equal(0, target.RequestedWrites.Count);
        issueCount = observer.Diagnostics.Count(diagnostic =>
            diagnostic.Code.Contains("UNRESOLVEDSIDECHAIN", StringComparison.Ordinal));

        source.ChangeFromConsole("/ch/1/gate/on", WingValue.FromInt32(1));
        await AssertEx.EventuallyAsync(
            () => observer.Diagnostics.Count(diagnostic =>
                diagnostic.Code.Contains("UNRESOLVEDSIDECHAIN", StringComparison.Ordinal)) > issueCount,
            TimeSpan.FromSeconds(2),
            "An enable event bypassed the unresolved sidechain block.")
            .ConfigureAwait(false);
        AssertEx.Equal(0, target.RequestedWrites.Count);

        source.ChangeFromConsole("/ch/1/gatesc/src", WingValue.FromString("CH.1"));
        await AssertEx.EventuallyAsync(
            () =>
                target.GetValue("/ch/8/gate/thr") == WingValue.FromFloat(-10F) &&
                target.GetValue("/ch/8/gate/mdl") == WingValue.FromString("EXP2") &&
                target.GetValue("/ch/8/gate/on") == WingValue.FromInt32(1) &&
                target.GetValue("/ch/8/gatesc/src") == WingValue.FromString("CH.8"),
            TimeSpan.FromSeconds(3),
            "A newly mapped sidechain did not release a fresh complete gate group.")
            .ConfigureAwait(false);
    }

    private static async Task RuntimeAuxUnresolvedSidechainRemainsBlockedAsync()
    {
        var source = Session(
            ("/aux/1/dyn/thr", WingValue.FromFloat(-20F)),
            ("/aux/1/dyn/mdl", WingValue.FromString("COMP")),
            ("/aux/1/dyn/byp", WingValue.FromString("ON")),
            ("/aux/1/dynsc/src", WingValue.FromString("CH.3")));
        var target = Session(
            ("/aux/2/dyn/thr", WingValue.FromFloat(-30F)),
            ("/aux/2/dyn/mdl", WingValue.FromString("COMP")),
            ("/aux/2/dyn/byp", WingValue.FromString("ON")),
            ("/aux/2/dynsc/src", WingValue.FromString("OFF")));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        var configuration = new AppConfiguration(
            FohIdentity.Endpoint,
            MonitorIdentity.Endpoint,
            SyncDirection.FohToMonitor,
            InitialSync.SourceWins,
            new SafetySettings(
                dryRun: false,
                requireReadback: true,
                allowHighRiskWrites: false,
                stopOnVerificationFailure: true),
            [SyncScope.Dyn],
            new ChannelMapping(
                inputChannels: [],
                auxChannels: [new AuxChannelMapping(1, 2)]));
        await coordinator.StartAsync(configuration, true, CancellationToken.None)
            .ConfigureAwait(false);

        AssertEx.Equal(0, target.RequestedWrites.Count);
        var issueCount = observer.Diagnostics.Count(diagnostic =>
            diagnostic.Code.Contains("UNRESOLVEDSIDECHAIN", StringComparison.Ordinal));
        source.ChangeFromConsole("/aux/1/dyn/thr", WingValue.FromFloat(-10F));
        await AssertEx.EventuallyAsync(
            () => observer.Diagnostics.Count(diagnostic =>
                diagnostic.Code.Contains("UNRESOLVEDSIDECHAIN", StringComparison.Ordinal)) > issueCount,
            TimeSpan.FromSeconds(2),
            "A later AUX dynamics parameter bypassed its unresolved sidechain.")
            .ConfigureAwait(false);
        AssertEx.Equal(0, target.RequestedWrites.Count);

        source.ChangeFromConsole("/aux/1/dynsc/src", WingValue.FromString("OFF"));
        await AssertEx.EventuallyAsync(
            () => target.GetValue("/aux/2/dyn/thr") == WingValue.FromFloat(-10F),
            TimeSpan.FromSeconds(3),
            "A valid AUX sidechain did not release a fresh complete dynamics group.")
            .ConfigureAwait(false);
        AssertEx.False(
            source.SnapshotRequests.Any(static token => token == "/aux/1/dynxo"),
            "AUX refresh requested the input-only DYNXO node.");
    }

    private static async Task IdentityMismatchFailsClosedAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(1F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        var verifier = new StaticWingIdentityVerifier([FohIdentity, MonitorIdentity]);
        await using var coordinator = new SyncCoordinator(
            new ScriptedSessionFactory(source, target),
            verifier,
            new RecordingStateSink(),
            observer);
        var invalidConfiguration = new AppConfiguration(
            new WingEndpoint(FohIdentity.IpAddress, 2222, "A-DIFFERENT-SERIAL"),
            MonitorIdentity.Endpoint,
            SyncDirection.FohToMonitor,
            InitialSync.SourceWins,
            new SafetySettings(dryRun: false),
            [SyncScope.Eq],
            new ChannelMapping([new InputChannelMapping(1, 2)]));

        await AssertEx.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync(invalidConfiguration, true, CancellationToken.None))
            .ConfigureAwait(false);

        AssertEx.Equal(SyncCoordinatorState.Faulted, coordinator.Status.State);
        AssertEx.Equal(0, source.ConnectCount);
        AssertEx.Equal(0, target.ConnectCount);
        AssertEx.Equal(0, target.RequestedWrites.Count);
    }

    private static async Task ConnectedIdentityMismatchFailsClosedAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(1F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        source.SeedSilently(
            "/$syscfg/$serial",
            WingValue.FromString("IMPOSTOR-AT-PINNED-IP"));

        await AssertEx.ThrowsAsync<IOException>(
                () => coordinator.StartAsync(
                    Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                    true,
                    CancellationToken.None))
            .ConfigureAwait(false);

        AssertEx.Equal(SyncCoordinatorState.Faulted, coordinator.Status.State);
        AssertEx.Equal(1, source.ConnectCount);
        AssertEx.Equal(1, target.ConnectCount);
        AssertEx.Equal(0, target.RequestedWrites.Count);
        AssertEx.True(
            observer.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "CONNECTED_IDENTITY_MISMATCH"),
            "The operator was not told that UDP discovery and connected WAPI identity differed.");
    }

    private static async Task ReconnectConnectedIdentityMismatchFailsClosedAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.SetSilently(
            "/$syscfg/$serial",
            WingValue.FromString("SWAPPED-DURING-RECONNECT"));
        source.SetSilently("/ch/1/eq/g", WingValue.FromFloat(7F));
        source.DropConnection("physical endpoint changed identity");

        await AssertEx.EventuallyAsync(
            () => coordinator.Status.State == SyncCoordinatorState.Paused,
            TimeSpan.FromSeconds(3),
            "Reconnect did not stop on a connected serial mismatch.")
            .ConfigureAwait(false);
        AssertEx.Equal(0, target.RequestedWrites.Count);
        AssertEx.Equal(0L, coordinator.Metrics.ReconnectCount);
        AssertEx.True(
            observer.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "CONNECTED_IDENTITY_MISMATCH"),
            "The reconnect identity mismatch was not exposed to the operator.");
    }

    private static async Task QueueOverflowFailsClosedAsync()
    {
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(
                    dryRun: false,
                    initialSync: InitialSync.SourceWins,
                    requireReadback: true),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        for (var index = 0; index < 20_000; index++)
        {
            source.EmitWithoutPersisting(
                $"/ch/1/eq/load{index}",
                WingValue.FromInt32(index));
        }

        await AssertEx.EventuallyAsync(
            () => observer.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "EVENT_QUEUE_OVERFLOW"),
            TimeSpan.FromSeconds(2),
            "Queue overflow was not detected and surfaced.").ConfigureAwait(false);

        AssertEx.True(
            coordinator.Status.State is SyncCoordinatorState.Paused or SyncCoordinatorState.Reconnecting,
            "Writes did not fail closed after event loss.");
        AssertEx.True(
            coordinator.Metrics.QueueDepth <= 4_096,
            "The bounded event queue exceeded its documented capacity.");
    }

    private static async Task SourceChangeDuringConfirmationSnapshotAsync()
    {
        var staleValue = WingValue.FromFloat(1F);
        var latestValue = WingValue.FromFloat(5F);
        var source = Session(("/ch/1/eq/g", staleValue));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(
                    dryRun: false,
                    initialSync: InitialSync.RequireConfirmation),
                false,
                CancellationToken.None)
            .ConfigureAwait(false);

        var gate = new SnapshotGate("/ch/1/eq", invocation: 3);
        source.SnapshotCapturedAsync = gate.OnCapturedAsync;
        var confirmation = coordinator.ConfirmInitialSyncAsync(CancellationToken.None);
        await gate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        source.ChangeFromConsole("/ch/1/eq/g", latestValue);
        gate.Release();

        await AssertEx.ThrowsAsync<InitialSyncPreviewChangedException>(() => confirmation)
            .ConfigureAwait(false);
        AssertEx.Equal(SyncCoordinatorState.AwaitingConfirmation, coordinator.Status.State);
        AssertEx.Equal(
            0,
            target.RequestedWrites.Count,
            "No value from the invalidated source snapshot may be written before reconfirmation.");

        await coordinator.ConfirmInitialSyncAsync(CancellationToken.None).ConfigureAwait(false);
        await AssertEx.EventuallyAsync(
            () =>
                coordinator.Status.State == SyncCoordinatorState.RunningLive &&
                target.GetValue("/ch/2/eq/g") == latestValue,
            TimeSpan.FromSeconds(2),
            "The event that invalidated confirmation was lost instead of becoming the new preview.")
            .ConfigureAwait(false);
        AssertEx.False(
            target.RequestedWrites.Any(write => write.Value == staleValue),
            "The stale source value captured before the event was written live.");
        AssertEx.True(
            target.RequestedWrites.Any(write => write.Value == latestValue),
            "The explicitly reconfirmed latest source value was not synchronized.");
    }

    private static async Task TargetChangeDuringConfirmationSnapshotAsync()
    {
        var authoritative = WingValue.FromFloat(1F);
        var drift = WingValue.FromFloat(9F);
        var source = Session(("/ch/1/eq/g", authoritative));
        var target = Session(("/ch/2/eq/g", authoritative));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(
                    dryRun: false,
                    initialSync: InitialSync.RequireConfirmation),
                false,
                CancellationToken.None)
            .ConfigureAwait(false);

        var gate = new SnapshotGate("/ch/2/eq", invocation: 3);
        target.SnapshotCapturedAsync = gate.OnCapturedAsync;
        var confirmation = coordinator.ConfirmInitialSyncAsync(CancellationToken.None);
        await gate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        target.ChangeFromConsole("/ch/2/eq/g", drift);
        gate.Release();

        await AssertEx.ThrowsAsync<InitialSyncPreviewChangedException>(() => confirmation)
            .ConfigureAwait(false);
        AssertEx.Equal(SyncCoordinatorState.AwaitingConfirmation, coordinator.Status.State);
        AssertEx.Equal(
            0,
            target.RequestedWrites.Count,
            "Target drift during the snapshot must first produce a new preview, not an implicit write.");

        await coordinator.ConfirmInitialSyncAsync(CancellationToken.None).ConfigureAwait(false);
        await AssertEx.EventuallyAsync(
            () =>
                coordinator.Status.State == SyncCoordinatorState.RunningLive &&
                target.GetValue("/ch/2/eq/g") == authoritative,
            TimeSpan.FromSeconds(2),
            "An older target snapshot hid drift instead of reconciling the authoritative value.")
            .ConfigureAwait(false);
        AssertEx.Equal(1, target.RequestedWrites.Count);
        AssertEx.Equal(authoritative, target.RequestedWrites[0].Value);
    }

    private static async Task SourceChangeDuringReconnectSnapshotAsync()
    {
        var staleValue = WingValue.FromFloat(0F);
        var latestValue = WingValue.FromFloat(6F);
        var source = Session(("/ch/1/eq/g", staleValue));
        var target = Session(("/ch/2/eq/g", staleValue));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        var gate = new SnapshotGate("/ch/1/eq", invocation: 3);
        source.SnapshotCapturedAsync = gate.OnCapturedAsync;
        source.DropConnection();
        await gate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        source.ChangeFromConsole("/ch/1/eq/g", latestValue);
        gate.Release();

        await AssertEx.EventuallyAsync(
            () =>
                coordinator.Status.State == SyncCoordinatorState.RunningLive &&
                coordinator.Metrics.ReconnectCount == 1,
            TimeSpan.FromSeconds(4),
            "Reconnect did not finish after its invalidated snapshot was retried.")
            .ConfigureAwait(false);
        AssertEx.Equal(
            latestValue,
            target.GetValue("/ch/2/eq/g"),
            "The source event emitted during reconnect snapshotting was lost.");
        AssertEx.False(
            target.RequestedWrites.Any(write => write.Value == staleValue),
            "Reconnect wrote a source value captured before the in-flight event.");
        AssertEx.True(
            target.RequestedWrites.Any(write => write.Value == latestValue),
            "Reconnect did not synchronize the latest source value after retrying its snapshot.");
    }

    private static async Task SourceAndTargetDriftSameDrainAsync()
    {
        var initial = WingValue.FromFloat(1F);
        var latestSource = WingValue.FromFloat(5F);
        var targetDrift = WingValue.FromFloat(9F);
        var source = Session(("/ch/1/eq/g", initial));
        var target = Session(("/ch/2/eq/g", initial));
        var observer = new RecordingObserver();
        var clock = new CoalescingDelayGateClock();
        await using var coordinator = Coordinator(source, target, observer, clock: clock);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.ChangeFromConsole("/ch/1/eq/g", latestSource);
        await clock.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        target.ChangeFromConsole("/ch/2/eq/g", targetDrift);
        clock.Release();

        await AssertEx.EventuallyAsync(
            () =>
                target.GetValue("/ch/2/eq/g") == latestSource &&
                target.RequestedWrites.Count > 0,
            TimeSpan.FromSeconds(2),
            "Same-drain source/drift work did not converge to the real latest source.")
            .ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        AssertEx.Equal(latestSource, target.RequestedWrites[^1].Value);
        AssertEx.False(
            target.RequestedWrites.Any(write => write.Value == initial || write.Value == targetDrift),
            "A synthetic drift value superseded the newer real source event.");
    }

    private static async Task SourceAndTargetDriftNextDrainAsync()
    {
        var initial = WingValue.FromFloat(1F);
        var latestSource = WingValue.FromFloat(5F);
        var targetDrift = WingValue.FromFloat(9F);
        var source = Session(("/ch/1/eq/g", initial));
        var target = Session(("/ch/2/eq/g", initial));
        var observer = new RecordingObserver();
        var setManyGate = new SetManyGate();
        target.BeforeSetManyAsync = setManyGate.OnBeforeSetManyAsync;
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.ChangeFromConsole("/ch/1/eq/g", latestSource);
        await setManyGate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        target.ChangeFromConsole("/ch/2/eq/g", targetDrift);
        setManyGate.Release();

        await AssertEx.EventuallyAsync(
            () =>
                target.GetValue("/ch/2/eq/g") == latestSource &&
                coordinator.Metrics.QueueDepth == 0,
            TimeSpan.FromSeconds(2),
            "Next-drain drift did not settle on the real latest source.")
            .ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        AssertEx.Equal(latestSource, target.RequestedWrites[^1].Value);
        AssertEx.False(
            target.RequestedWrites.Any(write => write.Value == initial || write.Value == targetDrift),
            "A later drift iteration restored an older synthetic source value.");
    }

    private static async Task InterruptedRuntimeWorkIsRequeuedAsync()
    {
        var source = Session(
            ("/ch/1/eq/g", WingValue.FromFloat(0F)),
            ("/ch/1/eq/1", WingValue.FromFloat(0F)));
        var target = Session(
            ("/ch/2/eq/g", WingValue.FromFloat(0F)),
            ("/ch/2/eq/1", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        var injected = 0;
        observer.ParameterActionRecorded = action =>
        {
            if (action.Action == "write" &&
                action.TargetToken == "/ch/2/eq/g" &&
                Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                source.ChangeFromConsole("/ch/1/eq/1", WingValue.FromFloat(7F));
            }
        };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        source.ChangeFromConsole("/ch/1/eq/g", WingValue.FromFloat(5F));

        await AssertEx.EventuallyAsync(
                () =>
                    target.GetValue("/ch/2/eq/g") == WingValue.FromFloat(5F) &&
                    target.GetValue("/ch/2/eq/1") == WingValue.FromFloat(7F),
                TimeSpan.FromSeconds(1),
                "Interrupted work did not requeue and converge before the health poll.")
            .ConfigureAwait(false);
        AssertEx.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            "Convergence waited for periodic polling instead of the immediate requeue.");
        AssertEx.Equal(SyncCoordinatorState.RunningLive, coordinator.Status.State);
    }

    private static async Task ReconnectDuringWorkerBatchIsSerializedAsync()
    {
        var latest = WingValue.FromFloat(4F);
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        var operationProbe = new ReconciliationOperationProbe();
        source.OperationProbe = operationProbe;
        target.OperationProbe = operationProbe;
        var setManyGate = new SetManyGate();
        target.BeforeSetManyAsync = setManyGate.OnBeforeSetManyAsync;
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.ChangeFromConsole("/ch/1/eq/g", latest);
        await setManyGate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        source.DropConnection("reconnect while worker SetMany is gated");
        await Task.Delay(650).ConfigureAwait(false);
        AssertEx.False(
            operationProbe.SnapshotSetManyOverlapDetected,
            "Reconnect snapshot overlapped the active worker SetMany.");
        setManyGate.Release();

        await AssertEx.EventuallyAsync(
            () =>
                coordinator.Status.State == SyncCoordinatorState.RunningLive &&
                coordinator.Metrics.ReconnectCount == 1,
            TimeSpan.FromSeconds(4),
            "Serialized reconnect did not finish after the worker batch released.")
            .ConfigureAwait(false);
        AssertEx.False(operationProbe.SnapshotSetManyOverlapDetected);
        AssertEx.Equal(1, operationProbe.MaximumConcurrentSetMany);
        AssertEx.Equal(1, target.RequestedWrites.Count);
        AssertEx.Equal(latest, target.RequestedWrites[0].Value);
    }

    private static async Task ReconnectDuringConfirmationIsSerializedAsync()
    {
        var authoritative = WingValue.FromFloat(3F);
        var source = Session(("/ch/1/eq/g", authoritative));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        var operationProbe = new ReconciliationOperationProbe();
        source.OperationProbe = operationProbe;
        target.OperationProbe = operationProbe;
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(
                    dryRun: false,
                    initialSync: InitialSync.RequireConfirmation),
                false,
                CancellationToken.None)
            .ConfigureAwait(false);

        var snapshotGate = new SnapshotGate("/ch/1/eq", invocation: 3);
        source.SnapshotCapturedAsync = snapshotGate.OnCapturedAsync;
        var confirmation = coordinator.ConfirmInitialSyncAsync(CancellationToken.None);
        await snapshotGate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        source.DropConnection("reconnect while Confirm snapshot is gated");
        await Task.Delay(650).ConfigureAwait(false);
        AssertEx.False(
            operationProbe.SnapshotSetManyOverlapDetected,
            "Reconnect reconciliation overlapped the active confirmation snapshot.");
        snapshotGate.Release();
        await AssertEx.ThrowsAsync<InvalidOperationException>(() => confirmation)
            .ConfigureAwait(false);

        await AssertEx.EventuallyAsync(
            () =>
                coordinator.Status.State == SyncCoordinatorState.AwaitingConfirmation &&
                source.ConnectCount >= 2 &&
                target.ConnectCount >= 2,
            TimeSpan.FromSeconds(4),
            "Reconnect did not resnapshot safely after confirmation lost its connection.")
            .ConfigureAwait(false);
        AssertEx.False(operationProbe.SnapshotSetManyOverlapDetected);
        AssertEx.True(operationProbe.MaximumConcurrentSetMany <= 1);
        AssertEx.Equal(
            0,
            target.RequestedWrites.Count,
            "A failed confirmation must not dispatch before the reconnect preview is reconfirmed.");

        await coordinator.ConfirmInitialSyncAsync(CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(1, target.RequestedWrites.Count);
        AssertEx.Equal(authoritative, target.RequestedWrites[0].Value);
    }

    private static async Task InitialGenerationChangeBeforeSetManyAsync()
    {
        var stalePlanValue = WingValue.FromFloat(1F);
        var latestSource = WingValue.FromFloat(2F);
        var source = Session(("/ch/1/eq/g", stalePlanValue));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(
                    dryRun: false,
                    initialSync: InitialSync.RequireConfirmation),
                false,
                CancellationToken.None)
            .ConfigureAwait(false);

        var injected = 0;
        observer.ParameterActionRecorded = action =>
        {
            if (action.Action == "write" &&
                Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                source.ChangeFromConsole("/ch/1/eq/g", latestSource);
            }
        };

        await AssertEx.ThrowsAsync<InitialSyncPreviewChangedException>(
                () => coordinator.ConfirmInitialSyncAsync(CancellationToken.None))
            .ConfigureAwait(false);
        observer.ParameterActionRecorded = null;
        AssertEx.Equal(SyncCoordinatorState.AwaitingConfirmation, coordinator.Status.State);
        AssertEx.Equal(
            0,
            target.RequestedWrites.Count,
            "The initial plan was dispatched after its snapshot generation changed.");

        await coordinator.ConfirmInitialSyncAsync(CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(latestSource, target.GetValue("/ch/2/eq/g"));
        AssertEx.Equal(1, target.RequestedWrites.Count);
        AssertEx.Equal(latestSource, target.RequestedWrites[0].Value);
    }

    private static async Task GuardInterruptedBeforeModelRollsBackAsync()
    {
        var source = Session(
            ("/ch/1/eq/on", WingValue.FromInt32(1)),
            ("/ch/1/eq/mdl", WingValue.FromString("SOUL")),
            ("/ch/1/eq/g", WingValue.FromFloat(3F)));
        var target = Session(
            ("/ch/2/eq/on", WingValue.FromInt32(1)),
            ("/ch/2/eq/mdl", WingValue.FromString("STD")),
            ("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(
                    dryRun: false,
                    initialSync: InitialSync.RequireConfirmation),
                false,
                CancellationToken.None)
            .ConfigureAwait(false);

        var injected = 0;
        observer.ParameterActionRecorded = action =>
        {
            if (action.Action == "write" &&
                action.TargetToken == "/ch/2/eq/mdl" &&
                Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                source.ChangeFromConsole("/ch/1/eq/g", WingValue.FromFloat(4F));
            }
        };

        await AssertEx.ThrowsAsync<InitialSyncPreviewChangedException>(
                () => coordinator.ConfirmInitialSyncAsync(CancellationToken.None))
            .ConfigureAwait(false);
        observer.ParameterActionRecorded = null;

        AssertEx.Equal(SyncCoordinatorState.AwaitingConfirmation, coordinator.Status.State);
        AssertEx.Equal(
            WingValue.FromInt32(1),
            target.GetValue("/ch/2/eq/on"),
            "The pre-model guard was not rolled back to the exact target original.");
        AssertEx.False(
            target.RequestedWrites.Any(write => write.TokenPath == "/ch/2/eq/mdl"),
            "The invalidated model batch was dispatched.");
        AssertEx.True(
            observer.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "SAFETY_GUARD_ROLLED_BACK"),
            "The exact guard rollback was not made visible to the operator.");
    }

    private static async Task GuardInterruptedAfterModelPausesAsync()
    {
        var source = Session(
            ("/ch/1/eq/on", WingValue.FromInt32(1)),
            ("/ch/1/eq/mdl", WingValue.FromString("SOUL")),
            ("/ch/1/eq/g", WingValue.FromFloat(3F)));
        var target = Session(
            ("/ch/2/eq/on", WingValue.FromInt32(1)),
            ("/ch/2/eq/mdl", WingValue.FromString("STD")),
            ("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        var injected = 0;
        target.BeforeSetManyAsync = (capture, _) =>
        {
            if (capture.Invocation == 3 &&
                Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                source.ChangeFromConsole("/ch/1/eq/g", WingValue.FromFloat(4F));
            }

            return Task.CompletedTask;
        };
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(
                    dryRun: false,
                    initialSync: InitialSync.RequireConfirmation),
                false,
                CancellationToken.None)
            .ConfigureAwait(false);

        await AssertEx.ThrowsAsync<IOException>(
                () => coordinator.ConfirmInitialSyncAsync(CancellationToken.None))
            .ConfigureAwait(false);

        AssertEx.Equal(SyncCoordinatorState.Paused, coordinator.Status.State);
        AssertEx.Equal(
            WingValue.FromInt32(0),
            target.GetValue("/ch/2/eq/on"),
            "A partially changed processor was unsafely re-enabled.");
        AssertEx.True(
            observer.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "SAFETY_GUARD_RECOVERY_REQUIRED"),
            "The operator did not receive the explicit stranded-guard recovery workflow.");
    }

    private static async Task ReconnectGenerationChangeBeforeSetManyAsync()
    {
        var snapshotValue = WingValue.FromFloat(3F);
        var latestSource = WingValue.FromFloat(4F);
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.SetSilently("/ch/1/eq/g", snapshotValue);
        var injected = 0;
        observer.ParameterActionRecorded = action =>
        {
            if (action.Action == "write" &&
                action.Value == snapshotValue &&
                Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                source.ChangeFromConsole("/ch/1/eq/g", latestSource);
            }
        };
        source.DropConnection("generation change immediately before reconnect SetMany");

        await AssertEx.EventuallyAsync(
            () =>
                coordinator.Status.State == SyncCoordinatorState.RunningLive &&
                coordinator.Metrics.ReconnectCount == 1 &&
                target.GetValue("/ch/2/eq/g") == latestSource,
            TimeSpan.FromSeconds(5),
            "Reconnect did not resnapshot after its generation changed before SetMany.")
            .ConfigureAwait(false);
        observer.ParameterActionRecorded = null;
        AssertEx.False(
            target.RequestedWrites.Any(write => write.Value == snapshotValue),
            "Reconnect dispatched the plan from the invalidated snapshot generation.");
        AssertEx.Equal(latestSource, target.RequestedWrites[^1].Value);
    }

    private static async Task DuplicateReconnectTriggersDoNotStarveSnapshotAsync()
    {
        var latestSource = WingValue.FromFloat(7F);
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.SetSilently("/ch/1/eq/g", latestSource);
        source.SnapshotCapturedAsync = (capture, _) =>
        {
            if (capture.Invocation >= 2)
            {
                for (var signal = 0; signal < 16; signal++)
                {
                    source.EmitStateSignal(
                        WingSessionState.Faulted,
                        $"duplicate health fault {signal}");
                    target.EmitStateSignal(
                        WingSessionState.Disconnected,
                        $"duplicate state disconnect {signal}");
                }
            }

            return Task.CompletedTask;
        };
        source.DropConnection("the one reconnect owner");

        await AssertEx.EventuallyAsync(
            () =>
                coordinator.Status.State == SyncCoordinatorState.RunningLive &&
                coordinator.Metrics.ReconnectCount == 1 &&
                target.GetValue("/ch/2/eq/g") == latestSource,
            TimeSpan.FromSeconds(4),
            "Duplicate reconnect triggers invalidated every active snapshot and starved recovery.")
            .ConfigureAwait(false);

        AssertEx.Equal(
            4,
            source.SnapshotRequests.Count(static token => token == "/ch/1/eq"),
            "The reconnect snapshot was invalidated by a trigger that did not own reconnect.");
        AssertEx.Equal(2, source.ConnectCount);
        AssertEx.Equal(2, target.ConnectCount);
        AssertEx.Equal(1, target.RequestedWrites.Count);
        AssertEx.Equal(latestSource, target.RequestedWrites[0].Value);
        AssertEx.False(
            observer.Diagnostics.Any(diagnostic => diagnostic.Code == "SNAPSHOT_INVALIDATED"),
            "Non-owning reconnect triggers incorrectly changed snapshot transport generation.");
    }

    private static async Task RealTransportLossInvalidatesActiveReconnectAsync()
    {
        var staleSnapshot = WingValue.FromFloat(3F);
        var latestSource = WingValue.FromFloat(8F);
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var observer = new RecordingObserver();
        await using var coordinator = Coordinator(source, target, observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        source.SetSilently("/ch/1/eq/g", staleSnapshot);
        var injected = 0;
        source.SnapshotCapturedAsync = (capture, _) =>
        {
            if (capture.NodeToken == "/ch/1/eq" &&
                capture.Invocation == 3 &&
                Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                source.SetSilently("/ch/1/eq/g", latestSource);
                source.DropConnection("new physical loss during reconnect snapshot");
            }

            return Task.CompletedTask;
        };

        source.DropConnection("initial transport loss");
        await AssertEx.EventuallyAsync(
            () =>
                coordinator.Status.State == SyncCoordinatorState.RunningLive &&
                coordinator.Metrics.ReconnectCount == 1 &&
                target.GetValue("/ch/2/eq/g") == latestSource,
            TimeSpan.FromSeconds(6),
            "A new physical loss did not invalidate and rebuild the active reconnect snapshot.")
            .ConfigureAwait(false);

        AssertEx.False(
            target.RequestedWrites.Any(write => write.Value == staleSnapshot),
            "A plan captured before the second physical loss was dispatched.");
        AssertEx.Equal(latestSource, target.RequestedWrites[^1].Value);
        AssertEx.True(source.ConnectCount >= 3, "The second loss did not establish a new transport.");
    }

    private static async Task StopDuringInitialConfirmationBlocksLaterUnitsAsync()
    {
        var first = WingValue.FromFloat(1F);
        var second = WingValue.FromFloat(2F);
        var zero = WingValue.FromFloat(0F);
        var source = Session(
            ("/ch/1/eq/g", first),
            ("/ch/2/eq/g", second));
        var target = Session(
            ("/ch/11/eq/g", zero),
            ("/ch/12/eq/g", zero));
        var gate = new SetManyGate();
        target.BeforeSetManyAsync = gate.OnBeforeSetManyAsync;
        await using var coordinator = Coordinator(source, target, new RecordingObserver());
        var configuration = new AppConfiguration(
            FohIdentity.Endpoint,
            MonitorIdentity.Endpoint,
            SyncDirection.FohToMonitor,
            InitialSync.RequireConfirmation,
            new SafetySettings(
                dryRun: false,
                requireReadback: true,
                allowHighRiskWrites: false,
                stopOnVerificationFailure: true),
            [SyncScope.Eq],
            new ChannelMapping(
            [
                new InputChannelMapping(1, 11),
                new InputChannelMapping(2, 12),
            ]));

        await coordinator.StartAsync(configuration, false, CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(SyncCoordinatorState.AwaitingConfirmation, coordinator.Status.State);

        var confirmation = coordinator.ConfirmInitialSyncAsync(CancellationToken.None);
        await gate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        AssertEx.Equal(
            SyncCoordinatorState.ApplyingLive,
            coordinator.Status.State,
            "Bevestigde livewrites werden onterecht als Snapshotting gerapporteerd.");

        var snapshotsBeforeRelease = target.SnapshotRequests.Count;
        var stopTask = coordinator.StopAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100).ConfigureAwait(false);
            AssertEx.False(
                stopTask.IsCompleted,
                "Stop retourneerde terwijl de reeds verstuurde transactie nog niet terminaal was.");
            AssertEx.False(
                confirmation.IsCompleted,
                "Bevestiging eindigde terwijl de eerste SetMany nog geblokkeerd was.");
            AssertEx.Equal(0, target.RequestedWrites.Count);

            gate.Release();
            await confirmation.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        AssertEx.Equal(SyncCoordinatorState.Stopped, coordinator.Status.State);
        AssertEx.Equal(1, target.RequestedBatches.Count);
        AssertEx.Equal(1, target.RequestedWrites.Count);
        AssertWrite(target.RequestedWrites[0], "/ch/11/eq/g", first);
        AssertEx.Equal(first, target.GetValue("/ch/11/eq/g"));
        AssertEx.Equal(zero, target.GetValue("/ch/12/eq/g"));
        AssertEx.False(
            target.RequestedWrites.Any(static write =>
                write.TokenPath == "/ch/12/eq/g"),
            "Na de stopaanvraag werd een latere transactie-unit verstuurd.");
        AssertEx.True(
            target.SnapshotRequests
                .Skip(snapshotsBeforeRelease)
                .Any(static token => token == "/ch/11/eq/g"),
            "De reeds verstuurde eerste batch kreeg geen expliciete terminale readback.");
    }

    private static async Task StopDrainsDispatchedWriteThroughReadbackAsync()
    {
        var latest = WingValue.FromFloat(7F);
        var source = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var target = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var gate = new SetManyGate();
        target.BeforeSetManyAsync = gate.OnBeforeSetManyAsync;
        await using var coordinator = Coordinator(source, target, new RecordingObserver());
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        var snapshotsBeforeWrite = target.SnapshotRequests.Count;
        source.ChangeFromConsole("/ch/1/eq/g", latest);
        await gate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var stopTask = coordinator.StopAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100).ConfigureAwait(false);
            AssertEx.False(
                stopTask.IsCompleted,
                "Stop returned while a helper-accepted SetMany was still non-terminal.");
            AssertEx.False(
                coordinator.Status.State == SyncCoordinatorState.Stopped,
                "Coordinator reported Stopped before the dispatched write completed.");
            AssertEx.Equal(
                0,
                target.RequestedWrites.Count,
                "The gated test double applied the write before terminal completion.");

            gate.Release();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        AssertEx.Equal(SyncCoordinatorState.Stopped, coordinator.Status.State);
        AssertEx.Equal(1, target.RequestedWrites.Count);
        AssertEx.Equal(latest, target.GetValue("/ch/2/eq/g"));
        AssertEx.True(
            target.SnapshotRequests
                .Skip(snapshotsBeforeWrite)
                .Any(static token => token == "/ch/2/eq/g"),
            "Stop drained SetMany completion but not its explicit scalar readback.");
    }

    private static async Task StopWaitsForCleanupBeforeRestartAsync()
    {
        var firstSource = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var firstTarget = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        var nextSource = Session(("/ch/1/eq/g", WingValue.FromFloat(0F)));
        var nextTarget = Session(("/ch/2/eq/g", WingValue.FromFloat(0F)));
        foreach (var pair in new[]
                 {
                     (Session: firstSource, Serial: FohIdentity.SerialNumber),
                     (Session: firstTarget, Serial: MonitorIdentity.SerialNumber),
                     (Session: nextSource, Serial: FohIdentity.SerialNumber),
                     (Session: nextTarget, Serial: MonitorIdentity.SerialNumber),
                 })
        {
            pair.Session.SetSilently(
                "/$syscfg/$serial",
                WingValue.FromString(pair.Serial));
        }

        var disconnectEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectReleased = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeReleased = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        firstSource.BeforeDisconnectAsync = _ =>
        {
            disconnectEntered.TrySetResult(true);
            return disconnectReleased.Task;
        };
        firstSource.BeforeDisposeAsync = () =>
        {
            disposeEntered.TrySetResult(true);
            return disposeReleased.Task;
        };

        var observer = new RecordingObserver();
        await using var coordinator = new SyncCoordinator(
            new ScriptedSessionFactory(firstSource, firstTarget, nextSource, nextTarget),
            new StaticWingIdentityVerifier([FohIdentity, MonitorIdentity]),
            new RecordingStateSink(),
            observer);
        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);

        var stopTask = coordinator.StopAsync(CancellationToken.None);
        try
        {
            await disconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);
            AssertEx.False(stopTask.IsCompleted, "Stop returned while DisconnectAsync was still active.");
            AssertEx.False(
                coordinator.Status.State == SyncCoordinatorState.Stopped,
                "Coordinator reported Stopped before DisconnectAsync completed.");

            disconnectReleased.TrySetResult(true);
            await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);
            AssertEx.False(stopTask.IsCompleted, "Stop returned while DisposeAsync was still active.");
            AssertEx.False(
                coordinator.Status.State == SyncCoordinatorState.Stopped,
                "Coordinator reported Stopped before DisposeAsync completed.");

            disposeReleased.TrySetResult(true);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        finally
        {
            disconnectReleased.TrySetResult(true);
            disposeReleased.TrySetResult(true);
        }

        AssertEx.Equal(SyncCoordinatorState.Stopped, coordinator.Status.State);
        AssertEx.Equal(1, firstSource.DisconnectCount);
        AssertEx.Equal(1, firstSource.DisposeCount);

        await coordinator.StartAsync(
                Configuration(dryRun: false, initialSync: InitialSync.SourceWins),
                true,
                CancellationToken.None)
            .ConfigureAwait(false);
        AssertEx.Equal(SyncCoordinatorState.RunningLive, coordinator.Status.State);
        AssertEx.Equal(1, nextSource.ConnectCount);
        AssertEx.Equal(1, nextTarget.ConnectCount);
    }

    private static Task LiveWriteCliFailsClosedAsync()
    {
        const string fohPin = "TEST-FOH-PIN";
        const string stagePin = "TEST-STAGE-PIN";
        AssertEx.Throws<LiveWriteSafetyException>(() =>
            LiveHardwareOptions.Parse(["--live-read-only"]));
        var readOnly = LiveHardwareOptions.Parse(
        [
            "--live-read-only",
            "--foh-serial",
            fohPin,
            "--stage-serial",
            stagePin,
        ]);
        AssertEx.True(readOnly.LiveReadOnly);
        AssertEx.False(readOnly.LiveWrites);
        AssertEx.Throws<LiveWriteSafetyException>(() =>
            LiveHardwareOptions.Parse(
            [
                "--live-read-only",
                "--live-writes",
                "--foh-serial",
                fohPin,
                "--stage-serial",
                stagePin,
            ]));
        AssertEx.Throws<LiveWriteSafetyException>(() =>
            LiveHardwareOptions.Parse(
            [
                "--live-writes",
                "--foh-serial",
                fohPin,
                "--stage-serial",
                stagePin,
            ]));
        AssertEx.Throws<LiveWriteSafetyException>(() =>
            LiveHardwareOptions.Parse(
            [
                "--live-writes",
                "--i-understand-live-writes",
                "--foh-serial",
                fohPin,
                "--stage-serial",
                fohPin,
            ]));
        var accepted = LiveHardwareOptions.Parse(
        [
            "--live-writes",
            "--i-understand-live-writes",
            "--foh-serial",
            fohPin,
            "--stage-serial",
            stagePin,
        ]);
        AssertEx.True(accepted.LiveWrites);
        AssertEx.True(accepted.UnderstandsLiveWrites);
        return Task.CompletedTask;
    }

    private static async Task LiveWriteGuardRejectsForbiddenScopesAsync()
    {
        var inner = Session(("/ch/40/fdr", WingValue.FromFloat(0F)));
        await inner.ConnectAsync(FohIdentity.Endpoint, CancellationToken.None).ConfigureAwait(false);
        await using var guarded = new GuardedLiveSession(
            inner,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "/ch/40/eq/g",
            });

        await guarded.SetAsync(
            "/ch/40/eq/g",
            WingValue.FromFloat(0.1F),
            CancellationToken.None).ConfigureAwait(false);
        var forbidden = new[]
        {
            "/ch/40/in/set/trim",
            "/ch/40/main/1/on",
            "/ch/40/send/1/on",
            "/ch/40/fdr",
            "/ch/40/mute",
            "/ch/40/eq/on",
            "/ch/40/eq/mdl",
            "/ch/40/gate/on",
            "/ch/40/dyn/mdl",
            "/ch/39/eq/g",
            "/ch/40/gate/thr",
        };
        foreach (var token in forbidden)
        {
            await AssertEx.ThrowsAsync<LiveWriteSafetyException>(() =>
                guarded.SetAsync(
                    token,
                    WingValue.FromFloat(0F),
                    CancellationToken.None)).ConfigureAwait(false);
        }

        AssertEx.Equal(1, inner.RequestedWrites.Count);
    }

    private static Task LivePreflightFailsClosedAsync()
    {
        var foh = SilentChannel40Snapshot();
        var stage = SilentChannel40Snapshot();
        var plan = Channel40Preflight.ValidateAndPlan(foh, stage);
        AssertEx.Equal(4, plan.Count);
        AssertEx.True(plan.Any(static mutation => mutation.TokenPath == "/ch/40/name"));
        AssertEx.True(plan.Any(static mutation => mutation.TokenPath == "/ch/40/eq/g"));
        AssertEx.True(plan.Any(static mutation => mutation.TokenPath == "/ch/40/gate/thr"));
        AssertEx.True(plan.Any(static mutation => mutation.TokenPath == "/ch/40/dyn/thr"));

        var defaultMainAssignment = foh
            .Select(parameter =>
                parameter.TokenPath == "/ch/40/main/1/on"
                    ? parameter with { Value = WingValue.FromInt32(1) }
                    : parameter)
            .ToArray();
        AssertEx.Equal(
            4,
            Channel40Preflight.ValidateAndPlan(defaultMainAssignment, stage).Count,
            "MAIN 1 alone may remain assigned only while the independent source-off and -inf checks pass.");

        var numericFoh = UseNumericProcessorLeaves(foh);
        var numericStage = UseNumericProcessorLeaves(stage);
        var numericPlan = Channel40Preflight.ValidateAndPlan(numericFoh, numericStage);
        AssertEx.True(numericPlan.Any(static mutation => mutation.TokenPath == "/ch/40/eq/1"));
        AssertEx.True(numericPlan.Any(static mutation => mutation.TokenPath == "/ch/40/gate/1"));
        AssertEx.True(numericPlan.Any(static mutation => mutation.TokenPath == "/ch/40/dyn/1"));

        var channel17Foh = Rechannel(foh, 17);
        var channel17Stage = Rechannel(stage, 17);
        var channel17Plan = Channel40Preflight.ValidateAndPlan(
            channel17Foh,
            channel17Stage,
            17);
        AssertEx.Equal(4, channel17Plan.Count);
        AssertEx.True(
            channel17Plan.All(static mutation =>
                mutation.TokenPath.StartsWith("/ch/17/", StringComparison.Ordinal)));

        var audibleFader = foh
            .Select(parameter =>
                parameter.TokenPath == "/ch/40/fdr"
                    ? parameter with { Value = WingValue.FromFloat(0F) }
                    : parameter)
            .ToArray();
        AssertEx.Throws<LiveWriteSafetyException>(() =>
            Channel40Preflight.ValidateAndPlan(audibleFader, stage));

        var enabledEq = foh
            .Select(parameter =>
                parameter.TokenPath == "/ch/40/eq/on"
                    ? parameter with { Value = WingValue.FromInt32(1) }
                    : parameter)
            .ToArray();
        AssertEx.Throws<LiveWriteSafetyException>(() =>
            Channel40Preflight.ValidateAndPlan(enabledEq, stage));

        var missingMatrix = foh
            .Where(static parameter => parameter.TokenPath != "/ch/40/send/mx8/on")
            .ToArray();
        AssertEx.Throws<LiveWriteSafetyException>(() =>
            Channel40Preflight.ValidateAndPlan(missingMatrix, stage));
        return Task.CompletedTask;
    }

    private static WingParameter[] Rechannel(
        IEnumerable<WingParameter> parameters,
        int channel) =>
        parameters
            .Select(parameter =>
                parameter with
                {
                    TokenPath = parameter.TokenPath.Replace(
                        "/ch/40/",
                        $"/ch/{channel}/",
                        StringComparison.Ordinal),
                })
            .ToArray();

    private static WingParameter[] UseNumericProcessorLeaves(
        IEnumerable<WingParameter> parameters) =>
        parameters
            .Select(parameter =>
                parameter with
                {
                    TokenPath = parameter.TokenPath switch
                    {
                        "/ch/40/eq/g" => "/ch/40/eq/1",
                        "/ch/40/gate/thr" => "/ch/40/gate/1",
                        "/ch/40/dyn/thr" => "/ch/40/dyn/1",
                        _ => parameter.TokenPath,
                    },
                })
            .ToArray();

    private static WingParameter[] SilentChannel40Snapshot()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var values = new Dictionary<string, WingValue>(StringComparer.Ordinal)
        {
            ["/ch/40/in/conn/grp"] = WingValue.FromString("OFF"),
            ["/ch/40/in/conn/altgrp"] = WingValue.FromString("OFF"),
            ["/ch/40/fdr"] = WingValue.FromFloat(-144F),
            ["/ch/40/eq/on"] = WingValue.FromInt32(0),
            ["/ch/40/gate/on"] = WingValue.FromInt32(0),
            ["/ch/40/dyn/on"] = WingValue.FromInt32(0),
            ["/ch/40/name"] = WingValue.FromString(string.Empty),
            ["/ch/40/eq/g"] = WingValue.FromFloat(0F),
            ["/ch/40/gate/thr"] = WingValue.FromFloat(-40F),
            ["/ch/40/dyn/thr"] = WingValue.FromFloat(-20F),
        };
        for (var main = 1; main <= 4; main++)
        {
            values[$"/ch/40/main/{main}/on"] = WingValue.FromInt32(0);
        }

        for (var send = 1; send <= 16; send++)
        {
            values[$"/ch/40/send/{send}/on"] = WingValue.FromInt32(0);
        }

        for (var matrix = 1; matrix <= 8; matrix++)
        {
            values[$"/ch/40/send/mx{matrix}/on"] = WingValue.FromInt32(0);
        }

        return values
            .Select(pair => new WingParameter(pair.Key, pair.Value, observedAt))
            .ToArray();
    }

    private static ScriptedWingSession Session(
        params (string Token, WingValue Value)[] values) =>
        new(values.ToDictionary(
            static pair => pair.Token,
            static pair => pair.Value,
            StringComparer.Ordinal));

    private static WingParameter[] ReplaceSnapshotValue(
        IReadOnlyList<WingParameter> values,
        string tokenPath,
        WingValue staleValue) =>
        values.Select(parameter =>
                parameter.TokenPath.Equals(tokenPath, StringComparison.Ordinal)
                    ? new WingParameter(
                        parameter.TokenPath,
                        staleValue,
                        parameter.ObservedAt)
                    : parameter)
            .ToArray();

    private static SyncCoordinator Coordinator(
        ScriptedWingSession source,
        ScriptedWingSession target,
        RecordingObserver observer,
        RecordingStateSink? stateSink = null,
        IClock? clock = null)
    {
        source.SeedSilently(
            "/$syscfg/$serial",
            WingValue.FromString(FohIdentity.SerialNumber));
        target.SeedSilently(
            "/$syscfg/$serial",
            WingValue.FromString(MonitorIdentity.SerialNumber));
        return new SyncCoordinator(
            new ScriptedSessionFactory(source, target),
            new StaticWingIdentityVerifier([FohIdentity, MonitorIdentity]),
            stateSink ?? new RecordingStateSink(),
            observer,
            clock);
    }

    private static AppConfiguration Configuration(
        int sourceChannel = 1,
        int targetChannel = 2,
        IReadOnlyList<SyncScope>? scopes = null,
        bool dryRun = false,
        bool requireReadback = true,
        InitialSync initialSync = InitialSync.SourceWins) =>
        new(
            FohIdentity.Endpoint,
            MonitorIdentity.Endpoint,
            SyncDirection.FohToMonitor,
            initialSync,
            new SafetySettings(
                dryRun: dryRun,
                requireReadback: requireReadback,
                allowHighRiskWrites: false,
                stopOnVerificationFailure: true),
            scopes ?? [SyncScope.Eq],
            new ChannelMapping([new InputChannelMapping(sourceChannel, targetChannel)]));

    private static void AssertWrite(
        WingWriteRequest actual,
        string expectedToken,
        WingValue expectedValue)
    {
        AssertEx.Equal(expectedToken, actual.TokenPath);
        AssertEx.Equal(expectedValue, actual.Value);
    }

    private sealed record TestCase(string Name, Func<Task> Body);
}

internal static class AssertEx
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "Expected condition to be true.");
        }
    }

    public static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected condition to be false.");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                message ?? $"Expected '{expected}', received '{actual}'.");
        }
    }

    public static async Task EventuallyAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        string message)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException(message);
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected exception {typeof(TException).Name} was not thrown.");
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected exception {typeof(TException).Name} was not thrown.");
    }
}
