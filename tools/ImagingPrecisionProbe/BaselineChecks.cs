using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class BaselineChecks
{
    public static void Run(ProbeReport report, string output, IReadOnlyList<string> images, bool rawScene, bool renderScene, bool compareRepair, bool compareExposure, bool compareLens, bool compareTca, bool compareWhiteBalance)
    {
        GpuImageService.Enabled = false;
        using (var synthetic = Synthetic()) Capture(report, output, synthetic, "synthetic");
        for (var i = 0; i < images.Count; i++)
        {
            try
            {
                if (RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(images[i]), StringComparer.OrdinalIgnoreCase) &&
                    !RawDecodeService.IsAvailable)
                    throw new InvalidOperationException("LibRaw is unavailable; refusing to label an embedded JPEG preview as a RAW development baseline.");
                using var decoded = ImageProcessor.DecodeWorkingImage(images[i])
                    ?? throw new InvalidOperationException("FerrumPix could not decode this image.");
                // A bounded preview keeps this first diagnostic safe on large RAW files. Decoding
                // remains full-size; the report must not misrepresent this as an export benchmark.
                var scale = Math.Min(1.0, 2048.0 / Math.Max(decoded.Width, decoded.Height));
                using var preview = decoded.Resize(new SKImageInfo(Math.Max(1, (int)(decoded.Width * scale)),
                    Math.Max(1, (int)(decoded.Height * scale)), SKColorType.Bgra8888, SKAlphaType.Premul),
                    new SKSamplingOptions(SKFilterMode.Linear))
                    ?? throw new InvalidOperationException("Cannot create baseline preview.");
                Capture(report, output, preview, $"image-{i + 1}");
                if (rawScene) RawSceneChecks.Capture(report, images[i], $"image-{i + 1}", output, renderScene, compareRepair, compareExposure, compareLens, compareTca, compareWhiteBalance);
            }
            catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Baseline / image-{i + 1}", ex.Message)); }
        }
    }

    private static void Capture(ProbeReport report, string output, SKBitmap source, string name)
    {
        WorkingFrameChecks.CaptureBoundary(report, output, source, name);
        var recipes = new (string Name, ImageAdjustments Adjustments)[]
        {
            ("neutral", new ImageAdjustments()),
            ("tone-color", new ImageAdjustments { Exposure = 0.75f, Contrast = 20, Temperature = 15, Saturation = 10, Vibrance = 15 }),
            ("details", new ImageAdjustments { Clarity = 15, Sharpness = 20, Vignette = 30 })
        };
        foreach (var recipe in recipes)
        {
            try
            {
                // Warm the same public render entry point that will later exercise the new path.
                using (var warm = Render(source, recipe.Adjustments)) { }
                var times = new List<double>();
                string? expectedHash = null;
                long allocated = 0;
                for (var run = 0; run < 3; run++)
                {
                    var before = GC.GetTotalAllocatedBytes(precise: true);
                    var clock = Stopwatch.StartNew();
                    using var rendered = Render(source, recipe.Adjustments);
                    clock.Stop();
                    allocated += GC.GetTotalAllocatedBytes(precise: true) - before;
                    times.Add(clock.Elapsed.TotalMilliseconds);
                    var bytes = new byte[rendered.ByteCount];
                    Marshal.Copy(rendered.GetPixels(), bytes, 0, bytes.Length);
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (expectedHash is not null && hash != expectedHash)
                        throw new InvalidOperationException("Repeated renders are not byte-identical.");
                    expectedHash = hash;
                    if (run == 0)
                    {
                        using var image = SKImage.FromBitmap(rendered);
                        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                        using var file = File.Create(Path.Combine(output, $"{name}-{recipe.Name}.png"));
                        png.SaveTo(file);
                    }
                }
                times.Sort();
                report.Checks.Add(new ProbeCheck($"Baseline / {name} / {recipe.Name}", "PASS",
                    $"CPU {source.Width}x{source.Height}; {source.ColorType}; 3 identical renders; SHA256 {expectedHash}; mean managed allocation {allocated / 3 / 1_000_000.0:F2} MB. Excludes decode and PNG encoding; not total native/GPU memory.", times[1]));
            }
            catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"Baseline / {name} / {recipe.Name}", ex.ToString())); }
        }
    }

    private static SKBitmap Render(SKBitmap source, ImageAdjustments adjustments)
    {
        var result = ImageProcessor.RenderPreviewSkBitmap(source, adjustments)
            ?? throw new InvalidOperationException("Renderer returned no image.");
        return ReferenceEquals(result, source) ? source.Copy() : result;
    }

    private static SKBitmap Synthetic()
    {
        const int width = 1024, height = 512;
        var image = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var row = new byte[image.RowBytes];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = (byte)(x * 255 / (width - 1));
                var a = y < 384 ? (byte)255 : (byte)((y - 384) * 255 / 127);
                var color = y < 128 ? new SKColor(v, v, v) : y < 256 ? new SKColor(v, 32, (byte)(255 - v)) : new SKColor(20, v, 180);
                var i = x * 4;
                row[i] = (byte)(color.Blue * a / 255);
                row[i + 1] = (byte)(color.Green * a / 255);
                row[i + 2] = (byte)(color.Red * a / 255);
                row[i + 3] = a;
            }
            Marshal.Copy(row, 0, image.GetPixels() + y * image.RowBytes, row.Length);
        }
        return image;
    }
}
