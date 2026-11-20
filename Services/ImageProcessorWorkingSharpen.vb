Imports System
Imports System.Linq

Namespace Services
    Partial Public Class ImageProcessor
        Private Shared Function WorkingSharpenMask(p As WorkingEffectPlanes, luma As Single(), x As Integer, y As Integer,
                                                   masking As Single) As Double
            If masking <= 0 Then Return 1
            Dim i = y * p.Width + x
            Dim valueAt = Function(sx As Integer, sy As Integer) As Double
                              Dim index = Math.Clamp(sy, 0, p.Height - 1) * p.Width + Math.Clamp(sx, 0, p.Width - 1)
                              Return If(p.A(index) > 0, CDbl(luma(index)), CDbl(luma(i)))
                          End Function
            Dim edge = Math.Clamp((Math.Abs(valueAt(x - 1, y) - valueAt(x + 1, y)) +
                                   Math.Abs(valueAt(x, y - 1) - valueAt(x, y + 1))) / SharpenEdgeFullScale, 0, 1)
            edge = edge * edge * (3 - 2 * edge)
            Return (1 - masking) + masking * edge
        End Function

        Friend Shared Sub WorkingSharpen(p As WorkingEffectPlanes, a As ImageAdjustments)
            Dim luma = p.Luma(), amount = a.Sharpness / 100.0F, radius = a.SharpenRadius / 100.0F
            Dim detail = a.SharpenDetail / 100.0F, masking = a.SharpenMasking / 100.0F
            Dim delta(luma.Length - 1) As Single
            Select Case a.SharpenMethod
                Case SharpenMethod.HighPass
                    Dim parameters = HighPassParameters(amount, radius), half = HighPassWeights(parameters.Sigma)
                    Dim kernel(half.Length * 2 - 2) As Single
                    For k = 0 To kernel.Length - 1
                        kernel(k) = half(Math.Abs(k - (half.Length - 1)))
                    Next
                    Dim blurred = WorkingConvolveCovered(luma, p, kernel)
                    For y = 0 To p.Height - 1
                        For x = 0 To p.Width - 1
                            Dim i = y * p.Width + x
                            delta(i) = CSng((luma(i) - blurred(i)) * parameters.Gain * WorkingSharpenMask(p, luma, x, y, masking))
                        Next
                    Next
                Case SharpenMethod.Deconvolution
                    Dim sigma = DeconvolutionSigmaBase + radius * DeconvolutionSigmaRange
                    Dim iterations = DeconvolutionMinIterations + CInt(Math.Round(detail * (DeconvolutionMaxIterations - DeconvolutionMinIterations)))
                    Dim kernel = GaussianKernel(sigma), offset = Math.Min(0.0, luma.Min())
                    ' Richardson-Lucy estimates nonnegative radiance. A removable
                    ' offset permits signed working luminance without clipping it
                    ' away; unlike the old SDR version, estimates have no 255 cap.
                    Dim observed = luma.Select(Function(v) CSng(v - offset)).ToArray()
                    Dim estimate = CType(observed.Clone(), Single())
                    Dim alpha = WorkingConvolve(p.A, p.Width, p.Height, kernel)
                    Dim convolve = Function(values As Single()) As Single()
                                       Dim weighted(values.Length - 1) As Single
                                       For i = 0 To values.Length - 1
                                           weighted(i) = values(i) * p.A(i)
                                       Next
                                       Dim filtered = WorkingConvolve(weighted, p.Width, p.Height, kernel)
                                       For i = 0 To values.Length - 1
                                           filtered(i) = If(alpha(i) > 0, filtered(i) / Math.Max(alpha(i), Single.Epsilon), values(i))
                                       Next
                                       Return filtered
                                   End Function
                    For iteration = 1 To iterations
                        Dim blurred = convolve(estimate)
                        For i = 0 To blurred.Length - 1
                            blurred(i) = CSng(observed(i) / Math.Max(0.5, CDbl(blurred(i))))
                        Next
                        Dim correction = convolve(blurred)
                        For i = 0 To estimate.Length - 1
                            estimate(i) = CSng(estimate(i) * Math.Clamp(CDbl(correction(i)), 1.0 / DeconvolutionStepLimit, CDbl(DeconvolutionStepLimit)))
                        Next
                    Next
                    For y = 0 To p.Height - 1
                        For x = 0 To p.Width - 1
                            Dim i = y * p.Width + x
                            delta(i) = CSng((estimate(i) - observed(i)) * amount * DeconvolutionGainScale * WorkingSharpenMask(p, luma, x, y, masking))
                        Next
                    Next
                Case SharpenMethod.UnsharpMask
                    Dim parameters = SharpenParameters(amount, radius, detail), weights = SharpenInnerWeights(parameters.InnerSigma)
                    Dim inner = WorkingConvolveCovered(luma, p, {weights.W2, weights.W1, weights.W0, weights.W1, weights.W2})
                    Dim blurred = p.BlurField(luma, parameters.Sigma)
                    For y = 0 To p.Height - 1
                        For x = 0 To p.Width - 1
                            Dim i = y * p.Width + x
                            Dim gain = CSng(parameters.Gain * WorkingSharpenMask(p, luma, x, y, masking))
                            delta(i) = SharpenDelta((inner(i) - blurred(i)) / 255.0F, gain, parameters.Halo, parameters.Dark) * 255.0F
                        Next
                    Next
                Case Else
                    Throw New ArgumentException("Unknown working sharpening method.")
            End Select
            ' A common delta preserves native linear chroma differences. Coverage
            ' is unchanged and all signed/HDR output is packed only after the group.
            p.AddLumaDelta(delta)
        End Sub
    End Class
End Namespace
