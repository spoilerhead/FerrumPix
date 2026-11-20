Imports System
Imports SkiaSharp

Namespace Services
    ''' <summary>Actual F16 GPU kernels for normal source-over and unmasked result
    ''' mixing. Invoke on the existing GPU worker while both input frames remain
    ''' alive. F32 coverage masks deliberately stay on WorkingComposite's CPU path;
    ''' uploading them through an unsupported F32 texture could reduce them to RGB8.</summary>
    Friend NotInheritable Class WorkingCompositeGpu
        Private Sub New()
        End Sub

        Private Shared ReadOnly BlendEffect As New Lazy(Of SKRuntimeEffect)(
            Function()
                Dim errors As String = Nothing
                Dim effect = SKRuntimeEffect.CreateShader("
uniform shader upper;
uniform shader lower;
uniform float opacity;
uniform float overMode;
half4 main(float2 p) {
    float4 top = float4(upper.eval(p));
    float4 bottom = float4(lower.eval(p));
    float retained = mix(1.0 - opacity, 1.0 - top.a * opacity, overMode);
    return half4(top * opacity + bottom * retained);
}", errors)
                If effect Is Nothing Then Throw New InvalidOperationException(errors)
                Return effect
            End Function)

        Friend Shared Function Render(context As GRContext, foreground As LinearWorkingFrame,
                                      background As LinearWorkingFrame, opacity As Single,
                                      sourceOver As Boolean) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(context)
            WorkingComposite.Validate(background, foreground, opacity, Nothing)
            ' A native F16 snapshot avoids the measured F32 GPU upload fallback.
            ' Untagged snapshots already contain linear Rec.2020 working values.
            Using top = foreground.CreateNativeSnapshot(), bottom = background.CreateNativeSnapshot(),
                  topShader = top.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, New SKSamplingOptions(SKFilterMode.Nearest)),
                  bottomShader = bottom.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, New SKSamplingOptions(SKFilterMode.Nearest))
                Dim effect = BlendEffect.Value
                Dim uniforms = New SKRuntimeEffectUniforms(effect) From {
                    {"opacity", opacity}, {"overMode", If(sourceOver, 1.0F, 0.0F)}}
                Dim children = New SKRuntimeEffectChildren(effect) From {{"upper", topShader}, {"lower", bottomShader}}
                Dim info = New SKImageInfo(foreground.Width, foreground.Height, SKColorType.RgbaF16, SKAlphaType.Premul)
                Using shader = effect.ToShader(uniforms, children),
                      target = SKSurface.Create(context, False, info)
                    If target Is Nothing Then Throw New NotSupportedException("GPU F16 compositing target unavailable.")
                    Using paint As New SKPaint With {.Shader = shader, .BlendMode = SKBlendMode.Src}
                        target.Canvas.DrawPaint(paint)
                    End Using
                    ' Read back through F32, then explicit half packing. Direct CPU
                    ' F16 conversion is not the fallback: it loses subnormal shadows.
                    Using readback As New SKBitmap(New SKImageInfo(info.Width, info.Height, SKColorType.RgbaF32, SKAlphaType.Premul))
                        If Not target.ReadPixels(readback.Info, readback.GetPixels(), readback.RowBytes, 0, 0) Then
                            Throw New InvalidOperationException("GPU compositing readback failed.")
                        End If
                        Return LinearWorkingFrame.FromLinearBitmap(readback, foreground.Domain)
                    End Using
                End Using
            End Using
        End Function
    End Class
End Namespace
