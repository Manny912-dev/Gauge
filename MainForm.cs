using System.ComponentModel;
using System.Diagnostics;

namespace Gauge;

internal sealed unsafe class MainForm : Form
{
    readonly Telemetry _telemetry = new();
    readonly Lighting _lighting = new();
    readonly Updater _updater = new();
    readonly Renderer _renderer;
    readonly Action _invalidate;
    int _hover;
    bool _loadingSpecs;
    int _drag; // hit id of the slider being dragged, or 0
    Tray? _tray;
    bool _hidden;   // the window is away in the tray (started from Windows, or closed with the tray on)
    bool _quitting; // a real exit: the close button would otherwise only hide the window

    float Dpi => DeviceDpi / 96f;

    /// <summary>"Keep running in the tray": the close button hides the window and Gauge keeps going.</summary>
    static bool KeepInTray
    {
        get => Settings.Get("tray.keep") == "1";
        set => Settings.Set(("tray.keep", value ? "1" : null));
    }

    public MainForm(bool background)
    {
        _hidden = background;
        Text = "Gauge";
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Renderer.Background;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        float s = Dpi;
        _renderer = new Renderer(_telemetry, s);
        if (Settings.LoadAccent() is Color accent) _renderer.SetAccent(accent);
        KeyPreview = true;
        _renderer.Updates = _updater;
        _renderer.UpdatedTo = Updater.JustUpdatedTo;
        _renderer.UpdatedNoticeUntil = Environment.TickCount64 + 12000;
        _updater.Changed += OnUpdater;
        _updater.Start();
        _renderer.Lights = _lighting;
        _lighting.Changed += OnTelemetry;
        _lighting.Changed += OnLightingChanged;
        _lighting.ReadSensor = ReadForScreen;
        _lighting.ScanAsync(delayMs: 1500); // read-only; after the window is up
        _invalidate = Invalidate;

        // The window draws its own title bar, so its outer size is its client size.
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Size = new Size(Math.Min((int)(1060 * s), area.Width - 24), Math.Min((int)(680 * s), area.Height - 12));
        MinimumSize = new Size(Math.Min((int)(880 * s), area.Width), Math.Min((int)(580 * s), area.Height));
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        _renderer.StartWithWindows = Startup.Enabled;
        _renderer.KeepInTray = KeepInTray;
        if (_renderer.StartWithWindows) Startup.Refresh();
        UpdateTray();

        _telemetry.Paused = _hidden;
        _telemetry.Updated += OnTelemetry;
        _telemetry.Start();
    }

    void OnTelemetry()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(_invalidate); } catch (InvalidOperationException) { }
    }

    void OnUpdater()
    {
        OnTelemetry();
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(UpdateWhileHidden); } catch (InvalidOperationException) { }
    }

    /// <summary>
    /// A copy living in the tray has no "Restart" button to press, so a downloaded update is applied
    /// straight away and Gauge comes back in the tray. Never while the window is on screen.
    /// </summary>
    void UpdateWhileHidden()
    {
        if (!_hidden || _quitting || _updater.Status != UpdateStatus.Ready) return;
        Log.Write("update: applying while in the tray");
        if (_updater.ApplyOnExit([Startup.BackgroundArg])) Quit();
    }

    // ---------------------------------------------------------------- tray

    /// <summary>A copy started in the tray stays invisible until it is asked for; the handle still exists, so it can be reached.</summary>
    protected override void SetVisibleCore(bool value)
    {
        if (value && _hidden)
        {
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Let a second, non-elevated launch reach a copy restarted as administrator.
        Native.ChangeWindowMessageFilterEx(Handle, Program.ShowMessage, 1, 0); // MSGFLT_ALLOW

        // Windows 11: rounded corners like the system's own windows, with the outline in the colour
        // the renderer would draw (white at 26/255 over the background). Windows 10 refuses both and
        // keeps the square outline drawn by the renderer.
        int round = 2; // DWMWCP_ROUND
        if (Native.DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)) == 0) // DWMWA_WINDOW_CORNER_PREFERENCE
        {
            int outline = 0x262423; // COLORREF for #232426
            Native.DwmSetWindowAttribute(Handle, 34, ref outline, sizeof(int)); // DWMWA_BORDER_COLOR
            _renderer.SystemFrame = true;
        }
        Log.Write($"window corners: {(_renderer.SystemFrame ? "rounded by Windows" : "square")}");
    }

    /// <summary>The icon is there while the window is away, and all the time when "Keep running in the tray" is on.</summary>
    void UpdateTray()
    {
        bool wanted = _hidden || _renderer.KeepInTray;
        if (wanted && _tray == null)
        {
            _tray = new Tray(DeviceDpi);
            _tray.OpenRequested += ShowFromTray;
            _tray.QuitRequested += Quit;
        }
        if (_tray != null) _tray.Visible = wanted;
        if (!wanted && _tray != null) { _tray.Dispose(); _tray = null; }
    }

    void ShowFromTray()
    {
        bool wasHidden = _hidden;
        _hidden = false;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (wasHidden) UpdateTray();
        UpdatePause();
        Invalidate();
    }

    void HideToTray()
    {
        _hidden = true;
        _drag = 0;
        Hide();
        UpdateTray();
        UpdatePause();
        if (Settings.Get("tray.told") == null && _tray != null)
        {
            Settings.Set(("tray.told", "1"));
            _tray.Notice("Gauge is still running", "It's in the notification area by the clock. Right-click its icon to quit.");
        }
        GC.Collect(2, GCCollectionMode.Aggressive, true, true); // give back what the window used while it is away
        UpdateWhileHidden();
    }

    void Quit()
    {
        _quitting = true;
        Close();
    }

    // The Kraken's screen may show a CPU or GPU value, which needs sampling even while the window is away.
    void UpdatePause() => _telemetry.Paused = (_hidden || WindowState == FormWindowState.Minimized) && !(_lighting.Screen?.NeedsTelemetry ?? false);

    void OnLightingChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(UpdatePause); } catch (InvalidOperationException) { }
    }

    /// <summary>A CPU or GPU value for the Kraken's screen, read on the screen's own thread.</summary>
    float ReadForScreen(LcdReading reading)
    {
        lock (_telemetry.Sync)
        {
            var gpu = _telemetry.Gpus.Count > 0 ? _telemetry.Gpus[Math.Min(_telemetry.GpuIndex, _telemetry.Gpus.Count - 1)] : null;
            return reading switch
            {
                LcdReading.CpuTemp => _telemetry.CpuTemp,
                LcdReading.CpuLoad => _telemetry.CpuLoad,
                LcdReading.CpuClock => _telemetry.CpuClock,
                LcdReading.GpuTemp => gpu?.Temp ?? float.NaN,
                LcdReading.GpuLoad => gpu?.Load ?? float.NaN,
                LcdReading.GpuClock => gpu?.Clock ?? float.NaN,
                _ => float.NaN,
            };
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && _renderer.KeepInTray && !_quitting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84;
        if (m.Msg == Program.ShowMessage) { ShowFromTray(); return; }
        switch (m.Msg)
        {
            // Claim the whole window as client area: no system title bar or borders, but the
            // window keeps its resize frame style, so snapping, shadow and animations still work.
            case WM_NCCALCSIZE when m.WParam != 0:
                if (Native.IsZoomed(Handle))
                {
                    int f = Native.GetSystemMetricsForDpi(32, (uint)DeviceDpi) + Native.GetSystemMetricsForDpi(92, (uint)DeviceDpi);
                    int* rect = (int*)m.LParam; // a maximised window overhangs the screen by its frame
                    rect[0] += f; rect[1] += f; rect[2] -= f; rect[3] -= f;
                }
                m.Result = 0;
                return;

            case WM_NCHITTEST:
                long lp = m.LParam;
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                m.Result = HitTest(p);
                return;
        }
        base.WndProc(ref m);
    }

    nint HitTest(Point p)
    {
        const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        if (!Native.IsZoomed(Handle))
        {
            int b = (int)(6 * Dpi);
            bool l = p.X < b, r = p.X >= ClientSize.Width - b, t = p.Y < b, bt = p.Y >= ClientSize.Height - b;
            if (t && l) return HTTOPLEFT;
            if (t && r) return HTTOPRIGHT;
            if (bt && l) return HTBOTTOMLEFT;
            if (bt && r) return HTBOTTOMRIGHT;
            if (l) return HTLEFT;
            if (r) return HTRIGHT;
            if (t) return HTTOP;
            if (bt) return HTBOTTOM;
        }
        int id = _renderer.HitAt(p);
        bool clickable = id is > 0 and < Renderer.HitRow;
        return !clickable && p.Y < Renderer.ChromeHeight * Dpi ? HTCAPTION : HTCLIENT;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        _renderer.Maximized = WindowState == FormWindowState.Maximized;
        lock (_telemetry.Sync)
            _renderer.Render(e.Graphics, ClientSize.Width, ClientSize.Height);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdatePause();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _renderer.SetScale(e.DeviceDpiNew / 96f);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _renderer.Mouse = e.Location;
        if (_drag == Renderer.HitHue) { _renderer.SetHueFromX(e.X); Invalidate(); return; }
        if (_drag == Renderer.HitCropBox) { _renderer.CropMove(e.Location); Invalidate(); return; }
        if (_drag != 0)
        {
            if (_renderer.LightingDrag(_drag, e.X)) ApplyLighting();
            Invalidate();
            return;
        }
        int id = _renderer.HitAt(e.Location);
        if (id == _hover) return;
        _hover = id;
        Cursor = id is > 0 and < Renderer.HitMin or >= Renderer.HitSettings and < Renderer.HitLast ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _renderer.Mouse = new PointF(-1, -1);
        if (_hover != 0) { _hover = 0; Cursor = Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int id = _renderer.HitAt(e.Location);
        if (_renderer.Crop != null && id is >= Renderer.HitCropShade and <= Renderer.HitCropCancel)
        {
            switch (id)
            {
                case Renderer.HitCropBox: _drag = id; _renderer.CropGrab(e.Location); break;
                case Renderer.HitCropUse:
                    if (_renderer.EndCrop(use: true) is { } chosen) _lighting.Screen?.Import(chosen.Path, chosen.Square);
                    break;
                case Renderer.HitCropCancel: _renderer.EndCrop(use: false); break;
            }
            Invalidate();
            return;
        }
        switch (id)
        {
            case Renderer.HitSettings: ShowPage(Renderer.PageSettings); break;
            case Renderer.HitHue: _drag = id; _renderer.SetHueFromX(e.X); break;
            case Renderer.HitLighting: ShowPage(Renderer.PageLighting); break;
            case Renderer.HitRescan: _lighting.ScanAsync(); break;
            case Renderer.HitBoard or Renderer.HitBoardOff when !_renderer.BoardBusy:
                BoardLightingAsync(id == Renderer.HitBoard ? "enable" : "disable");
                break;
            case Renderer.HitCopyDevices:
                try { Clipboard.SetText(_lighting.DeviceReport()); _renderer.CopiedUntil = Environment.TickCount64 + 1500; } catch { }
                break;
            case >= Renderer.HitZone and < Renderer.HitSwatch:
                if (id is Renderer.HitLightHue or Renderer.HitLightSat) _drag = id;
                if (_renderer.LightingPress(id, e.X)) ApplyLighting();
                if (_drag == 0) SaveLighting(); // sliders save when released
                break;
            case Renderer.HitReset: SetAccent(Renderer.DefaultAccent); break;
            case Renderer.HitAutoUpdate: _updater.Automatic = !_updater.Automatic; break;
            case Renderer.HitAutoStart:
                Startup.Enabled = !_renderer.StartWithWindows;
                _renderer.StartWithWindows = Startup.Enabled; // what the registry now says, in case the change was refused
                break;
            case Renderer.HitTray:
                KeepInTray = _renderer.KeepInTray = !_renderer.KeepInTray;
                UpdateTray();
                break;
            case Renderer.HitUpdate:
                if (_updater.ApplyOnExit()) Quit(); // the updater swaps versions once this window is gone, then reopens Gauge
                return;
            case Renderer.HitLcdUpload: ChooseScreenPicture(); break;
            case >= Renderer.HitLcdItem and <= Renderer.HitLcdLogo or >= Renderer.HitLcdMode and < Renderer.HitCropShade:
                if (id is Renderer.HitLcdHue1 or Renderer.HitLcdHue2) _drag = id;
                _renderer.ScreenPress(id, e.X);
                UpdatePause();
                break;
            case >= Renderer.HitSwatch and < Renderer.HitSwatch + 8: SetAccent(Renderer.Presets[id - Renderer.HitSwatch]); break;
            case Renderer.HitMonitor: ShowPage(Renderer.PageMonitor); break;
            case Renderer.HitSpecs: ShowPage(Renderer.PageSpecs); break;
            case Renderer.HitElevate: RestartElevated(); return;
            case Renderer.HitCopy when _renderer.Specs != null:
                try { Clipboard.SetText(Specs.ToText(_renderer.Specs)); _renderer.CopiedUntil = Environment.TickCount64 + 1500; } catch { }
                break;
            case Renderer.HitGpu:
                lock (_telemetry.Sync) _telemetry.NextGpu();
                break;
            case Renderer.HitMin: WindowState = FormWindowState.Minimized; return;
            case Renderer.HitMax: WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; return;
            case Renderer.HitClose: Close(); return;
            case >= Renderer.HitSort and < Renderer.HitSort + 6:
                lock (_telemetry.Sync)
                {
                    _telemetry.Processes.SortKey = id - Renderer.HitSort;
                    _telemetry.Processes.Sort();
                }
                break;
            default: return;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_drag == 0) return;
        if (_drag == Renderer.HitCropBox) { _drag = 0; return; }
        bool wasAccent = _drag == Renderer.HitHue;
        _drag = 0;
        if (wasAccent) Settings.SaveAccent(Renderer.Accent);
        else SaveLighting();
    }

    LightDevice? CurrentLight()
    {
        return _renderer.SelectedLight;
    }

    void ApplyLighting()
    {
        if (CurrentLight() is { } d) _lighting.Apply(d);
    }

    void SaveLighting()
    {
        if (CurrentLight() is { } d) _lighting.Save(d);
    }

    /// <summary>Runs motherboard and RAM setup or removal behind one administrator prompt, then rescans.</summary>
    void BoardLightingAsync(string action)
    {
        _renderer.BoardBusy = true;
        new Thread(() =>
        {
            string message = BoardLighting.RunElevated(action);
            Log.Write("board lighting: " + message);
            try
            {
                BeginInvoke(() =>
                {
                    _renderer.BoardBusy = false;
                    _renderer.BoardNote = message;
                    _renderer.BoardNoteUntil = Environment.TickCount64 + 10000;
                    _lighting.ScanAsync();
                    Invalidate();
                });
            }
            catch (InvalidOperationException) { }
        }) { IsBackground = true, Name = "BoardLighting" }.Start();
        Invalidate();
    }

    void SetAccent(Color c)
    {
        _renderer.SetAccent(c);
        Settings.SaveAccent(c);
    }

    void ShowPage(int page)
    {
        _renderer.Tab = page;
        if (page == Renderer.PageSpecs) LoadSpecs();
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _renderer.Crop != null) { _renderer.EndCrop(use: false); Invalidate(); e.Handled = true; return; }
        // Ctrl+1 to Ctrl+4 move between the pages, in rail order.
        if (e.Control && e.KeyCode is >= Keys.D1 and <= Keys.D4) { ShowPage(e.KeyCode - Keys.D1); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_renderer.Crop != null) { _renderer.CropZoom(e.Delta); Invalidate(); return; }
        if (_renderer.Tab == Renderer.PageMonitor) return;
        float next = Math.Clamp(_renderer.Scroll - e.Delta / 120f * 48 * Dpi, 0, _renderer.MaxScroll);
        if (next != _renderer.Scroll) { _renderer.Scroll = next; Invalidate(); }
    }

    void LoadSpecs()
    {
        if (_renderer.Specs != null || _loadingSpecs) return;
        _loadingSpecs = true;
        new Thread(() =>
        {
            var specs = Specs.Collect(_telemetry, out var summary);
            try { BeginInvoke(() => { _renderer.Specs = specs; _renderer.Summary = summary; Invalidate(); }); } catch (InvalidOperationException) { }
        }) { IsBackground = true, Name = "Specs" }.Start();
    }

    /// <summary>Picks a picture for the Kraken's screen and opens the crop dialog for it; a file Gauge cannot use is explained under the library.</summary>
    void ChooseScreenPicture()
    {
        var screen = _lighting.Screen;
        if (screen == null || screen.MediaBusy) return;
        using var dialog = new OpenFileDialog { Title = "Choose a picture or GIF for the cooler's screen", Filter = "Pictures and GIFs (*.gif, *.png, *.jpg, *.jpeg)|*.gif;*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var (first, frames) = KrakenMedia.OpenForCrop(dialog.FileName);
            _renderer.BeginCrop(dialog.FileName, first, frames);
        }
        catch (KrakenMediaException ex) { screen.ShowMediaError(ex.Message); }
        catch (Exception ex)
        {
            Log.Write($"lcd media: could not open {dialog.FileName}: {ex}");
            screen.ShowMediaError($"Could not open the picture: {ex.Message}");
        }
        Invalidate();
    }

    void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = "--restart" });
            Quit();
        }
        catch (Win32Exception) { } // prompt declined
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _telemetry.Updated -= OnTelemetry;
        _updater.Changed -= OnUpdater;
        _updater.Dispose();
        _lighting.Changed -= OnTelemetry;
        _lighting.Dispose();
        _tray?.Dispose();
        _tray = null;
        _telemetry.Dispose();
        _renderer.Dispose();
        base.OnFormClosed(e);
    }
}
