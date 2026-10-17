using System.Diagnostics;
using Velopack;

namespace Gauge;

static class Program
{
    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    [STAThread]
    static void Main(string[] args)
    {
        // Must run first: the installer and updater start Gauge with their own arguments to finish
        // an install, update or uninstall. Gauge applies a downloaded update itself, below, so the
        // updater's own progress window never appears. Uninstalling removes "Start with Windows".
        VelopackApp.Build().SetAutoApplyOnStartup(false).OnBeforeUninstallFastCallback(_ => Startup.Remove()).Run();

        ApplicationConfiguration.Initialize();
        if (args.Length >= 2 && args[0] == "--snapshot") { Snapshot.Run(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--icon") { Snapshot.WriteIcon(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--banner") { Snapshot.WriteBanner(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--splash") { Snapshot.WriteSplash(args[1], args.Length >= 3 ? args[2] : null); return; }
        if (args.Length >= 2 && args[0] == "--board-lighting") Environment.Exit(BoardLighting.Run(args[1]));
        if (args.Length >= 2 && args[0] == "--sensor-probe") { Snapshot.SensorProbe(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--tray-menu") { Snapshot.TrayMenu(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--kraken-test")
        {
            try { Environment.Exit(KrakenTest.Run(args[1]) == 0 ? 0 : 1); }
            catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "report.txt"), "CRASH " + ex); Environment.Exit(2); }
        }
        if (args.Length >= 2 && args[0] == "--qmk-test") { File.WriteAllText(args[1], QmkViaKeyboard.SelfTest(out int failed)); Environment.Exit(failed == 0 ? 0 : 1); }
        if (args.Length >= 2 && args[0] == "--orgb-test") { Snapshot.OpenRgbTest(args[1]); return; }
        if (args.Length >= 3 && args[0] == "--light-test") { Snapshot.LightTest(args[1], args[2]); return; }

        bool background = args.Contains(Startup.BackgroundArg);
        if (Updater.ApplyPendingAtStartup(background ? [Startup.BackgroundArg] : null)) return; // the updater swaps versions and reopens Gauge

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);

        // One window per user. "--restart" is passed when relaunching as administrator,
        // so the new copy waits for the old one to finish closing.
        Mutex mutex;
        try { mutex = new Mutex(false, @"Local\Gauge.SingleInstance"); }
        catch (UnauthorizedAccessException)
        {
            // Created by a copy running as administrator, which a normal launch may not open: it is running.
            if (!background) ShowExisting();
            return;
        }
        using var _ = mutex;
        if (!Acquire(mutex, args.Contains("--restart") ? 5000 : 0))
        {
            if (!background) ShowExisting(); // signing in again must not pop the window up
            return;
        }
        try
        {
            Log.Start();
            Application.Run(new MainForm(background));
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }

    static bool Acquire(Mutex mutex, int timeoutMs)
    {
        try { return mutex.WaitOne(timeoutMs); }
        catch (AbandonedMutexException) { return true; } // previous copy was killed
    }

    /// <summary>Asks the running copy to show its window. Sent to every top-level window, so it also reaches one hidden in the tray.</summary>
    public static readonly uint ShowMessage = Native.RegisterWindowMessageW("Gauge.ShowWindow");

    static void ShowExisting()
    {
        Native.AllowSetForegroundWindow(-1); // ASFW_ANY: the running copy may come to the front
        Native.PostMessageW(0xFFFF, ShowMessage, 0, 0); // HWND_BROADCAST
    }

    static void Crash(Exception? ex)
    {
        Log.Write("CRASH " + ex);
        try
        {
            MessageBox.Show($"Gauge hit a problem and has to close.\n\nDetails were saved to:\n{Log.FilePath}\n\n{ex?.GetType().Name}: {ex?.Message}",
                "Gauge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
        Environment.Exit(1);
    }
}
