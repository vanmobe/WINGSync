using WingSync.Core.Abstractions;

namespace WingSync.Infrastructure.Wapi;

/// <summary>Locates the bundled helper and creates one isolated process per console.</summary>
public sealed class WapiSessionFactory : IWingSessionFactory
{
    private readonly string helperPath;
    private readonly ISyncObserver observer;
    private readonly IClock clock;

    /// <summary>Initializes the factory with an explicit helper executable path.</summary>
    public WapiSessionFactory(string helperPath, ISyncObserver observer, IClock? clock = null)
    {
        this.helperPath = Path.GetFullPath(helperPath ?? throw new ArgumentNullException(nameof(helperPath)));
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.clock = clock ?? new SystemClock();
    }

    /// <inheritdoc />
    public IWingSession Create(string role) =>
        new WapiProcessSession(role, helperPath, observer, clock);

    /// <summary>Finds the helper in the package or common developer build locations.</summary>
    public static string LocateHelper(string? applicationDirectory = null)
    {
        var baseDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "WingSync.WapiHost.exe"),
            Path.Combine(baseDirectory, "native", "WingSync.WapiHost.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "build", "native", "Debug", "WingSync.WapiHost.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "build", "native", "Release", "WingSync.WapiHost.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "build", "native", "Debug", "WingSync.WapiHost.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "build", "native", "Release", "WingSync.WapiHost.exe")),
        };

        return candidates.FirstOrDefault(File.Exists) ??
            throw new FileNotFoundException(
                "WingSync.WapiHost.exe werd niet naast de applicatie of in de ontwikkelbuild gevonden.");
    }
}
