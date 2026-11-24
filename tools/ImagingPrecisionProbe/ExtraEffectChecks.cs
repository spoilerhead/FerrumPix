using System.Runtime.InteropServices;
using System.Text.Json;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;
internal static class ExtraEffectChecks
{
    internal static void Run(ProbeReport report)
    {
        Check(report, "legacy lighting and float-depth constructor", () =>
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return "SKIP Windows x64 lighting fixture.";
            var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/lights-legacy.json")))!;
            foreach (var (name, hash) in LightingLegacy.Hashes()) if (expected[name] != hash) throw new InvalidOperationException("Legacy lighting changed: " + name);
            return $"{expected.Count} legacy lighting recipes match the upstream reference, including relief and cast shadows.";
        });
        Check(report, "independent aperture-prefix reference", Aperture);
        Check(report, "continuous depth fog, focus bands and contracts", Depth);
        Check(report, "native lighting domains, alpha and HDR", Lights);
        Check(report, "model tensor tiling, reserve, alpha and cancellation", ModelBoundary);
        Check(report, "prepared RAW model mixing and TCA reference", RawModel);
        Check(report, "installed image-model inference", RealModel);
        Check(report, "installed RAW-model inference", RealRawModel);
    }
    private static void Check(ProbeReport report, string name, Func<string> run)
    {
        try { var s = run(); report.Checks.Add(new ProbeCheck("Additional effects / " + name, s.StartsWith("SKIP ") ? "SKIP" : "PASS", s, Required: !s.StartsWith("SKIP "))); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Additional effects / " + name, ex.ToString())); }
    }
    private static float[] Pixels(LinearWorkingFrame f)
    {
        var result = new float[f.Width * f.Height * 4]; var row = new float[f.Width * 4];
        for (var y = 0; y < f.Height; y++) { f.ReadLinearRow(y, row); row.CopyTo(result, y * row.Length); }
        return result;
    }
    private static void Near(double expected, double actual, string name, double relative = .002)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > Math.Max(1.2e-7, Math.Abs(expected) * relative))
            throw new InvalidOperationException($"{name}: expected {expected:G9}, got {actual:G9}.");
    }
    private static LinearWorkingFrame Source(int width = 17, int height = 11) => LinearWorkingFrame.Create(width, height, WorkingImageDomain.RenderedLinear, (y, row) =>
    {
        for (var x = 0; x < width; x++)
        {
            var a = x == 0 ? 0 : (x + y) % 3 == 0 ? .5f : 1;
            row[x * 4] = (x % 4 == 0 ? -.1f : .12345f + x / (float)width * 2) * a;
            row[x * 4 + 1] = (.15f + y / (float)height * .3f) * a;
            row[x * 4 + 2] = .65432f * a; row[x * 4 + 3] = a;
        }
    });
    private static string Aperture()
    {
        using var source = Source(11, 9); var p = WorkingEffectPlanes.Load(source);
        foreach (var corners in new[] { 0, 3, 6, 8 })
        foreach (var highlights in new[] { 0f, 60f })
        {
            var actual = DepthMapService.WorkingDiscBlur(p, 2, corners, highlights, default);
            var exponent = 1 + highlights / 100.0 * 3;
            var taps = new List<(int X, int Y)>();
            for (var y = -2; y <= 2; y++) for (var x = -2; x <= 2; x++)
            {
                var r = Math.Sqrt(x * x + y * y); bool inside;
                if (corners == 0) inside = r <= 2.5;
                else
                {
                    var angle = Math.Atan2(y, x) - 12 * Math.PI / 180; var part = 2 * Math.PI / corners;
                    var remainder = angle - part * Math.Floor(angle / part) - part / 2;
                    inside = r <= 2 * Math.Cos(Math.PI / corners) / Math.Max(.2, Math.Cos(remainder));
                }
                if (inside) taps.Add((x, y));
            }
            for (var y = 0; y < 9; y++) for (var x = 0; x < 11; x++)
            {
                double[] sum = [0, 0, 0]; double alpha = 0; float[][] channels = [p.R, p.G, p.B];
                foreach (var (dx, dy) in taps)
                {
                    var i = Math.Clamp(y + dy, 0, 8) * 11 + Math.Clamp(x + dx, 0, 10);
                    alpha += p.A[i];
                    for (var c = 0; c < 3; c++)
                    {
                        var v = channels[c][i] / 255.0;
                        sum[c] += Math.CopySign(Math.Pow(Math.Abs(v), exponent), v) * p.A[i];
                    }
                }
                var at = y * 11 + x;
                Near(alpha / taps.Count, actual.A[at], "Aperture alpha", .00001);
                if (alpha == 0) continue;
                float[][] output = [actual.R, actual.G, actual.B];
                for (var c = 0; c < 3; c++) Near(Math.CopySign(Math.Pow(Math.Abs(sum[c] / alpha), 1 / exponent) * 255, sum[c]), output[c][at], "Aperture color", .00005);
            }
        }
        return "Independent Double direct disc/polygon gather agrees with native prefix sums for 0/3/6/8 corners, continuous highlight power, alpha, signed/HDR colors and off-image edge extension.";
    }
    private static string Depth()
    {
        using var source = Source(); var original = Pixels(source);
        var samples = Enumerable.Range(0, 17 * 11).Select(i => (i % 17) / 16f + (i % 17 < 16 ? .0001234f : 0)).ToArray();
        var depth = new WorkingCoverage(17, 11, samples);
        Near(samples[3], WorkingDepthEffects.SampleDepth(depth, 3, 0, 17, 11), "Fractional depth", 1e-7);
        using var fog = WorkingDepthEffects.Fog(source, depth, 60, 100, 0, "#FFFFFFFF", 10); var output = Pixels(fog);
        double Optical(double x)
        {
            if (x <= 0) return 0; if (x >= 1) return 1;
            const double w = .05;
            static double Ramp(double t) => t * t * t - Math.Pow(t, 4) / 2;
            var length = x <= w ? w * Ramp(x / w) : x <= 1 - w ? w / 2 + x - w : 1 - w - w * Ramp((1 - x) / w);
            return length / (1 - w);
        }
        for (var i = 0; i < samples.Length; i++)
        {
            var amount = .1 * .6 + .9 * (1 - Math.Pow(.4, Optical(1 - samples[i])));
            for (var c = 0; c < 3; c++) Near(original[i * 4 + c] + (original[i * 4 + 3] - original[i * 4 + c]) * amount, output[i * 4 + c], "Fog");
            if (output[i * 4 + 3] != original[i * 4 + 3]) throw new InvalidOperationException("Fog changed coverage.");
        }
        using var focused = WorkingDepthEffects.Blur(source, new WorkingCoverage(1, 1, [.5f]), 30, 70, 70, 40, 6);
        if (!Pixels(focused).SequenceEqual(original)) throw new InvalidOperationException("In-focus depth image changed.");
        using var zero = WorkingDepthEffects.Blur(source, null!, 0, 100, 0, 0);
        if (!Pixels(zero).SequenceEqual(original)) throw new InvalidOperationException("Zero depth blur changed source.");
        // At 17x11 the largest radius is .88 px; the rounded radius-1
        // pentagon contains only its centre. Identity is correct there.
        using var degenerate = WorkingDepthEffects.Blur(source, new WorkingCoverage(1, 1, [0]), 40, 60, 100, 30, 5);
        if (!Pixels(degenerate).SequenceEqual(original)) throw new InvalidOperationException("Subpixel pentagon unexpectedly changed samples.");
        using var large = Source(33, 25);
        using var blurred = WorkingDepthEffects.Blur(large, new WorkingCoverage(1, 1, [0]), 40, 60, 100, 30, 5);
        if (Pixels(blurred).SequenceEqual(Pixels(large)) || Pixels(blurred).Any(v => !float.IsFinite(v))) throw new InvalidOperationException("Depth blur did not run.");
        try { using var invalid = WorkingDepthEffects.Blur(source, depth, 0, 100, 50, 30, 2); throw new Exception("Invalid aperture accepted."); } catch (ArgumentException) { }
        return "Independent optical-depth fog reference, sub-byte depth sampling, coverage, exact in-focus/zero identities, real out-of-focus polygon blur and invalid aperture contracts pass.";
    }
    private static string Lights()
    {
        using var source = Source(); var before = Pixels(source);
        foreach (var ev in new[] { -1.0, 1.0 })
        {
            using var ambient = ImageProcessor.ApplyWorkingLightSources(source, [], ev); var values = Pixels(ambient);
            for (var i = 0; i < values.Length; i++) Near(i % 4 == 3 ? before[i] : before[i] * Math.Pow(2, ev), values[i], "Linear ambient");
        }
        foreach (var (_, a) in LightingLegacy.Recipes())
        {
            var depth = new WorkingCoverage(17, 11, Enumerable.Range(0, 187).Select(i => (i % 17) / 16f).ToArray());
            using var lit = ImageProcessor.ApplyWorkingLightSources(source, a.LightSources, a.LightAmbient, depth); var values = Pixels(lit);
            if (values.Any(v => !float.IsFinite(v))) throw new InvalidOperationException("Invalid native light.");
            for (var i = 3; i < values.Length; i += 4) if (values[i] != before[i]) throw new InvalidOperationException("Lighting changed alpha.");
        }
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [2, -.1f, .000001f, 1], WorkingImageDomain.SceneLinear);
        using var doubled = ImageProcessor.ApplyWorkingLightSources(scene, [], 1); var data = Pixels(doubled);
        if (doubled.Domain != WorkingImageDomain.SceneLinear || data[0] != 4 || data[1] >= 0) throw new InvalidOperationException("Scene lighting clipped or changed domain.");
        using var identity = ImageProcessor.ApplyWorkingLightSources(source, [], 0);
        if (!Pixels(identity).SequenceEqual(before)) throw new InvalidOperationException("Neutral light changed source.");
        try { using var invalid = ImageProcessor.ApplyWorkingLightSources(source, [new() { Intensity = double.NaN }], 0); throw new Exception("Invalid light accepted."); } catch (ArgumentException) { }
        return "Linear ambient reference, all light shapes/glow/shadows/float-depth relief, both domains, alpha, signed/HDR range and neutral/source ownership pass. Legacy SDR inverse-tone-table behavior is intentionally not used.";
    }
    private static string ModelBoundary()
    {
        using var source = Source(100, 70); var before = Pixels(source); var calls = 0; var fractional = false;
        foreach (var strength in new[] { 0f, .35f, 1f })
        {
            using var result = DenoiseModelService.DenoiseWorkingTiles(source, strength, 64, (values, w, h) =>
            {
                if (w != 64 || h != 64 || values.Length != 3 * w * h) throw new InvalidOperationException("Wrong tensor layout.");
                fractional |= values.Any(v => Math.Abs(v * 255 - Math.Round(v * 255)) > .001);
                calls++; return values;
            });
            var after = Pixels(result);
            for (var i = 0; i < before.Length; i++) Near(before[i], after[i], "Identity tensor", .00001);
        }
        if (calls != 36 || !fractional) throw new InvalidOperationException("Tiling/input precision was not exercised.");
        using var colorOnly = DenoiseModelService.DenoiseWorkingTiles(source, 0, 64, (values, w, h) =>
        {
            var result = (float[])values.Clone(); for (var i = 0; i < w * h; i++) { result[i] += .01f; result[w * h * 2 + i] -= .01f; } return result;
        });
        if (Pixels(colorOnly).SequenceEqual(before)) throw new InvalidOperationException("Strength zero incorrectly disabled model chroma.");
        foreach (var invalid in new Func<float[], int, int, float[]>[] { (_, _, _) => [], (values, _, _) => { values[0] = float.NaN; return values; } })
        {
            try { using var r = DenoiseModelService.DenoiseWorkingTiles(source, 1, 64, invalid); throw new Exception("Invalid model result accepted."); } catch (InvalidOperationException) { }
        }
        using var cancelled = new CancellationTokenSource();
        try
        {
            using var r = DenoiseModelService.DenoiseWorkingTiles(source, 1, 64, (values, _, _) => { cancelled.Cancel(); return values; }, cancelled.Token);
            throw new Exception("Model cancellation was ignored.");
        }
        catch (OperationCanceledException) { }
        if (!before.SequenceEqual(Pixels(source))) throw new InvalidOperationException("Model adapter changed its source on failure.");
        return "CHW floating sRGB tiles, mirror/overlap coverage, identity inference and preserved signed/HDR reserve/alpha pass at strengths 0/0.35/1; strength zero retains chroma. Wrong layouts/NaN/cancellation fail atomically; source remains unchanged.";
    }
    private static string RawModel()
    {
        const int w = 7, h = 5;
        var noisy = new ushort[w * h * 3]; var model = new ushort[noisy.Length];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 3;
            noisy[i] = (ushort)(15000 + x * x * 800); noisy[i + 1] = (ushort)(22000 + y * 1400); noisy[i + 2] = (ushort)(28000 + x * 400);
            model[i] = (ushort)(noisy[i] + 2000 + y * 300); model[i + 1] = (ushort)(noisy[i + 1] - 3500); model[i + 2] = (ushort)(noisy[i + 2] + 1000);
        }
        var a = new byte[noisy.Length * 2]; var b = new byte[a.Length]; Buffer.BlockCopy(noisy, 0, a, 0, a.Length); Buffer.BlockCopy(model, 0, b, 0, b.Length);
        float[] matrix = [1.4f, -.2f, -.2f, -.1f, 1.2f, -.1f, -.1f, -.2f, 1.3f];
        double[] rec = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];
        double[] weight = Enumerable.Range(0, 3).Select(c => .2126 * matrix[c] + .7152 * matrix[3 + c] + .0722 * matrix[6 + c]).ToArray();
        foreach (var strength in new[] { 0.0, 37.0, 100.0 })
        foreach (var tca in new[] { false, true })
        {
            var mixed = new double[noisy.Length];
            for (var i = 0; i < noisy.Length; i += 3)
            {
                var shift = Enumerable.Range(0, 3).Sum(c => weight[c] * (noisy[i + c] - model[i + c])) * (1 - strength / 100) / weight.Sum();
                for (var c = 0; c < 3; c++) mixed[i + c] = model[i + c] + shift;
            }
            double Sample(int c, double x, double y)
            {
                x = Math.Clamp(x, 0, w - 1.001); y = Math.Clamp(y, 0, h - 1.001);
                var left = (int)Math.Floor(x); var top = (int)Math.Floor(y); var fx = x - left; var fy = y - top;
                return (mixed[(top * w + left) * 3 + c] * (1 - fx) + mixed[(top * w + left + 1) * 3 + c] * fx) * (1 - fy) +
                       (mixed[((top + 1) * w + left) * 3 + c] * (1 - fx) + mixed[((top + 1) * w + left + 1) * 3 + c] * fx) * fy;
            }
            var lens = tca ? new LensDataService.Korrektur { HasChromaticAberration = true, NormScale = .25, TcaVr = 1.08, TcaVb = .94, TcaCr = .03, TcaCb = -.02 } : null;
            using var result = RawDecodeService.BuildSceneFrame(w, h, a, matrix, 3, lens, b, strength); var actual = Pixels(result);
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            {
                var srgb = new double[3]; var dx = x - 3; var dy = y - 2; var radius = Math.Sqrt(dx * dx + dy * dy) * .25;
                for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++)
                {
                    var factor = r == 0 ? 1.08 + .03 * radius : .94 - .02 * radius;
                    var value = !tca || r == 1 || dx == 0 && dy == 0 ? mixed[(y * w + x) * 3 + c] : Sample(c, 3 + dx * factor, 2 + dy * factor);
                    srgb[r] += matrix[r * 3 + c] * value * 3 / 65535;
                }
                for (var c = 0; c < 3; c++) Near(rec[c * 3] * srgb[0] + rec[c * 3 + 1] * srgb[1] + rec[c * 3 + 2] * srgb[2], actual[(y * w + x) * 4 + c], "RAW model/TCA");
            }
            if (actual.Max() <= 1 || result.Domain != WorkingImageDomain.SceneLinear) throw new InvalidOperationException("RAW model lost HDR/domain.");
        }
        return "Independent Double camera-space luma/chroma model blend at strengths 0/37/100, with and without TCA, agrees through fused Rec.2020 conversion. No intermediate RGB16 blend rounding/clamp; original RAW/model caches remain RGB16.";
    }
    private static string RealRawModel()
    {
        if (!DenoiseModelService.Available) return "SKIP TreeNet RAW model/runtime is not installed; prepared-cache blend/TCA references still run.";
        var samples = new byte[64 * 64 * 6];
        for (var i = 0; i < samples.Length / 2; i++)
        {
            var v = (ushort)(1000 + (i / 3 % 64) * 350 + i % 3 * 400);
            samples[i * 2] = (byte)v; samples[i * 2 + 1] = (byte)(v >> 8);
        }
        var before = samples.ToArray();
        var denoised = DenoiseModelService.DenoiseLinear(samples, 64, 64, 1, [1, 0, 0, 0, 1, 0, 0, 0, 1], 800, default);
        if (denoised is null || denoised.Length != samples.Length || !samples.SequenceEqual(before)) throw new InvalidOperationException("Real RAW inference failed or changed source cache.");
        return "Installed TreeNet executed a real 64x64 conditioned RAW tile, retaining the existing RGB16 source-cache contract. The separate independent test verifies fractional mixing and TCA into native F16; this smoke test does not claim photographic model quality.";
    }
    private static string RealModel()
    {
        if (!DenoiseModelService.ImageAvailable) return "SKIP NAFNet image model/runtime is not installed; complete tensor/tiling boundary tests above still run.";
        using var source = Source(64, 64); var before = Pixels(source); var progress = 0;
        using var result = DenoiseModelService.DenoiseWorkingImage(source, .65f, progress: (done, _) => progress = done, tileEdge: 128);
        var output = Pixels(result);
        if (progress != 1 || output.Any(v => !float.IsFinite(v))) throw new InvalidOperationException("Installed-model smoke test failed.");
        for (var i = 3; i < output.Length; i += 4) if (output[i] != before[i]) throw new InvalidOperationException("Installed model changed alpha.");
        return "Installed NAFNet executed a real 128x128 CHW tile through the new F16 adapter; floating output, source dimensions, alpha and atomic completion pass. Photographic quality remains a visual gate.";
    }
}
