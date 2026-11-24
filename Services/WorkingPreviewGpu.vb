Imports System
Imports System.Collections.Generic
Imports SkiaSharp

Namespace Services
    ''' <summary>Native F16 preview stages. Shaders handle premultiplied linear
    ''' Rec.2020, and the worker owns the snapshot even if a GPU job times out.</summary>
    Friend NotInheritable Class WorkingPreviewGpu
        Private Sub New()
        End Sub

        Private Shared ReadOnly AreaEffect As New Lazy(Of SKRuntimeEffect)(
            Function()
                Dim errors As String = Nothing
                Dim effect = SKRuntimeEffect.CreateShader("
uniform shader src;
uniform float2 ratio;
half4 main(float2 p) {
    float2 low = floor(p) * ratio;
    float2 high = low + ratio;
    float2 first = floor(low);
    float4 sum = float4(0);
    // Each tap integrates the overlap of a destination footprint with
    // one source pixel. The caller bounds the footprint to this loop.
    for (int y = 0; y < 8; ++y) {
        float sy = first.y + float(y);
        float wy = min(high.y, sy + 1.0) - max(low.y, sy);
        if (wy > 0.0) {
            for (int x = 0; x < 8; ++x) {
                float sx = first.x + float(x);
                float wx = min(high.x, sx + 1.0) - max(low.x, sx);
                if (wx > 0.0) sum += float4(src.eval(float2(sx + 0.5, sy + 0.5))) * (wx * wy);
            }
        }
    }
    return half4(sum / (ratio.x * ratio.y));
}", errors)
                If effect Is Nothing Then Throw New InvalidOperationException(errors)
                Return effect
            End Function)

        Friend Shared Function TryReduce(source As LinearWorkingFrame, width As Integer, height As Integer) As LinearWorkingFrame
            If Not CanReduce(source.Width, source.Height, width, height) OrElse
               Not GpuImageService.Enabled OrElse Not GpuImageService.IsAvailable Then Return Nothing
            Dim snapshot = source.CreateNativeSnapshot(), domain = source.Domain
            Dim result As LinearWorkingFrame = Nothing
            GpuImageService.TryRun(Function(context) Reduce(context, snapshot, domain, width, height), result, owned:=snapshot)
            Return result
        End Function

        Private Shared ReadOnly DistortionEffect As New Lazy(Of SKRuntimeEffect)(
            Function()
                Dim errors As String = Nothing
                Dim effect = SKRuntimeEffect.CreateShader("
uniform shader src;
uniform float2 center;
uniform float norm;
uniform float invZoom;
uniform float4 polynomial;
uniform float quartic;
half4 main(float2 p) {
    // Match the CPU's integer destination grid. Interpolated fragment
    // coordinates can otherwise shift even the lens center on some GPUs.
    float2 delta = (floor(p) - center) * invZoom;
    float r = length(delta) * norm;
    float factor = 1.0 + polynomial.x + r * (polynomial.y + r * (polynomial.z + r * (polynomial.w + r * quartic)));
    float2 at = clamp(center + delta * factor, float2(0), center * 2.0);
    float2 low = floor(at);
    float2 weight = at - low;
    float2 base = low + 0.5;
    float4 top = mix(float4(src.eval(base)), float4(src.eval(base + float2(1, 0))), weight.x);
    float4 bottom = mix(float4(src.eval(base + float2(0, 1))), float4(src.eval(base + float2(1, 1))), weight.x);
    return half4(mix(top, bottom, weight.y));
}", errors)
                If effect Is Nothing Then Throw New InvalidOperationException(errors)
                Return effect
            End Function)

        Friend Shared Function TryDistort(source As LinearWorkingFrame, correction As LensDataService.Korrektur,
                                          norm As Double, zoom As Double) As LinearWorkingFrame
            Dim coefficients = DistortionCoefficients(correction)
            If coefficients Is Nothing OrElse Not GpuImageService.Enabled OrElse Not GpuImageService.IsAvailable Then Return Nothing
            Dim snapshot = source.CreateNativeSnapshot(), domain = source.Domain
            Dim result As LinearWorkingFrame = Nothing
            GpuImageService.TryRun(Function(context) Distort(context, snapshot, domain, coefficients, norm, zoom), result, owned:=snapshot)
            Return result
        End Function

        Friend Shared Function DistortionCoefficients(correction As LensDataService.Korrektur) As Single()
            Dim coefficients As Double()
            Select Case correction.DistortionModel
                Case "poly3" : coefficients = {-correction.Va, 0, correction.Va, 0, 0}
                Case "poly5" : coefficients = {0, 0, correction.Va, 0, correction.Vb}
                Case "ptlens"
                    coefficients = {-correction.Va - correction.Vb - correction.Vc,
                                    correction.Vc, correction.Vb, correction.Va, 0}
                Case Else
                    ' Measured knot profiles still use the exact CPU interpolator.
                    Return Nothing
            End Select
            Return Array.ConvertAll(coefficients, Function(value) CSng(value * correction.DistortionStrength))
        End Function

        Friend Shared Function Distort(context As GRContext, image As SKImage, domain As WorkingImageDomain,
                                       coefficients As Single(), norm As Double, zoom As Double) As LinearWorkingFrame
            Dim effect = DistortionEffect.Value
            Dim uniforms As New SKRuntimeEffectUniforms(effect) From {
                {"center", New Single() {(image.Width - 1) / 2.0F, (image.Height - 1) / 2.0F}},
                {"norm", CSng(norm)}, {"invZoom", CSng(1 / zoom)},
                {"polynomial", New Single() {coefficients(0), coefficients(1), coefficients(2), coefficients(3)}},
                {"quartic", coefficients(4)}}
            Return Render(context, image.Width, image.Height, domain, effect, uniforms, {("src", image)})
        End Function

        Private Shared Function CanReduce(sourceWidth As Integer, sourceHeight As Integer, width As Integer, height As Integer) As Boolean
            Return width > 0 AndAlso height > 0 AndAlso width <= sourceWidth AndAlso height <= sourceHeight AndAlso
                   sourceWidth <= width * 7L AndAlso sourceHeight <= height * 7L
        End Function

        Friend Shared Function Reduce(context As GRContext, image As SKImage, domain As WorkingImageDomain,
                                      width As Integer, height As Integer) As LinearWorkingFrame
            If Not CanReduce(image.Width, image.Height, width, height) Then Throw New ArgumentOutOfRangeException(NameOf(width))
            Dim effect = AreaEffect.Value
            Dim uniforms = New SKRuntimeEffectUniforms(effect) From {
                {"ratio", New Single() {CSng(image.Width / CDbl(width)), CSng(image.Height / CDbl(height))}}}
            Return Render(context, width, height, domain, effect, uniforms, {("src", image)})
        End Function

        Friend Shared Function Render(context As GRContext, width As Integer, height As Integer, domain As WorkingImageDomain,
                                      effect As SKRuntimeEffect, uniforms As SKRuntimeEffectUniforms,
                                      inputs As IEnumerable(Of (Name As String, Image As SKImage))) As LinearWorkingFrame
            Dim info As New SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul)
            Using target = SKSurface.Create(context, False, info)
                If target Is Nothing Then Return Nothing
                Dim children = New SKRuntimeEffectChildren(effect)
                Dim shaders As New List(Of SKShader)
                Try
                    For Each child In inputs
                        Dim shader = child.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, New SKSamplingOptions(SKFilterMode.Nearest))
                        shaders.Add(shader)
                        children(child.Name) = shader
                    Next
                    Using shader = effect.ToShader(uniforms, children)
                        If shader Is Nothing Then Return Nothing
                        Using paint As New SKPaint With {.Shader = shader, .BlendMode = SKBlendMode.Src}
                            target.Canvas.DrawPaint(paint)
                        End Using
                    End Using
                    ' F32 readback plus explicit half packing avoids CPU Skia's
                    ' measured direct-F16 readback loss in subnormal shadows.
                    Using readback As New SKBitmap(New SKImageInfo(width, height, SKColorType.RgbaF32, SKAlphaType.Premul))
                        If readback.GetPixels() = IntPtr.Zero OrElse Not target.ReadPixels(readback.Info, readback.GetPixels(), readback.RowBytes, 0, 0) Then Return Nothing
                        Return LinearWorkingFrame.FromLinearBitmap(readback, domain)
                    End Using
                Finally
                    For Each shader In shaders
                        shader.Dispose()
                    Next
                End Try
            End Using
        End Function
    End Class
End Namespace
