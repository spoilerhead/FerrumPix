Imports System

Namespace Services
    ''' <summary>Explizite, diagnostische Grenze Szene -> entwickeltes Bild. Die Ablage bleibt
    ''' lineares Rec.2020 D65; nur die Tonoperation benutzt voruebergehend ProPhoto wie die
    ''' vorhandene RAW-Stufe. Noch kein Ersatz fuer den normalen Decoder oder dessen Korrekturen.</summary>
    Friend NotInheritable Class SceneFrameRenderer
        Private Sub New()
        End Sub

        Private Shared ReadOnly RecToSrgb As Double() = {
            1.660491002, -0.587641139, -0.072849863,
            -0.124550475, 1.132899897, -0.008349422,
            -0.018150763, -0.100578898, 1.118729661}
        Private Shared ReadOnly SrgbToRec As Double() = {
            0.627403896, 0.329283038, 0.043313066,
            0.069097289, 0.919540395, 0.011362316,
            0.016391439, 0.088013308, 0.895595253}
        ' PointColorMath enthaelt die chromatische Adaption D65 <-> D50 bereits.
        ' Zusammengesetzte Matrizen vermeiden ein gespeichertes sRGB-Zwischenbild.
        Private Shared ReadOnly ToTone As Single() = Compose(PointColorMath.SrgbToProPhoto, RecToSrgb)
        Private Shared ReadOnly FromTone As Single() = Compose(SrgbToRec, PointColorMath.ProPhotoToSrgb)

        Private Shared Function Compose(left As Double(), right As Double()) As Single()
            Dim result(8) As Single
            For r = 0 To 2
                For c = 0 To 2
                    Dim value As Double = 0
                    For k = 0 To 2
                        value += left(r * 3 + k) * right(k * 3 + c)
                    Next
                    result(r * 3 + c) = CSng(value)
                Next
            Next
            Return result
        End Function

        ''' <summary>Die Kamera-Grundbelichtung wird hier einmal angewandt. Benutzerbelichtung
        ''' gehoert davor auf die Szene (SceneFrameOperations); die Cache-Normierung ist dort
        ''' schon wiederhergestellt und darf hier NICHT erneut einwirken.
        ''' Schwarzfuss, Schulter und ACR3 entsprechen der bisherigen Tonkurvenform, werden aber
        ''' ohne 16-Bit-Ausgangstabelle gerechnet. Dies ist SDR-Rendering, keine HDR-Ausgabe.</summary>
        Friend Shared Function Render(source As LinearWorkingFrame, baseExposureEv As Double,
                                      Optional blackLevel As Double = 0.003,
                                      Optional knee As Double = RawDecodeService.HighlightRecoveryKnee,
                                      Optional whiteBalance As RawWhiteBalanceSettings = Nothing,
                                      Optional primaryCalibration As RawPrimaryCalibrationSettings = Nothing) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.SceneLinear Then Throw New ArgumentException("Rendering requires a scene-linear source.", NameOf(source))
            If Not Double.IsFinite(baseExposureEv) OrElse baseExposureEv < -10 OrElse baseExposureEv > 10 Then Throw New ArgumentOutOfRangeException(NameOf(baseExposureEv))
            If Not Double.IsFinite(blackLevel) OrElse blackLevel < 0 Then Throw New ArgumentOutOfRangeException(NameOf(blackLevel))
            If Not Double.IsFinite(knee) OrElse knee < 0 OrElse knee > 1 Then Throw New ArgumentOutOfRangeException(NameOf(knee))

            Dim white = 2.0 ^ -baseExposureEv
            If blackLevel >= white Then Throw New ArgumentOutOfRangeException(NameOf(blackLevel), "Black must be below the exposure white point.")
            Dim black = CSng(blackLevel)
            Dim slope = CSng(1.0 / (white - blackLevel))
            Dim radius = Math.Min(0.5F * black, (1.0F / 16.0F) / slope)
            Dim qScale = If(radius > 0, slope / (4.0F * radius), 0.0F)
            Dim shoulder = CSng(knee)
            Dim rampMatrix = whiteBalance?.CopyToneMatrix()
            If primaryCalibration IsNot Nothing Then rampMatrix = primaryCalibration.CombineAfter(rampMatrix)

            Return LinearWorkingFrame.Create(source.Width, source.Height, WorkingImageDomain.RenderedLinear,
                Sub(y, row)
                    source.ReadLinearRow(y, row)
                    For x = 0 To source.Width - 1
                        Dim at = x * 4
                        Dim alpha = row(at + 3)
                        If alpha <= 0 Then Continue For
                        Dim r = row(at) / alpha, g = row(at + 1) / alpha, b = row(at + 2) / alpha
                        ' Erst am benannten Rendering-Uebergang negative ProPhoto-Werte begrenzen,
                        ' weil die SDR-Tonkurve nur nichtnegative Intensitaeten abbildet. Die Szene
                        ' bleibt unveraendert; Rec.2020-Ausgangswerte werden NICHT kanalweise geklemmt.
                        Dim pr = Math.Max(0.0F, ToTone(0) * r + ToTone(1) * g + ToTone(2) * b)
                        Dim pg = Math.Max(0.0F, ToTone(3) * r + ToTone(4) * g + ToTone(5) * b)
                        Dim pb = Math.Max(0.0F, ToTone(6) * r + ToTone(7) * g + ToTone(8) * b)
                        If rampMatrix IsNot Nothing Then
                            ' WB und danach Primaerfarben wirken auf der BELICHTUNGSRAMPE vor Schulter/ACR3,
                            ' nicht vor Schwarzabzug und auch nicht auf schon entwickeltem RGB.
                            ' Die alte Kette musste die Kurve invertieren; hier liegt die Rampe
                            ' direkt vor. Keine inverse LUT und kein kuenstlicher 4x-Weiss-Deckel.
                            Dim hi = Math.Max(pr, Math.Max(pg, pb)), lo = Math.Min(pr, Math.Min(pg, pb))
                            Dim topRamp = Toe(hi, black, slope, radius, qScale)
                            Dim bottomRamp = Toe(lo, black, slope, radius, qScale)
                            Dim rampR = topRamp, rampG = topRamp, rampB = topRamp
                            If hi - lo >= 0.0000001F Then
                                Dim k = (topRamp - bottomRamp) / (hi - lo)
                                rampR = bottomRamp + (pr - lo) * k
                                rampG = bottomRamp + (pg - lo) * k
                                rampB = bottomRamp + (pb - lo) * k
                            End If
                            pr = Math.Max(0.0F, rampMatrix(0) * rampR + rampMatrix(1) * rampG + rampMatrix(2) * rampB)
                            pg = Math.Max(0.0F, rampMatrix(3) * rampR + rampMatrix(4) * rampG + rampMatrix(5) * rampB)
                            pb = Math.Max(0.0F, rampMatrix(6) * rampR + rampMatrix(7) * rampG + rampMatrix(8) * rampB)
                        End If
                        Dim high = Math.Max(pr, Math.Max(pg, pb))
                        Dim low = Math.Min(pr, Math.Min(pg, pb))
                        Dim top = If(rampMatrix Is Nothing, Tone(high, black, slope, radius, qScale, shoulder), Curve(high, shoulder))
                        Dim bottom = If(rampMatrix Is Nothing, Tone(low, black, slope, radius, qScale, shoulder), Curve(low, shoulder))

                        ' RGBTone statt dreier unabhaengiger Kurven: die Lage des mittleren
                        ' Kanals bleibt zwischen hellstem und dunkelstem proportional erhalten.
                        Dim qr = top, qg = top, qb = top
                        If high - low >= 0.0000001F Then
                            Dim scale = (top - bottom) / (high - low)
                            qr = bottom + (pr - low) * scale
                            qg = bottom + (pg - low) * scale
                            qb = bottom + (pb - low) * scale
                        End If
                        row(at) = (FromTone(0) * qr + FromTone(1) * qg + FromTone(2) * qb) * alpha
                        row(at + 1) = (FromTone(3) * qr + FromTone(4) * qg + FromTone(5) * qb) * alpha
                        row(at + 2) = (FromTone(6) * qr + FromTone(7) * qg + FromTone(8) * qb) * alpha
                    Next
                End Sub)
        End Function

        Private Shared Function Toe(value As Single, black As Single, slope As Single, radius As Single, qScale As Single) As Single
            If value <= black - radius Then Return 0
            If value >= black + radius Then Return (value - black) * slope
            Dim distance = value - (black - radius)
            Return qScale * distance * distance
        End Function

        Private Shared Function Curve(value As Single, knee As Single) As Single
            Return CSng(RawDecodeService.Acr3CurveAt(RawDecodeService.ShoulderAt(value, knee)))
        End Function

        Private Shared Function Tone(value As Single, black As Single, slope As Single,
                                     radius As Single, qScale As Single, knee As Single) As Single
            Dim v As Single
            If value <= black - radius Then
                v = 0
            ElseIf value < black + radius Then
                Dim distance = value - (black - radius)
                v = qScale * distance * distance
            Else
                v = CSng(RawDecodeService.ShoulderAt((value - black) * slope, knee))
            End If
            ' Die vorhandenen 129 ACR3-Stuetzstellen sind die Definition des Looks.
            ' Sie wiederverwenden, ohne den Wert auf Integer16 zu runden.
            Return CSng(RawDecodeService.Acr3CurveAt(v))
        End Function
    End Class
End Namespace
