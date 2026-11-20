using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;

internal static class SpatialLegacyFixture
{
    internal static Dictionary<string, string> Hashes()
    {
        var method = typeof(ImageProcessor).GetMethod("BlurPlaneLikeSkia", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = new Dictionary<string, string>();
        foreach (var (w, h) in new[] { (3, 1), (8, 5), (37, 23) })
        foreach (var sigma in new[] { .2f, .5f, 1f, 2f, 7f, 20f })
        foreach (var scale in new[] { 1f, 255f })
        {
            var values = Enumerable.Range(0, w * h).Select(i => (i * 31 % 997) / 997f * scale).ToArray();
            object[] arguments = method.GetParameters().Length == 4 ? [values, w, h, sigma] : [values, w, h, sigma, false];
            var output = (float[])method.Invoke(null, arguments)!;
            result.Add(FormattableString.Invariant($"{w}x{h}-{sigma}-{scale}"), Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(output.AsSpan()))));
        }
        return result;
    }
    internal static void Capture(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Hashes(), new JsonSerializerOptions { WriteIndented = true }));
}
