using System.Windows;

namespace WingSync.App.Dialogs;

public partial class StartupGuidanceWindow : Window
{
    public StartupGuidanceWindow(object viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ConstrainToWorkArea();
    }

    private void ConstrainToWorkArea()
    {
        const double workAreaMargin = 24;
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(420, workArea.Width - workAreaMargin);
        var availableHeight = Math.Max(360, workArea.Height - workAreaMargin);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
    }

    private void OnStayOnStatus(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnOpenSetup(object sender, RoutedEventArgs e) => DialogResult = true;
}
