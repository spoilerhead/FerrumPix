using System.Runtime.InteropServices;
using SkiaSharp;
using FerrumPix.Services;

namespace ImagingPrecisionProbe;

/// <summary>
/// Prototype storage adapter, deliberately outside the production renderer. Skia's raster F16
/// shader path can flush subnormal values; staging through F32 avoids that path while explicit
/// IEEE half conversion retains the F16 values that can be represented, including subnormals.
/// </summary>
internal static class CpuHalfStorage
{
    // Exercise the application's storage conversion rather than retaining a second implementation.
    public static short[] Pack(float[] values) => HalfPixelStorage.Pack(values);
    public static float[] Unpack(short[] bits) => HalfPixelStorage.Unpack(bits);

    public static short[] Render(short[] input, int width, int height, SKRuntimeEffect effect,
        SKSamplingOptions sampling, SKBlendMode blend = SKBlendMode.Src, short[]? background = null,
        SKRuntimeEffectUniforms? uniforms = null)
    {
        var count = checked(width * height * 4);
        if (width <= 0 || height <= 0 || input.Length != count || (background is not null && background.Length != count))
            throw new ArgumentException("Input dimensions and RGBA storage must agree.");

        // This whole-image staging is a correctness experiment, not the final memory design.
        // A production implementation needs bounded scratch buffers and neighborhood-aware tiles.
        var info = new SKImageInfo(width, height, SKColorType.RgbaF32, SKAlphaType.Premul);
        using var source = new SKBitmap(info);
        var values = Unpack(input);
        Marshal.Copy(values, 0, source.GetPixels(), count);
        using var image = SKImage.FromBitmap(source);
        using var target = SKSurface.Create(info) ?? throw new InvalidOperationException("Cannot create CPU F32 target.");
        if (background is not null)
        {
            using var backing = new SKBitmap(info);
            var data = Unpack(background);
            Marshal.Copy(data, 0, backing.GetPixels(), count);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            target.Canvas.DrawBitmap(backing, 0, 0, paint);
        }
        var children = new SKRuntimeEffectChildren(effect);
        using var child = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        children["src"] = child;
        using var shader = effect.ToShader(uniforms ?? new SKRuntimeEffectUniforms(effect), children);
        using var draw = new SKPaint { Shader = shader, BlendMode = blend };
        target.Canvas.DrawPaint(draw);
        using var result = new SKBitmap(info);
        if (!target.ReadPixels(info, result.GetPixels(), result.RowBytes, 0, 0))
            throw new InvalidOperationException("Cannot read CPU F32 result.");
        Marshal.Copy(result.GetPixels(), values, 0, count);
        return Pack(values);
    }
}
