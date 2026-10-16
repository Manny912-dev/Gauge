using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Gauge;

/// <summary>
/// The notification-area icon and its menu, drawn to match the settings popover. Created only
/// while Gauge needs it (running hidden, or "Keep running in the tray" on), so it costs nothing otherwise.
/// </summary>
internal sealed class Tray : IDisposable
{
    public event Action? OpenRequested, QuitRequested;

    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu;
    readonly Font _body, _brand, _small;

    public Tray(int dpi)
    {
        float s = dpi / 96f;
        _body = new Font("Segoe UI", 12.5f * s, GraphicsUnit.Pixel);
        _brand = new Font("Bahnschrift SemiBold", 13f * s, GraphicsUnit.Pixel);
        _small = new Font("Bahnschrift SemiLight", 11.5f * s, GraphicsUnit.Pixel);

        _menu = new ContextMenuStrip
        {
            Renderer = new MenuRenderer(s),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Padding(0, (int)(5 * s), 0, (int)(5 * s)),
            DropShadowEnabled = true,
        };
        int width = (int)(214 * s);
        _menu.Items.Add(new Header(_brand, _small) { AutoSize = false, Size = new Size(width, (int)(40 * s)) });
        _menu.Items.Add(new ToolStripSeparator { AutoSize = false, Size = new Size(width, (int)(9 * s)) });
        _menu.Items.Add(Item("Open Gauge", width, s, () => OpenRequested?.Invoke()));
        _menu.Items.Add(Item("Quit Gauge", width, s, () => QuitRequested?.Invoke()));
        _menu.Opened += (_, _) =>
        {
            int round = 3; // DWMWCP_ROUNDSMALL: matches the system menus on Windows 11, ignored on 10
            Native.DwmSetWindowAttribute(_menu.Handle, 33, ref round, sizeof(int));
        };

        Icon? icon = null;
        try { icon = Icon.ExtractIcon(Environment.ProcessPath!, 0, Native.GetSystemMetricsForDpi(49, (uint)dpi)); } catch { } // SM_CXSMICON
        _icon = new NotifyIcon { Text = "Gauge", Icon = icon ?? SystemIcons.Application, ContextMenuStrip = _menu };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenRequested?.Invoke(); };
    }

    ToolStripMenuItem Item(string text, int width, float s, Action click)
    {
        var item = new ToolStripMenuItem(text) { AutoSize = false, Size = new Size(width, (int)(32 * s)), Font = _body };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>For the --tray-menu snapshot.</summary>
    internal ContextMenuStrip Menu => _menu;

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>A one-off Windows notification from the tray icon.</summary>
    public void Notice(string title, string text) => _icon.ShowBalloonTip(6000, title, text, ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _icon.Icon?.Dispose();
        _menu.Dispose();
        _body.Dispose();
        _brand.Dispose();
        _small.Dispose();
    }

    // Same tokens as the settings popover in Renderer.
    static readonly Color Fill = Color.FromArgb(0x1C, 0x1E, 0x23), Edge = Color.FromArgb(34, 255, 255, 255), Hot = Color.FromArgb(18, 255, 255, 255);
    static readonly Color Ink = Color.FromArgb(0xF2, 0xF3, 0xF5), Muted = Color.FromArgb(0xC9, 0xCD, 0xD3), Faint = Color.FromArgb(0x66, 0x6C, 0x76);

    /// <summary>
    /// Where the menu items' text starts and ends, in <paramref name="item"/>'s own coordinates.
    /// The menu places its other items further right than its menu items, so everything lines up on this.
    /// </summary>
    static (float Left, float Right) TextColumn(ToolStripItem item, float s)
    {
        var anchor = item.Owner?.Items.OfType<ToolStripMenuItem>().FirstOrDefault()?.Bounds ?? item.Bounds;
        return (anchor.X - item.Bounds.X + 16 * s, anchor.Right - item.Bounds.X - 16 * s);
    }

    /// <summary>The brand line at the top of the menu: accent mark, name, version.</summary>
    sealed class Header(Font brand, Font small) : ToolStripItem
    {
        public override bool CanSelect => false;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float s = Height / 40f, mid = Height / 2f + 1 * s, mark = 18 * s;
            var (x, right) = TextColumn(this, s);
            Brand.DrawMark(g, x + mark * 28 / 64, mid - 1 * s, mark, Gauge.Renderer.Accent, Color.FromArgb(70, 255, 255, 255), 9);
            TextRenderer.DrawText(g, "Gauge", brand, new Point((int)(x + mark * 56 / 64 + 9 * s), (int)(mid - brand.Height / 2f)), Ink, TextFormatFlags.NoPadding);
            string version = $"{Program.Version} beta";
            var size = TextRenderer.MeasureText(g, version, small, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, version, small, new Point((int)(right - size.Width), (int)(mid - small.Height / 2f)), Faint, TextFormatFlags.NoPadding);
        }
    }

    sealed class MenuRenderer(float s) : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Fill);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Edge);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = 5 * s, radius = 6 * s;
            var r = new RectangleF(inset, 1 * s, e.Item.Width - inset * 2, e.Item.Height - 2 * s);
            using var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
            path.AddArc(r.Right - radius * 2, r.Y, radius * 2, radius * 2, 270, 90);
            path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
            using var brush = new SolidBrush(Hot);
            g.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var bounds = new Rectangle((int)(16 * s), 0, e.Item.Width - (int)(32 * s), e.Item.Height);
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, bounds, e.Item.Selected ? Ink : Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            var (left, right) = TextColumn(e.Item, s);
            using var pen = new Pen(Color.FromArgb(16, 255, 255, 255));
            e.Graphics.DrawLine(pen, left, y, right, y);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { }
    }
}
