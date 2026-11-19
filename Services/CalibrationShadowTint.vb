Imports System

Namespace Services
    Partial Public Class ImageProcessor
        Friend Shared Function CalibrationShadowTintParameters(slider As Single) As (Response As Single, Dark As Single)
            Dim tint = ShadowTintResponse(slider)
            Return (100.0F * tint, ShadowTintDarkening * Math.Abs(tint) + ShadowTintDarkeningMagenta * tint)
        End Function

        ' Gemeinsame Rechnung fuer Alt-Kette und F16-Adapter. Response/Dark einmal vorbereiten,
        ' damit die Extraktion keine Potenzrechnung pro Pixel in die bestehende Kette einfuehrt.
        <Runtime.CompilerServices.MethodImpl(Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)>
        Friend Shared Sub ApplyCalibrationShadowTintEncoded(ByRef r As Single, ByRef g As Single, ByRef b As Single,
                                                            response As Single, darkResponse As Single)
            Dim brightness = 0.299F * r + 0.587F * g + 0.114F * b
            Dim weight = ShadowTintStrength * ShadowTintWeight(brightness)
            Dim strength = response / 100.0F * weight
            Dim dr = ShadowTintRed * strength
            Dim db = ShadowTintBlue * strength
            Dim dark = darkResponse * weight
            r = Clamp(r + dr - dark, 0.0F, 1.0F)
            b = Clamp(b + db - dark, 0.0F, 1.0F)
            g = Clamp(g - (0.299F * dr + 0.114F * db) / 0.587F - dark, 0.0F, 1.0F)
        End Sub
    End Class

    ''' <summary>Die spaete Schattentoenung des Kalibrierungs-Panels, auf RenderedLinear.
    ''' Ihre Kennlinie ist auf KODIERTES sRGB/Rec.601 geeicht. In der vollstaendigen Kette
    ''' gehoert sie nach dem HSL/Punktfarbe-/ggf. Kurven-/Grading-/Restore-Block und vor
    ''' Kanalmixer, Verlauf und Preset/LUT;
    ''' sie darf NICHT mit der fruehen WB-/Primaerfarbenmatrix verschmolzen werden.</summary>
    Friend NotInheritable Class CalibrationShadowTint
        Private Sub New()
        End Sub

        Friend Shared Function Apply(source As LinearWorkingFrame, slider As Single) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Shadow tint requires a rendered-linear source.", NameOf(source))
            If Not Single.IsFinite(slider) OrElse slider < -100 OrElse slider > 100 Then Throw New ArgumentOutOfRangeException(NameOf(slider))
            Dim parameters = ImageProcessor.CalibrationShadowTintParameters(slider)
            Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain,
                Sub(y, row)
                    source.ReadLinearRow(y, row)
                    If slider = 0 Then Return
                    For x = 0 To source.Width - 1
                        Dim at = x * 4
                        Dim alpha = CDbl(row(at + 3))
                        If alpha <= 0 Then Continue For
                        Dim r = row(at) / alpha, g = row(at + 1) / alpha, b = row(at + 2) / alpha
                        Dim sr = 1.660491002 * r - 0.587641139 * g - 0.072849863 * b
                        Dim sg = -0.124550475 * r + 1.132899897 * g - 0.008349422 * b
                        Dim sb = -0.018150763 * r - 0.100578898 * g + 1.118729661 * b
                        Dim er = CSng(PointColorMath.SrgbEncode(Math.Clamp(sr, 0.0, 1.0)))
                        Dim eg = CSng(PointColorMath.SrgbEncode(Math.Clamp(sg, 0.0, 1.0)))
                        Dim eb = CSng(PointColorMath.SrgbEncode(Math.Clamp(sb, 0.0, 1.0)))
                        Dim originalR = er, originalG = eg, originalB = eb
                        ImageProcessor.ApplyCalibrationShadowTintEncoded(er, eg, eb, parameters.Response, parameters.Dark)
                        If er = originalR AndAlso eg = originalG AndAlso eb = originalB Then Continue For
                        ' Nur das Delta der geeichten SDR-Operation zuruecktransformieren.
                        ' So bleiben signed/wide/HDR-Ausgangswerte ausserhalb ihres Messbereichs
                        ' erhalten, statt fuer dieses Werkzeug das ganze Arbeitsbild zu klemmen.
                        Dim dr = PointColorMath.SrgbDecode(er) - PointColorMath.SrgbDecode(originalR)
                        Dim dg = PointColorMath.SrgbDecode(eg) - PointColorMath.SrgbDecode(originalG)
                        Dim db = PointColorMath.SrgbDecode(eb) - PointColorMath.SrgbDecode(originalB)
                        row(at) += CSng((0.627403896 * dr + 0.329283038 * dg + 0.043313066 * db) * alpha)
                        row(at + 1) += CSng((0.069097289 * dr + 0.919540395 * dg + 0.011362316 * db) * alpha)
                        row(at + 2) += CSng((0.016391439 * dr + 0.088013308 * dg + 0.895595253 * db) * alpha)
                    Next
                End Sub)
        End Function
    End Class
End Namespace
