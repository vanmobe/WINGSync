using System.Globalization;
using System.IO;
using System.Text;

namespace WingSync.UiTests;

internal sealed class UiTestEnvironment
{
    private readonly string applicationPath;
    private readonly string artifactsRoot;

    public UiTestEnvironment(string applicationPath, string artifactsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsRoot);
        this.applicationPath = Path.GetFullPath(applicationPath);
        this.artifactsRoot = Path.GetFullPath(artifactsRoot);
        if (!File.Exists(this.applicationPath))
        {
            throw new FileNotFoundException(
                "The WingSync executable supplied with --app does not exist.",
                this.applicationPath);
        }

        Directory.CreateDirectory(this.artifactsRoot);
    }

    public void RunScenario(string name, Action<UiScenarioContext> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        var scenarioDirectory = Path.Combine(artifactsRoot, Sanitize(name));
        Directory.CreateDirectory(scenarioDirectory);
        var dataDirectory = Path.Combine(scenarioDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        using var context = new UiScenarioContext(
            applicationPath,
            scenarioDirectory,
            dataDirectory);
        try
        {
            body(context);
        }
#pragma warning disable CA1031 // Preserve the original failure after best-effort diagnostics.
        catch
#pragma warning restore CA1031
        {
            context.CaptureFailureArtifacts();
            throw;
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) || char.IsWhiteSpace(character)
                ? '_'
                : character);
        }

        return builder.ToString();
    }

    public static UiTestOptions ParseOptions(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? app = null;
        string? artifacts = null;
        string? filter = null;
        string? guideScreenshots = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--app" when index + 1 < arguments.Count:
                    app = arguments[++index];
                    break;
                case "--artifacts" when index + 1 < arguments.Count:
                    artifacts = arguments[++index];
                    break;
                case "--filter" when index + 1 < arguments.Count:
                    filter = arguments[++index];
                    break;
                case "--capture-guide" when index + 1 < arguments.Count:
                    guideScreenshots = Path.GetFullPath(arguments[++index]);
                    break;
                default:
                    throw new ArgumentException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Unknown or incomplete UI test argument '{arguments[index]}'."));
            }
        }

        if (string.IsNullOrWhiteSpace(app))
        {
            throw new ArgumentException(
                "Usage: WingSync.UiTests --app <WingSync.exe> "
                + "[--artifacts <directory>] [--filter <name>] "
                + "[--capture-guide <directory>]");
        }

        var runId = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        artifacts ??= Path.Combine(
            Path.GetTempPath(),
            "WingSync.UiTests",
            runId);
        return new UiTestOptions(app, artifacts, filter, guideScreenshots);
    }
}

internal sealed record UiTestOptions(
    string ApplicationPath,
    string ArtifactsDirectory,
    string? Filter,
    string? GuideScreenshotsDirectory);

internal sealed class UiScenarioContext : IDisposable
{
    private readonly string applicationPath;
    private readonly string scenarioDirectory;
    private readonly List<UiAppSession> sessions = [];
    private bool disposed;

    public UiScenarioContext(
        string applicationPath,
        string scenarioDirectory,
        string dataDirectory)
    {
        this.applicationPath = applicationPath;
        this.scenarioDirectory = scenarioDirectory;
        DataDirectory = dataDirectory;
    }

    public string DataDirectory { get; }

    public UiAppSession Launch(bool demoDiscoveryOffline = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var session = UiAppSession.Start(
            applicationPath,
            DataDirectory,
            demoDiscoveryOffline);
        sessions.Add(session);
        return session;
    }

    public void CaptureFailureArtifacts()
    {
        for (var index = 0; index < sessions.Count; index++)
        {
            sessions[index].CaptureFailureArtifacts(
                scenarioDirectory,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"session-{index + 1}"));
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var session in sessions.AsEnumerable().Reverse())
        {
            session.Dispose();
        }
    }
}
