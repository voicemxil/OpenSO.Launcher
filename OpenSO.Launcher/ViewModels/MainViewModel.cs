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
    private readonly StatusService _status;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonText))]
    private bool _clientInstalled;
    [ObservableProperty] private bool _assetsInstalled;
    [ObservableProperty] private bool _remeshInstalled;
    [ObservableProperty] private string _remeshState = "Checking…";

    // Game (client) version: the installed client's version.txt vs the version the server requires.
    // A mismatch means the client must update before it can connect (login version protocol).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonText))]
    private bool _gameUpdateAvailable;
    [ObservableProperty] private string? _installedGameVersion;
    [ObservableProperty] private string? _updateVersion;
    [ObservableProperty] private string _clock = "";

    // ---- Live server stats (HOME, polled from /userapi/status) -------------------------------------
    [ObservableProperty] private bool _statsAvailable;
    [ObservableProperty] private int _playersOnline;
    [ObservableProperty] private int _lotsOnline;
    [ObservableProperty] private string _serverName = "OpenSO";
    [ObservableProperty] private string _serverStatus = "Connecting…";
    [ObservableProperty] private bool _serverOnline;
    [ObservableProperty] private string _serverGameVersion = "—";
    [ObservableProperty] private string _serverTimeText = "—";
    private DateTime _serverTimeUtc;
    private DateTime _serverTimeSyncedAtUtc;
    public ObservableCollection<TopLot> TopLots { get; } = new();

    public string PlayButtonText => !ClientInstalled ? "INSTALL" : GameUpdateAvailable ? "UPDATE GAME" : "PLAY";
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
        _status = new StatusService(_config);
        _settings = LauncherSettings.Load();
        GraphicsMode = _settings.GraphicsMode; ThreeDMode = _settings.Enable3D ? "Enabled" : "Disabled";
        RefreshRate = _settings.RefreshRate; LiveNotifications = _settings.LiveNotifications ? "Enabled" : "Disabled";
        ClosingBehavior = _settings.ClosingBehavior;

        StartClock();
        _ = RefreshAsync();
        _ = LoadNewsAsync();
        _ = CheckLauncherUpdateAsync();
        StartStatusPolling();
    }

    partial void OnClientInstalledChanged(bool value) => OnPropertyChanged(nameof(PlayButtonText));
    partial void OnUpdateVersionChanged(string? value) => OnPropertyChanged(nameof(HasUpdate));

    partial void OnGraphicsModeChanged(string value) { _settings.GraphicsMode = value; _settings.Save(); }
    partial void OnThreeDModeChanged(string value)
    {
        _settings.Enable3D = value == "Enabled"; _settings.Save();
        // 3D mode looks far better with the remesh pack — nudge if it's on but the pack isn't there.
        if (_settings.Enable3D && ClientInstalled && !RemeshInstalled && !Busy)
            Notify("3D mode is on. Install the 3D mesh pack (Installer tab) for higher-quality models.");
    }
    partial void OnRefreshRateChanged(int value) { _settings.RefreshRate = value; _settings.Save(); }
    partial void OnLiveNotificationsChanged(string value) { _settings.LiveNotifications = value == "Enabled"; _settings.Save(); }
    partial void OnClosingBehaviorChanged(string value) { _settings.ClosingBehavior = value; _settings.Save(); }

    private async void StartClock()
    {
        while (true)
        {
            Clock = DateTime.Now.ToString("h:mm tt");
            if (StatsAvailable) // in-game time-of-day, anchored to the server's UTC and ticked locally
                ServerTimeText = GameClock.Format(_serverTimeUtc + (DateTime.UtcNow - _serverTimeSyncedAtUtc));
            await Task.Delay(1_000);
        }
    }

    private async void StartStatusPolling()
    {
        while (true)
        {
            await LoadStatusAsync();
            await Task.Delay(30_000); // endpoint is cached ~10s server-side; 30s is plenty
        }
    }

    private async Task LoadStatusAsync()
    {
        var s = await _status.GetAsync();
        if (s == null)
        {
            StatsAvailable = false; ServerOnline = false; ServerStatus = "Offline";
            GameUpdateAvailable = false; // can't know the required version while the status endpoint is down
            return;
        }
        StatsAvailable = true;
        PlayersOnline = s.PlayersOnline;
        LotsOnline = s.LotsOnline;
        ServerGameVersion = string.IsNullOrEmpty(s.GameVersion) ? "—" : s.GameVersion!;
        _serverTimeUtc = s.ServerTime; _serverTimeSyncedAtUtc = DateTime.UtcNow; // anchor for the in-game clock
        var shard = s.Shards?.FirstOrDefault();
        ServerName = shard?.Name ?? "OpenSO";
        // ShardStatus enum: Up / Down / Busy / Full / Closed / Frontier. Up/Busy/Full = reachable.
        var st = shard?.Status ?? "Down";
        ServerOnline = st is "Up" or "Busy" or "Full";
        ServerStatus = st == "Up" ? "Online" : st; // friendlier label for the common case
        var api = _config.ApiBaseUrl.TrimEnd('/');
        TopLots.Clear();
        foreach (var l in s.TopLots ?? Array.Empty<TopLot>())
        {
            l.ThumbnailUrl = $"{api}/userapi/city/{l.ShardId}/{l.Location}.png";
            TopLots.Add(l);
        }

        RecomputeGameUpdate(); // server version just refreshed — re-check it against the installed client
    }

    public async Task RefreshAsync()
    {
        StatusLine = "Checking your installation…";
        var installed = await _installState.GetInstalledAsync();
        var fso = installed.FirstOrDefault(s => s.Code == "FSO");
        var tso = installed.FirstOrDefault(s => s.Code == "TSO");
        var rms = installed.FirstOrDefault(s => s.Code == "RMS");

        ClientInstalled = fso?.IsInstalled == true;
        AssetsInstalled = tso?.IsInstalled == true;
        RemeshInstalled = rms?.IsInstalled == true;
        _fsoPath = ClientInstalled ? fso!.Path : null;
        InstalledGameVersion = ClientInstalled ? ReadGameVersion(_fsoPath) : null;
        ClientState = ClientInstalled ? $"Installed → {fso!.Path}" : "Not installed";
        AssetsState = AssetsInstalled ? $"Installed → {tso!.Path}" : "Not installed (downloaded on install)";
        RemeshState = RemeshInstalled ? "Installed" : "Not installed (optional — improves 3D mode)";
        StatusLine = ClientInstalled ? "Ready to play." : "Ready to install.";
        RecomputeGameUpdate();
    }

    /// <summary>True when the installed client's version differs from the version the server requires
    /// (or the client predates version stamping) — it must update before it can connect.</summary>
    private void RecomputeGameUpdate()
    {
        if (!ClientInstalled || !StatsAvailable || string.IsNullOrWhiteSpace(ServerGameVersion) || ServerGameVersion == "—")
        {
            GameUpdateAvailable = false;
            return;
        }
        var server = NormalizeVersion(ServerGameVersion);
        var local = NormalizeVersion(InstalledGameVersion);
        // Missing local version => an old install from before version.txt => treat as needing an update.
        GameUpdateAvailable = string.IsNullOrEmpty(local) || !string.Equals(local, server, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string? v) => (v ?? "").Trim().TrimStart('v', 'V');

    /// <summary>Reads the client's stamped version (the release CI writes &lt;install&gt;/version.txt).</summary>
    private static string? ReadGameVersion(string? fsoDir)
    {
        if (string.IsNullOrEmpty(fsoDir)) return null;
        try
        {
            var path = System.IO.Path.Combine(fsoDir, "version.txt");
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : null;
        }
        catch { return null; }
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

        // An out-of-date client can't connect (login version protocol) — update before launching.
        if (ClientInstalled && GameUpdateAvailable)
        {
            await UpdateGameAsync();
            return;
        }

        if (ClientInstalled)
        {
            await PlayAsync();
            return;
        }

        await InstallAsync();
    }

    /// <summary>Launches the game and only reports success once it has actually stayed up (instead of
    /// always claiming "running"). On macOS, if it's blocked by Gatekeeper/quarantine, offers to clear
    /// the block and retries once.</summary>
    private async Task PlayAsync()
    {
        var opts = BuildLaunchOptions();

        for (int attempt = 0; attempt < 2; attempt++)
        {
            StatusLine = "Launching OpenSO…";
            System.Diagnostics.Process proc;
            try { proc = _launcher.Launch(_fsoPath!, opts); }
            catch (Exception ex)
            {
                if (OperatingSystem.IsMacOS() && await _launcher.ShowMacBlockedHelpAsync(_fsoPath!)) continue;
                Notify("Couldn't launch OpenSO: " + ex.Message);
                return;
            }

            // Don't claim success until we know it didn't die immediately.
            var exitCode = await GameLauncher.WaitForEarlyExitAsync(proc, TimeSpan.FromSeconds(3));
            if (exitCode is null) { Notify("OpenSO is running."); return; }

            // A signal-kill (exit > 128) on macOS is almost always Gatekeeper/quarantine — offer to fix
            // + retry. Otherwise the game most likely showed its own error (e.g. missing game files).
            if (OperatingSystem.IsMacOS() && exitCode > 128 && await _launcher.ShowMacBlockedHelpAsync(_fsoPath!))
                continue;
            Notify($"OpenSO closed right after starting (exit code {exitCode}). If it showed an error, follow that; otherwise try reinstalling the game.");
            return;
        }
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

    private GameLauncher.Options BuildLaunchOptions() => new()
    {
        GraphicsMode = GraphicsMode == "DirectX" ? "dx" : GraphicsMode == "Software" ? "sw" : "ogl",
        Enable3D = ThreeDMode == "Enabled",
        RefreshRate = RefreshRate,
        LanguageCode = 0,
        Windowed = true,
    };

    /// <summary>Updates the installed client to the server's required version — the same way the game
    /// itself does it: download the incremental delta chain into PatchFiles/ and run the bundled
    /// patcher (update.exe), which applies it and relaunches the game. Falls back to a full reinstall
    /// (download + atomic swap) when that path isn't available: macOS/Linux builds don't ship the
    /// patcher, the update feed may be down, or the install predates version stamping.</summary>
    private async Task UpdateGameAsync()
    {
        if (Busy) return;
        Busy = true; Progress = 0; Section = "DOWNLOADS";
        Notify($"Updating the game to match the server ({ServerGameVersion})…");
        try
        {
            var reporter = new Progress<ProgressReport>(r =>
            {
                Progress = r.Fraction; ProgressDetail = r.Detail ?? "";
                StatusLine = $"{r.Stage}: {r.Detail}";
            });

            var patch = new GameUpdateService(_config);
            var patched = await patch.TryPatchUpdateAsync(_fsoPath!, InstalledGameVersion, ServerGameVersion,
                GameLauncher.BuildArgs(BuildLaunchOptions()), reporter);
            if (patched)
            {
                Notify("Update applied — the patcher restarts OpenSO when it's done.");
            }
            else
            {
                await _orchestrator.InstallAsync("FSO", _config.ResolvedInstallRoot(), reporter,
                    onUnsupported: code => Notify($"{code} not available."));
                Notify("Game updated. Press PLAY to launch.");
            }
        }
        catch (Exception ex) { Notify("Game update failed: " + ex.Message); }
        finally { Busy = false; Progress = 0; ProgressDetail = ""; await RefreshAsync(); }
    }

    [RelayCommand]
    private async Task InstallRemeshAsync()
    {
        if (Busy) return;
        if (!ClientInstalled) { Notify("Install the OpenSO client first, then add the 3D mesh pack."); Section = "INSTALLER"; return; }

        Busy = true; Progress = 0; Section = "DOWNLOADS";
        Notify("Installing the 3D mesh pack…");
        try
        {
            var reporter = new Progress<ProgressReport>(r =>
            {
                Progress = r.Fraction; ProgressDetail = r.Detail ?? "";
                StatusLine = $"{r.Stage}: {r.Detail}";
            });
            await _orchestrator.InstallAsync("RMS", _config.ResolvedInstallRoot(), reporter,
                onUnsupported: code => Notify($"{code} installer not available."));
            Notify("3D mesh pack installed.");
        }
        catch (Exception ex) { Notify("3D mesh pack failed: " + ex.Message); }
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
