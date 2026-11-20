using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;

// Exercise the authored shaders with DIRECT linear working values. The existing
// editor extension wrappers still belong to the legacy path; this is deliberately
// not a claim that their routing/storage has already been migrated.
internal static class ExtensionLinearChecks
{
    internal static void Run(ProbeReport report)
    {
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "ExtensionFixtures");
            var files = Directory.GetFiles(folder, "*.json");
            foreach (var path in files)
            {
                var loaded = ExtensionService.LoadFile(path);
                if (!loaded.Succeeded) throw new InvalidOperationException(Path.GetFileName(path) + ": " + loaded.ErrorText);
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                var body = string.Join("\n", json.RootElement.GetProperty("shader").EnumerateArray().Select(x => x.GetString()));
                if (body.Contains("fpToLinear(") || body.Contains("fpToSrgb(")) throw new InvalidOperationException("Redundant sRGB transfer in " + path);
            }
            float[] sample = [2, -.25f, .18f];
            Verify("example.light-falloff", sample, [1, 1], new() { ["amount"] = -100 }, sample.Select(x => x * .5f).ToArray());
            Verify("spoilerhead.vignette", sample, [1, 1], new() { ["strengthEv"] = 10, ["highlightThreshEv"] = 0 }, sample.Select(x => x / 1024).ToArray());
            Verify("spoilerhead.vignette", sample, [1, 1], new() { ["strengthEv"] = 0 }, sample);
            Verify("spoilerhead.gradfilter", sample, [.5f, 0], new() { ["strengthEv"] = 2, ["hardness"] = 100, ["highlightThreshEv"] = 0 }, sample.Select(x => x / 4).ToArray());
            Verify("spoilerhead.gradfilter", sample, [.5f, 1], new() { ["strengthEv"] = 2, ["hardness"] = 100 }, sample);
            Verify("spoilerhead.gradfilter", [.2f, .4f, .8f], [.5f, 0], new() { ["strengthEv"] = 0, ["hardness"] = 100, ["tintHue"] = 210, ["tintSaturation"] = 100, ["highlightThreshEv"] = 0 }, [0, .2f, .8f]);
            Verify("spoilerhead.silicon-bonk", [.18f, .18f, .18f], [.5f, .5f], new(), [.18f, .18f, .18f]);
            Verify("spoilerhead.fat-toni", [.18f, .25f, .8f], [.5f, .5f], new(), [.18f, .25f, .8f]);
            // Classify a colored highlight using native Rec.2020 Y, not the
            // legacy helper's sRGB coefficients. Zero threshold explicitly bypasses.
            float[] highlight = [.2f, .9f, .1f];
            var y = .2627 * highlight[0] + .678 * highlight[1] + .0593 * highlight[2];
            var t = Math.Clamp((y - .5) / .5, 0, 1); var mask = t * t * (3 - 2 * t);
            var multiplier = .25 + .75 * mask;
            foreach (var id in new[] { "spoilerhead.vignette", "spoilerhead.gradfilter" })
                Verify(id, highlight, id.EndsWith("vignette") ? [1, 1] : [.5f, 0],
                    new() { ["strengthEv"] = 2, ["hardness"] = 100, ["highlightThreshEv"] = -1, ["preservationRolloffEv"] = 1, ["preserveAmount"] = 1 },
                    highlight.Select(x => (float)(x * multiplier)).ToArray());
            report.Checks.Add(new ProbeCheck("Extensions / direct linear shader contract", "PASS", $"{files.Length} JSON files load/compile; actual authored shaders pass exposure, signed/HDR transport, neutral SB/FT gamma-2.2 pairs, native-channel tint and Rec.2020 highlight-mask references. This does not enable native editor extension routing."));
        }
        catch (Exception ex) { report.Checks.Add(ProbeCheck.Failure("Extensions / direct linear shader contract", ex.ToString())); }
    }

    private static void Verify(string id, float[] input, float[] position, Dictionary<string, float> overrides, float[] expected)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ExtensionFixtures", id + ".json")));
        var root = json.RootElement;
        var head = new StringBuilder("uniform float3 inputColor; uniform float2 inputPosition; uniform float2 fpSize;\n");
        var controls = new Dictionary<string, float>();
        foreach (var control in root.GetProperty("controls").EnumerateArray())
        {
            var key = control.GetProperty("key").GetString()!;
            var kind = control.GetProperty("type").GetString();
            if (kind is "color" or "curve") throw new NotSupportedException("Numerical fixture expects scalar controls.");
            head.Append("uniform float ").Append(key).Append(";\n");
            var value = control.GetProperty("default");
            controls[key] = overrides.GetValueOrDefault(key, value.ValueKind is JsonValueKind.True or JsonValueKind.False ? (value.GetBoolean() ? 1 : 0) : value.GetSingle());
        }
        // Reuse the real host helper definitions rather than reproducing the
        // authored algorithms. No automatic transfer occurs in this test wrapper.
        head.Append(typeof(ImageProcessor).GetField("ExtensionShaderHelpers", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue());
        var body = string.Join("\n", root.GetProperty("shader").EnumerateArray().Select(x => x.GetString()));
        head.Append(body).Append(body.Contains("float2 pos") ? "\nhalf4 main(float2 p) { return half4(effect(inputColor, inputPosition), 1); }" : "\nhalf4 main(float2 p) { return half4(effect(inputColor), 1); }");
        using var effect = SKRuntimeEffect.CreateShader(head.ToString(), out var error) ?? throw new InvalidOperationException(error);
        var uniforms = new SKRuntimeEffectUniforms(effect) { { "inputColor", input }, { "inputPosition", position }, { "fpSize", new float[] { 1, 1 } } };
        foreach (var (key, value) in controls) uniforms.Add(key, value);
        using var shader = effect.ToShader(uniforms);
        using var target = new SKBitmap(new SKImageInfo(1, 1, SKColorType.RgbaF32, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        using (var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src }) canvas.DrawPaint(paint);
        var actual = new float[4]; Marshal.Copy(target.GetPixels(), actual, 0, 4);
        for (var c = 0; c < 3; c++)
            if (!float.IsFinite(actual[c]) || Math.Abs(actual[c] - expected[c]) > Math.Max(2e-6, Math.Abs(expected[c]) * .002))
                throw new InvalidOperationException($"{id}/{c}: expected {expected[c].ToString("G9", CultureInfo.InvariantCulture)}, got {actual[c].ToString("G9", CultureInfo.InvariantCulture)}.");
    }
}
