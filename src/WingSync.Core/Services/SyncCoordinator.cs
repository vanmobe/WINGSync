using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Channels;
using WingSync.Core.Abstractions;
using WingSync.Core.Domain;
using WingSync.Core.Planning;

namespace WingSync.Core.Services;

/// <summary>Lifecycle state shown by the UI and used to gate every write.</summary>
public enum SyncCoordinatorState
{
    /// <summary>No session is active.</summary>
    Stopped,

    /// <summary>Console identity and WAPI connections are being established.</summary>
    Connecting,

    /// <summary>Both consoles are being read into a fresh connection epoch.</summary>
    Snapshotting,

    /// <summary>A live initial difference set is waiting for explicit confirmation.</summary>
    AwaitingConfirmation,

    /// <summary>A confirmed live plan is actively writing and reading back console state.</summary>
    ApplyingLive,

    /// <summary>Events are evaluated and logged without console writes.</summary>
    RunningDryRun,

    /// <summary>Events are actively synchronized and verified.</summary>
    RunningLive,

    /// <summary>Writes are paused while both identities and states are rebuilt.</summary>
    Reconnecting,

    /// <summary>A safety failure blocks further writes until the operator restarts.</summary>
    Paused,

    /// <summary>An unrecoverable startup or lifecycle error occurred.</summary>
    Faulted,
}

/// <summary>Immutable coordinator state transition.</summary>
/// <param name="State">Current state.</param>
/// <param name="Detail">Concise operator-readable detail.</param>
/// <param name="ChangedAt">UTC transition timestamp.</param>
public sealed record SyncCoordinatorStatus(
    SyncCoordinatorState State,
    string Detail,
    DateTimeOffset ChangedAt);

/// <summary>Bounded runtime counters displayed on the status page.</summary>
/// <param name="SynchronizedWrites">Successfully verified live writes.</param>
/// <param name="PreviewedWrites">Dry-run or confirmation previews.</param>
/// <param name="BlockedWrites">Writes blocked by scope or safety policy.</param>
/// <param name="QueueDepth">Current event-queue depth.</param>
/// <param name="ReconnectCount">Successful reconnect epochs.</param>
/// <param name="P95LatencyMilliseconds">Recent verified-write p95 latency.</param>
public sealed record SyncMetrics(
    long SynchronizedWrites,
    long PreviewedWrites,
    long BlockedWrites,
    int QueueDepth,
    long ReconnectCount,
    double P95LatencyMilliseconds);

/// <summary>Operator-facing summary of a freshly calculated initial live difference set.</summary>
/// <param name="TotalChanges">All changed target parameters, including safety-blocked values.</param>
/// <param name="ExecutableChanges">Changes that will be written after confirmation.</param>
/// <param name="BlockedChanges">Changes retained as audit-only because of safety policy.</param>
/// <param name="ChangesByScope">Changed parameter count grouped by global WING scope.</param>
/// <param name="GeneratedAt">When both fresh console snapshots had been compared.</param>
public sealed record InitialSyncPreviewSummary(
    int TotalChanges,
    int ExecutableChanges,
    int BlockedChanges,
    IReadOnlyDictionary<SyncScope, int> ChangesByScope,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Coordinates two isolated WAPI sessions, fresh snapshots, scoped planning, verified
/// writes, bounded event coalescing, and fail-closed reconnects.
/// </summary>
public sealed class SyncCoordinator : IAsyncDisposable
{
    private const int QueueCapacity = 4_096;
    private const int SnapshotStabilityAttempts = 5;
    private const int ReconciliationRootBudget = 2;
    private const int ReconciliationFallbackNodeBudget = 8;
    private const int ReconciliationCandidateBudget = 4;
    private const int ReconciliationScalarBudget = 8;
    private const int TargetReconciliationScalarBudget = 8;
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconciliationTimeBudget = TimeSpan.FromMilliseconds(1_200);
    private static readonly string[] DelayTupleLeaves = ["dlymode", "dly", "dlyon"];

    private readonly IWingSessionFactory sessionFactory;
    private readonly IWingIdentityVerifier identityVerifier;
    private readonly IWingStateSink stateSink;
    private readonly ISyncObserver observer;
    private readonly IClock clock;
    private readonly WritePlanner planner = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim reconcileGate = new(1, 1);
    private readonly ConcurrentDictionary<string, WingValue> sourceState =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WingValue> targetState =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DesiredTarget> desiredTargets =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReadbackWaiter> readbackWaiters =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EchoFingerprint> echoFingerprints =
        new(StringComparer.Ordinal);
    private readonly object eventIntakeSync = new();
    private readonly Dictionary<string, BufferedParameterEvent> deferredSourceEvents =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, BufferedParameterEvent> deferredTargetEvents =
        new(StringComparer.Ordinal);
    private readonly object transportStateSync = new();
    private readonly HashSet<IWingSession> faultedTransportSessions =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IWingSession, Task> pendingDisconnectTasks =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IWingSession, Task> pendingDisposeTasks =
        new(ReferenceEqualityComparer.Instance);
    private readonly object latencySync = new();
    private readonly Queue<double> recentLatencies = new();

    private CancellationTokenSource? sessionCancellation;
    private Channel<EngineWork>? workChannel;
    private Task? workerTask;
    private Task? healthTask;
    private Task? reconnectTask;
    private IWingSession? fohSession;
    private IWingSession? monitorSession;
    private IWingSession? sourceSession;
    private IWingSession? targetSession;
    private SafetyGuardExecutionContext? activeSafetyTransaction;
    private AppConfiguration? configuration;
    private DiscoveredWing? fohIdentity;
    private DiscoveredWing? monitorIdentity;
    private WritePlan? pendingInitialPlan;
    private InitialSyncPreviewSummary? pendingInitialPreview;
    private SyncCoordinatorStatus status;
    private long activeEpoch;
    private long sourceRevision;
    private long synchronizedWrites;
    private long previewedWrites;
    private long blockedWrites;
    private long reconnectCount;
    private long sourceEventSequence;
    private long targetEventSequence;
    private long transportGeneration;
    private int queueDepth;
    private int reconnectStarted;
    private int reconciliationRootCursor;
    private int reconciliationFallbackCursor;
    private int reconciliationCandidateCursor;
    private int reconciliationProbeCursor;
    private int reconciliationScalarCursor;
    private int reconciliationPollReported;
    private int reconciliationBudgetWarningRaised;
    private int targetReconciliationCriticalCursor;
    private int targetReconciliationGeneralCursor;
    private int targetReconciliationBudgetWarningRaised;
    private volatile bool acceptEvents;
    private volatile bool liveWritesArmed;
    private volatile bool safetyPaused;
    private volatile bool stopping;
    private long stopRequestSequence;
    private long completedStopRequestSequence;
    private bool disposed;

    /// <summary>Initializes a coordinator with explicit production or simulator adapters.</summary>
    public SyncCoordinator(
        IWingSessionFactory sessionFactory,
        IWingIdentityVerifier identityVerifier,
        IWingStateSink? stateSink,
        ISyncObserver observer,
        IClock? clock = null)
    {
        this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        this.identityVerifier = identityVerifier ?? throw new ArgumentNullException(nameof(identityVerifier));
        this.stateSink = stateSink ?? new NullWingStateSink();
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.clock = clock ?? new SystemClock();
        status = new SyncCoordinatorStatus(
            SyncCoordinatorState.Stopped,
            "Not started.",
            this.clock.UtcNow);
    }

    /// <summary>Raised on every lifecycle state transition.</summary>
    public event EventHandler<SyncCoordinatorStatus>? StatusChanged;

    /// <summary>Raised when bounded runtime counters change.</summary>
    public event EventHandler<SyncMetrics>? MetricsChanged;

    /// <summary>Gets the latest lifecycle state.</summary>
    public SyncCoordinatorStatus Status => status;

    /// <summary>Gets the exact fresh diff summary awaiting live confirmation, if any.</summary>
    public InitialSyncPreviewSummary? PendingInitialPreview => pendingInitialPreview;

    /// <summary>Gets a consistent snapshot of runtime counters.</summary>
    public SyncMetrics Metrics => new(
        Interlocked.Read(ref synchronizedWrites),
        Interlocked.Read(ref previewedWrites),
        Interlocked.Read(ref blockedWrites),
        Volatile.Read(ref queueDepth),
        Interlocked.Read(ref reconnectCount),
        CalculateP95Latency());

    /// <summary>
    /// Verifies both identities, connects both helpers, reads fresh state, and starts the
    /// event pipeline. Live writes always require <paramref name="confirmLiveInitialSync"/>.
    /// </summary>
    public async Task StartAsync(
        AppConfiguration configuration,
        bool confirmLiveInitialSync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ThrowIfDisposed();
        var startStopFence = Interlocked.Read(ref stopRequestSequence);

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopInternalAsync(CancellationToken.None).ConfigureAwait(false);
            ThrowIfStopRequested(
                startStopFence,
                "A stop request interrupted startup before consoles were opened.");
            var validation = ConfigValidator.Validate(configuration);
            if (!validation.IsValid)
            {
                throw new ConfigurationException(validation);
            }

            this.configuration = configuration;
            liveWritesArmed = !configuration.Safety.DryRun && confirmLiveInitialSync;
            safetyPaused = false;
            sessionCancellation = new CancellationTokenSource();
            workChannel = Channel.CreateBounded<EngineWork>(
                new BoundedChannelOptions(QueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false,
                });

            fohSession = sessionFactory.Create("FOH");
            monitorSession = sessionFactory.Create("Stage");
            ConfigureRoles(configuration);
            SubscribeSessions();
            Interlocked.Increment(ref transportGeneration);
            await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ChangeStatus(SyncCoordinatorState.Connecting, "Identifying and connecting consoles.");

                await VerifyIdentitiesAsync(cancellationToken).ConfigureAwait(false);
                await ConnectBothAsync(cancellationToken).ConfigureAwait(false);
                await VerifyConnectedIdentitiesAsync(cancellationToken).ConfigureAwait(false);
                await BeginCacheEpochAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfStopRequested(
                    startStopFence,
                    "A stop request interrupted startup after the connection check.");

                ChangeStatus(SyncCoordinatorState.Snapshotting, "Reading fresh scope snapshots.");
                var initialSnapshot = await BuildFreshPlanAsync(cancellationToken).ConfigureAwait(false);
                var initialPlan = initialSnapshot.Plan;
                pendingInitialPlan = initialPlan;
                RecordPlanningFindings(initialPlan);
                ThrowIfStopRequested(
                    startStopFence,
                    "A stop request interrupted startup after the first snapshot.");

                workerTask = ProcessWorkAsync(sessionCancellation.Token);
                healthTask = HealthLoopAsync(sessionCancellation.Token);

                if (!configuration.Safety.DryRun && !liveWritesArmed)
                {
                    SuspendEventIntake();
                    pendingInitialPreview = CreatePreviewSummary(initialPlan);
                    await PreviewPlanAsync(initialPlan).ConfigureAwait(false);
                    ChangeStatus(
                        SyncCoordinatorState.AwaitingConfirmation,
                        $"{initialPlan.ExecutableWrites.Count} initial changes are waiting for confirmation.");
                    return;
                }

                if (!configuration.Safety.DryRun &&
                    (!IsSnapshotCurrent(initialSnapshot) || initialSnapshot.WasInvalidated))
                {
                    liveWritesArmed = false;
                    pendingInitialPreview = CreatePreviewSummary(initialPlan);
                    await PreviewPlanAsync(initialPlan).ConfigureAwait(false);
                    ChangeStatus(
                        SyncCoordinatorState.AwaitingConfirmation,
                        "State changed during the first snapshot; review the fresh diff.");
                    return;
                }

                if (!configuration.Safety.DryRun &&
                    configuration.InitialSync == InitialSync.PreviewOnly)
                {
                    await PreviewPlanAsync(initialPlan).ConfigureAwait(false);
                }
                else
                {
                    ThrowIfStopRequested(
                        startStopFence,
                        "A stop request blocked the initial live execution.");
                    if (!configuration.Safety.DryRun &&
                        initialPlan.ExecutableWrites.Count > 0)
                    {
                        ChangeStatus(
                            SyncCoordinatorState.ApplyingLive,
                            "Confirmed live differences are being written and read back.");
                    }

                    await ExecuteOrPreviewPlanAsync(
                            initialPlan,
                            cancellationToken,
                            executionSnapshot: initialSnapshot)
                        .ConfigureAwait(false);
                }

                ThrowIfStopRequested(
                    startStopFence,
                    "A stop request blocked activation of the event stream.");
                pendingInitialPlan = null;
                pendingInitialPreview = null;
                EnableEventIntake(initialSnapshot);
                ChangeStatus(
                    configuration.Safety.DryRun
                        ? SyncCoordinatorState.RunningDryRun
                        : SyncCoordinatorState.RunningLive,
                    configuration.Safety.DryRun
                        ? "Dry run active; no writes are sent."
                        : "Live synchronization active; every write is verified.");
            }
            finally
            {
                reconcileGate.Release();
            }
        }
        catch (ReconciliationInterruptedException) when (
            StopWasRequested(startStopFence))
        {
            liveWritesArmed = false;
            SuspendEventIntake();
        }
        catch
        {
            ChangeStatus(SyncCoordinatorState.Faulted, "Startup failed; no writes are active.");
            await StopInternalAsync(CancellationToken.None, preserveFaultedStatus: true).ConfigureAwait(false);
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>Confirms and applies the already-previewed fresh initial difference set.</summary>
    public async Task ConfirmInitialSyncAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var confirmationStopFence = Interlocked.Read(ref stopRequestSequence);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var reconcileEntered = false;
        try
        {
            ThrowIfStopRequested(
                confirmationStopFence,
                "A stop request blocked live confirmation.");
            await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            reconcileEntered = true;
            ThrowIfStopRequested(
                confirmationStopFence,
                "A stop request blocked live confirmation.");
            if (Status.State != SyncCoordinatorState.AwaitingConfirmation ||
                pendingInitialPlan is null ||
                configuration is null)
            {
                throw new InvalidOperationException("No initial synchronization is waiting for confirmation.");
            }

            ChangeStatus(
                SyncCoordinatorState.Snapshotting,
                "After confirmation, reread both consoles.");
            var freshSnapshot = await BuildFreshPlanAsync(cancellationToken).ConfigureAwait(false);
            var freshPlan = freshSnapshot.Plan;
            RecordPlanningFindings(freshPlan);
            ThrowIfStopRequested(
                confirmationStopFence,
                "A stop request interrupted the fresh live check.");
            if (freshSnapshot.WasInvalidated ||
                !PlansAreEquivalent(pendingInitialPlan, freshPlan) ||
                !IsSnapshotCurrent(freshSnapshot))
            {
                if (!IsSnapshotCurrent(freshSnapshot))
                {
                    freshSnapshot = await BuildFreshPlanAsync(cancellationToken).ConfigureAwait(false);
                    freshPlan = freshSnapshot.Plan;
                    RecordPlanningFindings(freshPlan);
                }

                pendingInitialPlan = freshPlan;
                pendingInitialPreview = CreatePreviewSummary(freshPlan);
                liveWritesArmed = false;
                await PreviewPlanAsync(freshPlan).ConfigureAwait(false);
                ChangeStatus(
                    SyncCoordinatorState.AwaitingConfirmation,
                    "The consoles changed since the preview; review and confirm the new diff.");
                throw new InitialSyncPreviewChangedException(pendingInitialPreview);
            }

            ThrowIfStopRequested(
                confirmationStopFence,
                "A stop request blocked enabling live writes.");
            liveWritesArmed = true;
            if (!IsSnapshotCurrent(freshSnapshot))
            {
                freshSnapshot = await BuildFreshPlanAsync(cancellationToken).ConfigureAwait(false);
                freshPlan = freshSnapshot.Plan;
                RecordPlanningFindings(freshPlan);
                pendingInitialPlan = freshPlan;
                pendingInitialPreview = CreatePreviewSummary(freshPlan);
                liveWritesArmed = false;
                await PreviewPlanAsync(freshPlan).ConfigureAwait(false);
                ChangeStatus(
                    SyncCoordinatorState.AwaitingConfirmation,
                    "The consoles changed right before execution; review the new diff.");
                throw new InitialSyncPreviewChangedException(pendingInitialPreview);
            }

            if (configuration.InitialSync == InitialSync.PreviewOnly)
            {
                await PreviewPlanAsync(freshPlan).ConfigureAwait(false);
            }
            else
            {
                ThrowIfStopRequested(
                    confirmationStopFence,
                    "A stop request blocked the initial live execution.");
                if (freshPlan.ExecutableWrites.Count > 0)
                {
                    ChangeStatus(
                        SyncCoordinatorState.ApplyingLive,
                        "Confirmed live differences are being written and read back.");
                }

                await ExecutePlanAsync(
                        freshPlan,
                        cancellationToken,
                        executionSnapshot: freshSnapshot)
                    .ConfigureAwait(false);
            }

            ThrowIfStopRequested(
                confirmationStopFence,
                "A stop request blocked activation of the event stream.");
            pendingInitialPlan = null;
            pendingInitialPreview = null;
            EnableEventIntake(freshSnapshot);
            ChangeStatus(
                SyncCoordinatorState.RunningLive,
                "Live synchronization active; every write is verified.");
        }
        catch (ReconciliationInterruptedException) when (
            StopWasRequested(confirmationStopFence))
        {
            liveWritesArmed = false;
            SuspendEventIntake();
        }
        catch (SnapshotChangedBeforeWriteException exception)
        {
            var replacement = await RefreshPendingPreviewAfterFailureAsync(
                    exception.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            throw new InitialSyncPreviewChangedException(replacement);
        }
        catch (WriteVerificationException)
        {
            // VerifyBatchAsync already latched the coordinator in Paused.
            throw;
        }
        catch (SafetyGuardRecoveryException)
        {
            // ExecutePlanAsync already latched a critical Paused state with an
            // explicit operator recovery workflow.
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            liveWritesArmed = false;
            SuspendEventIntake();
            if (!stopping)
            {
                ChangeStatus(
                    SyncCoordinatorState.AwaitingConfirmation,
                    "Live confirmation was canceled; no writes are active.");
            }

            throw;
        }
        catch (Exception exception)
        {
            liveWritesArmed = false;
            SuspendEventIntake();
            ChangeStatus(
                SyncCoordinatorState.AwaitingConfirmation,
                "The fresh check failed; no writes are active. Try again.");
            Record(
                DiagnosticSeverity.Error,
                "INITIAL_CONFIRM_FAILED",
                exception.Message,
                "Synchronization");
            throw;
        }
        finally
        {
            if (reconcileEntered)
            {
                reconcileGate.Release();
            }

            lifecycleGate.Release();
        }
    }

    private async Task<InitialSyncPreviewSummary> RefreshPendingPreviewAfterFailureAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        liveWritesArmed = false;
        SuspendEventIntake();
        try
        {
            var replacement = await BuildFreshPlanAsync(cancellationToken).ConfigureAwait(false);
            pendingInitialPlan = replacement.Plan;
            pendingInitialPreview = CreatePreviewSummary(replacement.Plan);
            RecordPlanningFindings(replacement.Plan);
            await PreviewPlanAsync(replacement.Plan).ConfigureAwait(false);
        }
        catch (Exception refreshException) when (
            refreshException is not OperationCanceledException)
        {
            Record(
                DiagnosticSeverity.Error,
                "INITIAL_PREVIEW_REFRESH_FAILED",
                refreshException.Message,
                "Synchronization");
        }

        pendingInitialPreview ??= CreatePreviewSummary(
            pendingInitialPlan ??
            new WritePlan([], [], 0));
        ChangeStatus(
            SyncCoordinatorState.AwaitingConfirmation,
            $"{reason} Review the new preview once both consoles are stable.");
        return pendingInitialPreview;
    }

    /// <summary>Stops event processing and closes both isolated helper processes.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (disposed)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stopRequest = Interlocked.Increment(ref stopRequestSequence);
        stopping = true;
        workChannel?.Writer.TryComplete();

        // Once the operator requests Stop, cleanup is deliberately non-cancellable:
        // a transaction that has crossed helper dispatch must finish readback or its
        // safety recovery before either WAPI helper is closed.
        await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopInternalAsync(CancellationToken.None).ConfigureAwait(false);
            AdvanceCompletedStopSequence(stopRequest);
        }
        finally
        {
            RefreshStoppingFlag();
            lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        disposed = true;
        lifecycleGate.Dispose();
        reconcileGate.Dispose();
    }

    private void ConfigureRoles(AppConfiguration currentConfiguration)
    {
        if (fohSession is null || monitorSession is null)
        {
            throw new InvalidOperationException("Sessions have not been created yet.");
        }

        switch (currentConfiguration.Direction)
        {
            case SyncDirection.FohToMonitor:
                sourceSession = fohSession;
                targetSession = monitorSession;
                break;
            case SyncDirection.MonitorToFoh:
                sourceSession = monitorSession;
                targetSession = fohSession;
                break;
            default:
                throw new InvalidOperationException(
                    "Only an explicit one-way synchronization can be started.");
        }
    }

    private void SubscribeSessions()
    {
        if (fohSession is null || monitorSession is null)
        {
            return;
        }

        fohSession.ParameterChanged += OnFohParameterChanged;
        monitorSession.ParameterChanged += OnMonitorParameterChanged;
        fohSession.StateChanged += OnSessionStateChanged;
        monitorSession.StateChanged += OnSessionStateChanged;
    }

    private void UnsubscribeSessions()
    {
        if (fohSession is not null)
        {
            fohSession.ParameterChanged -= OnFohParameterChanged;
            fohSession.StateChanged -= OnSessionStateChanged;
        }

        if (monitorSession is not null)
        {
            monitorSession.ParameterChanged -= OnMonitorParameterChanged;
            monitorSession.StateChanged -= OnSessionStateChanged;
        }
    }

    private async Task VerifyIdentitiesAsync(CancellationToken cancellationToken)
    {
        var currentConfiguration = configuration ??
            throw new InvalidOperationException("Configuration is missing.");
        var fohTask = identityVerifier.VerifyAsync(currentConfiguration.Foh, cancellationToken);
        var monitorTask = identityVerifier.VerifyAsync(currentConfiguration.Monitor, cancellationToken);
        await Task.WhenAll(fohTask, monitorTask).ConfigureAwait(false);
        fohIdentity = await fohTask.ConfigureAwait(false);
        monitorIdentity = await monitorTask.ConfigureAwait(false);
        if (string.Equals(
                fohIdentity.SerialNumber,
                monitorIdentity.SerialNumber,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("FOH and stage refer to the same physical console.");
        }
    }

    private async Task ConnectBothAsync(CancellationToken cancellationToken)
    {
        var currentConfiguration = configuration ??
            throw new InvalidOperationException("Configuration is missing.");
        var currentFoh = fohSession ?? throw new InvalidOperationException("FOH session is missing.");
        var currentMonitor = monitorSession ?? throw new InvalidOperationException("Stage session is missing.");
        await Task.WhenAll(
                currentFoh.ConnectAsync(currentConfiguration.Foh, cancellationToken),
                currentMonitor.ConnectAsync(currentConfiguration.Monitor, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task VerifyConnectedIdentitiesAsync(CancellationToken cancellationToken)
    {
        var currentConfiguration = configuration ??
            throw new InvalidOperationException("Configuration is missing.");
        var currentFoh = fohSession ?? throw new InvalidOperationException("FOH session is missing.");
        var currentMonitor = monitorSession ??
            throw new InvalidOperationException("Stage session is missing.");
        var discoveredFoh = fohIdentity ??
            throw new InvalidOperationException("FOH identity is missing.");
        var discoveredMonitor = monitorIdentity ??
            throw new InvalidOperationException("Stage identity is missing.");

        var fohTask = VerifyConnectedIdentityAsync(
            "FOH",
            currentFoh,
            currentConfiguration.Foh,
            discoveredFoh,
            cancellationToken);
        var monitorTask = VerifyConnectedIdentityAsync(
            "Stage",
            currentMonitor,
            currentConfiguration.Monitor,
            discoveredMonitor,
            cancellationToken);
        await Task.WhenAll(fohTask, monitorTask).ConfigureAwait(false);
    }

    private async Task VerifyConnectedIdentityAsync(
        string role,
        IWingSession session,
        WingEndpoint configuredEndpoint,
        DiscoveredWing discoveredIdentity,
        CancellationToken cancellationToken)
    {
        const string serialToken = "/$syscfg/$serial";
        var snapshot = await session.SnapshotAsync(serialToken, cancellationToken).ConfigureAwait(false);
        var serialValues = snapshot
            .Where(parameter =>
                parameter.TokenPath.Equals(serialToken, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var observedSerial = serialValues.Length == 1 &&
            serialValues[0].Value.Type == WingValueType.S
                ? serialValues[0].Value.AsString().Trim()
                : string.Empty;
        var pinnedSerial = configuredEndpoint.ExpectedSerial?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(observedSerial) ||
            string.IsNullOrWhiteSpace(pinnedSerial) ||
            !observedSerial.Equals(pinnedSerial, StringComparison.OrdinalIgnoreCase) ||
            !observedSerial.Equals(
                discoveredIdentity.SerialNumber.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            var shownObserved = string.IsNullOrWhiteSpace(observedSerial)
                ? "missing/invalid"
                : observedSerial;
            var message =
                $"{role}: connected WAPI console reports serial number '{shownObserved}', " +
                $"but pin/discovery expect '{pinnedSerial}'. Writes remain blocked.";
            PauseForSafety("CONNECTED_IDENTITY_MISMATCH", message);
            throw new ConnectedIdentityMismatchException(message);
        }
    }

    private async Task BeginCacheEpochAsync(CancellationToken cancellationToken)
    {
        var epoch = Interlocked.Increment(ref activeEpoch);
        lock (eventIntakeSync)
        {
            // A fresh full snapshot follows immediately. Events from the previous
            // connection epoch must not be replayed into the new helper session.
            deferredSourceEvents.Clear();
            deferredTargetEvents.Clear();
            acceptEvents = false;
        }

        var currentFohIdentity = fohIdentity ?? throw new InvalidOperationException("FOH identity is missing.");
        var currentMonitorIdentity = monitorIdentity ??
            throw new InvalidOperationException("Stage identity is missing.");
        await Task.WhenAll(
                stateSink.BeginEpochAsync("FOH", currentFohIdentity, epoch, cancellationToken),
                stateSink.BeginEpochAsync("Stage", currentMonitorIdentity, epoch, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<FreshPlanSnapshot> BuildFreshPlanAsync(CancellationToken cancellationToken)
    {
        var wasInvalidated = false;
        for (var attempt = 1; attempt <= SnapshotStabilityAttempts; attempt++)
        {
            var before = GetEventSequences();
            var plan = await ReadFreshPlanAttemptAsync(cancellationToken).ConfigureAwait(false);
            var after = GetEventSequences();
            if (before == after)
            {
                return new FreshPlanSnapshot(
                    plan,
                    after.Source,
                    after.Target,
                    after.Transport,
                    wasInvalidated);
            }

            wasInvalidated = true;
            Record(
                DiagnosticSeverity.Warning,
                "SNAPSHOT_INVALIDATED",
                $"Snapshot attempt {attempt} was invalidated by a simultaneous console change; both states are being reread.",
                "Synchronization");
        }

        throw new SnapshotUnstableException(
            "The consoles changed during every snapshot attempt; live writes remain blocked.");
    }

    private async Task<WritePlan> ReadFreshPlanAttemptAsync(CancellationToken cancellationToken)
    {
        var currentConfiguration = configuration ??
            throw new InvalidOperationException("Configuration is missing.");
        var currentSource = sourceSession ?? throw new InvalidOperationException("Source session is missing.");
        var currentTarget = targetSession ?? throw new InvalidOperationException("Target session is missing.");

        sourceState.Clear();
        targetState.Clear();
        desiredTargets.Clear();
        readbackWaiters.Clear();
        echoFingerprints.Clear();

        var sourceNodes = GetSnapshotNodes(currentConfiguration, sourceSide: true);
        var targetNodes = GetSnapshotNodes(currentConfiguration, sourceSide: false);
        var sourceSnapshotTask = ReadNodesAsync(
            currentSource,
            sourceNodes,
            sourceSide: true,
            cancellationToken);
        var targetSnapshotTask = ReadNodesAsync(
            currentTarget,
            targetNodes,
            sourceSide: false,
            cancellationToken);
        await Task.WhenAll(sourceSnapshotTask, targetSnapshotTask).ConfigureAwait(false);
        var sourceSnapshot = await sourceSnapshotTask.ConfigureAwait(false);
        var targetSnapshot = await targetSnapshotTask.ConfigureAwait(false);

        foreach (var parameter in sourceSnapshot)
        {
            sourceState[parameter.TokenPath] = parameter.Value;
        }

        foreach (var parameter in targetSnapshot)
        {
            targetState[parameter.TokenPath] = parameter.Value;
        }

        await StoreSnapshotsAsync(sourceSnapshot, targetSnapshot, cancellationToken).ConfigureAwait(false);
        var changes = sourceSnapshot
            .Where(parameter => IsEnabledScopedToken(parameter.TokenPath))
            .Select(parameter => new SyncChange(
                TokenPath.Parse(parameter.TokenPath),
                parameter.Value,
                parameter.ObservedAt,
                Interlocked.Increment(ref sourceRevision)))
            .ToArray();
        var completePlan = planner.Plan(
            changes,
            currentConfiguration.Channels,
            currentConfiguration.Scopes,
            currentConfiguration.Safety,
            clock.UtcNow);
        UpdateDesiredTargets(completePlan);
        return FilterUnchangedWrites(completePlan);
    }

    private static TokenPath[] GetSnapshotNodes(
        AppConfiguration currentConfiguration,
        bool sourceSide)
    {
        var nodes = new Dictionary<string, TokenPath>(StringComparer.Ordinal);
        foreach (var mapping in currentConfiguration.Channels.InputChannels)
        {
            var channel = sourceSide ? mapping.Source : mapping.Target;
            foreach (var node in ScopeCatalog.GetSnapshotNodes(
                         currentConfiguration.Scopes,
                         WingChannelKind.Input,
                         channel))
            {
                nodes[node.Path.ToString()] = node.Path;
            }
        }

        foreach (var mapping in currentConfiguration.Channels.AuxChannels)
        {
            var channel = sourceSide ? mapping.Source : mapping.Target;
            foreach (var node in ScopeCatalog.GetSnapshotNodes(
                         currentConfiguration.Scopes,
                         WingChannelKind.Aux,
                         channel))
            {
                nodes[node.Path.ToString()] = node.Path;
            }
        }

        return nodes.Values.OrderBy(static path => path.ToString(), StringComparer.Ordinal).ToArray();
    }

    private async Task<IReadOnlyList<WingParameter>> ReadNodesAsync(
        IWingSession session,
        IReadOnlyList<TokenPath> nodes,
        bool sourceSide,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var first = await ReadScalarCertifiedNodePassAsync(
                    session,
                    nodes,
                    sourceSide,
                    cancellationToken)
                .ConfigureAwait(false);
            var second = await ReadScalarCertifiedNodePassAsync(
                    session,
                    nodes,
                    sourceSide,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!first.ModelMirrorMismatch &&
                !second.ModelMirrorMismatch &&
                ParameterMapsAreEquivalent(
                    first.Values,
                    second.Values,
                    0.0001F))
            {
                return second.Values.Values
                    .OrderBy(static parameter => parameter.TokenPath, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        throw new SnapshotUnstableException(
            "A console kept changing during duplicate node and scalar reads; " +
            "no writes were sent.");
    }

    private async Task<StableNodePass>
        ReadScalarCertifiedNodePassAsync(
            IWingSession session,
            IReadOnlyList<TokenPath> nodes,
            bool sourceSide,
            CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, WingParameter>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var snapshot = await session.SnapshotAsync(node.ToString(), cancellationToken)
                .ConfigureAwait(false);
            foreach (var parameter in snapshot)
            {
                if (IsEnabledMappedToken(parameter.TokenPath, sourceSide))
                {
                    candidates[parameter.TokenPath] = parameter;
                }
            }
        }

        var certified = new Dictionary<string, WingParameter>(StringComparer.Ordinal);
        var modelMirrorMismatch = false;
        foreach (var token in candidates.Keys.Order(StringComparer.Ordinal))
        {
            var exact = await ReadExactParameterAsync(
                    session,
                    token,
                    cancellationToken)
                .ConfigureAwait(false);
            certified[token] = exact;
            if (TokenPath.Parse(token).Leaf.Equals("mdl", StringComparison.OrdinalIgnoreCase) &&
                candidates.TryGetValue(token, out var mirrored) &&
                (mirrored.Value.Type != exact.Value.Type ||
                 !WingValueComparer.AreEquivalent(
                     mirrored.Value,
                     exact.Value,
                     0.0001F)))
            {
                modelMirrorMismatch = true;
            }
        }

        return new StableNodePass(
            new ReadOnlyDictionary<string, WingParameter>(certified),
            modelMirrorMismatch);
    }

    private static bool ParameterMapsAreEquivalent(
        IReadOnlyDictionary<string, WingParameter> left,
        IReadOnlyDictionary<string, WingParameter> right,
        float tolerance)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var other) ||
                pair.Value.Value.Type != other.Value.Type ||
                !WingValueComparer.AreEquivalent(
                    pair.Value.Value,
                    other.Value,
                    tolerance))
            {
                return false;
            }
        }

        return true;
    }

    private async Task StoreSnapshotsAsync(
        IReadOnlyList<WingParameter> sourceSnapshot,
        IReadOnlyList<WingParameter> targetSnapshot,
        CancellationToken cancellationToken)
    {
        var currentFohIdentity = fohIdentity ?? throw new InvalidOperationException("FOH identity is missing.");
        var currentMonitorIdentity = monitorIdentity ??
            throw new InvalidOperationException("Stage identity is missing.");
        var sourceIsFoh = ReferenceEquals(sourceSession, fohSession);
        await Task.WhenAll(
                stateSink.StoreAsync(
                    sourceIsFoh ? "FOH" : "Stage",
                    sourceIsFoh ? currentFohIdentity : currentMonitorIdentity,
                    activeEpoch,
                    sourceSnapshot,
                    cancellationToken),
                stateSink.StoreAsync(
                    sourceIsFoh ? "Stage" : "FOH",
                    sourceIsFoh ? currentMonitorIdentity : currentFohIdentity,
                    activeEpoch,
                    targetSnapshot,
                    cancellationToken))
            .ConfigureAwait(false);
    }

    private WritePlan FilterUnchangedWrites(WritePlan completePlan)
    {
        var currentConfiguration = configuration ??
            throw new InvalidOperationException("Configuration is missing.");
        var guardedDifferenceGroups = completePlan.Writes
            .Where(write =>
                IsGuardedMutationPath(write.TargetPath) &&
                !TargetAlreadyHas(write))
            .Select(write => GetDynamicGroup(write.TargetPath))
            .Where(static group => group is not null)
            .ToHashSet(StringComparer.Ordinal);

        var retained = new List<PlannedWrite>();
        foreach (var write in completePlan.Writes)
        {
            var dynamicGroup = GetDynamicGroup(write.TargetPath);
            var mustRewriteAfterGuardedMutation =
                dynamicGroup is not null &&
                guardedDifferenceGroups.Contains(dynamicGroup);
            if (write.IsSafetyGuard
                ? dynamicGroup is not null && guardedDifferenceGroups.Contains(dynamicGroup)
                : !TargetAlreadyHas(write) || mustRewriteAfterGuardedMutation)
            {
                retained.Add(write with { Sequence = retained.Count });
            }
        }

        return new WritePlan(
            retained,
            completePlan.Issues,
            completePlan.SupersededChangeCount);

        bool TargetAlreadyHas(PlannedWrite write) =>
            targetState.TryGetValue(write.TargetPath.ToString(), out var targetValue) &&
            WingValueComparer.AreEquivalent(
                write.Value,
                targetValue,
                currentConfiguration.Safety.FloatTolerance);
    }

    private void UpdateDesiredTargets(WritePlan plan)
    {
        foreach (var write in plan.Writes.Where(static item => !item.IsSafetyGuard))
        {
            desiredTargets[write.TargetPath.ToString()] =
                new DesiredTarget(write.SourcePath, write.TargetPath, write.Value, write.Scope);
        }
    }

    private async Task ExecuteOrPreviewPlanAsync(
        WritePlan plan,
        CancellationToken cancellationToken,
        bool requireActiveEventIntake = false,
        FreshPlanSnapshot? executionSnapshot = null)
    {
        var currentConfiguration = configuration ??
            throw new InvalidOperationException("Configuration is missing.");
        if (currentConfiguration.Safety.DryRun)
        {
            await PreviewPlanAsync(plan).ConfigureAwait(false);
            return;
        }

        await ExecutePlanAsync(
                plan,
                cancellationToken,
                requireActiveEventIntake,
                executionSnapshot)
            .ConfigureAwait(false);
    }

    private Task PreviewPlanAsync(WritePlan plan)
    {
        foreach (var write in plan.Writes)
        {
            observer.RecordParameterAction(
                write.Disposition == WriteDisposition.BlockedBySafety ? "blocked" : "preview",
                write.SourcePath.ToString(),
                write.TargetPath.ToString(),
                write.Scope,
                write.Value,
                dryRun: true);
            if (write.Disposition == WriteDisposition.BlockedBySafety)
            {
                Interlocked.Increment(ref blockedWrites);
            }
            else
            {
                Interlocked.Increment(ref previewedWrites);
            }
        }

        RaiseMetrics();
        return Task.CompletedTask;
    }

    private async Task ExecutePlanAsync(
        WritePlan plan,
        CancellationToken cancellationToken,
        bool requireActiveEventIntake = false,
        FreshPlanSnapshot? executionSnapshot = null)
    {
        await ExecutePlanCoreAsync(
                plan,
                cancellationToken,
                requireActiveEventIntake,
                executionSnapshot)
            .ConfigureAwait(false);
    }

    private async Task ExecutePlanCoreAsync(
        WritePlan plan,
        CancellationToken cancellationToken,
        bool requireActiveEventIntake = false,
        FreshPlanSnapshot? executionSnapshot = null)
    {
        var currentTarget = targetSession ?? throw new InvalidOperationException("Target session is missing.");
        var executable = plan.ExecutableWrites.ToArray();

        foreach (var blocked in plan.Writes.Where(static write =>
                     write.Disposition == WriteDisposition.BlockedBySafety))
        {
            Interlocked.Increment(ref blockedWrites);
            observer.RecordParameterAction(
                "blocked",
                blocked.SourcePath.ToString(),
                blocked.TargetPath.ToString(),
                blocked.Scope,
                blocked.Value,
                dryRun: false);
        }

        foreach (var preview in plan.Writes.Where(static write =>
                     write.Disposition == WriteDisposition.DryRun))
        {
            Interlocked.Increment(ref previewedWrites);
            observer.RecordParameterAction(
                "preview",
                preview.SourcePath.ToString(),
                preview.TargetPath.ToString(),
                preview.Scope,
                preview.Value,
                dryRun: true);
        }

        if (executable.Length == 0)
        {
            RaiseMetrics();
            return;
        }

        ThrowIfStopRequested(
            "A stop request blocked plan validation before new writes.");
        var units = BuildExecutionUnits(executable);
        var preflightTransportGeneration = Interlocked.Read(ref transportGeneration);
        var executionFence = GetEventSequences();
        await ValidateExecutionUnitsBeforeWriteAsync(
                units,
                currentTarget,
                preflightTransportGeneration,
                executionFence,
                cancellationToken)
            .ConfigureAwait(false);
        ThrowIfStopRequested(
            "A stop request blocked execution after plan validation.");

        foreach (var unit in units)
        {
            ThrowIfStopRequested(
                "A stop request blocked the next transaction unit.");
            ThrowIfEventFenceChanged(
                executionFence,
                "The source or target console changed after plan validation; the plan is being rebuilt.");
            var guardedMutation = unit.Writes.Any(static write =>
                IsGuardedMutationPath(write.TargetPath));
            var protectiveWrites = unit.Writes
                .Where(write =>
                    IsEnablePath(write.TargetPath) &&
                    GetExecutionPhase(write) == 0)
                .GroupBy(static write => write.TargetPath.ToString(), StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray();
            if (!guardedMutation)
            {
                await ExecuteUnitBatchesAsync(
                        unit,
                        currentTarget,
                        guardContext: null,
                        requireActiveEventIntake,
                        executionSnapshot,
                        executionFence,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (protectiveWrites.Length == 0)
            {
                throw new IOException(
                    $"Processor group {unit.TransactionGroup} bevat een model-/delaymutatie " +
                    "zonder een eenduidige veilige enablewaarde; no writes were sent.");
            }

            // Capture and protect exactly one physical processor group at a time.
            // This keeps bypass windows local and prevents an earlier group from
            // making the rollback state of a later group stale.
            var guardTransportGeneration = Interlocked.Read(ref transportGeneration);
            if (!IsTargetTransportCurrent(currentTarget, guardTransportGeneration))
            {
                throw new ReconciliationInterruptedException(
                    "The target transport session changed before reading the safety guards.");
            }

            var originals = await CaptureSafetyGuardOriginalsAsync(
                    protectiveWrites,
                    currentTarget,
                    guardTransportGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
            var context = new SafetyGuardExecutionContext(
                unit.TransactionGroup!,
                GetDynamicGroup(
                    unit.Writes.First(static write =>
                        IsGuardedMutationPath(write.SourcePath)).SourcePath) ??
                    throw new IOException(
                        "The source processor group for the safety transaction could not be determined."),
                originals,
                new ReadOnlyDictionary<string, PlannedWrite>(
                    protectiveWrites.ToDictionary(
                        static write => write.TargetPath.ToString(),
                        StringComparer.Ordinal)),
                unit.Writes
                    .Where(write =>
                        !write.IsSafetyGuard &&
                        GetExecutionPhase(write) is 1 or 2)
                    .Select(static write => write.TargetPath.ToString())
                    .ToHashSet(StringComparer.Ordinal),
                unit.Writes
                    .Where(write =>
                        IsEnablePath(write.TargetPath) &&
                        GetExecutionPhase(write) == 3)
                    .Select(static write => write.TargetPath.ToString())
                    .ToHashSet(StringComparer.Ordinal),
                guardTransportGeneration);
            try
            {
                ActivateSafetyTransaction(context, executionFence);
                await ExecuteUnitBatchesAsync(
                        unit,
                        currentTarget,
                        context,
                        requireActiveEventIntake,
                        executionSnapshot,
                        executionFence,
                        cancellationToken)
                    .ConfigureAwait(false);
                context.ActivePaths.RemoveWhere(path =>
                    !context.RestorePaths.Contains(path));
                if (context.ActivePaths.Count > 0)
                {
                    throw new InvalidOperationException(
                        "A safety guard had no verified end state in its processor group.");
                }
            }
            catch (Exception exception) when (context.ActivePaths.Count > 0)
            {
                await HandleInterruptedSafetyGuardAsync(
                        currentTarget,
                        context,
                        exception)
                    .ConfigureAwait(false);
                throw;
            }
            finally
            {
                lock (eventIntakeSync)
                {
                    if (ReferenceEquals(Volatile.Read(ref activeSafetyTransaction), context))
                    {
                        Volatile.Write(ref activeSafetyTransaction, null);
                    }
                }
            }
        }
    }

    private async Task ExecuteUnitBatchesAsync(
        ExecutionUnit unit,
        IWingSession currentTarget,
        SafetyGuardExecutionContext? guardContext,
        bool requireActiveEventIntake,
        FreshPlanSnapshot? executionSnapshot,
        EventSequences executionFence,
        CancellationToken cancellationToken)
    {
        var executionBatches = GetExecutionBatches(unit).ToArray();
        for (var batchIndex = 0; batchIndex < executionBatches.Length; batchIndex++)
        {
            var executionBatch = executionBatches[batchIndex];
            var batch = executionBatch.Writes;
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfStopRequested(
                "A stop request blocked the next write batch.");
            ThrowIfEventFenceChanged(
                executionFence,
                "The source or target console changed before the next transaction phase.");
            ThrowIfSafetyTransactionInterfered(guardContext);
            var batchTransportGeneration = Interlocked.Read(ref transportGeneration);
            if (!IsTargetTransportCurrent(currentTarget, batchTransportGeneration) ||
                (guardContext is not null &&
                 guardContext.TransportGeneration != batchTransportGeneration))
            {
                throw new ReconciliationInterruptedException(
                    "The target transport session changed before the next batch.");
            }

            if (requireActiveEventIntake && !acceptEvents)
            {
                throw new ReconciliationInterruptedException(
                    "The live batch was interrupted before the next write for a reconnect.");
            }

            if (executionSnapshot is not null &&
                !IsSnapshotCurrent(executionSnapshot))
            {
                throw new SnapshotChangedBeforeWriteException(
                    "The consoles or transport session changed before the write; the plan was not executed.");
            }

            var stopwatch = Stopwatch.StartNew();
            var reinforcingGuards =
                guardContext is not null && executionBatch.Phase is 1 or 2
                    ? guardContext.ActivePaths
                        .Order(StringComparer.Ordinal)
                        .Select(path => guardContext.Guards[path])
                        .ToArray()
                    : [];
            if (reinforcingGuards.Length > 0)
            {
                await ReassertSafetyGuardsBeforeMutationAsync(
                        currentTarget,
                        reinforcingGuards,
                        guardContext!,
                        executionFence,
                        requireActiveEventIntake,
                        executionSnapshot,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var waiters = RegisterReadbacks(batch);
            foreach (var write in batch)
            {
                echoFingerprints[write.TargetPath.ToString()] = write.Echo;
                observer.RecordParameterAction(
                    "write",
                    write.SourcePath.ToString(),
                    write.TargetPath.ToString(),
                    write.Scope,
                    write.Value,
                    dryRun: false);
            }

            try
            {
                if (requireActiveEventIntake && !acceptEvents)
                {
                    throw new ReconciliationInterruptedException(
                        "De live batch werd vóór dispatch onderbroken voor een reconnect.");
                }

                if (executionSnapshot is not null &&
                    !IsSnapshotCurrent(executionSnapshot))
                {
                    throw new SnapshotChangedBeforeWriteException(
                        "The consoles or transport session changed right before dispatch; the plan was not executed.");
                }

                if (!AreTransportSessionsConnected())
                {
                    throw new ReconciliationInterruptedException(
                        "A console session was no longer connected right before dispatch.");
                }

                if (guardContext is not null)
                {
                    foreach (var write in batch)
                    {
                        var token = write.TargetPath.ToString();
                        if (guardContext.Guards.TryGetValue(token, out var protective) &&
                            write.Value == protective.Value)
                        {
                            guardContext.ActivePaths.Add(token);
                        }
                    }

                    if (executionBatch.Phase is 1 or 2)
                    {
                        if (guardContext.ActivePaths.Count != guardContext.Guards.Count)
                        {
                            throw new IOException(
                                "Not all safety guards were active before the processor write.");
                        }

                        // A model or parameter batch may be partially applied even if
                        // SetMany throws. Re-enabling the old state is no longer a safe
                        // automatic rollback once this point has been crossed.
                        guardContext.ProcessorMutationMayHaveStarted = true;
                        Volatile.Write(ref guardContext.IntermediateSuppressionOpen, 1);
                    }
                    else if (executionBatch.Phase == 3)
                    {
                        Volatile.Write(ref guardContext.IntermediateSuppressionOpen, 0);
                        Volatile.Write(ref guardContext.RestoreInProgress, 1);
                    }
                }

                ThrowIfEventFenceChanged(
                    executionFence,
                    "The source or target console changed right before mutation dispatch.");
                ThrowIfSafetyTransactionInterfered(guardContext);
                ThrowIfStopRequested(
                    "A stop request blocked the write batch before dispatch.");
                var dispatchWrites = batch
                    .Select(static write =>
                        new WingWriteRequest(write.TargetPath.ToString(), write.Value))
                    .ToArray();
                await currentTarget.SetManyAsync(
                        dispatchWrites,
                        cancellationToken)
                    .ConfigureAwait(false);
                if ((requireActiveEventIntake && !acceptEvents) ||
                    (executionSnapshot is not null && !IsSnapshotCurrent(executionSnapshot)) ||
                    !AreTransportSessionsConnected())
                {
                    throw new ReconciliationInterruptedException(
                        "The transport session changed during the write; a fresh snapshot is required.");
                }

                await VerifyBatchAsync(batch, waiters, cancellationToken).ConfigureAwait(false);
                if (reinforcingGuards.Length > 0)
                {
                    await VerifyActiveSafetyGuardsAsync(
                            currentTarget,
                            reinforcingGuards,
                            guardContext!)
                        .ConfigureAwait(false);
                }
                ThrowIfSafetyTransactionInterfered(guardContext);
                ThrowIfEventFenceChanged(
                    executionFence,
                    "The source or target console changed during mutation verification.");

                var lastMutationBatch =
                    executionBatch.Phase is 1 or 2 &&
                    !executionBatches
                        .Skip(batchIndex + 1)
                        .Any(static candidate => candidate.Phase is 1 or 2);
                if (guardContext is not null && lastMutationBatch)
                {
                    await VerifyGuardedGroupDesiredStateAsync(
                            currentTarget,
                            unit,
                            guardContext,
                            executionFence,
                            verifySafeGuards: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!executionBatches.Any(static candidate => candidate.Phase == 3))
                    {
                        Volatile.Write(ref guardContext.IntermediateSuppressionOpen, 0);
                        await VerifyGuardedGroupDesiredStateAsync(
                                currentTarget,
                                unit,
                                guardContext,
                                executionFence,
                                verifySafeGuards: false,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                var lastRestoreBatch =
                    executionBatch.Phase == 3 &&
                    !executionBatches
                        .Skip(batchIndex + 1)
                        .Any(static candidate => candidate.Phase == 3);
                if (guardContext is not null && lastRestoreBatch)
                {
                    await VerifyGuardedGroupDesiredStateAsync(
                            currentTarget,
                            unit,
                            guardContext,
                            executionFence,
                            verifySafeGuards: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if ((requireActiveEventIntake && !acceptEvents) ||
                    (executionSnapshot is not null && !IsSnapshotCurrent(executionSnapshot)) ||
                    !IsTargetTransportCurrent(currentTarget, batchTransportGeneration))
                {
                    throw new ReconciliationInterruptedException(
                        "The transport session changed during write verification; " +
                        "the batch is not considered complete.");
                }

                if (guardContext is not null)
                {
                    foreach (var restore in batch.Where(write =>
                                 executionBatch.Phase == 3 &&
                                 guardContext.RestorePaths.Contains(
                                     write.TargetPath.ToString())))
                    {
                        guardContext.ActivePaths.Remove(restore.TargetPath.ToString());
                    }
                }
            }
            catch
            {
                foreach (var write in batch)
                {
                    var token = write.TargetPath.ToString();
                    readbackWaiters.TryRemove(token, out _);
                    if (echoFingerprints.TryGetValue(token, out var fingerprint) &&
                        fingerprint.Equals(write.Echo))
                    {
                        echoFingerprints.TryRemove(token, out _);
                    }
                }

                throw;
            }

            stopwatch.Stop();
            AddLatency(stopwatch.Elapsed.TotalMilliseconds);
            Interlocked.Add(ref synchronizedWrites, batch.Length);
            RaiseMetrics();
        }
    }

    private async Task VerifyActiveSafetyGuardsAsync(
        IWingSession target,
        IReadOnlyList<PlannedWrite> guards,
        SafetyGuardExecutionContext context)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tolerance = configuration?.Safety.FloatTolerance ?? 0.0001F;
        var mismatch = false;
        foreach (var guard in guards)
        {
            var token = guard.TargetPath.ToString();
            var snapshot = await target.SnapshotAsync(token, timeout.Token).ConfigureAwait(false);
            var actual = snapshot.FirstOrDefault(parameter =>
                parameter.TokenPath.Equals(token, StringComparison.Ordinal));
            if (actual is null ||
                !WingValueComparer.AreEquivalent(
                    guard.Value,
                    actual.Value,
                    tolerance))
            {
                mismatch = true;
                break;
            }

            targetState[token] = actual.Value;
        }

        if (!mismatch)
        {
            return;
        }

        var safeWrites = guards
            .Select(static guard =>
                new WingWriteRequest(guard.TargetPath.ToString(), guard.Value))
            .ToArray();
        if (!IsTargetTransportCurrent(target, context.TransportGeneration))
        {
            throw new IOException(
                "A safety guard changed during the processor write and the transport session " +
                "is no longer safely available.");
        }

        await target.SetManyAsync(safeWrites, timeout.Token).ConfigureAwait(false);
        var reasserted = true;
        foreach (var guard in guards)
        {
            var token = guard.TargetPath.ToString();
            var snapshot = await target.SnapshotAsync(token, timeout.Token).ConfigureAwait(false);
            var actual = snapshot.FirstOrDefault(parameter =>
                parameter.TokenPath.Equals(token, StringComparison.Ordinal));
            if (actual is null ||
                !WingValueComparer.AreEquivalent(
                    guard.Value,
                    actual.Value,
                    tolerance))
            {
                reasserted = false;
                break;
            }

            targetState[token] = actual.Value;
        }

        Record(
            DiagnosticSeverity.Critical,
            "SAFETY_GUARD_DRIFT_DURING_MUTATION",
            reasserted
                ? "Een externe targetwijziging activeerde een processor tijdens de mutatie; " +
                  "the safe guard was resent and remains active for manual review."
                : "An external target change activated a processor during the mutation and " +
                  "the safe guard could not be confirmed.",
            "Safety");
        throw new IOException(
            "A safety guard changed during the processor write; further writes are blocked.");
    }

    private async Task ReassertSafetyGuardsBeforeMutationAsync(
        IWingSession target,
        IReadOnlyList<PlannedWrite> guards,
        SafetyGuardExecutionContext context,
        EventSequences executionFence,
        bool requireActiveEventIntake,
        FreshPlanSnapshot? executionSnapshot,
        CancellationToken cancellationToken)
    {
        ThrowIfSafetyTransactionInterfered(context);
        ThrowIfEventFenceChanged(
            executionFence,
            "The source or target console changed before reactivating the safety guards.");
        if ((requireActiveEventIntake && !acceptEvents) ||
            (executionSnapshot is not null && !IsSnapshotCurrent(executionSnapshot)) ||
            !IsTargetTransportCurrent(target, context.TransportGeneration))
        {
            throw new ReconciliationInterruptedException(
                "The transport session changed before safety-guard reconfirmation.");
        }

        var currentSafety = configuration?.Safety ??
            throw new InvalidOperationException("Safety configuration is missing.");
        foreach (var guard in guards)
        {
            var echo = EchoFingerprint.Create(
                guard.TargetPath,
                guard.Value,
                clock.UtcNow,
                currentSafety.EchoSuppressionWindow,
                currentSafety.FloatTolerance);
            echoFingerprints[guard.TargetPath.ToString()] = echo;
            observer.RecordParameterAction(
                "guard-reassert",
                guard.SourcePath.ToString(),
                guard.TargetPath.ToString(),
                guard.Scope,
                guard.Value,
                dryRun: false);
        }

        try
        {
            ThrowIfStopRequested(
                "A stop request blocked reactivation of the safety guards.");
            await target.SetManyAsync(
                    guards
                        .Select(static guard =>
                            new WingWriteRequest(guard.TargetPath.ToString(), guard.Value))
                        .ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfSafetyTransactionInterfered(context);
            ThrowIfEventFenceChanged(
                executionFence,
                "The source or target console changed during safety-guard reconfirmation.");
            await VerifyActiveSafetyGuardsAsync(target, guards, context).ConfigureAwait(false);
            ThrowIfSafetyTransactionInterfered(context);
            ThrowIfEventFenceChanged(
                executionFence,
                "The source or target console changed after exact safety-guard readback.");
        }
        finally
        {
            foreach (var guard in guards)
            {
                echoFingerprints.TryRemove(guard.TargetPath.ToString(), out _);
            }
        }
    }

    private async Task VerifyGuardedGroupDesiredStateAsync(
        IWingSession target,
        ExecutionUnit unit,
        SafetyGuardExecutionContext context,
        EventSequences executionFence,
        bool verifySafeGuards,
        CancellationToken cancellationToken)
    {
        var tolerance = configuration?.Safety.FloatTolerance ?? 0.0001F;
        var desired = unit.Writes
            .Where(write =>
                !write.IsSafetyGuard &&
                !IsEnablePath(write.TargetPath) &&
                GetExecutionPhase(write) is 1 or 2)
            .GroupBy(static write => write.TargetPath.ToString(), StringComparer.Ordinal)
            .Select(static group => group.OrderBy(write => write.Sequence).Last())
            .OrderBy(static write => write.TargetPath.ToString(), StringComparer.Ordinal)
            .ToArray();
        foreach (var write in desired)
        {
            ThrowIfSafetyTransactionInterfered(context);
            ThrowIfEventFenceChanged(
                executionFence,
                "The source or target console changed during full group readback.");
            var actual = await ReadExactScalarAsync(
                    target,
                    write.TargetPath.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (actual.Type != write.Value.Type ||
                !WingValueComparer.AreEquivalent(write.Value, actual, tolerance))
            {
                throw new IOException(
                    $"De processorgroep {context.TransactionGroup} wijkt na de mutation af op " +
                    $"{write.TargetPath}; the safety guard remains active.");
            }

            targetState[write.TargetPath.ToString()] = actual;
        }

        if (verifySafeGuards)
        {
            await VerifyActiveSafetyGuardsAsync(
                    target,
                    context.ActivePaths
                        .Order(StringComparer.Ordinal)
                        .Select(path => context.Guards[path])
                        .ToArray(),
                    context)
                .ConfigureAwait(false);
        }
        ThrowIfSafetyTransactionInterfered(context);
        ThrowIfEventFenceChanged(
            executionFence,
            "The source or target console changed after full group readback.");
    }

    private static void ThrowIfSafetyTransactionInterfered(
        SafetyGuardExecutionContext? context)
    {
        if (context is not null &&
            (Interlocked.Read(ref context.UnexpectedSourceRevision) != 0 ||
             Interlocked.Read(ref context.UnexpectedTargetRevision) != 0))
        {
            throw new IOException(
                $"An unexpected source or target change affected processor group " +
                $"{context.TransactionGroup}; de transactie is fail-closed gestopt.");
        }
    }

    private async Task HandleInterruptedSafetyGuardAsync(
        IWingSession currentTarget,
        SafetyGuardExecutionContext context,
        Exception exception)
    {
        liveWritesArmed = false;
        SuspendEventIntake();
        var recovered = context.ProcessorMutationMayHaveStarted
            ? await TryHoldSafetyGuardsAsync(currentTarget, context).ConfigureAwait(false)
            : await TryRestoreSafetyGuardsAsync(currentTarget, context).ConfigureAwait(false);
        if (!recovered)
        {
            var paths = string.Join(
                ", ",
                context.ActivePaths.Order(StringComparer.Ordinal));
            var message =
                $"A phased processor write was aborted after safety guards were active " +
                $"({paths}). Leave the affected processor(s) off, verify the " +
                "target console manually, then restart through dry run.";
            PauseForSafety("SAFETY_GUARD_RECOVERY_REQUIRED", message);
            throw new SafetyGuardRecoveryException(message, exception);
        }

        Record(
            context.ProcessorMutationMayHaveStarted
                ? DiagnosticSeverity.Critical
                : DiagnosticSeverity.Warning,
            context.ProcessorMutationMayHaveStarted
                ? "SAFETY_GUARD_HELD_FOR_RECOVERY"
                : "SAFETY_GUARD_ROLLED_BACK",
            context.ProcessorMutationMayHaveStarted
                ? "De processortransactie werd na een mutation afgebroken; alle betrokken " +
                  "enable values were set safely again exactly and remain off for manual review."
                : "De gefaseerde write werd voor de model/parameterfase afgebroken; " +
                  "all original processor states were restored exactly.",
            "Safety");
        if (context.ProcessorMutationMayHaveStarted)
        {
            var message =
                "Een gefaseerde processorwrite werd na een mutation afgebroken. De safetyguards " +
                "were set safely again exactly; verify the target console manually and restart " +
                "afterward through dry run.";
            PauseForSafety("SAFETY_GUARD_RECOVERY_REQUIRED", message);
            throw new SafetyGuardRecoveryException(message, exception);
        }
    }

    private static ExecutionUnit[] BuildExecutionUnits(PlannedWrite[] executable)
    {
        var ordered = executable.OrderBy(static write => write.Sequence).ToArray();
        var units = new List<ExecutionUnit>();
        var currentWrites = new List<PlannedWrite>();
        string? currentGroup = null;
        var hasCurrent = false;

        foreach (var write in ordered)
        {
            var transactionGroup = GetDynamicGroup(write.TargetPath);
            if (hasCurrent &&
                !string.Equals(currentGroup, transactionGroup, StringComparison.Ordinal))
            {
                units.Add(new ExecutionUnit(currentGroup, currentWrites.ToArray()));
                currentWrites.Clear();
            }

            currentGroup = transactionGroup;
            hasCurrent = true;
            currentWrites.Add(write);
        }

        if (currentWrites.Count > 0)
        {
            units.Add(new ExecutionUnit(currentGroup, currentWrites.ToArray()));
        }

        return units.ToArray();
    }

    private async Task ValidateExecutionUnitsBeforeWriteAsync(
        IReadOnlyList<ExecutionUnit> units,
        IWingSession expectedTarget,
        long expectedTransportGeneration,
        EventSequences executionFence,
        CancellationToken cancellationToken)
    {
        var expectedSource = sourceSession ??
            throw new InvalidOperationException("Source session is missing.");
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        var tolerance = configuration?.Safety.FloatTolerance ?? 0.0001F;
        var targetPreflight = new Dictionary<string, WingValue>(StringComparer.Ordinal);

        foreach (var tokenGroup in units
                     .SelectMany(static unit => unit.Writes)
                     .GroupBy(
                         static write => write.TargetPath.ToString(),
                         StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            ThrowIfEventFenceChanged(
                executionFence,
                "The source or target console changed during full target preflight.");
            if (!IsTransportPairCurrent(
                    expectedSource,
                    expectedTarget,
                    expectedTransportGeneration))
            {
                throw new ReconciliationInterruptedException(
                    "The transport session changed during full target preflight.");
            }

            var actual = await ReadExactScalarAsync(
                    expectedTarget,
                    tokenGroup.Key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (tokenGroup.Any(write => write.Value.Type != actual.Type))
            {
                throw new IOException(
                    $"Target scalar {tokenGroup.Key} does not have the planned WAPI type; " +
                    "no writes were sent.");
            }

            targetPreflight[tokenGroup.Key] = actual;
        }

        foreach (var unit in units)
        {
            ThrowIfEventFenceChanged(
                executionFence,
                "The source or target console changed during safety validation of the plan.");
            var guardedMutations = unit.Writes
                .Where(static write => IsGuardedMutationPath(write.TargetPath))
                .ToArray();
            if (unit.TransactionGroup is not null &&
                !seenGroups.Add(unit.TransactionGroup))
            {
                throw new IOException(
                    $"Processor group {unit.TransactionGroup} is not contiguous in the plan; " +
                    "no writes were sent.");
            }

            if (guardedMutations.Length == 0)
            {
                continue;
            }

            if (unit.TransactionGroup is null ||
                unit.Writes.Any(write =>
                    !string.Equals(
                        GetDynamicGroup(write.TargetPath),
                        unit.TransactionGroup,
                        StringComparison.Ordinal)))
            {
                throw new IOException(
                    "A model/delay mutation has no unique physical processor group; " +
                    "no writes were sent.");
            }

            var mutation = guardedMutations[0];
            var targetEnablePaths = GetCanonicalEnablePaths(mutation.TargetPath);
            var sourceEnablePaths = GetCanonicalEnablePaths(mutation.SourcePath);
            if (targetEnablePaths.Length == 0 ||
                targetEnablePaths.Length != sourceEnablePaths.Length)
            {
                throw new IOException(
                    $"Processor group {unit.TransactionGroup} does not have a complete canonical " +
                    "enable-set; no writes were sent.");
            }

            for (var index = 0; index < targetEnablePaths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsTransportPairCurrent(
                        expectedSource,
                        expectedTarget,
                        expectedTransportGeneration))
                {
                    throw new ReconciliationInterruptedException(
                        "The transport session changed during safety validation of the plan.");
                }

                var sourceToken = sourceEnablePaths[index].ToString();
                var targetToken = targetEnablePaths[index].ToString();
                var sourceValue = await ReadExactScalarAsync(
                        expectedSource,
                        sourceToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!targetPreflight.TryGetValue(targetToken, out var targetValue))
                {
                    throw new IOException(
                        $"Processor group {unit.TransactionGroup} mist een geplande targetwrite " +
                        $"for canonical safety guard {targetToken}; no writes were sent.");
                }

                if (!IsTransportPairCurrent(
                        expectedSource,
                        expectedTarget,
                        expectedTransportGeneration))
                {
                    throw new ReconciliationInterruptedException(
                        "The transport session changed after safety validation of the plan.");
                }

                ThrowIfEventFenceChanged(
                    executionFence,
                    "The source or target console changed during exact reading of the safety guards.");

                var finalWrites = unit.Writes
                    .Where(write =>
                        !write.IsSafetyGuard &&
                        write.TargetPath.ToString().Equals(
                            targetToken,
                            StringComparison.Ordinal))
                    .OrderBy(static write => write.Sequence)
                    .ToArray();
                var safeWrites = unit.Writes
                    .Where(write =>
                        write.TargetPath.ToString().Equals(
                            targetToken,
                            StringComparison.Ordinal) &&
                        GetExecutionPhase(write) == 0 &&
                        !IsEnableActive(write.TargetPath, write.Value))
                    .ToArray();
                if (finalWrites.Length != 1 || safeWrites.Length == 0)
                {
                    throw new IOException(
                        $"Processor group {unit.TransactionGroup} is missing an unambiguous end value " +
                        $"or safe phase-0 write for {targetToken}; no writes were sent.");
                }

                var finalWrite = finalWrites[0];
                if (sourceValue.Type != finalWrite.Value.Type ||
                    targetValue.Type != finalWrite.Value.Type ||
                    !WingValueComparer.AreEquivalent(
                        sourceValue,
                        finalWrite.Value,
                        tolerance))
                {
                    throw new IOException(
                        $"The exact enable values for {sourceToken} and {targetToken} match " +
                        "do not match the plan in a type-safe way; no writes were sent.");
                }

                var expectedFinalPhase = IsEnableActive(finalWrite.TargetPath, finalWrite.Value)
                    ? 3
                    : 0;
                if (GetExecutionPhase(finalWrite) != expectedFinalPhase)
                {
                    throw new IOException(
                        $"The final phase for safety guard {targetToken} is invalid; " +
                        "no writes were sent.");
                }
            }
        }

        ThrowIfEventFenceChanged(
            executionFence,
            "The source or target console changed before the first write; the plan is being rebuilt.");
    }

    private bool IsTransportPairCurrent(
        IWingSession expectedSource,
        IWingSession expectedTarget,
        long expectedTransportGeneration) =>
        ReferenceEquals(expectedSource, sourceSession) &&
        IsTargetTransportCurrent(expectedTarget, expectedTransportGeneration);

    private static async Task<WingValue> ReadExactScalarAsync(
        IWingSession session,
        string token,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.SnapshotAsync(token, cancellationToken).ConfigureAwait(false);
        var exact = snapshot
            .Where(parameter =>
                parameter.TokenPath.Equals(token, StringComparison.Ordinal))
            .ToArray();
        if (exact.Length != 1)
        {
            throw new IOException(
                $"Scalar {token} could not be read unambiguously and authoritatively.");
        }

        return exact[0].Value;
    }

    private static TokenPath[] GetCanonicalEnablePaths(TokenPath mutationPath)
    {
        if (IsDelayPath(mutationPath))
        {
            return
            [
                TokenPath.Parse(
                    $"/{mutationPath.Segments[0]}/{mutationPath.Segments[1]}/in/set/dlyon"),
            ];
        }

        if (mutationPath.Segments.Count < 4)
        {
            return [];
        }

        var prefix = $"/{mutationPath.Segments[0]}/{mutationPath.Segments[1]}";
        return mutationPath.Segments[2].ToLowerInvariant() switch
        {
            "flt" =>
            [
                TokenPath.Parse($"{prefix}/flt/lc"),
                TokenPath.Parse($"{prefix}/flt/hc"),
                TokenPath.Parse($"{prefix}/flt/tf"),
            ],
            "gate" or "gatesc" =>
            [
                TokenPath.Parse($"{prefix}/gate/on"),
            ],
            "dyn" or "dynxo" or "dynsc" =>
            [
                TokenPath.Parse(
                    mutationPath.Segments[0].Equals("aux", StringComparison.OrdinalIgnoreCase)
                        ? $"{prefix}/dyn/byp"
                        : $"{prefix}/dyn/on"),
            ],
            "eq" =>
            [
                TokenPath.Parse($"{prefix}/eq/on"),
            ],
            "peq" =>
            [
                TokenPath.Parse($"{prefix}/peq/on"),
            ],
            _ => [],
        };
    }

    private static IEnumerable<ExecutionBatch> GetExecutionBatches(ExecutionUnit unit)
    {
        if (unit.TransactionGroup is null)
        {
            foreach (var chunk in unit.Writes.Chunk(256))
            {
                yield return new ExecutionBatch(-1, chunk);
            }

            yield break;
        }

        foreach (var phase in unit.Writes
                     .GroupBy(GetExecutionPhase)
                     .OrderBy(static group => group.Key))
        {
            foreach (var chunk in phase.Chunk(256))
            {
                yield return new ExecutionBatch(phase.Key, chunk);
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, WingValue>>
        CaptureSafetyGuardOriginalsAsync(
            IReadOnlyList<PlannedWrite> guardWrites,
            IWingSession target,
            long expectedTransportGeneration,
            CancellationToken cancellationToken)
    {
        var originals = new Dictionary<string, WingValue>(StringComparer.Ordinal);
        foreach (var guard in guardWrites)
        {
            if (!IsTargetTransportCurrent(target, expectedTransportGeneration))
            {
                throw new ReconciliationInterruptedException(
                    "The target transport session changed while reading the safety guards.");
            }

            var token = guard.TargetPath.ToString();
            if (originals.ContainsKey(token))
            {
                continue;
            }

            var snapshot = await target.SnapshotAsync(token, cancellationToken).ConfigureAwait(false);
            if (!IsTargetTransportCurrent(target, expectedTransportGeneration))
            {
                throw new ReconciliationInterruptedException(
                    "The target transport session changed after reading a safety guard.");
            }

            var exact = snapshot
                .Where(parameter =>
                    parameter.TokenPath.Equals(token, StringComparison.Ordinal))
                .ToArray();
            if (exact.Length != 1 || exact[0].Value.Type != guard.Value.Type)
            {
                throw new IOException(
                    $"De oorspronkelijke processorstand voor safetyguard {token} " +
                    "could not be read unambiguously and type-safely.");
            }

            originals[token] = exact[0].Value;
        }

        return new ReadOnlyDictionary<string, WingValue>(originals);
    }

    private bool IsTargetTransportCurrent(
        IWingSession expectedTarget,
        long expectedTransportGeneration) =>
        ReferenceEquals(expectedTarget, targetSession) &&
        AreTransportSessionsConnected() &&
        Interlocked.Read(ref transportGeneration) == expectedTransportGeneration;

    private async Task<bool> TryHoldSafetyGuardsAsync(
        IWingSession target,
        SafetyGuardExecutionContext context)
    {
        if (!IsTargetTransportCurrent(target, context.TransportGeneration))
        {
            return false;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var safeWrites = context.Guards.Values
                .OrderBy(static guard => guard.TargetPath.ToString(), StringComparer.Ordinal)
                .Select(static guard =>
                    new WingWriteRequest(guard.TargetPath.ToString(), guard.Value))
                .ToArray();
            await target.SetManyAsync(safeWrites, timeout.Token).ConfigureAwait(false);
            if (!IsTargetTransportCurrent(target, context.TransportGeneration))
            {
                return false;
            }

            var tolerance = configuration?.Safety.FloatTolerance ?? 0.0001F;
            foreach (var guard in context.Guards.Values)
            {
                var token = guard.TargetPath.ToString();
                var actual = await ReadExactScalarAsync(target, token, timeout.Token)
                    .ConfigureAwait(false);
                if (actual.Type != guard.Value.Type ||
                    !WingValueComparer.AreEquivalent(guard.Value, actual, tolerance))
                {
                    return false;
                }

                targetState[token] = actual;
                context.ActivePaths.Add(token);
            }

            return IsTargetTransportCurrent(target, context.TransportGeneration);
        }
        catch (Exception holdException)
        {
            Record(
                DiagnosticSeverity.Critical,
                "SAFETY_GUARD_HOLD_FAILED",
                holdException.Message,
                "Safety");
            return false;
        }
    }

    private async Task<bool> TryRestoreSafetyGuardsAsync(
        IWingSession target,
        SafetyGuardExecutionContext context)
    {
        if (!ReferenceEquals(target, targetSession) ||
            !AreTransportSessionsConnected() ||
            Interlocked.Read(ref transportGeneration) != context.TransportGeneration)
        {
            return false;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var tolerance = configuration?.Safety.FloatTolerance ?? 0.0001F;
            var restores = new List<WingWriteRequest>();
            foreach (var token in context.ActivePaths.Order(StringComparer.Ordinal))
            {
                var original = context.Originals[token];
                var snapshot = await target.SnapshotAsync(token, timeout.Token).ConfigureAwait(false);
                var current = snapshot.FirstOrDefault(parameter =>
                    parameter.TokenPath.Equals(token, StringComparison.Ordinal));
                if (current is null ||
                    !WingValueComparer.AreEquivalent(original, current.Value, tolerance))
                {
                    restores.Add(new WingWriteRequest(token, original));
                }
            }

            if (!AreTransportSessionsConnected() ||
                Interlocked.Read(ref transportGeneration) != context.TransportGeneration)
            {
                return false;
            }

            if (restores.Count > 0)
            {
                await target.SetManyAsync(restores, timeout.Token).ConfigureAwait(false);
            }

            if (!AreTransportSessionsConnected() ||
                Interlocked.Read(ref transportGeneration) != context.TransportGeneration)
            {
                return false;
            }

            foreach (var token in context.ActivePaths.Order(StringComparer.Ordinal))
            {
                var original = context.Originals[token];
                var snapshot = await target.SnapshotAsync(token, timeout.Token).ConfigureAwait(false);
                var actual = snapshot.FirstOrDefault(parameter =>
                    parameter.TokenPath.Equals(token, StringComparison.Ordinal));
                if (actual is null ||
                    !WingValueComparer.AreEquivalent(original, actual.Value, tolerance))
                {
                    return false;
                }

                targetState[token] = actual.Value;
            }

            if (!IsTargetTransportCurrent(target, context.TransportGeneration))
            {
                return false;
            }

            context.ActivePaths.Clear();
            return true;
        }
        catch (Exception exception)
        {
            Record(
                DiagnosticSeverity.Critical,
                "SAFETY_GUARD_ROLLBACK_FAILED",
                exception.Message,
                "Safety");
            return false;
        }
    }

    private Dictionary<string, ReadbackWaiter> RegisterReadbacks(IReadOnlyList<PlannedWrite> writes)
    {
        var result = new Dictionary<string, ReadbackWaiter>(StringComparer.Ordinal);
        foreach (var write in writes.Where(static item => item.RequiresReadback))
        {
            var waiter = new ReadbackWaiter(write.Value);
            readbackWaiters[write.TargetPath.ToString()] = waiter;
            result[write.TargetPath.ToString()] = waiter;
        }

        return result;
    }

    private async Task VerifyBatchAsync(
        IReadOnlyList<PlannedWrite> writes,
        IReadOnlyDictionary<string, ReadbackWaiter> waiters,
        CancellationToken cancellationToken)
    {
        if (waiters.Count == 0)
        {
            return;
        }

        var currentTarget = targetSession ?? throw new InvalidOperationException("Target session is missing.");
        var currentConfiguration = configuration ??
            throw new InvalidOperationException("Configuration is missing.");

        foreach (var write in writes.Where(static item => item.RequiresReadback))
        {
            var token = write.TargetPath.ToString();
            // An event is only a latency hint: it can have been queued before SetMany
            // dispatch and arrive after the waiter was registered. Always perform a fresh
            // scalar readback after dispatch so a delayed matching event can never certify
            // a dropped, transformed, or otherwise incorrect live write.
            var readback = await currentTarget.SnapshotAsync(token, cancellationToken).ConfigureAwait(false);
            var actual = readback.FirstOrDefault(parameter =>
                string.Equals(parameter.TokenPath, token, StringComparison.Ordinal));
            if (actual is null ||
                !WingValueComparer.AreEquivalent(
                    write.Value,
                    actual.Value,
                    currentConfiguration.Safety.FloatTolerance))
            {
                var message = actual is null
                    ? $"No readback received for {token}."
                    : $"Readback for {token} differs: expected {write.Value}, received {actual.Value}.";
                PauseForSafety("READBACK_MISMATCH", message);
                throw new WriteVerificationException(token, write.Value, actual?.Value, message);
            }

            targetState[token] = actual.Value;
            readbackWaiters.TryRemove(token, out _);
        }
    }

    private async Task ProcessWorkAsync(CancellationToken cancellationToken)
    {
        var channel = workChannel ?? throw new InvalidOperationException("Event channel is missing.");
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!channel.Reader.TryRead(out var first))
                {
                    continue;
                }

                Interlocked.Decrement(ref queueDepth);
                var currentConfiguration = configuration;
                if (currentConfiguration is null)
                {
                    continue;
                }

                await clock.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
                var coalesced = new Dictionary<string, EngineWork>(StringComparer.Ordinal)
                {
                    [first.Parameter.TokenPath] = first,
                };
                while (channel.Reader.TryRead(out var next))
                {
                    Interlocked.Decrement(ref queueDepth);
                    if (!coalesced.TryGetValue(next.Parameter.TokenPath, out var existing) ||
                        existing.Origin == EngineWorkOrigin.TargetDrift ||
                        next.Origin == EngineWorkOrigin.SourceEvent)
                    {
                        coalesced[next.Parameter.TokenPath] = next;
                    }
                }

                if (!acceptEvents)
                {
                    continue;
                }

                var epoch = Volatile.Read(ref activeEpoch);
                var valid = coalesced.Values
                    .Where(work => work.Epoch == epoch)
                    .OrderBy(static work => work.Parameter.ObservedAt)
                    .ToArray();
                if (valid.Length == 0)
                {
                    continue;
                }

                var reconcileEntered = false;
                try
                {
                    await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    reconcileEntered = true;
                    if (!acceptEvents || safetyPaused || stopping)
                    {
                        continue;
                    }

                    IReadOnlyList<WingParameter>? expanded = null;
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        var expansionFence = GetEventSequences();
                        var candidate = await ExpandModelChangesAsync(valid, cancellationToken)
                            .ConfigureAwait(false);
                        if (EventFenceIsCurrent(expansionFence))
                        {
                            expanded = candidate;
                            break;
                        }

                        Record(
                            DiagnosticSeverity.Trace,
                            "SOURCE_TUPLE_INVALIDATED",
                            $"A console event changed the source during group readback; " +
                            $"coherente herlezing {attempt}/3.",
                            "Synchronization");
                    }

                    if (expanded is null)
                    {
                        throw new ReconciliationInterruptedException(
                            "The source kept changing during group readback; no writes were sent.");
                    }

                    foreach (var parameter in expanded)
                    {
                        sourceState[parameter.TokenPath] = parameter.Value;
                    }

                    await StoreSourceEventsAsync(expanded, cancellationToken).ConfigureAwait(false);
                    var changes = expanded
                        .Where(parameter => IsEnabledScopedToken(parameter.TokenPath))
                        .Select(parameter => new SyncChange(
                            TokenPath.Parse(parameter.TokenPath),
                            parameter.Value,
                            parameter.ObservedAt,
                            Interlocked.Increment(ref sourceRevision)))
                        .ToArray();
                    var plan = planner.Plan(
                        changes,
                        currentConfiguration.Channels,
                        currentConfiguration.Scopes,
                        currentConfiguration.Safety,
                        clock.UtcNow);
                    UpdateDesiredTargets(plan);
                    plan = FilterUnchangedWrites(plan);
                    RecordPlanningFindings(plan);
                    await ExecuteOrPreviewPlanAsync(
                            plan,
                            cancellationToken,
                            requireActiveEventIntake: true)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ReconciliationInterruptedException exception)
                {
                    Record(
                        DiagnosticSeverity.Warning,
                        "SYNC_BATCH_INTERRUPTED",
                        exception.Message,
                        "Synchronization");
                    if (acceptEvents &&
                        !safetyPaused &&
                        !stopping &&
                        AreTransportSessionsConnected())
                    {
                        // The work has already been drained from the bounded channel.
                        // Requeue every original trigger immediately; expansion scalar-
                        // rereads each token, so a partial earlier unit is harmless and
                        // no correction has to wait for the rotating health poll.
                        foreach (var work in valid)
                        {
                            Enqueue(work with { Epoch = Volatile.Read(ref activeEpoch) });
                        }
                    }
                }
                catch (Exception exception)
                {
                    PauseForSafety("SYNC_BATCH_FAILED", exception.Message);
                }
                finally
                {
                    if (reconcileEntered)
                    {
                        reconcileGate.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop.
        }
    }

    private async Task<IReadOnlyList<WingParameter>> ExpandModelChangesAsync(
        IReadOnlyList<EngineWork> works,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, WingParameter>(StringComparer.Ordinal);
        var refreshedGroups = new HashSet<string>(StringComparer.Ordinal);
        var currentSource = sourceSession ?? throw new InvalidOperationException("Source session is missing.");
        var groupedWorks = works
            .Select(work => (
                Work: work,
                Path: TokenPath.Parse(work.Parameter.TokenPath)))
            .Where(static item => GetDynamicGroup(item.Path) is not null)
            .GroupBy(
                static item => GetDynamicGroup(item.Path)!,
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        foreach (var work in works)
        {
            var path = TokenPath.Parse(work.Parameter.TokenPath);
            if (IsDelayPath(path))
            {
                var delayGroup = GetDynamicGroup(path) ??
                    throw new InvalidOperationException(
                        $"Delay token {path} has no coherent processor group.");
                if (refreshedGroups.Add(delayGroup))
                {
                    var delayRoot = $"/{path.Segments[0]}/{path.Segments[1]}/in/set";
                    var delayTokens = DelayTupleLeaves
                        .Select(leaf => $"{delayRoot}/{leaf}")
                        .ToArray();
                    var stableTuple = await ReadStableExactGroupAsync(
                            currentSource,
                            delayTokens,
                            cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var parameter in stableTuple)
                    {
                        result[parameter.TokenPath] = parameter;
                    }
                }

                continue;
            }

            var processorRefresh = GetRuntimeProcessorRefresh(path);
            if (processorRefresh is not null)
            {
                if (refreshedGroups.Add(processorRefresh.Key))
                {
                    var relatedWorks = groupedWorks.TryGetValue(
                        processorRefresh.Key,
                        out var grouped)
                        ? grouped
                        : [(Work: work, Path: path)];
                    var requiredTokens = GetCanonicalEnablePaths(path)
                        .Select(static token => token.ToString())
                        .Append(processorRefresh.RequiredSidechainToken)
                        .Concat(relatedWorks.Select(static item =>
                            item.Work.Parameter.TokenPath))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var requiresCompleteGroup =
                        relatedWorks.Any(static item =>
                            IsGuardedMutationPath(item.Path) ||
                            ScopeCatalog.IsSidechainSourceToken(item.Path));
                    var refreshed = requiresCompleteGroup
                        ? await ReadStableDiscoveredGroupAsync(
                                currentSource,
                                processorRefresh.Nodes,
                                requiredTokens,
                                processorRefresh.Key,
                                cancellationToken)
                            .ConfigureAwait(false)
                        : await ReadStableExactGroupAsync(
                                currentSource,
                                requiredTokens,
                                cancellationToken)
                            .ConfigureAwait(false);
                    foreach (var parameter in refreshed)
                    {
                        result[parameter.TokenPath] = parameter;
                    }
                }

                continue;
            }

            if (path.Leaf.Equals("mdl", StringComparison.OrdinalIgnoreCase) &&
                path.Segments.Count >= 3)
            {
                var dynamicGroup = GetDynamicGroup(path) ??
                    throw new IOException(
                        $"Model token {path} has no unambiguous physical processor group.");
                if (refreshedGroups.Add(dynamicGroup))
                {
                    var enableTokens = GetCanonicalEnablePaths(path)
                        .Select(static token => token.ToString())
                        .Append(work.Parameter.TokenPath)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (enableTokens.Length == 0)
                    {
                        throw new IOException(
                            $"No authoritative enable scalar found for model group {dynamicGroup}; " +
                            "the model write remains blocked.");
                    }

                    foreach (var parameter in await ReadStableDiscoveredGroupAsync(
                                 currentSource,
                                 [dynamicGroup],
                                 enableTokens,
                                 dynamicGroup,
                                 cancellationToken)
                                 .ConfigureAwait(false))
                    {
                        result[parameter.TokenPath] = parameter;
                    }
                }

                // The trigger token is part of the scalar-certified stable group above.
            }
            else if (work.Origin == EngineWorkOrigin.TargetDrift)
            {
                var snapshot = await currentSource
                    .SnapshotAsync(work.Parameter.TokenPath, cancellationToken)
                    .ConfigureAwait(false);
                var authoritative = snapshot.FirstOrDefault(parameter =>
                    parameter.TokenPath.Equals(
                        work.Parameter.TokenPath,
                        StringComparison.Ordinal));
                if (authoritative is null)
                {
                    throw new IOException(
                        $"The current source value for target drift {work.Parameter.TokenPath} could not be read.");
                }

                result[authoritative.TokenPath] = authoritative;
            }
            else
            {
                var authoritative = await ReadExactParameterAsync(
                        currentSource,
                        work.Parameter.TokenPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                result[authoritative.TokenPath] = authoritative;
            }
        }

        return result.Values
            .OrderBy(static parameter => parameter.TokenPath, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<WingParameter>> ReadStableDiscoveredGroupAsync(
        IWingSession session,
        IReadOnlyList<string> nodes,
        IReadOnlyList<string> requiredTokens,
        string dynamicGroup,
        CancellationToken cancellationToken)
    {
        var tolerance = configuration?.Safety.FloatTolerance ?? 0.0001F;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var first = await ReadScalarCertifiedGroupPassAsync(
                    session,
                    nodes,
                    requiredTokens,
                    dynamicGroup,
                    cancellationToken)
                .ConfigureAwait(false);
            var second = await ReadScalarCertifiedGroupPassAsync(
                    session,
                    nodes,
                    requiredTokens,
                    dynamicGroup,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!first.ModelMirrorMismatch &&
                !second.ModelMirrorMismatch &&
                ParameterMapsAreEquivalent(
                    first.Values,
                    second.Values,
                    tolerance))
            {
                return second.Values.Values
                    .OrderBy(static item => item.TokenPath, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        throw new SnapshotUnstableException(
            $"Processor group {dynamicGroup} kept changing during duplicate exact reads; " +
            "no writes were sent.");
    }

    private async Task<StableGroupPass> ReadScalarCertifiedGroupPassAsync(
        IWingSession session,
        IReadOnlyList<string> nodes,
        IReadOnlyList<string> requiredTokens,
        string dynamicGroup,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, WingParameter>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var snapshot = await session.SnapshotAsync(node, cancellationToken)
                .ConfigureAwait(false);
            foreach (var parameter in snapshot.Where(parameter =>
                         IsEnabledScopedToken(parameter.TokenPath) &&
                         string.Equals(
                             GetDynamicGroup(TokenPath.Parse(parameter.TokenPath)),
                             dynamicGroup,
                             StringComparison.Ordinal)))
            {
                candidates[parameter.TokenPath] = parameter;
            }
        }

        var certified = new Dictionary<string, WingParameter>(StringComparer.Ordinal);
        var modelMirrorMismatch = false;
        var tolerance = configuration?.Safety.FloatTolerance ?? 0.0001F;
        var tokens = candidates.Keys
            .Concat(requiredTokens)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var token in tokens)
        {
            var exact = await ReadExactParameterAsync(session, token, cancellationToken)
                .ConfigureAwait(false);
            certified[token] = exact;
            if (TokenPath.Parse(token).Leaf.Equals("mdl", StringComparison.OrdinalIgnoreCase) &&
                candidates.TryGetValue(token, out var mirrored) &&
                (mirrored.Value.Type != exact.Value.Type ||
                 !WingValueComparer.AreEquivalent(
                     mirrored.Value,
                     exact.Value,
                     tolerance)))
            {
                modelMirrorMismatch = true;
            }
        }

        return new StableGroupPass(
            new ReadOnlyDictionary<string, WingParameter>(certified),
            modelMirrorMismatch);
    }

    private static async Task<IReadOnlyList<WingParameter>> ReadExactGroupOnceAsync(
        IWingSession session,
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken)
    {
        var result = new List<WingParameter>(tokens.Count);
        foreach (var token in tokens)
        {
            result.Add(await ReadExactParameterAsync(session, token, cancellationToken)
                .ConfigureAwait(false));
        }

        return result;
    }

    private async Task<IReadOnlyList<WingParameter>> ReadStableExactGroupAsync(
        IWingSession session,
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken)
    {
        var tolerance = configuration?.Safety.FloatTolerance ?? 0.0001F;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var first = await ReadExactGroupOnceAsync(session, tokens, cancellationToken)
                .ConfigureAwait(false);
            var second = await ReadExactGroupOnceAsync(session, tokens, cancellationToken)
                .ConfigureAwait(false);
            if (first.Count == second.Count &&
                first.Zip(second).All(pair =>
                    pair.First.TokenPath.Equals(
                        pair.Second.TokenPath,
                        StringComparison.Ordinal) &&
                    pair.First.Value.Type == pair.Second.Value.Type &&
                    WingValueComparer.AreEquivalent(
                        pair.First.Value,
                        pair.Second.Value,
                        tolerance)))
            {
                return second;
            }
        }

        throw new IOException(
            $"De scalargroep ({string.Join(", ", tokens)}) bleef wijzigen tijdens drie " +
            "gezaghebbende dubbele reads; no writes were sent.");
    }

    private static async Task<WingParameter> ReadExactParameterAsync(
        IWingSession session,
        string token,
        CancellationToken cancellationToken)
    {
        var snapshot = await session.SnapshotAsync(token, cancellationToken).ConfigureAwait(false);
        var exact = snapshot
            .Where(parameter =>
                parameter.TokenPath.Equals(token, StringComparison.Ordinal))
            .ToArray();
        if (exact.Length != 1)
        {
            throw new IOException(
                $"Scalar {token} could not be read unambiguously and authoritatively.");
        }

        return exact[0];
    }

    private static RuntimeProcessorRefresh? GetRuntimeProcessorRefresh(TokenPath path)
    {
        if (path.Segments.Count < 4)
        {
            return null;
        }

        var channelRoot = $"/{path.Segments[0]}/{path.Segments[1]}";
        var isInput = path.Segments[0].Equals("ch", StringComparison.OrdinalIgnoreCase);
        var isAux = path.Segments[0].Equals("aux", StringComparison.OrdinalIgnoreCase);
        return (isInput, isAux, path.Segments[2].ToLowerInvariant()) switch
        {
            (true, _, "gate" or "gatesc") => new RuntimeProcessorRefresh(
                $"{channelRoot}/gate",
                [$"{channelRoot}/gate", $"{channelRoot}/gatesc"],
                $"{channelRoot}/gatesc/src"),
            (true, _, "dyn" or "dynxo" or "dynsc") => new RuntimeProcessorRefresh(
                $"{channelRoot}/dyn",
                [$"{channelRoot}/dyn", $"{channelRoot}/dynxo", $"{channelRoot}/dynsc"],
                $"{channelRoot}/dynsc/src"),
            (_, true, "dyn" or "dynsc") => new RuntimeProcessorRefresh(
                $"{channelRoot}/dyn",
                [$"{channelRoot}/dyn", $"{channelRoot}/dynsc"],
                $"{channelRoot}/dynsc/src"),
            _ => null,
        };
    }

    private async Task StoreSourceEventsAsync(
        IReadOnlyList<WingParameter> parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.Count == 0)
        {
            return;
        }

        var sourceIsFoh = ReferenceEquals(sourceSession, fohSession);
        var identity = sourceIsFoh
            ? fohIdentity ?? throw new InvalidOperationException("FOH identity is missing.")
            : monitorIdentity ?? throw new InvalidOperationException("Stage identity is missing.");
        await stateSink.StoreAsync(
                sourceIsFoh ? "FOH" : "Stage",
                identity,
                activeEpoch,
                parameters,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HealthLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await clock.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                if (stopping || Volatile.Read(ref reconnectStarted) != 0)
                {
                    continue;
                }

                try
                {
                    var currentFoh = fohSession ?? throw new IOException("FOH session is missing.");
                    var currentMonitor = monitorSession ?? throw new IOException("Stage session is missing.");
                    await Task.WhenAll(
                            currentFoh.PingAsync(cancellationToken),
                            currentMonitor.PingAsync(cancellationToken))
                        .ConfigureAwait(false);
                    await Task.WhenAll(
                            PollSourceForMissedEventsAsync(cancellationToken),
                            PollTargetForMissedDriftAsync(cancellationToken))
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    TriggerReconnect($"Health check failed: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop.
        }
    }

    private async Task PollSourceForMissedEventsAsync(CancellationToken cancellationToken)
    {
        if (!acceptEvents || safetyPaused || stopping)
        {
            return;
        }

        var currentConfiguration = configuration ??
            throw new InvalidOperationException("Configuration is missing.");
        var currentSource = sourceSession ??
            throw new InvalidOperationException("Source session is missing.");
        var capturedEpoch = Volatile.Read(ref activeEpoch);
        var capturedTransport = Interlocked.Read(ref transportGeneration);
        long capturedSourceSequence;
        lock (eventIntakeSync)
        {
            if (!acceptEvents || safetyPaused || stopping)
            {
                return;
            }

            capturedSourceSequence = sourceEventSequence;
        }

        // Bounded WAPI I/O deliberately runs outside reconcileGate. A real console
        // event must never wait behind a channel sweep. Roots and exact scalars rotate
        // across ticks; a hard deadline protects the live console command path.
        var roots = SelectReconciliationRoots(
            GetSourceReconciliationRoots(currentConfiguration));
        var pollStopwatch = Stopwatch.StartNew();
        // The budget limits only later dispatches. Never cancel an in-flight WAPI
        // request here: WapiProcessSession deliberately treats caller cancellation
        // as a transport fault and recycles its isolated helper process.
        var snapshot = await ReadSourceReconciliationSnapshotAsync(
                currentSource,
                roots,
                currentConfiguration,
                pollStopwatch,
                cancellationToken)
            .ConfigureAwait(false);

        if (pollStopwatch.Elapsed >= ReconciliationTimeBudget &&
            !cancellationToken.IsCancellationRequested)
        {
            RaiseSourceReconciliationBudgetWarning();
        }

        var reconcileEntered = false;
        try
        {
            await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            reconcileEntered = true;
            if (!acceptEvents || safetyPaused || stopping ||
                Volatile.Read(ref reconnectStarted) != 0 ||
                !ReferenceEquals(configuration, currentConfiguration) ||
                !ReferenceEquals(sourceSession, currentSource) ||
                Volatile.Read(ref activeEpoch) != capturedEpoch ||
                Interlocked.Read(ref transportGeneration) != capturedTransport)
            {
                return;
            }

            var tolerance = currentConfiguration.Safety.FloatTolerance;
            lock (eventIntakeSync)
            {
                if (!acceptEvents ||
                    sourceEventSequence != capturedSourceSequence ||
                    Volatile.Read(ref activeEpoch) != capturedEpoch ||
                    Interlocked.Read(ref transportGeneration) != capturedTransport)
                {
                    return;
                }

                var recovered = snapshot.Where(parameter =>
                        IsEnabledMappedToken(parameter.TokenPath, sourceSide: true) &&
                        (!sourceState.TryGetValue(parameter.TokenPath, out var known) ||
                         !WingValueComparer.AreEquivalent(
                             known,
                             parameter.Value,
                             tolerance)))
                    .ToArray();
                if (recovered.Length > 0)
                {
                    Record(
                        DiagnosticSeverity.Warning,
                        "MISSED_SOURCE_EVENT_RECOVERED",
                        $"{recovered.Length} source change(s) were recovered by the periodic WAPI check.",
                        "Synchronization");
                }

                foreach (var parameter in recovered)
                {
                    checked
                    {
                        sourceEventSequence++;
                    }

                    Enqueue(
                        new EngineWork(
                            parameter,
                            capturedEpoch,
                            EngineWorkOrigin.SourceEvent));
                }
            }
        }
        finally
        {
            if (reconcileEntered)
            {
                reconcileGate.Release();
            }
        }
    }

    private async Task PollTargetForMissedDriftAsync(CancellationToken cancellationToken)
    {
        if (!acceptEvents || safetyPaused || stopping)
        {
            return;
        }

        var currentConfiguration = configuration ??
            throw new InvalidOperationException("Configuration is missing.");
        var currentTarget = targetSession ??
            throw new InvalidOperationException("Target session is missing.");
        var capturedEpoch = Volatile.Read(ref activeEpoch);
        var capturedTransport = Interlocked.Read(ref transportGeneration);
        var capturedSourceRevision = Interlocked.Read(ref sourceRevision);
        long capturedTargetSequence;
        lock (eventIntakeSync)
        {
            if (!acceptEvents || safetyPaused || stopping)
            {
                return;
            }

            capturedTargetSequence = targetEventSequence;
        }

        var selectedDesired = SelectTargetReconciliationScalars(
            desiredTargets.Values
                .OrderBy(static desired => desired.TargetPath.ToString(), StringComparer.Ordinal)
                .ToArray());
        if (selectedDesired.Length == 0)
        {
            return;
        }

        var capturedDesired = selectedDesired.ToDictionary(
            static desired => desired.TargetPath.ToString(),
            StringComparer.Ordinal);
        var observed = new List<WingParameter>(selectedDesired.Length);
        var pollStopwatch = Stopwatch.StartNew();
        foreach (var desired in selectedDesired)
        {
            if (pollStopwatch.Elapsed >= ReconciliationTimeBudget)
            {
                break;
            }

            var token = desired.TargetPath.ToString();
            var snapshot = await currentTarget
                .SnapshotAsync(token, cancellationToken)
                .ConfigureAwait(false);
            var exact = snapshot.FirstOrDefault(parameter =>
                parameter.TokenPath.Equals(token, StringComparison.Ordinal));
            if (exact is not null)
            {
                observed.Add(exact);
            }
        }

        if (pollStopwatch.Elapsed >= ReconciliationTimeBudget &&
            !cancellationToken.IsCancellationRequested)
        {
            RaiseTargetReconciliationBudgetWarning();
        }

        var reconcileEntered = false;
        try
        {
            await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            reconcileEntered = true;
            if (!acceptEvents || safetyPaused || stopping ||
                Volatile.Read(ref reconnectStarted) != 0 ||
                !ReferenceEquals(configuration, currentConfiguration) ||
                !ReferenceEquals(targetSession, currentTarget) ||
                Volatile.Read(ref activeEpoch) != capturedEpoch ||
                Interlocked.Read(ref transportGeneration) != capturedTransport ||
                Interlocked.Read(ref sourceRevision) != capturedSourceRevision)
            {
                return;
            }

            var tolerance = currentConfiguration.Safety.FloatTolerance;
            lock (eventIntakeSync)
            {
                if (!acceptEvents ||
                    targetEventSequence != capturedTargetSequence ||
                    Volatile.Read(ref activeEpoch) != capturedEpoch ||
                    Interlocked.Read(ref transportGeneration) != capturedTransport ||
                    Interlocked.Read(ref sourceRevision) != capturedSourceRevision ||
                    capturedDesired.Any(pair =>
                        !desiredTargets.TryGetValue(pair.Key, out var current) ||
                        !Equals(current, pair.Value)))
                {
                    return;
                }

                var drifted = observed
                    .Where(parameter =>
                        capturedDesired.TryGetValue(parameter.TokenPath, out var desired) &&
                        !WingValueComparer.AreEquivalent(
                            desired.Value,
                            parameter.Value,
                            tolerance))
                    .ToArray();
                if (drifted.Length > 0)
                {
                    Record(
                        DiagnosticSeverity.Warning,
                        "MISSED_TARGET_EVENT_RECOVERED",
                        $"{drifted.Length} missed target change(s) were found by exact WAPI readback.",
                        "Synchronization");
                }

                foreach (var parameter in drifted)
                {
                    checked
                    {
                        targetEventSequence++;
                    }

                    targetState[parameter.TokenPath] = parameter.Value;
                    _ = StoreTargetEventSafeAsync(parameter);
                    QueueTargetDrift(parameter, currentConfiguration);
                }
            }
        }
        finally
        {
            if (reconcileEntered)
            {
                reconcileGate.Release();
            }
        }
    }

    private void RaiseTargetReconciliationBudgetWarning()
    {
        if (Interlocked.Exchange(
                ref targetReconciliationBudgetWarningRaised,
                1) != 0)
        {
            return;
        }

        Record(
            DiagnosticSeverity.Warning,
            "TARGET_RECONCILIATION_BUDGET_EXCEEDED",
            "De target-driftcontrole bereikte haar harde WAPI-readbudget van 1,2 s; " +
            "this cycle was aborted and the remaining scalars will follow later.",
            "Synchronization");
    }

    private DesiredTarget[] SelectTargetReconciliationScalars(DesiredTarget[] desired)
    {
        if (desired.Length == 0)
        {
            return [];
        }

        var critical = desired
            .Where(static item => IsCriticalTargetReconciliationPath(item.TargetPath))
            .ToArray();
        var general = desired
            .Where(static item => !IsCriticalTargetReconciliationPath(item.TargetPath))
            .ToArray();
        var selected = new List<DesiredTarget>(TargetReconciliationScalarBudget);
        var criticalCount = Math.Min(
            Math.Min(4, critical.Length),
            TargetReconciliationScalarBudget);
        selected.AddRange(SelectRotatingDesiredTargets(
            critical,
            criticalCount,
            ref targetReconciliationCriticalCursor));

        var remaining = TargetReconciliationScalarBudget - selected.Count;
        var generalCount = Math.Min(remaining, general.Length);
        selected.AddRange(SelectRotatingDesiredTargets(
            general,
            generalCount,
            ref targetReconciliationGeneralCursor));

        remaining = TargetReconciliationScalarBudget - selected.Count;
        if (remaining > 0 && selected.Count < critical.Length)
        {
            selected.AddRange(SelectRotatingDesiredTargets(
                critical,
                Math.Min(remaining, critical.Length - selected.Count),
                ref targetReconciliationCriticalCursor));
        }

        return selected.ToArray();
    }

    private static DesiredTarget[] SelectRotatingDesiredTargets(
        DesiredTarget[] values,
        int count,
        ref int cursor)
    {
        if (count == 0 || values.Length == 0)
        {
            return [];
        }

        var start = cursor % values.Length;
        var selected = new DesiredTarget[count];
        for (var offset = 0; offset < count; offset++)
        {
            selected[offset] = values[(start + offset) % values.Length];
        }

        cursor = (start + count) % values.Length;
        return selected;
    }

    private static bool IsCriticalTargetReconciliationPath(TokenPath path) =>
        IsEnablePath(path) ||
        path.Leaf.Equals("mdl", StringComparison.OrdinalIgnoreCase) ||
        (path.Segments.Count >= 4 &&
         path.Leaf.Equals("src", StringComparison.OrdinalIgnoreCase) &&
         (path.Segments[2].Equals("gatesc", StringComparison.OrdinalIgnoreCase) ||
          path.Segments[2].Equals("dynsc", StringComparison.OrdinalIgnoreCase))) ||
        IsDelayPath(path);

    private async Task<IReadOnlyList<WingParameter>> ReadSourceReconciliationSnapshotAsync(
        IWingSession source,
        IReadOnlyList<TokenPath> roots,
        AppConfiguration currentConfiguration,
        Stopwatch pollStopwatch,
        CancellationToken cancellationToken)
    {
        var rootValues = new Dictionary<string, WingParameter>(StringComparer.Ordinal);
        var exactValues = new Dictionary<string, WingParameter>(StringComparer.Ordinal);
        var fallbackNodes = new Dictionary<string, TokenPath>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (pollStopwatch.Elapsed >= ReconciliationTimeBudget)
            {
                break;
            }

            var snapshot = await source.SnapshotAsync(root.ToString(), cancellationToken)
                .ConfigureAwait(false);
            var isInput = root.Segments[0].Equals("ch", StringComparison.OrdinalIgnoreCase);
            var minimumExpected = isInput ? 80 : 30;
            if (snapshot.Count < minimumExpected)
            {
                // Some firmware replies occasionally resolve a channel-root request as
                // a scalar. Fall back only for this bounded root; one weak response
                // must never expand into a full 40 CH + 8 AUX sweep.
                foreach (var node in GetSnapshotNodes(
                             currentConfiguration,
                             sourceSide: true)
                         .Where(node => node.StartsWith(root.Segments.ToArray())))
                {
                    fallbackNodes[node.ToString()] = node;
                }

                continue;
            }

            foreach (var parameter in snapshot)
            {
                rootValues[parameter.TokenPath] = parameter;
            }
        }

        if (fallbackNodes.Count > 0)
        {
            var selectedFallbackNodes = SelectReconciliationFallbackNodes(
                fallbackNodes.Values
                    .OrderBy(static node => node.ToString(), StringComparer.Ordinal)
                    .ToArray());
            foreach (var node in selectedFallbackNodes)
            {
                if (pollStopwatch.Elapsed >= ReconciliationTimeBudget)
                {
                    break;
                }

                var fallback = await source
                    .SnapshotAsync(node.ToString(), cancellationToken)
                    .ConfigureAwait(false);
                foreach (var parameter in fallback)
                {
                    rootValues[parameter.TokenPath] = parameter;
                }
            }
        }

        // WAPI 3.1 firmware can omit events for changes made through another remote
        // protocol, while a node snapshot may still reflect its local mirror. Poll a
        // bounded, rotating set of exact scalars to force authoritative console reads.
        // Events remain the low-latency path; this is the missed-event safety net.
        var scalarTokens = sourceState.Keys
            .Where(token => IsEnabledMappedToken(token, sourceSide: true))
            .OrderBy(static token => token, StringComparer.Ordinal)
            .ToArray();
        if (scalarTokens.Length > 0)
        {
            var selectedTokens = SelectReconciliationScalarTokens(
                scalarTokens,
                roots,
                currentConfiguration);
            var selectedSet = selectedTokens.ToHashSet(StringComparer.Ordinal);
            var candidateTokens = rootValues.Values
                .Where(parameter =>
                    IsEnabledMappedToken(parameter.TokenPath, sourceSide: true) &&
                    (!sourceState.TryGetValue(parameter.TokenPath, out var known) ||
                     !WingValueComparer.AreEquivalent(
                         known,
                         parameter.Value,
                         currentConfiguration.Safety.FloatTolerance)))
                .Select(static parameter => parameter.TokenPath)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static token => token, StringComparer.Ordinal)
                .ToArray();
            var replacementOffset = 0;
            foreach (var candidate in SelectReconciliationCandidateTokens(candidateTokens))
            {
                if (!selectedSet.Add(candidate))
                {
                    continue;
                }

                // A changed node hint deserves an immediate authoritative scalar
                // check, but may never expand the fixed per-cycle WAPI budget.
                if (selectedTokens.Count < ReconciliationScalarBudget)
                {
                    selectedTokens.Add(candidate);
                    continue;
                }

                var replacementIndex = selectedTokens.Count - 1 - replacementOffset;
                if (replacementIndex < 0)
                {
                    selectedSet.Remove(candidate);
                    break;
                }

                var displaced = selectedTokens[replacementIndex];
                selectedSet.Remove(displaced);
                selectedTokens[replacementIndex] = candidate;
                replacementOffset++;
            }

            if (Interlocked.CompareExchange(ref reconciliationPollReported, 1, 0) == 0)
            {
                Record(
                    DiagnosticSeverity.Trace,
                    "RECONCILIATION_SCALAR_SET",
                    $"Exact fallback reads {selectedTokens.Count} scalars; " +
                    $"first tokens: {string.Join(", ", selectedTokens.Take(16))}.",
                    "Synchronization");
            }

            foreach (var token in selectedTokens)
            {
                if (pollStopwatch.Elapsed >= ReconciliationTimeBudget)
                {
                    break;
                }

                var scalar = await source.SnapshotAsync(token, cancellationToken)
                    .ConfigureAwait(false);
                var exact = scalar.FirstOrDefault(parameter =>
                    parameter.TokenPath.Equals(token, StringComparison.Ordinal));
                if (exact is not null)
                {
                    exactValues[token] = exact;
                }
            }
        }

        // Node reads are hints only: WAPI can expose a stale local mirror after a
        // change through another protocol. Only explicit scalar reads may become
        // recovered source events, preventing old node data from flapping live state.
        return exactValues.Values
            .OrderBy(static parameter => parameter.TokenPath, StringComparer.Ordinal)
            .ToArray();
    }

    private void RaiseSourceReconciliationBudgetWarning()
    {
        if (Interlocked.Exchange(ref reconciliationBudgetWarningRaised, 1) != 0)
        {
            return;
        }

        Record(
            DiagnosticSeverity.Warning,
            "RECONCILIATION_BUDGET_EXCEEDED",
            "De gemiste-eventcontrole bereikte haar harde WAPI-readbudget van 1,2 s; " +
            "this cycle was aborted and the remaining scalars will follow later.",
            "Synchronization");
    }

    private List<string> SelectReconciliationCandidateTokens(string[] candidates)
    {
        if (candidates.Length == 0)
        {
            return [];
        }

        var count = Math.Min(ReconciliationCandidateBudget, candidates.Length);
        var start = reconciliationCandidateCursor % candidates.Length;
        var selected = new List<string>(count);
        for (var offset = 0; offset < count; offset++)
        {
            selected.Add(candidates[(start + offset) % candidates.Length]);
        }

        reconciliationCandidateCursor = (start + count) % candidates.Length;
        return selected;
    }

    private List<string> SelectReconciliationScalarTokens(
        string[] scalarTokens,
        IReadOnlyList<TokenPath> roots,
        AppConfiguration currentConfiguration)
    {
        var count = Math.Min(ReconciliationScalarBudget, scalarTokens.Length);
        var selected = new List<string>(count);
        var selectedSet = new HashSet<string>(StringComparer.Ordinal);

        // Give active channel/scope pairs a fair rotating health probe. This keeps a
        // large DYN subtree from delaying EQ or GATE for many poll cycles, while half
        // of the fixed budget remains available for the complete scalar round-robin.
        var probes = new List<string>();
        foreach (var root in roots)
        {
            var prefix = root + "/";
            foreach (var scope in currentConfiguration.Scopes)
            {
                var probe = scalarTokens.FirstOrDefault(token =>
                    token.StartsWith(prefix, StringComparison.Ordinal) &&
                    ScopeCatalog.TryMatch(TokenPath.Parse(token), out var actualScope) &&
                    actualScope == scope);
                if (probe is not null)
                {
                    probes.Add(probe);
                }
            }
        }

        if (probes.Count > 0)
        {
            var probeBudget = Math.Min(probes.Count, Math.Max(1, count / 2));
            var probeStart = reconciliationProbeCursor % probes.Count;
            for (var offset = 0; offset < probeBudget; offset++)
            {
                var probe = probes[(probeStart + offset) % probes.Count];
                if (selectedSet.Add(probe))
                {
                    selected.Add(probe);
                }
            }

            reconciliationProbeCursor = (probeStart + probeBudget) % probes.Count;
        }

        var start = reconciliationScalarCursor % scalarTokens.Length;
        var examined = 0;
        while (selected.Count < count && examined < scalarTokens.Length)
        {
            var token = scalarTokens[(start + examined) % scalarTokens.Length];
            if (selectedSet.Add(token))
            {
                selected.Add(token);
            }

            examined++;
        }

        reconciliationScalarCursor = (start + examined) % scalarTokens.Length;
        return selected;
    }

    private TokenPath[] SelectReconciliationRoots(TokenPath[] roots)
    {
        if (roots.Length == 0)
        {
            return [];
        }

        var count = Math.Min(ReconciliationRootBudget, roots.Length);
        var start = Math.Abs(reconciliationRootCursor % roots.Length);
        var selected = new TokenPath[count];
        for (var offset = 0; offset < count; offset++)
        {
            selected[offset] = roots[(start + offset) % roots.Length];
        }

        reconciliationRootCursor = (start + count) % roots.Length;
        return selected;
    }

    private TokenPath[] SelectReconciliationFallbackNodes(TokenPath[] nodes)
    {
        var count = Math.Min(ReconciliationFallbackNodeBudget, nodes.Length);
        var start = Math.Abs(reconciliationFallbackCursor % nodes.Length);
        var selected = new TokenPath[count];
        for (var offset = 0; offset < count; offset++)
        {
            selected[offset] = nodes[(start + offset) % nodes.Length];
        }

        reconciliationFallbackCursor = (start + count) % nodes.Length;
        return selected;
    }

    private static TokenPath[] GetSourceReconciliationRoots(
        AppConfiguration currentConfiguration)
    {
        var roots = new Dictionary<string, TokenPath>(StringComparer.Ordinal);
        foreach (var channel in currentConfiguration.Channels.InputChannels
                     .Select(static mapping => mapping.Source)
                     .Distinct())
        {
            var root = TokenPath.Parse($"/ch/{channel}");
            roots[root.ToString()] = root;
        }

        foreach (var channel in currentConfiguration.Channels.AuxChannels
                     .Select(static mapping => mapping.Source)
                     .Distinct())
        {
            var root = TokenPath.Parse($"/aux/{channel}");
            roots[root.ToString()] = root;
        }

        return roots.Values
            .OrderBy(static root => root.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private void OnFohParameterChanged(object? sender, WingParameter parameter)
    {
        if (sender is IWingSession session && ReferenceEquals(session, fohSession))
        {
            HandleParameter(session, parameter);
        }
    }

    private void OnMonitorParameterChanged(object? sender, WingParameter parameter)
    {
        if (sender is IWingSession session && ReferenceEquals(session, monitorSession))
        {
            HandleParameter(session, parameter);
        }
    }

    private void HandleParameter(IWingSession session, WingParameter parameter)
    {
        if (ReferenceEquals(session, targetSession))
        {
            HandleTargetParameter(parameter);
            return;
        }

        if (stopping ||
            !ReferenceEquals(session, sourceSession) ||
            !IsEnabledMappedToken(parameter.TokenPath, sourceSide: true))
        {
            return;
        }

        EngineWork work;
        lock (eventIntakeSync)
        {
            var transaction = Volatile.Read(ref activeSafetyTransaction);
            if (transaction is not null &&
                string.Equals(
                    GetDynamicGroup(TokenPath.Parse(parameter.TokenPath)),
                    transaction.SourceTransactionGroup,
                    StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref transaction.UnexpectedSourceRevision, 1);
            }

            var sequence = checked(++sourceEventSequence);
            work = new EngineWork(
                parameter,
                Volatile.Read(ref activeEpoch),
                EngineWorkOrigin.SourceEvent);
            if (!acceptEvents)
            {
                deferredSourceEvents[parameter.TokenPath] =
                    new BufferedParameterEvent(parameter, work.Epoch, sequence);
                return;
            }
        }

        Enqueue(work);
    }

    private void HandleTargetParameter(WingParameter parameter)
    {
        targetState[parameter.TokenPath] = parameter.Value;
        _ = StoreTargetEventSafeAsync(parameter);
        var currentConfiguration = configuration;
        if (currentConfiguration is null)
        {
            return;
        }

        if (readbackWaiters.TryGetValue(parameter.TokenPath, out var waiter) &&
            WingValueComparer.AreEquivalent(
                waiter.Expected,
                parameter.Value,
                currentConfiguration.Safety.FloatTolerance))
        {
            waiter.Completion.TrySetResult(parameter);
            echoFingerprints.TryRemove(parameter.TokenPath, out _);
            return;
        }

        if (echoFingerprints.TryGetValue(parameter.TokenPath, out var echo))
        {
            if (echo.Matches(
                    TokenPath.Parse(parameter.TokenPath),
                    parameter.Value,
                    parameter.ObservedAt,
                    currentConfiguration.Safety.FloatTolerance))
            {
                echoFingerprints.TryRemove(parameter.TokenPath, out _);
                return;
            }

            if (parameter.ObservedAt > echo.ExpiresAt)
            {
                echoFingerprints.TryRemove(parameter.TokenPath, out _);
            }
        }

        if (TryConsumeGuardedTransactionIntermediateEvent(
                parameter,
                currentConfiguration.Safety.FloatTolerance))
        {
            return;
        }

        if (stopping ||
            !IsEnabledMappedToken(parameter.TokenPath, sourceSide: false))
        {
            return;
        }

        lock (eventIntakeSync)
        {
            var transaction = Volatile.Read(ref activeSafetyTransaction);
            if (transaction is not null &&
                string.Equals(
                    GetDynamicGroup(TokenPath.Parse(parameter.TokenPath)),
                    transaction.TransactionGroup,
                    StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref transaction.UnexpectedTargetRevision, 1);
            }

            var sequence = checked(++targetEventSequence);
            if (!acceptEvents)
            {
                deferredTargetEvents[parameter.TokenPath] =
                    new BufferedParameterEvent(
                        parameter,
                        Volatile.Read(ref activeEpoch),
                        sequence);
                return;
            }
        }

        QueueTargetDrift(parameter, currentConfiguration);
    }

    private bool TryConsumeGuardedTransactionIntermediateEvent(
        WingParameter parameter,
        float tolerance)
    {
        var context = Volatile.Read(ref activeSafetyTransaction);
        if (context is null ||
            !context.ProcessorMutationMayHaveStarted ||
            Volatile.Read(ref context.IntermediateSuppressionOpen) == 0)
        {
            return false;
        }

        var path = TokenPath.Parse(parameter.TokenPath);
        if (!string.Equals(
                GetDynamicGroup(path),
                context.TransactionGroup,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (context.Guards.TryGetValue(parameter.TokenPath, out var guard))
        {
            if (WingValueComparer.AreEquivalent(guard.Value, parameter.Value, tolerance))
            {
                return true;
            }

            if (Volatile.Read(ref context.RestoreInProgress) != 0 &&
                context.RestorePaths.Contains(parameter.TokenPath) &&
                desiredTargets.TryGetValue(parameter.TokenPath, out var desired) &&
                WingValueComparer.AreEquivalent(desired.Value, parameter.Value, tolerance))
            {
                return true;
            }

            Interlocked.Exchange(ref context.UnexpectedTargetRevision, 1);
            return false;
        }

        if (context.PlannedMutationPaths.Contains(parameter.TokenPath))
        {
            // Model- and delay-mode mutations can reset another planned leaf in
            // their physical group. That leaf is rewritten and scalar-verified
            // before the guard is restored.
            return true;
        }

        if (IsGuardedMutationPath(path))
        {
            Interlocked.Exchange(ref context.UnexpectedTargetRevision, 1);
            return false;
        }

        // Unknown leaves are never treated as harmless model side-effects: if they
        // are not part of this exact planned and verified group, fail closed.
        Interlocked.Exchange(ref context.UnexpectedTargetRevision, 1);
        return false;
    }

    private void QueueTargetDrift(
        WingParameter parameter,
        AppConfiguration currentConfiguration)
    {
        if (liveWritesArmed &&
            currentConfiguration.Safety.DryRun == false &&
            desiredTargets.TryGetValue(parameter.TokenPath, out var desired) &&
            !WingValueComparer.AreEquivalent(
                desired.Value,
                parameter.Value,
                currentConfiguration.Safety.FloatTolerance))
        {
            Record(
                DiagnosticSeverity.Warning,
                "TARGET_DRIFT",
                $"Manual target change on {parameter.TokenPath}; source value is being restored.",
                "Synchronization");
            var synthetic = new WingParameter(
                desired.SourcePath.ToString(),
                desired.Value,
                clock.UtcNow);
            Enqueue(
                new EngineWork(
                    synthetic,
                    Volatile.Read(ref activeEpoch),
                    EngineWorkOrigin.TargetDrift));
        }
    }

    private EventSequences GetEventSequences()
    {
        lock (eventIntakeSync)
        {
            return new EventSequences(
                sourceEventSequence,
                targetEventSequence,
                Interlocked.Read(ref transportGeneration));
        }
    }

    private void ThrowIfEventFenceChanged(EventSequences expected, string message)
    {
        lock (eventIntakeSync)
        {
            var transport = Interlocked.Read(ref transportGeneration);
            if (sourceEventSequence == expected.Source &&
                targetEventSequence == expected.Target &&
                transport == expected.Transport)
            {
                return;
            }

            var transaction = Volatile.Read(ref activeSafetyTransaction);
            var onlyUnrelatedConsoleEventsChanged =
                transaction is not null &&
                transport == expected.Transport &&
                Interlocked.Read(ref transaction.UnexpectedSourceRevision) == 0 &&
                Interlocked.Read(ref transaction.UnexpectedTargetRevision) == 0;
            if (!onlyUnrelatedConsoleEventsChanged)
            {
                throw new ReconciliationInterruptedException(message);
            }
        }
    }

    private void ActivateSafetyTransaction(
        SafetyGuardExecutionContext context,
        EventSequences expected)
    {
        lock (eventIntakeSync)
        {
            if (sourceEventSequence != expected.Source ||
                targetEventSequence != expected.Target ||
                Interlocked.Read(ref transportGeneration) != expected.Transport)
            {
                throw new ReconciliationInterruptedException(
                    "The source or target console changed while preparing the safety guards.");
            }

            Volatile.Write(ref activeSafetyTransaction, context);
        }
    }

    private bool EventFenceIsCurrent(EventSequences expected)
    {
        var current = GetEventSequences();
        return current.Source == expected.Source &&
            current.Target == expected.Target &&
            current.Transport == expected.Transport;
    }

    private bool IsSnapshotCurrent(FreshPlanSnapshot snapshot)
    {
        lock (eventIntakeSync)
        {
            return sourceEventSequence == snapshot.SourceEventSequence &&
                targetEventSequence == snapshot.TargetEventSequence &&
                Interlocked.Read(ref transportGeneration) == snapshot.TransportGeneration;
        }
    }

    private void EnableEventIntake(FreshPlanSnapshot snapshot)
    {
        EngineWork[] sourceWork;
        WingParameter[] targetEvents;
        lock (eventIntakeSync)
        {
            if (stopping || safetyPaused)
            {
                return;
            }

            var epoch = Volatile.Read(ref activeEpoch);
            sourceWork = deferredSourceEvents.Values
                .Where(item =>
                    item.Epoch == epoch &&
                    item.Sequence > snapshot.SourceEventSequence)
                .OrderBy(static item => item.Sequence)
                .Select(static item =>
                    new EngineWork(
                        item.Parameter,
                        item.Epoch,
                        EngineWorkOrigin.SourceEvent))
                .ToArray();
            targetEvents = deferredTargetEvents.Values
                .Where(item =>
                    item.Epoch == epoch &&
                    item.Sequence > snapshot.TargetEventSequence)
                .OrderBy(static item => item.Sequence)
                .Select(static item => item.Parameter)
                .ToArray();
            deferredSourceEvents.Clear();
            deferredTargetEvents.Clear();
            acceptEvents = true;

            // Parameter callbacks use the same gate. Queueing the buffered source
            // work before releasing it preserves the snapshot/event hand-off.
            foreach (var work in sourceWork)
            {
                Enqueue(work);
                if (!acceptEvents)
                {
                    return;
                }
            }
        }

        var currentConfiguration = configuration;
        if (currentConfiguration is null || !acceptEvents)
        {
            return;
        }

        foreach (var parameter in targetEvents)
        {
            QueueTargetDrift(parameter, currentConfiguration);
            if (!acceptEvents)
            {
                return;
            }
        }
    }

    private void SuspendEventIntake()
    {
        lock (eventIntakeSync)
        {
            acceptEvents = false;
        }
    }

    private void OnSessionStateChanged(object? sender, WingSessionStateChange change)
    {
        if (sender is not IWingSession session ||
            (!ReferenceEquals(session, fohSession) &&
             !ReferenceEquals(session, monitorSession)))
        {
            return;
        }

        if (stopping)
        {
            return;
        }

        if (change.State is WingSessionState.Connected or WingSessionState.Connecting)
        {
            if (session.State == change.State)
            {
                lock (transportStateSync)
                {
                    faultedTransportSessions.Remove(session);
                }
            }

            return;
        }

        if (change.State is WingSessionState.Faulted or WingSessionState.Disconnected)
        {
            if (session.State is not (WingSessionState.Faulted or WingSessionState.Disconnected))
            {
                // A stale notification that does not match the session's authoritative
                // state must not invalidate and starve an otherwise healthy reconnect.
                return;
            }

            bool firstFaultInConnectionEpisode;
            lock (transportStateSync)
            {
                firstFaultInConnectionEpisode = faultedTransportSessions.Add(session);
            }

            TriggerReconnect(change.Reason, firstFaultInConnectionEpisode);
        }
    }

    private bool AreTransportSessionsConnected() =>
        fohSession?.State == WingSessionState.Connected &&
        monitorSession?.State == WingSessionState.Connected;

    private void Enqueue(EngineWork work)
    {
        var channel = workChannel;
        Interlocked.Increment(ref queueDepth);
        if (channel is null || !channel.Writer.TryWrite(work))
        {
            Interlocked.Decrement(ref queueDepth);
            PauseForSafety(
                "EVENT_QUEUE_OVERFLOW",
                "The event queue is full; writes are paused and a full resnapshot is required.");
            TriggerReconnect("Event queue overflow requires a fresh snapshot.");
            return;
        }

        RaiseMetrics();
    }

    private async Task StoreTargetEventSafeAsync(WingParameter parameter)
    {
        try
        {
            var targetIsFoh = ReferenceEquals(targetSession, fohSession);
            var identity = targetIsFoh
                ? fohIdentity
                : monitorIdentity;
            if (identity is null)
            {
                return;
            }

            await stateSink.StoreAsync(
                    targetIsFoh ? "FOH" : "Stage",
                    identity,
                    activeEpoch,
                    [parameter],
                    sessionCancellation?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session shutdown.
        }
        catch (Exception exception)
        {
            Record(
                DiagnosticSeverity.Warning,
                "CACHE_TARGET_EVENT_FAILED",
                exception.Message,
                "Cache");
        }
    }

    private void TriggerReconnect(
        string reason,
        bool invalidateActiveReconnect = false)
    {
        if (stopping || safetyPaused || disposed || sessionCancellation is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref reconnectStarted, 1, 0) != 0)
        {
            if (invalidateActiveReconnect)
            {
                Interlocked.Increment(ref transportGeneration);
                SuspendEventIntake();
            }

            return;
        }

        Interlocked.Increment(ref transportGeneration);
        SuspendEventIntake();
        reconnectTask = ReconnectLoopAsync(reason, sessionCancellation.Token);
    }

    private async Task ReconnectLoopAsync(string initialReason, CancellationToken cancellationToken)
    {
        var attempt = 0;
        try
        {
            ChangeStatus(SyncCoordinatorState.Reconnecting, initialReason);
            await MarkCachesStaleAsync(cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested && !stopping)
            {
                attempt++;
                var delay = TimeSpan.FromMilliseconds(
                    Math.Min(
                        MaximumReconnectDelay.TotalMilliseconds,
                        500D * Math.Pow(2D, Math.Min(attempt - 1, 5))));
                await clock.Delay(delay, cancellationToken).ConfigureAwait(false);
                var reconcileEntered = false;
                try
                {
                    await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    reconcileEntered = true;
                    if (safetyPaused || stopping)
                    {
                        return;
                    }

                    await VerifyIdentitiesAsync(cancellationToken).ConfigureAwait(false);
                    await ConnectBothAsync(cancellationToken).ConfigureAwait(false);
                    await VerifyConnectedIdentitiesAsync(cancellationToken).ConfigureAwait(false);
                    await BeginCacheEpochAsync(cancellationToken).ConfigureAwait(false);
                    ChangeStatus(SyncCoordinatorState.Snapshotting, "After reconnect, reread both consoles.");
                    var freshSnapshot = await BuildFreshPlanAsync(cancellationToken).ConfigureAwait(false);
                    while (!IsSnapshotCurrent(freshSnapshot))
                    {
                        freshSnapshot = await BuildFreshPlanAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var plan = freshSnapshot.Plan;
                    RecordPlanningFindings(plan);
                    ThrowIfStopRequested(
                        "A stop request interrupted the reconnect snapshot.");
                    if (configuration?.Safety.DryRun == true)
                    {
                        await PreviewPlanAsync(plan).ConfigureAwait(false);
                    }
                    else if (liveWritesArmed)
                    {
                        if (plan.ExecutableWrites.Count > 0)
                        {
                            ChangeStatus(
                                SyncCoordinatorState.ApplyingLive,
                                "Reconnect differences are being written live and read back.");
                        }

                        await ExecutePlanAsync(
                                plan,
                                cancellationToken,
                                executionSnapshot: freshSnapshot)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        pendingInitialPlan = plan;
                        pendingInitialPreview = CreatePreviewSummary(plan);
                        await PreviewPlanAsync(plan).ConfigureAwait(false);
                        ChangeStatus(
                            SyncCoordinatorState.AwaitingConfirmation,
                            "New snapshot is waiting for live confirmation.");
                        return;
                    }

                    ThrowIfStopRequested(
                        "A stop request blocked resuming the event stream.");
                    Interlocked.Increment(ref reconnectCount);
                    EnableEventIntake(freshSnapshot);
                    ChangeStatus(
                        configuration?.Safety.DryRun == true
                            ? SyncCoordinatorState.RunningDryRun
                            : SyncCoordinatorState.RunningLive,
                        "Both consoles were revalidated and reread.");
                    RaiseMetrics();
                    return;
                }
                catch (ReconciliationInterruptedException) when (stopping)
                {
                    return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (exception is WriteVerificationException
                        or ConnectedIdentityMismatchException
                        or SafetyGuardRecoveryException)
                    {
                        // The failing operation already raised a critical diagnostic and
                        // paused the coordinator. Never retry an unverified write or an
                        // identity mismatch automatically.
                        return;
                    }

                    Record(
                        DiagnosticSeverity.Error,
                        "RECONNECT_ATTEMPT_FAILED",
                        $"Reconnect attempt {attempt} failed: {exception.Message}",
                        "Netwerk");
                }
                finally
                {
                    if (reconcileEntered)
                    {
                        reconcileGate.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop.
        }
        finally
        {
            Interlocked.Exchange(ref reconnectStarted, 0);
        }
    }

    private async Task MarkCachesStaleAsync(CancellationToken cancellationToken)
    {
        if (fohIdentity is null || monitorIdentity is null)
        {
            return;
        }

        await Task.WhenAll(
                stateSink.MarkStaleAsync("FOH", fohIdentity, cancellationToken),
                stateSink.MarkStaleAsync("Stage", monitorIdentity, cancellationToken))
            .ConfigureAwait(false);
    }

    private void PauseForSafety(string code, string message)
    {
        SuspendEventIntake();
        liveWritesArmed = false;
        safetyPaused = true;
        ChangeStatus(SyncCoordinatorState.Paused, message);
        Record(DiagnosticSeverity.Critical, code, message, "Safety");
    }

    private void RecordPlanningFindings(WritePlan plan)
    {
        foreach (var issue in plan.Issues)
        {
            Record(
                DiagnosticSeverity.Warning,
                $"PLAN_{issue.Code.ToString().ToUpperInvariant()}",
                issue.Message,
                issue.SourcePath.ToString());
        }
    }

    private bool IsEnabledScopedToken(string token)
    {
        var currentConfiguration = configuration;
        return currentConfiguration is not null &&
            TokenPath.TryParse(token, out var path) &&
            ScopeCatalog.TryMatch(path, out var scope) &&
            currentConfiguration.Scopes.Contains(scope);
    }

    private bool IsEnabledMappedToken(string token, bool sourceSide)
    {
        var currentConfiguration = configuration;
        if (currentConfiguration is null ||
            !TokenPath.TryParse(token, out var path) ||
            !ScopeCatalog.TryMatch(path, out var scope) ||
            !currentConfiguration.Scopes.Contains(scope) ||
            !path.TryGetChannel(out var kind, out var channel))
        {
            return false;
        }

        return (kind, sourceSide) switch
        {
            (WingChannelKind.Input, true) =>
                currentConfiguration.Channels.InputChannels.Any(item => item.Source == channel),
            (WingChannelKind.Input, false) =>
                currentConfiguration.Channels.InputChannels.Any(item => item.Target == channel),
            (WingChannelKind.Aux, true) =>
                currentConfiguration.Channels.AuxChannels.Any(item => item.Source == channel),
            (WingChannelKind.Aux, false) =>
                currentConfiguration.Channels.AuxChannels.Any(item => item.Target == channel),
            _ => false,
        };
    }

    private async Task StopInternalAsync(
        CancellationToken cancellationToken,
        bool preserveFaultedStatus = false)
    {
        stopping = true;
        workChannel?.Writer.TryComplete();

        // Do not cancel the session token while a write transaction may already be
        // beyond helper-stdin dispatch. New source/drift work is blocked by `stopping`;
        // the reconcile gate then proves that any active SetMany and its explicit
        // scalar readback reached a terminal result before shutdown continues.
        var activeTransactionDrained = await WaitForActiveTransactionAsync(
                TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        if (!activeTransactionDrained)
        {
            FailIncompleteStop(
                "An active WAPI write/readback transaction could not be safely " +
                "completed; helpers remain blocked and 'Stopped' is not reported.");
        }

        SuspendEventIntake();
        sessionCancellation?.Cancel();

        var tasks = new[] { workerTask, healthTask, reconnectTask }
            .Where(static task => task is not null)
            .Cast<Task>()
            .ToArray();
        _ = await WaitForBackgroundTasksAsync(
                tasks,
                TimeSpan.FromSeconds(4),
                "STOP_TASK_WAIT_FAILED")
            .ConfigureAwait(false);

        UnsubscribeSessions();
        var sessions = new List<IWingSession>(2);
        if (fohSession is not null)
        {
            sessions.Add(fohSession);
        }

        if (monitorSession is not null &&
            !ReferenceEquals(monitorSession, fohSession))
        {
            sessions.Add(monitorSession);
        }
        foreach (var session in sessions)
        {
            if (!pendingDisconnectTasks.ContainsKey(session))
            {
                pendingDisconnectTasks[session] = StartDisconnectTask(session);
            }
        }

        var disconnectsFinished = await WaitForCleanupTasksAsync(
                sessions.Select(session => pendingDisconnectTasks[session]).ToArray(),
                TimeSpan.FromSeconds(20))
            .ConfigureAwait(false);
        if (!disconnectsFinished)
        {
            FailIncompleteStop(
                "A WAPI disconnect remained active; the old helpers remain blocked for restart.");
        }

        foreach (var session in sessions)
        {
            var disconnect = pendingDisconnectTasks[session];
            if (disconnect.IsFaulted || disconnect.IsCanceled)
            {
                Record(
                    DiagnosticSeverity.Warning,
                    "DISCONNECT_FAILED",
                    CleanupFailureMessage(disconnect),
                    "Lifecycle");
            }

            if (!pendingDisposeTasks.ContainsKey(session))
            {
                pendingDisposeTasks[session] = StartDisposeTask(session);
            }
        }

        var disposalsFinished = await WaitForCleanupTasksAsync(
                sessions.Select(session => pendingDisposeTasks[session]).ToArray(),
                TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        if (!disposalsFinished)
        {
            FailIncompleteStop(
                "A WAPI helper remained active during dispose; restart is blocked.");
        }

        var failedDisposals = sessions
            .Select(session => pendingDisposeTasks[session])
            .Where(static task => task.IsFaulted || task.IsCanceled)
            .ToArray();
        if (failedDisposals.Length > 0)
        {
            foreach (var failure in failedDisposals)
            {
                Record(
                    DiagnosticSeverity.Critical,
                    "SESSION_DISPOSE_FAILED",
                    CleanupFailureMessage(failure),
                    "Lifecycle");
            }

            FailIncompleteStop(
                "Not all WAPI helpers could be demonstrably closed; restart is blocked.");
        }

        var backgroundStopped = await WaitForBackgroundTasksAsync(
                tasks,
                TimeSpan.FromSeconds(10),
                "STOP_TASK_FINAL_WAIT_FAILED")
            .ConfigureAwait(false);
        if (!backgroundStopped)
        {
            safetyPaused = true;
            ChangeStatus(
                SyncCoordinatorState.Faulted,
                "Stopping could not be completed safely; restart WingSync before synchronizing again.");
            throw new TimeoutException(
                "A synchronization task remained active after both WAPI sessions were closed.");
        }

        sessionCancellation?.Dispose();
        sessionCancellation = null;
        workChannel = null;
        workerTask = null;
        healthTask = null;
        reconnectTask = null;
        pendingDisconnectTasks.Clear();
        pendingDisposeTasks.Clear();
        lock (transportStateSync)
        {
            faultedTransportSessions.Clear();
        }
        fohSession = null;
        monitorSession = null;
        sourceSession = null;
        targetSession = null;
        pendingInitialPlan = null;
        pendingInitialPreview = null;
        sourceState.Clear();
        targetState.Clear();
        desiredTargets.Clear();
        readbackWaiters.Clear();
        echoFingerprints.Clear();
        lock (eventIntakeSync)
        {
            deferredSourceEvents.Clear();
            deferredTargetEvents.Clear();
        }
        Interlocked.Exchange(ref queueDepth, 0);
        Interlocked.Exchange(ref reconnectStarted, 0);
        reconciliationRootCursor = 0;
        reconciliationFallbackCursor = 0;
        reconciliationCandidateCursor = 0;
        reconciliationProbeCursor = 0;
        reconciliationScalarCursor = 0;
        reconciliationPollReported = 0;
        reconciliationBudgetWarningRaised = 0;
        targetReconciliationCriticalCursor = 0;
        targetReconciliationGeneralCursor = 0;
        targetReconciliationBudgetWarningRaised = 0;
        liveWritesArmed = false;
        safetyPaused = false;
        RefreshStoppingFlag();

        if (!preserveFaultedStatus)
        {
            ChangeStatus(SyncCoordinatorState.Stopped, "Synchronization stopped.");
        }
    }

    private async Task<bool> WaitForActiveTransactionAsync(TimeSpan timeout)
    {
        try
        {
            var entered = await reconcileGate.WaitAsync(timeout, CancellationToken.None)
                .ConfigureAwait(false);
            if (!entered)
            {
                return false;
            }

            reconcileGate.Release();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static Task StartDisconnectTask(IWingSession session)
    {
        try
        {
            return session.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    private static Task StartDisposeTask(IWingSession session)
    {
        try
        {
            return session.DisposeAsync().AsTask();
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    private static async Task<bool> WaitForCleanupTasksAsync(
        Task[] tasks,
        TimeSpan timeout)
    {
        if (tasks.Length == 0)
        {
            return true;
        }

        try
        {
            await Task.WhenAll(tasks)
                .WaitAsync(timeout, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception)
        {
            // Faulted and cancelled cleanup tasks are terminal, not abandoned. The
            // caller inspects them and refuses restart when dispose did not succeed.
            return tasks.All(static task => task.IsCompleted);
        }
    }

    private static string CleanupFailureMessage(Task task)
    {
        if (task.IsCanceled)
        {
            return "De cleanup-taak werd geannuleerd.";
        }

        return task.Exception?.GetBaseException().Message ??
            "The cleanup task ended without successful confirmation.";
    }

    private void FailIncompleteStop(string message)
    {
        liveWritesArmed = false;
        safetyPaused = true;
        ChangeStatus(
            SyncCoordinatorState.Faulted,
            "Stopping is not demonstrably complete yet; restart remains blocked.");
        Record(DiagnosticSeverity.Critical, "STOP_INCOMPLETE", message, "Lifecycle");
        throw new TimeoutException(message);
    }

    private void AdvanceCompletedStopSequence(long request)
    {
        while (true)
        {
            var completed = Interlocked.Read(ref completedStopRequestSequence);
            if (completed >= request)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref completedStopRequestSequence,
                    request,
                    completed) == completed)
            {
                return;
            }
        }
    }

    private void RefreshStoppingFlag() =>
        stopping =
            Interlocked.Read(ref stopRequestSequence) >
            Interlocked.Read(ref completedStopRequestSequence);

    private bool StopWasRequested(long expectedStopSequence) =>
        stopping ||
        Interlocked.Read(ref stopRequestSequence) != expectedStopSequence ||
        Interlocked.Read(ref completedStopRequestSequence) < expectedStopSequence;

    private void ThrowIfStopRequested(string message)
    {
        if (stopping)
        {
            throw new ReconciliationInterruptedException(message);
        }
    }

    private void ThrowIfStopRequested(long expectedStopSequence, string message)
    {
        if (StopWasRequested(expectedStopSequence))
        {
            throw new ReconciliationInterruptedException(message);
        }
    }

    private async Task<bool> WaitForBackgroundTasksAsync(
        Task[] tasks,
        TimeSpan timeout,
        string diagnosticCode)
    {
        if (tasks.Length == 0)
        {
            return true;
        }

        try
        {
            await Task.WhenAll(tasks)
                .WaitAsync(timeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Record(
                tasks.All(static task => task.IsCompleted)
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Error,
                diagnosticCode,
                exception.Message,
                "Lifecycle");
        }

        return tasks.All(static task => task.IsCompleted);
    }

    private void ChangeStatus(SyncCoordinatorState newState, string detail)
    {
        status = new SyncCoordinatorStatus(newState, detail, clock.UtcNow);
        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<SyncCoordinatorStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, status);
            }
#pragma warning disable CA1031 // Presentation observers must not alter engine safety state.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                Record(
                    DiagnosticSeverity.Error,
                    "STATUS_OBSERVER_FAILED",
                    exception.Message,
                    "Observer");
            }
        }
    }

    private void RaiseMetrics()
    {
        var handlers = MetricsChanged;
        if (handlers is null)
        {
            return;
        }

        var currentMetrics = Metrics;
        foreach (EventHandler<SyncMetrics> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, currentMetrics);
            }
#pragma warning disable CA1031 // Presentation observers must not alter engine safety state.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                Record(
                    DiagnosticSeverity.Error,
                    "METRICS_OBSERVER_FAILED",
                    exception.Message,
                    "Observer");
            }
        }
    }

    private void Record(
        DiagnosticSeverity severity,
        string code,
        string message,
        string source) =>
        observer.Record(new DiagnosticEvent(severity, code, message, source, clock.UtcNow));

    private void AddLatency(double milliseconds)
    {
        lock (latencySync)
        {
            recentLatencies.Enqueue(milliseconds);
            while (recentLatencies.Count > 512)
            {
                recentLatencies.Dequeue();
            }
        }
    }

    private double CalculateP95Latency()
    {
        lock (latencySync)
        {
            if (recentLatencies.Count == 0)
            {
                return 0D;
            }

            var sorted = recentLatencies.Order().ToArray();
            var index = Math.Clamp(
                (int)Math.Ceiling(sorted.Length * 0.95D) - 1,
                0,
                sorted.Length - 1);
            return sorted[index];
        }
    }

    private static bool IsDelayPath(TokenPath path) =>
        path.Segments.Count >= 5 &&
        path.Segments[2].Equals("in", StringComparison.OrdinalIgnoreCase) &&
        path.Segments[3].Equals("set", StringComparison.OrdinalIgnoreCase) &&
        (path.Leaf.Equals("dlymode", StringComparison.OrdinalIgnoreCase) ||
         path.Leaf.Equals("dly", StringComparison.OrdinalIgnoreCase) ||
         path.Leaf.Equals("dlyon", StringComparison.OrdinalIgnoreCase));

    private static bool IsGuardedMutationPath(TokenPath path) =>
        path.Leaf.Equals("mdl", StringComparison.OrdinalIgnoreCase) ||
        (IsDelayPath(path) &&
         !path.Leaf.Equals("dlyon", StringComparison.OrdinalIgnoreCase));

    private static string? GetDynamicGroup(TokenPath path)
    {
        if (IsDelayPath(path))
        {
            return $"/{path.Segments[0]}/{path.Segments[1]}/delay";
        }

        if (path.Segments.Count < 4)
        {
            return null;
        }

        var section = path.Segments[2].ToLowerInvariant() switch
        {
            "flt" => "flt",
            "gate" or "gatesc" => "gate",
            "dyn" or "dynxo" or "dynsc" => "dyn",
            "eq" => "eq",
            "peq" => "peq",
            _ => null,
        };
        return section is null
            ? null
            : $"/{path.Segments[0]}/{path.Segments[1]}/{section}";
    }

    private static bool IsEnablePath(TokenPath path)
    {
        if (path.Leaf.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            path.Leaf.Equals("byp", StringComparison.OrdinalIgnoreCase) ||
            path.Leaf.Equals("bypass", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsDelayPath(path) &&
            path.Leaf.Equals("dlyon", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Segments.Count >= 4 &&
            path.Segments[2].Equals("flt", StringComparison.OrdinalIgnoreCase) &&
            (path.Leaf.Equals("lc", StringComparison.OrdinalIgnoreCase) ||
             path.Leaf.Equals("hc", StringComparison.OrdinalIgnoreCase) ||
             path.Leaf.Equals("tf", StringComparison.OrdinalIgnoreCase) ||
             path.Leaf.Equals("hpfon", StringComparison.OrdinalIgnoreCase) ||
             path.Leaf.Equals("lpfon", StringComparison.OrdinalIgnoreCase));
    }

    private InitialSyncPreviewSummary CreatePreviewSummary(WritePlan plan)
    {
        var byScope = new ReadOnlyDictionary<SyncScope, int>(
            plan.Writes
                .GroupBy(static write => write.Scope)
                .OrderBy(static group => group.Key)
                .ToDictionary(static group => group.Key, static group => group.Count()));
        return new InitialSyncPreviewSummary(
            plan.Writes.Count,
            plan.ExecutableWrites.Count,
            plan.Writes.Count(static write =>
                write.Disposition == WriteDisposition.BlockedBySafety),
            byScope,
            clock.UtcNow);
    }

    private bool PlansAreEquivalent(WritePlan previous, WritePlan current)
    {
        if (previous.Writes.Count != current.Writes.Count)
        {
            return false;
        }

        var tolerance = configuration?.Safety.FloatTolerance ?? 0.0001F;
        for (var index = 0; index < previous.Writes.Count; index++)
        {
            var left = previous.Writes[index];
            var right = current.Writes[index];
            if (!left.SourcePath.Equals(right.SourcePath) ||
                !left.TargetPath.Equals(right.TargetPath) ||
                left.Scope != right.Scope ||
                left.Disposition != right.Disposition ||
                left.IsSafetyGuard != right.IsSafetyGuard ||
                !WingValueComparer.AreEquivalent(left.Value, right.Value, tolerance))
            {
                return false;
            }
        }

        return true;
    }

    private static int GetExecutionPhase(PlannedWrite write)
    {
        if (write.IsSafetyGuard)
        {
            return 0;
        }

        if (write.TargetPath.Leaf.Equals("mdl", StringComparison.OrdinalIgnoreCase) ||
            (IsDelayPath(write.TargetPath) &&
             write.TargetPath.Leaf.Equals("dlymode", StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        if (!IsEnablePath(write.TargetPath))
        {
            return 2;
        }

        return IsEnableActive(write.TargetPath, write.Value) ? 3 : 0;
    }

    private static bool IsEnableActive(TokenPath path, WingValue value)
    {
        var enabled = value.Type switch
        {
            WingValueType.I => value.AsInt32() != 0,
            WingValueType.F => value.AsFloat() != 0F,
            WingValueType.S => IsTrue(value.AsString()),
            _ => false,
        };
        if (path.Leaf.Equals("byp", StringComparison.OrdinalIgnoreCase) ||
            path.Leaf.Equals("bypass", StringComparison.OrdinalIgnoreCase))
        {
            enabled = !enabled;
        }

        return enabled;
    }

    private static bool IsTrue(string value) =>
        value.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("1", StringComparison.Ordinal);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);

    private sealed record FreshPlanSnapshot(
        WritePlan Plan,
        long SourceEventSequence,
        long TargetEventSequence,
        long TransportGeneration,
        bool WasInvalidated);

    private sealed record EventSequences(long Source, long Target, long Transport);

    private sealed record BufferedParameterEvent(
        WingParameter Parameter,
        long Epoch,
        long Sequence);

    private enum EngineWorkOrigin
    {
        SourceEvent,
        TargetDrift,
    }

    private sealed record EngineWork(
        WingParameter Parameter,
        long Epoch,
        EngineWorkOrigin Origin);

    private sealed record ExecutionUnit(
        string? TransactionGroup,
        PlannedWrite[] Writes);

    private sealed record ExecutionBatch(
        int Phase,
        PlannedWrite[] Writes);

    private sealed record DesiredTarget(
        TokenPath SourcePath,
        TokenPath TargetPath,
        WingValue Value,
        SyncScope Scope);

    private sealed record RuntimeProcessorRefresh(
        string Key,
        IReadOnlyList<string> Nodes,
        string RequiredSidechainToken);

    private sealed record StableGroupPass(
        IReadOnlyDictionary<string, WingParameter> Values,
        bool ModelMirrorMismatch);

    private sealed record StableNodePass(
        IReadOnlyDictionary<string, WingParameter> Values,
        bool ModelMirrorMismatch);

    private sealed class SafetyGuardExecutionContext
    {
        public SafetyGuardExecutionContext(
            string transactionGroup,
            string sourceTransactionGroup,
            IReadOnlyDictionary<string, WingValue> originals,
            IReadOnlyDictionary<string, PlannedWrite> guards,
            IReadOnlySet<string> plannedMutationPaths,
            IReadOnlySet<string> restorePaths,
            long transportGeneration)
        {
            TransactionGroup = transactionGroup;
            SourceTransactionGroup = sourceTransactionGroup;
            Originals = originals;
            Guards = guards;
            PlannedMutationPaths = plannedMutationPaths;
            RestorePaths = restorePaths;
            TransportGeneration = transportGeneration;
        }

        public string TransactionGroup { get; }

        public string SourceTransactionGroup { get; }

        public IReadOnlyDictionary<string, WingValue> Originals { get; }

        public IReadOnlyDictionary<string, PlannedWrite> Guards { get; }

        public IReadOnlySet<string> PlannedMutationPaths { get; }

        public IReadOnlySet<string> RestorePaths { get; }

        public long TransportGeneration { get; }

        public HashSet<string> ActivePaths { get; } = new(StringComparer.Ordinal);

        public bool ProcessorMutationMayHaveStarted { get; set; }

        public long UnexpectedSourceRevision;

        public long UnexpectedTargetRevision;

        public int IntermediateSuppressionOpen;

        public int RestoreInProgress;
    }

    private sealed class ReadbackWaiter
    {
        public ReadbackWaiter(WingValue expected)
        {
            Expected = expected;
        }

        public WingValue Expected { get; }

        public TaskCompletionSource<WingParameter> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SnapshotUnstableException : IOException
    {
        public SnapshotUnstableException(string message)
            : base(message)
        {
        }
    }

    private sealed class ReconciliationInterruptedException : IOException
    {
        public ReconciliationInterruptedException(string message)
            : base(message)
        {
        }
    }

    private sealed class SnapshotChangedBeforeWriteException : IOException
    {
        public SnapshotChangedBeforeWriteException(string message)
            : base(message)
        {
        }
    }

    private sealed class ConnectedIdentityMismatchException : IOException
    {
        public ConnectedIdentityMismatchException(string message)
            : base(message)
        {
        }
    }

    private sealed class SafetyGuardRecoveryException : IOException
    {
        public SafetyGuardRecoveryException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}

/// <summary>Thrown when a configured synchronization session is not safe to start.</summary>
public sealed class ConfigurationException : InvalidOperationException
{
    /// <summary>Initializes the exception with all deterministic validation findings.</summary>
    public ConfigurationException(ConfigurationValidationResult validation)
        : base(string.Join(" ", validation.Issues.Select(static issue => issue.Message)))
    {
        Validation = validation ?? throw new ArgumentNullException(nameof(validation));
    }

    /// <summary>Gets every validation warning and error.</summary>
    public ConfigurationValidationResult Validation { get; }
}

/// <summary>
/// Thrown without executing writes when the fresh console diff changed after the operator
/// reviewed the previous preview.
/// </summary>
public sealed class InitialSyncPreviewChangedException : InvalidOperationException
{
    /// <summary>Initializes the exception with the replacement preview.</summary>
    public InitialSyncPreviewChangedException(InitialSyncPreviewSummary preview)
        : base("The initial console differences changed; confirm the new preview.")
    {
        Preview = preview ?? throw new ArgumentNullException(nameof(preview));
    }

    /// <summary>Gets the fresh replacement diff that still requires confirmation.</summary>
    public InitialSyncPreviewSummary Preview { get; }
}

/// <summary>Thrown when a console did not confirm an accepted WAPI write.</summary>
public sealed class WriteVerificationException : IOException
{
    /// <summary>Initializes a readback failure.</summary>
    public WriteVerificationException(
        string tokenPath,
        WingValue expected,
        WingValue? actual,
        string message)
        : base(message)
    {
        TokenPath = tokenPath;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>Gets the affected canonical token.</summary>
    public string TokenPath { get; }

    /// <summary>Gets the value sent to the target.</summary>
    public WingValue Expected { get; }

    /// <summary>Gets the observed target value, or null when no readback arrived.</summary>
    public WingValue? Actual { get; }
}
