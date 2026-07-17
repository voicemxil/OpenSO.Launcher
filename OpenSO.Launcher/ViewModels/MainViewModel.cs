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
using OpenSO.Launcher.Services.Installers;
using OpenSO.Launcher.Services.Updates;

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

    /// <summary>Guards <see cref="RunUpdateGameHandoffAsync"/> so the <c>--update-game</c> flag can only
    /// ever trigger the update-then-launch flow once per process, even if something re-entered it.</summary>
    private bool _updateGameHandoffRan;

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

    /// <summary>Coordinates the adaptive status-poll loop with a manual Refresh: <c>Nudge()</c>/<c>WaitAsync()</c>
    /// wake the loop early after a manual refresh so it re-schedules instead of firing a second, overlapping
    /// poll right on top of the manual one (see <see cref="WaitOrNudgeAsync"/>).</summary>
    private readonly PollGate _statusPollGate = new();

    /// <summary>Coordinates the 6-hour launcher self-update poll with Refresh's own on-demand check of the
    /// same feed (see <see cref="CheckLauncherUpdateAsync"/>): TryEnter/Release ensure the poll and a
    /// manual Refresh never hit the update feed concurrently (the loser just skips — the winner's result
    /// still lands in <see cref="UpdateVersion"/>), and Nudge()/WaitAsync() defer the poll's next automatic
    /// tick a full interval after a manual check so it never fires — and re-prompts — right behind one
    /// Refresh just did.</summary>
    private readonly PollGate _launcherUpdateGate = new();

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

    /// <summary>City map thumbnail for the SERVER STATUS card — the literal thumbnail.png shipped with
    /// the map (&lt;install&gt;/Content/Cities/city_{CityMapId}/thumbnail.png), loaded from the local
    /// client install. Null — and the banner collapsed — until the client is installed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCityThumbnail))]
    private Avalonia.Media.Imaging.Bitmap? _cityThumbnail;
    public bool HasCityThumbnail => CityThumbnail != null;
    private string? _cityThumbLoadedFrom;
    private DateTime _serverTimeUtc;
    private DateTime _serverTimeSyncedAtUtc;

    /// <summary>Caption next to the Refresh button: when the stats currently on screen were last
    /// SUCCESSFULLY loaded (set in <see cref="LoadStatusAsync"/> only on success — a failed/offline load
    /// leaves it as-is, so it always honestly reflects the age of what's displayed). See
    /// <see cref="StatusDisplay.FormatLastUpdated"/> for the pure formatting.</summary>
    [ObservableProperty] private string _lastUpdatedText = StatusDisplay.FormatLastUpdated(null);
    private DateTime? _lastStatusSuccessAtLocal;
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
    public string[] ClosingBehaviors { get; } = { "Exit launcher", "Minimize to tray" };

    [ObservableProperty] private string _graphicsMode = IsWindows ? "DirectX" : "OpenGL";
    [ObservableProperty] private string _threeDMode = "Enabled";
    [ObservableProperty] private string _autoUpdate = "Enabled";
    [ObservableProperty] private string _liveNotifications = "Enabled";
    [ObservableProperty] private string _closingBehavior = "Exit launcher";

    /// <summary>Read by MainWindow's close handler: true when closing should hide to the tray instead.</summary>
    public bool MinimizeToTray => ClosingBehavior == "Minimize to tray";

    // ---- Manual refresh state (SERVER STATUS card) ------------------------------------------------
    /// <summary>True while a manual Refresh is in flight — the button binds this to disable itself and
    /// swap to a "checking…" label, guarding against overlapping refreshes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshButtonText))]
    private bool _isRefreshing;
    public string RefreshButtonText => IsRefreshing ? "…" : "Refresh";

    /// <summary>Design-time / fallback constructor — composes the default service graph itself. The real
    /// app builds the graph in <see cref="App"/> (the composition root) and calls the injecting ctor.</summary>
    public MainViewModel() : this(AppServices.CreateDefault()) { }

    /// <summary>Injecting constructor — takes an already-wired service bundle (see <see cref="AppServices"/>),
    /// so the wiring lives in one composition root and services can be substituted in tests.</summary>
    /// <param name="updateGame">True when the launcher was started with <c>--update-game</c> — the game
    /// client's handoff on a version mismatch (see BUILD_AND_TEST.md → "Game → launcher handoff"). When
    /// set, <see cref="RunUpdateGameHandoffAsync"/> runs automatically once startup is under way.</param>
    public MainViewModel(AppServices services, bool updateGame = false)
    {
        _config = services.Config;
        _installState = services.InstallState;
        _orchestrator = services.Orchestrator;
        _news = services.News;
        _selfUpdate = services.SelfUpdate;
        _status = services.Status;
        _launcher = services.Launcher;
        _settings = LauncherSettings.Load();
        // Defensive: if a persisted value isn't a mode we can actually offer (e.g. "DirectX" carried
        // over onto macOS/Linux), fall back to OpenGL so the ComboBox isn't blank and nothing silently
        // maps to a backend this platform can't use.
        GraphicsMode = GraphicsModes.Contains(_settings.GraphicsMode) ? _settings.GraphicsMode : "OpenGL";
        ThreeDMode = _settings.Enable3D ? "Enabled" : "Disabled";
        AutoUpdate = _settings.AutoUpdateLauncher ? "Enabled" : "Disabled";
        LiveNotifications = _settings.LiveNotifications ? "Enabled" : "Disabled";
        // Sanitize retired values (an old settings.json may still say "Do nothing").
        ClosingBehavior = Array.IndexOf(ClosingBehaviors, _settings.ClosingBehavior) >= 0
            ? _settings.ClosingBehavior : "Exit launcher";

        StartClock();
        _ = InitializeAsync(updateGame);
        _ = LoadNewsAsync();
        StartLauncherUpdatePolling();
    }

    /// <summary>Startup sequence: establish the install state FIRST, then start status polling — so the
    /// poll's RecomputeGameUpdate never races the initial refresh over PlayButtonText/update state.
    /// RefreshAsync never throws, so this can be safely fire-and-forgotten from the ctor. In handoff mode
    /// (<c>--update-game</c>) the handoff drives its own RefreshAsync first thing, so the plain refresh is
    /// skipped — two overlapping install-state probes would race to set the same observable properties.</summary>
    private async Task InitializeAsync(bool updateGame)
    {
        if (updateGame)
        {
            StartStatusPolling();
            // Game→launcher handoff — drives Busy/Progress/StatusLine itself via the same
            // UpdateGameAsync/PlayAsync the UI buttons use.
            await RunUpdateGameHandoffAsync();
            return;
        }

        await RefreshAsync();
        if (ClientInstalled && _fsoPath != null)
            DeltaUpdateEngine.SweepStalePatchFiles(_fsoPath); // a failed patch must not be re-applied later
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

    /// <summary>Adaptive server-status poll. While the server is reachable it polls at the steady
    /// cadence; the moment a check comes back offline/unreachable (or throws) it switches to the fast
    /// cadence so a restart/deploy is picked up almost immediately, then relaxes back once it's up. A
    /// manual Refresh nudges this loop (via <see cref="_statusPollGate"/>) so the timer re-schedules instead
    /// of firing a redundant poll on top of the manual one. Every wait uses the shutdown token so the
    /// loop still ends cleanly on exit.</summary>
    private async void StartStatusPolling()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                try { await LoadStatusAsync(); }
                catch (OperationCanceledException) { throw; }
                catch { /* transient network/parse failure — keep the last known state and poll again */ }
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
        await _statusPollGate.WaitAsync(delay, _shutdownCts.Token);
    }

    /// <summary>Periodic launcher self-update check so a launcher left open still learns about a new
    /// version. Runs an initial check immediately, then every <see cref="LauncherUpdatePoll"/> — unless a
    /// manual Refresh's own check (see <see cref="RefreshStatusAsync"/>) nudges this wait early, in which
    /// case the next tick is measured from the manual check instead, so it never fires again right behind
    /// one Refresh just did.</summary>
    private async void StartLauncherUpdatePolling()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                await CheckLauncherUpdateAsync();
                await _launcherUpdateGate.WaitAsync(LauncherUpdatePoll, _shutdownCts.Token);
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
        // Stamp the successful load — never on the offline branch above — so LastUpdatedText always
        // honestly reflects the age of the data actually on screen.
        _lastStatusSuccessAtLocal = DateTime.Now;
        LastUpdatedText = StatusDisplay.FormatLastUpdated(_lastStatusSuccessAtLocal);
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
            LoadCityThumbnail(); // install state just changed — the map thumbnail may have (dis)appeared
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
        // Shared seam (also used by the --update-game handoff's fast path) — missing local version =>
        // an old install from before version.txt => treated as needing an update.
        GameUpdateAvailable = DeltaUpdateEngine.NeedsUpdate(InstalledGameVersion, ServerGameVersion);

        // Desktop-notify a newly detected required game update — once per server version, so the
        // periodic status poll doesn't re-toast the same update every tick.
        if (GameUpdateAvailable && _lastNotifiedGameVersion != ServerGameVersion)
        {
            _lastNotifiedGameVersion = ServerGameVersion;
            DesktopNotify("OpenSO game update", $"A game update was detected — the server is running {ServerGameVersion}.");
        }
    }

    private string? _lastNotifiedGameVersion;


    /// <summary>(Re)loads the city map thumbnail from the installed client — the literal thumbnail.png
    /// the map ships (Content/Cities/city_{CityMapId}/thumbnail.png). No-op when the same file is
    /// already showing; clears the banner when the client is gone. Corrupt/unreadable image => no banner.</summary>
    private void LoadCityThumbnail()
    {
        try
        {
            var path = ClientInstalled && _fsoPath != null
                ? System.IO.Path.Combine(_fsoPath, "Content", "Cities", $"city_{_config.CityMapId}", "thumbnail.png")
                : null;
            if (path != null && !System.IO.File.Exists(path)) path = null;
            if (path == _cityThumbLoadedFrom) return;

            var old = CityThumbnail;
            CityThumbnail = path != null ? new Avalonia.Media.Imaging.Bitmap(path) : null;
            _cityThumbLoadedFrom = path;
            old?.Dispose();
        }
        catch { /* unreadable/corrupt image -> just no banner */ }
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

    /// <summary>Checks the launcher-update feed and updates <see cref="UpdateVersion"/> — the single code
    /// path both the 6-hour background poll (<see cref="StartLauncherUpdatePolling"/>) and a manual Refresh
    /// (<see cref="RefreshStatusAsync"/>) call, so an update found via either shows the exact same banner.
    /// Guarded by <see cref="_launcherUpdateGate"/> so the two never hit the feed concurrently: if a check
    /// is already in flight, this call is a no-op — the in-flight caller's result still lands in
    /// <see cref="UpdateVersion"/> regardless of who asked.</summary>
    private async Task CheckLauncherUpdateAsync()
    {
        if (!_launcherUpdateGate.TryEnter()) return;
        try { UpdateVersion = await _selfUpdate.CheckForLauncherUpdateAsync(); }
        catch (Exception ex) { Log.Warn("Launcher update check failed (offline?)", ex); return; }
        finally { _launcherUpdateGate.Release(); }
        if (UpdateVersion == null) return;

        // Only toast/auto-apply ONCE per detected version — the 6-hour poll and manual Refreshes both
        // land here, and re-prompting for the same version every time would be noise.
        if (_lastHandledLauncherUpdate == UpdateVersion) return;
        _lastHandledLauncherUpdate = UpdateVersion;

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

    private string? _lastHandledLauncherUpdate;

    /// <summary>Manual Refresh (button in the SERVER STATUS card). Unlike the passive background polls,
    /// this EXPLICITLY re-checks for both kinds of update rather than merely nudging them to run sooner:
    /// <list type="number">
    /// <item>Reloads server status (<see cref="LoadStatusAsync"/>), which recomputes the game-update state
    /// from live data.</item>
    /// <item><see cref="RecheckGameUpdateAsync"/> — when the status endpoint didn't answer, step 1 can only
    /// conclude "no update" because the required version is UNKNOWN, not because none is needed. This
    /// re-checks against the canonical client manifest instead, so the "a game update is required" banner
    /// stays truthful even while the status API is unreachable.</item>
    /// <item>Runs the launcher self-update check (<see cref="CheckLauncherUpdateAsync"/>) — the same code
    /// path the 6-hour background poll uses, so an update found here shows the exact same banner.</item>
    /// </list>
    /// Steps 1 and 3 are independent network calls and run concurrently, so the common (server-reachable)
    /// path isn't slowed down. Guarded by <see cref="IsRefreshing"/> so this can't overlap itself;
    /// <see cref="IsRefreshing"/> always clears in a <c>finally</c> — every check below swallows its own
    /// errors (offline / unreachable), so no exception reaches here and nothing spams the UI. Nudges both
    /// poll loops afterwards so neither automatic timer fires — or, for the self-update poll, re-prompts —
    /// right behind this manual one.</summary>
    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var statusTask = LoadStatusAsync();              // refreshes server stats + recomputes game-update state
            var selfUpdateTask = CheckLauncherUpdateAsync();  // independent network call — run alongside it
            await statusTask;
            await RecheckGameUpdateAsync();                   // explicit, fresh check — manifest fallback if needed
            await selfUpdateTask;
        }
        finally
        {
            IsRefreshing = false;
            // Wake both poll loops so their next interval is measured from now — avoids a redundant
            // automatic poll / self-update check landing right on top of this manual one.
            _statusPollGate.Nudge();
            _launcherUpdateGate.Nudge();
        }
    }

    /// <summary>Refresh's explicit game-update re-check. <see cref="LoadStatusAsync"/> already recomputed
    /// <see cref="GameUpdateAvailable"/> from the live server status; that's sufficient — and cheap — when
    /// the status endpoint answered. When it DIDN'T (<see cref="StatsAvailable"/> false), that recompute
    /// forces "no update" purely because <see cref="ServerGameVersion"/> is unknown, which would silently
    /// hide a real pending update behind an unreachable status API. Falls back to a lightweight,
    /// version-only fetch of the canonical client manifest (<see cref="FsoInstaller.FetchManifestVersionAsync"/>
    /// — far cheaper than resolving a full download package) so the banner reflects reality. Best-effort: a
    /// manifest that's ALSO unreachable just leaves the offline-safe state <see cref="LoadStatusAsync"/>
    /// already set (no exception, no error spam) — see <see cref="DeltaUpdateEngine.ShouldFallBackToManifest"/>
    /// for the shared/testable decision of when this fallback is worth attempting at all.</summary>
    private async Task RecheckGameUpdateAsync()
    {
        if (!DeltaUpdateEngine.ShouldFallBackToManifest(StatsAvailable, ClientInstalled)) return;
        var manifestVersion = await new FsoInstaller(_config).FetchManifestVersionAsync();
        if (string.IsNullOrWhiteSpace(manifestVersion)) return; // manifest also unreachable — nothing more to learn
        ServerGameVersion = manifestVersion;
        GameUpdateAvailable = DeltaUpdateEngine.NeedsUpdate(InstalledGameVersion, manifestVersion);
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

    /// <summary>Restores the idle UI state when an install/update/remesh ends. On success the bar is left
    /// FULL rather than reset: the installers' final 1.0 report and the old unconditional `Progress = 0`
    /// in the wrappers' finally blocks landed in the same UI-thread batch, so a completed bar never
    /// actually rendered — users watched it top out at the last slow stage (the ~90% extraction band) and
    /// then empty. Leaving it at 1.0 keeps a visibly finished bar until the next operation starts (every
    /// entry point resets Progress to 0 up front).</summary>
    private void EndOperation(bool succeeded)
    {
        Busy = false;
        ProgressIndeterminate = false;
        Progress = succeeded ? 1 : 0;
        if (!succeeded) ProgressDetail = "";
    }

    /// <summary>Returns the memory a big one-off operation (install/update/remesh) allocated back to the
    /// OS. Extraction works through multi-MB buffers that land on the Large Object Heap; the GC frees
    /// them but by default neither compacts the LOH nor trims the working set, so Task Manager keeps
    /// showing the install's peak for the rest of the session. One aggressive, LOH-compacting collect
    /// after the operation ends releases it. Never called on a hot path.</summary>
    private static void TrimMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

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
        bool ok = false;
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
            ok = true;
        }
        catch (OperationCanceledException) { /* launcher closing */ }
        catch (Exception ex) { HandleOperationFailure("Install", ex); }
        finally { EndOperation(ok); await RefreshAsync(); TrimMemory(); }
    }

    private GameLauncher.Options BuildLaunchOptions() => new()
    {
        GraphicsMode = GraphicsMode == "DirectX" ? "dx" : GraphicsMode == "Software" ? "sw" : "ogl",
        Enable3D = ThreeDMode == "Enabled",
        LanguageCode = 0,
        Windowed = true,
    };

    /// <summary>Updates the installed client to the server's required version. On Windows, first tries the
    /// headless in-launcher delta engine (<see cref="DeltaUpdateEngine"/>): it applies the manifest's
    /// incremental patch CHAIN transactionally — every hop hash-verified before mutation, backed up, and
    /// rolled back on failure, with the version marker advanced per hop. The legacy <c>update.exe</c> is
    /// NEVER invoked. On ANY delta outcome other than a fully-applied chain (non-Windows, no/partial chain,
    /// hash mismatch, apply failure, or an install that predates version stamping) it falls back
    /// automatically to the full-package reinstall (download + atomic swap), which is always safe and
    /// hash-verified and preserves user data. Which path ran is surfaced in the notification.
    /// Returns whether the update succeeded — used by <see cref="RunUpdateGameHandoffAsync"/> to decide
    /// whether it's safe to auto-launch afterwards (a failure must leave the error up, not launch a
    /// possibly-broken install).</summary>
    private async Task<bool> UpdateGameAsync()
    {
        if (Busy || BlockedByRunningGame()) return false;
        ClearError();
        Busy = true; Progress = 0; Section = "DOWNLOADS";
        Notify($"Updating the game to match the server ({ServerGameVersion})…");
        bool ok = false;
        try
        {
            var reporter = MakeReporter();
            await _installGate.WaitAsync(_shutdownCts.Token);
            try
            {
                // Delta path (Windows / win-x64 only — the only platform deltas are published for). Any failure
                // returns false, which drops through to the full package below; the delta engine never throws
                // the update, and never touches user-owned files (Content/config.ini, NLog.config).
                bool appliedDelta = false;
                if (OperatingSystem.IsWindows())
                {
                    var engine = new DeltaUpdateEngine(_config);
                    var deltaTask = engine.TryDeltaUpdateAsync(_fsoPath!, InstalledGameVersion, ServerGameVersion,
                        FsoInstaller.CurrentRid(), reporter);
                    _activeInstall = deltaTask;
                    appliedDelta = await deltaTask;
                }

                if (appliedDelta)
                {
                    Notify("Game updated via incremental delta. Press PLAY to launch.");
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
            ok = true;
            return true;
        }
        catch (OperationCanceledException) { return false; /* launcher closing */ }
        catch (Exception ex) { HandleOperationFailure("Game update", ex); return false; }
        finally { EndOperation(ok); await RefreshAsync(); TrimMemory(); }
    }

    /// <summary>
    /// Entry point for the game→launcher handoff: on Windows, a Launcher-managed install whose client
    /// detects a version mismatch starts the launcher with <c>--update-game</c> and exits (see
    /// BUILD_AND_TEST.md → "Game → launcher handoff"). Runs the same version check → update → launch flow
    /// PLAY/UPDATE GAME drive manually, automatically and exactly once (<see cref="_updateGameHandoffRan"/>
    /// guards re-entry): refreshes install state, takes one deterministic read of the server's required
    /// version, updates ONLY if needed (skips straight to launch when already current — no gratuitous
    /// reinstall), and auto-launches the game on success. On failure (or if the client isn't installed at
    /// all here) it leaves the normal error/status in the UI and does NOT retry or launch.
    /// </summary>
    public async Task RunUpdateGameHandoffAsync()
    {
        if (_updateGameHandoffRan) return;
        _updateGameHandoffRan = true;

        await RefreshAsync(); // populates ClientInstalled / _fsoPath / InstalledGameVersion
        if (!ClientInstalled)
        {
            Notify("The game client isn't installed here yet — install it, then press PLAY.");
            return;
        }

        await LoadStatusAsync(); // one deterministic fetch of the server's required version before deciding

        // Skip the reinstall entirely when the install already matches what's required (fast path — see
        // NeedsUpdate). When the status endpoint can't be reached we can't rule out a mismatch — that's
        // exactly why the client handed off to us — so err on the side of updating rather than assuming OK.
        bool needsUpdate = !StatsAvailable || DeltaUpdateEngine.NeedsUpdate(InstalledGameVersion, ServerGameVersion);

        bool ok = !needsUpdate || await UpdateGameAsync();
        if (ok) await PlayAsync();
    }

    [RelayCommand]
    private async Task InstallRemeshAsync()
    {
        if (Busy || BlockedByRunningGame()) return;
        if (!ClientInstalled) { Notify("Install the OpenSO client first, then add the 3D mesh pack."); Section = "INSTALLER"; return; }

        ClearError();
        Busy = true; Progress = 0; Section = "DOWNLOADS";
        Notify("Installing the 3D mesh pack…");
        bool ok = false;
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
            ok = true;
        }
        catch (OperationCanceledException) { /* launcher closing */ }
        catch (Exception ex) { HandleOperationFailure("3D mesh pack install", ex); }
        finally { EndOperation(ok); await RefreshAsync(); TrimMemory(); }
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
