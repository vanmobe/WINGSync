using System.Text.Json;
using System.Text.Json.Serialization;
using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Persistence;

/// <summary>
/// Loads and atomically saves a schema-versioned <see cref="AppConfiguration"/>.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    /// <summary>
    /// The configuration schema written by this build.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly TimeProvider _timeProvider;
    private readonly string _filePath;
    private readonly string _backupPath;
    private readonly long _maximumFileSizeBytes;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigStore"/> class.
    /// </summary>
    /// <param name="options">Store path and size limits.</param>
    /// <param name="timeProvider">
    /// The clock used for saved and quarantine timestamps, or the system clock when omitted.
    /// </param>
    public ConfigStore(
        ConfigStoreOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _filePath = Path.GetFullPath(options.FilePath);
        _backupPath = string.Concat(_filePath, ".bak");
        _maximumFileSizeBytes = options.MaximumFileSizeBytes;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _serializerOptions = CreateSerializerOptions();
    }

    /// <summary>
    /// Gets the primary configuration path.
    /// </summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Gets the backup path populated when an existing primary is replaced.
    /// </summary>
    public string BackupPath => _backupPath;

    /// <summary>
    /// Loads the primary configuration, quarantines corrupt JSON, and attempts backup recovery.
    /// </summary>
    /// <param name="cancellationToken">Cancels file I/O.</param>
    /// <returns>A result that retains recovery and failure details.</returns>
    public async Task<ConfigLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var primary = await TryLoadFileAsync(_filePath, cancellationToken)
                .ConfigureAwait(false);
            if (primary.Status == FileLoadStatus.Loaded)
            {
                return new ConfigLoadResult(
                    ConfigLoadStatus.Loaded,
                    primary.Configuration,
                    primary.Envelope!.SchemaVersion);
            }

            if (primary.Status == FileLoadStatus.UnsupportedSchema)
            {
                return new ConfigLoadResult(
                    ConfigLoadStatus.UnsupportedSchema,
                    null,
                    primary.SchemaVersion,
                    Message: primary.Message);
            }

            if (primary.Status == FileLoadStatus.Failed)
            {
                return new ConfigLoadResult(
                    ConfigLoadStatus.Failed,
                    null,
                    Message: primary.Message);
            }

            string? quarantinedPath = null;
            if (primary.Status == FileLoadStatus.Corrupt)
            {
                // Preserve malformed content for support inspection, but move it
                // out of the active path before attempting backup recovery.
                if (!TryQuarantine(
                        _filePath,
                        "corrupt",
                        out quarantinedPath,
                        out var primaryQuarantineError))
                {
                    return new ConfigLoadResult(
                        ConfigLoadStatus.Failed,
                        null,
                        Message: primaryQuarantineError);
                }
            }

            var backup = await TryLoadFileAsync(_backupPath, cancellationToken)
                .ConfigureAwait(false);
            if (backup.Status == FileLoadStatus.Loaded)
            {
                // Recovery is reported to the caller rather than silently
                // rewriting the primary; the next explicit save performs rotation.
                return new ConfigLoadResult(
                    ConfigLoadStatus.RecoveredFromBackup,
                    backup.Configuration,
                    backup.Envelope!.SchemaVersion,
                    quarantinedPath,
                    primary.Message);
            }

            if (backup.Status == FileLoadStatus.Corrupt)
            {
                if (!TryQuarantine(
                        _backupPath,
                        "corrupt",
                        out var backupQuarantine,
                        out var backupQuarantineError))
                {
                    return new ConfigLoadResult(
                        ConfigLoadStatus.Failed,
                        null,
                        QuarantinedPath: quarantinedPath,
                        Message: backupQuarantineError);
                }

                quarantinedPath ??= backupQuarantine;
            }

            if (backup.Status == FileLoadStatus.UnsupportedSchema)
            {
                return new ConfigLoadResult(
                    ConfigLoadStatus.UnsupportedSchema,
                    null,
                    backup.SchemaVersion,
                    quarantinedPath,
                    backup.Message);
            }

            if (backup.Status == FileLoadStatus.Failed)
            {
                return new ConfigLoadResult(
                    ConfigLoadStatus.Failed,
                    null,
                    QuarantinedPath: quarantinedPath,
                    Message: backup.Message);
            }

            if (quarantinedPath is not null)
            {
                return new ConfigLoadResult(
                    ConfigLoadStatus.CorruptQuarantined,
                    null,
                    QuarantinedPath: quarantinedPath,
                    Message: primary.Message ?? backup.Message);
            }

            return new ConfigLoadResult(ConfigLoadStatus.Missing, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Serializes and atomically replaces the configuration while retaining the previous primary as backup.
    /// </summary>
    /// <param name="configuration">The immutable configuration to save.</param>
    /// <param name="cancellationToken">Cancels serialization and file I/O.</param>
    /// <returns>A task that completes after data has been flushed to disk.</returns>
    public async Task SaveAsync(
        AppConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);
        var envelope = new ConfigEnvelope(
            CurrentSchemaVersion,
            _timeProvider.GetUtcNow(),
            AppConfigurationDocument.FromDomain(configuration));
        var content = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            _serializerOptions);

        if (content.LongLength > _maximumFileSizeBytes)
        {
            throw new InvalidDataException(
                $"The configuration is {content.LongLength} bytes; the limit is {_maximumFileSizeBytes} bytes.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.WriteAsync(
                    _filePath,
                    _backupPath,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases synchronization resources owned by the store.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<FileLoadAttempt> TryLoadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new FileLoadAttempt(FileLoadStatus.Missing);
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
            return new FileLoadAttempt(FileLoadStatus.Corrupt, Message: exception.Message);
        }
        catch (IOException exception)
        {
            return new FileLoadAttempt(FileLoadStatus.Failed, Message: exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new FileLoadAttempt(FileLoadStatus.Failed, Message: exception.Message);
        }

        ConfigEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ConfigEnvelope>(
                content,
                _serializerOptions);
        }
        catch (JsonException exception)
        {
            return new FileLoadAttempt(FileLoadStatus.Corrupt, Message: exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return new FileLoadAttempt(FileLoadStatus.Corrupt, Message: exception.Message);
        }

        if (envelope?.Configuration is null || envelope.SchemaVersion <= 0)
        {
            return new FileLoadAttempt(
                FileLoadStatus.Corrupt,
                Message: "The configuration envelope is incomplete.");
        }

        if (envelope.SchemaVersion != CurrentSchemaVersion)
        {
            return new FileLoadAttempt(
                FileLoadStatus.UnsupportedSchema,
                SchemaVersion: envelope.SchemaVersion,
                Message:
                $"Configuration schema {envelope.SchemaVersion} is not supported by schema {CurrentSchemaVersion}.");
        }

        if (!envelope.Configuration.TryToDomain(
                out var configuration,
                out var conversionError))
        {
            return new FileLoadAttempt(
                FileLoadStatus.Corrupt,
                Message: conversionError);
        }

        return new FileLoadAttempt(
            FileLoadStatus.Loaded,
            envelope,
            configuration);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
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
            error = $"The corrupt configuration could not be quarantined: {exception.Message}";
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            quarantinedPath = null;
            error = $"The corrupt configuration could not be quarantined: {exception.Message}";
            return false;
        }
    }

    private sealed record ConfigEnvelope(
        int SchemaVersion,
        DateTimeOffset SavedAtUtc,
        AppConfigurationDocument? Configuration);

    private sealed record AppConfigurationDocument(
        WingEndpoint? Foh,
        WingEndpoint? Monitor,
        SyncDirection Direction,
        InitialSync InitialSync,
        SafetySettingsDocument? Safety,
        SyncScope[]? Scopes,
        ChannelMappingDocument? Channels)
    {
        public static AppConfigurationDocument FromDomain(
            AppConfiguration configuration) =>
            new(
                configuration.Foh,
                configuration.Monitor,
                configuration.Direction,
                configuration.InitialSync,
                SafetySettingsDocument.FromDomain(configuration.Safety),
                configuration.Scopes.ToArray(),
                ChannelMappingDocument.FromDomain(configuration.Channels));

        public bool TryToDomain(
            out AppConfiguration? configuration,
            out string? error)
        {
            configuration = null;

            if (Foh is null || Monitor is null)
            {
                error = "The configuration must contain both FOH and monitor endpoints.";
                return false;
            }

            if (!Enum.IsDefined(Direction))
            {
                error = $"The synchronization direction value '{Direction}' is invalid.";
                return false;
            }

            if (!Enum.IsDefined(InitialSync))
            {
                error = $"The initial synchronization value '{InitialSync}' is invalid.";
                return false;
            }

            if (Scopes?.Any(static scope => !Enum.IsDefined(scope)) == true)
            {
                error = "The configuration contains an unknown synchronization scope.";
                return false;
            }

            if (Channels?.InputChannels?.Any(static mapping => mapping is null) == true ||
                Channels?.AuxChannels?.Any(static mapping => mapping is null) == true)
            {
                error = "The channel mapping contains a null entry.";
                return false;
            }

            configuration = new AppConfiguration(
                Foh,
                Monitor,
                Direction,
                InitialSync,
                Safety?.ToDomain(),
                Scopes,
                Channels?.ToDomain());
            error = null;
            return true;
        }
    }

    private sealed record SafetySettingsDocument(
        bool? DryRun,
        bool? RequireReadback,
        bool? AllowHighRiskWrites,
        bool? StopOnVerificationFailure,
        float? FloatTolerance,
        TimeSpan? EchoSuppressionWindow)
    {
        public static SafetySettingsDocument FromDomain(
            SafetySettings settings) =>
            new(
                settings.DryRun,
                settings.RequireReadback,
                settings.AllowHighRiskWrites,
                settings.StopOnVerificationFailure,
                settings.FloatTolerance,
                settings.EchoSuppressionWindow);

        public SafetySettings ToDomain()
        {
            var defaults = SafetySettings.SafeDefaults;
            return new SafetySettings(
                DryRun ?? defaults.DryRun,
                RequireReadback ?? defaults.RequireReadback,
                AllowHighRiskWrites ?? defaults.AllowHighRiskWrites,
                StopOnVerificationFailure ?? defaults.StopOnVerificationFailure,
                FloatTolerance ?? defaults.FloatTolerance,
                EchoSuppressionWindow ?? defaults.EchoSuppressionWindow);
        }
    }

    private sealed record ChannelMappingDocument(
        InputChannelMapping?[]? InputChannels,
        AuxChannelMapping?[]? AuxChannels)
    {
        public static ChannelMappingDocument FromDomain(
            ChannelMapping mapping) =>
            new(
                mapping.InputChannels.ToArray(),
                mapping.AuxChannels.ToArray());

        public ChannelMapping ToDomain() =>
            new(
                InputChannels?.Select(static mapping => mapping!),
                AuxChannels?.Select(static mapping => mapping!));
    }

    private enum FileLoadStatus
    {
        Missing,
        Loaded,
        Corrupt,
        UnsupportedSchema,
        Failed,
    }

    private sealed record FileLoadAttempt(
        FileLoadStatus Status,
        ConfigEnvelope? Envelope = null,
        AppConfiguration? Configuration = null,
        int? SchemaVersion = null,
        string? Message = null);
}
