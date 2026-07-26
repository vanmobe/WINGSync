using System.Collections.ObjectModel;
using System.Globalization;

namespace WingSync.App.ViewModels;

public sealed class LiveDiffReviewViewModel
{
    private readonly ReadOnlyCollection<LiveDiffScopeCountViewModel> scopeCounts;
    private readonly ReadOnlyCollection<LiveDiffTokenViewModel> tokens;

    public LiveDiffReviewViewModel(
        string sourceSummary,
        string targetSummary,
        string directionSummary,
        string enabledScopesSummary,
        string mappingSummary,
        int totalChanges,
        int executableChanges,
        int blockedChanges,
        DateTimeOffset generatedAt,
        bool highRiskArmed,
        IEnumerable<LiveDiffScopeCountViewModel> scopeCounts,
        IEnumerable<LiveDiffTokenViewModel> tokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(directionSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(enabledScopesSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingSummary);
        ArgumentNullException.ThrowIfNull(scopeCounts);
        ArgumentNullException.ThrowIfNull(tokens);

        SourceSummary = sourceSummary;
        TargetSummary = targetSummary;
        DirectionSummary = directionSummary;
        EnabledScopesSummary = enabledScopesSummary;
        MappingSummary = mappingSummary;
        TotalChanges = totalChanges;
        ExecutableChanges = executableChanges;
        BlockedChanges = blockedChanges;
        GeneratedAtSummary =
            $"Berekend om {generatedAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture)}";
        HighRiskArmed = highRiskArmed;
        this.scopeCounts = Array.AsReadOnly(scopeCounts.ToArray());
        this.tokens = Array.AsReadOnly(tokens.ToArray());
    }

    public string SourceSummary { get; }

    public string TargetSummary { get; }

    public string DirectionSummary { get; }

    public string EnabledScopesSummary { get; }

    public string MappingSummary { get; }

    public int TotalChanges { get; }

    public int ExecutableChanges { get; }

    public int BlockedChanges { get; }

    public string GeneratedAtSummary { get; }

    public bool HighRiskArmed { get; }

    public IReadOnlyList<LiveDiffScopeCountViewModel> ScopeCounts => scopeCounts;

    public IReadOnlyList<LiveDiffTokenViewModel> Tokens => tokens;

    public string TokenListHeader => $"Optionele tokenlijst ({TotalChanges})";

    public string TokenListSummary => tokens.Count == TotalChanges
        ? "Alle verse bron- en doeltokens; parameterwaarden worden hier niet getoond."
        : $"Toont {tokens.Count} van {TotalChanges} verse tokens; de volledige audittrail staat lokaal in Activiteit.";
}

public sealed record LiveDiffScopeCountViewModel(string ScopeName, int ChangeCount);

public sealed record LiveDiffTokenViewModel(
    string ScopeName,
    string SourceToken,
    string TargetToken,
    string Disposition);
