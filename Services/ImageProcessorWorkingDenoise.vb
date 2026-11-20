Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks

Namespace Services
    Partial Public Class ImageProcessor
        Friend Shared Function WorkingMedian(p As WorkingEffectPlanes, amount As Single) As WorkingEffectPlanes
            Dim radius = Math.Max(1, CInt(Math.Round(1 + amount * 2))), result = p.ClonePlanes()
            Parallel.For(0, p.Height,
                Sub(y)
                    Dim side = 2 * radius + 1
                    Dim window(side * side - 1) As Single
                    For x = 0 To p.Width - 1
                        Dim at = y * p.Width + x
                        If p.A(at) <= 0 Then Continue For
                        For c = 0 To 2
                            Dim input = If(c = 0, p.R, If(c = 1, p.G, p.B))
                            Dim output = If(c = 0, result.R, If(c = 1, result.G, result.B)), count = 0
                            For sy = Math.Max(0, y - radius) To Math.Min(p.Height - 1, y + radius)
                                For sx = Math.Max(0, x - radius) To Math.Min(p.Width - 1, x + radius)
                                    Dim i = sy * p.Width + sx
                                    If p.A(i) <= 0 Then Continue For
                                    window(count) = input(i) : count += 1
                                Next
                            Next
                            If count = 0 Then Continue For
                            Array.Sort(window, 0, count)
                            output(at) = window(count \ 2)
                        Next
                    Next
                End Sub)
            Return result
        End Function

        Friend Shared Sub WorkingGaussianNoise(p As WorkingEffectPlanes, amount As Single, detail As Single)
            Dim smooth = p.Blurred(0.25F + amount * 2.2F)
            For i = 0 To p.R.Length - 1
                Dim difference = Math.Abs(p.R(i) - smooth.R(i)) + Math.Abs(p.G(i) - smooth.G(i)) + Math.Abs(p.B(i) - smooth.B(i))
                Dim restore = Math.Clamp(difference / 48.0F, 0, 1) * detail
                p.R(i) = smooth.R(i) + (p.R(i) - smooth.R(i)) * restore
                p.G(i) = smooth.G(i) + (p.G(i) - smooth.G(i)) * restore
                p.B(i) = smooth.B(i) + (p.B(i) - smooth.B(i)) * restore
            Next
            ' Denoise preserves source coverage. Only an explicit blur spreads it.
        End Sub

        ''' <summary>Measure detrended F32 blocks; only the small statistical
        ''' profile is binned. Neither samples nor guidance become an RGB8 image.</summary>
        Friend Shared Function WorkingNoiseProfile(p As WorkingEffectPlanes) As Single()
            Return WorkingNoiseProfileFromLuma(p.Luma(), p.Width, p.Height, p.A)
        End Function

        Friend Shared Function WorkingNoiseProfileFromLuma(luma As Single(), width As Integer, height As Integer, coverage As Single()) As Single()
            Dim bins(NoiseProfileBins - 1) As List(Of Double)
            For i = 0 To bins.Length - 1
                bins(i) = New List(Of Double)()
            Next
            Dim stride = Math.Max(NoiseProfileBlock, CInt(Math.Ceiling(Math.Sqrt(luma.Length / CDbl(NoiseProfileMaxBlocks)))))
            For blockTop As Integer = 0 To height - NoiseProfileBlock Step stride
                For blockLeft As Integer = 0 To width - NoiseProfileBlock Step stride
                    Dim sum = 0.0, squares = 0.0, sumX = 0.0, sumY = 0.0, count = 0
                    Dim xSquare = 0.0, ySquare = 0.0, valid = True
                    For y = 0 To NoiseProfileBlock - 1
                        For x = 0 To NoiseProfileBlock - 1
                            Dim i = (blockTop + y) * width + blockLeft + x
                            If coverage(i) < 0.95 Then valid = False
                            Dim v = CDbl(luma(i)), dx = x - (NoiseProfileBlock - 1) / 2.0, dy = y - (NoiseProfileBlock - 1) / 2.0
                            sum += v : squares += v * v : sumX += v * dx : sumY += v * dy
                            xSquare += dx * dx : ySquare += dy * dy : count += 1
                        Next
                    Next
                    If Not valid Then Continue For
                    Dim mean = sum / count
                    Dim variance = Math.Max(0, (squares - sum * mean - sumX * sumX / xSquare - sumY * sumY / ySquare) / Math.Max(1, count - 3))
                    Dim bin = Math.Clamp(CInt(Math.Floor(mean / 256 * bins.Length)), 0, bins.Length - 1)
                    bins(bin).Add(Math.Sqrt(variance) * NoiseProfileCalibration)
                Next
            Next
            Dim estimates(bins.Length - 1) As Double
            Array.Fill(estimates, Double.NaN)
            For i = 0 To bins.Length - 1
                If bins(i).Count < NoiseProfileMinBlocks Then Continue For
                bins(i).Sort()
                estimates(i) = bins(i)(Math.Clamp(CInt(Math.Floor((bins(i).Count - 1) * NoiseProfilePercentile)), 0, bins(i).Count - 1))
            Next
            Dim known = Enumerable.Range(0, estimates.Length).Where(Function(i) Double.IsFinite(estimates(i))).ToArray()
            If known.Length = 0 Then Return Nothing
            For i = 0 To estimates.Length - 1
                If Double.IsFinite(estimates(i)) Then Continue For
                Dim index = i
                estimates(i) = estimates(known.OrderBy(Function(k) Math.Abs(k - index)).First())
            Next
            Dim profile(255) As Single
            For i = 0 To 255
                Dim position = Math.Clamp((i + 0.5) / 256 * estimates.Length - 0.5, 0, estimates.Length - 1)
                Dim lower = CInt(Math.Floor(position)), upper = Math.Min(estimates.Length - 1, lower + 1)
                profile(i) = CSng(estimates(lower) + (estimates(upper) - estimates(lower)) * (position - lower))
            Next
            Return profile
        End Function

        ''' <summary>Automatic model strength is calibrated in encoded-sRGB luma.
        ''' Measure a continuous F32 guide in that space, using the existing full/
        ''' half texture rejection law, without first making an RGB8 image.</summary>
        Friend Shared Function EstimateWorkingNoiseLevel(source As LinearWorkingFrame) As Single
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Rendered input required.")
            Dim w = source.Width, h = source.Height, n = w * h, luma(n - 1), alpha(n - 1) As Single
            Dim row(w * 4 - 1) As Single
            For y = 0 To h - 1
                source.ReadLinearRow(y, row)
                For x = 0 To w - 1
                    Dim at = x * 4, i = y * w + x, a = CDbl(row(at + 3))
                    alpha(i) = CSng(a)
                    If a <= 0 Then Continue For
                    Dim r = 0.0, g = 0.0, b = 0.0
                    WorkingSurveySrgb(row(at) / a, row(at + 1) / a, row(at + 2) / a, r, g, b)
                    luma(i) = CSng((0.299 * r + 0.587 * g + 0.114 * b) * 255)
                Next
            Next
            Dim measure = Function(values As Single(), coverage As Single(), width As Integer, height As Integer) As Double
                              Dim profile = WorkingNoiseProfileFromLuma(values, width, height, coverage)
                              If profile Is Nothing Then Return Double.NaN
                              Dim sum = 0.0, count = 0
                              For i = 0 To values.Length - 1
                                  If coverage(i) < 0.95 Then Continue For
                                  Dim level = Math.Clamp(CDbl(values(i)), 0, 255), low = CInt(Math.Floor(level)), high = Math.Min(255, low + 1)
                                  sum += profile(low) + (profile(high) - profile(low)) * (level - low) : count += 1
                              Next
                              Return If(count = 0, Double.NaN, sum / Math.Max(1, count))
                          End Function
            Dim full = measure(luma, alpha, w, h)
            If Double.IsNaN(full) Then Return Single.NaN
            Dim hw = (w + 1) \ 2, hh = (h + 1) \ 2, halfL(hw * hh - 1), halfA(hw * hh - 1) As Single
            For y = 0 To hh - 1
                For x = 0 To hw - 1
                    Dim sum = 0.0, cover = 0.0, count = 0
                    For sy = y * 2 To Math.Min(h, y * 2 + 2) - 1
                        For sx = x * 2 To Math.Min(w, x * 2 + 2) - 1
                            sum += luma(sy * w + sx) : cover += alpha(sy * w + sx) : count += 1
                        Next
                    Next
                    halfL(y * hw + x) = CSng(sum / count) : halfA(y * hw + x) = CSng(cover / count)
                Next
            Next
            Dim half = measure(halfL, halfA, hw, hh)
            If Double.IsNaN(half) Then Return CSng(full)
            Dim variance = (TextureVarianceGainAtHalfScale * full * full - half * half) /
                           (TextureVarianceGainAtHalfScale - NoiseVarianceKeptAtHalfScale)
            Return CSng(Math.Sqrt(Math.Clamp(variance, 0, full * full)))
        End Function

        Friend Shared Sub WorkingGuided(p As WorkingEffectPlanes, amount As Single, detail As Single, adaptive As Boolean,
                                       Optional levels As Integer = 0)
            If adaptive AndAlso levels < MultiScaleGuidedLevels AndAlso Math.Min(p.Width, p.Height) >= MultiScaleGuidedMinEdge Then
                Dim originalCoarse = p.HalfSize(), filtered = originalCoarse.ClonePlanes()
                WorkingGuided(filtered, amount * MultiScaleCoarseWeight, detail, True, levels + 1)
                Dim add = Sub(values As Single(), denoised As Single(), original As Single())
                              For y = 0 To p.Height - 1
                                  For x = 0 To p.Width - 1
                                      Dim fx = (x + 0.5) / 2 - 0.5, fy = (y + 0.5) / 2 - 0.5
                                      Dim delta = WorkingSampleDifference(denoised, original, originalCoarse.Width, originalCoarse.Height, fx, fy)
                                      values(y * p.Width + x) += CSng(delta)
                                  Next
                              Next
                          End Sub
                add(p.R, filtered.R, originalCoarse.R) : add(p.G, filtered.G, originalCoarse.G) : add(p.B, filtered.B, originalCoarse.B)
            End If
            Dim profile = If(adaptive, WorkingNoiseProfile(p), Nothing)
            Dim eps = If(profile Is Nothing, Nothing, AdaptiveNoiseEpsilonTable(profile, amount))
            Dim luma = p.Luma(), mean = WorkingMean(luma, p, GuidedRadius)
            Dim square = luma.Select(Function(v) v * v).ToArray()
            square = WorkingMean(square, p, GuidedRadius)
            Dim coefA(luma.Length - 1), coefB(luma.Length - 1) As Single
            Dim texture = GuidedDetailTexture(amount, detail)
            For i = 0 To luma.Length - 1
                Dim variance = Math.Max(0, square(i) - CDbl(mean(i)) * mean(i))
                Dim epsilon = CDbl(GuidedNoiseEpsilon(amount))
                If eps IsNot Nothing Then
                    Dim level = Math.Clamp(CDbl(luma(i)), 0, 255), lower = CInt(Math.Floor(level)), upper = Math.Min(255, lower + 1)
                    epsilon = eps(lower) + (eps(upper) - eps(lower)) * (level - lower)
                End If
                If texture > 0 Then epsilon /= 1 + texture * (variance / Math.Max(0.000001, epsilon)) ^ 2
                coefA(i) = CSng(variance / (variance + Math.Max(0.000001, epsilon)))
                coefB(i) = (1 - coefA(i)) * mean(i)
            Next
            coefA = WorkingMean(coefA, p, GuidedRadius) : coefB = WorkingMean(coefB, p, GuidedRadius)
            Dim cr = p.R.Select(Function(v, i) v - luma(i)).ToArray()
            Dim cb = p.B.Select(Function(v, i) v - luma(i)).ToArray()
            Dim smoothR = WorkingMean(cr, p, GuidedRadius), smoothB = WorkingMean(cb, p, GuidedRadius)
            For i = 0 To luma.Length - 1
                Dim gain = coefA(i), newY = gain * luma(i) + coefB(i)
                Dim newR = newY + cr(i) + (smoothR(i) - cr(i)) * (1 - gain)
                Dim newB = newY + cb(i) + (smoothB(i) - cb(i)) * (1 - gain)
                p.R(i) = newR : p.B(i) = newB
                p.G(i) = CSng((newY - WorkingEffectPlanes.Kr * newR - WorkingEffectPlanes.Kb * newB) / WorkingEffectPlanes.Kg)
            Next
        End Sub

        Private Shared Function WorkingSampleDifference(a As Single(), b As Single(), w As Integer, h As Integer, x As Double, y As Double) As Double
            x = Math.Clamp(x, 0, w - 1) : y = Math.Clamp(y, 0, h - 1)
            Dim left = CInt(Math.Floor(x)), top = CInt(Math.Floor(y)), right = Math.Min(w - 1, left + 1), bottom = Math.Min(h - 1, top + 1)
            Dim tx = x - left, ty = y - top
            Return ((CDbl(a(top * w + left)) - b(top * w + left)) * (1 - tx) + (CDbl(a(top * w + right)) - b(top * w + right)) * tx) * (1 - ty) +
                   ((CDbl(a(bottom * w + left)) - b(bottom * w + left)) * (1 - tx) + (CDbl(a(bottom * w + right)) - b(bottom * w + right)) * tx) * ty
        End Function

        Friend Shared Sub WorkingColorDenoise(p As WorkingEffectPlanes, amount As Single, coarse As Single, rootSpace As Boolean)
            Dim originalY = p.Luma(), count = originalY.Length
            Dim luminance(count - 1), cr(count - 1), cb(count - 1) As Single
            Dim toRoot = Function(v As Single) CSng(Math.CopySign(Math.Sqrt(Math.Abs(v) / 255.0) * 255, v))
            For i = 0 To count - 1
                Dim r = If(rootSpace, toRoot(p.R(i)), p.R(i)), g = If(rootSpace, toRoot(p.G(i)), p.G(i)), b = If(rootSpace, toRoot(p.B(i)), p.B(i))
                luminance(i) = CSng(WorkingEffectPlanes.Kr * r + WorkingEffectPlanes.Kg * g + WorkingEffectPlanes.Kb * b)
                cr(i) = r - luminance(i) : cb(i) = b - luminance(i)
            Next
            Dim levels = If(rootSpace, 3, Math.Clamp(CInt(Math.Round(3 + coarse * 4)), 3, 7))
            Dim threshold = 1.0F + 19.0F * CSng(Math.Pow(amount, 1.5))
            Dim shrinkPlane = Sub(values As Single())
                                  Dim baseline = CType(values.Clone(), Single()), details(values.Length - 1) As Single
                                  For level = 0 To levels - 1
                                      Dim smooth = WorkingMean(baseline, p, 1 << level)
                                      For i = 0 To values.Length - 1
                                          details(i) += Shrink(baseline(i) - smooth(i), threshold)
                                      Next
                                      baseline = smooth
                                  Next
                                  For i = 0 To values.Length - 1
                                      values(i) = baseline(i) + details(i)
                                  Next
                              End Sub
            shrinkPlane(cr) : shrinkPlane(cb)
            Dim fromRoot = Function(v As Single) CSng(Math.CopySign(CDbl(v) * v / 255, v))
            For i = 0 To count - 1
                Dim r = luminance(i) + cr(i), b = luminance(i) + cb(i)
                Dim g = CSng((luminance(i) - WorkingEffectPlanes.Kr * r - WorkingEffectPlanes.Kb * b) / WorkingEffectPlanes.Kg)
                If rootSpace Then
                    r = fromRoot(r) : g = fromRoot(g) : b = fromRoot(b)
                End If
                ' Root-domain filtering can alter linear Y. Return its chroma
                ' change while retaining the exact original working luminance.
                Dim shift = originalY(i) - (WorkingEffectPlanes.Kr * r + WorkingEffectPlanes.Kg * g + WorkingEffectPlanes.Kb * b)
                p.R(i) = CSng(r + shift) : p.G(i) = CSng(g + shift) : p.B(i) = CSng(b + shift)
            Next
        End Sub
    End Class
End Namespace
