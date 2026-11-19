using System.Diagnostics;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;

internal static class RawSceneChecks
{
    public static void Run(ProbeReport report)
    {
        Check(report, "RGB16 import reference", ImportReference);
        Check(report, "linear exposure and domain", ExposureReference);
        Check(report, "invalid source guards", Guards);
    }

    private static void Check(ProbeReport report, string name, Func<string> test)
    {
        var clock = Stopwatch.StartNew();
        try { report.Checks.Add(new ProbeCheck("RAW scene / " + name, "PASS", test(), clock.Elapsed.TotalMilliseconds)); }
        catch (Exception ex) { report.Checks.Add(new ProbeCheck("RAW scene / " + name, "FAIL", ex.ToString(), clock.Elapsed.TotalMilliseconds)); }
    }

    private static byte[] Bytes(ushort[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string ImportReference()
    {
        ushort[] samples = [65535, 0, 0, 0, 65535, 0, 0, 0, 65535, 1, 1, 1, 32768, 32768, 32768, 0, 0, 0];
        float[] camera = [1.4f, -.2f, -.2f, -.1f, 1.2f, -.1f, -.1f, -.2f, 1.3f];
        double[] toRec = [.627403896, .329283038, .043313066, .069097289, .919540395, .011362316, .016391439, .088013308, .895595253];
        using var frame = RawDecodeService.BuildSceneFrame(3, 2, Bytes(samples), camera, 4);
        var row = new float[12];
        var negative = false; var hdr = false;
        for (var y = 0; y < 2; y++)
        {
            frame.ReadLinearRow(y, row);
            for (var x = 0; x < 3; x++)
            {
                // Independent double-precision two-stage matrix, rather than the combined
                // F32 matrix used by the importer. Include a one-code-value RAW shadow.
                var srgb = new double[3];
                for (var r = 0; r < 3; r++)
                    for (var c = 0; c < 3; c++) srgb[r] += camera[r * 3 + c] * (samples[(y * 3 + x) * 3 + c] * 4.0 / 65535);
                for (var r = 0; r < 3; r++)
                {
                    var expected = Enumerable.Range(0, 3).Sum(c => toRec[r * 3 + c] * srgb[c]);
                    var actual = row[x * 4 + r];
                    if (Math.Abs(actual - expected) > Math.Max(.00000012, Math.Abs(expected) * .002))
                        throw new InvalidOperationException($"RGB16 reference mismatch: {actual} != {expected}.");
                    negative |= actual < 0; hdr |= actual > 1;
                }
                if (row[x * 4 + 3] != 1) throw new InvalidOperationException("RAW alpha is not opaque.");
            }
        }
        frame.ReadLinearRow(1, row);
        if (!negative || !hdr || row[0] <= 0 || frame.Domain != WorkingImageDomain.SceneLinear)
            throw new InvalidOperationException("RAW import clipped values, lost the shadow, or changed domain.");
        return "RGB16 -> camera matrix -> normalized Rec.2020 agrees with double reference across two rows; negative/HDR/shadow values retained before any tone curve.";
    }

    private static string ExposureReference()
    {
        using var source = LinearWorkingFrame.FromLinearPixels(2, 1, [-.25f, .000001f, 4, 1, 16, .18f, 0, 1], WorkingImageDomain.SceneLinear);
        foreach (var ev in new[] { -2.0, 0.0, 2.0 })
        {
            using var changed = SceneFrameOperations.ApplyExposure(source, ev);
            ValidateExposure(source, changed, ev);
        }
        var row = new float[8]; source.ReadLinearRow(0, row);
        if (row[0] != -.25f) throw new InvalidOperationException("Exposure modified its source.");
        return "-2/0/+2 EV matches explicitly rounded F32 multiplication; alpha, domain and source ownership unchanged.";
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static string Guards()
    {
        float[] identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        Reject<ArgumentException>(() => RawDecodeService.BuildSceneFrame(1, 1, [], identity, 1));
        Reject<ArgumentException>(() => RawDecodeService.BuildSceneFrame(1, 1, new byte[6], [1, 0], 1));
        Reject<ArgumentOutOfRangeException>(() => RawDecodeService.BuildSceneFrame(1, 1, new byte[6], identity, double.NaN));
        using var rendered = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.RenderedLinear);
        Reject<ArgumentException>(() => SceneFrameOperations.ApplyExposure(rendered, 1));
        using var scene = LinearWorkingFrame.FromLinearPixels(1, 1, [1, 1, 1, 1], WorkingImageDomain.SceneLinear);
        Reject<ArgumentOutOfRangeException>(() => SceneFrameOperations.ApplyExposure(scene, double.PositiveInfinity));
        Reject<ArgumentOutOfRangeException>(() => SceneFrameOperations.ApplyExposure(scene, 11));
        using var bright = LinearWorkingFrame.FromLinearPixels(1, 1, [32768, 1, 1, 1], WorkingImageDomain.SceneLinear);
        Reject<ArgumentOutOfRangeException>(() => SceneFrameOperations.ApplyExposure(bright, 2));
        return "Rejects malformed cache data, invalid normalization/matrices, rendered-domain exposure and F16 overflow rather than clipping.";
    }

    private static void ValidateExposure(LinearWorkingFrame source, LinearWorkingFrame result, double ev)
    {
        if (source.Width != result.Width || source.Height != result.Height || result.Domain != WorkingImageDomain.SceneLinear)
            throw new InvalidOperationException("Exposure changed frame geometry or domain.");
        var before = new float[source.Width * 4];
        var after = new float[before.Length];
        var gain = (float)Math.Pow(2, ev);
        for (var y = 0; y < source.Height; y++)
        {
            source.ReadLinearRow(y, before); result.ReadLinearRow(y, after);
            for (var i = 0; i < before.Length; i++)
            {
                var expected = i % 4 == 3 ? before[i] : (float)(Half)(before[i] * gain);
                if (expected != after[i]) throw new InvalidOperationException($"Scene exposure differs from F32/half reference at row {y}, sample {i}.");
            }
        }
    }

    internal static void Capture(ProbeReport report, string path, string name, string output, bool renderScene, bool compareRepair, bool compareExposure, bool compareLens, bool compareTca, bool compareWhiteBalance)
    {
        if (!RawPreviewService.SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            report.Checks.Add(new ProbeCheck($"RAW scene input / {name}", "SKIP", "Rendered-file input; not relabeled as scene-linear.", Required: false));
            return;
        }
        var clock = Stopwatch.StartNew();
        try
        {
            using var source = RawDecodeService.ReadSceneSource(path);
            if (!source.LibRawWhiteBalanceAlreadyApplied || source.LensCorrectionsApplied || source.GeometryApplied || source.ToneCurveApplied || source.ModelDenoiseApplied)
                throw new InvalidOperationException("RAW scene source stage metadata is incorrect.");
            var frame = source.Frame;
            var row = new float[frame.Width * 4];
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            long negatives = 0, aboveWhite = 0;
            for (var y = 0; y < frame.Height; y++)
            {
                frame.ReadLinearRow(y, row);
                for (var i = 0; i < row.Length; i++)
                {
                    if (i % 4 == 3) { if (row[i] != 1) throw new InvalidOperationException("RAW alpha changed."); continue; }
                    if (!float.IsFinite(row[i])) throw new InvalidOperationException("Non-finite RAW scene value.");
                    minimum = Math.Min(minimum, row[i]); maximum = Math.Max(maximum, row[i]);
                    if (row[i] < 0) negatives++; if (row[i] > 1) aboveWhite++;
                }
            }
            foreach (var ev in new[] { -2.0, 2.0 })
            {
                using var changed = SceneFrameOperations.ApplyExposure(frame, ev);
                ValidateExposure(frame, changed, ev);
                if (renderScene) SceneRenderingChecks.Capture(report, output, changed, source.BaseExposureEv, name, ev < 0 ? "minus2ev" : "plus2ev");
            }
            if (renderScene) SceneRenderingChecks.Capture(report, output, frame, source.BaseExposureEv, name, "neutral");
            if (compareRepair) HighlightRepairChecks.Capture(report, output, source, name);
            if (compareExposure) RawExposureChecks.Capture(report, output, source, name);
            if (compareLens) LensVignetteChecks.Capture(report, output, source, path, name);
            if (compareTca) LensTcaChecks.Capture(report, output, path, name);
            if (compareWhiteBalance) RawWhiteBalanceChecks.Capture(report, output, path, name);
            report.Checks.Add(new ProbeCheck($"RAW scene input / {name}", "PASS",
                $"Full-resolution {frame.Width}x{frame.Height}; linear Rec.2020 F16 scene frame {frame.StorageBytes / 1_000_000.0:F2} MB; normalization {source.Normalization:G6}; reference base exposure {source.BaseExposureEv:G6} EV (not applied); range [{minimum:G6},{maximum:G6}]; negative channels {negatives}; above-white channels {aboveWhite}. +/-2 EV verified for every pixel. LibRaw WB already applied; lens/geometry/tone/model-denoise not applied. Not an SDR preview.", clock.Elapsed.TotalMilliseconds));
        }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure($"RAW scene input / {name}", ex.ToString())); }
    }
}
