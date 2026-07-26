using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using WingSync.App.ViewModels;

namespace WingSync.App;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private bool _closeAfterDisposal;
    private bool _loadStarted;
    private bool _windowClosing;
    private Task? _disposeTask;

    public MainWindow()
    {
        InitializeComponent();
        ConstrainToWorkArea();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void ConstrainToWorkArea()
    {
        const double workAreaMargin = 24;
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(640, workArea.Width - workAreaMargin);
        var availableHeight = Math.Max(360, workArea.Height - workAreaMargin);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control)
        {
            var pageIndex = e.Key switch
            {
                Key.D1 or Key.NumPad1 => 0,
                Key.D2 or Key.NumPad2 => 1,
                Key.D3 or Key.NumPad3 => 2,
                Key.D4 or Key.NumPad4 => 3,
                _ => -1,
            };

            if (pageIndex >= 0)
            {
                _viewModel.SelectedPageIndex = pageIndex;
                e.Handled = true;
                return;
            }
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        {
            if (_viewModel.StopCommand.CanExecute(null))
            {
                _viewModel.StopCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadStarted)
        {
            return;
        }

        _loadStarted = true;
        while (!_windowClosing)
        {
            try
            {
                var viewModel = await MainViewModel.CreateAsync(Environment.GetCommandLineArgs());
                if (_windowClosing)
                {
                    await viewModel.DisposeAsync();
                    return;
                }

                _viewModel = viewModel;
                DataContext = viewModel;
                return;
            }
            catch (Exception exception)
            {
                var answer = MessageBox.Show(
                    $"WingSync could not start safely.\n\n{exception.Message}\n\n" +
                    "Check storage permissions and that the installation is complete.",
                    "WingSync – startup error",
                    MessageBoxButton.RetryCancel,
                    MessageBoxImage.Error,
                    MessageBoxResult.Cancel);
                if (answer != MessageBoxResult.Retry)
                {
                    _closeAfterDisposal = true;
                    Close();
                    return;
                }
            }
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeAfterDisposal)
        {
            return;
        }

        if (_disposeTask is not null)
        {
            e.Cancel = true;
            return;
        }

        if (_viewModel is null)
        {
            _windowClosing = true;
            return;
        }

        if (!_viewModel.CanCloseImmediately)
        {
            var detail = _viewModel.IsRunning
                ? "Synchronization is active. Stopping disconnects both connections."
                : "A local or network action is still being completed.";
            var answer = MessageBox.Show(
                $"{detail}\n\nClose WingSync?",
                "Close WingSync",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        e.Cancel = true;
        _windowClosing = true;
        IsEnabled = false;
        _disposeTask = _viewModel.DisposeAsync().AsTask();
        try
        {
            await _disposeTask;
            _closeAfterDisposal = true;
            Close();
        }
        catch (Exception exception)
        {
            _windowClosing = false;
            _disposeTask = null;
            IsEnabled = true;
            MessageBox.Show(
                $"WingSync could not close safely.\n\n{exception.Message}",
                "WingSync – shutdown error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
