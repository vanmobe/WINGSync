namespace WingSync.Infrastructure.Diagnostics;

/// <summary>
/// Configures the structured rotating JSON-lines logger.
/// </summary>
public sealed class JsonLineLoggerOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JsonLineLoggerOptions"/> class.
    /// </summary>
    /// <param name="directoryPath">The directory in which log files are stored.</param>
    public JsonLineLoggerOptions(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = directoryPath;

        RedactedPropertyNames.UnionWith(
        [
            "apiKey",
            "apiSecret",
            "authorization",
            "credential",
            "password",
            "secret",
            "token",
        ]);
    }

    /// <summary>
    /// Gets the directory in which log files are stored.
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// Gets or sets the active log filename.
    /// </summary>
    public string FileName { get; set; } = "wingsync.jsonl";

    /// <summary>
    /// Gets or sets the maximum size of the active log file before rotation.
    /// </summary>
    public long MaximumFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the number of rotated files retained in addition to the active file.
    /// </summary>
    public int RetainedFileCount { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum encoded size of one JSON-lines entry.
    /// Oversized entries are safely reduced before being written.
    /// </summary>
    public int MaximumEntrySizeBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Gets or sets the lowest severity that is persisted and raised in memory.
    /// </summary>
    public WingLogLevel MinimumLevel { get; set; } = WingLogLevel.Information;

    /// <summary>
    /// Gets case-insensitive structured property names whose values are replaced by
    /// <c>[REDACTED]</c>.
    /// </summary>
    public ISet<string> RedactedPropertyNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets literal sensitive values that are replaced in messages, string properties,
    /// exception messages and stack traces.
    /// </summary>
    public IList<string> SensitiveValues { get; } = [];

    internal void Validate()
    {
        if (!string.Equals(
                Path.GetFileName(FileName),
                FileName,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(FileName))
        {
            throw new ArgumentException(
                "The log filename must be a filename without directory components.",
                nameof(FileName));
        }

        if (MaximumFileSizeBytes is < 4_096 or > 1024L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumFileSizeBytes),
                MaximumFileSizeBytes,
                "The maximum log size must be between 4 KiB and 1 GiB.");
        }

        if (RetainedFileCount is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetainedFileCount),
                RetainedFileCount,
                "The retained file count must be between 0 and 100.");
        }

        if (MaximumEntrySizeBytes is < 512
            || MaximumEntrySizeBytes > MaximumFileSizeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumEntrySizeBytes),
                MaximumEntrySizeBytes,
                "The maximum entry size must be at least 512 bytes and no larger than the log file.");
        }
    }
}
