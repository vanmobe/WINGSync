namespace WingSync.Infrastructure.Persistence;

/// <summary>
/// Configures persisted per-console state snapshots.
/// </summary>
public sealed class WingStateCacheOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WingStateCacheOptions"/> class.
    /// </summary>
    /// <param name="directoryPath">The directory in which identity-keyed cache files are stored.</param>
    public WingStateCacheOptions(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = directoryPath;
    }

    /// <summary>
    /// Gets the cache directory.
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// Gets or sets how long rapid updates are coalesced before disk I/O.
    /// </summary>
    public TimeSpan WriteDebounce { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets or sets the maximum accepted serialized or loaded cache-file size.
    /// </summary>
    public long MaximumFileSizeBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum age used when an active connection epoch explicitly
    /// asks whether observations are still fresh.
    /// </summary>
    public TimeSpan MaximumFreshAge { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (WriteDebounce < TimeSpan.Zero || WriteDebounce > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(WriteDebounce),
                WriteDebounce,
                "The write debounce must be between zero and one minute.");
        }

        if (MaximumFileSizeBytes is < 4_096 or > 256L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumFileSizeBytes),
                MaximumFileSizeBytes,
                "The cache size limit must be between 4 KiB and 256 MiB.");
        }

        if (MaximumFreshAge <= TimeSpan.Zero
            || MaximumFreshAge > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumFreshAge),
                MaximumFreshAge,
                "The maximum fresh age must be greater than zero and no more than one hour.");
        }
    }
}
