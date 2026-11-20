using System.Diagnostics;
using System.Net;
using System.Text;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;

// Review previews are explicitly resized; sharpening/NR also receive untouched
// source-pixel crops so a small overview cannot conceal noise or halo problems.
internal static class EffectReview
{
    internal static void Capture(ProbeReport report, string output, string path, string name, int maximum)
    {
        using var full = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? ProfiledPngFixture.Read(path).Frame : RenderedContrastChecks.Input(path);
        var factor = maximum == 0 ? 1 : Math.Min(1, maximum / (double)Math.Max(full.Width, full.Height));
        var planes = WorkingEffectPlanes.Load(full);
        using var source = planes.ResizeTo(Math.Max(1, (int)Math.Round(full.Width * factor)), Math.Max(1, (int)Math.Round(full.Height * factor))).ToFrame();
        var html = new StringBuilder($"<!doctype html><html lang='en'><meta charset='utf-8'><title>Effects review</title><style>body{{background:#202326;color:#eee;font:16px system-ui;margin:24px}}.grid{{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px}}img{{width:100%;height:330px;object-fit:contain;background:#111}}figure{{margin:0}}a{{color:#bce0ff}}</style><h1>{WebUtility.HtmlEncode(Path.GetFileName(path))}</h1><p>Full source {full.Width}×{full.Height}; overview {source.Width}×{source.Height}. All PNGs are diagnostic SDR sRGB. Native effects use linear Rec.2020 and intentionally differ from gamma-space legacy effects. Click images for actual pixels. Editor routing remains unchanged.</p><div class='grid'>");
        using var oldSource = WorkingImageBoundary.ToDiagnosticSdrSrgb8(source);
        foreach (var recipe in EffectRecipes.All)
        {
            var settings = EffectRecipes.Settings(recipe); var timer = Stopwatch.StartNew();
            using var result = EffectChecks.Apply(source, settings);
            Save(recipe.Name, result);
            report.Checks.Add(new ProbeCheck($"Effect photo / {name}/{recipe.Name}", "PASS", $"{source.Width}x{source.Height}, {timer.Elapsed.TotalSeconds:F2}s; appearance awaits review."));
            if (!(recipe.Name.StartsWith("noise-") || recipe.Name is "unsharp" or "deconvolution" or "high-pass" or "haze" or "dehaze" or "grain-color")) continue;
            var old = ImageProcessor.RenderPreviewSkBitmap(oldSource, settings) ?? throw new InvalidOperationException("Legacy effect failed.");
            using var owned = ReferenceEquals(old, oldSource) ? old.Copy() : old;
            using var legacy = WorkingImageBoundary.FromEncodedSrgb(owned);
            Save(recipe.Name + "-legacy", legacy);
        }
        html.Append("</div><h2>Untouched source-pixel crops</h2><p>256×256 crops, no scaling before filtering. Fixed-pixel sharpening/NR only; crop boundaries are locally edge-extended.</p><div class='grid'>");
        var size = Math.Min(256, Math.Min(full.Width, full.Height));
        foreach (var (label, left, top) in new[] { ("center", (full.Width - size) / 2, (full.Height - size) / 2), ("lower-left", 0, full.Height - size) })
        {
            var row = new float[full.Width * 4];
            using var crop = LinearWorkingFrame.Create(size, size, full.Domain, (y, target) => { full.ReadLinearRow(top + y, row); row.AsSpan(left * 4, size * 4).CopyTo(target); });
            using var oldCrop = WorkingImageBoundary.ToDiagnosticSdrSrgb8(crop);
            foreach (var recipe in EffectRecipes.All.Where(r => r.Name.StartsWith("noise-") || r.Name is "zero" or "color-noise" or "color-coarse" or "unsharp" or "unsharp-masked" or "high-pass" or "deconvolution"))
            {
                var settings = EffectRecipes.Settings(recipe);
                using var result = EffectChecks.Apply(crop, settings); Save($"{label}-{recipe.Name}", result);
                var old = ImageProcessor.RenderPreviewSkBitmap(oldCrop, settings) ?? throw new InvalidOperationException("Legacy crop failed.");
                using var owned = ReferenceEquals(old, oldCrop) ? old.Copy() : old;
                using var legacy = WorkingImageBoundary.FromEncodedSrgb(owned); Save($"{label}-{recipe.Name}-legacy", legacy);
            }
            if (label == "center" && DenoiseModelService.ImageAvailable)
                foreach (var strength in new[] { 0f, .65f, 1f })
                { using var result = DenoiseModelService.DenoiseWorkingImage(crop, strength, tileEdge: 128); Save($"{label}-NAFNet-{strength}", result); }
        }
        html.Append("</div><h2>Depth and lights</h2><p>Explicit synthetic floating depth ramp, far on the left and near on the right; this tests effect behavior, not AI depth estimation.</p><div class='grid'>");
        var depth = new WorkingCoverage(64, 64, Enumerable.Range(0, 4096).Select(i => i % 64 / 63f).ToArray());
        using (var fog = WorkingDepthEffects.Fog(source, depth, 65, 15, 85, "#FFCCDDEE")) Save("depth-fog", fog);
        foreach (var corners in new[] { 0, 5, 8 })
        { using var blur = WorkingDepthEffects.Blur(source, depth, 45, 60, 55, 30, corners); Save("depth-blur-" + corners, blur); }
        foreach (var (label, a) in LightingLegacy.Recipes())
        { using var lit = ImageProcessor.ApplyWorkingLightSources(source, a.LightSources, a.LightAmbient, label == "depth" ? depth : null!); Save("light-" + label, lit); }
        html.Append("</div></html>");
        File.WriteAllText(Path.Combine(output, name + "-effects.html"), html.ToString());
        void Save(string label, LinearWorkingFrame frame)
        {
            var file = $"{name}-effects-{label}.png";
            SceneRenderingChecks.SavePreview(frame, output, file);
            html.Append($"<figure><figcaption>{WebUtility.HtmlEncode(label)}</figcaption><a href='{file}'><img loading='lazy' src='{file}'></a></figure>");
        }
    }

    internal static void FullResolutionSmoke(ProbeReport report, string output, string path)
    {
        using var source = RenderedContrastChecks.Input(path);
        var timer = Stopwatch.StartNew();
        using var result = EffectChecks.Apply(source, EffectRecipes.Settings(EffectRecipes.All.Single(r => r.Name == "mixed")));
        if (result.Width != source.Width || result.Height != source.Height) throw new InvalidOperationException("Full resolution was lost.");
        var elapsed = timer.Elapsed.TotalSeconds;
        var row = new float[result.Width * 4]; var peak = 0f;
        for (var y = 0; y < result.Height; y++)
        {
            result.ReadLinearRow(y, row);
            foreach (var value in row) { if (!float.IsFinite(value)) throw new InvalidOperationException("Nonfinite full-resolution result."); peak = Math.Max(peak, value); }
        }
        using var preview = WorkingEffectPlanes.Load(result).ResizeTo(1536, Math.Max(1, (int)Math.Round(result.Height * 1536.0 / result.Width))).ToFrame();
        SceneRenderingChecks.SavePreview(preview, output, "full-resolution-mixed.png");
        report.Checks.Add(new ProbeCheck("Effects full-resolution mixed chain", "PASS", $"{source.Width}x{source.Height}; {elapsed:F2}s effect-only; process peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0} MiB; finite native peak {peak:G6}. Saved image is a resized viewing artifact after full-resolution processing, not a speed benchmark."));
    }

    internal static void Index(string output, IReadOnlyList<string> images) =>
        File.WriteAllText(Path.Combine(output, "effects-review.html"), "<!doctype html><meta charset='utf-8'><h1>Native effects, sharpening and denoise</h1><p>Review each source. Overview, actual-pixel crops, old references, depth and lighting are labelled separately.</p>" +
            string.Join("", images.Select((p, i) => $"<p><a href='image-{i + 1}-effects.html'>{WebUtility.HtmlEncode(Path.GetFileName(p))}</a></p>")));
}
