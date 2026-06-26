using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSO.Launcher.Models;
using OpenSO.Launcher.Services;

namespace OpenSO.Launcher.ViewModels;

/// <summary>
/// Shell view-model for the launcher. Mirrors the upstream FreeSO Electron launcher's layout — a left
/// sidebar (logo, PLAY, clock, nav) and a content area that switches between sections: HOME, INSTALLER,
/// DOWNLOADS, SETTINGS, NOTIFICATIONS, ABOUT. One VM drives all of them (no IPC); sections are toggled by
/// the Is&lt;Section&gt; flags the AXAML binds to.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly LauncherConfig _config = new();
    private readonly InstallStateService _installState;
    private readonly InstallOrchestrator _orchestrator;
    private readonly GameLauncher _launcher = new();
    private readonly NewsService _news;
    private readonly SelfUpdateService _selfUpdate;
    private readonly LauncherSettings _settings;
    private string? _fsoPath;

    // ---- Navigation -------------------------------------------------------------------------------
    [ObservableProperty] private string _section = "HOME";
    public bool IsHome => Section == "HOME";
    public bool IsInstaller => Section == "INSTALLER";
    public bool IsDownloads => Section == "DOWNLOADS";
    public bool IsSettings => Section == "SETTINGS";
    public bool IsNotifications => Section == "NOTIFICATIONS";
    public bool IsAbout => Section == "ABOUT";

    partial void OnSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsHome)); OnPropertyChanged(nameof(IsInstaller));
        OnPropertyChanged(nameof(IsDownloads)); OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsNotifications)); OnPropertyChanged(nameof(IsAbout));
    }

    [RelayCommand] private void Navigate(string section) => Section = section;

    // ---- Shared state -----------------------------------------------------------------------------
    [ObservableProperty] private string _statusLine = "Starting up…";
    [ObservableProperty] private string _clientState = "Checking…";
    [ObservableProperty] private string _assetsState = "Checking…";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressDetail = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _clientInstalled;
    [ObservableProperty] private bool _assetsInstalled;
    [ObservableProperty] private string? _updateVersion;
    [ObservableProperty] private string _clock = "";

    public string PlayButtonText => ClientInstalled ? "PLAY" : "INSTALL";
    public bool HasUpdate => UpdateVersion != null;
    public string LauncherVersion => "OpenSO Launcher " + SelfUpdateService.CurrentVersion();
    public ObservableCollection<NewsItem> NewsItems { get; } = new();
    public ObservableCollection<string> Notifications { get; } = new();

    // ---- Settings (bound on the SETTINGS page) ----------------------------------------------------
    public string[] GraphicsModes { get; } = { "OpenGL", "DirectX", "Software" };
    public string[] OnOff { get; } = { "Disabled", "Enabled" };
    public string[] ClosingBehaviors { get; } = { "Exit launcher", "Minimize to tray", "Do nothing" };

    [ObservableProperty] private string _graphicsMode = "OpenGL";
    [ObservableProperty] private string _threeDMode = "Disabled";
    [ObservableProperty] private int _refreshRate = 60;
    [ObservableProperty] private string _liveNotifications = "Enabled";
    [ObservableProperty] private string _closingBehavior = "Exit launcher";

    public MainViewModel()
    {
        _installState = new InstallStateService(_config);
        _orchestrator = new InstallOrchestrator(_config, _installState);
        _news = new NewsService(_config);
        _selfUpdate = new SelfUpdateService(_config);
        _settings = LauncherSettings.Load();
        GraphicsMode = _settings.GraphicsMode; ThreeDMode = _settings.Enable3D ? "Enabled" : "Disabled";
        RefreshRate = _settings.RefreshRate; LiveNotifications = _settings.LiveNotifications ? "Enabled" : "Disabled";
        ClosingBehavior = _settings.ClosingBehavior;

        StartClock();
        _ = RefreshAsync();
        _ = LoadNewsAsync();
        _ = CheckLauncherUpdateAsync();
    }

    partial void OnClientInstalledChanged(bool value) => OnPropertyChanged(nameof(PlayButtonText));
    partial void OnUpdateVersionChanged(string? value) => OnPropertyChanged(nameof(HasUpdate));

    partial void OnGraphicsModeChanged(string value) { _settings.GraphicsMode = value; _settings.Save(); }
    partial void OnThreeDModeChanged(string value) { _settings.Enable3D = value == "Enabled"; _settings.Save(); }
    partial void OnRefreshRateChanged(int value) { _settings.RefreshRate = value; _settings.Save(); }
    partial void OnLiveNotificationsChanged(string value) { _settings.LiveNotifications = value == "Enabled"; _settings.Save(); }
    partial void OnClosingBehaviorChanged(string value) { _settings.ClosingBehavior = value; _settings.Save(); }

    private async void StartClock()
    {
        while (true)
        {
            Clock = DateTime.Now.ToString("h:mm tt");
            await Task.Delay(10_000);
        }
    }

    public async Task RefreshAsync()
    {
        StatusLine = "Checking your installation…";
        var installed = await _installState.GetInstalledAsync();
        var fso = installed.FirstOrDefault(s => s.Code == "FSO");
        var tso = installed.FirstOrDefault(s => s.Code == "TSO");

        ClientInstalled = fso?.IsInstalled == true;
        AssetsInstalled = tso?.IsInstalled == true;
        _fsoPath = ClientInstalled ? fso!.Path : null;
        ClientState = ClientInstalled ? $"Installed → {fso!.Path}" : "Not installed";
        AssetsState = AssetsInstalled ? $"Installed → {tso!.Path}" : "Not installed (downloaded on install)";
        StatusLine = ClientInstalled ? "Ready to play." : "Ready to install.";
    }

    private async Task LoadNewsAsync()
    {
        var items = await _news.GetLatestAsync();
        NewsItems.Clear();
        foreach (var n in items) NewsItems.Add(n);
    }

    private async Task CheckLauncherUpdateAsync()
    {
        try { UpdateVersion = await _selfUpdate.CheckForLauncherUpdateAsync(); }
        catch { /* offline */ }
    }

    private void Notify(string message)
    {
        Notifications.Insert(0, $"{DateTime.Now:h:mm tt}  •  {message}");
        StatusLine = message;
    }

    [RelayCommand] private void OpenNews(NewsItem item) => _news.OpenPost(item.Slug);
    [RelayCommand] private void OpenWebsite() => _news.OpenWebsite();
    [RelayCommand] private void OpenDiscord() => NewsService.OpenUrl("https://openso.org");

    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        if (Busy) return;

        if (ClientInstalled)
        {
            try
            {
                StatusLine = "Launching OpenSO…";
                _launcher.Launch(_fsoPath!, new GameLauncher.Options
                {
                    GraphicsMode = GraphicsMode == "DirectX" ? "dx" : GraphicsMode == "Software" ? "sw" : "ogl",
                    Enable3D = ThreeDMode == "Enabled",
                    RefreshRate = RefreshRate,
                    LanguageCode = 0,
                    Windowed = true,
                });
                Notify("OpenSO is running.");
            }
            catch (Exception ex) { Notify("Couldn't launch: " + ex.Message); }
            return;
        }

        await InstallAsync();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Busy) return;
        Busy = true; Progress = 0; Section = "DOWNLOADS";
        Notify("Preparing installation…");
        try
        {
            var reporter = new Progress<ProgressReport>(r =>
            {
                Progress = r.Fraction; ProgressDetail = r.Detail ?? "";
                StatusLine = $"{r.Stage}: {r.Detail}";
            });
            await _orchestrator.InstallAsync("FSO", _config.ResolvedInstallRoot(), reporter,
                onUnsupported: code => Notify($"{code} installer not ported yet — install it separately for now."));
            Notify("Install complete.");
        }
        catch (Exception ex) { Notify("Install failed: " + ex.Message); }
        finally { Busy = false; Progress = 0; ProgressDetail = ""; await RefreshAsync(); }
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (Busy || UpdateVersion == null) return;
        Busy = true;
        try
        {
            var reporter = new Progress<ProgressReport>(r => { Progress = r.Fraction; StatusLine = $"{r.Stage}: {r.Detail}"; });
            await _selfUpdate.ApplyLauncherUpdateAsync(reporter);
            StatusLine = "Restarting to finish the update…";
            Environment.Exit(0);
        }
        catch (Exception ex) { Notify("Launcher update failed: " + ex.Message); Busy = false; }
    }
}
