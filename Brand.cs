namespace Gauge;

/// <summary>
/// The Gauge mark: a 270° dial in nine segments, six of them lit, like a meter two thirds of the way up.
/// Drawn in one place so the app, its icon, the tray, the installer and the README art all match.
/// </summary>
internal static class Brand
{
    public static readonly Color Amber = Color.FromArgb(0xFF, 0xA5, 0x3C);

    /// <summary>
    /// Draws the mark centred in a square box <paramref name="size"/> pixels wide. Designed on a 64-unit grid:
    /// the dial's radius is 24 units and its stroke 8. At small sizes the nine gaps would close up, so
    /// <paramref name="simple"/> draws five segments (three lit) instead, the same shape with wider gaps.
    /// </summary>
    public static void DrawMark(Graphics g, float cx, float cy, float size, Color accent, Color track, float strokeUnits = 8, bool simple = false)
    {
        int segments = simple ? 5 : 9, lit = simple ? 3 : 6;
        float gap = simple ? 13 : 8, sweep = (270 - (segments - 1) * gap) / segments;
        float k = size / 64f, r = 24 * k;
        // The open bottom makes the dial sit high in its box; lower it so it looks centred.
        var rect = new RectangleF(cx - r, cy - r + 4 * k, r * 2, r * 2);
        var mode = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var on = new Pen(accent, strokeUnits * k);
        using var off = new Pen(track, strokeUnits * k);
        for (int i = 0; i < segments; i++)
            g.DrawArc(i < lit ? on : off, rect, 135 + i * (sweep + gap), sweep);
        g.SmoothingMode = mode;
    }
}
