using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
    private readonly LauncherConfig _config;
    private readonly InstallStateService _installState;
    private readonly InstallOrchestrator _orchestrator;
    private readonly GameLauncher _launcher;
    private readonly NewsService _news;
    private readonly SelfUpdateService _selfUpdate;
    private readonly StatusService _status;
    private readonly LauncherSettings _settings;
    private string? _fsoPath;

    /// <summary>Cancelled when the app shuts down — ends the clock/status polling loops so no
    /// background work outlives the window (see App.OnFrameworkInitializationCompleted).</summary>
    private readonly CancellationTokenSource _shutdownCts = new();

    /// <summary>Serializes installs/updates against the install-state probe: a RefreshAsync must never
    /// read half-written markers mid-install, and two component installs can never mutate the install
    /// tree concurrently. Scoped around the orchestrator call itself (NOT the whole command) so the
    /// RefreshAsync in each command's finally doesn't deadlock on its own gate.</summary>
    private readonly SemaphoreSlim _installGate = new(1, 1);

    /// <summary>The install/update/remesh operation currently in flight (or a completed task). Tracked so
    /// Shutdown can cancel it and wait briefly for it to unwind before the process is killed.</summary>
    private Task _activeInstall = Task.CompletedTask;

    /// <summary>Stops all periodic background work and cancels any in-flight install. Called by App when
    /// the Avalonia lifetime exits. Waits a bounded time for a running download/extract to observe the
    /// cancellation and stop cleanly, so Program's Environment.Exit can't kill it mid-write.</summary>
    public void Shutdown()
    {
        try { _shutdownCts.Cancel(); } catch (ObjectDisposedException) { }
        // Give an in-flight install up to 3s to unwind (it checks the token in its download/extract
        // loops). The task swallows its own exceptions, and cancellation surfaces as a faulted Wait —
        // either way we're exiting, so ignore the outcome.
        try { _activeInstall.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

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
    // Loud, dedicated error surface (bound to a red banner on the Downloads page) so a blocked/failed
    // operation isn't buried as raw exception text in the progress subtext.
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _clientState = "Checking…";
    [ObservableProperty] private string _assetsState = "Checking…";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressIndeterminate;
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
    public string[] ClosingBehaviors { get; } = { "Exit launcher", "Minimize to tray" };

    [ObservableProperty] private string _graphicsMode = "OpenGL";
    [ObservableProperty] private string _threeDMode = "Disabled";
    [ObservableProperty] private string _autoUpdate = "Enabled";
    [ObservableProperty] private string _liveNotifications = "Enabled";
    [ObservableProperty] private string _closingBehavior = "Exit launcher";

    /// <summary>Read by MainWindow's close handler: true when closing should hide to the tray instead.</summary>
    public bool MinimizeToTray => ClosingBehavior == "Minimize to tray";

    /// <summary>Design-time / fallback constructor — composes the default service graph itself. The real
    /// app builds the graph in <see cref="App"/> (the composition root) and calls the injecting ctor.</summary>
    public MainViewModel() : this(AppServices.CreateDefault()) { }

    /// <summary>Injecting constructor — takes an already-wired service bundle (see <see cref="AppServices"/>),
    /// so the wiring lives in one composition root and services can be substituted in tests.</summary>
    public MainViewModel(AppServices services)
    {
        _config = services.Config;
        _installState = services.InstallState;
        _orchestrator = services.Orchestrator;
        _news = services.News;
        _selfUpdate = services.SelfUpdate;
        _status = services.Status;
        _launcher = services.Launcher;
        _settings = LauncherSettings.Load();
        GraphicsMode = _settings.GraphicsMode; ThreeDMode = _settings.Enable3D ? "Enabled" : "Disabled";
        AutoUpdate = _settings.AutoUpdateLauncher ? "Enabled" : "Disabled";
        LiveNotifications = _settings.LiveNotifications ? "Enabled" : "Disabled";
        // Sanitize retired values (an old settings.json may still say "Do nothing").
        ClosingBehavior = Array.IndexOf(ClosingBehaviors, _settings.ClosingBehavior) >= 0
            ? _settings.ClosingBehavior : "Exit launcher";

        StartClock();
        _ = InitializeAsync();
        _ = LoadNewsAsync();
        _ = CheckLauncherUpdateAsync();
    }

    /// <summary>Startup sequence: establish the install state FIRST, then start status polling — so the
    /// poll's RecomputeGameUpdate never races the initial refresh over PlayButtonText/update state.
    /// RefreshAsync never throws, so this can be safely fire-and-forgotten from the ctor.</summary>
    private async Task InitializeAsync()
    {
        await RefreshAsync();
        if (ClientInstalled && _fsoPath != null)
            GameUpdateService.SweepStalePatchFiles(_fsoPath); // a failed patch must not be re-applied later
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
    partial void OnAutoUpdateChanged(string value) { _settings.AutoUpdateLauncher = value == "Enabled"; _settings.Save(); }
    partial void OnLiveNotificationsChanged(string value) { _settings.LiveNotifications = value == "Enabled"; _settings.Save(); }
    partial void OnClosingBehaviorChanged(string value) { _settings.ClosingBehavior = value; _settings.Save(); }

    /// <summary>Native desktop notification (Windows toast/balloon), gated by the settings toggle.</summary>
    private void DesktopNotify(string title, string message)
    {
        if (LiveNotifications == "Enabled") TrayNotifier.Show(title, message);
    }

    // Both loops are async void (fire-and-forget from the ctor), so ANY escaped exception would kill
    // the loop silently — a frozen clock or a status pill stuck on "Connecting…" for the rest of the
    // session. Each iteration therefore swallows per-tick failures and keeps looping; only shutdown
    // cancellation exits.
    private async void StartClock()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            try
            {
                Clock = DateTime.Now.ToString("h:mm tt");
                if (StatsAvailable) // in-game time-of-day, anchored to the server's UTC and ticked locally
                    ServerTimeText = GameClock.Format(_serverTimeUtc + (DateTime.UtcNow - _serverTimeSyncedAtUtc));
            }
            catch { /* keep ticking */ }
            try { await Task.Delay(1_000, _shutdownCts.Token); }
            catch (OperationCanceledException) { return; /* app is shutting down */ }
        }
    }

    private async void StartStatusPolling()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            try { await LoadStatusAsync(); }
            catch { /* transient network/parse failure — keep the last known state and poll again */ }
            try { await Task.Delay(30_000, _shutdownCts.Token); } // endpoint is cached ~10s server-side; 30s is plenty
            catch (OperationCanceledException) { return; /* app is shutting down */ }
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

    /// <summary>Re-probes the install state and updates the UI. Never throws (fire-and-forgotten from
    /// the ctor and awaited in install finally blocks — an escaped exception would either vanish
    /// silently or mask the install's own error). The probe is serialized behind _installGate so it
    /// can't read half-written install markers while an install/update is mutating them.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            StatusLine = "Checking your installation…";
            System.Collections.Generic.IReadOnlyList<InstallStatus> installed;
            await _installGate.WaitAsync();
            try { installed = await _installState.GetInstalledAsync(); }
            finally { _installGate.Release(); }
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
        catch (Exception ex)
        {
            // Don't guess at an install state we couldn't read — say so instead of showing
            // "Ready to install" over a real install (which invites a conflicting reinstall).
            ClientState = "Unknown — couldn't check the install";
            StatusLine = "Couldn't check your installation: " + ex.Message;
        }
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
        GameUpdateAvailable = string.IsNullOrEmpty(local) || !SameVersion(local, server);

        // Desktop-notify a newly detected required game update — once per server version, so the
        // periodic status poll doesn't re-toast the same update every tick.
        if (GameUpdateAvailable && _lastNotifiedGameVersion != server)
        {
            _lastNotifiedGameVersion = server;
            DesktopNotify("OpenSO game update", $"A game update was detected — the server is running {ServerGameVersion}.");
        }
    }

    private string? _lastNotifiedGameVersion;

    private static string NormalizeVersion(string? v) => (v ?? "").Trim().TrimStart('v', 'V');

    /// <summary>Version equality that treats "1.2.3" and "1.2.3.0" as the same (a plain string compare
    /// would flag a phantom update). Pads both to four numeric components before comparing — System.Version
    /// otherwise stores an unspecified revision as -1, so 1.2.3 != 1.2.3.0. Non-numeric versions fall back
    /// to a case-insensitive string compare.</summary>
    private static bool SameVersion(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (Version.TryParse(Pad4(a), out var va) && Version.TryParse(Pad4(b), out var vb)) return va == vb;
        return false;

        static string Pad4(string s)
        {
            var parts = s.Split('.');
            var padded = new string[4];
            for (int i = 0; i < 4; i++) padded[i] = i < parts.Length ? parts[i] : "0";
            return string.Join('.', padded);
        }
    }

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
        catch (Exception ex) { Log.Warn("Launcher update check failed (offline?)", ex); return; }
        if (UpdateVersion == null) return;

        // Auto-update: install without asking (the banner's Update button, unprompted). Skipped while
        // Busy — an install/update in flight must not be killed by the self-update's restart.
#if DEBUG
        // Never auto-apply from a debug/dev build: the release feed is always "newer" than a local
        // build, so auto-update would instantly overwrite the build under test. The banner (manual
        // update) stays available.
        Log.Warn($"Debug build: skipping launcher auto-update to {UpdateVersion}.");
#else
        if (_settings.AutoUpdateLauncher && !Busy)
        {
            DesktopNotify("OpenSO Launcher update", $"Version {UpdateVersion} was detected — installing now.");
            Notify($"Updating launcher to {UpdateVersion}…");
            await ApplyUpdateAsync();
        }
        else
        {
            DesktopNotify("OpenSO Launcher update", $"Version {UpdateVersion} is available — open the launcher to update.");
        }
#endif
    }

    private void Notify(string message)
    {
        Notifications.Insert(0, $"{DateTime.Now:h:mm tt}  •  {message}");
        StatusLine = message;
    }

    /// <summary>Clears the loud error banner — call when starting a new operation.</summary>
    private void ClearError() { HasError = false; ErrorMessage = ""; }

    /// <summary>The progress reporter every install/update uses — updates the bar (value + indeterminate),
    /// the detail subtext, and the status line from one place.</summary>
    private IProgress<ProgressReport> MakeReporter() => new Progress<ProgressReport>(r =>
    {
        Progress = r.Fraction;
        ProgressIndeterminate = r.IsIndeterminate;
        ProgressDetail = r.Detail ?? "";
        StatusLine = $"{r.Stage}: {r.Detail}";
    });

    /// <summary>Central handler for a failed install/update/remesh. A file-in-use failure gets a loud,
    /// specific message naming the locking process (see <see cref="FileLocks"/>) rather than the raw
    /// IOException text buried in the progress subtext; everything else gets a clear "&lt;what&gt; failed".</summary>
    private void HandleOperationFailure(string what, Exception ex)
    {
        Log.Error($"{what} failed", ex);
        string message = FileLocks.IsFileInUse(ex)
            ? "Couldn't update the game — " + FileLocks.Explain(ex)
            : $"{what} failed: {ex.Message}";
        ErrorMessage = message;
        HasError = true;
        Notify(message);       // also lands in the Notifications list + status line
        Section = "DOWNLOADS";  // make sure the banner is on-screen
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
                Log.Error("Game launch failed", ex);
                if (OperatingSystem.IsMacOS() && await _launcher.ShowMacBlockedHelpAsync(_fsoPath!)) continue;
                Notify("Couldn't launch OpenSO: " + ex.Message);
                return;
            }

            // Don't claim success until we know it didn't die immediately. Dispose the Process
            // component afterwards — it only releases our handle to the game, which keeps running
            // detached; holding it open would leak a handle for the launcher's whole lifetime.
            int? exitCode;
            using (proc)
                exitCode = await GameLauncher.WaitForEarlyExitAsync(proc, TimeSpan.FromSeconds(3));
            // null = still running after the window. 0 = the parent exited CLEANLY within it — that's
            // a hand-off (relaunch/fork to a detached child), not a crash; a client that fails shows
            // an error and exits non-zero. Only a non-zero early exit is treated as a failed launch.
            if (exitCode is null or 0) { Notify("OpenSO is running."); return; }

            // A signal-kill (exit > 128) on macOS is almost always Gatekeeper/quarantine — offer to fix
            // + retry. Otherwise the game most likely showed its own error (e.g. missing game files).
            if (OperatingSystem.IsMacOS() && exitCode > 128 && await _launcher.ShowMacBlockedHelpAsync(_fsoPath!))
                continue;
            Notify($"OpenSO closed right after starting (exit code {exitCode}). If it showed an error, follow that; otherwise try reinstalling the game.");
            return;
        }
    }

    /// <summary>Refuses an install/update/patch while the OpenSO client is open (it would overwrite
    /// locked game files and corrupt the install). Returns true — and notifies the user — if blocked.</summary>
    private bool BlockedByRunningGame()
    {
        if (ClientInstalled && GameProcessGuard.IsGameRunning(_fsoPath))
        {
            ErrorMessage = "OpenSO is running. Close the game completely, then try again.";
            HasError = true;
            Notify(ErrorMessage);
            Section = "DOWNLOADS";
            return true;
        }
        return false;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Busy || BlockedByRunningGame()) return;
        ClearError();
        Busy = true; Progress = 0; Section = "DOWNLOADS";
        Notify("Preparing installation…");
        try
        {
            var reporter = MakeReporter();
            await _installGate.WaitAsync(_shutdownCts.Token);
            try
            {
                _activeInstall = _orchestrator.InstallAsync("FSO", _config.ResolvedInstallRoot(), reporter,
                    onUnsupported: code => Notify($"{code} installer not ported yet — install it separately for now."),
                    _shutdownCts.Token);
                await _activeInstall;

                // Auto-install the 3D mesh pack right after the game (still inside the install gate).
                // Non-fatal: the game install already succeeded, so a remesh failure only gets a nudge
                // to retry from the Installer tab.
                Notify("Game installed. Adding the 3D mesh pack…");
                try
                {
                    _activeInstall = _orchestrator.InstallAsync("RMS", _config.ResolvedInstallRoot(), reporter,
                        onUnsupported: code => Notify($"{code} installer not available."), _shutdownCts.Token);
                    await _activeInstall;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warn("3D mesh pack auto-install failed after game install", ex);
                    Notify("Game installed, but the 3D mesh pack failed — you can retry it from the Installer tab.");
                }
            }
            finally { _installGate.Release(); }
            Notify("Install complete.");
        }
        catch (OperationCanceledException) { /* launcher closing */ }
        catch (Exception ex) { HandleOperationFailure("Install", ex); }
        finally { Busy = false; Progress = 0; ProgressDetail = ""; await RefreshAsync(); }
    }

    private GameLauncher.Options BuildLaunchOptions() => new()
    {
        GraphicsMode = GraphicsMode == "DirectX" ? "dx" : GraphicsMode == "Software" ? "sw" : "ogl",
        Enable3D = ThreeDMode == "Enabled",
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
        if (Busy || BlockedByRunningGame()) return;
        ClearError();
        Busy = true; Progress = 0; Section = "DOWNLOADS";
        Notify($"Updating the game to match the server ({ServerGameVersion})…");
        try
        {
            var reporter = MakeReporter();
            await _installGate.WaitAsync(_shutdownCts.Token);
            try
            {
                var patch = new GameUpdateService(_config);
                var patchTask = patch.TryPatchUpdateAsync(_fsoPath!, InstalledGameVersion, ServerGameVersion,
                    GameLauncher.BuildArgs(BuildLaunchOptions()), reporter, _shutdownCts.Token);
                _activeInstall = patchTask;
                var patched = await patchTask;
                if (patched)
                {
                    Notify("Update applied — the patcher restarts OpenSO when it's done.");
                }
                else
                {
                    _activeInstall = _orchestrator.InstallAsync("FSO", _config.ResolvedInstallRoot(), reporter,
                        onUnsupported: code => Notify($"{code} not available."), _shutdownCts.Token);
                    await _activeInstall;
                    Notify("Game updated. Press PLAY to launch.");
                }
            }
            finally { _installGate.Release(); }
        }
        catch (OperationCanceledException) { /* launcher closing */ }
        catch (Exception ex) { HandleOperationFailure("Game update", ex); }
        finally { Busy = false; Progress = 0; ProgressDetail = ""; await RefreshAsync(); }
    }

    [RelayCommand]
    private async Task InstallRemeshAsync()
    {
        if (Busy || BlockedByRunningGame()) return;
        if (!ClientInstalled) { Notify("Install the OpenSO client first, then add the 3D mesh pack."); Section = "INSTALLER"; return; }

        ClearError();
        Busy = true; Progress = 0; Section = "DOWNLOADS";
        Notify("Installing the 3D mesh pack…");
        try
        {
            var reporter = MakeReporter();
            await _installGate.WaitAsync(_shutdownCts.Token);
            try
            {
                _activeInstall = _orchestrator.InstallAsync("RMS", _config.ResolvedInstallRoot(), reporter,
                    onUnsupported: code => Notify($"{code} installer not available."), _shutdownCts.Token);
                await _activeInstall;
            }
            finally { _installGate.Release(); }
            Notify("3D mesh pack installed.");
        }
        catch (OperationCanceledException) { /* launcher closing */ }
        catch (Exception ex) { HandleOperationFailure("3D mesh pack install", ex); }
        finally { Busy = false; Progress = 0; ProgressDetail = ""; await RefreshAsync(); }
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (Busy || UpdateVersion == null) return;
        Busy = true;
        ClearError();
        try
        {
            var reporter = MakeReporter();
            await _selfUpdate.ApplyLauncherUpdateAsync(reporter);
            StatusLine = "Restarting to finish the update…";
            App.ExitRequested = true; // the minimize-to-tray close handler must not swallow this close
            // Shut down through the Avalonia lifetime so App cancels background work and Main's
            // process-exit backstop runs; the staged swap script waits for this PID to disappear.
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Environment.Exit(0);
        }
        catch (Exception ex) { Log.Error("Launcher self-update failed", ex); Notify("Launcher update failed: " + ex.Message); Busy = false; }
    }
}
