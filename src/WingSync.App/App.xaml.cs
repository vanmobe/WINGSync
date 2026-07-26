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
                    ? $"WingSync heeft een onverwachte fout opgevangen.\n\n{args.Exception.Message}\n\n" +
                      "De synchronisatie is fail-safe gestopt. Bekijk Activiteit voor details."
                    : $"WingSync heeft een onverwachte fout opgevangen.\n\n{args.Exception.Message}\n\n" +
                      "Een veilige stop kon niet worden bevestigd; WingSync wordt afgesloten.",
                "WingSync – onverwachte fout",
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
