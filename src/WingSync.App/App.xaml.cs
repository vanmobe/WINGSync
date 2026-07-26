using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using WingSync.App.ViewModels;

namespace WingSync.App;

public partial class App : Application
{
    private int handlingUnhandledException;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private async void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        if (Interlocked.Exchange(ref handlingUnhandledException, 1) != 0)
        {
            Shutdown(-1);
            return;
        }

        var stopped = false;
        try
        {
            if (MainWindow?.DataContext is MainViewModel viewModel)
            {
                stopped = await viewModel.EmergencyStopAsync(args.Exception);
            }

            MessageBox.Show(
                stopped
                    ? $"WingSync caught an unexpected error.\n\n{args.Exception.Message}\n\n" +
                      "Synchronization was stopped fail-safe. See Activity for details."
                    : $"WingSync caught an unexpected error.\n\n{args.Exception.Message}\n\n" +
                      "A safe stop could not be confirmed; WingSync will close.",
                "WingSync – unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            if (!stopped)
            {
                Shutdown(-1);
            }
        }
        finally
        {
            Interlocked.Exchange(ref handlingUnhandledException, 0);
        }
    }
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool boolean && !boolean;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool boolean && !boolean;
}
