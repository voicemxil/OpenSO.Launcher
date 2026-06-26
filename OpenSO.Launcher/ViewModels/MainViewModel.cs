using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSO.Launcher.Models;
using OpenSO.Launcher.Services;

namespace OpenSO.Launcher.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly LauncherConfig _config = new();
    private readonly InstallStateService _installState;
    private readonly InstallOrchestrator _orchestrator;
    private readonly GameLauncher _launcher = new();
    private readonly NewsService _news;
    private readonly SelfUpdateService _selfUpdate;
    private string? _fsoPath;

    [ObservableProperty] private string _statusLine = "Starting up…";
    [ObservableProperty] private string _clientState = "Checking…";
    [ObservableProperty] private string _assetsState = "Checking…";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressDetail = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _clientInstalled;
    [ObservableProperty] private string? _updateVersion;

    public string PlayButtonText => ClientInstalled ? "Play" : "Install OpenSO";
    public bool HasUpdate => UpdateVersion != null;
    public ObservableCollection<NewsItem> NewsItems { get; } = new();

    public MainViewModel()
    {
        _installState = new InstallStateService(_config);
        _orchestrator = new InstallOrchestrator(_config, _installState);
        _news = new NewsService(_config);
        _selfUpdate = new SelfUpdateService(_config);
        _ = RefreshAsync();
        _ = LoadNewsAsync();
        _ = CheckLauncherUpdateAsync();
    }

    partial void OnClientInstalledChanged(bool value) => OnPropertyChanged(nameof(PlayButtonText));
    partial void OnUpdateVersionChanged(string? value) => OnPropertyChanged(nameof(HasUpdate));

    private async Task LoadNewsAsync()
    {
        var items = await _news.GetLatestAsync();
        NewsItems.Clear();
        foreach (var n in items) NewsItems.Add(n);
    }

    private async Task CheckLauncherUpdateAsync()
    {
        try { UpdateVersion = await _selfUpdate.CheckForLauncherUpdateAsync(); }
        catch { /* offline / no update */ }
    }

    [RelayCommand]
    private void OpenNews(NewsItem item) => _news.OpenPost(item.Slug);

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
            Environment.Exit(0); // the swap script waits for us to exit, replaces files, and relaunches
        }
        catch (Exception ex)
        {
            StatusLine = "Launcher update failed: " + ex.Message;
            Busy = false;
        }
    }

    public async Task RefreshAsync()
    {
        StatusLine = "Checking your installation…";
        var installed = await _installState.GetInstalledAsync();

        var fso = installed.FirstOrDefault(s => s.Code == "FSO");
        var tso = installed.FirstOrDefault(s => s.Code == "TSO");

        ClientInstalled = fso?.IsInstalled == true;
        _fsoPath = ClientInstalled ? fso!.Path : null;
        ClientState = ClientInstalled ? $"Installed → {fso!.Path}" : "Not installed";
        AssetsState = tso?.IsInstalled == true ? $"Installed → {tso!.Path}" : "Not installed (will be downloaded)";

        StatusLine = ClientInstalled ? "Ready to play." : "Ready to install.";
    }

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
                    GraphicsMode = "ogl",
                    RefreshRate = 60,
                    LanguageCode = 0,
                    Windowed = true,
                });
                StatusLine = "OpenSO is running.";
            }
            catch (Exception ex)
            {
                StatusLine = "Couldn't launch: " + ex.Message;
            }
            return;
        }

        Busy = true;
        Progress = 0;
        StatusLine = "Preparing installation…";

        try
        {
            var installRoot = _config.ResolvedInstallRoot();

            var reporter = new Progress<ProgressReport>(r =>
            {
                Progress = r.Fraction;
                ProgressDetail = r.Detail ?? "";
                StatusLine = $"{r.Stage}: {r.Detail}";
            });

            await _orchestrator.InstallAsync(
                "FSO", installRoot, reporter,
                onUnsupported: code => StatusLine =
                    $"Note: {code} installer not ported yet — install it separately for now.");

            StatusLine = "Install complete.";
        }
        catch (Exception ex)
        {
            StatusLine = "Install failed: " + ex.Message;
        }
        finally
        {
            Busy = false;
            Progress = 0;
            ProgressDetail = "";
            // Always refresh so anything that DID install (e.g. TSO) shows correctly even if a later
            // step (e.g. the OpenSO client) failed.
            await RefreshAsync();
        }
    }
}
