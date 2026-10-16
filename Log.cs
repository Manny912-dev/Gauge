using System.Runtime.InteropServices;

namespace Gauge;

/// <summary>Small diagnostic log under %AppData%\Gauge, for testers to send back with a report.</summary>
internal static class Log
{
    static readonly object s_lock = new();
    static readonly HashSet<string> s_once = new();

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gauge", "gauge.log");

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 256 * 1024) info.Delete();
        }
        catch { }
        Write($"---- Gauge {Program.Version} started on {RuntimeInformation.OSDescription}, .NET {Environment.Version}");
    }

    public static void Write(string message)
    {
        try
        {
            lock (s_lock) File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Records a recurring failure the first time only, so a per-second error cannot flood the file.</summary>
    public static void Once(string key, Exception ex)
    {
        lock (s_lock) if (!s_once.Add(key)) return;
        Write($"{key}: {ex}");
    }
}
