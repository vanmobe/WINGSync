using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WingSync.App.ViewModels;

namespace WingSync.App.Dialogs;

public partial class LiveDiffReviewWindow : Window
{
    private readonly bool highRiskArmed;

    public bool StartFromCurrentBaseline { get; private set; }

    public LiveDiffReviewWindow(LiveDiffReviewViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        ConstrainToWorkArea();
        DataContext = viewModel;
        highRiskArmed = viewModel.HighRiskArmed;
        HighRiskPanel.Visibility = highRiskArmed ? Visibility.Visible : Visibility.Collapsed;
        ApproveButton.IsEnabled = !highRiskArmed;
        StartFromNowButton.IsEnabled = !highRiskArmed;
        ContentRendered += (_, _) =>
        {
            _ = Activate();
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    _ = CancelButton.Focus();
                    _ = Keyboard.Focus(CancelButton);
                }));
        };
    }

    private void ConstrainToWorkArea()
    {
        const double workAreaMargin = 24;
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(320, workArea.Width - workAreaMargin);
        var availableHeight = Math.Max(320, workArea.Height - workAreaMargin);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
    }

    private void OnHighRiskAcknowledgementChanged(object sender, RoutedEventArgs e)
    {
        ApproveButton.IsEnabled =
            !highRiskArmed || HighRiskAcknowledgement.IsChecked == true;
        StartFromNowButton.IsEnabled = ApproveButton.IsEnabled;
    }

    private void OnApprove(object sender, RoutedEventArgs e)
    {
        if (highRiskArmed && HighRiskAcknowledgement.IsChecked != true)
        {
            return;
        }

        DialogResult = true;
    }

    private void OnStartFromNow(object sender, RoutedEventArgs e)
    {
        if (highRiskArmed && HighRiskAcknowledgement.IsChecked != true)
        {
            return;
        }

        StartFromCurrentBaseline = true;
        DialogResult = true;
    }
}
