#:package SkiaSharp@3.119.4
#:package SkiaSharp.NativeAssets.Linux@3.119.4

// Makes every icon the app ships from the one drawing, assets/AppIconBase.png. Run it from
// anywhere after the drawing changes, and commit what it writes:
//
//   dotnet run tools/make-icons.cs
//
// It writes:
//   src/ReelsMagician.App/Assets/reelsmagician.ico      the .exe, the title bar and the taskbar
//   src/ReelsMagician.App/Assets/reelsmagician-256.png  the window icon on Linux
//   src/ReelsMagician.App/Assets/reelsmagician-128.png  the header image, drawn at 40 px
//   packaging/linux/icons/reelsmagician-{16…512}.png    the AppImage's hicolor icons and .DirIcon
//   assets/AppIcon.sha256                               checksums of the drawing and of all the
//                                                       above, which the logic checks compare
//
// The drawing is a rounded square in the middle of a transparent canvas, about 13 % of its side
// away from each edge. Each icon is that square, cut out and scaled to fill the icon up to a
// margin of 1/32 of its size (none below 32 px), as the icons beside it on a taskbar do.
// Before and after scaling, an alpha of 250 or more becomes fully opaque (the drawing's body is
// 252–253, which lets a light taskbar show through), and an alpha below 8, such as the faint
// noise around the square, becomes fully transparent. Scaling uses a Lanczos filter on
// premultiplied colour, and the sizes up to 48 px get a light sharpen. At 16 px the hat and the
// stars only blur together, so that size shows the reel alone, cut from the same drawing, inside
// the same rounded square.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

const string Source = "assets/AppIconBase.png";
const string Checksums = "assets/AppIcon.sha256";
const string AppAssets = "src/ReelsMagician.App/Assets";
const string LinuxIcons = "packaging/linux/icons";

int[] WindowsSizes = [16, 20, 24, 32, 40, 48, 64, 256];   // Windows picks among these by DPI
int[] LinuxSizes = [16, 24, 32, 48, 64, 128, 256, 512];   // the hicolor sizes build-appimage.sh installs
int[] ReelOnlySizes = [16];
const int SharpenUpTo = 48;

// The reel alone, as a square of the rounded square: left, top and side, in fractions of its side.
(double X, double Y, double Side) ReelOnly = (0.198, 0.113, 0.58);

var root = Path.GetFullPath(Path.Combine(
    Path.GetDirectoryName((string)AppContext.GetData("EntryPointFilePath")!)!, ".."));
if (!File.Exists(Path.Combine(root, "ReelsMagician.slnx")))
    return Fail($"{root} is not the repository root.");

var source = Load(Path.Combine(root, Source));
if (source is null) return Fail($"{Source} is not a PNG this script can read.");
if (source.Width != source.Height || source.Width < 1024)
    return Fail($"{Source} is {source.Width}×{source.Height}; it must be square, 1024 px or larger.");

source.CleanAlpha();
var plate = source.Plate();
if (plate.Side < source.Width / 2)
    return Fail($"{Source}: the rounded square is only {plate.Side} px wide; is the drawing transparent?");

var written = new List<string>();
void Save(string path, byte[] data)
{
    File.WriteAllBytes(Path.Combine(root, path), data);
    written.Add(path);
}

var ico = new List<(int Size, byte[] Data)>();
foreach (var size in WindowsSizes)
{
    var icon = Icon(size, Margin(size));
    ico.Add((size, size == 256 ? Png(icon) : Dib(icon)));
}
Save($"{AppAssets}/reelsmagician.ico", Ico(ico));
Save($"{AppAssets}/reelsmagician-256.png", Png(Icon(256, Margin(256))));
// The header draws the square itself at 40 px, beside the title, so it gets no margin.
Save($"{AppAssets}/reelsmagician-128.png", Png(Icon(128, 0)));

Directory.CreateDirectory(Path.Combine(root, LinuxIcons));
foreach (var stale in Directory.GetFiles(Path.Combine(root, LinuxIcons), "reelsmagician-*.png"))
    File.Delete(stale);
foreach (var size in LinuxSizes)
    Save($"{LinuxIcons}/reelsmagician-{size}.png", Png(Icon(size, Margin(size))));

var sums = new StringBuilder();
foreach (var path in written.Prepend(Source))
    sums.Append($"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, path))))}  {path}\n");
File.WriteAllText(Path.Combine(root, Checksums), sums.ToString());

foreach (var path in written) Console.WriteLine($"wrote {path}");
Console.WriteLine($"wrote {Checksums}");
return 0;

int Margin(int size) => size / 32;

Image Icon(int size, int margin)
{
    var inner = size - 2 * margin;
    var drawing = source.Resample(plate.X, plate.Y, plate.Side, plate.Side, inner);
    if (ReelOnlySizes.Contains(size))
    {
        // The reel's crop lies inside the square, so it is opaque; the square's own alpha keeps
        // the rounded corners.
        var reel = source.Resample(plate.X + ReelOnly.X * plate.Side, plate.Y + ReelOnly.Y * plate.Side,
            ReelOnly.Side * plate.Side, ReelOnly.Side * plate.Side, inner);
        drawing = reel.Masked(drawing);
    }
    if (size <= SharpenUpTo) drawing = drawing.Sharpened(0.35f);
    // The filter rings a little at the square's edge: just inside it that would leave pixels
    // of 250–254, and just outside it faint ones in the transparent corners.
    drawing.CleanAlpha();
    return drawing.Placed(size, margin);
}

static Image? Load(string path)
{
    using var codec = SKCodec.Create(path);
    if (codec is null) return null;
    var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    return codec.GetPixels(info, out var bytes) == SKCodecResult.Success
        ? Image.FromRgba(info.Width, info.Height, bytes)
        : null;
}

static byte[] Png(Image image)
{
    var rgba = image.ToRgba();
    var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    using var bitmap = new SKBitmap(info);
    System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
    using var pixmap = bitmap.PeekPixels();
    using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 9));
    return data?.ToArray() ?? throw new InvalidOperationException("Skia could not encode a PNG.");
}

// A frame below 256 px as Windows draws icons best: a 32-bit bottom-up bitmap, not premultiplied,
// followed by the 1-bit mask that marks the fully transparent pixels.
static byte[] Dib(Image image)
{
    int w = image.Width, h = image.Height, maskStride = (w + 31) / 32 * 4;
    var rgba = image.ToRgba();
    var dib = new byte[40 + w * h * 4 + maskStride * h];
    var span = dib.AsSpan();
    BinaryPrimitives.WriteInt32LittleEndian(span[0..], 40);
    BinaryPrimitives.WriteInt32LittleEndian(span[4..], w);
    BinaryPrimitives.WriteInt32LittleEndian(span[8..], h * 2);   // the colour and the mask
    BinaryPrimitives.WriteInt16LittleEndian(span[12..], 1);
    BinaryPrimitives.WriteInt16LittleEndian(span[14..], 32);
    BinaryPrimitives.WriteInt32LittleEndian(span[20..], w * h * 4 + maskStride * h);
    for (var y = 0; y < h; y++)
    for (var x = 0; x < w; x++)
    {
        var s = (y * w + x) * 4;
        var d = 40 + ((h - 1 - y) * w + x) * 4;
        dib[d] = rgba[s + 2]; dib[d + 1] = rgba[s + 1]; dib[d + 2] = rgba[s]; dib[d + 3] = rgba[s + 3];
        if (rgba[s + 3] == 0)
            dib[40 + w * h * 4 + (h - 1 - y) * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
    }
    return dib;
}

static byte[] Ico(List<(int Size, byte[] Data)> frames)
{
    using var ms = new MemoryStream();
    using var writer = new BinaryWriter(ms);
    writer.Write((ushort)0);
    writer.Write((ushort)1);   // an icon, not a cursor
    writer.Write((ushort)frames.Count);
    var offset = 6 + 16 * frames.Count;
    foreach (var (size, data) in frames)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)data.Length);
        writer.Write((uint)offset);
        offset += data.Length;
    }
    foreach (var (_, data) in frames) writer.Write(data);
    writer.Flush();
    return ms.ToArray();
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

/// <summary>Premultiplied RGBA, 0–1 per channel.</summary>
sealed class Image(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public float[] Pixels { get; } = new float[width * height * 4];

    public static Image FromRgba(int width, int height, byte[] rgba)
    {
        var image = new Image(width, height);
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var a = rgba[i + 3] / 255f;
            image.Pixels[i] = rgba[i] / 255f * a;
            image.Pixels[i + 1] = rgba[i + 1] / 255f * a;
            image.Pixels[i + 2] = rgba[i + 2] / 255f * a;
            image.Pixels[i + 3] = a;
        }
        return image;
    }

    public byte[] ToRgba()
    {
        var rgba = new byte[Pixels.Length];
        for (var i = 0; i < Pixels.Length; i += 4)
        {
            var a = Math.Clamp(Pixels[i + 3], 0f, 1f);
            var alpha = (byte)MathF.Round(a * 255f);
            if (alpha == 0) continue;
            for (var c = 0; c < 3; c++)
                rgba[i + c] = (byte)MathF.Round(Math.Clamp(Pixels[i + c] / a, 0f, 1f) * 255f);
            rgba[i + 3] = alpha;
        }
        return rgba;
    }

    /// <summary>An alpha that would round to 250 or more becomes opaque, one below 8 transparent.</summary>
    public void CleanAlpha()
    {
        for (var i = 0; i < Pixels.Length; i += 4)
        {
            var a = Pixels[i + 3];
            var target = a * 255 >= 249.5f ? 1f : a * 255 < 7.5f ? 0f : a;
            if (target == a) continue;
            var scale = a > 0 ? target / a : 0f;
            for (var c = 0; c < 3; c++) Pixels[i + c] *= scale;
            Pixels[i + 3] = target;
        }
    }

    /// <summary>The square around the drawing's body: what is at least half opaque.</summary>
    public (int X, int Y, int Side) Plate()
    {
        int left = Width, top = Height, right = -1, bottom = -1;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            if (Pixels[(y * Width + x) * 4 + 3] < 0.5f) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        if (right < 0) return (0, 0, 0);
        var side = Math.Max(right - left + 1, bottom - top + 1);
        var cx = (left + right + 1) / 2.0;
        var cy = (top + bottom + 1) / 2.0;
        var x0 = (int)Math.Round(cx - side / 2.0);
        var y0 = (int)Math.Round(cy - side / 2.0);
        return (Math.Clamp(x0, 0, Width - side), Math.Clamp(y0, 0, Height - side), side);
    }

    /// <summary>The square at (x, y) with the given sides, scaled to size × size.</summary>
    public Image Resample(double x, double y, double width, double height, int size)
    {
        var columns = Taps(x, width, Width, size);
        var rows = Taps(y, height, Height, size);
        // Only the rows the second pass reads need the first.
        var firstRow = rows.Min(t => t.Sources.Min());
        var lastRow = rows.Max(t => t.Sources.Max());
        var across = new Image(size, Height);
        for (var row = firstRow; row <= lastRow; row++)
            Filter(Pixels, row * Width * 4, 4, columns, across.Pixels, row * size * 4, 4);
        var result = new Image(size, size);
        for (var column = 0; column < size; column++)
            Filter(across.Pixels, column * 4, size * 4, rows, result.Pixels, column * 4, size * 4);
        return result;
    }

    // Lanczos-3, widened by the scale factor when shrinking, so every source pixel counts.
    private static (int[] Sources, double[] Weights)[] Taps(double from, double span, int srcLength, int dstLength)
    {
        var scale = span / dstLength;
        var support = 3 * Math.Max(scale, 1);
        var taps = new (int[], double[])[dstLength];
        for (var i = 0; i < dstLength; i++)
        {
            var centre = from + (i + 0.5) * scale;
            var sources = new List<int>();
            var weights = new List<double>();
            for (var j = (int)Math.Floor(centre - support); j <= (int)Math.Ceiling(centre + support); j++)
            {
                var weight = Lanczos((j + 0.5 - centre) / Math.Max(scale, 1));
                if (weight == 0) continue;
                sources.Add(Math.Clamp(j, 0, srcLength - 1));
                weights.Add(weight);
            }
            taps[i] = (sources.ToArray(), weights.ToArray());
        }
        return taps;
    }

    private static void Filter(float[] src, int srcStart, int srcStep, (int[] Sources, double[] Weights)[] taps,
        float[] dst, int dstStart, int dstStep)
    {
        for (var i = 0; i < taps.Length; i++)
        {
            var (sources, weights) = taps[i];
            double r = 0, g = 0, b = 0, a = 0, total = 0;
            for (var t = 0; t < sources.Length; t++)
            {
                var k = srcStart + sources[t] * srcStep;
                var weight = weights[t];
                r += src[k] * weight; g += src[k + 1] * weight; b += src[k + 2] * weight; a += src[k + 3] * weight;
                total += weight;
            }
            var d = dstStart + i * dstStep;
            dst[d] = (float)(r / total); dst[d + 1] = (float)(g / total);
            dst[d + 2] = (float)(b / total); dst[d + 3] = (float)(a / total);
        }
    }

    private static double Lanczos(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-9) return 1;
        if (x >= 3) return 0;
        var px = Math.PI * x;
        return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
    }

    /// <summary>This image's colour inside the other image's alpha.</summary>
    public Image Masked(Image shape)
    {
        var result = new Image(Width, Height);
        for (var i = 0; i < Pixels.Length; i += 4)
        {
            var a = Math.Clamp(Pixels[i + 3], 0f, 1f);
            var keep = Math.Clamp(shape.Pixels[i + 3], 0f, 1f);
            for (var c = 0; c < 3; c++) result.Pixels[i + c] = (a > 0 ? Pixels[i + c] / a : 0) * keep;
            result.Pixels[i + 3] = keep;
        }
        return result;
    }

    /// <summary>An unsharp mask over a 3×3 binomial blur, clamped back into range.</summary>
    public Image Sharpened(float amount)
    {
        var result = new Image(Width, Height);
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var i = (y * Width + x) * 4;
            for (var c = 0; c < 4; c++)
            {
                float blur = 0;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var sx = Math.Clamp(x + dx, 0, Width - 1);
                    var sy = Math.Clamp(y + dy, 0, Height - 1);
                    blur += Pixels[(sy * Width + sx) * 4 + c] * (2 - Math.Abs(dx)) * (2 - Math.Abs(dy)) / 16f;
                }
                result.Pixels[i + c] = Math.Clamp(Pixels[i + c] + amount * (Pixels[i + c] - blur), 0f, 1f);
            }
            var alpha = result.Pixels[i + 3];
            for (var c = 0; c < 3; c++) result.Pixels[i + c] = Math.Min(result.Pixels[i + c], alpha);
        }
        return result;
    }

    /// <summary>This image in the middle of a transparent size × size canvas.</summary>
    public Image Placed(int size, int margin)
    {
        if (margin == 0 && Width == size) return this;
        var result = new Image(size, size);
        for (var y = 0; y < Height; y++)
            Array.Copy(Pixels, y * Width * 4, result.Pixels, ((y + margin) * size + margin) * 4, Width * 4);
        return result;
    }
}
