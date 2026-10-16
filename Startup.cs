using Microsoft.Win32;

namespace Gauge;

/// <summary>
/// "Start with Windows": one value under the user's Run key, so it needs no administrator rights
/// and Windows' own Startup apps page can switch it off. Gauge starts from it in the tray.
/// </summary>
internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "Gauge";

    /// <summary>Passed by the Run key: start with the window hidden in the tray.</summary>
    public const string BackgroundArg = "--background";

    /// <summary>
    /// The exe to start. An installed copy is started through Velopack's launcher one folder up,
    /// whose path stays the same across updates; anything else is started directly.
    /// </summary>
    static string ExePath
    {
        get
        {
            string self = Environment.ProcessPath!;
            string? root = Path.GetDirectoryName(Path.GetDirectoryName(self));
            if (root != null)
            {
                string stub = Path.Combine(root, "Gauge.exe");
                if (File.Exists(stub) && File.Exists(Path.Combine(root, "Update.exe"))) return stub;
            }
            return self;
        }
    }

    static string Command => $"\"{ExePath}\" {BackgroundArg}";

    public static bool Enabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) key.SetValue(ValueName, Command);
                else key.DeleteValue(ValueName, false);
                Log.Write($"startup: {(value ? "on" : "off")}");
            }
            catch (Exception ex) { Log.Write("startup: could not change: " + ex.Message); }
        }
    }

    /// <summary>
    /// Points an existing entry at this installed copy, so a reinstalled Gauge keeps starting.
    /// A loose build (a zip copy, a developer build) leaves the entry alone.
    /// </summary>
    public static void Refresh()
    {
        if (ExePath == Environment.ProcessPath) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, Command);
        }
        catch { }
    }

    /// <summary>Called by the uninstaller: nothing may be left that starts a removed program.</summary>
    public static void Remove()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, false);
        }
        catch { }
    }
}
