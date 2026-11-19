Imports System

Namespace Services
    ''' <summary>Complete migrated basic group, including spatial tone zones and the late
    ''' shadow-color restore. Existing low-level RenderedBasicTone remains a stable subset.
    ''' Saturation is native linear Rec.2020; tone/curve/color processing uses F32
    ''' encoded-sRGB adapters. The source is immutable; replacing artistic operators
    ''' may deliberately bound the result, unlike the reserve-preserving controls.</summary>
    Friend NotInheritable Class RenderedBasicControls
        Private Sub New()
        End Sub

        Friend Shared Function Apply(source As LinearWorkingFrame, settings As RenderedBasicSettings,
                                     Optional curves As RenderedCurveSettings = Nothing,
                                     Optional lateColor As RenderedLateColorSettings = Nothing) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            ArgumentNullException.ThrowIfNull(settings)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Basic controls require rendered-linear input.", NameOf(source))
            If settings.IsIdentity AndAlso (curves Is Nothing OrElse curves.IsIdentity) AndAlso
               (lateColor Is Nothing OrElse lateColor.IsIdentity) Then Return RenderedBasicTone.Apply(source, 0)
            If CLng(source.Width) * source.Height > Integer.MaxValue \ 16 Then Throw New ArgumentOutOfRangeException(NameOf(source), "Full F32 basic analysis exceeds staging size.")

            ' Saturate before projecting: wide colors must never be desaturated through
            ' a signed gamma curve or a bounded RGB cube.
            ' Correctness-first staging: retain floating source precision for BOTH statistics
            ' and the full-resolution local grid. No RGB8 proxy may choose the tone curves.
            ' A later tiled/row-reader implementation can remove this full F32 allocation.
            Dim pixels(source.Width * source.Height * 4 - 1) As Single
            Dim input(source.Width * 4 - 1) As Single
            For y = 0 To source.Height - 1
                source.ReadLinearRow(y, input)
                For x = 0 To source.Width - 1
                    Dim at = x * 4, target = (y * source.Width + x) * 4
                    Dim alpha = CDbl(input(at + 3))
                    pixels(target + 3) = CSng(alpha)
                    If alpha <= 0 Then Continue For
                    Dim r = CDbl(input(at)), g = CDbl(input(at + 1)), b = CDbl(input(at + 2))
                    ApplyLinearSaturation(r, g, b, settings.Saturation)
                    r /= alpha : g /= alpha : b /= alpha
                    pixels(target) = CSng(PointColorMath.SrgbEncode(Math.Clamp(1.660491002 * r - 0.587641139 * g - 0.072849863 * b, 0.0, 1.0)))
                    pixels(target + 1) = CSng(PointColorMath.SrgbEncode(Math.Clamp(-0.124550475 * r + 1.132899897 * g - 0.008349422 * b, 0.0, 1.0)))
                    pixels(target + 2) = CSng(PointColorMath.SrgbEncode(Math.Clamp(-0.018150763 * r - 0.100578898 * g + 1.118729661 * b, 0.0, 1.0)))
                Next
            Next
            Dim stage = ImageProcessor.PrepareFloatingBasic(source.Width, source.Height, pixels, settings,
                                                          saturationAlreadyApplied:=True, curves:=curves, lateColor:=lateColor)
            Dim retention = If(lateColor Is Nothing, 1.0, lateColor.ResidualRetention)
            Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain,
                Sub(y, row)
                    source.ReadLinearRow(y, row)
                    For x = 0 To source.Width - 1
                        Dim at = x * 4, encoded = (y * source.Width + x) * 4
                        Dim alpha = CDbl(row(at + 3))
                        If alpha <= 0 Then Continue For
                        Dim r = pixels(encoded), g = pixels(encoded + 1), b = pixels(encoded + 2)
                        Dim originalR = r, originalG = g, originalB = b
                        stage.Apply(r, g, b, x, y)
                        ' Use the same unquantized saturated baseline as the analysis pass.
                        ' Linear operations commute with premultiplication, so alpha need not
                        ' be divided out here and hidden RGB remains untouched.
                        Dim baseR = CDbl(row(at)), baseG = CDbl(row(at + 1)), baseB = CDbl(row(at + 2))
                        ApplyLinearSaturation(baseR, baseG, baseB, settings.Saturation)
                        If retention = 1 AndAlso r = originalR AndAlso g = originalG AndAlso b = originalB Then
                            row(at) = CSng(baseR) : row(at + 1) = CSng(baseG) : row(at + 2) = CSng(baseB)
                            Continue For
                        End If
                        ' Replacement effects cannot keep the input's chromatic reserve:
                        ' it would leak color into monochrome and leave non-palette values.
                        ' Partial gradient maps fade that reserve with their unmodified part.
                        If retention <> 1 Then
                            Dim sr = PointColorMath.SrgbDecode(r), sg = PointColorMath.SrgbDecode(g), sb = PointColorMath.SrgbDecode(b)
                            Dim pr = PointColorMath.SrgbDecode(originalR), pg = PointColorMath.SrgbDecode(originalG), pb = PointColorMath.SrgbDecode(originalB)
                            row(at) = CSng((baseR - (0.627403896 * pr + 0.329283038 * pg + 0.043313066 * pb) * alpha) * retention +
                                           (0.627403896 * sr + 0.329283038 * sg + 0.043313066 * sb) * alpha)
                            row(at + 1) = CSng((baseG - (0.069097289 * pr + 0.919540395 * pg + 0.011362316 * pb) * alpha) * retention +
                                               (0.069097289 * sr + 0.919540395 * sg + 0.011362316 * sb) * alpha)
                            row(at + 2) = CSng((baseB - (0.016391439 * pr + 0.088013308 * pg + 0.895595253 * pb) * alpha) * retention +
                                               (0.016391439 * sr + 0.088013308 * sg + 0.895595253 * sb) * alpha)
                            Continue For
                        End If
                        ' Preserve the saturated baseline's wide/signed/HDR reserves by returning only the
                        ' calibrated SDR effect's linear delta, once for this entire group.
                        Dim dr = PointColorMath.SrgbDecode(r) - PointColorMath.SrgbDecode(originalR)
                        Dim dg = PointColorMath.SrgbDecode(g) - PointColorMath.SrgbDecode(originalG)
                        Dim db = PointColorMath.SrgbDecode(b) - PointColorMath.SrgbDecode(originalB)
                        row(at) = CSng(baseR + (0.627403896 * dr + 0.329283038 * dg + 0.043313066 * db) * alpha)
                        row(at + 1) = CSng(baseG + (0.069097289 * dr + 0.919540395 * dg + 0.011362316 * db) * alpha)
                        row(at + 2) = CSng(baseB + (0.016391439 * dr + 0.088013308 * dg + 0.895595253 * db) * alpha)
                    Next
                End Sub)
        End Function

        ' Rec.2020 D65 luminance is a linear functional. Desaturating around it
        ' stays smooth across gamut boundaries, including negative sRGB coordinates.
        ' A signed gamma extension of the old Rec.601 encoded matrix instead folds
        ' valid wide colors through black. Keep this native step unclipped; only the
        ' compatibility analysis above uses a bounded encoded-sRGB projection.
        Private Shared Sub ApplyLinearSaturation(ByRef r As Double, ByRef g As Double, ByRef b As Double, saturation As Single)
            If saturation = 0 Then Return
            Dim luminance = 0.2627 * r + 0.678 * g + 0.0593 * b
            Dim gain = 1.0 + saturation / 100.0
            r = luminance + gain * (r - luminance)
            g = luminance + gain * (g - luminance)
            b = luminance + gain * (b - luminance)
        End Sub
    End Class
End Namespace
