using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;
internal static class StageLegacyFixture
{
    internal static Dictionary<string, string> Hashes(IEnumerable<(string Name, ImageAdjustments Adjustments)> recipes)
    {
        GpuImageService.Enabled = false;
        using var source = FullBasicChecks.Synthetic();
        var result = new Dictionary<string, string>();
        foreach (var (name, adjustments) in recipes)
        {
            var rendered = ImageProcessor.RenderPreviewSkBitmap(source, adjustments) ?? throw new InvalidOperationException("Legacy stage render failed.");
            using var owned = ReferenceEquals(rendered, source) ? rendered.Copy() : rendered;
            var bytes = new byte[owned.ByteCount]; Marshal.Copy(owned.GetPixels(), bytes, 0, bytes.Length);
            result.Add(name, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        return result;
    }
    internal static void Capture(string path, IEnumerable<(string Name, ImageAdjustments Adjustments)> recipes) =>
        File.WriteAllText(path, JsonSerializer.Serialize(Hashes(recipes), new JsonSerializerOptions { WriteIndented = true }));
}
