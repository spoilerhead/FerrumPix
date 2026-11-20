Imports System
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services
    Partial Public Class ImageProcessor
        ''' <summary>Coverage-weighted rectangular mean. Double rolling sums avoid
        ''' HDR-to-shadow cancellation; missing/transparent neighbors have no vote.</summary>
        Friend Shared Function WorkingMean(values As Single(), p As WorkingEffectPlanes, radius As Integer) As Single()
            Dim n = values.Length, w = p.Width, h = p.Height
            Dim numerator(n - 1), denominator(n - 1), result(n - 1) As Single
            Dim mean = Function(input As Single()) As Single()
                           Dim horizontal(n - 1), output(n - 1) As Single
                           For y = 0 To h - 1
                               Dim sum = 0.0, count = 0
                               For x = 0 To Math.Min(radius, w - 1)
                                   sum += input(y * w + x) : count += 1
                               Next
                               For x = 0 To w - 1
                                   horizontal(y * w + x) = CSng(sum / count)
                                   If x + radius + 1 < w Then sum += CDbl(input(y * w + x + radius + 1)) : count += 1
                                   If x - radius >= 0 Then sum -= CDbl(input(y * w + x - radius)) : count -= 1
                               Next
                           Next
                           For x = 0 To w - 1
                               Dim sum = 0.0, count = 0
                               For y = 0 To Math.Min(radius, h - 1)
                                   sum += horizontal(y * w + x) : count += 1
                               Next
                               For y = 0 To h - 1
                                   output(y * w + x) = CSng(sum / count)
                                   If y + radius + 1 < h Then sum += CDbl(horizontal((y + radius + 1) * w + x)) : count += 1
                                   If y - radius >= 0 Then sum -= CDbl(horizontal((y - radius) * w + x)) : count -= 1
                               Next
                           Next
                           Return output
                       End Function
            For i = 0 To n - 1
                numerator(i) = values(i) * p.A(i)
            Next
            numerator = mean(numerator) : denominator = mean(p.A)
            For i = 0 To n - 1
                result(i) = If(denominator(i) > 0, numerator(i) / Math.Max(denominator(i), Single.Epsilon), values(i))
            Next
            Return result
        End Function

        Friend Shared Function WorkingConvolve(values As Single(), w As Integer, h As Integer, kernel As Single()) As Single()
            Dim horizontal(values.Length - 1), output(values.Length - 1) As Single
            Dim radius = kernel.Length \ 2
            Parallel.For(0, h,
                Sub(y)
                    For x = 0 To w - 1
                        Dim sum = 0.0
                        For k = -radius To radius
                            sum += CDbl(kernel(k + radius)) * values(y * w + Math.Clamp(x + k, 0, w - 1))
                        Next
                        horizontal(y * w + x) = CSng(sum)
                    Next
                End Sub)
            Parallel.For(0, h,
                Sub(y)
                    For x = 0 To w - 1
                        Dim sum = 0.0
                        For k = -radius To radius
                            sum += CDbl(kernel(k + radius)) * horizontal(Math.Clamp(y + k, 0, h - 1) * w + x)
                        Next
                        output(y * w + x) = CSng(sum)
                    Next
                End Sub)
            Return output
        End Function

        Friend Shared Function WorkingConvolveCovered(values As Single(), p As WorkingEffectPlanes, kernel As Single()) As Single()
            Dim premul(values.Length - 1) As Single
            For i = 0 To values.Length - 1
                premul(i) = values(i) * p.A(i)
            Next
            Dim output = WorkingConvolve(premul, p.Width, p.Height, kernel)
            Dim alpha = WorkingConvolve(p.A, p.Width, p.Height, kernel)
            For i = 0 To output.Length - 1
                output(i) = If(alpha(i) > 0, output(i) / Math.Max(alpha(i), Single.Epsilon), values(i))
            Next
            Return output
        End Function

        Friend Shared Sub WorkingClarity(p As WorkingEffectPlanes, amount As Single)
            Dim luma = p.Luma()
            For i = 0 To luma.Length - 1
                luma(i) /= 255.0F
            Next
            Dim radius = Math.Max(1, CInt(Math.Round(Math.Min(p.Width, p.Height) * ClarityRadiusShare)))
            Dim average = WorkingMean(luma, p, radius)
            Dim squares = luma.Select(Function(v) v * v).ToArray()
            squares = WorkingMean(squares, p, radius)
            Dim coefA(luma.Length - 1), coefB(luma.Length - 1) As Single
            For i = 0 To luma.Length - 1
                Dim variance = Math.Max(0.0, squares(i) - CDbl(average(i)) * average(i))
                coefA(i) = CSng(variance / (variance + ClarityEdgeEpsilon))
                coefB(i) = (1 - coefA(i)) * average(i)
            Next
            coefA = WorkingMean(coefA, p, radius) : coefB = WorkingMean(coefB, p, radius)
            Dim gain = If(amount > 0, amount * ClarityPositiveGain, amount)
            For i = 0 To luma.Length - 1
                Dim baseValue = coefA(i) * luma(i) + coefB(i)
                Dim t = 2 * CDbl(baseValue) - 1
                average(i) = CSng((luma(i) - baseValue) * gain * Math.Max(0, 1 - t * t) * 255.0)
            Next
            p.AddLumaDelta(average)
        End Sub

        Friend Shared Sub WorkingStructure(p As WorkingEffectPlanes, amount As Single)
            Dim blurred = p.Blurred(0.25F + (3.6F / 8) * 2.2F), gain = amount * 2.4F
            For i = 0 To p.R.Length - 1
                p.R(i) += (p.R(i) - blurred.R(i)) * gain
                p.G(i) += (p.G(i) - blurred.G(i)) * gain
                p.B(i) += (p.B(i) - blurred.B(i)) * gain
            Next
        End Sub

        Friend Shared Sub WorkingGlow(p As WorkingEffectPlanes, amount As Single)
            Dim blurred = p.Blurred(0.25F + 0.8F * 2.2F), gain = amount * 0.55F
            For i = 0 To p.R.Length - 1
                p.R(i) += blurred.R(i) * gain : p.G(i) += blurred.G(i) * gain : p.B(i) += blurred.B(i) * gain
            Next
        End Sub

        Friend Shared Sub WorkingTiltShift(p As WorkingEffectPlanes, a As ImageAdjustments)
            Dim sigma = Math.Min(p.Width, p.Height) * TiltShiftSigmaShare * a.TiltShiftAmount / 100.0F
            If sigma < 0.5F Then Return
            Dim full = p.Blurred(sigma), mid = p.Blurred(sigma * TiltShiftMidShare)
            Dim radians = a.TiltShiftAngle * Math.PI / 180, nx = Math.Sin(radians), ny = Math.Cos(radians)
            Dim extent = Math.Max(1, Math.Abs(p.Width * nx) + Math.Abs(p.Height * ny))
            Dim half = a.TiltShiftFocus / 200.0, ramp = Math.Max(0.01, a.TiltShiftTransition / 200.0)
            For y = 0 To p.Height - 1
                For x = 0 To p.Width - 1
                    Dim i = y * p.Width + x
                    Dim distance = Math.Abs((x + 0.5 - p.Width * a.TiltShiftCenterX / 100) * nx +
                                           (y + 0.5 - p.Height * a.TiltShiftCenterY / 100) * ny) / extent
                    Dim t = Math.Clamp((distance - half) / ramp, 0, 1)
                    t = t * t * (3 - 2 * t)
                    If t <= 0 Then Continue For
                    Dim originalAlpha = p.A(i)
                    Dim wa = If(t < 0.5, 1 - t * 2, 0), wb = If(t < 0.5, t * 2, 2 - t * 2), wc = If(t < 0.5, 0, 2 * t - 1)
                    Dim alpha = wa * originalAlpha + wb * mid.A(i) + wc * full.A(i)
                    If alpha > 0 Then
                        p.R(i) = CSng((wa * p.R(i) * originalAlpha + wb * mid.R(i) * mid.A(i) + wc * full.R(i) * full.A(i)) / alpha)
                        p.G(i) = CSng((wa * p.G(i) * originalAlpha + wb * mid.G(i) * mid.A(i) + wc * full.G(i) * full.A(i)) / alpha)
                        p.B(i) = CSng((wa * p.B(i) * originalAlpha + wb * mid.B(i) * mid.A(i) + wc * full.B(i) * full.A(i)) / alpha)
                    End If
                    p.A(i) = CSng(alpha)
                Next
            Next
        End Sub

        Friend Shared Sub WorkingVignette(p As WorkingEffectPlanes, a As ImageAdjustments)
            Dim amount = a.Vignette / 100.0 * If(a.Vignette > 0, 0.59, 0.85)
            Dim strength = Math.Min(1.5, Math.Abs(amount)), rounded = a.VignetteRoundness / 100.0
            Dim radiusX = p.Width * (0.52 - Math.Min(0, rounded) * 0.18)
            Dim radiusY = p.Height * (0.52 + Math.Max(0, rounded) * 0.18)
            Dim inner = 0.2 + a.VignetteTransition / 100.0 * 0.55, softness = 0.04 + a.VignetteFeather / 100.0 * 0.42
            For y = 0 To p.Height - 1
                For x = 0 To p.Width - 1
                    Dim dx = (x - p.Width * a.VignetteCenterX / 100) / Math.Max(1, radiusX)
                    Dim dy = (y - p.Height * a.VignetteCenterY / 100) / Math.Max(1, radiusY)
                    Dim t = Math.Clamp((Math.Sqrt(dx * dx + dy * dy) - inner) / softness, 0, 1)
                    Dim mix = t * t * (3 - 2 * t) * If(amount > 0, 1.0, 220.0 / 255) * strength
                    Dim i = y * p.Width + x
                    If amount > 0 Then
                        Dim luma = WorkingEffectPlanes.Kr * p.R(i) + WorkingEffectPlanes.Kg * p.G(i) + WorkingEffectPlanes.Kb * p.B(i)
                        If a.VignetteStyle = VignetteStyle.PaintOverlay Then
                            p.R(i) = CSng((p.R(i) + (luma - p.R(i)) * mix * 0.5) * (1 - mix))
                            p.G(i) = CSng((p.G(i) + (luma - p.G(i)) * mix * 0.5) * (1 - mix))
                            p.B(i) = CSng((p.B(i) + (luma - p.B(i)) * mix * 0.5) * (1 - mix))
                        Else
                            Dim protection = If(a.VignetteStyle = VignetteStyle.HighlightPriority, Math.Pow(Math.Clamp(luma / 255, 0, 1), 2) * 0.7, 0)
                            Dim factor = 1 - mix * (1 - protection)
                            p.R(i) = CSng(p.R(i) * factor) : p.G(i) = CSng(p.G(i) * factor) : p.B(i) = CSng(p.B(i) * factor)
                        End If
                    Else
                        p.R(i) = CSng(p.R(i) + (255 - p.R(i)) * mix)
                        p.G(i) = CSng(p.G(i) + (255 - p.G(i)) * mix)
                        p.B(i) = CSng(p.B(i) + (255 - p.B(i)) * mix)
                    End If
                Next
            Next
        End Sub

        Friend Shared Sub WorkingGrain(p As WorkingEffectPlanes, a As ImageAdjustments)
            Dim cell = 1 + CInt(Math.Round(a.GrainSize / 100 * 5)), frequency = a.GrainFrequency / 100.0
            Dim color = a.GrainColor / 100.0, norm = If(color = 0, 1, 1 / Math.Sqrt((1 - color) ^ 2 + color ^ 2))
            Dim amplitude = 8 + a.Grain / 100.0 * 34, seed = p.Width * 397 Xor p.Height * 151
            Dim gridWidth = (p.Width + cell - 1) \ cell, modWidth = (p.Width + 15) \ 16
            For y = 0 To p.Height - 1
                For x = 0 To p.Width - 1
                    Dim i = y * p.Width + x, index = (y \ cell) * gridWidth + x \ cell
                    Dim mono = GrainNoiseAt(index, seed), amp = amplitude
                    If frequency > 0 Then amp *= 1 + frequency * GrainNoiseAt((y \ 16) * modWidth + x \ 16, seed Xor &H5BF03635) * 0.9
                    p.R(i) = WorkingNoiseChannel(p.R(i), ((1 - color) * mono + color * CellChannelNoise(index, 1)) * amp * norm)
                    p.G(i) = WorkingNoiseChannel(p.G(i), ((1 - color) * mono + color * CellChannelNoise(index, 2)) * amp * norm)
                    p.B(i) = WorkingNoiseChannel(p.B(i), ((1 - color) * mono + color * CellChannelNoise(index, 3)) * amp * norm)
                Next
            Next
        End Sub

        Private Shared Function WorkingNoiseChannel(value As Single, noise As Double) As Single
            ' Grain/noise amplitudes were calibrated in perceptual code units.
            ' Adding that amplitude directly to linear radiance overwhelms shadows.
            ' Apply only the continuous transfer curve in the native primaries;
            ' extended signed/HDR channels stay unclipped and never become RGB8.
            Return CSng(PointColorMath.SrgbDecode(PointColorMath.SrgbEncode(value / 255.0) + noise / 255.0) * 255)
        End Function

        Friend Shared Sub WorkingAddedNoise(p As WorkingEffectPlanes, amount As Single, colorOnly As Boolean)
            Dim random = New Random(If(colorOnly, p.Width * 733 Xor p.Height * 397, p.Width * 541 Xor p.Height * 877))
            Dim amplitude = amount * If(colorOnly, 48.0, 72.0)
            For i = 0 To p.R.Length - 1
                Dim dr = (random.NextDouble() * 2 - 1) * amplitude
                Dim dg = If(colorOnly, 0, (random.NextDouble() * 2 - 1) * amplitude)
                Dim db = (random.NextDouble() * 2 - 1) * amplitude
                If colorOnly Then dg = -(WorkingEffectPlanes.Kr * dr + WorkingEffectPlanes.Kb * db) / WorkingEffectPlanes.Kg
                Dim luminance = WorkingEffectPlanes.Kr * p.R(i) + WorkingEffectPlanes.Kg * p.G(i) + WorkingEffectPlanes.Kb * p.B(i)
                p.R(i) = WorkingNoiseChannel(p.R(i), dr)
                p.G(i) = WorkingNoiseChannel(p.G(i), dg)
                p.B(i) = WorkingNoiseChannel(p.B(i), db)
                If colorOnly Then
                    ' The nonlinear transfer would otherwise move linear Y even
                    ' for a zero-luma random vector. Keep this a chroma-only tool.
                    Dim shift = luminance - (WorkingEffectPlanes.Kr * p.R(i) + WorkingEffectPlanes.Kg * p.G(i) + WorkingEffectPlanes.Kb * p.B(i))
                    p.R(i) = CSng(p.R(i) + shift) : p.G(i) = CSng(p.G(i) + shift) : p.B(i) = CSng(p.B(i) + shift)
                End If
            Next
        End Sub

        Friend Shared Sub WorkingDust(p As WorkingEffectPlanes, amount As Single)
            If amount < 0 Then
                Dim median = WorkingMedian(p, -amount)
                p.Mix(median, -amount)
                Return
            End If
            ' Draw only a procedural overlay, never the source image, into F32.
            ' Its gray/alpha paint values are UI metadata in encoded sRGB. Decode
            ' the resulting overlay before compositing it over working light.
            Using overlay As New SKBitmap(New SKImageInfo(p.Width, p.Height, SKColorType.RgbaF32, SKAlphaType.Premul))
                Using canvas As New SKCanvas(overlay)
                    canvas.Clear(SKColors.Transparent)
                    DrawDustAndScratches(canvas, p.Width, p.Height, amount)
                End Using
                Dim row(p.Width * 4 - 1) As Single
                For y = 0 To p.Height - 1
                    Marshal.Copy(overlay.GetPixels() + y * overlay.RowBytes, row, 0, row.Length)
                    For x = 0 To p.Width - 1
                        Dim at = x * 4, i = y * p.Width + x, alpha = CDbl(row(at + 3))
                        If alpha <= 0 Then Continue For
                        Dim value = PointColorMath.SrgbDecode(Math.Clamp(row(at) / alpha, 0, 1)) * 255
                        Dim oldAlpha = p.A(i) * (1 - alpha), total = alpha + oldAlpha
                        p.R(i) = CSng((value * alpha + p.R(i) * oldAlpha) / total)
                        p.G(i) = CSng((value * alpha + p.G(i) * oldAlpha) / total)
                        p.B(i) = CSng((value * alpha + p.B(i) * oldAlpha) / total)
                        p.A(i) = CSng(total)
                    Next
                Next
            End Using
        End Sub
    End Class
End Namespace
