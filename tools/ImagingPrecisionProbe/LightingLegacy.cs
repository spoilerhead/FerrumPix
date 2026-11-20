using System.Runtime.InteropServices;
using FerrumPix.Services;
using SkiaSharp;

namespace ImagingPrecisionProbe;
internal static class LightingLegacy
{
    internal const string Token = "working-light-fixture";
    internal static IEnumerable<(string, ImageAdjustments)> Recipes()
    {
        yield return ("ambient", new() { LightAmbient = -.5 });
        foreach (var kind in LightSourceKinds.All)
            yield return (kind, new() { LightSources = [new() { Id = "fixed-light", Kind = kind, Intensity = 1.2, RadiusPercent = 70, TemperatureKelvin = 4200, Glow = 15 }] });
        yield return ("shadow", new() { LightSources = [new() { Id = "fixed-light", Intensity = -1.2, RadiusPercent = 70, UseTemperature = false, Color = "#FF88AADD" }] });
        yield return ("depth", new() { LightDepthToken = Token, LightSources = [new() { Id = "fixed-light", Intensity = 1.2, RadiusPercent = 70, UseDepth = true, Shading = 60, CastShadows = true }] });
    }
    internal static Dictionary<string, string> Hashes()
    {
        using var depth = new SKBitmap(new SKImageInfo(32, 24, SKColorType.Alpha8, SKAlphaType.Premul));
        var bytes = Enumerable.Range(0, 768).Select(i => (byte)((i % 32) * 7 + i / 32)).ToArray();
        Marshal.Copy(bytes, 0, depth.GetPixels(), bytes.Length);
        ImageProcessor.SetLightDepthMap(Token, depth);
        try { return StageLegacyFixture.Hashes(Recipes()); }
        finally { ImageProcessor.SetLightDepthMap("", null!); }
    }
}
