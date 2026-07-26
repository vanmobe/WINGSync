namespace WingSync.Infrastructure.Persistence;

/// <summary>
/// Configures the atomic application-configuration store.
/// </summary>
public sealed class ConfigStoreOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigStoreOptions"/> class.
    /// </summary>
    /// <param name="filePath">The JSON configuration file.</param>
    public ConfigStoreOptions(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    /// <summary>
    /// Gets the configuration file path.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets or sets the maximum accepted serialized or loaded configuration size.
    /// </summary>
    public long MaximumFileSizeBytes { get; set; } = 4 * 1024 * 1024;

    internal void Validate()
    {
        if (MaximumFileSizeBytes is < 1_024 or > 64L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumFileSizeBytes),
                MaximumFileSizeBytes,
                "The configuration size limit must be between 1 KiB and 64 MiB.");
        }
    }
}
