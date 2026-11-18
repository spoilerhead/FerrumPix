Imports System

Namespace Services
    Partial Public NotInheritable Class RawDecodeService
        ''' <summary>Die vorhandene Rosa-Lichter-Korrektur auf dem neuen Szenenbild, VOR der
        ''' Benutzerbelichtung. Ihre Schwellen sind auf lineares sRGB im normierten RAW-Cache
        ''' geeicht, nicht auf Rec.2020 oder schon abgedunkelte Werte. Deshalb Normierung und
        ''' Primaerfarben nur fuer die Messung/Korrektur zuruecknehmen; kein sRGB-Zwischenbild.</summary>
        Friend Shared Function RepairSceneHighlights(source As SceneSource) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            Dim frame = source.Frame
            If frame.Domain <> WorkingImageDomain.SceneLinear Then Throw New ArgumentException("Highlight repair requires an unexposed scene source.", NameOf(source))
            If Not Double.IsFinite(source.Normalization) OrElse source.Normalization <= 0 OrElse source.Normalization > Single.MaxValue Then
                Throw New ArgumentOutOfRangeException(NameOf(source), "A positive finite RAW normalization is required.")
            End If
            Dim normalization = CSng(source.Normalization)
            If normalization = 0 Then Throw New ArgumentOutOfRangeException(NameOf(source))

            Return LinearWorkingFrame.Create(frame.Width, frame.Height, WorkingImageDomain.SceneLinear,
                Sub(y, row)
                    frame.ReadLinearRow(y, row)
                    For x = 0 To frame.Width - 1
                        Dim at = x * 4
                        Dim alpha = row(at + 3)
                        If alpha <= 0 Then Continue For
                        Dim r = row(at) / alpha / normalization
                        Dim g = row(at + 1) / alpha / normalization
                        Dim b = row(at + 2) / alpha / normalization
                        Dim sr = 1.660491002F * r - 0.587641139F * g - 0.072849863F * b
                        Dim sg = -0.124550475F * r + 1.132899897F * g - 0.008349422F * b
                        Dim sb = -0.018150763F * r - 0.100578898F * g + 1.118729661F * b
                        Dim originalR = sr, originalG = sg, originalB = sb
                        RecoverMagentaHighlightFloat(sr, sg, sb)
                        If sr = originalR AndAlso sg = originalG AndAlso sb = originalB Then Continue For

                        ' Nur das Korrekturdelta zuruecktransformieren. Unbetroffene Schatten,
                        ' HDR-Farben und Neutrale bleiben dadurch bitgleich statt durch einen
                        ' unnoetigen Matrix-Hin-und-Rueckweg neue F16-Rundung zu bekommen.
                        Dim dr = sr - originalR, dg = sg - originalG, db = sb - originalB
                        Dim scale = normalization * alpha
                        row(at) += (0.627403896F * dr + 0.329283038F * dg + 0.043313066F * db) * scale
                        row(at + 1) += (0.069097289F * dr + 0.919540395F * dg + 0.011362316F * db) * scale
                        row(at + 2) += (0.016391439F * dr + 0.088013308F * dg + 0.895595253F * db) * scale
                    Next
                End Sub)
        End Function
    End Class
End Namespace
