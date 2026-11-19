using System.Net;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class RenderedCurveChecks
{
    internal sealed record Recipe(string Name, string Master = "0,0;255,255", string Red = "0,0;255,255",
        string Green = "0,0;255,255", string Blue = "0,0;255,255", string Luminance = "0,0;255,255",
        FullBasicChecks.Recipe? Basic = null);

    internal static readonly Recipe[] Recipes =
    [
        new("zero"),
        new("master-s", Master: "0,0;64,48;192,208;255,255"),
        new("master-endpoints", Master: "20,12;235,242"),
        new("red-lift", Red: "0,0;128,154;255,255"),
        new("green-pull", Green: "0,0;128,108;255,255"),
        new("blue-pull", Blue: "0,0;128,102;255,255"),
        new("luminance-s", Luminance: "0,0;64,48;192,208;255,255"),
        new("combined-curves", Master: "0,8;64,56;192,202;255,248", Red: "0,0;128,142;255,255",
            Blue: "0,0;128,116;255,255", Luminance: "0,0;128,136;255,255"),
        new("mixed-basic-curves", Master: "0,0;64,56;192,202;255,255", Blue: "0,0;128,116;255,255",
            Luminance: "0,0;128,136;255,255",
            Basic: new("mixed", Highlights: -35, Shadows: 30, Whites: 10, Blacks: -10,
                Saturation: 15, Vibrance: 20, Brightness: 5, Contrast: 15)),
        new("gray-master", Master: "0,0;64,48;192,208;255,255",
            Basic: new("gray", Saturation: -100, Vibrance: 100))
    ];

    internal static ImageAdjustments Adjustments(Recipe recipe)
    {
        var result = FullBasicChecks.Adjustments(recipe.Basic ?? new("zero"));
        result.CurveRgbPoints = recipe.Master;
        result.CurveRedPoints = recipe.Red;
        result.CurveGreenPoints = recipe.Green;
        result.CurveBluePoints = recipe.Blue;
        result.CurveLuminancePoints = recipe.Luminance;
        return result;
    }

    // Capture from the accepted binary before changing any application service.
    // Replacing a failed fixture with current hashes would hide a regression.
    internal static Dictionary<string, string> LegacyHashes()
    {
        GpuImageService.Enabled = false;
        using var source = FullBasicChecks.Synthetic();
        var hashes = new Dictionary<string, string>();
        foreach (var recipe in Recipes)
        {
            var result = ImageProcessor.RenderPreviewSkBitmap(source, Adjustments(recipe))
                ?? throw new InvalidOperationException("Legacy curve renderer failed.");
            using var owned = ReferenceEquals(result, source) ? result.Copy() : result;
            var bytes = new byte[owned.ByteCount];
            Marshal.Copy(owned.GetPixels(), bytes, 0, bytes.Length);
            hashes.Add(recipe.Name, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        return hashes;
    }

    internal static void CaptureLegacy(string path) => File.WriteAllText(path,
        JsonSerializer.Serialize(LegacyHashes(), new JsonSerializerOptions { WriteIndented = true }));

    internal static RenderedCurveSettings Curves(Recipe recipe) => new(recipe.Master, recipe.Red, recipe.Green, recipe.Blue, recipe.Luminance);
    internal static RenderedBasicSettings Basic(Recipe recipe) => FullBasicChecks.Settings(recipe.Basic ?? new("zero"));

    internal static void Run(ProbeReport report)
    {
        Check(report, "accepted legacy hashes", () =>
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                return "SKIP Windows x64 curve fixture; numerical checks still run.";
            var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/curves-legacy.json")))!;
            foreach (var (name, hash) in LegacyHashes())
                if (expected[name] != hash) throw new InvalidOperationException("Legacy curve rendering changed: " + name);
            return "Ten curve/mixed recipes captured from accepted 35162ddc before service changes are byte-identical.";
        });
        Check(report, "bounded legacy oracle and joint preparation", Oracle);
        Check(report, "independent primaries, transfer and spline references", Reference);
        Check(report, "smooth ramps, neutrality and sub-code detail", Ramps);
        Check(report, "identity, range, alpha and validated snapshot", Contracts);
    }

    private static void Check(ProbeReport report, string name, Func<string> run)
    {
        try
        {
            var detail = run();
            report.Checks.Add(new ProbeCheck("Rendered curves / " + name,
                detail.StartsWith("SKIP ") ? "SKIP" : "PASS", detail, Required: !detail.StartsWith("SKIP ")));
        }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Rendered curves / " + name, ex.ToString())); }
    }

    private static string Oracle()
    {
        using var source = FullBasicChecks.Synthetic();
        var bytes = new byte[source.ByteCount]; Marshal.Copy(source.GetPixels(), bytes, 0, bytes.Length);
        var pixels = new float[source.Width * source.Height * 4];
        for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
        {
            var at = (y * source.Width + x) * 4; var o = y * source.RowBytes + x * 4;
            pixels[at] = bytes[o + 2] / 255f; pixels[at + 1] = bytes[o + 1] / 255f;
            pixels[at + 2] = bytes[o] / 255f; pixels[at + 3] = 1;
        }
        foreach (var recipe in Recipes)
        {
            // Keep legacy encoded saturation here to isolate curve/preparation parity.
            // The native pipeline's intentionally different saturation has its own suite.
            var stage = ImageProcessor.PrepareFloatingBasic(source.Width, source.Height, pixels, Basic(recipe), curves: Curves(recipe));
            var result = ImageProcessor.RenderPreviewSkBitmap(source, Adjustments(recipe))!;
            using var owned = ReferenceEquals(result, source) ? result.Copy() : result;
            var expected = new byte[owned.ByteCount]; Marshal.Copy(owned.GetPixels(), expected, 0, expected.Length);
            for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
            {
                var at = (y * source.Width + x) * 4; var o = y * owned.RowBytes + x * 4;
                float r = pixels[at], g = pixels[at + 1], b = pixels[at + 2];
                stage.Apply(ref r, ref g, ref b, x, y);
                if (Math.Abs(r * 255 - expected[o + 2]) > 1.1 || Math.Abs(g * 255 - expected[o + 1]) > 1.1 || Math.Abs(b * 255 - expected[o]) > 1.1)
                    throw new InvalidOperationException($"Curve oracle mismatch {recipe.Name} at {x},{y}.");
            }
            if (stage.Grid is null) continue;
            var chain = ImageProcessor.BuildPointOpChain(Adjustments(recipe), source);
            var grid = ImageProcessor.BuildLocalToneGridFor(source, chain, 0);
            if (!stage.Grid.Gray.SequenceEqual(grid.Gray) || !stage.Grid.Coverage.SequenceEqual(grid.Coverage))
                throw new InvalidOperationException("Curves changed pre-curve local tone preparation.");
        }
        return "Ten exact-code floating recipes match the old renderer within its 1.1-code quantization/dither allowance. Mixed local-tone grids and alpha coverage match exactly; RGB/luminance/restore/vibrance ordering is exercised together.";
    }

    private static string Reference()
    {
        // Independent Double reference: affine master mapping commutes with the
        // RGBTone extrema rule. Per-channel curves must still follow the master.
        var curves = new RenderedCurveSettings(master: "0,12;255,240", red: "0,8;255,248", blue: "0,16;255,232");
        float[] colors = [.35f, .24f, .19f, 1, .15f, .4f, .2f, 1, .6f, .2f, .55f, 1, .25f, .25f, .25f, 1];
        var stage = ImageProcessor.PrepareFloatingBasic(4, 1, colors, new(), curves: curves);
        double[] toMelissa = [.529214, .330063, .140723, .098324, .873625, .028051, .016879, .117714, .865407];
        double[] fromMelissa = [2.034515, -.727257, -.307258, -.228704, 1.231430, -.002726, -.008573, -.153316, 1.161889];
        var maximum = 0.0;
        for (var x = 0; x < 4; x++)
        {
            double[] input = [colors[x * 4], colors[x * 4 + 1], colors[x * 4 + 2]];
            var pro = RenderedContrastChecks.Matrix(toMelissa, input.Select(RenderedContrastChecks.Decode).ToArray())
                .Select(v => RenderedContrastChecks.Encode(Math.Clamp(v, 0, 1))).ToArray();
            pro = pro.Select(v => (12 + 228 * v) / 255).ToArray();
            pro[0] = (8 + 240 * pro[0]) / 255;
            pro[2] = (16 + 216 * pro[2]) / 255;
            var expected = RenderedContrastChecks.Matrix(fromMelissa, pro.Select(RenderedContrastChecks.Decode).ToArray())
                .Select(v => RenderedContrastChecks.Encode(Math.Clamp(v, 0, 1))).ToArray();
            float r = colors[x * 4], g = colors[x * 4 + 1], b = colors[x * 4 + 2];
            stage.Apply(ref r, ref g, ref b, x, 0);
            maximum = Math.Max(maximum, new[] { (double)r, g, b }.Zip(expected).Max(v => Math.Abs(v.First - v.Second)));
        }
        if (maximum > .00008) throw new InvalidOperationException($"Independent Melissa reference error {maximum}.");

        // Closed-form natural spline through (0,0),(.5,.4),(1,1), rather
        // than another call to the application's curve-table/spline builder.
        double Natural(double x) => x <= .5 ? .7 * x + .4 * x * x * x : .4 + (x - .5) + .6 * Math.Pow(x - .5, 2) - .4 * Math.Pow(x - .5, 3);
        double Hermite(double t, double lo, double hi, double leftSlope, double rightSlope) =>
            (2 * t * t * t - 3 * t * t + 1) * lo + (t * t * t - 2 * t * t + t) * .5 * leftSlope +
            (-2 * t * t * t + 3 * t * t) * hi + (t * t * t - t * t) * .5 * rightSlope;
        double Pchip(double x) => x <= .5 ? Hermite(x * 2, 0, .4, .8, .96) : Hermite((x - .5) * 2, .4, 1, .96, 1.2);
        foreach (var luminance in new[] { false, true })
        {
            var control = luminance ? new RenderedCurveSettings(luminance: "0,0;127.5,102;255,255") : new(master: "0,0;127.5,102;255,255");
            var pixel = new float[] { .5f, .5f, .5f, 1 };
            var processor = ImageProcessor.PrepareFloatingBasic(1, 1, pixel, new(), curves: control);
            for (var i = 0; i <= 4096; i++)
            {
                float r = i / 4096f, g = r, b = r;
                var expected = luminance ? Pchip(r) : Natural(r);
                processor.Apply(ref r, ref g, ref b, 0, 0);
                if (new[] { r, g, b }.Any(v => Math.Abs(v - expected) > .00008))
                    throw new InvalidOperationException($"Independent {(luminance ? "PCHIP" : "natural cubic")} reference differs at {i}.");
            }
        }
        return $"Independent Double ProPhoto-primary/sRGB-transfer affine master/channel-order reference: maximum encoded error {maximum:G5}. Closed-form natural RGB and PCHIP HSL-lightness curves checked at 4097 gray values; tolerance 0.00008 covers existing F32 transfer-table interpolation.";
    }

    private static string Ramps()
    {
        foreach (var recipe in Recipes.Where(r => r.Name is "master-s" or "master-endpoints" or "luminance-s"))
        {
            var stage = ImageProcessor.PrepareFloatingBasic(1, 1, [.5f, .5f, .5f, 1], new(), curves: Curves(recipe));
            var previous = -1f;
            for (var i = 0; i <= 4096; i++)
            {
                float r = i / 4096f, g = r, b = r; stage.Apply(ref r, ref g, ref b, 0, 0);
                if (r < previous - .000002 || Math.Abs(r - g) > .00001 || Math.Abs(r - b) > .00001)
                    throw new InvalidOperationException("Monotone gray curve gained a reversal or color fringe.");
                previous = r;
            }
        }
        var precise = ImageProcessor.PrepareFloatingBasic(2, 1, [.5001f, .5001f, .5001f, 1, .5002f, .5002f, .5002f, 1], new(), curves: Curves(Recipes[1]));
        float r0 = .5001f, g0 = r0, b0 = r0, r1 = .5002f, g1 = r1, b1 = r1;
        precise.Apply(ref r0, ref g0, ref b0, 0, 0); precise.Apply(ref r1, ref g1, ref b1, 1, 0);
        if (r1 <= r0) throw new InvalidOperationException("Sub-RGB8 curve detail disappeared.");
        return "4097-step monotone/neutral master and luminance ramps, interior black/white endpoints, and sub-code input separation pass without an RGB8 working image. Non-monotone user curves remain intentionally allowed.";
    }

    private static string Contracts()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(6, 1,
            [4, -.25f, .3f, 1, .02f, .03f, .01f, .5f, 4, 4, 4, 1, -.25f, -.25f, -.25f, 1, .000001f, .000001f, .000001f, 1, 0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        var before = new float[24]; source.ReadLinearRow(0, before);
        foreach (var recipe in Recipes)
        {
            using var result = RenderedBasicControls.Apply(source, Basic(recipe), Curves(recipe));
            var output = new float[24]; result.ReadLinearRow(0, output);
            if (output.Any(v => !float.IsFinite(v)) || output[3] != 1 || output[7] != .5 || output.Skip(20).Any(v => v != 0) || output[8] <= 1 || output[12] >= 0)
                throw new InvalidOperationException("Curves lost finite signed/HDR range or alpha.");
            if (recipe.Name == "zero" && !before.SequenceEqual(output)) throw new InvalidOperationException("Neutral curves changed source values.");
        }
        var unchanged = new float[24]; source.ReadLinearRow(0, unchanged);
        if (!before.SequenceEqual(unchanged)) throw new InvalidOperationException("Curves modified the source.");
        foreach (var diagonal in new[] { "255,255;0,0", "0,0;64,64;255,255", "0.0,0;255,255.0", "", " " })
        {
            var curves = new RenderedCurveSettings(master: diagonal);
            if (!curves.IsIdentity) throw new InvalidOperationException("Redundant diagonal is not identity.");
            using var identity = RenderedBasicControls.Apply(source, new(), curves);
            var output = new float[24]; identity.ReadLinearRow(0, output);
            if (!before.SequenceEqual(output)) throw new InvalidOperationException("Diagonal curve lost exact identity.");
        }
        if (new RenderedCurveSettings(master: "20,20;235,235").IsIdentity)
            throw new InvalidOperationException("Interior diagonal endpoints were incorrectly discarded.");
        foreach (var basic in FullBasicChecks.Recipes)
        {
            using var accepted = RenderedBasicControls.Apply(source, FullBasicChecks.Settings(basic));
            using var neutralCurves = RenderedBasicControls.Apply(source, FullBasicChecks.Settings(basic), new(master: "0,0;64,64;255,255"));
            var expected = new float[24]; var actual = new float[24];
            accepted.ReadLinearRow(0, expected); neutralCurves.ReadLinearRow(0, actual);
            if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Neutral curve settings changed accepted basic controls: " + basic.Name);
        }
        var constructor = typeof(RenderedCurveSettings).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        for (var index = 0; index < 5; index++) foreach (var bad in new[] { "0,0", "0,0;0,255", "0,0;NaN,255", "0,0;255,Infinity", "-1,0;255,255", "0,0;255,256", "broken;255,255", "0,0;255,255;" })
        {
            var values = constructor.GetParameters().Select(p => p.DefaultValue).ToArray(); values[index] = bad;
            try { constructor.Invoke(values); throw new InvalidOperationException("Malformed curve accepted."); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
        }
        try
        {
            _ = new RenderedCurveSettings(master: string.Join(';', Enumerable.Range(0, 1025).Select(i => $"{(i / 8.0).ToString(CultureInfo.InvariantCulture)},0")));
            throw new InvalidOperationException("Unbounded curve preparation accepted.");
        }
        catch (ArgumentException) { }
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var curves = new RenderedCurveSettings(master: "255,240;0,12.5");
            var target = new ImageAdjustments(); curves.CopyTo(target); target.CurveRgbPoints = "0,0;255,255";
            if (curves.Master != "0,12.5;255,240") throw new InvalidOperationException("Curve snapshot/culture changed coordinates.");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        return "Whole-frame recipes preserve alpha/source ownership and neutral signed/HDR reserves. Default/redundant diagonal identity is exact; all five curves reject malformed/nonfinite/duplicate/out-of-range coordinates. Invariant fractional coordinates, sorted endpoints and immutable snapshots pass.";
    }

    private static bool HasReference(Recipe recipe) => recipe.Name is "master-s" or "red-lift" or "luminance-s" or "combined-curves";

    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        var cube = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? ProfiledPngFixture.Read(path) : null;
        using var source = cube?.Frame ?? RenderedContrastChecks.Input(path);
        if (cube is not null) report.Checks.Add(new ProbeCheck($"Curve cube / {name}", "PASS", $"{cube.Description}; ICC SHA256 {cube.ProfileHash}; decoded RGBA SHA256 {cube.PixelHash}. Every source pixel matches original ICC matrix/TRC and alpha; the adapter is diagnostic only."));
        using var legacySource = WorkingImageBoundary.ToDiagnosticSdrSrgb8(source);
        foreach (var recipe in Recipes)
        {
            using var result = RenderedBasicControls.Apply(source, Basic(recipe), Curves(recipe));
            SceneRenderingChecks.SavePreview(result, output, $"{name}-curves-{recipe.Name}.png");
            if (!HasReference(recipe)) continue;
            var legacyResult = ImageProcessor.RenderPreviewSkBitmap(legacySource, Adjustments(recipe))
                ?? throw new InvalidOperationException("Legacy curve reference failed.");
            using var owned = ReferenceEquals(legacyResult, legacySource) ? legacyResult.Copy() : legacyResult;
            using var linear = WorkingImageBoundary.FromEncodedSrgb(owned);
            SceneRenderingChecks.SavePreview(linear, output, $"{name}-curves-{recipe.Name}-legacy.png");
        }
        report.Checks.Add(new ProbeCheck($"Rendered curves / {name}", "PASS", $"{Path.GetFileName(path)}: ten full-resolution recipes and four old-renderer references from the same developed source. Master/channel Melissa curves, HSL-lightness curve, interior endpoints, mixed basic controls and monochrome/vibrance are covered. Appearance awaits visual review."));
    }

    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new System.Text.StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>F16 tone curves</title><style>body{font:16px system-ui;background:#202326;color:#eee;margin:24px}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;height:400px;object-fit:contain;background:#111}a{color:#bce0ff}code{font-size:13px}</style><h1>F16 tone curves</h1><p>Working storage: linear Rec.2020 F16. Master/RGB curves retain ProPhoto primaries with the sRGB transfer function; the luminance curve retains encoded-sRGB HSL lightness. All run in the existing basic group's F32 adapter. Review smoothness, neutral grays, skin, saturated flowers, highlight/shadow separation and interior black/white endpoints. Mixed-basic-curves uses the accepted native Rec.2020 saturation. Gray-master should remain neutral. Four old-editor references use the same developed source through its sRGB8 boundary; wide-gamut differences can be intentional. These PNGs are diagnostic SDR sRGB. <a href="report.md">Validation report</a></p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2><div class='grid'>");
            foreach (var recipe in Recipes)
            {
                var file = $"image-{i + 1}-curves-{recipe.Name}.png";
                html.Append($"<figure><figcaption>{recipe.Name}<br><code>RGB {recipe.Master}<br>R {recipe.Red}; G {recipe.Green}; B {recipe.Blue}<br>L {recipe.Luminance}</code></figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{recipe.Name}'></a></figure>");
            }
            html.Append("</div><h3>Old sRGB8 references</h3><div class='grid'>");
            foreach (var recipe in Recipes.Where(HasReference))
            {
                var file = $"image-{i + 1}-curves-{recipe.Name}-legacy.png";
                html.Append($"<figure><figcaption>{recipe.Name} — old editor</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{recipe.Name}'></a></figure>");
            }
            html.Append("</div>");
        }
        File.WriteAllText(Path.Combine(output, "curves-review.html"), html.Append("</html>").ToString());
    }
}
