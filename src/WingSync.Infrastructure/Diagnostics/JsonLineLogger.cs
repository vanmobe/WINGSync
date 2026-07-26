using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WingSync.Infrastructure.Diagnostics;

/// <summary>
/// Writes redacted structured diagnostics as one JSON object per line and rotates files by size.
/// </summary>
public sealed class JsonLineLogger : IAsyncDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly TimeProvider _timeProvider;
    private readonly string _directoryPath;
    private readonly string _activePath;
    private readonly string _fileStem;
    private readonly string _fileExtension;
    private readonly long _maximumFileSizeBytes;
    private readonly int _retainedFileCount;
    private readonly int _maximumEntrySizeBytes;
    private readonly WingLogLevel _minimumLevel;
    private readonly HashSet<string> _redactedPropertyNames;
    private readonly string[] _sensitiveValues;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonLineLogger"/> class.
    /// </summary>
    /// <param name="options">Logger, rotation and redaction options.</param>
    /// <param name="timeProvider">
    /// The clock used for entry timestamps, or <see cref="TimeProvider.System"/> when omitted.
    /// </param>
    public JsonLineLogger(
        JsonLineLoggerOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _timeProvider = timeProvider ?? TimeProvider.System;
        _directoryPath = Path.GetFullPath(options.DirectoryPath);
        _activePath = Path.Combine(_directoryPath, options.FileName);
        _fileStem = Path.GetFileNameWithoutExtension(options.FileName);
        _fileExtension = Path.GetExtension(options.FileName);
        _maximumFileSizeBytes = options.MaximumFileSizeBytes;
        _retainedFileCount = options.RetainedFileCount;
        _maximumEntrySizeBytes = options.MaximumEntrySizeBytes;
        _minimumLevel = options.MinimumLevel;
        _redactedPropertyNames = new HashSet<string>(
            options.RedactedPropertyNames,
            StringComparer.OrdinalIgnoreCase);
        _sensitiveValues = options.SensitiveValues
            .Where(static value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static value => value.Length)
            .ToArray();

        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        _serializerOptions.Converters.Add(new JsonStringEnumConverter<WingLogLevel>());
    }

    /// <summary>
    /// Raised after a redacted entry has been persisted successfully.
    /// Subscribers receive no unredacted values.
    /// </summary>
    public event EventHandler<WingLogEntryEventArgs>? EntryWritten;

    /// <summary>
    /// Writes one structured, redacted diagnostic entry.
    /// </summary>
    /// <param name="level">The severity.</param>
    /// <param name="eventName">A stable machine-readable event name.</param>
    /// <param name="message">A human-readable message.</param>
    /// <param name="properties">Optional structured properties.</param>
    /// <param name="exception">An optional exception.</param>
    /// <param name="cancellationToken">Cancels the file write.</param>
    /// <returns>A task that completes after the line has been flushed.</returns>
    public async ValueTask LogAsync(
        WingLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(message);

        if (level < _minimumLevel)
        {
            return;
        }

        var entry = CreateEntry(level, eventName, message, properties, exception);
        var encodedEntry = SerializeWithSizeGuard(entry);
        var encodedLine = new byte[encodedEntry.Length + NewLine.Length];
        encodedEntry.CopyTo(encodedLine, 0);
        NewLine.CopyTo(encodedLine, encodedEntry.Length);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Directory.CreateDirectory(_directoryPath);
            RotateIfRequired(encodedLine.Length);

            await using var stream = new FileStream(
                _activePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(encodedLine, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        RaiseEntryWritten(entry);
    }

    /// <summary>
    /// Waits for an active write to complete and releases logger resources.
    /// </summary>
    /// <returns>A task representing asynchronous disposal.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
        }
        finally
        {
            _writeGate.Release();
            _writeGate.Dispose();
        }
    }

    private WingLogEntry CreateEntry(
        WingLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? properties,
        Exception? exception)
    {
        var safeProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (properties is not null)
        {
            foreach (var property in properties)
            {
                safeProperties[property.Key] = SanitizeProperty(property.Key, property.Value);
            }
        }

        WingLogExceptionDetails? safeException = null;
        if (exception is not null)
        {
            safeException = new WingLogExceptionDetails(
                exception.GetType().FullName ?? exception.GetType().Name,
                RedactText(exception.Message),
                exception.StackTrace is null ? null : RedactText(exception.StackTrace));
        }

        return new WingLogEntry(
            _timeProvider.GetUtcNow(),
            level,
            eventName,
            RedactText(message),
            safeProperties,
            safeException);
    }

    private JsonElement SanitizeProperty(string propertyName, object? value)
    {
        if (_redactedPropertyNames.Contains(propertyName))
        {
            return JsonSerializer.SerializeToElement("[REDACTED]");
        }

        JsonNode? node;
        try
        {
            node = value is null
                ? null
                : JsonSerializer.SerializeToNode(
                    value,
                    value.GetType(),
                    _serializerOptions);
        }
        catch (NotSupportedException)
        {
            return JsonSerializer.SerializeToElement(
                $"<unserializable:{value?.GetType().Name ?? "null"}>");
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(
                $"<unserializable:{value?.GetType().Name ?? "null"}>");
        }

        var sanitized = SanitizeNode(propertyName, node);
        return JsonSerializer.SerializeToElement(sanitized, _serializerOptions);
    }

    private JsonNode? SanitizeNode(string? propertyName, JsonNode? node)
    {
        if (propertyName is not null && _redactedPropertyNames.Contains(propertyName))
        {
            return JsonValue.Create("[REDACTED]");
        }

        switch (node)
        {
            case JsonObject jsonObject:
            {
                var sanitizedObject = new JsonObject();
                foreach (var property in jsonObject)
                {
                    sanitizedObject[property.Key] = SanitizeNode(property.Key, property.Value);
                }

                return sanitizedObject;
            }

            case JsonArray jsonArray:
            {
                var sanitizedArray = new JsonArray();
                foreach (var item in jsonArray)
                {
                    sanitizedArray.Add(SanitizeNode(null, item));
                }

                return sanitizedArray;
            }

            case JsonValue jsonValue
                when jsonValue.TryGetValue<string>(out var stringValue):
                return JsonValue.Create(RedactText(stringValue));

            default:
                return node?.DeepClone();
        }
    }

    private string RedactText(string value)
    {
        var redacted = value;
        foreach (var sensitiveValue in _sensitiveValues)
        {
            redacted = redacted.Replace(
                sensitiveValue,
                "[REDACTED]",
                StringComparison.Ordinal);
        }

        return redacted;
    }

    private byte[] SerializeWithSizeGuard(WingLogEntry entry)
    {
        var encoded = JsonSerializer.SerializeToUtf8Bytes(entry, _serializerOptions);
        if (encoded.Length <= _maximumEntrySizeBytes)
        {
            return encoded;
        }

        var reducedMessageLength = Math.Min(entry.Message.Length, 4_096);
        var reducedProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["entryTruncated"] = JsonSerializer.SerializeToElement(true),
            ["originalEncodedBytes"] = JsonSerializer.SerializeToElement(encoded.Length),
        };

        var reduced = entry with
        {
            Message = entry.Message[..reducedMessageLength],
            Properties = reducedProperties,
            Exception = entry.Exception is null
                ? null
                : entry.Exception with { StackTrace = null },
        };

        encoded = JsonSerializer.SerializeToUtf8Bytes(reduced, _serializerOptions);
        if (encoded.Length <= _maximumEntrySizeBytes)
        {
            return encoded;
        }

        var minimal = reduced with
        {
            Message = "<entry exceeded configured size>",
            Exception = null,
        };
        return JsonSerializer.SerializeToUtf8Bytes(minimal, _serializerOptions);
    }

    private void RotateIfRequired(int nextLineSize)
    {
        var currentLength = File.Exists(_activePath)
            ? new FileInfo(_activePath).Length
            : 0;
        if (currentLength == 0
            || currentLength + nextLineSize <= _maximumFileSizeBytes)
        {
            return;
        }

        if (_retainedFileCount == 0)
        {
            File.Delete(_activePath);
            return;
        }

        for (var index = _retainedFileCount; index >= 1; index--)
        {
            var destination = GetRotatedPath(index);
            if (index == _retainedFileCount && File.Exists(destination))
            {
                File.Delete(destination);
            }

            var source = index == 1
                ? _activePath
                : GetRotatedPath(index - 1);
            if (File.Exists(source))
            {
                File.Move(source, destination);
            }
        }
    }

    private string GetRotatedPath(int index)
    {
        return Path.Combine(
            _directoryPath,
            $"{_fileStem}.{index}{_fileExtension}");
    }

    private void RaiseEntryWritten(WingLogEntry entry)
    {
        var handlers = EntryWritten;
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new WingLogEntryEventArgs(entry);
        foreach (EventHandler<WingLogEntryEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
#pragma warning disable CA1031 // A UI/log observer must never fail the logging caller.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Intentionally isolated from persistence and other subscribers.
            }
        }
    }
}
