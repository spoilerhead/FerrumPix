using System.Net;
using FerrumPix.Services;
using SkiaSharp;
using static ImagingPrecisionProbe.RenderedContrastChecks;

namespace ImagingPrecisionProbe;

internal static class BasicToneChecks
{
    private sealed record Recipe(string Name, float Brightness = 0, float Contrast = 0, double Ev = 0);
    private static readonly Recipe[] Recipes = [new("zero"), new("brightness-minus", -30), new("brightness-plus", 30), new("exposure-minus", Ev: -1), new("exposure-plus", Ev: 1), new("combined", 20, 30, -1), new("contrast-regression", Contrast: 40)];
    private static readonly double[] ToSrgb = [1.660491002, -.587641139, -.072849863, -.124550475, 1.132899897, -.008349422, -.018150763, -.100578898, 1.118729661];
    private static readonly double[] ToRec = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];

    internal static void Run(ProbeReport report)
    {
        Check(report, "scalar/contrast reference and ordering", Reference);
        Check(report, "identity, ramps and contracts", Contracts);
    }

    private static void Check(ProbeReport report, string name, Func<string> test)
    {
        try { report.Checks.Add(new ProbeCheck("Basic tone / " + name, "PASS", test())); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Basic tone / " + name, ex.ToString())); }
    }

    // Independent Double reference for the existing scalar policy. The old Single
    // kernel's centering roundoff is covered by the F16 error budget, not copied here.
    private static double[] Scalar(float brightness, double ev)
    {
        var gain = Math.Pow(2, ev);
        var offset = brightness / 100.0 * 80 / 255;
        double Transfer(double x) => Encode(Decode(x) * gain) + offset;
        var high = Transfer(1); var low = Transfer(0);
        var above = Math.Max(0, high - 1); var below = Math.Max(0, -low);
        var shoulder = Math.Clamp(.12 + .5 * above, .12, .4);
        var toe = Math.Clamp(.12 + .5 * below, .12, .4);
        var roll = Math.Clamp((above + below) / .12, 0, 1);
        return Enumerable.Range(0, 4097).Select(i =>
        {
            var y = Transfer(i / 4096.0);
            var soft = y > 1 - shoulder ? 1 - shoulder * Math.Exp(-(y - (1 - shoulder)) / shoulder)
                : y < toe ? toe * Math.Exp((y - toe) / toe) : y;
            var hard = Math.Clamp(y, 0, 1);
            return Math.Clamp(hard + (soft - hard) * roll, 0, 1);
        }).ToArray();
    }

    private static double[] Zones(double[] rgb, double[] curve)
    {
        var low = rgb.Min(); var high = rgb.Max();
        var bottom = Sample(curve, low); var top = Sample(curve, high);
        return rgb.Select(v => high - low < 1e-6 ? top : bottom + (v - low) * (top - bottom) / (high - low)).ToArray();
    }

    private static string Reference()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(4, 2,
            [.000001f, .000001f, .000001f, 1, .02f, .04f, .01f, 1, .3f, .2f, .1f, 1, .7f, .8f, .6f, 1,
             3, -.25f, .8f, 1, .08f, .03f, .01f, .5f, 4, 4, 4, 1, 0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        Recipe[] cases = [new("dark", -100), new("bright", 100), new("under", Ev: -4), new("over", Ev: 4), new("mixed", 20, 30, -1), new("other-order", -30, -50, 1)];
        double orderSensitivity = 0;
        foreach (var recipe in cases)
        {
            var scalar = Scalar(recipe.Brightness, recipe.Ev); var zones = Curve(recipe.Contrast);
            using var result = RenderedBasicTone.Apply(source, recipe.Brightness, recipe.Contrast, recipe.Ev);
            var input = new float[16]; var output = new float[16];
            for (var y = 0; y < 2; y++)
            {
                source.ReadLinearRow(y, input); result.ReadLinearRow(y, output);
                for (var x = 0; x < 4; x++)
                {
                    var at = x * 4; var alpha = (double)input[at + 3];
                    if (output[at + 3] != alpha) throw new InvalidOperationException("Basic tone changed alpha.");
                    if (alpha == 0) continue;
                    double[] rgb = [input[at] / alpha, input[at + 1] / alpha, input[at + 2] / alpha];
                    var encoded = Matrix(ToSrgb, rgb).Select(v => Encode(Math.Clamp(v, 0, 1))).ToArray();
                    var mapped = Zones(encoded.Select(v => Sample(scalar, v)).ToArray(), zones);
                    if (recipe.Contrast != 0)
                    {
                        var reversed = Zones(encoded, zones).Select(v => Sample(scalar, v)).ToArray();
                        orderSensitivity = Math.Max(orderSensitivity, mapped.Zip(reversed).Max(pair => Math.Abs(pair.First - pair.Second)));
                    }
                    var delta = Matrix(ToRec, Enumerable.Range(0, 3).Select(c => Decode(mapped[c]) - Decode(encoded[c])).ToArray());
                    for (var c = 0; c < 3; c++)
                    {
                        var expected = input[at + c] + delta[c] * alpha;
                        if (!float.IsFinite(output[at + c]) || Math.Abs(output[at + c] - expected) > Math.Max(2e-7, Math.Abs(expected) * .001))
                            throw new InvalidOperationException($"Basic tone {recipe.Name}, {x},{y}/{c}: {output[at + c]} != {expected}.");
                    }
                }
            }
            result.ReadLinearRow(1, output);
            if (output[0] <= 1 || output[1] >= 0 || output[8] <= 1) throw new InvalidOperationException("Basic tone clipped original HDR/signed reserves.");
        }
        if (orderSensitivity < .001) throw new InvalidOperationException("Order fixture is not sensitive.");
        return "Independent Double scalar exposure/brightness/shoulder/toe reference, then measured min/max Contrast; +/-100 brightness, +/-4 EV and mixed controls. Sensitive to swapped order. Signed/HDR, shadows and partial/zero alpha included.";
    }

    private static string Contracts()
    {
        using var ramp = LinearWorkingFrame.Create(4097, 1, WorkingImageDomain.RenderedLinear, (_, row) =>
        {
            for (var x = 0; x < 4097; x++)
            {
                row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = (float)Decode(x / 4096.0);
                row[x * 4 + 3] = 1;
            }
        });
        var original = new float[4097 * 4]; ramp.ReadLinearRow(0, original);
        using (var zero = RenderedBasicTone.Apply(ramp, 0))
        {
            var row = new float[original.Length]; zero.ReadLinearRow(0, row);
            if (!original.SequenceEqual(row)) throw new InvalidOperationException("Basic tone zero is not exact.");
        }
        foreach (var recipe in Recipes)
        {
            using var result = RenderedBasicTone.Apply(ramp, recipe.Brightness, recipe.Contrast, recipe.Ev);
            var row = new float[original.Length]; result.ReadLinearRow(0, row);
            for (var x = 1; x < 4097; x++)
                if (row[x * 4] < row[(x - 1) * 4] || Math.Abs(row[x * 4] - row[x * 4 + 1]) > 1e-6 || Math.Abs(row[x * 4] - row[x * 4 + 2]) > 1e-6)
                    throw new InvalidOperationException("Basic tone breaks monotonicity/neutrality.");
        }
        var after = new float[original.Length]; ramp.ReadLinearRow(0, after);
        if (!original.SequenceEqual(after)) throw new InvalidOperationException("Basic tone modified its source.");
        foreach (var bad in new[] { float.NaN, float.PositiveInfinity, -101, 101 })
        {
            Reject(() => { using var image = RenderedBasicTone.Apply(ramp, bad); });
            Reject(() => { using var image = RenderedBasicTone.Apply(ramp, 0, bad); });
        }
        foreach (var bad in new[] { double.NaN, double.NegativeInfinity, -4.01, 4.01 }) Reject(() => { using var image = RenderedBasicTone.Apply(ramp, 0, 0, bad); });
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.SceneLinear);
        Reject(() => { using var image = RenderedBasicTone.Apply(scene, 0); });
        return "Exact neutral and immutable input; neutral ramps remain monotone across individual/combined recipes; invalid slider/EV/domain rejected. Existing contrast reference checks still run through its compatibility entry point.";
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid basic-tone contract accepted.");
    }

    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        var raw = RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        using var scene = raw ? RawDecodeService.ReadSceneSource(path, true, true) : null;
        using var source = scene is null ? Input(path) : RawSceneDevelopment.Render(scene, 0, true);
        foreach (var recipe in Recipes)
        {
            // RAW exposure belongs before the scene curve. Rendered-file exposure uses
            // the scalar compatibility policy. Never apply BOTH to the same RAW recipe.
            using var exposedRaw = scene is not null && recipe.Ev != 0 ? RawSceneDevelopment.Render(scene, recipe.Ev, true) : null;
            var developed = exposedRaw ?? source;
            var renderedEv = raw ? 0 : recipe.Ev;
            using var result = RenderedBasicTone.Apply(developed, recipe.Brightness, recipe.Contrast, renderedEv);
            SceneRenderingChecks.SavePreview(result, output, $"{name}-basic-{recipe.Name}.png");
            if (recipe.Name is "zero" or "contrast-regression") continue;
            using var legacySource = WorkingImageBoundary.ToDiagnosticSdrSrgb8(developed);
            var old = ImageProcessor.RenderPreviewSkBitmap(legacySource, new ImageAdjustments { Brightness = recipe.Brightness, Contrast = recipe.Contrast, Exposure = (float)(renderedEv * 25) })
                ?? throw new InvalidOperationException("Legacy basic tone failed.");
            using var owned = ReferenceEquals(old, legacySource) ? old.Copy() : old;
            using var linear = WorkingImageBoundary.FromEncodedSrgb(owned);
            SceneRenderingChecks.SavePreview(linear, output, $"{name}-basic-{recipe.Name}-legacy.png");
        }
        report.Checks.Add(new ProbeCheck($"Basic tone / {name}", "PASS", $"{Path.GetFileName(path)}: brightness +/-30, exposure +/-1 EV, combined -1 EV/brightness +20/contrast +30, zero and accepted contrast +40 regression. {(raw ? "RAW exposure applied only before scene rendering." : "Rendered-file exposure applied only in scalar stage.")} Other basic controls neutral; visual acceptance pending."));
    }

    internal static void WriteReview(string output, IReadOnlyList<string> images)
    {
        var html = new System.Text.StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>F16 basic tone</title><style>body{background:#202326;color:#eee;font:16px system-ui;margin:24px}.pair{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;height:460px;object-fit:contain;background:#111}a{color:#bce0ff}</style><h1>Brightness, exposure and Contrast</h1><p>Neutral image first; each pair below is new F16 (left), old sRGB8 reference from the same developed source (right). Compare skin, gradients, shadows, saturated colors and the combined recipe. RAW exposure runs only before scene rendering; JPEG/TIFF exposure uses the existing rendered-file scalar policy. Other tools are neutral. All PNGs are diagnostic SDR sRGB, so this does not demonstrate HDR output. <a href="report.md">Report</a></p>
            """);
        for (var i = 0; i < images.Count; i++)
        {
            html.Append($"<h2>{WebUtility.HtmlEncode(Path.GetFileName(images[i]))}</h2><a href='image-{i + 1}-basic-zero.png'>Neutral image</a>");
            foreach (var recipe in Recipes.Where(r => r.Name is not ("zero" or "contrast-regression")))
            {
                html.Append($"<h3>{recipe.Name}</h3><div class='pair'>");
                foreach (var suffix in new[] { "", "-legacy" })
                {
                    var file = $"image-{i + 1}-basic-{recipe.Name}{suffix}.png";
                    html.Append($"<figure><figcaption>{(suffix == "" ? "F16" : "old reference")}</figcaption><a href='{file}'><img loading='lazy' src='{file}' alt='{recipe.Name}'></a></figure>");
                }
                html.Append("</div>");
            }
        }
        File.WriteAllText(Path.Combine(output, "basic-tone-review.html"), html.Append("</html>").ToString());
    }
}
