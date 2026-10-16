using Velopack;
using Velopack.Sources;

namespace Gauge;

internal enum UpdateStatus { Off, NotInstalled, NoServer, UpToDate, Checking, Downloading, Ready, Failed }

/// <summary>
/// Checks the release feed, downloads a newer version in the background and holds it until
/// the next restart. Only an installed copy updates; a loose build has nothing to swap.
/// </summary>
internal sealed class Updater : IDisposable
{
    /// <summary>
    /// Where releases are published: a web address (or folder) holding the files from release\updates.
    /// "update.url" in settings.ini overrides it, for testing.
    /// </summary>
    const string DefaultFeed = "https://github.com/Manny912-dev/Gauge";

    /// <summary>How often a running copy looks for a new version. Each check is one small file.</summary>
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    /// <summary>Waits after failed checks: a dropped connection is retried quickly, a longer outage backs off.</summary>
    static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    public readonly object Sync = new();
    public UpdateStatus Status { get; private set; } = UpdateStatus.Off;
    public int Progress { get; private set; }
    public string? NewVersion { get; private set; }
    public event Action? Changed;

    readonly ManualResetEventSlim _stop = new(false);
    readonly AutoResetEvent _kick = new(false);
    UpdateManager? _manager;
    VelopackAsset? _ready;
    Thread? _thread;

    static string Feed => Settings.Get("update.url") ?? DefaultFeed;

    /// <summary>A GitHub repository address reads its releases; anything else is a plain web folder or path.</summary>
    static UpdateManager CreateManager(string feed)
        => feed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            ? new UpdateManager(new GithubSource(feed, null, false))
            : new UpdateManager(feed);

    /// <summary>The version this copy was just updated to, when this is the first run after an update.</summary>
    public static string? JustUpdatedTo { get; private set; }

    /// <summary>
    /// Applies an update downloaded in an earlier run, with no window of the updater's own.
    /// True when the swap has been handed off and this process should exit at once.
    /// </summary>
    /// <param name="restartArgs">Passed to the reopened Gauge, so a copy started in the tray comes back in the tray.</param>
    public static bool ApplyPendingAtStartup(string[]? restartArgs)
    {
        try
        {
            // The version a previous run tried to move to. Reaching it means the update worked;
            // still being behind it means it failed, and it is not retried, so a broken package
            // can never trap Gauge in a restart loop.
            string? attempted = Settings.Get("update.attempt");
            if (attempted != null)
            {
                Settings.Set(("update.attempt", null));
                if (attempted == Program.Version) JustUpdatedTo = attempted;
                else Log.Write($"update: {attempted} did not apply; staying on {Program.Version}");
                return false;
            }

            if (Settings.Get("update.auto") == "0" || Feed.Length == 0) return false;
            var manager = CreateManager(Feed);
            if (!manager.IsInstalled || manager.UpdatePendingRestart is not { } pending) return false;

            Settings.Set(("update.attempt", pending.Version.ToString()));
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true, restartArgs);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("update: startup apply failed: " + ex.Message);
            return false;
        }
    }

    public bool Automatic
    {
        get => Settings.Get("update.auto") != "0";
        set
        {
            Settings.Set(("update.auto", value ? "1" : "0"));
            if (value) _kick.Set();
            else Set(UpdateStatus.Off);
        }
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Updater" };
        _thread.Start();
    }

    void Run()
    {
        if (_stop.Wait(4000)) return; // let the window settle first
        int failures = 0;
        while (!_stop.IsSet)
        {
            if (Automatic) Check();
            bool failed;
            lock (Sync) failed = Status == UpdateStatus.Failed;
            failures = failed ? failures + 1 : 0;
            var wait = failed && failures <= RetryDelays.Length ? RetryDelays[failures - 1] : Interval;
            WaitHandle.WaitAny([_stop.WaitHandle, _kick], wait);
        }
    }

    void Set(UpdateStatus status)
    {
        lock (Sync)
        {
            if (Status == status && status != UpdateStatus.Downloading) return;
            if (Status != status && status is not (UpdateStatus.Checking or UpdateStatus.Downloading)) Log.Write($"update: {status}");
            Status = status;
        }
        Changed?.Invoke();
    }

    void Check()
    {
        try
        {
            string feed = Feed;
            if (feed.Length == 0) { Set(UpdateStatus.NoServer); return; }

            var manager = _manager ??= CreateManager(feed);
            if (!manager.IsInstalled) { Set(UpdateStatus.NotInstalled); return; }

            Set(UpdateStatus.Checking);
            var info = manager.CheckForUpdates();
            if (info == null)
            {
                // Nothing newer on the feed; an earlier download may still be waiting for a restart.
                if (manager.UpdatePendingRestart is { } pending) Hold(pending);
                else Set(UpdateStatus.UpToDate);
                return;
            }

            lock (Sync) { Progress = 0; NewVersion = info.TargetFullRelease.Version.ToString(); }
            Set(UpdateStatus.Downloading);
            manager.DownloadUpdates(info, p => { lock (Sync) Progress = p; Changed?.Invoke(); });
            Hold(info.TargetFullRelease);
        }
        catch (Exception ex)
        {
            Log.Write("update: failed: " + ex.Message);
            Set(UpdateStatus.Failed);
        }
    }

    void Hold(VelopackAsset asset)
    {
        lock (Sync) { _ready = asset; NewVersion = asset.Version.ToString(); }
        Log.Write($"update: {asset.Version} downloaded, applies on restart");
        Set(UpdateStatus.Ready);
    }

    /// <summary>
    /// Arranges for the downloaded update to be applied and Gauge reopened once this process exits.
    /// The caller then closes the window normally, so shutdown stays clean. False if nothing is ready.
    /// </summary>
    public bool ApplyOnExit(string[]? restartArgs = null)
    {
        VelopackAsset? asset;
        lock (Sync) asset = _ready;
        if (_manager == null || asset == null) return false;
        try
        {
            Settings.Set(("update.attempt", asset.Version.ToString()));
            _manager.WaitExitThenApplyUpdates(asset, silent: true, restart: true, restartArgs);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("update: could not start the updater: " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(300);
    }
}
