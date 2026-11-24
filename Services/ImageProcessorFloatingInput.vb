Imports System
Imports SkiaSharp

Namespace Services
    Friend NotInheritable Class RenderedInputControls
        Private Sub New()
        End Sub
        Friend Shared Function Apply(source As LinearWorkingFrame,
                                     Optional whiteBalance As RenderedWhiteBalanceSettings = Nothing,
                                     Optional negative As RenderedNegativeSettings = Nothing) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Rendered input required.", NameOf(source))
            Dim wb = If(whiteBalance, New RenderedWhiteBalanceSettings())
            Dim film = If(negative, New RenderedNegativeSettings())
            If wb.IsIdentity AndAlso Not film.Enabled Then Return RenderedBasicTone.Apply(source, 0)
            Return ImageProcessor.ApplyRenderedInputWorking(source, wb, film)
        End Function
    End Class

    Partial Public Class ImageProcessor
        Friend Shared Function RenderedLegacyWhiteBalanceMatrix(temperature As Single, tint As Single) As Single()
            Return BuildPointOpColorMatrix(New ImageAdjustments With {
                .WhiteBalanceModel = 1, .Temperature = temperature, .Tint = tint,
                .SourceFollowsRawExposure = False})
        End Function

        Friend Shared Function ExplicitNegativeTables(film As RenderedNegativeSettings) As Single()()
            Dim gamma = CSng(Math.Pow(2.0, film.Gamma / 100.0))
            If film.HasMeasuredCalibration Then
                Dim bases = film.CopyMeasuredBase(), densities = film.CopyMeasuredDensity()
                Dim tables(2)() As Single
                For c = 0 To 2
                    Dim span = Math.Log(bases(c) / Math.Max(FloatingFilmMeasurement.LogFloor, densities(c)))
                    Dim table(PointOpTableSize - 1) As Single
                    For i = 0 To table.Length - 1
                        ' Same log-density inversion as the explicit swatch path,
                        ' without rounding calibration to bytes or imposing a one-code span.
                        Dim level = Math.Max(FloatingFilmMeasurement.LogFloor, i / CDbl(table.Length - 1))
                        Dim t = Math.Clamp(Math.Log(bases(c) / level) / span, 0.0, 1.0)
                        table(i) = CSng(Math.Pow(t, 1.0 / Math.Max(0.05, CDbl(gamma))))
                    Next
                    tables(c) = table
                Next
                Return tables
            End If
            Return {
                BuildPointOpFilmNegativeTable(film.BaseColor.Red, film.DensityColor.Red, gamma),
                BuildPointOpFilmNegativeTable(film.BaseColor.Green, film.DensityColor.Green, gamma),
                BuildPointOpFilmNegativeTable(film.BaseColor.Blue, film.DensityColor.Blue, gamma)}
        End Function

        Friend Shared Function ApplyRenderedInputWorking(source As LinearWorkingFrame, wb As RenderedWhiteBalanceSettings,
                                                        film As RenderedNegativeSettings) As LinearWorkingFrame
            Dim linear = wb.CopyLinearMatrix(), encoded = wb.CopyEncodedMatrix()
            Dim tables = If(film.Enabled, ExplicitNegativeTables(film), Nothing)
            Dim direct = If(Not film.Enabled AndAlso linear IsNot Nothing, RenderedInputWorkingMatrix(linear), Nothing)
            Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain,
                Sub(y, row)
                    source.ReadLinearRow(y, row)
                    For x = 0 To source.Width - 1
                        Dim at = x * 4, alpha = CDbl(row(at + 3))
                        If alpha <= 0 Then Continue For
                        If direct IsNot Nothing Then
                            ' A linear matrix commutes with premultiplication. Keep
                            ' alpha and HDR/signed data without a per-pixel roundtrip.
                            Dim rr = CDbl(row(at)), gg = CDbl(row(at + 1)), bb = CDbl(row(at + 2))
                            row(at) = CSng(direct(0) * rr + direct(1) * gg + direct(2) * bb)
                            row(at + 1) = CSng(direct(3) * rr + direct(4) * gg + direct(5) * bb)
                            row(at + 2) = CSng(direct(6) * rr + direct(7) * gg + direct(8) * bb)
                            Continue For
                        End If
                        Dim originalR = row(at) / alpha, originalG = row(at + 1) / alpha, originalB = row(at + 2) / alpha
                        Dim r = originalR, g = originalG, b = originalB
                        RenderedInputToSrgb(r, g, b)

                        If tables IsNot Nothing OrElse encoded IsNot Nothing Then
                            ' Negative inversion is a deliberately bounded scan operator.
                            ' Legacy WB instead returns only its SDR delta to the original
                            ' wide/HDR baseline. Neither needs signed gamma extrapolation.
                            Dim pr = CSng(PointColorMath.SrgbEncode(Math.Clamp(r, 0.0, 1.0)))
                            Dim pg = CSng(PointColorMath.SrgbEncode(Math.Clamp(g, 0.0, 1.0)))
                            Dim pb = CSng(PointColorMath.SrgbEncode(Math.Clamp(b, 0.0, 1.0)))
                            Dim beforeR = pr, beforeG = pg, beforeB = pb
                            If tables IsNot Nothing Then
                                pr = SampleTable(tables(0), pr)
                                pg = SampleTable(tables(1), pg)
                                pb = SampleTable(tables(2), pb)
                                If film.Monochrome Then
                                    Dim gray = 0.299F * pr + 0.587F * pg + 0.114F * pb
                                    pr = gray : pg = gray : pb = gray
                                End If
                            End If
                            ' WB follows film normalization/desaturation, as in the editor.
                            If encoded IsNot Nothing Then ApplyBasicColorMatrix(pr, pg, pb, encoded)
                            r = PointColorMath.SrgbDecode(pr)
                            g = PointColorMath.SrgbDecode(pg)
                            b = PointColorMath.SrgbDecode(pb)
                            If tables Is Nothing Then
                                r -= PointColorMath.SrgbDecode(beforeR)
                                g -= PointColorMath.SrgbDecode(beforeG)
                                b -= PointColorMath.SrgbDecode(beforeB)
                            End If
                        End If

                        ' Bradford WB works directly in linear light, retaining signed/HDR
                        ' channels. No old 0..1 clamp or inverse RAW tone ramp is introduced.
                        If linear IsNot Nothing Then
                            Dim nr = linear(0) * r + linear(1) * g + linear(2) * b
                            Dim ng = linear(3) * r + linear(4) * g + linear(5) * b
                            Dim nb = linear(6) * r + linear(7) * g + linear(8) * b
                            r = nr : g = ng : b = nb
                        End If
                        RenderedInputToRec2020(r, g, b)
                        If tables Is Nothing AndAlso encoded IsNot Nothing Then
                            r += originalR : g += originalG : b += originalB
                        End If
                        row(at) = CSng(r * alpha)
                        row(at + 1) = CSng(g * alpha)
                        row(at + 2) = CSng(b * alpha)
                    Next
                End Sub)
        End Function

        ' Same D65 working-boundary matrices as the accepted basic-control adapter.
        ' Keep conversion outside the scan tables and WB matrix, without an F16
        ' intermediate between negative inversion and chromatic adaptation.
        Friend Shared Function ApplyRenderedWorkingCalibration(source As LinearWorkingFrame, recipe As ImageAdjustments) As LinearWorkingFrame
            ' Rendered files use the existing post-WB linear sRGB calibration.
            ' Fuse its primaries changes so the image stays in working RGB and
            ' signed/HDR values never pass through an SDR projection.
            Dim settings = recipe.Clone()
            settings.SourceFollowsRawExposure = False
            settings.WhiteBalanceAnchorX = 0
            settings.WhiteBalanceAnchorY = 0
            Dim matrix = BuildCalibrationMatrix(settings)
            If matrix Is Nothing Then Return RenderedBasicTone.Apply(source, 0)
            Dim working = RenderedInputWorkingMatrix({matrix(0), matrix(1), matrix(2),
                                                      matrix(5), matrix(6), matrix(7),
                                                      matrix(10), matrix(11), matrix(12)})
            Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain,
                Sub(y, row)
                    source.ReadLinearRow(y, row)
                    For at = 0 To row.Length - 1 Step 4
                        Dim r = CDbl(row(at)), g = CDbl(row(at + 1)), b = CDbl(row(at + 2))
                        row(at) = CSng(working(0) * r + working(1) * g + working(2) * b)
                        row(at + 1) = CSng(working(3) * r + working(4) * g + working(5) * b)
                        row(at + 2) = CSng(working(6) * r + working(7) * g + working(8) * b)
                    Next
                End Sub)
        End Function

        Private Shared Function RenderedInputWorkingMatrix(linear As Single()) As Double()
            Dim toSrgb As Double() = {1.660491002, -0.587641139, -0.072849863, -0.124550475, 1.132899897, -0.008349422, -0.018150763, -0.100578898, 1.118729661}
            Dim toRec As Double() = {0.627403896, 0.329283038, 0.043313066, 0.069097289, 0.919540395, 0.011362316, 0.016391439, 0.088013308, 0.895595253}
            Dim intermediate(8), result(8) As Double
            ' Fuse P * WB * P^-1 once in Double, not two primaries transforms
            ' for every pixel. There is no gamma function in this linear path.
            For r = 0 To 2
                For c = 0 To 2
                    Dim value = 0.0
                    For k = 0 To 2
                        value += linear(r * 3 + k) * toSrgb(k * 3 + c)
                    Next
                    intermediate(r * 3 + c) = value
                Next
            Next
            For r = 0 To 2
                For c = 0 To 2
                    Dim value = 0.0
                    For k = 0 To 2
                        value += toRec(r * 3 + k) * intermediate(k * 3 + c)
                    Next
                    result(r * 3 + c) = value
                Next
            Next
            Return result
        End Function

        Private Shared Sub RenderedInputToSrgb(ByRef r As Double, ByRef g As Double, ByRef b As Double)
            Dim nr = 1.660491002 * r - 0.587641139 * g - 0.072849863 * b
            Dim ng = -0.124550475 * r + 1.132899897 * g - 0.008349422 * b
            Dim nb = -0.018150763 * r - 0.100578898 * g + 1.118729661 * b
            r = nr : g = ng : b = nb
        End Sub
        Private Shared Sub RenderedInputToRec2020(ByRef r As Double, ByRef g As Double, ByRef b As Double)
            Dim nr = 0.627403896 * r + 0.329283038 * g + 0.043313066 * b
            Dim ng = 0.069097289 * r + 0.919540395 * g + 0.011362316 * b
            Dim nb = 0.016391439 * r + 0.088013308 * g + 0.895595253 * b
            r = nr : g = ng : b = nb
        End Sub
    End Class
End Namespace
