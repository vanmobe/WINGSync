using System.IO.Compression;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;

namespace WingSync.UiTests;

internal static class UiScenarios
{
    private static readonly string[] ScopeIds =
    [
        "Scope-Cust",
        "Scope-Tags",
        "Scope-Conn",
        "Scope-In",
        "Scope-Filter",
        "Scope-Delay",
        "Scope-Gate",
        "Scope-Dyn",
        "Scope-Pre",
        "Scope-Post",
        "Scope-Eq",
        "Scope-Pan",
        "Scope-Main",
        "Scope-Bus",
        "Scope-Fader",
        "Scope-Mute",
        "Scope-Config",
    ];

    public static void Register(TestSuite suite, UiTestEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(environment);
        suite.Add(
            "Launch defaults and accessibility contract",
            () => environment.RunScenario(
                "launch-defaults-accessibility",
                LaunchDefaultsAndAccessibility));
        suite.Add(
            "Navigation exposes every page",
            () => environment.RunScenario(
                "navigation",
                Navigation));
        suite.Add(
            "Mapping auto-map AUX invalid text and CanStart",
            () => environment.RunScenario(
                "mapping-validation",
                MappingValidation));
        suite.Add(
            "Configuration persists across restart",
            () => environment.RunScenario(
                "configuration-persistence",
                ConfigurationPersistence));
        suite.Add(
            "Dry-run start stop restart locks editor and keeps truthful badge",
            () => environment.RunScenario(
                "dry-run-lifecycle",
                DryRunLifecycle));
        suite.Add(
            "Live preview defaults to No and applies no writes",
            () => environment.RunScenario(
                "live-preview-safe-no",
                LivePreviewSafeNo));
        suite.Add(
            "Confirmed live apply exposes writes and emergency-stop truthfully",
            () => environment.RunScenario(
                "live-apply-status-stop",
                LiveApplyStatusAndStop));
        suite.Add(
            "High-risk live review requires a separate acknowledgement",
            () => environment.RunScenario(
                "high-risk-live-review",
                HighRiskLiveReview));
        suite.Add(
            "Close while running handles No Yes and duplicate close",
            () => environment.RunScenario(
                "close-running",
                CloseWhileRunning));
        suite.Add(
            "Support bundle is complete and readable",
            () => environment.RunScenario(
                "support-bundle",
                SupportBundle));
        suite.Add(
            "Minimum-size layout keeps primary actions reachable",
            () => environment.RunScenario(
                "minimum-size",
                MinimumSize));
        suite.Add(
            "Live workflow shows identities truthful metrics and revokes approval on edits",
            () => environment.RunScenario(
                "live-workflow-truth",
                LiveWorkflowTruth));
        suite.Add(
            "Offline cache is visible across restart without replay",
            () => environment.RunScenario(
                "offline-cache-restart",
                OfflineCacheAcrossRestart));
        suite.Add(
            "Keyboard shortcuts navigate and emergency-stop",
            () => environment.RunScenario(
                "keyboard-shortcuts",
                KeyboardShortcuts));
    }

    private static void LaunchDefaultsAndAccessibility(UiScenarioContext context)
    {
        var app = context.Launch();
        AssertEx.Equal("MainWindow", app.MainWindow.Current.AutomationId);
        AssertEx.Equal("WingSync", app.MainWindow.Current.Name);
        app.WaitForElementName("HeaderTitleText", "Status");
        app.WaitForElementName("OverallStatusText", "Not started");
        app.WaitForElementName("StartButton", "Test connection");
        AssertEx.True(app.FindById("StartButton").Current.IsEnabled);
        AssertEx.False(app.FindById("StopButton").Current.IsEnabled);
        app.WaitForElementNameContaining(
            "SetupProgressIndicator",
            "2 van vier",
            TimeSpan.FromSeconds(5));
        var visibleProblem = app.TryFindById("ProblemBanner");
        AssertEx.True(
            visibleProblem is null || visibleProblem.Current.IsOffscreen,
            "A fresh demo launch must not show a blocking-problem banner.");

        foreach (var id in new[]
                 {
                     "Navigation",
                     "NavigationStatus",
                     "NavigationSync",
                     "NavigationActivity",
                     "NavigationSettings",
                     "HeaderTitleText",
                     "WingSyncLogo",
                     "DirectionSummaryText",
                     "OverallStatusText",
                     "RunModeText",
                     "FohStatusText",
                     "FlowStatusText",
                     "StageStatusText",
                     "StartButton",
                     "StopButton",
                     "SetupProgressText",
                     "SetupProgressIndicator",
                     "NextStepText",
                     "NextStepFooterText",
                     "OpenSetupButton",
                     "FohSerialText",
                     "StageSerialText",
                     "VerifiedWritesText",
                     "PreviewedWritesText",
                     "BlockedWritesText",
                 })
        {
            app.AssertUniqueId(id);
        }
        AssertEx.False(
            string.IsNullOrWhiteSpace(app.FindById("DirectionSummaryText").Current.Name),
            "The dynamic console direction must be exposed through UIA.");
        AssertEx.False(
            string.Equals(
                "Direction of console flow",
                app.FindById("DirectionSummaryText").Current.Name,
                StringComparison.Ordinal),
            "The direction UIA name must contain the current direction, not a static label.");
        AssertEx.False(
            string.IsNullOrWhiteSpace(app.FindById("NextStepFooterText").Current.Name),
            "The dynamic next step must be exposed through UIA.");
        AssertEx.False(
            string.Equals(
                "Next step",
                app.FindById("NextStepFooterText").Current.Name,
                StringComparison.Ordinal),
            "The footer UIA name must contain the current next step, not a static label.");
        var stopButton = app.FindById("StopButton");
        AssertEx.Contains(
            "synchronization writes",
            stopButton.Current.Name,
            StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(
            "reeds verstuurde transactie",
            stopButton.Current.HelpText,
            StringComparison.OrdinalIgnoreCase);

        var liveSetting = app.FindById("OverallStatusText")
            .GetCurrentPropertyValue(AutomationElementIdentifiers.LiveSettingProperty);
        if (liveSetting is AutomationLiveSetting automationLiveSetting)
        {
            AssertEx.Equal(
                AutomationLiveSetting.Polite,
                automationLiveSetting,
                "Overall status must be announced as a polite UIA live region.");
        }
        else
        {
            // Some Windows UIA2 clients return an ArgumentException sentinel for
            // the newer LiveSetting property even though WPF exposes it. The
            // stable AutomationId/name and lifecycle scenarios below still prove
            // that status changes are observable to assistive technology.
            AssertEx.True(
                liveSetting is ArgumentException ||
                ReferenceEquals(liveSetting, AutomationElement.NotSupported),
                "The UIA provider returned an unexpected LiveSetting value.");
        }

        var runModeLiveSetting = app.FindById("RunModeText")
            .GetCurrentPropertyValue(AutomationElementIdentifiers.LiveSettingProperty);
        if (runModeLiveSetting is AutomationLiveSetting runModeAutomationLiveSetting)
        {
            AssertEx.Equal(
                AutomationLiveSetting.Assertive,
                runModeAutomationLiveSetting,
                "The write-safety badge must be announced as an assertive UIA live region.");
        }
        else
        {
            AssertEx.True(
                runModeLiveSetting is ArgumentException ||
                ReferenceEquals(runModeLiveSetting, AutomationElement.NotSupported),
                "RunModeText returned an unexpected UIA LiveSetting value.");
        }

        app.Invoke("OpenSetupButton");
        app.WaitForElementName("HeaderTitleText", "Configure synchronization");
        foreach (var id in new[]
                 {
                     "FohConsoleCombo",
                     "FohIpTextBox",
                     "StageConsoleCombo",
                     "StageIpTextBox",
                     "DiscoverButton",
                     "TestConnectionsButton",
                     "DiscoveryStatusText",
                     "ScopeList",
                     "DirectionCombo",
                     "DryRunCheckBox",
                     "VerifyReadbackCheckBox",
                     "AllowHighRiskWritesCheckBox",
                     "AutoMapButton",
                     "AddMappingButton",
                     "AddAuxMappingButton",
                     "RemoveMappingButton",
                     "MappingGrid",
                     "ValidationSummaryText",
                     "SaveConfigurationButton",
                 })
        {
            app.AssertUniqueId(id);
        }
        foreach (var id in new[]
                 {
                     "FohIpTextBox",
                     "StageIpTextBox",
                     "DirectionCombo",
                     "DryRunCheckBox",
                     "VerifyReadbackCheckBox",
                     "AllowHighRiskWritesCheckBox",
                 })
        {
            AssertEx.False(
                string.IsNullOrWhiteSpace(app.FindById(id).Current.Name),
                $"{id} must expose an accessible name.");
        }

        foreach (var id in new[] { "DiscoveryStatusText", "ValidationSummaryText" })
        {
            var statusLiveSetting = app.FindById(id)
                .GetCurrentPropertyValue(AutomationElementIdentifiers.LiveSettingProperty);
            if (statusLiveSetting is AutomationLiveSetting statusAutomationLiveSetting)
            {
                AssertEx.Equal(
                    AutomationLiveSetting.Polite,
                    statusAutomationLiveSetting,
                    $"{id} must be announced as a polite UIA live region.");
            }
            else
            {
                AssertEx.True(
                    statusLiveSetting is ArgumentException ||
                    ReferenceEquals(statusLiveSetting, AutomationElement.NotSupported),
                    $"{id} returned an unexpected UIA LiveSetting value.");
            }
        }

        AssertEx.Equal(
            "127.10.0.10",
            app.GetTextValue("FohIpTextBox"));
        AssertEx.Equal(
            "127.10.0.11",
            app.GetTextValue("StageIpTextBox"));
        AssertEx.Equal(ToggleState.On, app.GetToggleState("DryRunCheckBox"));
        AssertEx.Equal(ToggleState.On, app.GetToggleState("VerifyReadbackCheckBox"));
        AssertEx.Equal(
            ToggleState.Off,
            app.GetToggleState("AllowHighRiskWritesCheckBox"));
        AssertEx.Equal(1, app.GetGridRowCount("MappingGrid"));

        var safeScopes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Scope-Cust",
            "Scope-Filter",
            "Scope-Delay",
            "Scope-Gate",
            "Scope-Dyn",
            "Scope-Eq",
        };
        foreach (var scopeId in ScopeIds)
        {
            app.AssertUniqueId(scopeId);
            var scope = app.FindById(scopeId);
            AssertEx.False(
                string.IsNullOrWhiteSpace(scope.Current.Name),
                $"{scopeId} must expose an accessible name.");
            AssertEx.False(
                string.IsNullOrWhiteSpace(scope.Current.HelpText),
                $"{scopeId} must expose UIA help text.");
            AssertEx.Equal(
                safeScopes.Contains(scopeId) ? ToggleState.On : ToggleState.Off,
                app.GetToggleState(scopeId),
                $"Unexpected safe-default state for {scopeId}.");
        }

        app.CloseAndWait();
    }

    private static void LiveWorkflowTruth(UiScenarioContext context)
    {
        var app = context.Launch();
        AssertEx.Contains(
            "DEMO-FOH-0001",
            app.FindById("FohSerialText").Current.Name);
        AssertEx.Contains(
            "DEMO-MON-0001",
            app.FindById("StageSerialText").Current.Name);
        app.Navigate("NavigationSync", "Configure synchronization");
        AssertEx.Contains(
            "S/N DEMO-FOH-0001",
            app.FindById("FohConsoleCombo").Current.Name);
        CompleteConnectionTest(app);
        app.Navigate("NavigationStatus", "Status");

        app.Invoke("StartButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Dry run active",
            TimeSpan.FromSeconds(10));
        app.WaitForElementName("VerifiedWritesText", "0");
        app.WaitForElementName("PreviewedWritesText", "1");
        app.Invoke("StopButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(10));

        app.Navigate("NavigationSync", "Configure synchronization");
        app.ToggleTo("DryRunCheckBox", ToggleState.Off);
        app.ToggleTo("AllowHighRiskWritesCheckBox", ToggleState.On);
        app.ToggleTo("Scope-Eq", ToggleState.Off);
        app.WaitFor(
            () => app.GetToggleState("DryRunCheckBox") == ToggleState.On &&
                  app.GetToggleState("AllowHighRiskWritesCheckBox") == ToggleState.Off,
            TimeSpan.FromSeconds(5),
            "a topology edit to revoke live and high-risk approval");
        app.WaitForElementNameContaining(
            "ConfigurationSafetyNoticeText",
            "ingetrokken",
            TimeSpan.FromSeconds(5));
        app.CloseAndWait();
    }

    private static void OfflineCacheAcrossRestart(UiScenarioContext context)
    {
        var first = context.Launch();
        CompleteConnectionTest(first);
        first.Navigate("NavigationStatus", "Status");
        first.Invoke("StartButton");
        first.WaitForElementName(
            "OverallStatusText",
            "Dry run active",
            TimeSpan.FromSeconds(10));
        first.Invoke("StopButton");
        first.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(10));
        first.WaitForElementNameContaining(
            "FohCacheSummaryText",
            "values",
            TimeSpan.FromSeconds(5));
        first.WaitForElementNameContaining(
            "StageCacheSummaryText",
            "values",
            TimeSpan.FromSeconds(5));
        first.CloseAndWait();

        var second = context.Launch(demoDiscoveryOffline: true);
        second.WaitForElementNameContaining(
            "FohCacheSummaryText",
            "offline/stale",
            TimeSpan.FromSeconds(5));
        second.WaitForElementNameContaining(
            "FohSerialText",
            "DEMO-FOH-0001",
            TimeSpan.FromSeconds(5));
        second.WaitForElementNameContaining(
            "FohSerialText",
            "OFFLINE NOT CONFIRMED",
            TimeSpan.FromSeconds(5));
        second.WaitForElementNameContaining(
            "StageSerialText",
            "DEMO-MON-0001",
            TimeSpan.FromSeconds(5));
        second.WaitForElementNameContaining(
            "FohIdentityText",
            "offline not confirmed",
            TimeSpan.FromSeconds(5));
        second.WaitForElementNameContaining(
            "StageCacheSummaryText",
            "offline/stale",
            TimeSpan.FromSeconds(5));
        AssertEx.False(
            second.FindById("FohLastDataText").Current.Name.Contains(
                "no ",
                StringComparison.OrdinalIgnoreCase));
        AssertEx.False(
            second.FindById("StageLastDataText").Current.Name.Contains(
                "no ",
                StringComparison.OrdinalIgnoreCase));
        AssertNoAppliedWrites(ReadAllLogs(context.DataDirectory));
        second.CloseAndWait();
    }

    private static void KeyboardShortcuts(UiScenarioContext context)
    {
        var app = context.Launch();
        CompleteConnectionTest(app);
        AssertEx.Equal(
            "Ctrl+1",
            app.FindById("NavigationStatus").Current.AcceleratorKey);
        AssertEx.Equal(
            "Ctrl+2",
            app.FindById("NavigationSync").Current.AcceleratorKey);
        AssertEx.Equal(
            "Ctrl+3",
            app.FindById("NavigationActivity").Current.AcceleratorKey);
        AssertEx.Equal(
            "Ctrl+4",
            app.FindById("NavigationSettings").Current.AcceleratorKey);
        app.Navigate("NavigationActivity", "Activity and diagnostics");
        app.Navigate("NavigationSettings", "Settings");
        app.Navigate("NavigationStatus", "Status");

        app.Invoke("StartButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Dry run active",
            TimeSpan.FromSeconds(10));
        AssertEx.Equal(
            "Ctrl+Shift+S",
            app.FindById("StopButton").Current.AcceleratorKey);
        app.Invoke("StopButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(10));
        app.CloseAndWait();
    }

    private static void Navigation(UiScenarioContext context)
    {
        var app = context.Launch();
        app.Navigate("NavigationSync", "Configure synchronization");
        _ = app.WaitForId("MappingGrid");
        app.Navigate("NavigationActivity", "Activity and diagnostics");
        _ = app.WaitForId("ActivityList");
        _ = app.WaitForId("ExportSupportButton");
        app.Navigate("NavigationSettings", "Settings");
        _ = app.WaitForId("DataDirectoryTextBox");
        _ = app.WaitForId("CopyDiagnosticsButton");
        app.Navigate("NavigationStatus", "Status");
        _ = app.WaitForId("FlowStatusText");
        app.CloseAndWait();
    }

    private static void MappingValidation(UiScenarioContext context)
    {
        var app = context.Launch();
        app.Navigate("NavigationSync", "Configure synchronization");
        app.Invoke("AutoMapButton");
        app.WaitFor(
            () => app.GetGridRowCount("MappingGrid") == 40,
            TimeSpan.FromSeconds(5),
            "40 input mappings");
        app.Invoke("AddAuxMappingButton");
        app.WaitFor(
            () => app.GetGridRowCount("MappingGrid") == 41,
            TimeSpan.FromSeconds(5),
            "one AUX mapping in addition to 40 inputs");

        app.SetGridCellText("MappingGrid", row: 0, column: 2, value: "abc");
        app.WaitForElementNameContaining(
            "ValidationSummaryText",
            "error",
            TimeSpan.FromSeconds(5));
        app.WaitForEnabled("StartButton", enabled: false);

        app.SetGridCellText("MappingGrid", row: 0, column: 2, value: "1");
        app.WaitForElementNameContaining(
            "ValidationSummaryText",
            "warning(s)",
            TimeSpan.FromSeconds(5));
        app.WaitForEnabled("StartButton", enabled: true);
        app.WaitForElementName("StartButton", "Test connection");
        app.CloseAndWait();
    }

    private static void ConfigurationPersistence(UiScenarioContext context)
    {
        var first = context.Launch();
        first.Navigate("NavigationSync", "Configure synchronization");
        first.Invoke("AutoMapButton");
        first.WaitFor(
            () => first.GetGridRowCount("MappingGrid") == 40,
            TimeSpan.FromSeconds(5),
            "40 input mappings");
        first.Invoke("AddAuxMappingButton");
        first.WaitFor(
            () => first.GetGridRowCount("MappingGrid") == 41,
            TimeSpan.FromSeconds(5),
            "the persisted AUX mapping");
        first.ToggleTo("Scope-Eq", ToggleState.Off);
        first.SelectComboItem("DirectionCombo", "Stage → FOH");
        first.Invoke("SaveConfigurationButton");
        first.WaitForElementNameContaining(
            "ValidationSummaryText",
            "Opgeslagen om",
            TimeSpan.FromSeconds(5));
        CompleteConnectionTest(first);
        first.Navigate("NavigationStatus", "Status");
        first.Invoke("StartButton");
        first.WaitForElementName(
            "OverallStatusText",
            "Dry run active",
            TimeSpan.FromSeconds(10));
        first.Invoke("StopButton");
        first.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(10));
        first.Navigate("NavigationSync", "Configure synchronization");
        first.ToggleTo("DryRunCheckBox", ToggleState.Off);
        first.ToggleTo("AllowHighRiskWritesCheckBox", ToggleState.On);
        first.Invoke("SaveConfigurationButton");
        first.WaitForElementNameContaining(
            "ValidationSummaryText",
            "only in this session",
            TimeSpan.FromSeconds(5));
        first.WaitForEnabled("SaveConfigurationButton", enabled: true);
        first.CloseAndWait();

        var configPath = Path.Combine(context.DataDirectory, "config.json");
        AssertEx.FileExists(configPath);
        AssertPersistedConfiguration(configPath);

        var second = context.Launch();
        second.Navigate("NavigationSync", "Configure synchronization");
        AssertEx.Equal(41, second.GetGridRowCount("MappingGrid"));
        AssertEx.Equal(ToggleState.Off, second.GetToggleState("Scope-Eq"));
        AssertEx.Equal(
            "Stage → FOH",
            second.GetSelectedComboItemName("DirectionCombo"));
        AssertEx.Equal(ToggleState.On, second.GetToggleState("DryRunCheckBox"));
        AssertEx.Equal(
            ToggleState.Off,
            second.GetToggleState("AllowHighRiskWritesCheckBox"));
        second.WaitForElementName("StartButton", "Test connection");
        second.CloseAndWait();
    }

    private static void DryRunLifecycle(UiScenarioContext context)
    {
        var app = context.Launch();
        CompleteConnectionTest(app);
        app.Navigate("NavigationStatus", "Status");
        app.Invoke("StartButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Dry run active",
            TimeSpan.FromSeconds(10));
        app.WaitForEnabled("StopButton", enabled: true);
        app.WaitForEnabled("StartButton", enabled: false);
        _ = app.FindTextByName("DRY RUN · NO WRITES");

        app.Navigate("NavigationSync", "Configure synchronization");
        foreach (var id in new[]
                 {
                     "FohIpTextBox",
                     "StageIpTextBox",
                     "DirectionCombo",
                     "DryRunCheckBox",
                     "Scope-Eq",
                     "AutoMapButton",
                     "AddMappingButton",
                     "AddAuxMappingButton",
                     "MappingGrid",
                     "SaveConfigurationButton",
                 })
        {
            app.WaitForEnabled(id, enabled: false);
        }

        app.Invoke("StopButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(10));
        app.WaitForEnabled("StartButton", enabled: true);
        app.Invoke("StartButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Dry run active",
            TimeSpan.FromSeconds(10));
        app.Invoke("StopButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(10));
        app.CloseAndWait();

        var logs = ReadAllLogs(context.DataDirectory);
        AssertEx.Contains("PARAMETER_PREVIEW", logs, StringComparison.OrdinalIgnoreCase);
        AssertNoAppliedWrites(logs);
    }

    private static void LivePreviewSafeNo(UiScenarioContext context)
    {
        var app = context.Launch();
        CompleteConnectionTest(app);
        app.Navigate("NavigationStatus", "Status");
        app.Invoke("StartButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Dry run active",
            TimeSpan.FromSeconds(10));
        app.Invoke("StopButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(10));
        app.WaitForElementNameContaining(
            "SetupProgressIndicator",
            "Setup voltooid",
            TimeSpan.FromSeconds(5));
        app.Navigate("NavigationSync", "Configure synchronization");
        app.ToggleTo("DryRunCheckBox", ToggleState.Off);
        app.WaitForElementName("StartButton", "Start live");
        app.Navigate("NavigationStatus", "Status");
        app.Invoke("StartButton");

        var dialog = app.WaitForDialog(
            "Confirm fresh live preview",
            TimeSpan.FromSeconds(12));
        app.AssertDialogOwnedByMainWindow(dialog);
        app.AssertDialogSafeDefaultNoContract(dialog);
        var prompt = app.CollectText(dialog);
        AssertEx.Contains("127.10.0.10", prompt);
        AssertEx.Contains("DEMO-FOH-0001", prompt);
        AssertEx.Contains("127.10.0.11", prompt);
        AssertEx.Contains("DEMO-MON-0001", prompt);
        AssertEx.Contains("Fresh differences", prompt);
        AssertEx.Contains("Executable after confirmation", prompt);
        AssertEx.Contains("Scopes:", prompt);
        AssertEx.Contains("CUST", prompt);
        AssertEx.Contains("Mappings:", prompt);
        _ = app.FindIn(dialog, "LiveDiffTotalCount");
        _ = app.FindIn(dialog, "LiveDiffExecutableCount");
        _ = app.FindIn(dialog, "LiveDiffBlockedCount");
        app.Expand(dialog, "LiveDiffTokenExpander");
        _ = app.FindIn(dialog, "LiveDiffTokenList");
        AssertEx.Contains("/ch/1/eq/1", app.CollectText(dialog));
        app.ClickDialogNo(dialog);
        app.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(12));
        app.CloseAndWait();

        AssertNoAppliedWrites(ReadAllLogs(context.DataDirectory));
    }

    private static void LiveApplyStatusAndStop(UiScenarioContext context)
    {
        var app = context.Launch();
        CompleteConnectionTest(app);
        app.Navigate("NavigationStatus", "Status");
        app.Invoke("StartButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Dry run active",
            TimeSpan.FromSeconds(10));
        app.Invoke("StopButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(10));

        app.Navigate("NavigationSync", "Configure synchronization");
        app.ToggleTo("DryRunCheckBox", ToggleState.Off);
        app.WaitForElementName("StartButton", "Start live");
        app.Navigate("NavigationStatus", "Status");
        app.Invoke("StartButton");
        var dialog = app.WaitForDialog(
            "Confirm fresh live preview",
            TimeSpan.FromSeconds(12));
        app.AssertDialogOwnedByMainWindow(dialog);
        app.AssertDialogSafeDefaultNoContract(dialog);
        app.ClickDialogYes(dialog);

        app.WaitFor(
            () =>
            {
                var runMode = app.TryFindById("RunModeText");
                var overall = app.TryFindById("OverallStatusText");
                var flow = app.TryFindById("FlowStatusText");
                var stop = app.TryFindById("StopButton");
                return
                    runMode?.Current.Name ==
                        "APPLYING LIVE · WRITES + READBACK" &&
                    overall?.Current.Name == "Live toepassen" &&
                    flow?.Current.Name == "WRITING + READING BACK" &&
                    stop?.Current.IsEnabled == true;
            },
            TimeSpan.FromSeconds(5),
            "all live-apply write/readback warnings to be visible together");

        app.Invoke("StopButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(12));
        app.WaitForEnabled("StartButton", enabled: true);
        app.CloseAndWait();
    }

    private static void HighRiskLiveReview(UiScenarioContext context)
    {
        var app = context.Launch();
        app.Navigate("NavigationSync", "Configure synchronization");
        app.ToggleTo("Scope-Mute", ToggleState.On);
        CompleteConnectionTest(app);
        app.Navigate("NavigationStatus", "Status");
        app.Invoke("StartButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Dry run active",
            TimeSpan.FromSeconds(10));
        app.Invoke("StopButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(10));

        app.Navigate("NavigationSync", "Configure synchronization");
        app.ToggleTo("DryRunCheckBox", ToggleState.Off);
        app.WaitForEnabled("AllowHighRiskWritesCheckBox", enabled: true);
        app.ToggleTo("AllowHighRiskWritesCheckBox", ToggleState.On);
        app.Invoke("StartButton");

        var dialog = app.WaitForDialog(
            "Confirm fresh live preview",
            TimeSpan.FromSeconds(12));
        app.AssertDialogOwnedByMainWindow(dialog);
        app.AssertDialogSafeDefaultNoContract(dialog);
        _ = app.FindIn(dialog, "LiveDiffHighRiskPanel");
        var approve = app.FindIn(dialog, "6");
        AssertEx.False(
            approve.Current.IsEnabled,
            "High-risk live approval must remain disabled until its separate acknowledgement.");
        app.ToggleTo(
            dialog,
            "LiveDiffHighRiskAcknowledgement",
            ToggleState.On);
        app.WaitFor(
            () => approve.Current.IsEnabled,
            TimeSpan.FromSeconds(3),
            "high-risk live approval to unlock after the explicit acknowledgement");
        app.ClickDialogNo(dialog);
        app.WaitForElementName(
            "OverallStatusText",
            "Not started",
            TimeSpan.FromSeconds(12));
        app.CloseAndWait();

        AssertNoAppliedWrites(ReadAllLogs(context.DataDirectory));
    }

    private static void CloseWhileRunning(UiScenarioContext context)
    {
        var app = context.Launch();
        CompleteConnectionTest(app);
        app.Navigate("NavigationStatus", "Status");
        app.Invoke("StartButton");
        app.WaitForElementName(
            "OverallStatusText",
            "Dry run active",
            TimeSpan.FromSeconds(10));

        app.RequestClose();
        var firstDialog = app.WaitForDialog("Close WingSync");
        app.AssertDialogDefaultNo(firstDialog);
        app.ClickDialogNo(firstDialog);
        app.WaitFor(
            () => !app.HasExited && app.MainWindow.Current.IsEnabled,
            TimeSpan.FromSeconds(5),
            "the window to remain active after No");

        app.RequestClose();
        var secondDialog = app.WaitForDialog("Close WingSync");
        app.ClickDialogYes(secondDialog);
        Thread.Sleep(30);
        app.RequestSecondCloseDuringDisposal();
        app.WaitForExit(TimeSpan.FromSeconds(15));
    }

    private static void SupportBundle(UiScenarioContext context)
    {
        var app = context.Launch();
        app.Navigate("NavigationSync", "Configure synchronization");
        app.Invoke("SaveConfigurationButton");
        app.WaitForElementNameContaining(
            "ValidationSummaryText",
            "Opgeslagen om",
            TimeSpan.FromSeconds(5));
        var logDirectory = Path.Combine(context.DataDirectory, "logs");
        app.WaitFor(
            () => Directory.Exists(logDirectory) &&
                  Directory.EnumerateFiles(logDirectory, "*.jsonl*").Any(),
            TimeSpan.FromSeconds(5),
            "the structured log to be persisted before export");
        app.Navigate("NavigationActivity", "Activity and diagnostics");
        app.Invoke("ExportSupportButton");
        var privacyDialog = app.WaitForDialog(
            "Create WingSync support bundle",
            TimeSpan.FromSeconds(10));
        app.AssertDialogDefaultNo(privacyDialog);
        var privacyCopy = app.CollectText(privacyDialog);
        AssertEx.Contains("IP addresses", privacyCopy);
        AssertEx.Contains("serial numbers", privacyCopy);
        AssertEx.Contains("WING parameter values", privacyCopy);
        app.ClickDialogYes(privacyDialog);
        var dialog = app.WaitForDialog(
            "WingSync support bundle",
            TimeSpan.FromSeconds(10));
        AssertEx.Contains(context.DataDirectory, app.CollectText(dialog));
        app.ClickDialogOk(dialog);

        var supportDirectory = Path.Combine(context.DataDirectory, "support");
        string? bundlePath = null;
        app.WaitFor(
            () =>
            {
                bundlePath = Directory.Exists(supportDirectory)
                    ? Directory.EnumerateFiles(
                            supportDirectory,
                            "WingSync-support-*.zip")
                        .SingleOrDefault()
                    : null;
                return bundlePath is not null;
            },
            TimeSpan.FromSeconds(5),
            "the support ZIP file");
        using (var archive = ZipFile.OpenRead(bundlePath!))
        {
            var entries = archive.Entries
                .Select(static entry => entry.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            AssertEx.True(entries.Contains("config.json"));
            AssertEx.True(entries.Contains("diagnostics.txt"));
            AssertEx.True(
                entries.Any(static entry => entry.StartsWith(
                    "logs/",
                    StringComparison.OrdinalIgnoreCase)),
                "The support bundle must contain at least one structured log.");
            var bundleText = string.Join(
                Environment.NewLine,
                archive.Entries.Select(ReadZipEntry));
            AssertEx.DoesNotContain(
                "127.10.0.10",
                bundleText,
                StringComparison.OrdinalIgnoreCase);
            AssertEx.DoesNotContain(
                "127.10.0.11",
                bundleText,
                StringComparison.OrdinalIgnoreCase);
            AssertEx.DoesNotContain(
                "DEMO-FOH-0001",
                bundleText,
                StringComparison.OrdinalIgnoreCase);
            AssertEx.DoesNotContain(
                "DEMO-MON-0001",
                bundleText,
                StringComparison.OrdinalIgnoreCase);
            AssertEx.Contains("[REDACTED:IP]", bundleText);
            AssertEx.Contains("[REDACTED:SERIAL]", bundleText);
        }

        app.CloseAndWait();
    }

    private static void MinimumSize(UiScenarioContext context)
    {
        var app = context.Launch();
        app.ResizeToMinimum();
        app.AssertVisibleWithinWindow("HeaderTitleText");
        app.AssertVisibleWithinWindow("StartButton");
        app.AssertVisibleWithinWindow("StopButton");

        app.Navigate("NavigationSync", "Configure synchronization");
        app.AssertVisibleWithinWindow("FohIpTextBox");
        app.AssertVisibleWithinWindow("AutoMapButton");
        app.AssertVisibleWithinWindow("MappingGrid");
        app.AssertVisibleWithinWindow("SaveConfigurationButton");

        app.Navigate("NavigationActivity", "Activity and diagnostics");
        app.AssertVisibleWithinWindow("ActivityFilterCombo");
        app.AssertVisibleWithinWindow("ExportSupportButton");
        app.AssertVisibleWithinWindow("ActivityList");

        app.Navigate("NavigationSettings", "Settings");
        app.AssertVisibleWithinWindow("DataDirectoryTextBox");
        app.AssertVisibleWithinWindow("RebuildCacheButton");
        app.AssertVisibleWithinWindow("CopyDiagnosticsButton");
        app.CloseAndWait();
    }

    private static void CompleteConnectionTest(UiAppSession app)
    {
        app.Navigate("NavigationSync", "Configure synchronization");
        app.Invoke("TestConnectionsButton");
        app.WaitForElementNameContaining(
            "DiscoveryStatusText",
            "WAPI gezond",
            TimeSpan.FromSeconds(12));
        app.WaitForEnabled("StartButton", enabled: true);
        app.Navigate("NavigationStatus", "Status");
        app.WaitForElementNameContaining(
            "SetupProgressIndicator",
            "3 van vier",
            TimeSpan.FromSeconds(5));
        app.Navigate("NavigationSync", "Configure synchronization");
    }

    private static void AssertPersistedConfiguration(string configPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var configuration = document.RootElement.GetProperty("configuration");
        AssertEx.Equal(
            "MonitorToFoh",
            configuration.GetProperty("direction").GetString());
        var scopes = configuration.GetProperty("scopes")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        AssertEx.False(
            scopes.Contains("Eq", StringComparer.Ordinal),
            "EQ scope was expected to remain disabled after save.");
        var channels = configuration.GetProperty("channels");
        AssertEx.Equal(
            40,
            channels.GetProperty("inputChannels").GetArrayLength());
        AssertEx.Equal(
            1,
            channels.GetProperty("auxChannels").GetArrayLength());
        var safety = configuration.GetProperty("safety");
        AssertEx.True(
            safety.GetProperty("dryRun").GetBoolean(),
            "Live arming must never persist across an application restart.");
        AssertEx.False(
            safety.GetProperty("allowHighRiskWrites").GetBoolean(),
            "High-risk approval must remain session-bound.");
    }

    private static string ReadAllLogs(string dataDirectory)
    {
        var logDirectory = Path.Combine(dataDirectory, "logs");
        return Directory.Exists(logDirectory)
            ? string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(logDirectory, "*.jsonl*")
                    .OrderBy(static path => path, StringComparer.Ordinal)
                    .Select(File.ReadAllText))
            : string.Empty;
    }

    private static string ReadZipEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertNoAppliedWrites(string logText)
    {
        AssertEx.DoesNotContain(
            "PARAMETER_APPLIED",
            logText,
            StringComparison.OrdinalIgnoreCase);
        AssertEx.DoesNotContain(
            "\"action\":\"applied\"",
            logText,
            StringComparison.OrdinalIgnoreCase);
    }
}
