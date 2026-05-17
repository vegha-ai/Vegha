// Rasterizes Assets/Vegha.svg into logo.png (256x256) and a multi-resolution app.ico,
// and Assets/wordmark.svg into wordmark.png (the title-bar logo + lettering).
// One-shot tool; re-run any time a source SVG changes:  dotnet run --project eng/RasterizeIcon
//
// Output paths are relative to the repo root, resolved via AppContext.BaseDirectory + ../../.. walk.

using System;
using System.IO;
using SkiaSharp;
using Svg.Skia;

var repoRoot = FindRepoRoot();
var assetsDir = Path.Combine(repoRoot, "app", "Vegha.App", "Assets");
var svgPath = Path.Combine(assetsDir, "Vegha.svg");
if (!File.Exists(svgPath))
{
    Console.Error.WriteLine($"SVG not found: {svgPath}");
    return 1;
}

var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var pngBySize = new Dictionary<int, byte[]>(sizes.Length);

using (var svg = new SKSvg())
{
    if (svg.Load(svgPath) is null || svg.Picture is null)
    {
        Console.Error.WriteLine("Failed to parse SVG.");
        return 2;
    }
    var bounds = svg.Picture.CullRect;
    foreach (var size in sizes)
    {
        pngBySize[size] = Render(svg, bounds, size, size);
    }
}

File.WriteAllBytes(Path.Combine(assetsDir, "logo.png"), pngBySize[256]);
WriteIco(Path.Combine(assetsDir, "app.ico"), pngBySize, sizes);

Console.WriteLine($"Wrote {Path.Combine(assetsDir, "logo.png")} (256×256) and app.ico ({string.Join(",", sizes)}).");

// --- Wordmark (logo + "Vegha" lettering) for the title bar ---
// Rendered tall (HiDPI headroom) at its native aspect ratio; the title bar scales it down.
var wordmarkSvgPath = Path.Combine(assetsDir, "wordmark.svg");
if (File.Exists(wordmarkSvgPath))
{
    using var wsvg = new SKSvg();
    if (wsvg.Load(wordmarkSvgPath) is not null && wsvg.Picture is not null)
    {
        var wb = wsvg.Picture.CullRect;
        const int wordmarkHeight = 128;
        var wordmarkWidth = (int)Math.Round(wordmarkHeight * wb.Width / wb.Height);
        File.WriteAllBytes(Path.Combine(assetsDir, "wordmark.png"),
            Render(wsvg, wb, wordmarkWidth, wordmarkHeight));
        Console.WriteLine($"Wrote {Path.Combine(assetsDir, "wordmark.png")} ({wordmarkWidth}×{wordmarkHeight}).");
    }
    else
    {
        Console.Error.WriteLine($"Failed to parse {wordmarkSvgPath}.");
        return 2;
    }
}

return 0;

static byte[] Render(SKSvg svg, SKRect bounds, int width, int height)
{
    using var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(SKColors.Transparent);

    // Scale-to-fit with center alignment.
    var scale = Math.Min(width / bounds.Width, height / bounds.Height);
    var dx = (width - bounds.Width * scale) * 0.5f;
    var dy = (height - bounds.Height * scale) * 0.5f;
    canvas.Translate(dx, dy);
    canvas.Scale(scale);
    canvas.Translate(-bounds.Left, -bounds.Top);

    using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
    canvas.DrawPicture(svg.Picture, paint);
    canvas.Flush();

    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static void WriteIco(string path, Dictionary<int, byte[]> pngs, int[] orderedSizes)
{
    using var fs = File.Open(path, FileMode.Create);
    using var bw = new BinaryWriter(fs);

    // ICONDIR
    bw.Write((ushort)0);                       // reserved
    bw.Write((ushort)1);                       // type = icon
    bw.Write((ushort)orderedSizes.Length);     // count

    var headerSize = 6 + (16 * orderedSizes.Length);
    var offset = headerSize;
    foreach (var s in orderedSizes)
    {
        var bytes = pngs[s];
        bw.Write((byte)(s >= 256 ? 0 : s)); // width  (0 ⇒ 256)
        bw.Write((byte)(s >= 256 ? 0 : s)); // height (0 ⇒ 256)
        bw.Write((byte)0);                  // palette colors
        bw.Write((byte)0);                  // reserved
        bw.Write((ushort)1);                // color planes
        bw.Write((ushort)32);               // bpp
        bw.Write((uint)bytes.Length);
        bw.Write((uint)offset);
        offset += bytes.Length;
    }
    foreach (var s in orderedSizes)
        bw.Write(pngs[s]);
}

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir, "Vegha.sln"))) return dir;
        dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not find Vegha.sln walking up from " + AppContext.BaseDirectory);
}
