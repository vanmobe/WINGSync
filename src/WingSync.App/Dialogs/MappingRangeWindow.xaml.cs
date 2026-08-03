using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using WingSync.Core.Domain;

namespace WingSync.App.Dialogs;

public partial class MappingRangeWindow : Window
{
    public MappingRangeWindow()
    {
        InitializeComponent();
        RangeRows = [new MappingRangeRow("1", "40", "1")];
        DataContext = this;
    }

    public ObservableCollection<MappingRangeRow> RangeRows { get; }
    public MappingRangeRow? SelectedRangeRow { get; set; }
    public IReadOnlyList<MappingRange> Ranges { get; private set; } = [];

    private void OnAddRange(object sender, RoutedEventArgs e)
    {
        var next = RangeRows.Count == 0
            ? 1
            : RangeRows.Select(row => ParseOrZero(row.SourceThrough)).Max() + 1;
        next = Math.Min(next, WingChannelLimits.LastInput);
        var row = new MappingRangeRow(next.ToString(CultureInfo.InvariantCulture),
            next.ToString(CultureInfo.InvariantCulture), next.ToString(CultureInfo.InvariantCulture));
        RangeRows.Add(row);
        SelectedRangeRow = row;
        RangesGrid.SelectedItem = row;
        RangesGrid.ScrollIntoView(row);
    }

    private void OnRemoveRange(object sender, RoutedEventArgs e)
    {
        if (RangesGrid.SelectedItem is MappingRangeRow row)
        {
            RangeRows.Remove(row);
        }
    }

    private void OnFill(object sender, RoutedEventArgs e)
    {
        RangesGrid.CommitEdit();
        if (RangeRows.Count == 0)
        {
            ValidationMessage.Text = "Add at least one range.";
            return;
        }

        var parsed = new List<MappingRange>(RangeRows.Count);
        var sources = new HashSet<int>();
        var targets = new HashSet<int>();
        foreach (var row in RangeRows)
        {
            if (!TryRead(row.SourceFrom, out var sourceFrom) ||
                !TryRead(row.SourceThrough, out var sourceThrough) ||
                !TryRead(row.TargetFrom, out var targetFrom) || sourceFrom > sourceThrough)
            {
                ValidationMessage.Text = "Every row needs a valid ascending range from 1 through 40.";
                return;
            }

            var targetThrough = targetFrom + sourceThrough - sourceFrom;
            if (targetThrough > WingChannelLimits.LastInput)
            {
                ValidationMessage.Text = $"A target range would end at {targetThrough}; the maximum is 40.";
                return;
            }

            for (var offset = 0; offset <= sourceThrough - sourceFrom; offset++)
            {
                if (!sources.Add(sourceFrom + offset) || !targets.Add(targetFrom + offset))
                {
                    ValidationMessage.Text = "Source and target ranges must not overlap.";
                    return;
                }
            }
            parsed.Add(new MappingRange(sourceFrom, sourceThrough, targetFrom));
        }

        Ranges = parsed;
        DialogResult = true;
    }

    private static int ParseOrZero(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static bool TryRead(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
        value is >= WingChannelLimits.FirstInput and <= WingChannelLimits.LastInput;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class MappingRangeRow(string sourceFrom, string sourceThrough, string targetFrom)
{
    public string SourceFrom { get; set; } = sourceFrom;
    public string SourceThrough { get; set; } = sourceThrough;
    public string TargetFrom { get; set; } = targetFrom;
}

public sealed record MappingRange(int SourceFrom, int SourceThrough, int TargetFrom);
