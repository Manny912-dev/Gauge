using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using static SixLabors.ImageSharp.ImageExtensions;
using static SixLabors.ImageSharp.MetadataExtensions;
using IS = SixLabors.ImageSharp;

namespace Gauge;

/// <summary>A picture or GIF the user added for the Kraken's screen, kept in Gauge's library with the square they cropped.</summary>
internal sealed class LcdMediaItem
{
    /// <summary>A fingerprint of the source file, so adding the same file twice finds the same item.</summary>
    public required string Id;
    public required string Name;
    /// <summary>The square to show, in source pixels.</summary>
    public Rectangle Crop;
    public int Frames = 1;
    /// <summary>A small picture for the library, or null when it could not be read.</summary>
    public Bitmap? Thumb;

    public string Folder => Path.Combine(KrakenMedia.Root, Id);
}

/// <summary>A problem with a picture, in words for the user.</summary>
internal sealed class KrakenMediaException(string message) : Exception(message);

/// <summary>
/// The Image/GIF mode's pictures: a library in %AppData%\Gauge\lcd-media, and the pipeline that turns a source
/// into what the cooler plays. For each frame, keeping its delay: crop to the user's square, resize to 320×320
/// (Lanczos3), turn for the rotation dial, black out everything past radius 167 (off the glass, and smaller to
/// store), then encode a looping GIF of at most 23.75 MiB, the cooler's whole memory. A still picture becomes a
/// one-frame GIF. Uses ImageSharp, which loads only when a picture is being prepared.
/// </summary>
internal static class KrakenMedia
{
    const int N = KrakenSession.Size;
    public const long MaxInputBytes = 32L * 1024 * 1024;
    public const int MaxOutputBytes = KrakenSession.MaxGifBytes; // 24,903,680
    public const int MinCrop = 50;
    /// <summary>Pixels farther than this from the centre are set to black, as NZXT CAM does: half the screen plus 7.</summary>
    public const float MaskRadius = N / 2f + 7;
    const long MaxDecodedBytes = 1536L * 1024 * 1024; // frames are decoded in full before they are cut down

    static readonly object s_lock = new();
    static List<LcdMediaItem>? s_items;

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gauge", "lcd-media");

    // ------------------------------------------------------------------ library

    /// <summary>The library, oldest first.</summary>
    public static List<LcdMediaItem> Items
    {
        get
        {
            lock (s_lock)
            {
                s_items ??= Load();
                return [.. s_items];
            }
        }
    }

    public static LcdMediaItem? Find(string id) => id.Length == 0 ? null : Items.Find(i => i.Id == id);

    static List<LcdMediaItem> Load()
    {
        var items = new List<LcdMediaItem>();
        if (!Directory.Exists(Root)) return items;
        foreach (var dir in new DirectoryInfo(Root).GetDirectories().OrderBy(d => d.CreationTimeUtc))
        {
            try
            {
                var item = ReadItem(dir.FullName, dir.Name);
                if (item != null) items.Add(item);
            }
            catch (Exception ex) { Log.Write($"lcd media: could not read {dir.Name}: {ex.Message}"); }
        }
        return items;
    }

    static LcdMediaItem? ReadItem(string folder, string id)
    {
        string meta = Path.Combine(folder, "item.txt");
        if (!File.Exists(meta) || Source(folder) == null) return null;
        var values = File.ReadAllLines(meta).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        var crop = values.GetValueOrDefault("crop", "").Split(',').Select(v => int.TryParse(v, out int n) ? n : -1).ToArray();
        if (crop.Length != 4 || crop.Any(v => v < 0)) return null;
        return new LcdMediaItem
        {
            Id = id,
            Name = values.GetValueOrDefault("name", id),
            Crop = new Rectangle(crop[0], crop[1], crop[2], crop[3]),
            Frames = int.TryParse(values.GetValueOrDefault("frames"), out int f) ? f : 1,
            Thumb = LoadBitmap(Path.Combine(folder, "thumb.png")),
        };
    }

    static string? Source(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "source.*").FirstOrDefault() : null;

    /// <summary>A copy of a picture file, so the file itself is not held open.</summary>
    static Bitmap? LoadBitmap(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var file = new Bitmap(path);
            return new Bitmap(file);
        }
        catch { return null; }
    }

    /// <summary>The item's first frame as cropped and masked, upright, for the window's preview. Null when missing.</summary>
    public static Bitmap? Preview(LcdMediaItem item) => LoadBitmap(Path.Combine(item.Folder, "preview.png"));

    public static void Delete(string id)
    {
        lock (s_lock)
        {
            s_items ??= Load();
            s_items.RemoveAll(i => i.Id == id); // thumbnails may still be on screen, so they are left to the collector
        }
        try { Directory.Delete(Path.Combine(Root, id), recursive: true); }
        catch (Exception ex) { Log.Write($"lcd media: could not delete {id}: {ex.Message}"); }
    }

    /// <summary>What the cooler's GIF bucket would hold for this item at this rotation; equal keys mean nothing needs sending.</summary>
    public static string Key(LcdMediaItem item, int gifClockwise) => $"{item.Id}:{item.Crop.X},{item.Crop.Y},{item.Crop.Width},{item.Crop.Height}:{gifClockwise}";

    // ------------------------------------------------------------------ adding a picture

    /// <summary>The square NZXT CAM starts with: 95 % of the shorter side, centred, at least <see cref="MinCrop"/> pixels.</summary>
    public static Rectangle DefaultCrop(Size source)
    {
        int side = Math.Min(Math.Min(source.Width, source.Height), Math.Max(MinCrop, (int)MathF.Round(0.95f * Math.Min(source.Width, source.Height))));
        return new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side);
    }

    /// <summary>Checks a file and reads its first frame for the crop dialog. Throws <see cref="KrakenMediaException"/> with the reason.</summary>
    public static (Bitmap First, int Frames) OpenForCrop(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new KrakenMediaException("The file could not be found.");
        if (info.Length > MaxInputBytes) throw new KrakenMediaException($"This file is {info.Length / 1048576.0:0.#} MB. Pictures and GIFs can be at most 32 MB.");
        try
        {
            int frames = Math.Max(1, IS.Image.Identify(path).FrameMetadataCollection.Count);
            using var first = IS.Image.Load<Rgba32>(new DecoderOptions { MaxFrames = 1 }, path);
            if (first.Width < MinCrop || first.Height < MinCrop) throw new KrakenMediaException($"This picture is smaller than {MinCrop} × {MinCrop} pixels.");
            using var png = new MemoryStream();
            first.SaveAsPng(png);
            png.Position = 0;
            using var decoded = new Bitmap(png);
            return (new Bitmap(decoded), frames);
        }
        catch (IS.UnknownImageFormatException) { throw new KrakenMediaException("Gauge cannot read this file. Choose a GIF, PNG or JPEG."); }
        catch (IS.InvalidImageContentException) { throw new KrakenMediaException("This file looks damaged, so Gauge cannot read it."); }
        finally { Release(); }
    }

    /// <summary>
    /// Adds a file to the library with the chosen square, and prepares it for the given rotation so that a
    /// picture too large for the cooler is refused now. Throws <see cref="KrakenMediaException"/> with the reason.
    /// </summary>
    public static LcdMediaItem Import(string path, Rectangle crop, int gifClockwise)
    {
        if (new FileInfo(path).Length > MaxInputBytes) throw new KrakenMediaException("Pictures and GIFs can be at most 32 MB.");
        string id;
        using (var stream = File.OpenRead(path)) id = Convert.ToHexString(SHA256.HashData(stream))[..16].ToLowerInvariant();
        string folder = Path.Combine(Root, id);
        bool existed = Directory.Exists(folder);
        Directory.CreateDirectory(folder);
        try
        {
            string source = Source(folder) ?? Path.Combine(folder, "source" + Path.GetExtension(path).ToLowerInvariant());
            if (!File.Exists(source)) File.Copy(path, source);
            foreach (string old in Directory.GetFiles(folder, "gif-*.gif")) File.Delete(old); // made for another square

            using var image = Prepare(source, crop);
            using (var first = image.Frames.CloneFrame(0))
            {
                Finish(first, 0);
                first.SaveAsPng(Path.Combine(folder, "preview.png"));
                first.Mutate(c => c.Resize(128, 128, KnownResamplers.Lanczos3));
                first.SaveAsPng(Path.Combine(folder, "thumb.png"));
            }
            var item = new LcdMediaItem { Id = id, Name = Path.GetFileName(path), Crop = crop, Frames = image.Frames.Count };
            File.WriteAllLines(Path.Combine(folder, "item.txt"), [$"name={item.Name}", $"crop={crop.X},{crop.Y},{crop.Width},{crop.Height}", $"frames={item.Frames}"]);
            Finish(image, gifClockwise);
            File.WriteAllBytes(GifPath(item, gifClockwise), Encode(image));
            item.Thumb = LoadBitmap(Path.Combine(folder, "thumb.png"));
            lock (s_lock)
            {
                s_items ??= Load();
                s_items.RemoveAll(i => i.Id == id);
                s_items.Add(item);
            }
            Log.Write($"lcd media: added {item.Name} ({item.Frames} frame(s), crop {crop})");
            return item;
        }
        catch
        {
            if (!existed) try { Directory.Delete(folder, recursive: true); } catch { }
            throw;
        }
        finally { Release(); }
    }

    static string GifPath(LcdMediaItem item, int gifClockwise) => Path.Combine(item.Folder, $"gif-{gifClockwise}.gif");

    /// <summary>The GIF to send for this item at this rotation, made once and kept. Throws <see cref="KrakenMediaException"/> with the reason.</summary>
    public static byte[] Gif(LcdMediaItem item, int gifClockwise)
    {
        string path = GifPath(item, gifClockwise);
        if (File.Exists(path)) return File.ReadAllBytes(path);
        string source = Source(item.Folder) ?? throw new KrakenMediaException("This picture's file is missing from Gauge's library. Add it again.");
        try
        {
            using var image = Prepare(source, item.Crop);
            Finish(image, gifClockwise);
            var gif = Encode(image);
            foreach (string old in Directory.GetFiles(item.Folder, "gif-*.gif")) File.Delete(old); // one rotation at a time is enough
            File.WriteAllBytes(path, gif);
            return gif;
        }
        finally { Release(); }
    }

    // ------------------------------------------------------------------ the pipeline

    /// <summary>Decodes every frame, crops it to the square and resizes it to 320×320 with Lanczos3.</summary>
    static IS.Image<Rgba32> Prepare(string path, Rectangle crop)
    {
        IS.ImageInfo info;
        try { info = IS.Image.Identify(path); }
        catch (IS.UnknownImageFormatException) { throw new KrakenMediaException("Gauge cannot read this file. Choose a GIF, PNG or JPEG."); }
        if ((long)info.Width * info.Height * 4 * Math.Max(1, info.FrameMetadataCollection.Count) > MaxDecodedBytes)
            throw new KrakenMediaException("This GIF has too many large frames for Gauge to prepare. Try a shorter or smaller one.");
        var image = IS.Image.Load<Rgba32>(path);
        var square = IS.Rectangle.Intersect(new IS.Rectangle(crop.X, crop.Y, crop.Width, crop.Height), image.Bounds);
        if (square.Width < 1 || square.Height < 1) { image.Dispose(); throw new KrakenMediaException("The chosen square is outside the picture."); }
        image.Mutate(c => c.Crop(square).Resize(N, N, KnownResamplers.Lanczos3));
        return image;
    }

    /// <summary>Per frame: flattens onto black, turns clockwise for the dial and blacks out the corners.</summary>
    static void Finish(IS.Image<Rgba32> image, int gifClockwise)
    {
        var pixels = new uint[N * N];
        var turned = new uint[N * N];
        foreach (var frame in image.Frames)
        {
            frame.CopyPixelDataTo(MemoryMarshal.Cast<uint, Rgba32>(pixels.AsSpan()));
            for (int i = 0; i < pixels.Length; i++)
            {
                uint p = pixels[i], a = p >> 24; // R, G, B, A from the low byte up
                if (a == 255) continue;
                uint r = (p & 0xFF) * a / 255, g = (p >> 8 & 0xFF) * a / 255, b = (p >> 16 & 0xFF) * a / 255;
                pixels[i] = 0xFF000000 | b << 16 | g << 8 | r;
            }
            KrakenFrame.Rotate(pixels, turned, N, gifClockwise);
            Mask(turned, N);
            frame.ProcessPixelRows(rows =>
            {
                for (int y = 0; y < N; y++) MemoryMarshal.Cast<uint, Rgba32>(turned.AsSpan(y * N, N)).CopyTo(rows.GetRowSpan(y));
            });
        }
    }

    /// <summary>Sets every pixel whose centre is farther than <see cref="MaskRadius"/> from the middle to opaque black.</summary>
    public static void Mask(Span<uint> pixels, int size)
    {
        float c = size / 2f, limit = MaskRadius * MaskRadius;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - c, dy = y + 0.5f - c;
                if (dx * dx + dy * dy > limit) pixels[y * size + x] = 0xFF000000;
            }
    }

    /// <summary>
    /// Encodes a looping GIF no larger than the cooler holds. Smaller in steps until it fits: a palette per frame
    /// with dithering, then one shared palette without, then fewer colours, then every other frame dropped with
    /// its time given to the frame before, so the animation keeps its length.
    /// </summary>
    static byte[] Encode(IS.Image<Rgba32> image, int limit = MaxOutputBytes)
    {
        image.Metadata.GetGifMetadata().RepeatCount = 0; // loop forever
        ReadOnlySpan<(int Colours, bool Dither, GifColorTableMode Table)> steps =
            [(256, true, GifColorTableMode.Local), (256, false, GifColorTableMode.Global), (128, false, GifColorTableMode.Global), (64, false, GifColorTableMode.Global)];
        byte[] gif = [];
        foreach (var step in steps)
        {
            gif = Save(image, step.Colours, step.Dither, step.Table);
            if (gif.Length <= limit) return gif;
        }
        while (image.Frames.Count > 1)
        {
            for (int i = image.Frames.Count - 1; i >= 1; i -= 2)
            {
                var kept = image.Frames[i - 1].Metadata.GetGifMetadata();
                kept.FrameDelay += image.Frames[i].Metadata.GetGifMetadata().FrameDelay;
                image.Frames.RemoveFrame(i);
            }
            gif = Save(image, 64, false, GifColorTableMode.Global);
            if (gif.Length <= limit)
            {
                Log.Write($"lcd media: dropped frames to fit, now {image.Frames.Count}");
                return gif;
            }
        }
        throw new KrakenMediaException($"Even reduced, this picture comes to {gif.Length / 1048576.0:0.0} MB and the cooler holds 23.75 MB. Try a shorter GIF.");
    }

    static byte[] Save(IS.Image<Rgba32> image, int colours, bool dither, GifColorTableMode table)
    {
        var encoder = new GifEncoder
        {
            ColorTableMode = table,
            Quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = colours, Dither = dither ? KnownDitherings.FloydSteinberg : null }),
        };
        using var stream = new MemoryStream();
        image.SaveAsGif(stream, encoder);
        return stream.ToArray();
    }

    /// <summary>Hands ImageSharp's pooled buffers back, so preparing a big GIF does not leave Gauge holding the memory.</summary>
    static void Release() => IS.Configuration.Default.MemoryAllocator.ReleaseRetainedResources();

    // ------------------------------------------------------------------ tests

    /// <summary>Runs the whole pipeline on a file without adding it to the library. For the self-test.</summary>
    internal static byte[] ProcessForTest(string path, Rectangle crop, int gifClockwise, int limit = MaxOutputBytes)
    {
        try
        {
            using var image = Prepare(path, crop);
            Finish(image, gifClockwise);
            return Encode(image, limit);
        }
        finally { Release(); }
    }

    /// <summary>Decodes a GIF's frames to 32-bit pixels (R, G, B, A from the low byte up) with their delays. For the self-test.</summary>
    internal static List<(uint[] Pixels, int Delay)> DecodeForTest(byte[] gif, out int width, out int height, out int repeat)
    {
        using var image = IS.Image.Load<Rgba32>(gif);
        width = image.Width;
        height = image.Height;
        repeat = image.Metadata.GetGifMetadata().RepeatCount;
        var frames = new List<(uint[], int)>();
        foreach (var frame in image.Frames)
        {
            var pixels = new uint[image.Width * image.Height];
            frame.CopyPixelDataTo(MemoryMarshal.Cast<uint, Rgba32>(pixels.AsSpan()));
            frames.Add((pixels, frame.Metadata.GetGifMetadata().FrameDelay));
        }
        return frames;
    }

    /// <summary>Writes a GIF of random noise, which hardly compresses. For testing the size limit.</summary>
    internal static void WriteNoiseGif(string path, int frames, int size)
    {
        var random = new Random(7);
        using var image = new IS.Image<Rgba32>(size, size);
        var pixels = new Rgba32[size * size];
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
            var frame = f == 0 ? image.Frames.RootFrame : image.Frames.AddFrame(pixels);
            if (f == 0) frame.ProcessPixelRows(rows => { for (int y = 0; y < size; y++) pixels.AsSpan(y * size, size).CopyTo(rows.GetRowSpan(y)); });
            frame.Metadata.GetGifMetadata().FrameDelay = 4;
        }
        image.Metadata.GetGifMetadata().RepeatCount = 0;
        image.SaveAsGif(path);
    }

    /// <summary>Writes a test GIF: an upward arrow on a coloured disc, with a moving dot so it animates. For the self-test and hardware checks.</summary>
    internal static void WriteArrowGif(string path, int frames = 8)
    {
        using var image = new IS.Image<Rgba32>(N, N);
        for (int f = 0; f < frames; f++)
        {
            using var bmp = new Bitmap(N, N, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(255, 20, 24, 40));
                using var white = new SolidBrush(Color.White);
                g.FillPolygon(white, [new PointF(160, 40), new PointF(230, 140), new PointF(185, 140), new PointF(185, 270), new PointF(135, 270), new PointF(135, 140), new PointF(90, 140)]);
                float t = f / (float)frames * 2 * MathF.PI;
                using var dot = new SolidBrush(Color.FromArgb(255, 255, 165, 60));
                g.FillEllipse(dot, 160 + 110 * MathF.Cos(t) - 12, 160 + 110 * MathF.Sin(t) - 12, 24, 24);
            }
            var data = bmp.LockBits(new Rectangle(0, 0, N, N), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var pixels = new Rgba32[N * N];
            unsafe
            {
                var src = new ReadOnlySpan<uint>((void*)data.Scan0, N * N);
                for (int i = 0; i < pixels.Length; i++) { uint p = src[i]; pixels[i] = new Rgba32((byte)(p >> 16), (byte)(p >> 8), (byte)p, 255); }
            }
            bmp.UnlockBits(data);
            var frame = f == 0 ? image.Frames.RootFrame : image.Frames.AddFrame(pixels);
            if (f == 0) frame.ProcessPixelRows(rows => { for (int y = 0; y < N; y++) pixels.AsSpan(y * N, N).CopyTo(rows.GetRowSpan(y)); });
            frame.Metadata.GetGifMetadata().FrameDelay = 10;
        }
        image.Metadata.GetGifMetadata().RepeatCount = 0;
        image.SaveAsGif(path);
    }
}
