using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

internal static class FilmMeasurementChecks
{
    internal static void Run(ProbeReport report)
    {
        Check(report, "linear block means and fractional percentiles", Reference);
        Check(report, "holder, subject and transparent boundaries", Edges);
        Check(report, "calibration snapshot, logarithms and invalid inputs", Contracts);
    }

    private static void Check(ProbeReport report, string name, Func<string> run)
    {
        try { report.Checks.Add(new ProbeCheck("Film measurement / " + name, "PASS", run())); }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Film measurement / " + name, ex.ToString())); }
    }

    private static double Encode(double v) => v <= .0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - .055;
    private static double Rank(List<double> values, double p)
    {
        values.Sort();
        return values[Math.Clamp((int)Math.Round(values.Count * p), 1, values.Count) - 1];
    }

    private static string Reference()
    {
        // Odd dimensions force partial right/bottom blocks and a two-row ring.
        // Vary alpha and sample colors independently; keep line means comparable
        // so this test isolates averaging/percentiles from the holder heuristic.
        const int w = 1003, h = 1001;
        using var source = LinearWorkingFrame.Create(w, h, WorkingImageDomain.RenderedLinear, (y, row) =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = x * 4; var a = (x + y) % 17 == 0 ? 0f : (x + y) % 3 == 0 ? .5f : 1f;
                row[i] = (.12f + (x * 31 + y * 7) % 100 * .004f) * a;
                row[i + 1] = (.15f + (x * 3 + y * 19) % 100 * .003f) * a;
                row[i + 2] = (.1f + (x * 13 + y * 5) % 100 * .005f) * a;
                row[i + 3] = a;
            }
        });
        var measured = FloatingFilmMeasurement.Analyze(source);
        if (measured.BlockSize != 2 || measured.MeasurementArea != new SKRectI(0, 0, w, h))
            throw new InvalidOperationException("Unexpected sampling grid or holder exclusion.");
        List<double>[] channels = [[], [], []];
        var row0 = new float[w * 4]; var row1 = new float[w * 4];
        for (var y = 0; y < h; y += 2)
        {
            source.ReadLinearRow(y, row0);
            if (y + 1 < h) source.ReadLinearRow(y + 1, row1);
            for (var x = 0; x < w; x += 2)
            {
                double[] sum = [0, 0, 0]; var n = 0;
                for (var dy = 0; dy < Math.Min(2, h - y); dy++)
                for (var dx = 0; dx < Math.Min(2, w - x); dx++)
                {
                    var row = dy == 0 ? row0 : row1; var at = (x + dx) * 4; double a = row[at + 3];
                    if (a < 8.0 / 255) continue;
                    double r = row[at] / a, g = row[at + 1] / a, b = row[at + 2] / a;
                    sum[0] += Math.Clamp(1.660491002 * r - .587641139 * g - .072849863 * b, 0, 1);
                    sum[1] += Math.Clamp(-.124550475 * r + 1.132899897 * g - .008349422 * b, 0, 1);
                    sum[2] += Math.Clamp(-.018150763 * r - .100578898 * g + 1.118729661 * b, 0, 1);
                    n++;
                }
                if (n > 0) for (var c = 0; c < 3; c++) channels[c].Add(Encode(sum[c] / n));
            }
        }
        var bases = measured.CopyBase(); var densities = measured.CopyDensity();
        for (var c = 0; c < 3; c++)
            if (Math.Abs(bases[c] - Rank(channels[c], .995)) > 1e-12 || Math.Abs(densities[c] - Rank(channels[c], .005)) > 1e-12)
                throw new InvalidOperationException("Independent linear-light block mean/percentile differs.");
        if (measured.SampleCount != channels[0].Count || bases.All(v => Math.Abs(v * 255 - Math.Round(v * 255)) < .00001))
            throw new InvalidOperationException("Measurement count or fractional precision lost.");
        return "Independent Double two-dimensional block integration and sorted nearest-rank 0.5/99.5 percentiles match; odd 1003x1001 edges, alpha and fractional encoded calibration covered.";
    }

    private static LinearWorkingFrame EdgeSource(int kind) => LinearWorkingFrame.Create(100, 100, WorkingImageDomain.RenderedLinear, (y, row) =>
    {
        for (var x = 0; x < 100; x++)
        {
            var edge = x < 8 || x >= 92 || y < 8 || y >= 92;
            float v = kind switch { 0 when edge => .001f, 1 when x < 25 => .001f, 2 when edge => 1f, _ => .2f + (x + y) % 10 * .01f };
            var a = kind == 3 && edge ? .02f : 1f;
            row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = v * a; row[x * 4 + 3] = a;
        }
    });

    private static string Edges()
    {
        for (var kind = 0; kind < 4; kind++)
        {
            using var source = EdgeSource(kind); var m = FloatingFilmMeasurement.Analyze(source);
            var expected = kind == 0 ? new SKRectI(10, 10, 90, 90) : new SKRectI(0, 0, 100, 100);
            if (m.MeasurementArea != expected) throw new InvalidOperationException($"Holder/subject/border case {kind}: {m.MeasurementArea}.");
            if (kind == 3 && m.SampleCount != 84 * 84) throw new InvalidOperationException("Low-alpha border participated in statistics.");
            if (kind == 2 && Math.Abs(m.CopyBase()[0] - 1) > 1e-6) throw new InvalidOperationException("Unexposed bright base was excluded.");
            if (source.Width != 100 || source.Height != 100) throw new InvalidOperationException("Measurement changed image geometry.");
        }
        return "Opaque holder excluded with seam; dark subject reaching interior and bright unexposed film retained; transparent edge ignored without becoming a holder. Geometry is unchanged.";
    }

    private static string Contracts()
    {
        // Span below one RGB8 code must survive calibration and inversion.
        using var narrow = LinearWorkingFrame.Create(1024, 1, WorkingImageDomain.RenderedLinear, (_, row) =>
        {
            for (var x = 0; x < 1024; x++)
            {
                var v = (float)RenderedContrastChecks.Decode(.5 + x / 1023.0 * .002);
                row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = v; row[x * 4 + 3] = 1;
            }
        });
        var m = FloatingFilmMeasurement.Analyze(narrow); var bases = m.CopyBase(); var densities = m.CopyDensity();
        if (bases[0] - densities[0] >= 1 / 255.0 || bases[0] <= densities[0]) throw new InvalidOperationException("Narrow calibration fixture invalid.");
        foreach (var gamma in new[] { -100f, 0, 100 })
        {
            var settings = RenderedNegativeSettings.FromMeasurement(m, gamma);
            var tables = ImageProcessor.ExplicitNegativeTables(settings);
            for (var c = 0; c < 3; c++)
            for (var i = 0; i <= 4096; i++)
            {
                var t = Math.Clamp(Math.Log(bases[c] / Math.Max(1.0 / 65535, i / 4096.0)) /
                                   Math.Log(bases[c] / Math.Max(1.0 / 65535, densities[c])), 0, 1);
                var expected = Math.Pow(t, 1 / (float)Math.Pow(2, gamma / 100.0));
                if (Math.Abs(tables[c][i] - expected) > 1e-7) throw new InvalidOperationException("Measured log-density reference differs.");
            }
            using var result = RenderedInputControls.Apply(narrow, negative: settings);
            var output = new float[4096]; result.ReadLinearRow(0, output);
            if (output[0] < .9 || output[^4] > .1) throw new InvalidOperationException("Fractional density span lost in actual inversion.");
            var copy = settings.CopyMeasuredBase(); copy[0] = 0;
            if (settings.CopyMeasuredBase()[0] != bases[0]) throw new InvalidOperationException("Settings calibration is mutable.");
        }
        bases[0] = 0; if (m.CopyBase()[0] == 0) throw new InvalidOperationException("Measurement is mutable.");
        using var empty = LinearWorkingFrame.FromLinearPixels(1, 1, [0, 0, 0, 0], WorkingImageDomain.RenderedLinear);
        try { FloatingFilmMeasurement.Analyze(empty); throw new Exception("Empty film accepted."); } catch (InvalidOperationException) { }
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.SceneLinear);
        try { FloatingFilmMeasurement.Analyze(scene); throw new Exception("Scene film accepted."); } catch (ArgumentException) { }
        using var flat = LinearWorkingFrame.FromLinearPixels(1, 1, [.2f, .2f, .2f, 1], WorkingImageDomain.RenderedLinear);
        try { RenderedNegativeSettings.FromMeasurement(FloatingFilmMeasurement.Analyze(flat)); throw new Exception("Zero span accepted."); } catch (ArgumentException) { }
        return "Fractional sub-byte density span reaches inversion unchanged; independent 4097-node log/gamma references, cloned snapshots, empty/scene rejection and explicit zero-span failure pass.";
    }

    internal static void Capture(ProbeReport report, string output, LinearWorkingFrame source, SKBitmap legacySource, string name)
    {
        var measured = FloatingFilmMeasurement.Analyze(source);
        var old = ImageProcessor.AnalyzeFilmNegative(legacySource, new ImageAdjustments());
        foreach (var mono in new[] { false, true })
        {
            using var positive = RenderedInputControls.Apply(source, negative: RenderedNegativeSettings.FromMeasurement(measured, monochrome: mono));
            SceneRenderingChecks.SavePreview(positive, output, $"{name}-input-auto{(mono ? "-mono" : "")}.png");
        }
        var a = new ImageAdjustments { NegativeEnabled = true, NegativeBaseColor = old.BaseColor.ToString(), NegativeDensityColor = old.DensityColor.ToString() };
        var bitmap = ImageProcessor.RenderPreviewSkBitmap(legacySource, a)!;
        using var owned = ReferenceEquals(bitmap, legacySource) ? bitmap.Copy() : bitmap;
        using var frame = WorkingImageBoundary.FromEncodedSrgb(owned);
        SceneRenderingChecks.SavePreview(frame, output, $"{name}-input-auto-legacy.png");
        report.Checks.Add(new ProbeCheck($"Automatic film / {name}", "PASS",
            $"Area {measured.MeasurementArea}; block {measured.BlockSize}; samples {measured.SampleCount}; encoded base [{string.Join(", ", measured.CopyBase().Select(v => v.ToString("F7")))}]; density [{string.Join(", ", measured.CopyDensity().Select(v => v.ToString("F7")))}]. Old byte calibration {old.BaseColor}/{old.DensityColor}."));
    }
}
