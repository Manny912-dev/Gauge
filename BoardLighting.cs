using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Gauge;

/// <summary>
/// Motherboard and RAM lighting. Those devices sit on the SMBus, which OpenRGB reaches through the
/// PawnIO kernel driver and only with administrator rights. One elevated setup:
///   1. downloads the official PawnIO installer, checks it against a pinned fingerprint, installs it silently;
///   2. copies the engine and its settings folder into Program Files, where only administrators can write,
///      so nothing running as the user can change what then runs with full rights;
///   3. registers a hidden scheduled task that starts that engine as the system account at boot,
///      listening on this PC only, on a fixed port Gauge connects to.
/// Afterwards Gauge uses that single engine for every device. Turning it off reverses steps 2 and 3;
/// PawnIO stays installed, since other software may use it, and can be removed from Windows' app list.
/// </summary>
internal static class BoardLighting
{
    public const int Port = 6745;
    const string TaskName = "Gauge Lighting Engine";
    const string PawnIoUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
    const string PawnIoSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";

    static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Gauge");
    static string EngineDir => Path.Combine(Root, "engine");
    static string ConfigDir => Path.Combine(Root, "engine-config");
    static string Marker => Path.Combine(Root, "board-lighting.txt");
    static string EngineExe => Path.Combine(EngineDir, "OpenRGB.exe");

    /// <summary>True once setup has completed on this PC (for any user).</summary>
    public static bool Enabled => File.Exists(Marker) && File.Exists(EngineExe);

    /// <summary>True when the protected engine is older than the one this Gauge ships, so setup should run again.</summary>
    public static bool NeedsRefresh
    {
        get
        {
            try { return Enabled && File.ReadAllText(Marker).Trim() != Fingerprint(Path.Combine(OrgbEngine.EngineFolder, "OpenRGB.exe")); }
            catch { return false; }
        }
    }

    static string Fingerprint(string exe)
    {
        var info = new FileInfo(exe);
        return info.Exists ? $"{info.Length}:{FileVersionInfo.GetVersionInfo(exe).FileVersion}" : "missing";
    }

    // ---------------------------------------------------------------- the unelevated side

    /// <summary>Runs setup ("enable") or removal ("disable") behind one administrator prompt. Returns a message for the user.</summary>
    public static string RunElevated(string action)
    {
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            info.ArgumentList.Add("--board-lighting");
            info.ArgumentList.Add(action);
            using var p = Process.Start(info);
            if (p == null) return "Setup did not start.";
            p.WaitForExit();
            return Describe(action, p.ExitCode);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "Cancelled. Motherboard and RAM lighting needs administrator approval once.";
        }
    }

    static string Describe(string action, int code) => (action, code) switch
    {
        ("enable", 0) => "Motherboard and RAM lighting is on.",
        ("enable", 3010) => "Almost done: restart your PC to finish installing the lighting driver.",
        ("enable", 10) => "Could not download the lighting driver. Check the internet connection and try again.",
        ("enable", 11) => "The downloaded lighting driver did not match the expected file, so it was not installed.",
        ("enable", 12) => "The lighting driver failed to install.",
        ("disable", 0) => "Motherboard and RAM lighting is off.",
        _ => $"Setup did not finish (code {code}). Details are in the log.",
    };

    // ---------------------------------------------------------------- the elevated side

    /// <summary>Entry point for "Gauge.exe --board-lighting enable|disable|dryrun". Returns the process exit code.</summary>
    public static int Run(string action)
    {
        bool dry = action == "dryrun";
        if (!dry && !IsAdministrator()) return 5; // access denied
        try
        {
            return action switch
            {
                "enable" => Enable(dry: false),
                "dryrun" => Enable(dry: true),
                "disable" => Disable(),
                _ => 87, // invalid parameter
            };
        }
        catch (Exception ex)
        {
            Log.Write($"board lighting {action} failed: {ex}");
            return 1;
        }
    }

    static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static int Enable(bool dry)
    {
        bool pawnIoNeedsRestart = false;
        var plan = new StringBuilder();
        void Step(string s) { plan.AppendLine(s); Log.Write("board lighting: " + s); }

        // 1. PawnIO, downloaded straight into the protected folder so nothing can swap it before it runs
        string setup = Path.Combine(Root, "setup", "PawnIO_setup.exe");
        Step($"download {PawnIoUrl} to {setup}, require sha256 {PawnIoSha256}, run it with -install -silent");
        if (!dry)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                File.WriteAllBytes(setup, http.GetByteArrayAsync(PawnIoUrl).GetAwaiter().GetResult());
            }
            catch (Exception ex) { Log.Write("board lighting: download failed: " + ex.Message); return 10; }
            if (!Sha256(setup).Equals(PawnIoSha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(setup); return 11; }
            int code = RunAndWait(setup, "-install", "-silent");
            File.Delete(setup);
            Step($"PawnIO installer returned {code}");
            pawnIoNeedsRestart = code == 3010;
            if (code is not (0 or 3010)) return 12; // 3010: carry on; the driver loads after a restart
        }

        // 2. Protected copy of the engine, and a settings folder only administrators can change
        Step($"stop task '{TaskName}' and any engine running from {EngineDir}");
        if (!dry) StopEngine();
        Step($"copy {OrgbEngine.EngineFolder} to {EngineDir}");
        if (!dry) CopyFolder(OrgbEngine.EngineFolder, EngineDir);
        Step($"settings folder {ConfigDir} with Gauge's built-in keyboard detectors switched off");
        if (!dry)
        {
            Directory.CreateDirectory(ConfigDir);
            OrgbEngine.SeedConfig(ConfigDir);
        }

        // 3. Start at boot as the system account, hidden, local-only
        string xml = TaskXml();
        Step("register scheduled task:\n" + xml);
        if (dry)
        {
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml.Substring(xml.IndexOf("<Task", StringComparison.Ordinal))); // checks the XML is well formed
            Console.Out.Write(plan.ToString());
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "gauge-board-lighting-dryrun.txt"), plan.ToString());
            return 0;
        }
        string xmlFile = Path.Combine(Root, "setup", "task.xml");
        File.WriteAllText(xmlFile, xml, Encoding.Unicode);
        int created = RunAndWait("schtasks.exe", "/Create", "/TN", TaskName, "/XML", xmlFile, "/F");
        File.Delete(xmlFile);
        if (created != 0) { Step($"schtasks /Create returned {created}"); return 13; }
        RunAndWait("schtasks.exe", "/Run", "/TN", TaskName);

        File.WriteAllText(Marker, Fingerprint(EngineExe));
        return pawnIoNeedsRestart ? 3010 : 0;
    }

    static int Disable()
    {
        RunAndWait("schtasks.exe", "/End", "/TN", TaskName);
        RunAndWait("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
        StopEngine();
        Thread.Sleep(500);
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        Log.Write("board lighting: turned off; PawnIO left installed");
        return 0;
    }

    static void StopEngine()
    {
        RunAndWait("schtasks.exe", "/End", "/TN", TaskName);
        foreach (var p in Process.GetProcessesByName("OpenRGB"))
        {
            using (p)
            {
                try
                {
                    if (p.MainModule?.FileName?.StartsWith(EngineDir, StringComparison.OrdinalIgnoreCase) == true) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
            }
        }
    }

    static string TaskXml()
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s)!;
        string args = $"--server --server-host 127.0.0.1 --server-port {Port} --config \"{ConfigDir}\" --noautoconnect";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Gauge</Author>
                <Description>Runs Gauge's lighting engine with the access that motherboard and RAM lighting needs. Remove it from Gauge's Lighting tab.</Description>
              </RegistrationInfo>
              <Triggers>
                <BootTrigger><Enabled>true</Enabled></BootTrigger>
              </Triggers>
              <Principals>
                <Principal id="Engine"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>true</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>
              </Settings>
              <Actions Context="Engine">
                <Exec>
                  <Command>{X(EngineExe)}</Command>
                  <Arguments>{X(args)}</Arguments>
                  <WorkingDirectory>{X(EngineDir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    static int RunAndWait(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }
}
