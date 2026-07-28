using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace WingSync.UiTests;

internal sealed class UiAppSession : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private readonly Process process;
    private bool disposed;

    private UiAppSession(Process process, AutomationElement mainWindow, string dataDirectory)
    {
        this.process = process;
        MainWindow = mainWindow;
        DataDirectory = dataDirectory;
    }

    public AutomationElement MainWindow { get; }

    public string DataDirectory { get; }

    public int ProcessId => process.Id;

    public bool HasExited
    {
        get
        {
            process.Refresh();
            return process.HasExited;
        }
    }

    public IntPtr WindowHandle =>
        new(MainWindow.Current.NativeWindowHandle);

    public static UiAppSession Start(
        string applicationPath,
        string dataDirectory,
        bool demoDiscoveryOffline = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = applicationPath,
            WorkingDirectory = Path.GetDirectoryName(applicationPath)
                ?? throw new InvalidOperationException("The application directory is unavailable."),
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--demo");
        if (demoDiscoveryOffline)
        {
            startInfo.ArgumentList.Add("--demo-offline");
        }

        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(dataDirectory);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start WingSync.");

        try
        {
            var mainWindow = WaitForTopLevelWindow(
                process,
                element =>
                    element.Current.AutomationId.Equals(
                        "MainWindow",
                        StringComparison.Ordinal),
                StartupTimeout,
                "the WingSync main window");
            var session = new UiAppSession(process, mainWindow, dataDirectory);
            session.WaitFor(
                () =>
                {
                    var startButton = session.TryFindById("StartButton");
                    var overallStatus = session.TryFindById("OverallStatusText");
                    return startButton is not null &&
                           startButton.Current.Name.Equals(
                               demoDiscoveryOffline ? "Setup required" : "Test connection",
                               StringComparison.Ordinal) &&
                           overallStatus is not null &&
                           overallStatus.Current.Name.Equals(
                               "Not started",
                               StringComparison.Ordinal);
                },
                StartupTimeout,
                "the demo view model to finish loading");
            return session;
        }
#pragma warning disable CA1031 // Ensure only the child process is reclaimed after any startup failure.
        catch
#pragma warning restore CA1031
        {
            StopOwnedProcess(process);
            process.Dispose();
            throw;
        }
    }

    public AutomationElement FindById(string automationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        return TryFindById(automationId)
            ?? throw new UiTestAssertionException(
                $"AutomationId '{automationId}' was not present in the current page.");
    }

    public AutomationElement WaitForId(
        string automationId,
        TimeSpan? timeout = null)
    {
        AutomationElement? result = null;
        WaitFor(
            () =>
            {
                result = TryFindById(automationId);
                return result is not null;
            },
            timeout ?? DefaultTimeout,
            $"AutomationId '{automationId}'");
        return result!;
    }

    public AutomationElement? TryFindById(string automationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        try
        {
            return MainWindow.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.AutomationIdProperty,
                    automationId));
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    public void AssertUniqueId(string automationId)
    {
        var matches = MainWindow.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(
                AutomationElement.AutomationIdProperty,
                automationId));
        AssertEx.Equal(
            1,
            matches.Count,
            $"AutomationId '{automationId}' must occur exactly once on its page.");
    }

    public void Navigate(string markerAutomationId, string expectedHeader)
    {
        var marker = FindById(markerAutomationId);
        var item = marker.Current.ControlType == ControlType.ListItem
            ? marker
            : FindAncestor(marker, ControlType.ListItem);
        if (item is null)
        {
            throw new UiTestAssertionException(
                $"'{markerAutomationId}' is not contained by a UIA ListItem.");
        }

        GetPattern<SelectionItemPattern>(item, SelectionItemPattern.Pattern).Select();
        WaitForElementName("HeaderTitleText", expectedHeader);
    }

    public void Invoke(string automationId)
    {
        var element = FindById(automationId);
        AssertEx.True(
            element.Current.IsEnabled,
            $"'{automationId}' must be enabled before invocation.");
        BringIntoView(element);
        GetPattern<InvokePattern>(element, InvokePattern.Pattern).Invoke();
    }

    public void ToggleTo(string automationId, ToggleState desiredState)
    {
        ToggleElementTo(FindById(automationId), automationId, desiredState);
    }

    public void ToggleTo(
        AutomationElement root,
        string automationId,
        ToggleState desiredState)
    {
        ToggleElementTo(FindIn(root, automationId), automationId, desiredState);
    }

    private static void ToggleElementTo(
        AutomationElement element,
        string automationId,
        ToggleState desiredState)
    {
        AssertEx.True(
            element.Current.IsEnabled,
            $"'{automationId}' must be enabled before toggling.");
        BringIntoView(element);
        var pattern = GetPattern<TogglePattern>(element, TogglePattern.Pattern);
        for (var attempt = 0;
             attempt < 3 && pattern.Current.ToggleState != desiredState;
             attempt++)
        {
            pattern.Toggle();
            Thread.Sleep(40);
        }

        AssertEx.Equal(
            desiredState,
            pattern.Current.ToggleState,
            $"'{automationId}' did not reach toggle state {desiredState}.");
    }

    public ToggleState GetToggleState(string automationId) =>
        GetPattern<TogglePattern>(
                FindById(automationId),
                TogglePattern.Pattern)
            .Current.ToggleState;

    public void SetText(string automationId, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var element = FindById(automationId);
        AssertEx.True(element.Current.IsEnabled, $"'{automationId}' is disabled.");
        BringIntoView(element);
        GetPattern<ValuePattern>(element, ValuePattern.Pattern).SetValue(value);
    }

    public string GetTextValue(string automationId) =>
        GetPattern<ValuePattern>(
                FindById(automationId),
                ValuePattern.Pattern)
            .Current.Value;

    public void SelectComboItem(string automationId, string itemName)
    {
        var combo = FindById(automationId);
        AssertEx.True(combo.Current.IsEnabled, $"'{automationId}' is disabled.");
        BringIntoView(combo);
        var expand = GetPattern<ExpandCollapsePattern>(
            combo,
            ExpandCollapsePattern.Pattern);
        expand.Expand();

        AutomationElement? item = null;
        WaitFor(
            () =>
            {
                item = combo.FindFirst(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(
                            AutomationElement.ControlTypeProperty,
                            ControlType.ListItem),
                        new PropertyCondition(
                            AutomationElement.NameProperty,
                            itemName)));
                return item is not null;
            },
            DefaultTimeout,
            $"combo item '{itemName}'");
        GetPattern<SelectionItemPattern>(item!, SelectionItemPattern.Pattern).Select();
        if (expand.Current.ExpandCollapseState != ExpandCollapseState.Collapsed)
        {
            expand.Collapse();
        }

        WaitFor(
            () => GetSelectedComboItemName(combo).Equals(
                itemName,
                StringComparison.Ordinal),
            DefaultTimeout,
            $"combo selection '{itemName}'");
    }

    public string GetSelectedComboItemName(string automationId) =>
        GetSelectedComboItemName(FindById(automationId));

    public int GetGridRowCount(string automationId) =>
        GetPattern<GridPattern>(
                FindById(automationId),
                GridPattern.Pattern)
            .Current.RowCount;

    public void SetGridCellText(
        string gridAutomationId,
        int row,
        int column,
        string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var grid = FindById(gridAutomationId);
        BringIntoView(grid);
        var gridPattern = GetPattern<GridPattern>(grid, GridPattern.Pattern);
        var cell = gridPattern.GetItem(row, column);
        BringIntoView(cell);
        TrySelectContainingRow(cell);
        cell.SetFocus();
        NativeInput.PressF2(WindowHandle);

        AutomationElement? editor = null;
        WaitFor(
            () =>
            {
                editor = cell.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.Edit));
                if (editor is not null)
                {
                    return true;
                }

                var focused = AutomationElement.FocusedElement;
                return focused is not null &&
                       focused.Current.ProcessId == ProcessId &&
                       focused.Current.ControlType == ControlType.Edit &&
                       IsDescendantOf(focused, cell) &&
                       (editor = focused) is not null;
            },
            TimeSpan.FromSeconds(2),
            $"editing grid row {row}, column {column}",
            throwOnTimeout: false);

        if (editor is null)
        {
            var clickablePoint = GetClickablePoint(cell);
            NativeInput.DoubleClick(clickablePoint, WindowHandle);
            editor = WaitForDescendant(
                cell,
                ControlType.Edit,
                TimeSpan.FromSeconds(3),
                $"the editor for grid row {row}, column {column}");
        }

        GetPattern<ValuePattern>(editor, ValuePattern.Pattern).SetValue(value);
        NativeInput.PressTab(WindowHandle);
        Thread.Sleep(80);
    }

    public void WaitForElementName(
        string automationId,
        string expectedName,
        TimeSpan? timeout = null) =>
        WaitFor(
            () =>
            {
                var element = TryFindById(automationId);
                return element is not null &&
                       element.Current.Name.Equals(
                           expectedName,
                           StringComparison.Ordinal);
            },
            timeout ?? DefaultTimeout,
            $"'{automationId}' to have name '{expectedName}'");

    public void WaitForElementNameContaining(
        string automationId,
        string expectedSubstring,
        TimeSpan? timeout = null) =>
        WaitFor(
            () =>
            {
                var element = TryFindById(automationId);
                return element is not null &&
                       element.Current.Name.Contains(
                           expectedSubstring,
                           StringComparison.Ordinal);
            },
            timeout ?? DefaultTimeout,
            $"'{automationId}' to contain '{expectedSubstring}'");

    public void WaitForEnabled(
        string automationId,
        bool enabled,
        TimeSpan? timeout = null) =>
        WaitFor(
            () =>
            {
                var element = TryFindById(automationId);
                return element is not null && element.Current.IsEnabled == enabled;
            },
            timeout ?? DefaultTimeout,
            $"'{automationId}' enabled={enabled}");

    public AutomationElement WaitForDialog(
        string title,
        TimeSpan? timeout = null)
    {
        AutomationElement? dialog = null;
        WaitFor(
            () =>
            {
                dialog = FindTopLevelWindow(title);
                return dialog is not null;
            },
            timeout ?? DefaultTimeout,
            $"dialog '{title}'");
        return dialog!;
    }

    public void AssertDialogOwnedByMainWindow(AutomationElement dialog)
    {
        AssertEx.Equal(ProcessId, dialog.Current.ProcessId);
        var owned = MainWindow.FindFirst(
            TreeScope.Children,
            new PropertyCondition(
                AutomationElement.NativeWindowHandleProperty,
                dialog.Current.NativeWindowHandle));
        AssertEx.True(
            owned is not null,
            $"Dialog '{dialog.Current.Name}' is not exposed as an owned child of MainWindow.");
        AssertEx.False(
            IsWindowEnabled(WindowHandle),
            "The owner must remain disabled while the modal live-diff review is open.");
    }

    public AutomationElement FindIn(
        AutomationElement root,
        string automationId)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        AssertEx.Equal(ProcessId, root.Current.ProcessId);
        var element = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(
                AutomationElement.AutomationIdProperty,
                automationId));
        return element ??
            throw new UiTestAssertionException(
                $"Element '{automationId}' was not found below '{root.Current.Name}'.");
    }

    public void Expand(AutomationElement root, string automationId)
    {
        var element = FindIn(root, automationId);
        var pattern = GetPattern<ExpandCollapsePattern>(
            element,
            ExpandCollapsePattern.Pattern);
        pattern.Expand();
        WaitFor(
            () => pattern.Current.ExpandCollapseState == ExpandCollapseState.Expanded,
            TimeSpan.FromSeconds(3),
            $"'{automationId}' to expand");
    }

    public void AssertDialogDefaultNo(AutomationElement dialog)
    {
        AssertEx.Equal(ProcessId, dialog.Current.ProcessId);
        var noButton = FindDialogButton(dialog, "7", ["Nee", "No"]);
        AssertEx.True(
            noButton.Current.IsEnabled && noButton.Current.IsKeyboardFocusable,
            "The safe No action must be enabled and keyboard reachable.");
        var defaultIdentifier = GetNativeDialogDefaultIdentifier(
            new IntPtr(dialog.Current.NativeWindowHandle));
        AssertEx.Equal(
            7,
            defaultIdentifier,
            "The native dialog default action must be No (IDNO=7).");
    }

    public void AssertDialogSafeDefaultNoContract(AutomationElement dialog)
    {
        var noButton = FindDialogButton(dialog, "7", ["Nee", "No"]);
        AssertEx.True(
            noButton.Current.IsEnabled && noButton.Current.IsKeyboardFocusable,
            "The safe No action must be enabled and keyboard reachable.");
        AssertEx.Contains(
            "Safe default choice",
            noButton.Current.HelpText,
            StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(
            "Default choice: cancel",
            CollectText(dialog),
            StringComparison.OrdinalIgnoreCase);
    }

    public void ClickDialogNo(AutomationElement dialog)
    {
        AssertEx.Equal(ProcessId, dialog.Current.ProcessId);
        InvokeDialogButton(dialog, 7, ["Nee", "No"]);
    }

    public void ClickDialogYes(AutomationElement dialog)
    {
        AssertEx.Equal(ProcessId, dialog.Current.ProcessId);
        InvokeDialogButton(dialog, 6, ["Ja", "Yes"]);
    }

    public void ClickDialogOk(AutomationElement dialog)
    {
        AssertEx.Equal(ProcessId, dialog.Current.ProcessId);
        InvokeDialogButton(dialog, 1, ["OK"]);
    }

    public string CollectText(AutomationElement root)
    {
        AssertEx.Equal(ProcessId, root.Current.ProcessId);
        var builder = new StringBuilder();
        var elements = root.FindAll(
            TreeScope.Descendants,
            System.Windows.Automation.Condition.TrueCondition);
        foreach (AutomationElement element in elements)
        {
            if (element.Current.ControlType == ControlType.Text ||
                element.Current.ControlType == ControlType.Document)
            {
                var name = element.Current.Name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    builder.AppendLine(name);
                }
            }
        }

        return builder.ToString();
    }

    public AutomationElement FindTextByName(string text)
    {
        return MainWindow.FindFirst(
                   TreeScope.Descendants,
                   new AndCondition(
                       new PropertyCondition(
                           AutomationElement.ControlTypeProperty,
                           ControlType.Text),
                       new PropertyCondition(
                           AutomationElement.NameProperty,
                           text)))
               ?? throw new UiTestAssertionException(
                   $"Text element '{text}' was not found.");
    }

    public void RequestClose()
    {
        if (HasExited)
        {
            return;
        }

        const uint closeMessage = 0x0010;
        _ = PostMessage(
            WindowHandle,
            closeMessage,
            UIntPtr.Zero,
            IntPtr.Zero);
    }

    public void RequestSecondCloseDuringDisposal()
    {
        if (HasExited)
        {
            return;
        }

        try
        {
            RequestClose();
        }
        catch (ElementNotAvailableException)
        {
            // A clean first close may win the race. That is acceptable.
        }
        catch (InvalidOperationException) when (HasExited)
        {
            // The provider disappeared between HasExited and the second request.
        }
    }

    public void CloseAndWait(TimeSpan? timeout = null)
    {
        if (!HasExited)
        {
            RequestClose();
        }

        WaitForExit(timeout ?? TimeSpan.FromSeconds(15));
    }

    public void WaitForExit(TimeSpan timeout)
    {
        var milliseconds = checked((int)Math.Ceiling(timeout.TotalMilliseconds));
        if (!process.WaitForExit(milliseconds))
        {
            throw new UiTestAssertionException(
                $"WingSync process {ProcessId} did not exit within {timeout.TotalSeconds:F0}s.");
        }
    }

    public void ResizeToMinimum()
    {
        var window = GetPattern<WindowPattern>(MainWindow, WindowPattern.Pattern);
        if (window.Current.WindowVisualState != WindowVisualState.Normal)
        {
            window.SetWindowVisualState(WindowVisualState.Normal);
        }

        var transform = GetPattern<TransformPattern>(
            MainWindow,
            TransformPattern.Pattern);
        AssertEx.True(transform.Current.CanResize, "The main window must be resizable.");
        transform.Resize(860, 520);
        Thread.Sleep(250);
    }

    public void ScrollIntoView(string automationId)
    {
        BringIntoView(FindById(automationId));
        Thread.Sleep(250);
    }

    public void ScrollToTop(string automationId)
    {
        var scroll = GetPattern<ScrollPattern>(
            FindById(automationId),
            ScrollPattern.Pattern);
        if (scroll.Current.VerticallyScrollable)
        {
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            Thread.Sleep(250);
        }
    }

    public void AssertVisibleWithinWindow(string automationId)
    {
        var element = FindById(automationId);
        BringIntoView(element);
        if (element.Current.IsEnabled && element.Current.IsKeyboardFocusable)
        {
            element.SetFocus();
            Thread.Sleep(50);
        }

        var windowBounds = MainWindow.Current.BoundingRectangle;
        var elementBounds = element.Current.BoundingRectangle;
        AssertEx.True(
            elementBounds.Width > 0 && elementBounds.Height > 0,
            $"'{automationId}' has an empty bounding rectangle.");
        AssertEx.False(
            element.Current.IsOffscreen,
            $"'{automationId}' is still off-screen after BringIntoView/focus.");
        AssertEx.True(
            elementBounds.IntersectsWith(windowBounds),
            $"'{automationId}' does not intersect the minimum-size window.");
    }

    public void WaitFor(
        Func<bool> condition,
        TimeSpan timeout,
        string description,
        bool throwOnTimeout = true)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var stopwatch = Stopwatch.StartNew();
        Exception? lastTransient = null;
        while (stopwatch.Elapsed < timeout)
        {
            if (HasExited)
            {
                throw new UiTestAssertionException(
                    $"WingSync exited with code {process.ExitCode} while waiting for {description}.");
            }

            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (ElementNotAvailableException exception)
            {
                lastTransient = exception;
            }
            catch (COMException exception)
            {
                lastTransient = exception;
            }
            catch (InvalidOperationException exception)
            {
                lastTransient = exception;
            }

            Thread.Sleep(50);
        }

        if (throwOnTimeout)
        {
            throw new UiTestAssertionException(
                lastTransient is null
                    ? $"Timed out after {timeout.TotalSeconds:F1}s waiting for {description}."
                    : $"Timed out waiting for {description}. Last UIA error: "
                      + lastTransient.Message);
        }
    }

    public void CaptureFailureArtifacts(string directory, string prefix)
    {
        if (HasExited)
        {
            File.WriteAllText(
                Path.Combine(directory, $"{prefix}-process-exited.txt"),
                $"Process {ProcessId} exited with code {process.ExitCode}.",
                Encoding.UTF8);
            return;
        }

#pragma warning disable CA1031 // Diagnostics are best-effort and must not hide the test failure.
        try
        {
            CaptureScreenshot(Path.Combine(directory, $"{prefix}-failure.png"));
        }
        catch (Exception exception)
        {
            File.WriteAllText(
                Path.Combine(directory, $"{prefix}-screenshot-error.txt"),
                exception.ToString(),
                Encoding.UTF8);
        }

        try
        {
            using var writer = new StreamWriter(
                Path.Combine(directory, $"{prefix}-uia-tree.txt"),
                append: false,
                Encoding.UTF8);
            var remaining = 2_000;
            DumpTree(MainWindow, writer, depth: 0, ref remaining);
        }
        catch (Exception exception)
        {
            File.WriteAllText(
                Path.Combine(directory, $"{prefix}-tree-error.txt"),
                exception.ToString(),
                Encoding.UTF8);
        }
#pragma warning restore CA1031
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StopOwnedProcess(process);
        process.Dispose();
    }

    private static AutomationElement WaitForTopLevelWindow(
        Process process,
        Func<AutomationElement, bool> predicate,
        TimeSpan timeout,
        string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new UiTestAssertionException(
                    $"WingSync exited with code {process.ExitCode} before showing {description}.");
            }

            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(
                    AutomationElement.ProcessIdProperty,
                    process.Id));
            foreach (AutomationElement window in windows)
            {
                try
                {
                    if (predicate(window))
                    {
                        return window;
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // Retry while the native window is still being constructed.
                }
            }

            Thread.Sleep(50);
        }

        throw new UiTestAssertionException(
            $"Timed out after {timeout.TotalSeconds:F0}s waiting for {description}.");
    }

    private AutomationElement? FindTopLevelWindow(string title)
    {
        var ownedWindow = MainWindow.FindFirst(
            TreeScope.Children,
            new AndCondition(
                new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.Window),
                new PropertyCondition(
                    AutomationElement.NameProperty,
                    title)));
        if (ownedWindow is not null)
        {
            return ownedWindow;
        }

        var windows = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            new AndCondition(
                new PropertyCondition(
                    AutomationElement.ProcessIdProperty,
                    ProcessId),
                new PropertyCondition(
                    AutomationElement.NameProperty,
                    title)));
        return windows.Count == 0 ? null : windows[0];
    }

    private static AutomationElement FindDialogButton(
        AutomationElement dialog,
        string automationId,
        IReadOnlyList<string> fallbackNames)
    {
        var button = dialog.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.Button),
                new PropertyCondition(
                    AutomationElement.AutomationIdProperty,
                    automationId)));
        if (button is not null)
        {
            return button;
        }

        var buttons = dialog.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.Button));
        foreach (AutomationElement candidate in buttons)
        {
            if (fallbackNames.Any(name =>
                    candidate.Current.Name.Equals(
                        name,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        throw new UiTestAssertionException(
            $"Dialog '{dialog.Current.Name}' has no expected button "
            + $"(AutomationId {automationId}).");
    }

    private static string GetSelectedComboItemName(AutomationElement combo)
    {
        var selection = GetPattern<SelectionPattern>(
                combo,
                SelectionPattern.Pattern)
            .Current.GetSelection();
        return selection.Length == 0 ? string.Empty : selection[0].Current.Name;
    }

    private static AutomationElement? FindAncestor(
        AutomationElement element,
        ControlType controlType)
    {
        var walker = TreeWalker.RawViewWalker;
        var current = walker.GetParent(element);
        while (current is not null)
        {
            if (current.Current.ControlType == controlType)
            {
                return current;
            }

            current = walker.GetParent(current);
        }

        return null;
    }

    private static bool IsDescendantOf(
        AutomationElement element,
        AutomationElement expectedAncestor)
    {
        var walker = TreeWalker.RawViewWalker;
        var current = element;
        while (current is not null)
        {
            if (Automation.Compare(current, expectedAncestor))
            {
                return true;
            }

            current = walker.GetParent(current);
        }

        return false;
    }

    private static void TrySelectContainingRow(AutomationElement cell)
    {
        var row = FindAncestor(cell, ControlType.DataItem);
        if (row is not null &&
            row.TryGetCurrentPattern(
                SelectionItemPattern.Pattern,
                out var rawPattern))
        {
            ((SelectionItemPattern)rawPattern).Select();
        }
    }

    private static AutomationElement WaitForDescendant(
        AutomationElement root,
        ControlType controlType,
        TimeSpan timeout,
        string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var result = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    controlType));
            if (result is not null)
            {
                return result;
            }

            Thread.Sleep(40);
        }

        throw new UiTestAssertionException(
            $"Timed out waiting for {description}.");
    }

    private static System.Windows.Point GetClickablePoint(AutomationElement element)
    {
        if (element.TryGetClickablePoint(out var point))
        {
            return point;
        }

        var bounds = element.Current.BoundingRectangle;
        return new System.Windows.Point(
            bounds.Left + (bounds.Width / 2),
            bounds.Top + (bounds.Height / 2));
    }

    private static void BringIntoView(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(
                ScrollItemPattern.Pattern,
                out var rawPattern))
        {
            ((ScrollItemPattern)rawPattern).ScrollIntoView();
            Thread.Sleep(40);
        }
    }

    private static TPattern GetPattern<TPattern>(
        AutomationElement element,
        AutomationPattern pattern)
        where TPattern : class
    {
        if (!element.TryGetCurrentPattern(pattern, out var rawPattern) ||
            rawPattern is not TPattern typedPattern)
        {
            throw new UiTestAssertionException(
                $"Element '{element.Current.AutomationId}'/'{element.Current.Name}' "
                + $"does not expose {typeof(TPattern).Name}.");
        }

        return typedPattern;
    }

    private static void StopOwnedProcess(Process process)
    {
        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(5_000);
        }
#pragma warning disable CA1031 // Teardown must not obscure the original test result.
        catch
#pragma warning restore CA1031
        {
            // The process may have exited between checks.
        }
    }

    public void CaptureScreenshot(
        string path,
        AutomationElement? overlay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bounds = MainWindow.Current.BoundingRectangle;
        using var bitmap = CaptureWindow(MainWindow);
        if (overlay is not null)
        {
            using var overlayBitmap = CaptureWindow(overlay);
            var overlayBounds = overlay.Current.BoundingRectangle;
            using var graphics = Graphics.FromImage(bitmap);
            graphics.DrawImageUnscaled(
                overlayBitmap,
                checked((int)Math.Round(overlayBounds.Left - bounds.Left)),
                checked((int)Math.Round(overlayBounds.Top - bounds.Top)));
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private static Bitmap CaptureWindow(AutomationElement window)
    {
        var bounds = window.Current.BoundingRectangle;
        var width = Math.Max(1, checked((int)Math.Ceiling(bounds.Width)));
        var height = Math.Max(1, checked((int)Math.Ceiling(bounds.Height)));
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var deviceContext = graphics.GetHdc();
        try
        {
            const uint renderFullContent = 2;
            if (!PrintWindow(
                    new IntPtr(window.Current.NativeWindowHandle),
                    deviceContext,
                    renderFullContent))
            {
                throw new InvalidOperationException(
                    $"Windows could not render '{window.Current.Name}' for capture.");
            }
        }
        finally
        {
            graphics.ReleaseHdc(deviceContext);
        }

        return bitmap;
    }

    private static void DumpTree(
        AutomationElement element,
        TextWriter writer,
        int depth,
        ref int remaining)
    {
        if (remaining-- <= 0)
        {
            return;
        }

        var current = element.Current;
        writer.Write(new string(' ', depth * 2));
        writer.Write(current.ControlType?.ProgrammaticName ?? "<no type>");
        writer.Write(" id=");
        writer.Write(Escape(current.AutomationId));
        writer.Write(" name=");
        writer.Write(Escape(current.Name));
        writer.Write(" enabled=");
        writer.Write(current.IsEnabled);
        writer.Write(" offscreen=");
        writer.Write(current.IsOffscreen);
        writer.Write(" bounds=");
        writer.WriteLine(current.BoundingRectangle.ToString(CultureInfo.InvariantCulture));
        if (depth >= 20)
        {
            return;
        }

        var children = element.FindAll(
            TreeScope.Children,
            System.Windows.Automation.Condition.TrueCondition);
        foreach (AutomationElement child in children)
        {
            DumpTree(child, writer, depth + 1, ref remaining);
            if (remaining <= 0)
            {
                break;
            }
        }
    }

    private static string Escape(string value) =>
        string.IsNullOrEmpty(value)
            ? "\"\""
            : "\"" + value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool IsWindowEnabled(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "PrintWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool PrintWindow(
        IntPtr windowHandle,
        IntPtr deviceContext,
        uint flags);

    private static int GetNativeDialogDefaultIdentifier(IntPtr windowHandle)
    {
        const uint getDefaultIdentifierMessage = 0x0400;
        const int hasDefaultIdentifier = 0x534B;
        var result = SendMessage(
            windowHandle,
            getDefaultIdentifierMessage,
            UIntPtr.Zero,
            IntPtr.Zero).ToInt64();
        var marker = (int)((result >> 16) & 0xFFFF);
        return marker == hasDefaultIdentifier
            ? (int)(result & 0xFFFF)
            : 0;
    }

    private static void InvokeDialogButton(
        AutomationElement dialog,
        int identifier,
        IReadOnlyList<string> names)
    {
        var button = FindDialogButton(
            dialog,
            identifier.ToString(CultureInfo.InvariantCulture),
            names);
        var nativeIdentifier = int.TryParse(
            button.Current.AutomationId,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedIdentifier)
                ? parsedIdentifier
                : identifier;
        try
        {
            GetPattern<InvokePattern>(button, InvokePattern.Pattern).Invoke();
        }
        catch (InvalidOperationException)
        {
            const uint commandMessage = 0x0111;
            _ = SendMessage(
                new IntPtr(dialog.Current.NativeWindowHandle),
                commandMessage,
                new UIntPtr((uint)nativeIdentifier),
                IntPtr.Zero);
        }
        catch (ExternalException)
        {
            const uint commandMessage = 0x0111;
            _ = SendMessage(
                new IntPtr(dialog.Current.NativeWindowHandle),
                commandMessage,
                new UIntPtr((uint)nativeIdentifier),
                IntPtr.Zero);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);
}
