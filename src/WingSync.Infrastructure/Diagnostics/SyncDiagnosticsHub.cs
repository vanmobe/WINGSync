using System.Threading.Channels;
using WingSync.Core.Abstractions;
using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Diagnostics;

/// <summary>
/// Bridges synchronous engine diagnostics to bounded asynchronous JSONL persistence and
/// an in-memory UI event stream.
/// </summary>
public sealed class SyncDiagnosticsHub : ISyncObserver, IAsyncDisposable
{
    private const int QueueCapacity = 8_192;
    private readonly JsonLineLogger logger;
    private readonly Channel<LogRequest> queue;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task writerTask;
    private long droppedEntries;
    private bool disposed;

    /// <summary>Initializes a diagnostics hub around the rotating logger.</summary>
    public SyncDiagnosticsHub(JsonLineLogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        queue = Channel.CreateBounded<LogRequest>(
            new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
        writerTask = WriteLoopAsync(cancellation.Token);
    }

    /// <summary>Raised immediately with safe structured data for the activity UI.</summary>
    public event EventHandler<DiagnosticEvent>? DiagnosticRecorded;

    /// <summary>Gets how many log requests could not enter the bounded persistence queue.</summary>
    public long DroppedEntries => Interlocked.Read(ref droppedEntries);

    /// <summary>Waits until every diagnostic accepted before this call is persisted.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(
                new LogRequest(
                    WingLogLevel.Trace,
                    string.Empty,
                    string.Empty,
                    null,
                    completion),
                cancellationToken)
            .ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Record(DiagnosticEvent diagnosticEvent)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        var handlers = DiagnosticRecorded;
        if (handlers is not null)
        {
            foreach (EventHandler<DiagnosticEvent> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, diagnosticEvent);
                }
#pragma warning disable CA1031 // UI observers must not interrupt the safety engine or persistence.
                catch (Exception)
#pragma warning restore CA1031
                {
                    // Continue with independent observers and durable logging.
                }
            }
        }

        var properties = diagnosticEvent.Properties?.ToDictionary(
            static pair => pair.Key,
            static pair => (object?)pair.Value,
            StringComparer.Ordinal);
        Enqueue(new LogRequest(
            MapLevel(diagnosticEvent.Severity),
            diagnosticEvent.Code,
            diagnosticEvent.Message,
            properties));
    }

    /// <inheritdoc />
    public void RecordParameterAction(
        string action,
        string sourceToken,
        string targetToken,
        SyncScope scope,
        WingValue value,
        bool dryRun)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var severity = action.Equals("blocked", StringComparison.OrdinalIgnoreCase)
            ? DiagnosticSeverity.Warning
            : DiagnosticSeverity.Information;
        var diagnostic = new DiagnosticEvent(
            severity,
            $"PARAMETER_{action.ToUpperInvariant()}",
            $"{scope}: {sourceToken} → {targetToken}",
            "SyncEngine",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["action"] = action,
                ["sourceToken"] = sourceToken,
                ["targetToken"] = targetToken,
                ["scope"] = scope.ToString(),
                ["type"] = value.Type.ToString(),
                ["value"] = value.ToString(),
                ["dryRun"] = dryRun.ToString(),
            });
        Record(diagnostic);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        queue.Writer.TryComplete();
        try
        {
            await writerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            cancellation.Cancel();
        }

        cancellation.Dispose();
        await logger.DisposeAsync().ConfigureAwait(false);
    }

    private void Enqueue(LogRequest request)
    {
        if (!queue.Writer.TryWrite(request))
        {
            Interlocked.Increment(ref droppedEntries);
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (request.FlushCompletion is not null)
                {
                    request.FlushCompletion.TrySetResult(true);
                    continue;
                }

                try
                {
                    await logger.LogAsync(
                            request.Level,
                            request.EventName,
                            request.Message,
                            request.Properties,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref droppedEntries);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Forced shutdown after the drain deadline.
        }
    }

    private static WingLogLevel MapLevel(DiagnosticSeverity severity) =>
        severity switch
        {
            DiagnosticSeverity.Trace => WingLogLevel.Trace,
            DiagnosticSeverity.Information => WingLogLevel.Information,
            DiagnosticSeverity.Warning => WingLogLevel.Warning,
            DiagnosticSeverity.Error => WingLogLevel.Error,
            DiagnosticSeverity.Critical => WingLogLevel.Critical,
            _ => WingLogLevel.Information,
        };

    private sealed record LogRequest(
        WingLogLevel Level,
        string EventName,
        string Message,
        IReadOnlyDictionary<string, object?>? Properties,
        TaskCompletionSource<bool>? FlushCompletion = null);
}
