using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WingSync.App.Dialogs;
using WingSync.Core.Abstractions;
using WingSync.Core.Domain;
using WingSync.Core.Services;
using WingSync.Infrastructure.Diagnostics;
using WingSync.Infrastructure.Discovery;
using WingSync.Infrastructure.Persistence;
using WingSync.Infrastructure.Simulation;
using WingSync.Infrastructure.Wapi;

namespace WingSync.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaximumPreviewAuditEntries = 20_000;
    private const int MaximumDialogTokenEntries = 2_000;
    private static readonly string ProductVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ??
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ??
        "1.0.0";
    private static readonly Brush Green = FrozenBrush(23, 131, 92);
    private static readonly Brush GreenSoft = FrozenBrush(226, 244, 237);
    private static readonly Brush Orange = FrozenBrush(180, 107, 8);
    private static readonly Brush OrangeSoft = FrozenBrush(255, 243, 226);
    private static readonly Brush Red = FrozenBrush(190, 59, 67);
    private static readonly Brush RedSoft = FrozenBrush(253, 232, 233);
    private static readonly Brush Gray = FrozenBrush(92, 102, 122);
    private static readonly Brush GraySoft = FrozenBrush(235, 238, 242);
    private static readonly Brush Blue = FrozenBrush(11, 120, 208);
    private static readonly Brush BlueSoft = FrozenBrush(227, 241, 251);
    private static readonly Brush White = FrozenBrush(255, 255, 255);

    private readonly string dataDirectory;
    private readonly bool demoMode;
    private readonly bool demoDiscoveryOffline;
    private readonly ConfigStore configStore;
    private readonly WingDiscoveryService discoveryService;
    private readonly IWingSessionFactory sessionFactory;
    private readonly IWingIdentityVerifier identityVerifier;
    private readonly WingStateCacheSink cacheSink;
    private readonly SyncDiagnosticsHub diagnostics;
    private readonly SyncCoordinator coordinator;
    private readonly ConcurrentQueue<PreviewTokenAuditEntry> previewTokenAudit = new();
    private readonly ObservableCollection<ActivityItemViewModel> activityItems = [];
    private readonly ICollectionView filteredActivity;
    private readonly SemaphoreSlim disposeGate = new(1, 1);
    private readonly SemaphoreSlim uiOperationGate = new(1, 1);
    private AppConfiguration? activeConfiguration;
    private bool disposed;
    private bool editableConfigurationIsValid;
    private bool suppressLiveSafetyReset = true;
    private bool connectionTestSucceeded;
    private bool hasCompletedDryRun;
    private bool isUiBusy;
    private int selectedPageIndex;
    private DiscoveredWingViewModel? selectedFohWing;
    private DiscoveredWingViewModel? selectedStageWing;
    private string editableFohIp = string.Empty;
    private string editableStageIp = string.Empty;
    private string discoveryStatus = "Nog niet gezocht.";
    private DirectionOption selectedDirection;
    private bool isDryRun = true;
    private bool verifyEveryWrite = true;
    private bool allowHighRiskWrites;
    private ChannelMappingViewModel? selectedMapping;
    private string validationSummary = "Controleer de configuratie en sla ze op.";
    private string validationDetails = string.Empty;
    private string configurationSafetyNotice = string.Empty;
    private Brush validationBrush = Gray;
    private string selectedActivityFilter = "Alles";
    private string overallStatusText = "Niet gestart";
    private Brush overallStatusBrush = Gray;
    private string fohStatusText = "Offline";
    private Brush fohStatusBackground = GraySoft;
    private Brush fohStatusForeground = Gray;
    private string stageStatusText = "Offline";
    private Brush stageStatusBackground = GraySoft;
    private Brush stageStatusForeground = Gray;
    private string flowStatusText = "GESTOPT";
    private Brush flowBrush = Gray;
    private bool hasBlockingProblems;
    private string primaryProblemTitle = string.Empty;
    private string primaryProblemDetail = string.Empty;
    private long syncedCount;
    private long previewedCount;
    private long blockedCount;
    private int queueDepth;
    private string p95Latency = "—";
    private string cacheStatus = "Offline beschikbaar";
    private string cacheAge = "nog geen live snapshot";
    private string fohLastData = "nog geen snapshot";
    private string stageLastData = "nog geen snapshot";
    private string fohCacheSummary = "Geen lokale snapshot";
    private string stageCacheSummary = "Geen lokale snapshot";
    private long cachePresentationGeneration;
    private int previewTokenAuditCount;

    private MainViewModel(
        string dataDirectory,
        bool demoMode,
        bool demoDiscoveryOffline,
        ConfigStore configStore,
        WingDiscoveryService discoveryService,
        IWingSessionFactory sessionFactory,
        IWingIdentityVerifier identityVerifier,
        WingStateCacheSink cacheSink,
        SyncDiagnosticsHub diagnostics,
        SyncCoordinator coordinator)
    {
        this.dataDirectory = dataDirectory;
        this.demoMode = demoMode;
        this.demoDiscoveryOffline = demoDiscoveryOffline;
        this.configStore = configStore;
        this.discoveryService = discoveryService;
        this.sessionFactory = sessionFactory;
        this.identityVerifier = identityVerifier;
        this.cacheSink = cacheSink;
        this.diagnostics = diagnostics;
        this.coordinator = coordinator;

        DirectionOptions =
        [
            new DirectionOption("FOH → Podium (aanbevolen)", SyncDirection.FohToMonitor),
            new DirectionOption("Podium → FOH", SyncDirection.MonitorToFoh),
        ];
        selectedDirection = DirectionOptions[0];
        ActivityFilters = ["Alles", "Problemen", "Writes", "Netwerk"];
        ScopeSelections = new ObservableCollection<ScopeSelectionViewModel>(CreateScopeSelections());
        ChannelMappings = [];
        DiscoveredWings = [];
        filteredActivity = CollectionViewSource.GetDefaultView(activityItems);
        filteredActivity.Filter = FilterActivity;

        StartCommand = new AsyncRelayCommand(
            () => RunExclusiveAsync(StartAsync),
            () => CanPrimaryAction,
            HandleCommandError);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning, HandleCommandError);
        DiscoverCommand = new AsyncRelayCommand(
            () => RunExclusiveAsync(() => DiscoverAsync(silent: false)),
            () => !IsRunning && !IsUiBusy,
            HandleCommandError);
        TestConnectionsCommand = new AsyncRelayCommand(
            () => RunExclusiveAsync(TestConnectionsAsync),
            () => !IsRunning && !IsUiBusy,
            HandleCommandError);
        SaveConfigurationCommand = new AsyncRelayCommand(
            () => RunExclusiveAsync(SaveConfigurationAsync),
            () => !IsRunning && !IsUiBusy && editableConfigurationIsValid,
            HandleCommandError);
        AutoMapCommand = new RelayCommand(
            AutoMap,
            () => !IsRunning && !IsUiBusy,
            HandleCommandError);
        AddMappingCommand = new RelayCommand(
            AddMapping,
            () => !IsRunning && !IsUiBusy,
            HandleCommandError);
        AddAuxMappingCommand = new RelayCommand(
            AddAuxMapping,
            () => !IsRunning && !IsUiBusy,
            HandleCommandError);
        RemoveMappingsCommand = new RelayCommand(
            RemoveSelectedMapping,
            () => !IsRunning && !IsUiBusy && SelectedMapping is not null,
            HandleCommandError);
        ShowProblemsCommand = new RelayCommand(
            () => SelectedPageIndex = 2,
            onError: HandleCommandError);
        OpenSetupCommand = new RelayCommand(
            () => SelectedPageIndex = 1,
            onError: HandleCommandError);
        ClearActivityCommand = new RelayCommand(ClearActivity, onError: HandleCommandError);
        ExportSupportBundleCommand = new AsyncRelayCommand(
            () => RunExclusiveAsync(ExportSupportBundleAsync),
            () => !IsUiBusy,
            onError: HandleCommandError);
        OpenDataDirectoryCommand = new RelayCommand(OpenDataDirectory, onError: HandleCommandError);
        RebuildCacheCommand = new AsyncRelayCommand(
            () => RunExclusiveAsync(RebuildCacheAsync),
            () => !IsRunning && !IsUiBusy,
            HandleCommandError);
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics, onError: HandleCommandError);

        coordinator.StatusChanged += OnCoordinatorStatusChanged;
        coordinator.MetricsChanged += OnCoordinatorMetricsChanged;
        diagnostics.DiagnosticRecorded += OnDiagnosticRecorded;

        foreach (var scope in ScopeSelections)
        {
            scope.PropertyChanged += OnEditableConfigurationChanged;
        }
    }

    public static async Task<MainViewModel> CreateAsync(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var demo = arguments.Any(static argument =>
            argument.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        var demoDiscoveryOffline = arguments.Any(static argument =>
            argument.Equals("--demo-offline", StringComparison.OrdinalIgnoreCase));
        var dataDirectory = GetDataDirectory(arguments);
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "cache"));

        var configStore = new ConfigStore(
            new ConfigStoreOptions(Path.Combine(dataDirectory, "config.json")));
        var logger = new JsonLineLogger(
            new JsonLineLoggerOptions(Path.Combine(dataDirectory, "logs"))
            {
                MaximumFileSizeBytes = 5 * 1024 * 1024,
                RetainedFileCount = 10,
                MinimumLevel = WingLogLevel.Information,
            });
        var diagnostics = new SyncDiagnosticsHub(logger);
        var stateCache = new WingStateCache(
            new WingStateCacheOptions(Path.Combine(dataDirectory, "cache")));
        var cacheSink = new WingStateCacheSink(stateCache);
        stateCache.WriteFailed += (_, eventArgs) =>
            diagnostics.Record(
                new DiagnosticEvent(
                    DiagnosticSeverity.Error,
                    "CACHE_WRITE_FAILED",
                    eventArgs.Exception.Message,
                    "Cache",
                    DateTimeOffset.UtcNow));

        var discovery = new WingDiscoveryService();
        IWingSessionFactory sessionFactory;
        IWingIdentityVerifier identityVerifier;
        string? startupProblem = null;
        if (demo)
        {
            var identities = CreateDemoIdentities();
            var sourceState = CreateDemoState(isTarget: false);
            var targetState = CreateDemoState(isTarget: true);
            sessionFactory = new SimulatedWingSessionFactory(role =>
                new SimulatedWingSession(
                    role.Equals("FOH", StringComparison.OrdinalIgnoreCase)
                        ? sourceState
                        : targetState,
                    operationDelay: TimeSpan.FromMilliseconds(2),
                    writeDelay: TimeSpan.FromSeconds(3)));
            identityVerifier = new StaticWingIdentityVerifier(identities);
        }
        else
        {
            string helperPath;
            try
            {
                helperPath = WapiSessionFactory.LocateHelper();
            }
            catch (FileNotFoundException exception)
            {
                helperPath = Path.Combine(AppContext.BaseDirectory, "WingSync.WapiHost.exe");
                startupProblem = exception.Message;
                diagnostics.Record(
                    new DiagnosticEvent(
                        DiagnosticSeverity.Critical,
                        "WAPI_HELPER_MISSING",
                        exception.Message,
                        "Installatie",
                        DateTimeOffset.UtcNow));
            }

            sessionFactory = new WapiSessionFactory(helperPath, diagnostics);
            identityVerifier = new WingIdentityVerifier(discovery);
        }

        var coordinator = new SyncCoordinator(
            sessionFactory,
            identityVerifier,
            cacheSink,
            diagnostics);
        var viewModel = new MainViewModel(
            dataDirectory,
            demo,
            demoDiscoveryOffline,
            configStore,
            discovery,
            sessionFactory,
            identityVerifier,
            cacheSink,
            diagnostics,
            coordinator);
        await viewModel.InitializeAsync();
        if (startupProblem is not null)
        {
            viewModel.SetProblem(
                "WAPI-helper ontbreekt",
                $"{startupProblem} Installeer het volledige WingSync-pakket opnieuw.");
        }

        return viewModel;
    }

    public ObservableCollection<DiscoveredWingViewModel> DiscoveredWings { get; }

    public ObservableCollection<ScopeSelectionViewModel> ScopeSelections { get; }

    public ObservableCollection<ChannelMappingViewModel> ChannelMappings { get; }

    public IReadOnlyList<DirectionOption> DirectionOptions { get; }

    public IReadOnlyList<string> ActivityFilters { get; }

    public ICollectionView FilteredActivity => filteredActivity;

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand DiscoverCommand { get; }

    public AsyncRelayCommand TestConnectionsCommand { get; }

    public AsyncRelayCommand SaveConfigurationCommand { get; }

    public RelayCommand AutoMapCommand { get; }

    public RelayCommand AddMappingCommand { get; }

    public RelayCommand AddAuxMappingCommand { get; }

    public RelayCommand RemoveMappingsCommand { get; }

    public RelayCommand ShowProblemsCommand { get; }

    public RelayCommand OpenSetupCommand { get; }

    public RelayCommand ClearActivityCommand { get; }

    public AsyncRelayCommand ExportSupportBundleCommand { get; }

    public RelayCommand OpenDataDirectoryCommand { get; }

    public AsyncRelayCommand RebuildCacheCommand { get; }

    public RelayCommand CopyDiagnosticsCommand { get; }

    public int SelectedPageIndex
    {
        get => selectedPageIndex;
        set
        {
            if (SetProperty(ref selectedPageIndex, value))
            {
                OnPropertyChanged(nameof(HeaderTitle));
                OnPropertyChanged(nameof(HeaderSubtitle));
            }
        }
    }

    public string HeaderTitle => SelectedPageIndex switch
    {
        0 => "Status",
        1 => "Synchronisatie instellen",
        2 => "Activiteit en diagnose",
        3 => "Instellingen",
        _ => "WingSync",
    };

    public string HeaderSubtitle => SelectedPageIndex switch
    {
        0 => "Veilige eenrichtingssynchronisatie met readback",
        1 => "Scopes en kanaalmapping",
        2 => "Gestructureerde lokale audittrail",
        3 => "Herstel, cache en productinformatie",
        _ => string.Empty,
    };

    public DiscoveredWingViewModel? SelectedFohWing
    {
        get => selectedFohWing;
        set
        {
            var identityChanged = !SamePhysicalConsole(selectedFohWing, value);
            if (SetProperty(ref selectedFohWing, value))
            {
                if (value is not null)
                {
                    EditableFohIp = value.Wing.IpAddress;
                }

                if (identityChanged)
                {
                    NotifyTopologyChanged();
                }

                RefreshConsolePresentation();
                _ = RefreshOfflineCachePresentationSafeAsync();
            }
        }
    }

    public DiscoveredWingViewModel? SelectedStageWing
    {
        get => selectedStageWing;
        set
        {
            var identityChanged = !SamePhysicalConsole(selectedStageWing, value);
            if (SetProperty(ref selectedStageWing, value))
            {
                if (value is not null)
                {
                    EditableStageIp = value.Wing.IpAddress;
                }

                if (identityChanged)
                {
                    NotifyTopologyChanged();
                }

                RefreshConsolePresentation();
                _ = RefreshOfflineCachePresentationSafeAsync();
            }
        }
    }

    public string EditableFohIp
    {
        get => editableFohIp;
        set
        {
            if (SetProperty(ref editableFohIp, value ?? string.Empty))
            {
                NotifyTopologyChanged();
                OnPropertyChanged(nameof(FohIp));
                OnPropertyChanged(nameof(FohSerial));
                OnPropertyChanged(nameof(FohIdentityText));
            }
        }
    }

    public string EditableStageIp
    {
        get => editableStageIp;
        set
        {
            if (SetProperty(ref editableStageIp, value ?? string.Empty))
            {
                NotifyTopologyChanged();
                OnPropertyChanged(nameof(StageIp));
                OnPropertyChanged(nameof(StageSerial));
                OnPropertyChanged(nameof(StageIdentityText));
            }
        }
    }

    public string DiscoveryStatus
    {
        get => discoveryStatus;
        private set => SetProperty(ref discoveryStatus, value);
    }

    public DirectionOption SelectedDirection
    {
        get => selectedDirection;
        set
        {
            if (SetProperty(ref selectedDirection, value))
            {
                NotifyTopologyChanged();
                RaiseDirectionProperties();
            }
        }
    }

    public bool IsDryRun
    {
        get => isDryRun;
        set
        {
            if (SetProperty(ref isDryRun, value))
            {
                NotifyConfigurationChanged();
                OnPropertyChanged(nameof(StartButtonText));
                OnPropertyChanged(nameof(RunModeText));
                OnPropertyChanged(nameof(RunModeBackground));
                OnPropertyChanged(nameof(RunModeForeground));
                OnPropertyChanged(nameof(SafetySummary));
            }
        }
    }

    public bool VerifyEveryWrite
    {
        get => verifyEveryWrite;
        set
        {
            if (SetProperty(ref verifyEveryWrite, value))
            {
                NotifyConfigurationChanged();
            }
        }
    }

    public bool AllowHighRiskWrites
    {
        get => allowHighRiskWrites;
        set
        {
            if (SetProperty(ref allowHighRiskWrites, value))
            {
                NotifyConfigurationChanged();
                OnPropertyChanged(nameof(SafetySummary));
            }
        }
    }

    public ChannelMappingViewModel? SelectedMapping
    {
        get => selectedMapping;
        set
        {
            if (SetProperty(ref selectedMapping, value))
            {
                RemoveMappingsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ValidationSummary
    {
        get => validationSummary;
        private set => SetProperty(ref validationSummary, value);
    }

    public string ValidationDetails
    {
        get => validationDetails;
        private set => SetProperty(ref validationDetails, value);
    }

    public string ConfigurationSafetyNotice
    {
        get => configurationSafetyNotice;
        private set => SetProperty(ref configurationSafetyNotice, value);
    }

    public Brush ValidationBrush
    {
        get => validationBrush;
        private set => SetProperty(ref validationBrush, value);
    }

    public string SelectedActivityFilter
    {
        get => selectedActivityFilter;
        set
        {
            if (SetProperty(ref selectedActivityFilter, value ?? "Alles"))
            {
                filteredActivity.Refresh();
            }
        }
    }

    public string OverallStatusText
    {
        get => overallStatusText;
        private set => SetProperty(ref overallStatusText, value);
    }

    public Brush OverallStatusBrush
    {
        get => overallStatusBrush;
        private set => SetProperty(ref overallStatusBrush, value);
    }

    public string FohStatusText
    {
        get => fohStatusText;
        private set => SetProperty(ref fohStatusText, value);
    }

    public Brush FohStatusBackground
    {
        get => fohStatusBackground;
        private set => SetProperty(ref fohStatusBackground, value);
    }

    public Brush FohStatusForeground
    {
        get => fohStatusForeground;
        private set => SetProperty(ref fohStatusForeground, value);
    }

    public string StageStatusText
    {
        get => stageStatusText;
        private set => SetProperty(ref stageStatusText, value);
    }

    public Brush StageStatusBackground
    {
        get => stageStatusBackground;
        private set => SetProperty(ref stageStatusBackground, value);
    }

    public Brush StageStatusForeground
    {
        get => stageStatusForeground;
        private set => SetProperty(ref stageStatusForeground, value);
    }

    public string FlowStatusText
    {
        get => flowStatusText;
        private set => SetProperty(ref flowStatusText, value);
    }

    public Brush FlowBrush
    {
        get => flowBrush;
        private set => SetProperty(ref flowBrush, value);
    }

    public bool HasBlockingProblems
    {
        get => hasBlockingProblems;
        private set => SetProperty(ref hasBlockingProblems, value);
    }

    public string PrimaryProblemTitle
    {
        get => primaryProblemTitle;
        private set => SetProperty(ref primaryProblemTitle, value);
    }

    public string PrimaryProblemDetail
    {
        get => primaryProblemDetail;
        private set => SetProperty(ref primaryProblemDetail, value);
    }

    public long SyncedCount
    {
        get => syncedCount;
        private set => SetProperty(ref syncedCount, value);
    }

    public long PreviewedCount
    {
        get => previewedCount;
        private set => SetProperty(ref previewedCount, value);
    }

    public long BlockedCount
    {
        get => blockedCount;
        private set => SetProperty(ref blockedCount, value);
    }

    public int QueueDepth
    {
        get => queueDepth;
        private set => SetProperty(ref queueDepth, value);
    }

    public string P95Latency
    {
        get => p95Latency;
        private set => SetProperty(ref p95Latency, value);
    }

    public string CacheStatus
    {
        get => cacheStatus;
        private set => SetProperty(ref cacheStatus, value);
    }

    public string CacheAge
    {
        get => cacheAge;
        private set => SetProperty(ref cacheAge, value);
    }

    public string FohCacheSummary
    {
        get => fohCacheSummary;
        private set => SetProperty(ref fohCacheSummary, value);
    }

    public string StageCacheSummary
    {
        get => stageCacheSummary;
        private set => SetProperty(ref stageCacheSummary, value);
    }

    public bool IsRunning =>
        coordinator.Status.State is not SyncCoordinatorState.Stopped and not SyncCoordinatorState.Faulted;

    public bool CanStart =>
        !IsRunning &&
        !IsUiBusy &&
        editableConfigurationIsValid &&
        BothIdentitiesPinned &&
        connectionTestSucceeded &&
        (IsDryRun || hasCompletedDryRun) &&
        !string.IsNullOrWhiteSpace(EditableFohIp) &&
        !string.IsNullOrWhiteSpace(EditableStageIp) &&
        ChannelMappings.Any(static mapping => mapping.IsEnabled);

    public bool CanPrimaryAction =>
        !IsRunning &&
        !IsUiBusy &&
        editableConfigurationIsValid &&
        BothIdentitiesPinned &&
        !string.IsNullOrWhiteSpace(EditableFohIp) &&
        !string.IsNullOrWhiteSpace(EditableStageIp) &&
        ChannelMappings.Any(static mapping => mapping.IsEnabled) &&
        (!connectionTestSucceeded || IsDryRun || hasCompletedDryRun);

    public bool CanSelectLiveMode =>
        !IsRunning &&
        !IsUiBusy &&
        connectionTestSucceeded &&
        hasCompletedDryRun;

    public bool CanArmHighRisk =>
        !IsRunning &&
        !IsUiBusy &&
        !IsDryRun &&
        connectionTestSucceeded &&
        hasCompletedDryRun;

    public bool CanCloseImmediately => !IsRunning && !IsUiBusy;

    public bool IsUiBusy
    {
        get => isUiBusy;
        private set
        {
            if (SetProperty(ref isUiBusy, value))
            {
                RaiseRunProperties();
            }
        }
    }

    public bool IsEditorEnabled => !IsRunning && !IsUiBusy;

    public string StartButtonText
    {
        get
        {
            if (!BothIdentitiesPinned)
            {
                return "Setup vereist";
            }

            if (!connectionTestSucceeded)
            {
                return "Test verbinding";
            }

            if (!IsDryRun && !hasCompletedDryRun)
            {
                return "Droogloop vereist";
            }

            return IsDryRun ? "Start droogloop" : "Start live";
        }
    }

    public string StartButtonHelpText
    {
        get
        {
            if (!BothIdentitiesPinned)
            {
                return "Setup vereist: wijs beide consoles toe en controleer de serienummers.";
            }

            if (!connectionTestSucceeded)
            {
                return "Test eerst beide WAPI-verbindingen en hardware-identiteiten in Setup.";
            }

            if (!IsDryRun && !hasCompletedDryRun)
            {
                return "Schakel droogloop opnieuw in en beoordeel eerst de preview.";
            }

            return IsDryRun
                ? "Start een write-vrije droogloop en beoordeel de preview."
                : "Maak een verse live-diff; de veilige standaardkeuze in de bevestiging is Nee.";
        }
    }

    public string RunModeText => coordinator.Status.State switch
    {
        SyncCoordinatorState.RunningLive => "LIVE ACTIEF · READBACK OK",
        SyncCoordinatorState.ApplyingLive => "LIVE WORDT TOEGEPAST · WRITES + READBACK",
        SyncCoordinatorState.RunningDryRun => "DROOGLOOP · GEEN WRITES",
        SyncCoordinatorState.AwaitingConfirmation or
        SyncCoordinatorState.Connecting or
        SyncCoordinatorState.Snapshotting => activeConfiguration?.Safety.DryRun == false
            ? "WORDT VOORBEREID · GEEN WRITES"
            : "DROOGLOOP · GEEN WRITES",
        SyncCoordinatorState.Reconnecting =>
            activeConfiguration?.Safety.DryRun == false
                ? "LIVE GEPAUZEERD · GEEN WRITES"
                : "DROOGLOOP GEPAUZEERD",
        SyncCoordinatorState.Paused or SyncCoordinatorState.Faulted =>
            "GEBLOKKEERD · GEEN WRITES",
        _ => IsDryRun ? "LIVE UIT · DROOGLOOP" : "LIVE GEREED · NOG GEEN WRITES",
    };

    public Brush RunModeBackground => coordinator.Status.State switch
    {
        SyncCoordinatorState.RunningLive => GreenSoft,
        SyncCoordinatorState.ApplyingLive => OrangeSoft,
        SyncCoordinatorState.RunningDryRun => BlueSoft,
        SyncCoordinatorState.Paused => OrangeSoft,
        SyncCoordinatorState.Faulted => RedSoft,
        SyncCoordinatorState.AwaitingConfirmation or
        SyncCoordinatorState.Connecting or
        SyncCoordinatorState.Snapshotting or
        SyncCoordinatorState.Reconnecting => OrangeSoft,
        _ => IsDryRun ? GraySoft : OrangeSoft,
    };

    public Brush RunModeForeground => coordinator.Status.State switch
    {
        SyncCoordinatorState.RunningLive => Green,
        SyncCoordinatorState.ApplyingLive => Orange,
        SyncCoordinatorState.RunningDryRun => Blue,
        SyncCoordinatorState.Paused => Orange,
        SyncCoordinatorState.Faulted => Red,
        SyncCoordinatorState.AwaitingConfirmation or
        SyncCoordinatorState.Connecting or
        SyncCoordinatorState.Snapshotting or
        SyncCoordinatorState.Reconnecting => Orange,
        _ => IsDryRun ? Gray : Orange,
    };

    public string SafetySummary =>
        IsDryRun
            ? "Droogloop is veilig: verschillen worden berekend en gelogd, maar niet verstuurd."
            : AllowHighRiskWrites
                ? "Live en verhoogd risico: routing/levels kunnen hoorbaar wijzigen. Een extra bevestiging is verplicht."
                : "Live writes worden teruggelezen. CONN-, MAIN-, BUS-, FADER- en MUTE-scopes blijven geblokkeerd.";

    public string VersionText =>
        $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}" +
        (demoMode ? " · DEMO" : string.Empty);

    public string FullVersionText =>
        $"WingSync {ProductVersion} · Windows x64" +
        (demoMode ? " · demo" : string.Empty);

    public string DataDirectory => dataDirectory;

    public string DirectionSummaryText =>
        SelectedDirection.Direction == SyncDirection.MonitorToFoh
            ? "PODIUM ↦ FOH"
            : "FOH ↦ PODIUM";

    public string FohRoleCaption =>
        SelectedDirection.Direction == SyncDirection.MonitorToFoh
            ? "FOH · DOEL"
            : "FOH · BRON";

    public string StageRoleCaption =>
        SelectedDirection.Direction == SyncDirection.MonitorToFoh
            ? "PODIUM · BRON"
            : "PODIUM · DOEL";

    public string FohEditorCaption =>
        SelectedDirection.Direction == SyncDirection.MonitorToFoh
            ? "FOH-console (doel)"
            : "FOH-console (bron)";

    public string StageEditorCaption =>
        SelectedDirection.Direction == SyncDirection.MonitorToFoh
            ? "Podiumconsole (bron)"
            : "Podiumconsole (doel)";

    public string FlowArrowText =>
        SelectedDirection.Direction == SyncDirection.MonitorToFoh ? "◀────" : "────▶";

    public string FohName => SelectedFohWing?.Wing.Name ?? "FOH niet toegewezen";

    public string FohIp => EditableFohIp.Length == 0 ? "—" : EditableFohIp;

    public string FohModel => SelectedFohWing?.Wing.Model ?? "—";

    public string FohFirmware => SelectedFohWing?.Wing.FirmwareVersion ?? "—";

    public string FohSerial =>
        ResolveVisibleSerial(SelectedFohWing, EditableFohIp, activeConfiguration?.Foh);

    public string FohIdentityText =>
        ResolveIdentityStatus(SelectedFohWing, EditableFohIp, activeConfiguration?.Foh);

    public string FohLastSeen => fohLastData;

    public string StageName => SelectedStageWing?.Wing.Name ?? "Podium niet toegewezen";

    public string StageIp => EditableStageIp.Length == 0 ? "—" : EditableStageIp;

    public string StageModel => SelectedStageWing?.Wing.Model ?? "—";

    public string StageFirmware => SelectedStageWing?.Wing.FirmwareVersion ?? "—";

    public string StageSerial =>
        ResolveVisibleSerial(SelectedStageWing, EditableStageIp, activeConfiguration?.Monitor);

    public string StageIdentityText =>
        ResolveIdentityStatus(
            SelectedStageWing,
            EditableStageIp,
            activeConfiguration?.Monitor);

    public string StageLastSeen => stageLastData;

    public string SetupProgressText =>
        $"{CompletedSetupSteps}/4 setupstappen gereed";

    public string SetupProgressIndicatorText =>
        CompletedSetupSteps == 4
            ? "✓"
            : CompletedSetupSteps.ToString(CultureInfo.CurrentCulture);

    public string SetupProgressAccessibleText =>
        CompletedSetupSteps == 4
            ? "Setup voltooid, vier van vier stappen gereed"
            : $"Setup in uitvoering, {CompletedSetupSteps} van vier stappen gereed";

    public Brush SetupProgressBackground =>
        CompletedSetupSteps == 4 ? GreenSoft : GraySoft;

    public Brush SetupProgressForeground =>
        CompletedSetupSteps == 4 ? Green : Gray;

    public string NextStepText
    {
        get
        {
            if (!BothIdentitiesPinned)
            {
                return "Volgende stap: zoek beide consoles en controleer hun serienummer.";
            }

            if (!editableConfigurationIsValid ||
                !ChannelMappings.Any(static mapping => mapping.IsEnabled) ||
                !ScopeSelections.Any(static scope => scope.IsSelected))
            {
                return "Volgende stap: kies scopes en controleer de kanaalmapping.";
            }

            if (!connectionTestSucceeded)
            {
                return "Volgende stap: test de twee verbindingen en identiteiten.";
            }

            if (!hasCompletedDryRun)
            {
                return "Volgende stap: start een droogloop en beoordeel de preview.";
            }

            return IsDryRun
                ? "Droogloop gecontroleerd: stop, schakel live bewust in en bekijk de verse diff."
                : "Setup gereed: start live en bevestig de verse diff; standaardkeuze blijft Nee.";
        }
    }

    private async Task InitializeAsync()
    {
        if (demoMode && !demoDiscoveryOffline)
        {
            foreach (var identity in CreateDemoIdentities())
            {
                DiscoveredWings.Add(new DiscoveredWingViewModel(identity));
            }

            SelectedFohWing = DiscoveredWings[0];
            SelectedStageWing = DiscoveredWings[1];
            DiscoveryStatus = "2 gesimuleerde WINGs beschikbaar.";
        }
        else if (demoMode)
        {
            DiscoveryStatus = "Demo-netwerk offline; alleen opgeslagen pins en lokale cache beschikbaar.";
        }

        var load = await configStore.LoadAsync();
        if (load.Configuration is not null)
        {
            ApplyConfiguration(load.Configuration);
        }
        else
        {
            ChannelMappings.Add(new ChannelMappingViewModel(1, 1, "Kanaal 1"));
            AttachMappingHandlers();
            ApplySafeDefaultScopes();
        }

        switch (load.Status)
        {
            case ConfigLoadStatus.RecoveredFromBackup:
                SetProblem(
                    "Configuratie hersteld",
                    "De primaire configuratie was niet leesbaar; de vorige geldige backup is geladen.");
                break;
            case ConfigLoadStatus.CorruptQuarantined:
                SetProblem(
                    "Beschadigde configuratie geïsoleerd",
                    $"Veilige standaardwaarden zijn geladen. Bestand: {load.QuarantinedPath}");
                break;
            case ConfigLoadStatus.UnsupportedSchema:
                SetProblem(
                    "Configuratieversie niet ondersteund",
                    load.Message ?? "Werk WingSync bij of maak een nieuwe configuratie.");
                break;
            case ConfigLoadStatus.Failed:
                SetProblem(
                    "Configuratie kon niet worden gelezen",
                    load.Message ?? "Controleer de toegangsrechten van de lokale opslagmap.");
                break;
        }

        if (!demoMode)
        {
            await DiscoverAsync(silent: true);
        }

        await RefreshOfflineCachePresentationAsync();
        suppressLiveSafetyReset = false;
        RefreshConsolePresentation();
        ValidateEditableConfiguration();
        RaiseSetupProperties();
    }

    private async Task DiscoverAsync(bool silent)
    {
        DiscoveryStatus = "Zoeken op actieve netwerkadapters…";
        try
        {
            var result = await discoveryService.DiscoverAsync();
            var previousFohSerial =
                SelectedFohWing?.Wing.SerialNumber ??
                activeConfiguration?.Foh.ExpectedSerial;
            var previousStageSerial =
                SelectedStageWing?.Wing.SerialNumber ??
                activeConfiguration?.Monitor.ExpectedSerial;
            DiscoveredWings.Clear();
            foreach (var wing in result.Wings)
            {
                DiscoveredWings.Add(new DiscoveredWingViewModel(wing));
            }

            SelectedFohWing = FindSelection(previousFohSerial, EditableFohIp);
            if (SelectedFohWing is null && string.IsNullOrWhiteSpace(previousFohSerial))
            {
                SelectedFohWing =
                    FindByRoleHint("desk", "foh") ??
                    DiscoveredWings.FirstOrDefault();
            }

            SelectedStageWing = FindSelection(previousStageSerial, EditableStageIp);
            if (SelectedStageWing is null && string.IsNullOrWhiteSpace(previousStageSerial))
            {
                SelectedStageWing =
                    FindByRoleHint("rack", "mon", "stage") ??
                    DiscoveredWings.FirstOrDefault(item => !ReferenceEquals(item, SelectedFohWing));
            }

            if (SelectedStageWing is not null &&
                ReferenceEquals(SelectedStageWing, SelectedFohWing))
            {
                SelectedStageWing = string.IsNullOrWhiteSpace(previousStageSerial)
                    ? DiscoveredWings.FirstOrDefault(
                        item => !ReferenceEquals(item, SelectedFohWing))
                    : null;
            }
            DiscoveryStatus = result.Wings.Count switch
            {
                0 => "Geen WING gevonden.",
                1 => "1 WING gevonden; wijs de tweede console handmatig toe.",
                _ => $"{result.Wings.Count} WINGs gevonden.",
            };

            if (result.Issues.Count > 0)
            {
                diagnostics.Record(
                    new DiagnosticEvent(
                        DiagnosticSeverity.Warning,
                        "DISCOVERY_ADAPTER_ISSUES",
                        $"{result.Issues.Count} adapterprobe(s) gaven een probleem.",
                        "Discovery",
                        DateTimeOffset.UtcNow));
            }

            if (!silent && result.Wings.Count == 0)
            {
                SetProblem(
                    "Geen consoles gevonden",
                    "Controleer de control-netwerkkabels, IPv4-adapters en Windows Firewall.");
            }
        }
        catch (Exception exception)
        {
            DiscoveryStatus = $"Zoeken mislukt: {exception.Message}";
            SetProblem("Discovery mislukt", exception.Message);
            diagnostics.Record(
                new DiagnosticEvent(
                    DiagnosticSeverity.Error,
                    "DISCOVERY_FAILED",
                    exception.Message,
                    "Discovery",
                    DateTimeOffset.UtcNow));
        }

        RefreshConsolePresentation();
        ValidateEditableConfiguration();
    }

    private async Task TestConnectionsAsync()
    {
        if (!demoMode)
        {
            await DiscoverAsync(silent: false);
        }

        var configuration = BuildConfiguration();
        if (string.IsNullOrWhiteSpace(configuration.Foh.IpAddress) ||
            string.IsNullOrWhiteSpace(configuration.Monitor.IpAddress))
        {
            SetProblem("Verbindingstest niet mogelijk", "Vul voor beide consoles een IP-adres in.");
            return;
        }

        DiscoveryStatus = "Identiteiten, WAPI-verbinding en statusreadback testen…";
        IWingSession? foh = null;
        IWingSession? stage = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var fohIdentityTask = identityVerifier.VerifyAsync(configuration.Foh, timeout.Token);
            var stageIdentityTask = identityVerifier.VerifyAsync(configuration.Monitor, timeout.Token);
            await Task.WhenAll(fohIdentityTask, stageIdentityTask);
            var fohIdentity = await fohIdentityTask;
            var stageIdentity = await stageIdentityTask;
            if (fohIdentity.SerialNumber.Equals(
                    stageIdentity.SerialNumber,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "FOH en podium werden als dezelfde fysieke console geïdentificeerd.");
            }

            foh = sessionFactory.Create("FOH-test");
            stage = sessionFactory.Create("Podium-test");
            await Task.WhenAll(
                foh.ConnectAsync(configuration.Foh, timeout.Token),
                stage.ConnectAsync(configuration.Monitor, timeout.Token));
            await Task.WhenAll(
                foh.PingAsync(timeout.Token),
                stage.PingAsync(timeout.Token));
            var fohStatusTask = foh.SnapshotAsync("$STAT", timeout.Token);
            var stageStatusTask = stage.SnapshotAsync("$STAT", timeout.Token);
            await Task.WhenAll(fohStatusTask, stageStatusTask);
            var fohStatus = await fohStatusTask;
            var stageStatus = await stageStatusTask;

            DiscoveryStatus =
                $"WAPI gezond: {fohIdentity.Name} ({fohStatus.Count} statuswaarden) en " +
                $"{stageIdentity.Name} ({stageStatus.Count} statuswaarden).";
            connectionTestSucceeded = true;
            var testedAt = DateTimeOffset.Now;
            fohLastData = FormatLastSeen(testedAt);
            stageLastData = FormatLastSeen(testedAt);
            SetConsoleStatus("Identiteit bevestigd", Green, GreenSoft);
            RefreshConsolePresentation();
            RaiseSetupProperties();
            ClearProblem();
            diagnostics.Record(
                new DiagnosticEvent(
                    DiagnosticSeverity.Information,
                    "CONNECTION_TEST_OK",
                    DiscoveryStatus,
                    "Netwerk",
                    DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            connectionTestSucceeded = false;
            RaiseSetupProperties();
            const string message = "De verbindingstest overschreed de veilige timeout van 30 seconden.";
            DiscoveryStatus = message;
            SetProblem("Verbindingstest timeout", message);
            diagnostics.Record(
                new DiagnosticEvent(
                    DiagnosticSeverity.Error,
                    "CONNECTION_TEST_TIMEOUT",
                    message,
                    "Netwerk",
                    DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            connectionTestSucceeded = false;
            RaiseSetupProperties();
            DiscoveryStatus = $"Verbindingstest mislukt: {exception.Message}";
            SetProblem("Verbindingstest mislukt", exception.Message);
            diagnostics.Record(
                new DiagnosticEvent(
                    DiagnosticSeverity.Error,
                    "CONNECTION_TEST_FAILED",
                    exception.Message,
                    "Netwerk",
                    DateTimeOffset.UtcNow));
        }
        finally
        {
            await SafeDisconnectAndDisposeAsync(foh);
            await SafeDisconnectAndDisposeAsync(stage);
        }
    }

    private async Task SaveConfigurationAsync()
    {
        var configuration = BuildConfiguration();
        var validation = ConfigValidator.Validate(configuration);
        ApplyValidation(validation);
        if (!validation.IsValid)
        {
            SelectedPageIndex = 1;
            return;
        }

        await configStore.SaveAsync(CreatePersistedSafeConfiguration(configuration));
        activeConfiguration = configuration;
        ValidationSummary =
            $"Opgeslagen om {DateTime.Now:HH:mm:ss}; live- en hoog-risicotoestemming blijven alleen in deze sessie.";
        ValidationBrush = Green;
        diagnostics.Record(
            new DiagnosticEvent(
                DiagnosticSeverity.Information,
                "CONFIG_SAVED",
                "Configuratie atomisch opgeslagen.",
                "Configuratie",
                DateTimeOffset.UtcNow));
    }

    private async Task StartAsync()
    {
        if (!BothIdentitiesPinned)
        {
            SetProblem(
                "Setup nog niet klaar",
                "Wijs beide consoles toe en controleer de zichtbare serienummers.");
            SelectedPageIndex = 1;
            return;
        }

        if (!connectionTestSucceeded)
        {
            await TestConnectionsAsync();
            return;
        }

        if (!IsDryRun && !hasCompletedDryRun)
        {
            SetProblem(
                "Droogloop vereist",
                "Voer na de laatste configuratiewijziging eerst een droogloop uit en stop die bewust.");
            SelectedPageIndex = 1;
            return;
        }

        var configuration = BuildConfiguration();
        var validation = ConfigValidator.Validate(configuration);
        ApplyValidation(validation);
        if (!validation.IsValid)
        {
            SelectedPageIndex = 1;
            return;
        }

        if (!VerifyEveryWrite && !IsDryRun)
        {
            SetProblem(
                "Readback is verplicht",
                "Live synchronisatie kan niet starten zonder verificatie van iedere write.");
            return;
        }

        if (!IsDryRun &&
            (string.IsNullOrWhiteSpace(configuration.Foh.ExpectedSerial) ||
             string.IsNullOrWhiteSpace(configuration.Monitor.ExpectedSerial)))
        {
            SetProblem(
                "Console-identiteit niet vastgezet",
                "Zoek beide consoles en wijs ze toe voordat live writes kunnen worden geactiveerd.");
            SelectedPageIndex = 1;
            return;
        }

        try
        {
            await configStore.SaveAsync(CreatePersistedSafeConfiguration(configuration));
            activeConfiguration = configuration;
            ClearProblem();
            await coordinator.StartAsync(
                configuration,
                confirmLiveInitialSync: false,
                CancellationToken.None);
            RaiseRunProperties();
            if (!configuration.Safety.DryRun)
            {
                await ReviewAndConfirmLiveInitialSyncAsync(configuration);
            }
        }
        catch (Exception exception)
        {
            SetProblem("Starten mislukt", exception.Message);
            diagnostics.Record(
                new DiagnosticEvent(
                    DiagnosticSeverity.Critical,
                    "START_FAILED",
                    exception.Message,
                    "Lifecycle",
                    DateTimeOffset.UtcNow));
        }

        RaiseRunProperties();
    }

    private async Task ReviewAndConfirmLiveInitialSyncAsync(AppConfiguration configuration)
    {
        const int MaximumPreviewChanges = 3;
        for (var attempt = 1; attempt <= MaximumPreviewChanges; attempt++)
        {
            var preview = coordinator.PendingInitialPreview ??
                throw new InvalidOperationException(
                    "De live synchronisatie wachtte zonder geldige verse verschilpreview.");
            var source = configuration.Direction == SyncDirection.FohToMonitor
                ? configuration.Foh
                : configuration.Monitor;
            var target = configuration.Direction == SyncDirection.FohToMonitor
                ? configuration.Monitor
                : configuration.Foh;
            var highRiskArmed =
                configuration.Safety.AllowHighRiskWrites &&
                configuration.Scopes.Any(ConfigValidator.IsHighRiskScope);
            var review = CreateLiveDiffReview(
                configuration,
                preview,
                source,
                target,
                highRiskArmed,
                previewWasReplaced: attempt > 1);
            var dialog = new LiveDiffReviewWindow(review);
            if (Application.Current?.MainWindow is { } owner)
            {
                dialog.Owner = owner;
            }

            if (dialog.ShowDialog() != true)
            {
                await coordinator.StopAsync(CancellationToken.None);
                return;
            }

            try
            {
                await coordinator.ConfirmInitialSyncAsync(CancellationToken.None);
                return;
            }
            catch (InitialSyncPreviewChangedException)
            {
                if (attempt == MaximumPreviewChanges)
                {
                    await coordinator.StopAsync(CancellationToken.None);
                    throw new InvalidOperationException(
                        "De consoles bleven tijdens bevestiging wijzigen. Live writes zijn niet geactiveerd.");
                }

                // A fresh preview is now pending; the next loop requires a new confirmation.
            }
        }

        throw new UnreachableException();
    }

    private LiveDiffReviewViewModel CreateLiveDiffReview(
        AppConfiguration configuration,
        InitialSyncPreviewSummary preview,
        WingEndpoint source,
        WingEndpoint target,
        bool highRiskArmed,
        bool previewWasReplaced)
    {
        var enabledScopes = string.Join(
            ", ",
            ScopeSelections
                .Where(static scope => scope.IsSelected)
                .Select(static scope => scope.DisplayName));
        var scopeCounts = preview.ChangesByScope
            .GroupBy(static pair => ToGlobalScopeDisplayName(pair.Key))
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => new LiveDiffScopeCountViewModel(
                group.Key,
                group.Sum(static pair => pair.Value)))
            .ToArray();
        var currentAudit = previewTokenAudit
            .ToArray()
            .Where(entry => entry.OccurredAt >= preview.GeneratedAt.AddSeconds(-1))
            .TakeLast(preview.TotalChanges)
            .Take(MaximumDialogTokenEntries)
            .Select(entry => new LiveDiffTokenViewModel(
                Enum.TryParse<SyncScope>(entry.ScopeName, ignoreCase: true, out var scope)
                    ? ToGlobalScopeDisplayName(scope)
                    : entry.ScopeName.ToUpperInvariant(),
                entry.SourceToken,
                entry.TargetToken,
                entry.Disposition))
            .ToArray();
        var previewPrefix = previewWasReplaced
            ? "Nieuwe preview na consolewijziging. "
            : string.Empty;
        return new LiveDiffReviewViewModel(
            $"{source.IpAddress} · S/N {source.ExpectedSerial}",
            $"{target.IpAddress} · S/N {target.ExpectedSerial}",
            $"Richting: {DirectionSummaryText}",
            $"{previewPrefix}Scopes: {enabledScopes}",
            $"Mappings: {configuration.Channels.InputChannels.Count} INPUT · " +
            $"{configuration.Channels.AuxChannels.Count} AUX",
            preview.TotalChanges,
            preview.ExecutableChanges,
            preview.BlockedChanges,
            preview.GeneratedAt,
            highRiskArmed,
            scopeCounts,
            currentAudit);
    }

    private static string ToGlobalScopeDisplayName(SyncScope scope) =>
        scope switch
        {
            SyncScope.Main1 or SyncScope.Main2 or SyncScope.Main3 or SyncScope.Main4 => "MAIN",
            SyncScope.Send => "BUS",
            SyncScope.Fdr => "FADER",
            _ => scope.ToString().ToUpperInvariant(),
        };

    private async Task StopAsync()
    {
        var completedDryRun =
            coordinator.Status.State == SyncCoordinatorState.RunningDryRun;
        await coordinator.StopAsync(CancellationToken.None);
        if (completedDryRun)
        {
            hasCompletedDryRun = true;
        }

        await cacheSink.FlushAsync();
        await RefreshOfflineCachePresentationAsync();
        RaiseSetupProperties();
        RaiseRunProperties();
    }

    private AppConfiguration BuildConfiguration()
    {
        var inputMappings = ChannelMappings
            .Where(static mapping => mapping.IsEnabled && !mapping.IsAux)
            .Select(static mapping => new InputChannelMapping(
                mapping.SourceChannel,
                mapping.TargetChannel))
            .ToArray();
        var auxMappings = ChannelMappings
            .Where(static mapping => mapping.IsEnabled && mapping.IsAux)
            .Select(static mapping => new AuxChannelMapping(
                mapping.SourceChannel,
                mapping.TargetChannel))
            .ToArray();
        var selectedScopes = ScopeSelections
            .Where(static selection => selection.IsSelected)
            .SelectMany(static selection => selection.Scopes)
            .Distinct()
            .ToArray();
        var fohSerial = ResolvePinnedSerial(SelectedFohWing, EditableFohIp, activeConfiguration?.Foh);
        var stageSerial = ResolvePinnedSerial(
            SelectedStageWing,
            EditableStageIp,
            activeConfiguration?.Monitor);
        return new AppConfiguration(
            new WingEndpoint(EditableFohIp.Trim(), ExpectedSerial: fohSerial),
            new WingEndpoint(EditableStageIp.Trim(), ExpectedSerial: stageSerial),
            SelectedDirection.Direction,
            InitialSync.RequireConfirmation,
            new SafetySettings(
                dryRun: IsDryRun,
                requireReadback: true,
                allowHighRiskWrites: AllowHighRiskWrites,
                stopOnVerificationFailure: true,
                floatTolerance: 0.0001F,
                echoSuppressionWindow: TimeSpan.FromSeconds(2)),
            selectedScopes,
            new ChannelMapping(inputMappings, auxMappings));
    }

    private static AppConfiguration CreatePersistedSafeConfiguration(
        AppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new AppConfiguration(
            configuration.Foh,
            configuration.Monitor,
            configuration.Direction,
            configuration.InitialSync,
            new SafetySettings(
                dryRun: true,
                requireReadback: true,
                allowHighRiskWrites: false,
                stopOnVerificationFailure: true,
                configuration.Safety.FloatTolerance,
                configuration.Safety.EchoSuppressionWindow),
            configuration.Scopes,
            configuration.Channels);
    }

    private void ApplyConfiguration(AppConfiguration configuration)
    {
        activeConfiguration = configuration;
        EditableFohIp = configuration.Foh.IpAddress;
        EditableStageIp = configuration.Monitor.IpAddress;
        SelectedDirection = DirectionOptions.FirstOrDefault(option =>
                option.Direction == configuration.Direction)
            ?? DirectionOptions[0];
        IsDryRun = true;
        VerifyEveryWrite = true;
        AllowHighRiskWrites = false;
        if (!configuration.Safety.DryRun || configuration.Safety.AllowHighRiskWrites)
        {
            ConfigurationSafetyNotice =
                "Veilige herstart: opgeslagen live- en hoog-risicotoestemming is ingetrokken; begin opnieuw met verbindingstest en droogloop.";
        }

        var selectedScopes = configuration.Scopes.ToHashSet();
        foreach (var scope in ScopeSelections)
        {
            scope.IsSelected = scope.Scopes.All(selectedScopes.Contains);
        }

        ChannelMappings.Clear();
        foreach (var mapping in configuration.Channels.InputChannels)
        {
            ChannelMappings.Add(
                new ChannelMappingViewModel(
                    mapping.Source,
                    mapping.Target,
                    $"CH {mapping.Source}"));
        }

        foreach (var mapping in configuration.Channels.AuxChannels)
        {
            ChannelMappings.Add(
                new ChannelMappingViewModel(
                    mapping.Source,
                    mapping.Target,
                    $"AUX {mapping.Source}",
                    isAux: true));
        }

        AttachMappingHandlers();
    }

    private void ApplySafeDefaultScopes()
    {
        var safe = AppConfiguration.SafeDefaultScopes.ToHashSet();
        foreach (var scope in ScopeSelections)
        {
            scope.IsSelected = scope.Scopes.All(safe.Contains);
        }
    }

    private void AutoMap()
    {
        var overwritesCustomMapping =
            ChannelMappings.Count > 1 ||
            ChannelMappings.Count == 1 &&
            (ChannelMappings[0].IsAux ||
             ChannelMappings[0].SourceChannel != 1 ||
             ChannelMappings[0].TargetChannel != 1);
        if (overwritesCustomMapping &&
            MessageBox.Show(
                Application.Current?.MainWindow,
                "De huidige kanaalmapping wordt vervangen door INPUT 1 → 1 tot en met 40 → 40.\n\nDoorgaan?",
                "Standaardmapping invullen",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        ChannelMappings.Clear();
        for (var channel = WingChannelLimits.FirstInput;
             channel <= WingChannelLimits.LastInput;
             channel++)
        {
            ChannelMappings.Add(new ChannelMappingViewModel(channel, channel, $"CH {channel}"));
        }

        AttachMappingHandlers();
        NotifyTopologyChanged();
    }

    private void AddMapping()
    {
        var usedSources = ChannelMappings
            .Where(static mapping => !mapping.IsAux)
            .Select(static mapping => mapping.SourceChannel)
            .ToHashSet();
        var next = Enumerable.Range(WingChannelLimits.FirstInput, WingChannelLimits.LastInput)
            .FirstOrDefault(channel => !usedSources.Contains(channel));
        if (next == 0)
        {
            next = WingChannelLimits.LastInput;
        }

        var mapping = new ChannelMappingViewModel(next, next, $"CH {next}");
        mapping.PropertyChanged += OnEditableConfigurationChanged;
        ChannelMappings.Add(mapping);
        SelectedMapping = mapping;
        NotifyTopologyChanged();
    }

    private void AddAuxMapping()
    {
        var usedSources = ChannelMappings
            .Where(static mapping => mapping.IsAux)
            .Select(static mapping => mapping.SourceChannel)
            .ToHashSet();
        var next = Enumerable.Range(WingChannelLimits.FirstAux, WingChannelLimits.LastAux)
            .FirstOrDefault(channel => !usedSources.Contains(channel));
        if (next == 0)
        {
            next = WingChannelLimits.LastAux;
        }

        var mapping = new ChannelMappingViewModel(
            next,
            next,
            $"AUX {next}",
            isAux: true);
        mapping.PropertyChanged += OnEditableConfigurationChanged;
        ChannelMappings.Add(mapping);
        SelectedMapping = mapping;
        NotifyTopologyChanged();
    }

    private void RemoveSelectedMapping()
    {
        if (SelectedMapping is null)
        {
            return;
        }

        SelectedMapping.PropertyChanged -= OnEditableConfigurationChanged;
        ChannelMappings.Remove(SelectedMapping);
        SelectedMapping = null;
        NotifyTopologyChanged();
    }

    private void AttachMappingHandlers()
    {
        foreach (var mapping in ChannelMappings)
        {
            mapping.PropertyChanged -= OnEditableConfigurationChanged;
            mapping.PropertyChanged += OnEditableConfigurationChanged;
        }
    }

    private void OnEditableConfigurationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelMappingViewModel.ValidationText))
        {
            return;
        }

        NotifyTopologyChanged();
    }

    private void NotifyConfigurationChanged()
    {
        ValidateEditableConfiguration();
        RaiseRunProperties();
        RaiseSetupProperties();
    }

    private void NotifyTopologyChanged()
    {
        if (!suppressLiveSafetyReset)
        {
            connectionTestSucceeded = false;
            hasCompletedDryRun = false;
            var resetToSafeMode = false;
            if (!isDryRun)
            {
                SetProperty(ref isDryRun, true, nameof(IsDryRun));
                resetToSafeMode = true;
            }

            if (allowHighRiskWrites)
            {
                SetProperty(
                    ref allowHighRiskWrites,
                    false,
                    nameof(AllowHighRiskWrites));
                resetToSafeMode = true;
            }

            ConfigurationSafetyNotice = resetToSafeMode
                ? "Configuratie gewijzigd: live-toestemming is ingetrokken en droogloop is opnieuw ingeschakeld."
                : "Configuratie gewijzigd: voer de verbindingstest en droogloop opnieuw uit.";
            OnPropertyChanged(nameof(StartButtonText));
            OnPropertyChanged(nameof(RunModeText));
            OnPropertyChanged(nameof(RunModeBackground));
            OnPropertyChanged(nameof(RunModeForeground));
            OnPropertyChanged(nameof(SafetySummary));
        }

        ValidateEditableConfiguration();
        RaiseRunProperties();
        RaiseSetupProperties();
    }

    private async Task RunExclusiveAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!await uiOperationGate.WaitAsync(0))
        {
            return;
        }

        IsUiBusy = true;
        try
        {
            await operation();
        }
        finally
        {
            uiOperationGate.Release();
            IsUiBusy = false;
        }
    }

    private void ValidateEditableConfiguration()
    {
        if (disposed)
        {
            return;
        }

        var configuration = BuildConfiguration();
        var validation = ConfigValidator.Validate(configuration);
        editableConfigurationIsValid = validation.IsValid;
        ApplyValidation(validation);
        ValidateMappingRows();
        SaveConfigurationCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        RaiseSetupProperties();
    }

    private void ValidateMappingRows()
    {
        var enabled = ChannelMappings.Where(static mapping => mapping.IsEnabled).ToArray();
        var duplicateSources = enabled
            .GroupBy(static mapping => (mapping.IsAux, mapping.SourceChannel))
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet();
        var duplicateTargets = enabled
            .GroupBy(static mapping => (mapping.IsAux, mapping.TargetChannel))
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet();
        foreach (var mapping in ChannelMappings)
        {
            var maximum = mapping.IsAux
                ? WingChannelLimits.LastAux
                : WingChannelLimits.LastInput;
            mapping.ValidationText =
                mapping.SourceChannel < WingChannelLimits.FirstInput ||
                mapping.SourceChannel > maximum ||
                mapping.TargetChannel < WingChannelLimits.FirstInput ||
                mapping.TargetChannel > maximum
                    ? $"Buiten bereik 1–{maximum}"
                    : duplicateSources.Contains((mapping.IsAux, mapping.SourceChannel))
                        ? "Dubbele bron"
                        : duplicateTargets.Contains((mapping.IsAux, mapping.TargetChannel))
                            ? "Dubbel doel"
                            : "OK";
        }
    }

    private void ApplyValidation(ConfigurationValidationResult validation)
    {
        ValidationDetails = string.Join(
            Environment.NewLine,
            validation.Issues
                .Select(static issue => $"• {issue.Message}")
                .Distinct(StringComparer.Ordinal)
                .Take(8));
        var errors = validation.Issues
            .Where(static issue => issue.Severity == ValidationSeverity.Error)
            .ToArray();
        var warnings = validation.Issues
            .Where(static issue => issue.Severity == ValidationSeverity.Warning)
            .ToArray();
        if (errors.Length > 0)
        {
            ValidationSummary = $"{errors.Length} fout(en): {errors[0].Message}";
            ValidationBrush = Red;
        }
        else if (warnings.Length > 0)
        {
            ValidationSummary = $"{warnings.Length} waarschuwing(en): {warnings[0].Message}";
            ValidationBrush = Orange;
        }
        else
        {
            ValidationSummary = "Configuratie is geldig.";
            ValidationBrush = Green;
        }
    }

    private void OnCoordinatorStatusChanged(object? sender, SyncCoordinatorStatus newStatus) =>
        Dispatch(() =>
        {
            OverallStatusText = newStatus.State switch
            {
                SyncCoordinatorState.Stopped => "Niet gestart",
                SyncCoordinatorState.RunningDryRun => "Droogloop actief",
                SyncCoordinatorState.RunningLive => "Live actief",
                SyncCoordinatorState.ApplyingLive => "Live toepassen",
                SyncCoordinatorState.Reconnecting => "Opnieuw verbinden",
                SyncCoordinatorState.Paused => "Gepauzeerd",
                SyncCoordinatorState.Faulted => "Fout",
                _ => "Bezig",
            };
            OverallStatusBrush = newStatus.State switch
            {
                SyncCoordinatorState.RunningLive => Green,
                SyncCoordinatorState.RunningDryRun => Blue,
                SyncCoordinatorState.Paused => Orange,
                SyncCoordinatorState.Faulted => Red,
                SyncCoordinatorState.Stopped => Gray,
                _ => Orange,
            };
            FlowStatusText = newStatus.State switch
            {
                SyncCoordinatorState.RunningDryRun => "PREVIEW",
                SyncCoordinatorState.RunningLive => "SYNCHRONISEERT",
                SyncCoordinatorState.ApplyingLive => "SCHRIJFT + LEEST TERUG",
                SyncCoordinatorState.Reconnecting => "GEPAUZEERD",
                SyncCoordinatorState.Paused => "GEBLOKKEERD",
                _ => newStatus.State.ToString().ToUpperInvariant(),
            };
            FlowBrush = OverallStatusBrush;
            var connected = newStatus.State is SyncCoordinatorState.Snapshotting
                or SyncCoordinatorState.AwaitingConfirmation
                or SyncCoordinatorState.ApplyingLive
                or SyncCoordinatorState.RunningDryRun
                or SyncCoordinatorState.RunningLive;
            var reconnecting = newStatus.State is SyncCoordinatorState.Connecting
                or SyncCoordinatorState.Reconnecting;
            SetConsoleStatus(
                connected ? "Verbonden" : reconnecting ? "Verbinden…" : "Offline",
                connected ? Green : reconnecting ? Orange : Gray,
                connected ? GreenSoft : reconnecting ? OrangeSoft : GraySoft);
            var snapshotReady = newStatus.State is
                SyncCoordinatorState.AwaitingConfirmation or
                SyncCoordinatorState.ApplyingLive or
                SyncCoordinatorState.RunningDryRun or
                SyncCoordinatorState.RunningLive;
            if (snapshotReady)
            {
                fohLastData = FormatLastSeen(newStatus.ChangedAt);
                stageLastData = FormatLastSeen(newStatus.ChangedAt);
                FohCacheSummary = "Live snapshot · cache wordt lokaal bijgewerkt";
                StageCacheSummary = "Live snapshot · cache wordt lokaal bijgewerkt";
                RefreshConsolePresentation();
            }

            CacheStatus = connected ? "Live vers" : "Offline cache";
            CacheAge = connected
                ? $"snapshot {FormatLastSeen(newStatus.ChangedAt)}"
                : "laatst bekende toestand; nooit automatisch teruggeschreven";

            if (newStatus.State is SyncCoordinatorState.Paused or SyncCoordinatorState.Faulted)
            {
                SetProblem("Synchronisatie gepauzeerd", newStatus.Detail);
            }
            else if (
                (newStatus.State is
                    SyncCoordinatorState.RunningDryRun or SyncCoordinatorState.RunningLive) &&
                PrimaryProblemTitle == "Synchronisatie gepauzeerd")
            {
                ClearProblem();
            }

            RaiseRunProperties();
            RaiseSetupProperties();
        });

    private void OnCoordinatorMetricsChanged(object? sender, SyncMetrics metrics) =>
        Dispatch(() =>
        {
            SyncedCount = metrics.SynchronizedWrites;
            PreviewedCount = metrics.PreviewedWrites;
            BlockedCount = metrics.BlockedWrites;
            QueueDepth = metrics.QueueDepth;
            P95Latency = metrics.P95LatencyMilliseconds <= 0
                ? "—"
                : $"{metrics.P95LatencyMilliseconds:0} ms";
        });

    private void OnDiagnosticRecorded(object? sender, DiagnosticEvent diagnosticEvent)
    {
        CapturePreviewAuditEntry(diagnosticEvent);
        Dispatch(() =>
        {
            var details = diagnosticEvent.Properties is null
                ? string.Empty
                : string.Join(
                    " · ",
                    diagnosticEvent.Properties
                        .Where(static pair => pair.Key is not "value")
                        .Take(4)
                        .Select(static pair => $"{pair.Key}={pair.Value}"));
            activityItems.Insert(
                0,
                new ActivityItemViewModel(
                    diagnosticEvent.OccurredAt,
                    diagnosticEvent.Severity,
                    diagnosticEvent.Source,
                    diagnosticEvent.Message,
                    details));
            while (activityItems.Count > 2_000)
            {
                activityItems.RemoveAt(activityItems.Count - 1);
            }

            if (diagnosticEvent.Severity >= DiagnosticSeverity.Error)
            {
                SetProblem(diagnosticEvent.Code, diagnosticEvent.Message);
            }
        });
    }

    private void CapturePreviewAuditEntry(DiagnosticEvent diagnosticEvent)
    {
        if (diagnosticEvent.Code is not ("PARAMETER_PREVIEW" or "PARAMETER_BLOCKED") ||
            diagnosticEvent.Properties is null ||
            !diagnosticEvent.Properties.TryGetValue("sourceToken", out var sourceToken) ||
            !diagnosticEvent.Properties.TryGetValue("targetToken", out var targetToken) ||
            !diagnosticEvent.Properties.TryGetValue("scope", out var scope))
        {
            return;
        }

        previewTokenAudit.Enqueue(
            new PreviewTokenAuditEntry(
                diagnosticEvent.OccurredAt,
                scope,
                sourceToken,
                targetToken,
                diagnosticEvent.Code == "PARAMETER_BLOCKED"
                    ? "Geblokkeerd"
                    : "Uitvoerbaar"));
        var count = Interlocked.Increment(ref previewTokenAuditCount);
        while (count > MaximumPreviewAuditEntries &&
               previewTokenAudit.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref previewTokenAuditCount);
        }
    }

    private bool FilterActivity(object item)
    {
        if (item is not ActivityItemViewModel activity)
        {
            return false;
        }

        return SelectedActivityFilter switch
        {
            "Problemen" => activity.Severity >= DiagnosticSeverity.Warning,
            "Writes" => activity.Message.Contains('→', StringComparison.Ordinal),
            "Netwerk" => activity.Source.Contains("Netwerk", StringComparison.OrdinalIgnoreCase)
                || activity.Message.Contains("connect", StringComparison.OrdinalIgnoreCase)
                || activity.Message.Contains("WING", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private void ClearActivity()
    {
        activityItems.Clear();
        diagnostics.Record(
            new DiagnosticEvent(
                DiagnosticSeverity.Information,
                "ACTIVITY_VIEW_CLEARED",
                "De in-memory activiteitweergave is gewist; logbestanden zijn behouden.",
                "UI",
                DateTimeOffset.UtcNow));
    }

    private async Task ExportSupportBundleAsync()
    {
        const string privacyNotice =
            "WingSync maakt een diagnosepakket met configuratiestructuur, statuscodes, " +
            "tijdstippen en loggebeurtenissen.\n\n" +
            "IP-adressen, serienummers, lokale gebruikerspaden en WING-parameterwaarden " +
            "worden standaard geredigeerd. Cachebestanden en consolesnapshots worden niet " +
            "opgenomen.\n\nDoorgaan?";
        var owner = Application.Current?.MainWindow;
        var confirmation = owner is null
            ? MessageBox.Show(
                privacyNotice,
                "WingSync supportpakket maken",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.No)
            : MessageBox.Show(
                owner,
                privacyNotice,
                "WingSync supportpakket maken",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var supportDirectory = Path.Combine(dataDirectory, "support");
        Directory.CreateDirectory(supportDirectory);
        var bundlePath = Path.Combine(
            supportDirectory,
            $"WingSync-support-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.zip");
        await cacheSink.FlushAsync();
        await diagnostics.FlushAsync();

        await SupportBundleExporter.ExportAsync(
            new SupportBundleExportRequest(
                bundlePath,
                configStore.FilePath,
                Path.Combine(dataDirectory, "logs"),
                FullVersionText,
                coordinator.Status.State.ToString(),
                coordinator.Status.Detail,
                diagnostics.DroppedEntries));

        diagnostics.Record(
            new DiagnosticEvent(
                DiagnosticSeverity.Information,
                "SUPPORT_BUNDLE_EXPORTED",
                "Een geredigeerd supportpakket is gemaakt.",
                "Support",
                DateTimeOffset.UtcNow));
        const string completedTitle = "WingSync supportpakket";
        var completedMessage =
            $"Geredigeerd supportpakket opgeslagen:\n{bundlePath}\n\n" +
            "IP-adressen, serienummers en WING-parameterwaarden zijn verwijderd.";
        if (owner is null)
        {
            MessageBox.Show(
                completedMessage,
                completedTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                owner,
                completedMessage,
                completedTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OpenDataDirectory()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{dataDirectory}\"",
            UseShellExecute = true,
        };
        using var process = Process.Start(startInfo);
    }

    private async Task RebuildCacheAsync()
    {
        if (IsRunning)
        {
            SetProblem("Cache is in gebruik", "Stop de synchronisatie voordat je de cache vernieuwt.");
            return;
        }

        var quarantine = Path.Combine(
            dataDirectory,
            $"cache-reset-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        await cacheSink.ResetAsync(quarantine);
        CacheStatus = "Vernieuwd";
        CacheAge = $"vorige cache bewaard als {Path.GetFileName(quarantine)}";
        FohCacheSummary = "Geen lokale snapshot";
        StageCacheSummary = "Geen lokale snapshot";
        fohLastData = "nog geen snapshot";
        stageLastData = "nog geen snapshot";
        RefreshConsolePresentation();
    }

    private void CopyDiagnostics()
    {
        var diagnosticsText =
            $"{FullVersionText}\n" +
            $"Status: {coordinator.Status.State}\n" +
            $"Detail: {coordinator.Status.Detail}\n" +
            $"FOH: {FohName} {FohIp} {FohFirmware}\n" +
            $"Podium: {StageName} {StageIp} {StageFirmware}\n" +
            $"Mappings: {ChannelMappings.Count(static mapping => mapping.IsEnabled)}\n" +
            $"Scopes: {string.Join(",", ScopeSelections.Where(static scope => scope.IsSelected).Select(static scope => scope.DisplayName))}\n" +
            $"Dropped logs: {diagnostics.DroppedEntries}";
        Clipboard.SetText(diagnosticsText);
    }

    private void HandleCommandError(Exception exception)
    {
        if (disposed)
        {
            return;
        }

        SetProblem("Actie mislukt", exception.Message);
        diagnostics.Record(
            new DiagnosticEvent(
                DiagnosticSeverity.Error,
                "UI_COMMAND_FAILED",
                exception.Message,
                "UI",
                DateTimeOffset.UtcNow));
    }

    public async Task<bool> EmergencyStopAsync(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (disposed)
        {
            return true;
        }

        try
        {
            diagnostics.Record(
                new DiagnosticEvent(
                    DiagnosticSeverity.Critical,
                    "UNHANDLED_UI_EXCEPTION",
                    exception.Message,
                    "UI",
                    DateTimeOffset.UtcNow));
            await coordinator.StopAsync(CancellationToken.None);
            SetProblem(
                "Noodstop uitgevoerd",
                "Een onverwachte applicatiefout is opgevangen. Beide WAPI-verbindingen zijn gesloten.");
            RaiseRunProperties();
            return true;
        }
        catch (Exception stopException)
        {
            SetProblem(
                "Noodstop niet bevestigd",
                $"De verbindingen konden niet gecontroleerd worden gesloten: {stopException.Message}");
            return false;
        }
    }

    private void SetProblem(string title, string detail)
    {
        HasBlockingProblems = true;
        PrimaryProblemTitle = title;
        PrimaryProblemDetail = detail;
    }

    private void ClearProblem()
    {
        HasBlockingProblems = false;
        PrimaryProblemTitle = string.Empty;
        PrimaryProblemDetail = string.Empty;
    }

    private async Task RefreshOfflineCachePresentationSafeAsync()
    {
        try
        {
            await RefreshOfflineCachePresentationAsync();
        }
        catch (ObjectDisposedException) when (disposed)
        {
            // A selection callback can finish while the window is closing.
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostics.Record(
                new DiagnosticEvent(
                    DiagnosticSeverity.Warning,
                    "CACHE_DISPLAY_LOAD_FAILED",
                    exception.Message,
                    "Cache",
                    DateTimeOffset.UtcNow));
        }
    }

    private async Task RefreshOfflineCachePresentationAsync()
    {
        var generation = Interlocked.Increment(ref cachePresentationGeneration);
        var fohIdentity = SelectedFohWing?.Wing;
        var stageIdentity = SelectedStageWing?.Wing;
        var fohLoadTask = LoadCacheForDisplayAsync(
            fohIdentity,
            ResolveConfiguredSerial(activeConfiguration?.Foh, EditableFohIp));
        var stageLoadTask = LoadCacheForDisplayAsync(
            stageIdentity,
            ResolveConfiguredSerial(activeConfiguration?.Monitor, EditableStageIp));
        await Task.WhenAll(fohLoadTask, stageLoadTask);
        if (generation != Interlocked.Read(ref cachePresentationGeneration) ||
            disposed ||
            IsRunning)
        {
            return;
        }

        var fohPresentation = CreateCachePresentation(await fohLoadTask);
        var stagePresentation = CreateCachePresentation(await stageLoadTask);
        FohCacheSummary = fohPresentation.Summary;
        StageCacheSummary = stagePresentation.Summary;
        fohLastData = fohPresentation.LastData;
        stageLastData = stagePresentation.LastData;
        var available = (fohPresentation.HasSnapshot ? 1 : 0) +
            (stagePresentation.HasSnapshot ? 1 : 0);
        CacheStatus = available switch
        {
            2 => "2 consoles lokaal",
            1 => "1 console lokaal",
            _ => "Cache leeg",
        };
        var latest = new[]
            {
                fohPresentation.CapturedAt,
                stagePresentation.CapturedAt,
            }
            .Where(static timestamp => timestamp is not null)
            .Max();
        CacheAge = latest is null
            ? "nog geen live snapshot"
            : $"laatste lokale snapshot {FormatLastSeen(latest)} · alleen voor weergave";
        RefreshConsolePresentation();
    }

    private Task<WingStateCacheLoadResult> LoadCacheForDisplayAsync(
        DiscoveredWing? discoveredIdentity,
        string? expectedSerial)
    {
        if (discoveredIdentity is not null)
        {
            return cacheSink.LoadForDisplayAsync(discoveredIdentity);
        }

        return string.IsNullOrWhiteSpace(expectedSerial)
            ? Task.FromResult(
                new WingStateCacheLoadResult(
                    WingStateCacheLoadStatus.Missing,
                    CacheFreshness.Unknown))
            : cacheSink.LoadForDisplayBySerialAsync(expectedSerial);
    }

    private static CachePresentation CreateCachePresentation(
        WingStateCacheLoadResult load)
    {
        if (load.Snapshot is not null &&
            load.Status is
                WingStateCacheLoadStatus.Loaded or
                WingStateCacheLoadStatus.RecoveredFromBackup)
        {
            var recovered = load.Status == WingStateCacheLoadStatus.RecoveredFromBackup
                ? " · backup hersteld"
                : string.Empty;
            return new CachePresentation(
                $"{load.Snapshot.Values.Count} waarden · offline/stale{recovered}",
                FormatLastSeen(load.Snapshot.CapturedAtUtc),
                load.Snapshot.CapturedAtUtc,
                true);
        }

        var summary = load.Status switch
        {
            WingStateCacheLoadStatus.Missing => "Geen lokale snapshot",
            WingStateCacheLoadStatus.CorruptQuarantined => "Beschadigde cache geïsoleerd",
            WingStateCacheLoadStatus.IdentityMismatchQuarantined =>
                "Cache-identiteit kwam niet overeen; geïsoleerd",
            WingStateCacheLoadStatus.UnsupportedSchema => "Cacheversie niet ondersteund",
            WingStateCacheLoadStatus.Failed => "Cache kon niet worden gelezen",
            _ => "Geen bruikbare lokale snapshot",
        };
        return new CachePresentation(summary, "nog geen snapshot", null, false);
    }

    private void SetConsoleStatus(string text, Brush foreground, Brush background)
    {
        FohStatusText = text;
        FohStatusForeground = foreground;
        FohStatusBackground = background;
        StageStatusText = text;
        StageStatusForeground = foreground;
        StageStatusBackground = background;
    }

    private void RefreshConsolePresentation()
    {
        OnPropertyChanged(nameof(FohName));
        OnPropertyChanged(nameof(FohIp));
        OnPropertyChanged(nameof(FohModel));
        OnPropertyChanged(nameof(FohFirmware));
        OnPropertyChanged(nameof(FohSerial));
        OnPropertyChanged(nameof(FohIdentityText));
        OnPropertyChanged(nameof(FohLastSeen));
        OnPropertyChanged(nameof(FohCacheSummary));
        OnPropertyChanged(nameof(StageName));
        OnPropertyChanged(nameof(StageIp));
        OnPropertyChanged(nameof(StageModel));
        OnPropertyChanged(nameof(StageFirmware));
        OnPropertyChanged(nameof(StageSerial));
        OnPropertyChanged(nameof(StageIdentityText));
        OnPropertyChanged(nameof(StageLastSeen));
        OnPropertyChanged(nameof(StageCacheSummary));
        RaiseSetupProperties();
        RaiseRunProperties();
    }

    private void RaiseRunProperties()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanPrimaryAction));
        OnPropertyChanged(nameof(CanSelectLiveMode));
        OnPropertyChanged(nameof(CanCloseImmediately));
        OnPropertyChanged(nameof(IsUiBusy));
        OnPropertyChanged(nameof(IsEditorEnabled));
        OnPropertyChanged(nameof(CanArmHighRisk));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(StartButtonHelpText));
        OnPropertyChanged(nameof(RunModeText));
        OnPropertyChanged(nameof(RunModeBackground));
        OnPropertyChanged(nameof(RunModeForeground));
        OnPropertyChanged(nameof(SafetySummary));
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        DiscoverCommand.RaiseCanExecuteChanged();
        TestConnectionsCommand.RaiseCanExecuteChanged();
        SaveConfigurationCommand.RaiseCanExecuteChanged();
        AutoMapCommand.RaiseCanExecuteChanged();
        AddMappingCommand.RaiseCanExecuteChanged();
        AddAuxMappingCommand.RaiseCanExecuteChanged();
        RemoveMappingsCommand.RaiseCanExecuteChanged();
        RebuildCacheCommand.RaiseCanExecuteChanged();
        ExportSupportBundleCommand.RaiseCanExecuteChanged();
    }

    private void RaiseSetupProperties()
    {
        OnPropertyChanged(nameof(SetupProgressText));
        OnPropertyChanged(nameof(SetupProgressIndicatorText));
        OnPropertyChanged(nameof(SetupProgressAccessibleText));
        OnPropertyChanged(nameof(SetupProgressBackground));
        OnPropertyChanged(nameof(SetupProgressForeground));
        OnPropertyChanged(nameof(NextStepText));
    }

    private void RaiseDirectionProperties()
    {
        OnPropertyChanged(nameof(DirectionSummaryText));
        OnPropertyChanged(nameof(FohRoleCaption));
        OnPropertyChanged(nameof(StageRoleCaption));
        OnPropertyChanged(nameof(FohEditorCaption));
        OnPropertyChanged(nameof(StageEditorCaption));
        OnPropertyChanged(nameof(FlowArrowText));
    }

    private DiscoveredWingViewModel? FindSelection(string? serial, string ipAddress)
    {
        var identity = PinnedWingSelection.Find(
            DiscoveredWings.Select(static item => item.Wing),
            serial,
            ipAddress);
        return identity is null
            ? null
            : DiscoveredWings.FirstOrDefault(item => ReferenceEquals(item.Wing, identity));
    }

    private DiscoveredWingViewModel? FindByRoleHint(params string[] hints) =>
        DiscoveredWings.FirstOrDefault(item =>
            hints.Any(hint =>
                item.Wing.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
                item.Wing.Model.Contains(hint, StringComparison.OrdinalIgnoreCase)));

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await disposeGate.WaitAsync();
        try
        {
            if (disposed)
            {
                return;
            }

            await uiOperationGate.WaitAsync();
            try
            {
                coordinator.StatusChanged -= OnCoordinatorStatusChanged;
                coordinator.MetricsChanged -= OnCoordinatorMetricsChanged;
                diagnostics.DiagnosticRecorded -= OnDiagnosticRecorded;
                await coordinator.DisposeAsync();
                await cacheSink.DisposeAsync();
                configStore.Dispose();
                await diagnostics.DisposeAsync();
                disposed = true;
            }
            finally
            {
                uiOperationGate.Release();
            }
        }
        finally
        {
            disposeGate.Release();
        }
    }

    private async Task SafeDisconnectAndDisposeAsync(IWingSession? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            await session.DisconnectAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // A failed test cleanup must not mask the primary connection result.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            diagnostics.Record(
                new DiagnosticEvent(
                    DiagnosticSeverity.Warning,
                    "CONNECTION_TEST_CLEANUP_FAILED",
                    exception.Message,
                    "Netwerk",
                    DateTimeOffset.UtcNow));
        }

        try
        {
            await session.DisposeAsync();
        }
#pragma warning disable CA1031 // A process adapter must be given best-effort independent cleanup.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            diagnostics.Record(
                new DiagnosticEvent(
                    DiagnosticSeverity.Warning,
                    "CONNECTION_TEST_DISPOSE_FAILED",
                    exception.Message,
                    "Netwerk",
                    DateTimeOffset.UtcNow));
        }
    }

    private static IEnumerable<ScopeSelectionViewModel> CreateScopeSelections()
    {
        var definitions =
            new (SyncScope[] Scopes, string Name, string Description, string AutomationId)[]
        {
            ([SyncScope.Cust], "CUST", "Naam, kleur, icoon en licht", "Scope-Cust"),
            ([SyncScope.Tags], "TAGS", "Tags en DCA-/mutetoewijzingen", "Scope-Tags"),
            ([SyncScope.Conn], "CONN", "Bron A/B en inputselectie", "Scope-Conn"),
            ([SyncScope.In], "IN", "Trim, balance en fase", "Scope-In"),
            ([SyncScope.Filter], "FILTER", "HPF, LPF, tilt en all-pass", "Scope-Filter"),
            ([SyncScope.Delay], "DELAY", "Inputdelay", "Scope-Delay"),
            ([SyncScope.Gate], "GATE", "Gate en sidechain", "Scope-Gate"),
            ([SyncScope.Dyn], "DYN", "Dynamics en sidechain", "Scope-Dyn"),
            ([SyncScope.Pre], "PRE", "Pre-inserttoewijzing", "Scope-Pre"),
            ([SyncScope.Post], "POST", "Post-inserttoewijzing", "Scope-Post"),
            ([SyncScope.Eq], "EQ", "EQ en pre-send EQ", "Scope-Eq"),
            ([SyncScope.Pan], "PAN", "Pan en width", "Scope-Pan"),
            (
                [SyncScope.Main1, SyncScope.Main2, SyncScope.Main3, SyncScope.Main4],
                "MAIN",
                "Alle vier main sends: levels, aan/uit en pre/post",
                "Scope-Main"),
            ([SyncScope.Send], "BUS", "Bus- en matrixsends", "Scope-Bus"),
            ([SyncScope.Fdr], "FADER", "Kanaalfader", "Scope-Fader"),
            ([SyncScope.Mute], "MUTE", "Kanaalmute", "Scope-Mute"),
            ([SyncScope.Config], "CONFIG", "Procesvolgorde en tap", "Scope-Config"),
        };
        var safeDefaults = AppConfiguration.SafeDefaultScopes.ToHashSet();
        return definitions.Select(definition =>
            new ScopeSelectionViewModel(
                definition.Scopes,
                definition.Name,
                definition.Description,
                definition.Scopes.Any(ConfigValidator.IsHighRiskScope),
                definition.Scopes.All(safeDefaults.Contains),
                definition.AutomationId));
    }

    private static string? ResolvePinnedSerial(
        DiscoveredWingViewModel? selected,
        string ipAddress,
        WingEndpoint? previous)
    {
        if (selected is not null &&
            selected.Wing.IpAddress.Equals(ipAddress.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return selected.Wing.SerialNumber;
        }

        return previous is not null &&
            previous.IpAddress.Equals(ipAddress.Trim(), StringComparison.OrdinalIgnoreCase)
                ? previous.ExpectedSerial
                : null;
    }

    private bool BothIdentitiesPinned =>
        HasPinnedIdentity(SelectedFohWing, EditableFohIp) &&
        HasPinnedIdentity(SelectedStageWing, EditableStageIp) &&
        !string.Equals(
            SelectedFohWing!.Wing.SerialNumber,
            SelectedStageWing!.Wing.SerialNumber,
            StringComparison.OrdinalIgnoreCase);

    private int CompletedSetupSteps
    {
        get
        {
            var completed = BothIdentitiesPinned ? 1 : 0;
            if (editableConfigurationIsValid &&
                ChannelMappings.Any(static mapping => mapping.IsEnabled) &&
                ScopeSelections.Any(static scope => scope.IsSelected))
            {
                completed++;
            }

            if (connectionTestSucceeded)
            {
                completed++;
            }

            if (hasCompletedDryRun)
            {
                completed++;
            }

            return completed;
        }
    }

    private static bool HasPinnedIdentity(
        DiscoveredWingViewModel? selected,
        string ipAddress) =>
        selected is not null &&
        !string.IsNullOrWhiteSpace(selected.Wing.SerialNumber) &&
        selected.Wing.IpAddress.Equals(
            ipAddress.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static string ResolveVisibleSerial(
        DiscoveredWingViewModel? selected,
        string ipAddress,
        WingEndpoint? configured)
    {
        if (HasPinnedIdentity(selected, ipAddress))
        {
            return selected!.Wing.SerialNumber;
        }

        var expectedSerial = ResolveConfiguredSerial(configured, ipAddress);
        return expectedSerial is null
            ? "NIET BEVESTIGD"
            : $"{expectedSerial} · VERWACHT, OFFLINE NIET BEVESTIGD";
    }

    private static string ResolveIdentityStatus(
        DiscoveredWingViewModel? selected,
        string ipAddress,
        WingEndpoint? configured)
    {
        if (HasPinnedIdentity(selected, ipAddress))
        {
            return "Serienummerpin via discovery bevestigd";
        }

        return ResolveConfiguredSerial(configured, ipAddress) is null
            ? "Geen serienummerpin voor dit IP"
            : "Opgeslagen pin · offline niet bevestigd";
    }

    private static string? ResolveConfiguredSerial(
        WingEndpoint? configured,
        string ipAddress) =>
        configured is not null &&
        configured.IpAddress.Equals(ipAddress.Trim(), StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(configured.ExpectedSerial)
            ? configured.ExpectedSerial.Trim()
            : null;

    private static bool SamePhysicalConsole(
        DiscoveredWingViewModel? left,
        DiscoveredWingViewModel? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Wing.SerialNumber.Equals(
                right.Wing.SerialNumber,
                StringComparison.OrdinalIgnoreCase) &&
            left.Wing.IpAddress.Equals(
                right.Wing.IpAddress,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatLastSeen(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "—"
            : timestamp.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    private static string GetDataDirectory(string[] arguments)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index].Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(arguments[index + 1]);
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WingSync");
    }

    private static IReadOnlyList<DiscoveredWing> CreateDemoIdentities() =>
    [
        new DiscoveredWing(
            "127.10.0.10",
            "DEMO-FOH",
            "wing-fullsize",
            "DEMO-FOH-0001",
            "3.1-demo",
            DateTimeOffset.UtcNow),
        new DiscoveredWing(
            "127.10.0.11",
            "DEMO-PODIUM",
            "wing-rack",
            "DEMO-MON-0001",
            "3.1-demo",
            DateTimeOffset.UtcNow),
    ];

    private static Dictionary<string, WingValue> CreateDemoState(bool isTarget)
    {
        var state = new Dictionary<string, WingValue>(StringComparer.Ordinal)
        {
            ["/$syscfg/$serial"] = WingValue.FromString(
                isTarget ? "DEMO-MON-0001" : "DEMO-FOH-0001"),
        };
        for (var channel = 1; channel <= WingChannelLimits.LastInput; channel++)
        {
            var prefix = $"/ch/{channel}";
            state[$"{prefix}/name"] = WingValue.FromString($"CH {channel:00}");
            state[$"{prefix}/col"] = WingValue.FromInt32((channel % 8) + 1);
            state[$"{prefix}/icon"] = WingValue.FromInt32(1);
            state[$"{prefix}/led"] = WingValue.FromInt32(1);
            state[$"{prefix}/flt/on"] = WingValue.FromInt32(0);
            state[$"{prefix}/flt/mdl"] = WingValue.FromString("STD");
            state[$"{prefix}/flt/1"] = WingValue.FromFloat(80F);
            state[$"{prefix}/in/set/dlyon"] = WingValue.FromInt32(0);
            state[$"{prefix}/in/set/dly"] = WingValue.FromFloat(0F);
            state[$"{prefix}/gate/on"] = WingValue.FromInt32(0);
            state[$"{prefix}/gate/mdl"] = WingValue.FromString("GATE");
            state[$"{prefix}/gate/1"] = WingValue.FromFloat(-40F);
            state[$"{prefix}/eq/on"] = WingValue.FromInt32(0);
            state[$"{prefix}/eq/mdl"] = WingValue.FromString("STD");
            state[$"{prefix}/eq/1"] = WingValue.FromFloat(isTarget && channel == 1 ? 1F : 0F);
            state[$"{prefix}/dyn/on"] = WingValue.FromInt32(0);
            state[$"{prefix}/dyn/mdl"] = WingValue.FromString("COMP");
            state[$"{prefix}/dyn/1"] = WingValue.FromFloat(-18F);
        }

        return state;
    }

    private static SolidColorBrush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private sealed record PreviewTokenAuditEntry(
        DateTimeOffset OccurredAt,
        string ScopeName,
        string SourceToken,
        string TargetToken,
        string Disposition);

    private sealed record CachePresentation(
        string Summary,
        string LastData,
        DateTimeOffset? CapturedAt,
        bool HasSnapshot);

}
