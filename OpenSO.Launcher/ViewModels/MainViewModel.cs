using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
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
    private readonly LauncherConfig _config = new();
    private readonly InstallStateService _installState;
    private readonly InstallOrchestrator _orchestrator;
    private readonly GameLauncher _launcher = new();
    private readonly NewsService _news;
    private readonly SelfUpdateService _selfUpdate;
    private readonly StatusService _status;
    private readonly LauncherSettings _settings;
    private string? _fsoPath;

    /// <summary>Cancelled when the app shuts down — ends the clock/status polling loops so no
    /// background work outlives the window (see App.OnFrameworkInitializationCompleted).</summary>
    private readonly CancellationTokenSource _shutdownCts = new();

    // ---- Polling cadences (named so they're easy to tune) ------------------------------------------
    /// <summary>Steady-state server-status poll interval once the server is reachable. The endpoint is
    /// cached ~10s server-side, so polling faster than this buys nothing.</summary>
    private static readonly TimeSpan StatusPollSteady = TimeSpan.FromSeconds(10);
    /// <summary>Faster poll used while the server looks offline/unreachable, so a restart/deploy is
    /// picked up near-immediately instead of waiting a full steady interval.</summary>
    private static readonly TimeSpan StatusPollFast = TimeSpan.FromSeconds(3);
    /// <summary>How often to re-check for a new launcher version so a long-running launcher still
    /// learns about updates (the check is startup-only otherwise).</summary>
    private static readonly TimeSpan LauncherUpdatePoll = TimeSpan.FromHours(6);

    /// <summary>Signalled to wake the status-poll loop early (e.g. after a manual Refresh) so it
    /// re-schedules instead of firing a second, overlapping poll on top of the manual one.</summary>
    private readonly SemaphoreSlim _pollNudge = new(0, 1);

    /// <summary>Stops all periodic background work. Called by App when the Avalonia lifetime exits.</summary>
    public void Shutdown()
    {
        try { _shutdownCts.Cancel(); } catch (ObjectDisposedException) { }
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
    // DirectX is a Windows-only backend, so only offer it there — on macOS/Linux the game always uses
    // OpenGL. (GameLauncher also coerces to ogl at launch, but hiding the option keeps the UI honest.)
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public string[] GraphicsModes { get; } = IsWindows
        ? new[] { "OpenGL", "DirectX", "Software" }
        : new[] { "OpenGL", "Software" };
    /// <summary>Caption under the Graphics Mode picker — only mentions DirectX where it's actually
    /// selectable, so macOS/Linux users aren't told about an option they can't see.</summary>
    public string GraphicsModeHint => IsWindows
        ? "DirectX usually performs best on Windows; Software is a compatibility fallback."
        : "OpenGL is used on macOS/Linux; Software is a compatibility fallback.";
    public string[] OnOff { get; } = { "Disabled", "Enabled" };
    public string[] ClosingBehaviors { get; } = { "Exit launcher", "Minimize to tray", "Do nothing" };

    [ObservableProperty] private string _graphicsMode = "OpenGL";
    [ObservableProperty] private string _threeDMode = "Disabled";
    [ObservableProperty] private string _liveNotifications = "Enabled";
    [ObservableProperty] private string _closingBehavior = "Exit launcher";

    // ---- Manual refresh state (SERVER STATUS card) ------------------------------------------------
    /// <summary>True while a manual Refresh is in flight — the button binds this to disable itself and
    /// swap to a "checking…" label, guarding against overlapping refreshes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshButtonText))]
    private bool _isRefreshing;
    public string RefreshButtonText => IsRefreshing ? "…" : "Refresh";

    public MainViewModel()
    {
        _installState = new InstallStateService(_config);
        _orchestrator = new InstallOrchestrator(_config, _installState);
        _news = new NewsService(_config);
        _selfUpdate = new SelfUpdateService(_config);
        _status = new StatusService(_config);
        _settings = LauncherSettings.Load();
        // Defensive: if a persisted value isn't a mode we can actually offer (e.g. "DirectX" carried
        // over onto macOS/Linux), fall back to OpenGL so the ComboBox isn't blank and nothing silently
        // maps to a backend this platform can't use.
        GraphicsMode = GraphicsModes.Contains(_settings.GraphicsMode) ? _settings.GraphicsMode : "OpenGL";
        ThreeDMode = _settings.Enable3D ? "Enabled" : "Disabled";
        LiveNotifications = _settings.LiveNotifications ? "Enabled" : "Disabled";
        ClosingBehavior = _settings.ClosingBehavior;

        StartClock();
        _ = RefreshAsync();
        _ = LoadNewsAsync();
        StartLauncherUpdatePolling();
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
    partial void OnLiveNotificationsChanged(string value) { _settings.LiveNotifications = value == "Enabled"; _settings.Save(); }
    partial void OnClosingBehaviorChanged(string value) { _settings.ClosingBehavior = value; _settings.Save(); }

    private async void StartClock()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                Clock = DateTime.Now.ToString("h:mm tt");
                if (StatsAvailable) // in-game time-of-day, anchored to the server's UTC and ticked locally
                    ServerTimeText = GameClock.Format(_serverTimeUtc + (DateTime.UtcNow - _serverTimeSyncedAtUtc));
                await Task.Delay(1_000, _shutdownCts.Token);
            }
        }
        catch (OperationCanceledException) { /* app is shutting down */ }
    }

    /// <summary>Adaptive server-status poll. While the server is reachable it polls at the steady
    /// cadence; the moment a check comes back offline/unreachable (or throws) it switches to the fast
    /// cadence so a restart/deploy is picked up almost immediately, then relaxes back once it's up. A
    /// manual Refresh nudges this loop (via <see cref="_pollNudge"/>) so the timer re-schedules instead
    /// of firing a redundant poll on top of the manual one. Every wait uses the shutdown token so the
    /// loop still ends cleanly on exit.</summary>
    private async void StartStatusPolling()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                await LoadStatusAsync();
                // Server down/unreachable => poll fast until it returns; otherwise relax to steady.
                var delay = ServerOnline ? StatusPollSteady : StatusPollFast;
                await WaitOrNudgeAsync(delay);
            }
        }
        catch (OperationCanceledException) { /* app is shutting down */ }
    }

    /// <summary>Waits up to <paramref name="delay"/>, but returns early if a manual Refresh signals the
    /// nudge — so the next scheduled poll lands a full interval after the manual one, never on top of
    /// it. Honours the shutdown token so cancellation still propagates.</summary>
    private async Task WaitOrNudgeAsync(TimeSpan delay)
    {
        // WaitAsync returns true when the nudge fires (manual Refresh) and false on timeout (the normal
        // "interval elapsed" path); either way we just loop and poll again. It throws only when the
        // shutdown token is cancelled, which bubbles up to the caller to end the loop.
        await _pollNudge.WaitAsync(delay, _shutdownCts.Token);
    }

    /// <summary>Periodic launcher self-update check so a launcher left open still learns about a new
    /// version. Runs an initial check immediately, then every <see cref="LauncherUpdatePoll"/>.</summary>
    private async void StartLauncherUpdatePolling()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                await CheckLauncherUpdateAsync();
                await Task.Delay(LauncherUpdatePoll, _shutdownCts.Token);
            }
        }
        catch (OperationCanceledException) { /* app is shutting down */ }
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

    /// <summary>Manual Refresh (button in the SERVER STATUS card): immediately re-checks server status
    /// (which also recomputes the game-update state) and the launcher self-update, rather than waiting
    /// for the next timer tick. Guarded by <see cref="IsRefreshing"/> so it can't overlap itself, and
    /// nudges the poll loop so the automatic timer doesn't fire a second poll right behind this one.</summary>
    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            await LoadStatusAsync();               // refreshes server stats + recomputes game-update state
            await CheckLauncherUpdateAsync();
        }
        finally
        {
            IsRefreshing = false;
            // Wake the poll loop so its next interval is measured from now — avoids a redundant poll
            // landing right on top of this manual one. Ignore if a nudge is already pending.
            try { _pollNudge.Release(); } catch (SemaphoreFullException) { }
        }
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

            // Don't claim success until we know it didn't die immediately. Dispose the Process
            // component afterwards — it only releases our handle to the game, which keeps running
            // detached; holding it open would leak a handle for the launcher's whole lifetime.
            int? exitCode;
            using (proc)
                exitCode = await GameLauncher.WaitForEarlyExitAsync(proc, TimeSpan.FromSeconds(3));
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
            // Shut down through the Avalonia lifetime so App cancels background work and Main's
            // process-exit backstop runs; the staged swap script waits for this PID to disappear.
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Environment.Exit(0);
        }
        catch (Exception ex) { Notify("Launcher update failed: " + ex.Message); Busy = false; }
    }
}
