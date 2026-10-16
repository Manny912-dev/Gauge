namespace Gauge;

/// <summary>User preferences, kept as a small key=value file under %AppData%\Gauge.</summary>
internal static class Settings
{
    static readonly object s_lock = new();
    static Dictionary<string, string>? s_values;

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gauge", "settings.ini");

    /// <summary>Every key in the file, loaded once. Unknown keys are kept, so one feature saving never drops another's values.</summary>
    static Dictionary<string, string> Values
    {
        get
        {
            if (s_values != null) return s_values;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }
            }
            catch { }
            return s_values = values;
        }
    }

    public static string? Get(string key)
    {
        lock (s_lock) return Values.GetValueOrDefault(key);
    }

    public static void Set(params (string Key, string? Value)[] entries)
    {
        lock (s_lock)
        {
            foreach (var (key, value) in entries)
            {
                if (value == null) Values.Remove(key);
                else Values[key] = value;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var lines = new List<string>();
                foreach (var kv in Values) lines.Add($"{kv.Key}={kv.Value}");
                lines.Sort(StringComparer.Ordinal);
                File.WriteAllLines(FilePath, lines);
            }
            catch { }
        }
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color? ParseColor(string? text)
    {
        text = text?.Trim().TrimStart('#');
        if (text is not { Length: 6 }) return null;
        try
        {
            int rgb = Convert.ToInt32(text, 16);
            return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }
        catch (FormatException) { return null; }
    }

    public static Color? LoadAccent() => ParseColor(Get("accent"));

    public static void SaveAccent(Color c) => Set(("accent", Hex(c)));
}
