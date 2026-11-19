using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class FullBasicChecks
{
    internal sealed record Recipe(string Name, float Highlights = 0, float Shadows = 0, float Whites = 0, float Blacks = 0, float Saturation = 0, float Vibrance = 0, float Brightness = 0, float Contrast = 0);
    internal static readonly Recipe[] Recipes = [new("zero"), new("highlights-minus", Highlights: -60), new("highlights-plus", Highlights: 60), new("shadows-minus", Shadows: -60), new("shadows-plus", Shadows: 60), new("whites-minus", Whites: -50), new("whites-plus", Whites: 50), new("blacks-minus", Blacks: -50), new("blacks-plus", Blacks: 50), new("saturation-minus", Saturation: -50), new("saturation-plus", Saturation: 50), new("vibrance-minus", Vibrance: -60), new("vibrance-plus", Vibrance: 60), new("gray-vibrance", Saturation: -100, Vibrance: 100), new("mixed-tone", Highlights: -50, Shadows: 50, Whites: -25, Blacks: -15, Brightness: 10, Contrast: 20), new("mixed-all", Highlights: -40, Shadows: 35, Whites: 15, Blacks: -10, Saturation: 15, Vibrance: 30, Brightness: 5, Contrast: 15)];
    internal static ImageAdjustments Adjustments(Recipe r) => new() { Highlights = r.Highlights, ShadowsLevel = r.Shadows, Whites = r.Whites, Blacks = r.Blacks, Saturation = r.Saturation, Vibrance = r.Vibrance, Brightness = r.Brightness, Contrast = r.Contrast };

    internal static SKBitmap Synthetic()
    {
        var bitmap = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Bgra8888, SKAlphaType.Premul));
        var data = new byte[bitmap.ByteCount];
        for (var y = 0; y < 48; y++) for (var x = 0; x < 64; x++)
        {
            var at = y * bitmap.RowBytes + x * 4;
            var edge = x < 32 ? 25 : 130;
            data[at] = (byte)((y * 3 + edge) % 256);
            data[at + 1] = (byte)((x * 3 + y + edge) % 256);
            data[at + 2] = (byte)((x + y * 2 + edge) % 256);
            data[at + 3] = 255;
        }
        Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length);
        return bitmap;
    }

    internal static Dictionary<string, string> LegacyHashes()
    {
        GpuImageService.Enabled = false;
        using var source = Synthetic();
        var hashes = new Dictionary<string, string>();
        foreach (var recipe in Recipes)
        {
            var result = ImageProcessor.RenderPreviewSkBitmap(source, Adjustments(recipe)) ?? throw new InvalidOperationException("Legacy basic renderer failed.");
            using var owned = ReferenceEquals(result, source) ? result.Copy() : result;
            var bytes = new byte[owned.ByteCount];
            Marshal.Copy(owned.GetPixels(), bytes, 0, bytes.Length);
            hashes.Add(recipe.Name, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        return hashes;
    }

    internal static void CaptureLegacy(string path) => File.WriteAllText(path, JsonSerializer.Serialize(LegacyHashes(), new JsonSerializerOptions { WriteIndented = true }));

    internal static RenderedBasicSettings Settings(Recipe r) => new(r.Highlights, r.Shadows, r.Whites, r.Blacks, r.Saturation, r.Vibrance, r.Brightness, r.Contrast);

    internal static void Run(ProbeReport report)
    {
        Check(report, "frozen legacy references", () =>
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return "SKIP platform-specific legacy fixture; other numerical tests still run.";
            var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/full-basic-legacy.json")))!;
            foreach (var (name, hash) in LegacyHashes()) if (expected[name] != hash) throw new InvalidOperationException("Legacy basic controls changed: " + name);
            return "Sixteen opaque spatial/color fixtures captured from accepted 9930ef60 before kernel extraction are unchanged.";
        });
        Check(report, "floating spatial preparation and legacy oracle", SpatialOracle);
        Check(report, "independent saturation/vibrance reference", ColorReference);
        Check(report, "extended saturation and full-frame monochrome", ExtendedSaturation);
        Check(report, "range, alpha, identity and contracts", Contracts);
    }

    private static void Check(ProbeReport report, string name, Func<string> action)
    {
        try
        {
            var detail = action();
            report.Checks.Add(new ProbeCheck("Full basic / " + name, detail.StartsWith("SKIP ") ? "SKIP" : "PASS", detail, Required: !detail.StartsWith("SKIP ")));
        }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Full basic / " + name, ex.ToString())); }
    }

    private static float[] Encoded(SKBitmap source)
    {
        var bytes = new byte[source.ByteCount]; Marshal.Copy(source.GetPixels(), bytes, 0, bytes.Length);
        var pixels = new float[source.Width * source.Height * 4];
        for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
        {
            var at = (y * source.Width + x) * 4; var b = y * source.RowBytes + x * 4;
            pixels[at] = bytes[b + 2] / 255f; pixels[at + 1] = bytes[b + 1] / 255f; pixels[at + 2] = bytes[b] / 255f; pixels[at + 3] = 1;
        }
        return pixels;
    }

    private static string SpatialOracle()
    {
        using var source = Synthetic();
        var pixels = Encoded(source);
        foreach (var recipe in Recipes)
        {
            var stage = ImageProcessor.PrepareFloatingBasic(source.Width, source.Height, pixels, Settings(recipe));
            var oldResult = ImageProcessor.RenderPreviewSkBitmap(source, Adjustments(recipe))!;
            using var old = ReferenceEquals(oldResult, source) ? oldResult.Copy() : oldResult;
            var bytes = new byte[old.ByteCount]; Marshal.Copy(old.GetPixels(), bytes, 0, bytes.Length);
            for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
            {
                var at = (y * source.Width + x) * 4;
                float r = pixels[at], g = pixels[at + 1], b = pixels[at + 2];
                stage.Apply(ref r, ref g, ref b, x, y);
                var o = y * old.RowBytes + x * 4;
                if (Math.Abs(r * 255 - bytes[o + 2]) > 1.1 || Math.Abs(g * 255 - bytes[o + 1]) > 1.1 || Math.Abs(b * 255 - bytes[o]) > 1.1)
                    throw new InvalidOperationException($"Floating/legacy oracle mismatch {recipe.Name} at {x},{y}: {r},{g},{b} vs {bytes[o + 2]},{bytes[o + 1]},{bytes[o]}.");
            }
            if (recipe.Highlights != 0 || recipe.Shadows != 0)
            {
                var chain = ImageProcessor.BuildPointOpChain(Adjustments(recipe), source);
                var oldGrid = ImageProcessor.BuildLocalToneGridFor(source, chain, 0);
                if (!stage.Grid.Gray.SequenceEqual(oldGrid.Gray) || !stage.Grid.Coverage.SequenceEqual(oldGrid.Coverage))
                    throw new InvalidOperationException("Local float grid differs from the legacy exact-code grid.");
            }
        }
        float[] subcode = [.5001f, .5001f, .5001f, 1, .5002f, .5002f, .5002f, 1];
        var precise = ImageProcessor.PrepareFloatingBasic(2, 1, subcode, new(highlights: -50));
        if (precise.Grid.Gray[0] == precise.Grid.Gray[1]) throw new InvalidOperationException("Sub-RGB8 local differences disappeared.");
        var sparse = new float[1024 * 1024 * 4];
        for (var i = 0; i < 1024; i++) { var at = (i * 1024 + i) * 4; sparse[at] = sparse[at + 1] = sparse[at + 2] = .8f; sparse[at + 3] = 1; }
        sparse[7] = 1;
        var measured = ImageProcessor.PrepareFloatingBasic(1024, 1024, sparse, new(whites: 25));
        if (measured.Reference.Total != 1025) throw new InvalidOperationException("Sparse statistic sampling missed visible diagonal pixels.");
        return "All sixteen exact RGB8-code floating inputs match the frozen legacy renderer within its one-code quantization/dither budget; local grids match exactly. Sub-code local differences survive; sparse diagonal triggers full histogram counting.";
    }

    private static double[] Hsl(double[] rgb)
    {
        var max = rgb.Max(); var min = rgb.Min(); var chroma = max - min; var l = (max + min) / 2;
        if (chroma < 1e-9) return [0, 0, l];
        var h = max == rgb[0] ? (rgb[1] - rgb[2]) / chroma : max == rgb[1] ? (rgb[2] - rgb[0]) / chroma + 2 : (rgb[0] - rgb[1]) / chroma + 4;
        return [((h * 60) + 360) % 360, chroma / (1 - Math.Abs(2 * l - 1)), l];
    }

    private static double[] Rgb(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s; var x = c * (1 - Math.Abs((h / 60) % 2 - 1)); var m = l - c / 2;
        double[] rgb = h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x] : h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x];
        return rgb.Select(v => v + m).ToArray();
    }

    private static string ColorReference()
    {
        // Calibration coefficients are policy data; the reference independently evaluates
        // HSL conversion, circular hue interpolation, exponential attenuation and Y keeping.
        double[] hue50 = [.12, .22, .45, .46, .46, .43, .48, .46, .54, .59, .46, .3, .28, .31, .36, .34, .23, .12];
        double[] hue100 = [.49, .89, 1.82, 1.93, 1.91, 1.74, 1.92, 1.85, 2.16, 2.1, 1.47, 1.08, 1.01, 1.11, 1.29, 1.2, .83, .45];
        double Y(double[] rgb) => .2126 * RenderedContrastChecks.Decode(rgb[0]) + .7152 * RenderedContrastChecks.Decode(rgb[1]) + .0722 * RenderedContrastChecks.Decode(rgb[2]);
        foreach (var saturation in new[] { -100, -50, 0, 50, 100 }) foreach (var vibrance in new[] { -100, -50, 0, 50, 100 })
        {
            var input = new float[] { .35f, .24f, .19f, 1, .15f, .4f, .2f, 1, .6f, .2f, .55f, 1, .25f, .25f, .25f, 1 };
            var stage = ImageProcessor.PrepareFloatingBasic(4, 1, input, new(saturation: saturation, vibrance: vibrance));
            for (var x = 0; x < 4; x++)
            {
                double[] rgb = [input[x * 4], input[x * 4 + 1], input[x * 4 + 2]];
                var gray = .299 * rgb[0] + .587 * rgb[1] + .114 * rgb[2];
                rgb = rgb.Select(v => Math.Clamp(gray + (v - gray) * (1 + saturation / 100.0), 0, 1)).ToArray();
                var hsl = Hsl(rgb); var sat = hsl[1];
                if (vibrance < 0) sat *= Math.Exp(vibrance / 100.0 * 3.2 * (1 - sat));
                else if (vibrance > 0)
                {
                    var position = hsl[0] / 20 - .5; var lower = (int)Math.Floor(position); var t = position - lower;
                    double At(int i) { i = (i + 18) % 18; var v = vibrance / 100.0; return 1.25 * (v <= .5 ? v / .5 * hue50[i] : hue50[i] + (v - .5) / .5 * (hue100[i] - hue50[i])); }
                    var strength = At(lower) * (1 - t) + At(lower + 1) * t;
                    sat = Math.Clamp(sat * (1 + strength * (1 - sat)), 0, 1);
                }
                var expected = Rgb(hsl[0], sat, hsl[2]);
                if (vibrance > 0 && Y(expected) > 1e-6)
                {
                    var gain = Y(rgb) / Y(expected);
                    expected = expected.Select(v => RenderedContrastChecks.Encode(Math.Clamp(RenderedContrastChecks.Decode(v) * gain, 0, 1))).ToArray();
                }
                float r = input[x * 4], g = input[x * 4 + 1], b = input[x * 4 + 2]; stage.Apply(ref r, ref g, ref b, x, 0);
                if (new[] { (double)r, g, b }.Zip(expected).Any(p => Math.Abs(p.First - p.Second) > .00004)) throw new InvalidOperationException($"Color reference differs at saturation {saturation}, vibrance {vibrance}.");
                if (saturation == -100 && (Math.Abs(r - g) > 1e-6 || Math.Abs(r - b) > 1e-6)) throw new InvalidOperationException("Vibrance invents color after complete desaturation.");
            }
        }
        return "Independent Double HSL/matrix/hue-table/exponential/linear-Y reference for saturation and vibrance at -100/-50/0/+50/+100; skin/green/magenta/neutral examples; complete desaturation stays neutral even at maximum vibrance.";
    }

    private static string Contracts()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(6, 1, [4, -.25f, .3f, 1, .02f, .03f, .01f, .5f, 4, 4, 4, 1, -.25f, -.25f, -.25f, 1, .000001f, .000001f, .000001f, 1, 0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        var before = new float[24]; source.ReadLinearRow(0, before);
        foreach (var recipe in Recipes)
        {
            using var result = RenderedBasicControls.Apply(source, Settings(recipe));
            var after = new float[24]; result.ReadLinearRow(0, after);
            if (after[3] != 1 || after[7] != .5 || after.Skip(20).Any(v => v != 0) || after[8] <= 1 || after[12] >= 0) throw new InvalidOperationException("Full basic controls lost alpha or signed/HDR reserves.");
            if (recipe.Saturation == -100 && Enumerable.Range(0, 5).Any(x => after[x * 4] != after[x * 4 + 1] || after[x * 4] != after[x * 4 + 2])) throw new InvalidOperationException("Residual color survives full desaturation with vibrance.");
            if (recipe.Name == "zero" && !before.SequenceEqual(after)) throw new InvalidOperationException("Full basic zero changed source values.");
        }
        var unchanged = new float[24]; source.ReadLinearRow(0, unchanged); if (!before.SequenceEqual(unchanged)) throw new InvalidOperationException("Full basic modified source.");
        foreach (var property in new[] { "highlights", "shadowLevel", "whites", "blacks", "saturation", "vibrance", "brightness", "contrast" })
        {
            var constructor = typeof(RenderedBasicSettings).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
            foreach (var bad in new[] { float.NaN, -101, 101 })
            {
                var values = constructor.GetParameters().Select(p => p.DefaultValue).ToArray(); values[Array.FindIndex(constructor.GetParameters(), p => p.Name == property)] = bad;
                try { constructor.Invoke(values); throw new InvalidOperationException("Invalid basic slider accepted."); } catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException) { }
            }
        }
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.SceneLinear);
        try { using var rejected = RenderedBasicControls.Apply(scene, new()); throw new InvalidOperationException("Scene source accepted."); } catch (ArgumentException) { }
        var mapped = new RenderedBasicSettings(renderedExposureEv: 1, exposureAlreadyAppliedEv: -2).ToAdjustments();
        if (mapped.ChainExposure() != 25 || mapped.Exposure != -25 || mapped.ChainExposureInRawStage()) throw new InvalidOperationException("Applied/raw EV routing changed.");
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -4.01, 4.01 })
        {
            try { _ = new RenderedBasicSettings(renderedExposureEv: bad); throw new InvalidOperationException("Invalid rendered EV accepted."); } catch (ArgumentOutOfRangeException) { }
        }
        foreach (var bad in new[] { double.NaN, double.NegativeInfinity, -10.01, 10.01 })
        {
            try { _ = new RenderedBasicSettings(exposureAlreadyAppliedEv: bad); throw new InvalidOperationException("Invalid applied EV accepted."); } catch (ArgumentOutOfRangeException) { }
        }
        try { _ = ImageProcessor.PrepareFloatingBasic(int.MaxValue, int.MaxValue, new float[4], new()); throw new InvalidOperationException("Overflowing analysis dimensions accepted."); } catch (ArgumentException) { }
        return "Neutral signed/HDR probes retain range; saturation intentionally remaps colored reserves. Alpha/ownership, exact neutral, invalid sliders/EV/domain/dimensions and separate already-applied versus remaining exposure checked.";
    }

    private static string ExtendedSaturation()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(4, 1, [4, -.25f, .3f, 1, .02f, .04f, .01f, .5f, 4, 4, 4, 1, -.25f, -.25f, -.25f, 1], WorkingImageDomain.RenderedLinear);
        double[] toSrgb = [1.660491002, -.587641139, -.072849863, -.124550475, 1.132899897, -.008349422, -.018150763, -.100578898, 1.118729661];
        double[] toRec = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];
        double EncodeSigned(double v) => Math.CopySign(RenderedContrastChecks.Encode(Math.Abs(v)), v);
        double DecodeSigned(double v) => Math.CopySign(RenderedContrastChecks.Decode(Math.Abs(v)), v);
        foreach (var saturation in new[] { -100, -50, 50, 100 })
        {
            using var result = RenderedBasicControls.Apply(source, new(saturation: saturation));
            var input = new float[16]; var output = new float[16]; source.ReadLinearRow(0, input); result.ReadLinearRow(0, output);
            double[] Saturate(double[] v) { var gray = .299 * v[0] + .587 * v[1] + .114 * v[2]; return v.Select(c => gray + (c - gray) * (1 + saturation / 100.0)).ToArray(); }
            for (var x = 0; x < 4; x++)
            {
                var at = x * 4; var alpha = (double)input[at + 3];
                var linear = RenderedContrastChecks.Matrix(toSrgb, [input[at] / alpha, input[at + 1] / alpha, input[at + 2] / alpha]);
                var q = linear.Select(EncodeSigned).ToArray();
                var p = linear.Select(v => (double)(float)RenderedContrastChecks.Encode(Math.Clamp(v, 0, 1))).ToArray();
                var bounded = Saturate(p).Select(v => Math.Clamp(v, 0, 1)).ToArray();
                var residual = Saturate(q.Zip(p).Select(v => v.First - v.Second).ToArray());
                var expected = RenderedContrastChecks.Matrix(toRec, bounded.Zip(residual).Select(v => DecodeSigned(v.First + v.Second)).ToArray());
                for (var c = 0; c < 3; c++) if (Math.Abs(output[at + c] - expected[c] * alpha) > Math.Max(2e-7, Math.Abs(expected[c] * alpha) * .001)) throw new InvalidOperationException("Extended saturation reference differs.");
            }
        }
        return "Independent odd-transfer encoded-matrix extension on signed/HDR and partial-alpha colors. Saturation transforms the residual, so -100 is genuinely monochrome; neutral HDR/negative values retain range instead of being clipped.";
    }

    private static bool HasReference(Recipe recipe) => recipe.Name is "highlights-minus" or "shadows-plus" or "mixed-tone" or "mixed-all";

    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        var cube = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? ProfiledPngFixture.Read(path) : null;
        using var source = cube?.Frame ?? RenderedContrastChecks.Input(path);
        if (cube is not null) report.Checks.Add(new ProbeCheck($"Cube fixture / {name}", "PASS", $"RGBA8 PNG diagnostic adapter; embedded {cube.Description}; ICC SHA256 {cube.ProfileHash}; straight decoded pixel SHA256 {cube.PixelHash}. Every input pixel matches independent original-ICC matrix/TRC + premultiplied-alpha reference; no sRGB8 intermediary. General production PNG migration is not implied."));
        using var legacySource = WorkingImageBoundary.ToDiagnosticSdrSrgb8(source);
        foreach (var recipe in Recipes)
        {
            using var result = RenderedBasicControls.Apply(source, Settings(recipe));
            SceneRenderingChecks.SavePreview(result, output, $"{name}-full-{recipe.Name}.png");
            if (!HasReference(recipe)) continue;
            var legacyResult = ImageProcessor.RenderPreviewSkBitmap(legacySource, Adjustments(recipe)) ?? throw new InvalidOperationException("Legacy full-basic reference failed.");
            using var owned = ReferenceEquals(legacyResult, legacySource) ? legacyResult.Copy() : legacyResult;
            using var linear = WorkingImageBoundary.FromEncodedSrgb(owned);
            SceneRenderingChecks.SavePreview(linear, output, $"{name}-full-{recipe.Name}-legacy.png");
        }
        report.Checks.Add(new ProbeCheck($"Full basic / {name}", "PASS", $"{Path.GetFileName(path)}: sixteen full-resolution recipes (both directions of six controls, gray+vibrance, joint tones/all controls, neutral), four legacy spatial/mixed references from the same developed source. F32 statistics/local base; no integer working image. Appearance awaits review."));
    }

    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new System.Text.StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>F16 complete basic controls</title><style>body{background:#202326;color:#eee;font:16px system-ui;margin:24px}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;height:400px;object-fit:contain;background:#111}a{color:#bce0ff}</style><h1>Highlights/shadows, whites/blacks, saturation/vibrance</h1><p>Sixteen new F16 recipes per source. Check skin, saturated colors, neutral grays, highlight/shadow detail, edge halos and local texture. The color matrix runs before tones; vibrance runs later, after the existing shadow-color restore. Gray+vibrance uses saturation -100 and vibrance +100 and should remain gray. Four old sRGB8 references follow each grid. All review PNGs are diagnostic SDR sRGB; retained HDR/wide gamut is checked before output. <a href="report.md">Validation report</a></p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2><div class='grid'>");
            foreach (var recipe in Recipes)
            {
                var file = $"image-{i + 1}-full-{recipe.Name}.png";
                html.Append($"<figure><figcaption>{recipe.Name}: HL {recipe.Highlights}, shadows {recipe.Shadows}, whites {recipe.Whites}, blacks {recipe.Blacks}, saturation {recipe.Saturation}, vibrance {recipe.Vibrance}, brightness {recipe.Brightness}, contrast {recipe.Contrast}</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{recipe.Name}'></a></figure>");
            }
            html.Append("</div><h3>Old sRGB8 references</h3><div class='grid'>");
            foreach (var recipe in Recipes.Where(HasReference))
            {
                var file = $"image-{i + 1}-full-{recipe.Name}-legacy.png";
                html.Append($"<figure><figcaption>{recipe.Name} — old reference</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{recipe.Name}'></a></figure>");
            }
            html.Append("</div>");
        }
        File.WriteAllText(Path.Combine(output, "full-basic-review.html"), html.Append("</html>").ToString());
    }
}
