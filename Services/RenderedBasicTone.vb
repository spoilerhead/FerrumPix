Imports System

Namespace Services
    ''' <summary>Isolierte Grund-Tonstufe fuer entwickelte Bilder: Belichtung/Helligkeit
    ''' je Kanal, danach Kontrast ueber die gemeinsame Min/Max-Farbtonregel. Die auf
    ''' kodiertem sRGB geeichten Kurven bleiben unveraendert; das Arbeitsbild bleibt
    ''' lineares Rec.2020 F16. Nur das lineare Effekt-Delta geht zur Quelle zurueck.</summary>
    Friend NotInheritable Class RenderedBasicTone
        Private Sub New()
        End Sub

        ''' <summary>renderedExposureEv ist NUR fuer entwickelte Quellen/Legacy-Semantik.
        ''' Bei RAW muss die Benutzerbelichtung zuvor durch RawSceneDevelopment gehen;
        ''' hier dann 0 uebergeben. Ein RenderedLinear-Frame allein traegt keine Herkunft,
        ''' daher ist das eine explizite Aufruferpflicht, keine automatische RAW-Erkennung.
        ''' Noch keine Lichter/Tiefen/Weiss/Schwarz, Kurven, Masken oder Filmnegative.
        ''' Weitere Tonzonen muessen ihre Huebe vor EINER gemeinsamen Min/Max-Stufe mischen.</summary>
        Friend Shared Function Apply(source As LinearWorkingFrame, brightness As Single,
                                     Optional contrast As Single = 0,
                                     Optional renderedExposureEv As Double = 0) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Basic tone requires rendered-linear input.", NameOf(source))
            If Not Single.IsFinite(brightness) OrElse brightness < -100 OrElse brightness > 100 Then Throw New ArgumentOutOfRangeException(NameOf(brightness))
            If Not Single.IsFinite(contrast) OrElse contrast < -100 OrElse contrast > 100 Then Throw New ArgumentOutOfRangeException(NameOf(contrast))
            If Not Double.IsFinite(renderedExposureEv) OrElse renderedExposureEv < -4 OrElse renderedExposureEv > 4 Then Throw New ArgumentOutOfRangeException(NameOf(renderedExposureEv))
            Dim adjustments As New ImageAdjustments With {
                .Brightness = brightness, .Contrast = contrast, .Exposure = CSng(renderedExposureEv * 25.0)}
            Dim scalar = If(brightness = 0 AndAlso renderedExposureEv = 0, Nothing,
                            ImageProcessor.BuildPointOpScalarTable(adjustments, True, False))
            Dim zones = If(contrast = 0, Nothing, ImageProcessor.BuildToneZoneTable(adjustments))

            Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain,
                Sub(y, row)
                    source.ReadLinearRow(y, row)
                    ' Neutral muss auch HDR, negative Kanaele und winzige Schatten bitgleich
                    ' erhalten: nicht einmal eine unnoetige Farbraum-Rundreise ausfuehren.
                    If scalar Is Nothing AndAlso zones Is Nothing Then Return
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

                        ' Per-Kanal-Belichtung/Helligkeit VOR der Farbton-erhaltenden
                        ' Kontrastzone. Beide im selben Durchlauf: kein zweites F16-Runden,
                        ' kein erneutes Klemmen einer schon veraenderten Zwischenprojektion.
                        If scalar IsNot Nothing Then
                            er = ImageProcessor.SampleTable(scalar, er)
                            eg = ImageProcessor.SampleTable(scalar, eg)
                            eb = ImageProcessor.SampleTable(scalar, eb)
                        End If
                        ' Dieselbe Farbtonregel wie im Editor: Kurve auf Minimum/Maximum,
                        ' den mittleren Kanal dazwischen. Keine RGB8-Quantisierung.
                        If zones IsNot Nothing Then ImageProcessor.ApplyToneZones(er, eg, eb, zones)
                        If er = originalR AndAlso eg = originalG AndAlso eb = originalB Then Continue For

                        ' Die SDR-Kalibrierung definiert nur den Effekt im Einheitswuerfel.
                        ' Nur dessen LINEARES Delta zur Quelle addieren, wie beim spaeten
                        ' Shadow-Tint-Adapter. Das gesamte wide/signed/HDR-Arbeitsbild durch
                        ' den begrenzten Messraum zu schicken, wuerde seine Reserve zerstoeren.
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
