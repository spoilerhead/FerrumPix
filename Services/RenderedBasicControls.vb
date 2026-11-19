Imports System

Namespace Services
    ''' <summary>Complete migrated basic group, including spatial tone zones and the late
    ''' shadow-color restore. Existing low-level RenderedBasicTone remains a stable subset.
    ''' Analysis is F32 encoded sRGB; the original Rec.2020 F16 frame is never clipped.</summary>
    Friend NotInheritable Class RenderedBasicControls
        Private Sub New()
        End Sub

        Friend Shared Function Apply(source As LinearWorkingFrame, settings As RenderedBasicSettings) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            ArgumentNullException.ThrowIfNull(settings)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Basic controls require rendered-linear input.", NameOf(source))
            If settings.IsIdentity Then Return RenderedBasicTone.Apply(source, 0)
            If CLng(source.Width) * source.Height > Integer.MaxValue \ 16 Then Throw New ArgumentOutOfRangeException(NameOf(source), "Full F32 basic analysis exceeds staging size.")

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
                    Dim r = input(at) / alpha, g = input(at + 1) / alpha, b = input(at + 2) / alpha
                    pixels(target) = CSng(PointColorMath.SrgbEncode(Math.Clamp(1.660491002 * r - 0.587641139 * g - 0.072849863 * b, 0.0, 1.0)))
                    pixels(target + 1) = CSng(PointColorMath.SrgbEncode(Math.Clamp(-0.124550475 * r + 1.132899897 * g - 0.008349422 * b, 0.0, 1.0)))
                    pixels(target + 2) = CSng(PointColorMath.SrgbEncode(Math.Clamp(-0.018150763 * r - 0.100578898 * g + 1.118729661 * b, 0.0, 1.0)))
                Next
            Next
            Dim stage = ImageProcessor.PrepareFloatingBasic(source.Width, source.Height, pixels, settings)
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
                        If settings.Saturation = 0 AndAlso r = originalR AndAlso g = originalG AndAlso b = originalB Then Continue For
                        If settings.Saturation <> 0 Then
                            ' Saturation must transform the residue as well: leaving original
                            ' chroma there would make -100 fail to produce monochrome on wide
                            ' colors. Extend the SAME encoded-domain matrix, then add only the
                            ' remaining tone/vibrance delta measured after bounded saturation.
                            Dim sr = 1.660491002 * row(at) / alpha - 0.587641139 * row(at + 1) / alpha - 0.072849863 * row(at + 2) / alpha
                            Dim sg = -0.124550475 * row(at) / alpha + 1.132899897 * row(at + 1) / alpha - 0.008349422 * row(at + 2) / alpha
                            Dim sb = -0.018150763 * row(at) / alpha - 0.100578898 * row(at + 1) / alpha + 1.118729661 * row(at + 2) / alpha
                            Dim er = SignedEncode(sr) - originalR, eg = SignedEncode(sg) - originalG, eb = SignedEncode(sb) - originalB
                            stage.SaturateResidual(er, eg, eb)
                            Dim pr = originalR, pg = originalG, pb = originalB
                            stage.SaturateProjection(pr, pg, pb)
                            sr = SignedDecode(pr + er) + PointColorMath.SrgbDecode(r) - PointColorMath.SrgbDecode(pr)
                            sg = SignedDecode(pg + eg) + PointColorMath.SrgbDecode(g) - PointColorMath.SrgbDecode(pg)
                            sb = SignedDecode(pb + eb) + PointColorMath.SrgbDecode(b) - PointColorMath.SrgbDecode(pb)
                            row(at) = CSng((0.627403896 * sr + 0.329283038 * sg + 0.043313066 * sb) * alpha)
                            row(at + 1) = CSng((0.069097289 * sr + 0.919540395 * sg + 0.011362316 * sb) * alpha)
                            row(at + 2) = CSng((0.016391439 * sr + 0.088013308 * sg + 0.895595253 * sb) * alpha)
                            Continue For
                        End If
                        ' Preserve original wide/signed/HDR reserves by returning only the
                        ' calibrated SDR effect's linear delta, once for this entire group.
                        Dim dr = PointColorMath.SrgbDecode(r) - PointColorMath.SrgbDecode(originalR)
                        Dim dg = PointColorMath.SrgbDecode(g) - PointColorMath.SrgbDecode(originalG)
                        Dim db = PointColorMath.SrgbDecode(b) - PointColorMath.SrgbDecode(originalB)
                        row(at) += CSng((0.627403896 * dr + 0.329283038 * dg + 0.043313066 * db) * alpha)
                        row(at + 1) += CSng((0.069097289 * dr + 0.919540395 * dg + 0.011362316 * db) * alpha)
                        row(at + 2) += CSng((0.016391439 * dr + 0.088013308 * dg + 0.895595253 * db) * alpha)
                    Next
                End Sub)
        End Function

        ' Explicit odd extension of the sRGB transfer pair for the saturation residue.
        ' It preserves sign/range and avoids fractional powers of negative values.
        Private Shared Function SignedEncode(value As Double) As Double
            Return Math.CopySign(PointColorMath.SrgbEncode(Math.Abs(value)), value)
        End Function

        Private Shared Function SignedDecode(value As Double) As Double
            Return Math.CopySign(PointColorMath.SrgbDecode(Math.Abs(value)), value)
        End Function
    End Class
End Namespace
