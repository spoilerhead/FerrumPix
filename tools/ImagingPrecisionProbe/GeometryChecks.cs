using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class GeometryChecks
{
    private sealed record Recipe(string Name, params GeometryOperation[] Steps);
    private static GeometryOperation Step(string kind, Action<ImageAdjustments> edit)
    {
        var a = new ImageAdjustments(); edit(a); return new() { Kind = kind, Adjustments = a };
    }
    private static ImageAdjustments Settings(params GeometryOperation[] steps) => new() { GeometryOperations = steps.ToList() };
    private static readonly Recipe[] Recipes =
    [
        new("zero"),
        new("crop", Step("crop", a => { a.CropLeftPercent = 11; a.CropTopPercent = 7; a.CropRightPercent = 17; a.CropBottomPercent = 13; })),
        new("rotate-flip", Step("transform", a => { a.RotationDegrees = 90; a.FlipHorizontal = true; })),
        new("resize", Step("resize", a => { a.ResizeScalePercent = 46; })),
        new("straighten-expand", Step("transform", a => { a.StraightenDegrees = 12; a.StraightenExpandCanvas = true; })),
        new("straighten-crop", Step("transform", a => { a.StraightenDegrees = -12; a.StraightenAutoCrop = true; })),
        new("perspective", Step("perspective", a => { a.PerspectiveHorizontal = 14; a.PerspectiveVertical = -9; a.PerspectiveAspect = 12; })),
        new("mesh", Step("warp", a => a.ImageWarp = new() { Kind = "Gitter", Columns = 1, Rows = 1, Nodes = [7, 2, 95, 8, 3, 95, 100, 100] })),
        new("crop-canvas", Step("crop", a => { a.CropLeftPercent = 20; a.CropRightPercent = 20; }),
                           Step("canvas", a => { a.CanvasWidth = 420; a.CanvasHeight = 300; a.CanvasAnchor = "center"; })),
        new("mixed", Step("crop", a => { a.CropTopPercent = 10; a.CropBottomPercent = 15; }),
                     Step("transform", a => { a.RotationDegrees = 270; a.FlipVertical = true; }),
                     Step("resize", a => a.ResizeScalePercent = 46))
    ];

    internal static void Run(ProbeReport report)
    {
        Check(report, "exact permutations, crops and subnormals", Permutations);
        Check(report, "linear area, fractional alpha and anisotropic resize", ResizeReference);
        Check(report, "order, transparent canvas, coordinate scaling and contracts", Contracts);
        Check(report, "projective and mesh coordinate agreement", Coordinates);
    }
    private static void Check(ProbeReport report, string name, Func<string> run)
    {
        try { report.Checks.Add(new ProbeCheck("Working geometry / " + name, "PASS", run())); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Working geometry / " + name, ex.ToString())); }
    }
    private static float[] Pixels(LinearWorkingFrame frame)
    {
        var result = new float[frame.Width * frame.Height * 4]; var row = new float[frame.Width * 4];
        for (var y = 0; y < frame.Height; y++) { frame.ReadLinearRow(y, row); row.CopyTo(result, y * row.Length); }
        return result;
    }
    private static LinearWorkingFrame Source(WorkingImageDomain domain = WorkingImageDomain.RenderedLinear) =>
        LinearWorkingFrame.Create(7, 5, domain, (y, row) =>
        {
            for (var x = 0; x < 7; x++)
            {
                var i = x * 4; var alpha = (x + y) % 3 == 0 ? .5f : 1f;
                row[i] = (x + y * 7 - 10) * alpha; row[i + 1] = .000000059604644775390625f * (x + 1) * alpha;
                row[i + 2] = (y - 1) * .2f * alpha; row[i + 3] = alpha;
            }
        });
    private static string Permutations()
    {
        foreach (var domain in Enum.GetValues<WorkingImageDomain>())
        {
            using var source = Source(domain); var before = Pixels(source);
            using var identity = WorkingGeometry.Apply(source, new());
            if (ReferenceEquals(source, identity) || identity.Domain != domain || !Pixels(identity).SequenceEqual(before))
                throw new InvalidOperationException("Identity changed pixels, domain or ownership.");
            using var unmovedMesh = WorkingGeometry.Apply(source, Settings(Step("warp", a =>
                a.ImageWarp = new() { Kind = "Gitter", Columns = 1, Rows = 1, Nodes = [0, 0, 100, 0, 0, 100, 100, 100] })));
            if (!Pixels(unmovedMesh).SequenceEqual(before)) throw new InvalidOperationException("Unmoved mesh changed samples.");
            foreach (var angle in new[] { 0, 90, 180, 270 })
            foreach (var flip in new[] { false, true })
            {
                using var turned = WorkingGeometry.Apply(source, Settings(Step("transform", a => { a.RotationDegrees = angle; a.FlipHorizontal = flip; })));
                var actual = Pixels(turned);
                for (var sy = 0; sy < 5; sy++) for (var sx = 0; sx < 7; sx++)
                {
                    var (tx, ty) = angle switch { 90 => (4 - sy, sx), 180 => (6 - sx, 4 - sy), 270 => (sy, 6 - sx), _ => (sx, sy) };
                    if (flip) tx = turned.Width - 1 - tx;
                    for (var c = 0; c < 4; c++) if (actual[(ty * turned.Width + tx) * 4 + c] != before[(sy * 7 + sx) * 4 + c])
                        throw new InvalidOperationException($"Permutation changed value: {angle}/{flip}/{sx}/{sy}/{c}.");
                }
            }
            using var crop = WorkingGeometry.Apply(source, Settings(Step("crop", a => { a.CropLeftPercent = 100f / 7; a.CropTopPercent = 20; a.CropRightPercent = 200f / 7; a.CropBottomPercent = 20; })));
            if (crop.Width != 4 || crop.Height != 3) throw new InvalidOperationException("Crop size differs.");
            var cropped = Pixels(crop);
            for (var y = 0; y < 3; y++) for (var x = 0; x < 4; x++) for (var c = 0; c < 4; c++)
                if (cropped[(y * 4 + x) * 4 + c] != before[((y + 1) * 7 + x + 1) * 4 + c]) throw new InvalidOperationException("Crop changed a code.");
            if (!before.SequenceEqual(Pixels(source))) throw new InvalidOperationException("Geometry modified source.");
        }
        return "Independent integer-coordinate oracle: crops and all quarter-turn/horizontal-flip combinations preserve exact signed/HDR/partial-alpha/subnormal samples in both scene and rendered domains.";
    }
    private static string ResizeReference()
    {
        using var checker = LinearWorkingFrame.FromLinearPixels(2, 1, [0, 0, 0, 1, 1, 1, 1, 1], WorkingImageDomain.RenderedLinear);
        using var mean = WorkingGeometry.Apply(checker, Settings(Step("resize", a => { a.ResizeWidth = 1; a.ResizeHeight = 1; a.LockResizeAspect = false; })));
        if (!Pixels(mean).SequenceEqual(new float[] { .5f, .5f, .5f, 1 })) throw new InvalidOperationException("Black/white energy mean is not linear 0.5.");
        using var transparent = LinearWorkingFrame.FromLinearPixels(2, 1, [4, -1, .5f, .5f, 0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        using var partial = WorkingGeometry.Apply(transparent, Settings(Step("resize", a => { a.ResizeWidth = 1; a.ResizeHeight = 1; a.LockResizeAspect = false; })));
        if (!Pixels(partial).SequenceEqual(new float[] { 2, -.5f, .25f, .25f })) throw new InvalidOperationException("Premultiplied/HDR averaging changed.");
        // Independent overlap integration plus linear reconstruction per axis,
        // including anisotropic reduction/magnification and noninteger footprints.
        using var source = Source(); var input = Pixels(source);
        foreach (var (w, h) in new[] { (3, 2), (11, 9), (3, 9), (11, 2), (1, 1) })
        {
            using var result = WorkingGeometry.Apply(source, Settings(Step("resize", a => { a.ResizeWidth = w; a.ResizeHeight = h; a.LockResizeAspect = false; })));
            var actual = Pixels(result);
            double Weight(int s, int target, int count, int sourceCount)
            {
                var scale = sourceCount / (double)count;
                if (scale > 1) return Math.Max(0, Math.Min((target + 1) * scale, s + 1) - Math.Max(target * scale, s)) / scale;
                var center = Math.Clamp((target + .5) * scale - .5, 0, sourceCount - 1);
                return Math.Max(0, 1 - Math.Abs(s - center));
            }
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) for (var c = 0; c < 4; c++)
            {
                double expected = 0;
                for (var sy = 0; sy < 5; sy++) for (var sx = 0; sx < 7; sx++)
                    expected += input[(sy * 7 + sx) * 4 + c] * Weight(sx, x, w, 7) * Weight(sy, y, h, 5);
                var half = (float)(Half)(float)expected;
                if (actual[(y * w + x) * 4 + c] != half) throw new InvalidOperationException($"Independent resize differs at {w}/{h}/{x}/{y}/{c}.");
            }
        }
        return "Full independent Double overlap oracle agrees after one half rounding; black/white integrates to 0.5 linear, transparent signed/HDR averaging and mixed-axis kernels pass.";
    }
    private static string Contracts()
    {
        using var source = Source();
        var ordered = Settings(Step("crop", a => { a.CropLeftPercent = 100f / 7; a.CropRightPercent = 100f / 7; }),
                               Step("canvas", a => { a.CanvasWidth = 9; a.CanvasHeight = 7; }));
        using var output = WorkingGeometry.Apply(source, ordered); var data = Pixels(output);
        if (output.Width != 9 || output.Height != 7 || data.Take(9 * 4).Any(v => v != 0) ||
            Enumerable.Range(0, 7).Any(y => data[(y * 9) * 4 + 3] != 0))
            throw new InvalidOperationException("Crop/canvas resurrected removed content or filled transparent margins.");
        var scaled = Settings(Step("resize", a => { a.ResizeWidth = 14; a.ResizeHeight = 10; a.LockResizeAspect = false; }));
        scaled.SourceWidthPixels = 14; scaled.SourceHeightPixels = 10;
        using var resized = WorkingGeometry.Apply(source, scaled);
        if (resized.Width != 7 || resized.Height != 5) throw new InvalidOperationException("Preview coordinate scaling differs.");
        if (scaled.GeometryOperations[0].Adjustments.ResizeWidth != 14) throw new InvalidOperationException("Recipe mutated.");
        foreach (var a in new[] { Settings(new GeometryOperation { Kind = "unknown", Adjustments = new() }), Settings(Step("transform", a => a.StraightenDegrees = float.NaN)) })
        {
            try { using var rejected = WorkingGeometry.Apply(source, a); throw new Exception("Invalid geometry accepted."); }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { }
        }
        return "Crop-before-canvas cannot reveal discarded pixels; transparent margins, preview/full-size coordinate scaling, immutable recipe/source and unknown/nonfinite rejection pass.";
    }
    private static string Coordinates()
    {
        // Coordinate agreement is a shared contract with mask/overlay mapping.
        // This complements independent permutation/resize oracles; it is not an
        // independent test of the editor's perspective model itself.
        using var source = LinearWorkingFrame.Create(64, 48, WorkingImageDomain.SceneLinear, (y, row) =>
        {
            for (var x = 0; x < 64; x++) { row[x * 4] = x; row[x * 4 + 1] = y; row[x * 4 + 2] = -.25f; row[x * 4 + 3] = 1; }
        });
        foreach (var recipe in Recipes.Where(r => r.Name is "perspective" or "mesh" or "straighten-expand" or "straighten-crop"))
        {
            var a = Settings(recipe.Steps); using var result = WorkingGeometry.Apply(source, a); var values = Pixels(result); var compared = 0;
            var prepared = ImageProcessor.PrepareGeometryInverse(64, 48, a);
            for (var y = 2; y < result.Height - 2; y += 3) for (var x = 2; x < result.Width - 2; x += 3)
            {
                SKPoint p = default;
                if (!ImageProcessor.TryGeometryOutputToSourcePoint(x + .5, y + .5, prepared, ref p) || p.X < 2 || p.Y < 2 || p.X > 62 || p.Y > 46) continue;
                var i = (y * result.Width + x) * 4;
                if (Math.Abs(values[i] - (p.X - .5)) > .04 || Math.Abs(values[i + 1] - (p.Y - .5)) > .04 || values[i + 3] != 1)
                    throw new InvalidOperationException("Pixel/mask coordinate disagreement: " + recipe.Name);
                compared++;
            }
            if (compared < 20) throw new InvalidOperationException("Insufficient coordinate coverage.");
        }
        return "Perspective, expanded/cropped straighten and mesh pixels align with prepared mask/overlay inverse coordinates; interior linear coordinate fields preserved within half tolerance.";
    }

    internal static void Capture(ProbeReport report, string output, string path, string name)
    {
        var cube = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? ProfiledPngFixture.Read(path) : null;
        using var source = cube?.Frame ?? RenderedContrastChecks.Input(path);
        using var oldSource = WorkingImageBoundary.ToDiagnosticSdrSrgb8(source);
        foreach (var recipe in Recipes)
        {
            var a = Settings(recipe.Steps);
            // Canvas dimensions are fixture-relative, not a giant frame around a
            // thumbnail. All input recipes use the same normalized geometry.
            a.SourceWidthPixels = 350; a.SourceHeightPixels = 250;
            using var result = WorkingGeometry.Apply(source, a);
            SceneRenderingChecks.SavePreview(result, output, $"{name}-geometry-{recipe.Name}.png");
            var size = ImageProcessor.ComputeGeometryOutputSize(source.Width, source.Height, a);
            if (result.Width != size.Width || result.Height != size.Height) throw new InvalidOperationException("Native/legacy geometry dimensions disagree.");
            if (recipe.Name is "resize" or "perspective" or "mixed")
            {
                using var old = ImageProcessor.ApplyGeometryAdjustmentsSk(oldSource, a);
                using var frame = WorkingImageBoundary.FromEncodedSrgb(old);
                SceneRenderingChecks.SavePreview(frame, output, $"{name}-geometry-{recipe.Name}-legacy.png");
            }
        }
        report.Checks.Add(new ProbeCheck($"Working geometry / {name}", "PASS", "Ten full-resolution native geometry recipes; output dimensions match shared editor geometry; three old encoded-space references. Visual acceptance pending."));
    }
    internal static void WriteReview(string output, IReadOnlyList<string> images) =>
        StageReview.Write(output, images, "geometry", "geometry-review.html", "Linear Rec.2020 geometry",
            "Check crops, flips/rotation, transparent canvas, straighten, perspective, mesh and resize. New sampling averages premultiplied linear Rec.2020, preserving HDR/signed values; old references interpolate encoded sRGB with Mitchell. Bright edges can therefore differ intentionally. Resize uses exact area reduction and linear enlargement per axis; other transforms use bilinear sampling, without a projective minification/mipmap filter yet. Dark backgrounds show transparency. No document background or editor routing is enabled.",
            Recipes.Select(r => (r.Name, r.Name is "resize" or "perspective" or "mixed")));
}
