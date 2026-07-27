using System.IO;
using System.Windows.Automation;

namespace WingSync.UiTests;

internal static class GuideScreenshotScenarios
{
    public static void Capture(
        UiTestEnvironment environment,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        environment.RunScenario(
            "user-guide-screenshots",
            context => Capture(context, outputDirectory));
    }

    private static void Capture(
        UiScenarioContext context,
        string outputDirectory)
    {
        var app = context.Launch();
        Save(app, outputDirectory, "01-status-overview.png");

        app.Navigate("NavigationSync", "Configure synchronization");
        Save(app, outputDirectory, "02-find-consoles-and-safety.png");

        app.Invoke("AddAuxMappingButton");
        app.WaitFor(
            () => app.GetGridRowCount("MappingGrid") == 2,
            TimeSpan.FromSeconds(5),
            "an input and AUX mapping");
        app.ScrollIntoView("MappingGrid");
        Save(app, outputDirectory, "03-channel-mapping.png");

        CompleteConnectionTest(app);
        app.Navigate("NavigationStatus", "Status");
        app.Invoke("StartButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Dry run active",
            TimeSpan.FromSeconds(12));
        app.WaitForElementName("PreviewedWritesText", "1");
        Save(app, outputDirectory, "04-dry-run-preview.png");

        app.Navigate("NavigationActivity", "Activity and diagnostics");
        Save(app, outputDirectory, "07-activity-log.png");
        app.Invoke("StopButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(12));

        app.Navigate("NavigationStatus", "Status");
        app.WaitForElementName("StartButton", "Enable live");
        app.Invoke("StartButton");
        app.WaitForElementName("StartButton", "Start live");
        app.Invoke("StartButton");
        var dialog = app.WaitForDialog(
            "Confirm fresh live preview",
            TimeSpan.FromSeconds(12));
        app.AssertDialogSafeDefaultNoContract(dialog);
        Save(app, outputDirectory, "05-live-confirmation.png", dialog);
        app.ClickDialogYes(dialog);

        app.WaitForElementName(
            "OverallStatusText",
            "Live active",
            TimeSpan.FromSeconds(12));
        app.WaitForElementName("VerifiedWritesText", "1");
        Save(app, outputDirectory, "06-live-active-readback.png");
        app.Invoke("StopButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(12));

        app.Navigate("NavigationSettings", "Settings");
        Save(app, outputDirectory, "08-settings-cache-support.png");

        app.Navigate("NavigationSync", "Configure synchronization");
        app.SetText("FohIpTextBox", string.Empty);
        app.Invoke("TestConnectionsButton");
        app.Navigate("NavigationStatus", "Status");
        app.WaitFor(
            () =>
            {
                var banner = app.TryFindById("ProblemBanner");
                return banner is not null && !banner.Current.IsOffscreen;
            },
            TimeSpan.FromSeconds(5),
            "the blocking problem banner");
        Save(app, outputDirectory, "09-blocking-fault.png");
        app.CloseAndWait();
    }

    private static void CompleteConnectionTest(UiAppSession app)
    {
        app.Navigate("NavigationSync", "Configure synchronization");
        app.Invoke("TestConnectionsButton");
        app.WaitForElementNameContaining(
            "DiscoveryStatusText",
            "WAPI healthy",
            TimeSpan.FromSeconds(12));
        app.WaitForEnabled("StartButton", enabled: true);
    }

    private static void Save(
        UiAppSession app,
        string outputDirectory,
        string fileName,
        AutomationElement? overlay = null)
    {
        Thread.Sleep(250);
        app.CaptureScreenshot(Path.Combine(outputDirectory, fileName), overlay);
    }
}
