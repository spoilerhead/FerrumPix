using System.Diagnostics;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class SceneRenderingChecks
{
    internal static void Run(ProbeReport report)
    {
        Check(report, "neutral curve reference", NeutralReference);
        Check(report, "color reference and alpha", ColorReference);
        Check(report, "domain and parameter guards", Guards);
    }

    private static void Check(ProbeReport report, string name, Func<string> test)
    {
        var clock = Stopwatch.StartNew();
        try { report.Checks.Add(new ProbeCheck("Scene rendering / " + name, "PASS", test(), clock.Elapsed.TotalMilliseconds)); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Scene rendering / " + name, ex.ToString())); }
    }

    private static string NeutralReference()
    {
        const int count = 1024;
        var pixels = new float[count * 4];
        for (var x = 0; x < count; x++)
        {
            // Include the black toe, middle gray, the shoulder and values well above white.
            var value = (float)(Half)(x < 512 ? x / 511.0 * .02 : (x - 512) / 511.0 * 8);
            pixels[x * 4] = pixels[x * 4 + 1] = pixels[x * 4 + 2] = value;
            pixels[x * 4 + 3] = 1;
        }
        using var source = LinearWorkingFrame.FromLinearPixels(count, 1, pixels, WorkingImageDomain.SceneLinear);
        foreach (var knee in new[] { .45, 1.0 })
        {
            using var rendered = SceneFrameRenderer.Render(source, 0, .003, knee);
            var row = new float[pixels.Length]; rendered.ReadLinearRow(0, row);
            var previous = -1f;
            for (var x = 0; x < count; x++)
            {
                var expected = ReferenceTone(pixels[x * 4], 0, .003, knee);
                for (var c = 0; c < 3; c++) AssertClose(row[x * 4 + c], expected, .001, "Neutral curve");
                if (x != 512 && row[x * 4] < previous) throw new InvalidOperationException("Neutral curve is not monotone.");
                previous = row[x * 4];
            }
        }
        return "Black toe, midtones and HDR shoulder match an independent double reference; neutral ramp stays neutral and monotone for soft and hard shoulders.";
    }

    private static double ReferenceTone(double value, double ev, double black, double knee)
    {
        var slope = 1 / (Math.Pow(2, -ev) - black);
        var radius = Math.Min(black / 2, 1 / (16 * slope));
        double ramp;
        if (value <= black - radius) ramp = 0;
        else if (value < black + radius) ramp = slope * Math.Pow(value - black + radius, 2) / (4 * radius);
        else
        {
            ramp = (value - black) * slope;
            if (knee < 1 && ramp > knee) ramp = 1 - (1 - knee) * Math.Exp(-(ramp - knee) / (1 - knee));
        }
        // Share only the existing look's definition, not the renderer's toe/shoulder arithmetic.
        return RawDecodeService.Acr3CurveAt(Math.Clamp(ramp, 0, 1));
    }

    private static void AssertClose(double actual, double expected, double absolute, string label)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > Math.Max(absolute, Math.Abs(expected) * .003))
            throw new InvalidOperationException($"{label}: {actual:G9} != {expected:G9}.");
    }

    private static double[] Matrix(double[] matrix, double[] rgb) =>
        Enumerable.Range(0, 3).Select(r => Enumerable.Range(0, 3).Sum(c => matrix[r * 3 + c] * rgb[c])).ToArray();

    private static string ColorReference()
    {
        float[] pixels = [.9f, .02f, .01f, 1, .01f, 1.5f, .05f, 1, -.05f, .02f, 2, 1,
            .2f, .05f, .01f, .5f, .00001f, .00001f, .00001f, 1, 0, 0, 0, 0];
        using var source = LinearWorkingFrame.FromLinearPixels(3, 2, pixels, WorkingImageDomain.SceneLinear);
        using var rendered = SceneFrameRenderer.Render(source, .28);
        if (rendered.Domain != WorkingImageDomain.RenderedLinear) throw new InvalidOperationException("Wrong rendered domain.");
        double[] recToSrgb = [1.660491002, -.587641139, -.072849863, -.124550475, 1.132899897, -.008349422, -.018150763, -.100578898, 1.118729661];
        double[] srgbToRec = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];
        var before = new float[12]; var after = new float[12];
        for (var y = 0; y < 2; y++)
        {
            source.ReadLinearRow(y, before); rendered.ReadLinearRow(y, after);
            for (var x = 0; x < 3; x++)
            {
                var at = x * 4; var alpha = before[at + 3];
                if (after[at + 3] != alpha) throw new InvalidOperationException("Rendering changed alpha.");
                if (alpha == 0) continue;
                var input = new[] { before[at] / (double)alpha, before[at + 1] / (double)alpha, before[at + 2] / (double)alpha };
                var pro = Matrix(PointColorMath.SrgbToProPhoto, Matrix(recToSrgb, input)).Select(v => Math.Max(0, v)).ToArray();
                var high = pro.Max(); var low = pro.Min();
                var top = ReferenceTone(high, .28, .003, .45); var bottom = ReferenceTone(low, .28, .003, .45);
                var toned = pro.Select(v => high - low < 1e-7 ? top : bottom + (v - low) * (top - bottom) / (high - low)).ToArray();
                var expected = Matrix(srgbToRec, Matrix(PointColorMath.ProPhotoToSrgb, toned));
                for (var c = 0; c < 3; c++) AssertClose(after[at + c], expected[c] * alpha, .0002, "Color reference");
            }
        }
        source.ReadLinearRow(0, before);
        if (before[8] >= 0 || before[10] != 2) throw new InvalidOperationException("Rendering modified scene values.");
        using var sdr = WorkingImageBoundary.ToDiagnosticSdrSrgb8(rendered);
        if (sdr.Width != 3 || sdr.Height != 2) throw new InvalidOperationException("SDR output changed geometry.");
        return "Signed/HDR colors and partial alpha match independent two-stage double matrices and RGBTone; source unchanged; only RenderedLinear reaches SDR output.";
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid rendering input accepted.");
    }

    private static string Guards()
    {
        using var rendered = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.RenderedLinear);
        Reject(() => SceneFrameRenderer.Render(rendered, 0));
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.SceneLinear);
        Reject(() => SceneFrameRenderer.Render(scene, double.NaN));
        Reject(() => SceneFrameRenderer.Render(scene, 11));
        Reject(() => SceneFrameRenderer.Render(scene, 0, double.NaN));
        Reject(() => SceneFrameRenderer.Render(scene, 0, 1));
        Reject(() => SceneFrameRenderer.Render(scene, 0, -.1));
        Reject(() => SceneFrameRenderer.Render(scene, 0, .003, double.NaN));
        Reject(() => SceneFrameRenderer.Render(scene, 0, .003, 1.1));
        return "Rejects repeated rendering, non-finite settings, invalid exposure/knee and a black point at or above white.";
    }

    internal static void Capture(ProbeReport report, string output, LinearWorkingFrame scene, double baseEv, string name, string setting, bool highlightRepair = false)
    {
        var clock = Stopwatch.StartNew();
        using var rendered = SceneFrameRenderer.Render(scene, baseEv);
        var dimensions = SavePreview(rendered, output, $"{name}-scene-{setting}.png");
        report.Checks.Add(new ProbeCheck($"Scene preview / {name} / {setting}", "PASS",
            $"Full-resolution F16 scene -> ProPhoto tone operation -> F16 rendered Rec.2020 -> SDR sRGB PNG ({dimensions}); base EV {baseEv:G6}, black .003, knee .45; magenta repair {(highlightRepair ? "on before exposure" : "off")}. No lens/geometry/model denoise. Not a production-parity or monitor-profile test.", clock.Elapsed.TotalMilliseconds));
    }

    internal static string SavePreview(LinearWorkingFrame rendered, string output, string filename)
    {
        using var sdr = WorkingImageBoundary.ToDiagnosticSdrSrgb8(rendered);
        var scale = Math.Min(1, 2048.0 / Math.Max(sdr.Width, sdr.Height));
        using var preview = sdr.Resize(new SKImageInfo(Math.Max(1, (int)(sdr.Width * scale)), Math.Max(1, (int)(sdr.Height * scale)), SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear))
            ?? throw new InvalidOperationException("Cannot create scene rendering preview.");
        using var image = SKImage.FromBitmap(preview);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(output, filename));
        png.SaveTo(file);
        return $"{preview.Width}x{preview.Height}";
    }
}
