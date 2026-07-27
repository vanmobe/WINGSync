using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WingSync.Core.Abstractions;
using WingSync.Core.Domain;

namespace WingSync.App.ViewModels;

public sealed class DiscoveredWingViewModel
{
    public DiscoveredWingViewModel(DiscoveredWing wing)
    {
        Wing = wing ?? throw new ArgumentNullException(nameof(wing));
    }

    public DiscoveredWing Wing { get; }

    public string DisplayName =>
        $"{Wing.Name} · {Wing.IpAddress} · {Wing.Model} · FW {Wing.FirmwareVersion} · S/N {Wing.SerialNumber}";
}

public sealed class ScopeSelectionViewModel : ObservableObject
{
    private readonly SyncScope[] scopes;
    private bool isSelected;
    private bool isSupported = true;

    public ScopeSelectionViewModel(
        SyncScope scope,
        string displayName,
        string description,
        bool isHighRisk,
        bool selected)
        : this(
            [scope],
            displayName,
            description,
            isHighRisk,
            selected,
            $"Scope-{scope}")
    {
    }

    public ScopeSelectionViewModel(
        IReadOnlyList<SyncScope> scopes,
        string displayName,
        string description,
        bool isHighRisk,
        bool selected,
        string automationId)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count == 0)
        {
            throw new ArgumentException("A scope selection must contain at least one scope.", nameof(scopes));
        }

        this.scopes = scopes.Distinct().ToArray();
        Scope = this.scopes[0];
        DisplayName = displayName;
        Description = description;
        IsHighRisk = isHighRisk;
        AutomationId = automationId;
        isSelected = selected;
    }

    public SyncScope Scope { get; }

    public IReadOnlyList<SyncScope> Scopes => scopes;

    public string AutomationId { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public bool IsHighRisk { get; }

    public string RiskLabel => IsHighRisk ? "HIGH RISK" : "STANDARD";

    public Brush RiskBrush => IsHighRisk
        ? new SolidColorBrush(Color.FromRgb(240, 179, 90))
        : new SolidColorBrush(Color.FromRgb(67, 193, 141));

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    public bool IsSupported
    {
        get => isSupported;
        set => SetProperty(ref isSupported, value);
    }
}

public sealed class ChannelMappingViewModel : ObservableObject
{
    private bool isEnabled = true;
    private bool isAux;
    private string sourceChannelText;
    private string targetChannelText;
    private string label = string.Empty;
    private string validationText = "OK";

    public ChannelMappingViewModel(
        int sourceChannel,
        int targetChannel,
        string? label = null,
        bool isAux = false)
    {
        sourceChannelText = sourceChannel.ToString(CultureInfo.InvariantCulture);
        targetChannelText = targetChannel.ToString(CultureInfo.InvariantCulture);
        this.label = label ?? string.Empty;
        this.isAux = isAux;
    }

    public bool IsEnabled
    {
        get => isEnabled;
        set => SetProperty(ref isEnabled, value);
    }

    public bool IsAux
    {
        get => isAux;
        set
        {
            if (SetProperty(ref isAux, value))
            {
                OnPropertyChanged(nameof(ChannelType));
            }
        }
    }

    public string ChannelType => IsAux ? "AUX" : "INPUT";

    public string SourceChannelText
    {
        get => sourceChannelText;
        set => SetProperty(ref sourceChannelText, value ?? string.Empty);
    }

    public string TargetChannelText
    {
        get => targetChannelText;
        set => SetProperty(ref targetChannelText, value ?? string.Empty);
    }

    public int SourceChannel =>
        int.TryParse(
            SourceChannelText,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var channel)
            ? channel
            : 0;

    public int TargetChannel =>
        int.TryParse(
            TargetChannelText,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var channel)
            ? channel
            : 0;

    public string Label
    {
        get => label;
        set => SetProperty(ref label, value ?? string.Empty);
    }

    public string ValidationText
    {
        get => validationText;
        set => SetProperty(ref validationText, value);
    }
}

public sealed record DirectionOption(string DisplayName, SyncDirection Direction)
{
    public override string ToString() => DisplayName;
}

public sealed record WorkflowStepViewModel(
    string Name,
    string Marker,
    string Status,
    Brush Background,
    Brush Foreground,
    Brush Connector,
    Visibility ConnectorVisibility,
    string AutomationId);

public sealed record ActivityItemViewModel(
    DateTimeOffset Timestamp,
    DiagnosticSeverity Severity,
    string Source,
    string Message,
    string Details)
{
    public string TimeText =>
        Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public string Level => Severity switch
    {
        DiagnosticSeverity.Trace => "Trace",
        DiagnosticSeverity.Information => "Info",
        DiagnosticSeverity.Warning => "Warning",
        DiagnosticSeverity.Error => "Error",
        DiagnosticSeverity.Critical => "Critical",
        _ => Severity.ToString(),
    };
}
