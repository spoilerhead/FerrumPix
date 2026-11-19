Imports System

Namespace Services
    ''' <summary>Erster isolierter Tonzonen-Regler auf RenderedLinear: nur Kontrast.
    ''' Die gemessene Kennlinie und ihre Monotoniekorrektur gehoeren zum bisherigen
    ''' kodierten sRGB-Look, nicht zu linearem Rec.2020. Sie werden deshalb geteilt,
    ''' statt neue Luma-Koeffizienten oder eine lineare Kontrastformel einzusetzen.</summary>
    Friend NotInheritable Class RenderedContrast
        Private Sub New()
        End Sub

        ''' <summary>Nach RAW-Rendering/WB/Primaerfarben und vor Punkt-/Kanalkurven.
        ''' Sobald weitere Tonzonen migrieren, muessen ihre Huebe VOR der Anwendung
        ''' in EINER gemeinsamen Tabelle kombiniert werden: serielle Einzelregler
        ''' wuerden die gemessene Wechselwirkung und das Ergebnis veraendern.
        ''' Helligkeit, lokale Lichter/Tiefen, Masken und spaete Schattentoenung
        ''' sind hier nicht enthalten. Keine vollstaendige Editorrezept-Auswertung.</summary>
        Friend Shared Function Apply(source As LinearWorkingFrame, slider As Single) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Contrast requires rendered-linear input.", NameOf(source))
            If Not Single.IsFinite(slider) OrElse slider < -100 OrElse slider > 100 Then Throw New ArgumentOutOfRangeException(NameOf(slider))
            Dim zones = If(slider = 0, Nothing, ImageProcessor.BuildToneZoneTable(New ImageAdjustments With {.Contrast = slider}))

            Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain,
                Sub(y, row)
                    source.ReadLinearRow(y, row)
                    ' Neutral muss auch HDR, negative Kanaele und winzige Schatten bitgleich
                    ' erhalten: nicht einmal eine unnoetige Farbraum-Rundreise ausfuehren.
                    If zones Is Nothing Then Return
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

                        ' Dieselbe Farbtonregel wie im Editor: Kurve auf Minimum/Maximum,
                        ' den mittleren Kanal dazwischen. Keine RGB8-Quantisierung.
                        ImageProcessor.ApplyToneZones(er, eg, eb, zones)
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
