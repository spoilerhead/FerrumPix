Imports System
Imports SkiaSharp

Namespace Services
    Partial Public Class ImageProcessor
        ' Geteilte Nachstufe: die Extraktion laesst die Alt-Rechnung bitgleich und
        ' verhindert eine zweite Wahrheit fuer hue-abhaengige Dynamik/Leuchtdichte.
        Private Shared Sub ApplyBasicVibrance(ByRef h As Double, ByRef sat As Double, ByRef lum As Double,
                                             vibrance As Single, vibranceExponential As Boolean,
                                             vibranceByHue As Single(), toLinear As Single(), toGamma As Single())
            If vibranceExponential Then
                sat = sat * Math.Exp(vibrance * (1.0 - sat))
            ElseIf vibranceByHue IsNot Nothing Then
                Dim v = SampleVibranceByHue(vibranceByHue, h)
                Dim satNew = Math.Max(0.0, Math.Min(1.0, sat * (1.0 + v * (1.0 - sat))))
                If satNew <> sat Then
                    Dim ar As Double, ag As Double, ab As Double
                    Dim br As Double, bg As Double, bbv As Double
                    HslToRgbF(h, sat, lum, ar, ag, ab)
                    HslToRgbF(h, satNew, lum, br, bg, bbv)
                    Dim lr = SampleTable(toLinear, CSng(br))
                    Dim lg = SampleTable(toLinear, CSng(bg))
                    Dim lb = SampleTable(toLinear, CSng(bbv))
                    Dim y0 = 0.2126 * SampleTable(toLinear, CSng(ar)) +
                             0.7152 * SampleTable(toLinear, CSng(ag)) +
                             0.0722 * SampleTable(toLinear, CSng(ab))
                    Dim y1 = 0.2126 * lr + 0.7152 * lg + 0.0722 * lb
                    If y1 > 0.000001 Then
                        Dim k = y0 / y1
                        br = SampleTable(toGamma, Clamp(CSng(lr * k), 0.0F, 1.0F))
                        bg = SampleTable(toGamma, Clamp(CSng(lg * k), 0.0F, 1.0F))
                        bbv = SampleTable(toGamma, Clamp(CSng(lb * k), 0.0F, 1.0F))
                    End If
                    RgbToHslF(br, bg, bbv, h, sat, lum)
                End If
            ElseIf vibrance <> 0.0F Then
                sat = Math.Max(0.0, Math.Min(1.0, sat * (1.0 + vibrance * (1.0 - sat))))
            End If
        End Sub

        Private Shared Function ToneReferenceFromHistogram(histogram As Long(), total As Long) As ToneReference
            If total = 0 Then Return Nothing
            Dim half = total \ 2
            Dim topCount = CLng(Math.Ceiling(total * ToneTopFraction))
            Dim result As New ToneReference With {.MedianL = 100.0, .TopL = 100.0, .Histogram = histogram, .Total = total}
            ' Ueber der Grenze heisst: Fach k mit k / 10 > Grenze, wie im Messwerkzeug (L > 70).
            Dim bright = 0L
            For k = CInt(Math.Floor(ToneSliderCurves.BrightShareLimitL * 10.0)) + 1 To 1000
                bright += histogram(k)
            Next
            result.BrightShare = 100.0 * bright / total
            Dim medianFound = False
            Dim acc = 0L
            For k = 0 To 1000
                acc += histogram(k)
                If Not medianFound AndAlso acc > half Then
                    result.MedianL = Math.Max(0.05, k / 10.0)
                    medianFound = True
                End If
                If acc >= topCount Then
                    result.TopL = Math.Max(0.05, k / 10.0)
                    Exit For
                End If
            Next
            Return result
        End Function
        ''' <summary>Shared pixel stages; only the input reader/analysis is new.</summary>
        Friend NotInheritable Class FloatingBasicStage
            Private ReadOnly _chain As PointOpChain
            Private ReadOnly _stages As PreMixerStages
            Friend ReadOnly Reference As ToneReference
            Friend ReadOnly Grid As LocalToneGrid

            Friend Sub New(chain As PointOpChain, reference As ToneReference, grid As LocalToneGrid)
                _chain = chain
                Me.Reference = reference
                Me.Grid = grid
                Dim local = If(grid Is Nothing, Nothing, LocalToneBaseFromGrid(grid, True))
                _stages = New PreMixerStages(chain, local, _srgbToLinearTable, _linearToSrgbTable)
            End Sub

            Friend Sub Apply(ByRef r As Single, ByRef g As Single, ByRef b As Single, x As Integer, y As Integer)
                Dim restore = _stages.Apply(r, g, b, x, y)
                ' Intervening curves/HSL tools are neutral in this restricted API. The
                ' existing late shadow-color restore therefore precedes vibrance here.
                If _chain.Vibrance <> 0 Then
                    Dim h, sat, lum As Double
                    RgbToHslF(r, g, b, h, sat, lum)
                    If sat < restore Then sat = restore
                    ApplyBasicVibrance(h, sat, lum, _chain.Vibrance, _chain.VibranceExponential,
                                       _chain.VibranceByHue, _srgbToLinearTable, _linearToSrgbTable)
                    Dim rr, gg, bb As Double
                    HslToRgbF(h, sat, lum, rr, gg, bb)
                    r = CSng(rr) : g = CSng(gg) : b = CSng(bb)
                ElseIf restore > 0 Then
                    Dim h, sat, lum As Double
                    RgbToHslF(r, g, b, h, sat, lum)
                    If sat < restore Then
                        Dim rr, gg, bb As Double
                        HslToRgbF(h, restore, lum, rr, gg, bb)
                        r = CSng(rr) : g = CSng(gg) : b = CSng(bb)
                    End If
                End If
            End Sub

            Friend Sub SaturateProjection(ByRef r As Single, ByRef g As Single, ByRef b As Single)
                ApplyBasicColorMatrix(r, g, b, _chain.ColorMatrix)
            End Sub

            Friend Sub SaturateResidual(ByRef r As Double, ByRef g As Double, ByRef b As Double)
                ' This restricted matrix contains ONLY saturation, with zero offsets.
                ' Apply its same encoded-domain linear map to the extended-range residue.
                Dim m = _chain.ColorMatrix
                If m Is Nothing Then Return
                Dim rr = m(0) * r + m(1) * g + m(2) * b
                Dim gg = m(5) * r + m(6) * g + m(7) * b
                Dim bb = m(10) * r + m(11) * g + m(12) * b
                r = rr : g = gg : b = bb
            End Sub
        End Class

        Private Shared Sub ApplyBasicColorMatrix(ByRef r As Single, ByRef g As Single, ByRef b As Single, m As Single())
            If m Is Nothing Then Return
            Dim nr = m(0) * r + m(1) * g + m(2) * b + m(4)
            Dim ng = m(5) * r + m(6) * g + m(7) * b + m(9)
            Dim nb = m(10) * r + m(11) * g + m(12) * b + m(14)
            r = If(nr < 0.0F, 0.0F, If(nr > 1.0F, 1.0F, nr))
            g = If(ng < 0.0F, 0.0F, If(ng > 1.0F, 1.0F, ng))
            b = If(nb < 0.0F, 0.0F, If(nb > 1.0F, 1.0F, nb))
        End Sub

        Friend Shared Function PrepareFloatingBasic(width As Integer, height As Integer, pixels As Single(),
                                                    settings As RenderedBasicSettings) As FloatingBasicStage
            ArgumentNullException.ThrowIfNull(pixels)
            ArgumentNullException.ThrowIfNull(settings)
            If width <= 0 OrElse height <= 0 OrElse CLng(width) * height > Integer.MaxValue \ 16 OrElse
               CLng(width) * height * 4 <> pixels.LongLength Then Throw New ArgumentException("Floating RGBA dimensions exceed staging limits or differ.")
            For Each value In pixels
                If Not Single.IsFinite(value) OrElse value < 0 OrElse value > 1 Then Throw New ArgumentException("Expected bounded straight encoded-sRGB RGBA F32.")
            Next
            EnsureGammaTables()
            Dim bounds = FloatingVisibleBounds(width, height, pixels)
            Dim reference = MeasureFloatingTone(width, height, pixels, bounds)
            Dim chain = BuildPointOpChain(settings.ToAdjustments(), toneReference:=reference)
            Dim grid = If(chain.LocalToneZones Is Nothing, Nothing, FloatingToneGrid(width, height, pixels, bounds, chain))
            Return New FloatingBasicStage(chain, reference, grid)
        End Function

        Private Shared Function FloatingVisibleBounds(width As Integer, height As Integer, pixels As Single()) As SKRectI
            Dim left = width, right = -1, top = height, bottom = -1
            For y = 0 To height - 1
                For x = 0 To width - 1
                    If pixels((y * width + x) * 4 + 3) <= 0 Then Continue For
                    left = Math.Min(left, x) : right = Math.Max(right, x)
                    top = Math.Min(top, y) : bottom = Math.Max(bottom, y)
                Next
            Next
            Return If(right < left, SKRectI.Empty, New SKRectI(left, top, right + 1, bottom + 1))
        End Function

        Private Shared Function MeasureFloatingTone(width As Integer, height As Integer, pixels As Single(), bounds As SKRectI) As ToneReference
            If bounds.IsEmpty Then Return Nothing
            Dim stepSize = Math.Max(1, CInt(Math.Sqrt(CDbl(bounds.Width) * bounds.Height / 250000.0)))
            Do
                Dim histogram(1000) As Long
                Dim total = 0L
                Dim cellRow = 0
                For yCell = bounds.Top To bounds.Bottom - 1 Step stepSize
                    Dim y = If(stepSize > 1, Math.Min(bounds.Bottom - 1, yCell + RasterJitter(cellRow, -1, stepSize)), yCell)
                    Dim cellColumn = 0
                    For xCell = bounds.Left To bounds.Right - 1 Step stepSize
                        Dim x = If(stepSize > 1, Math.Min(bounds.Right - 1, xCell + RasterJitter(cellColumn, cellRow, stepSize)), xCell)
                        Dim at = (y * width + x) * 4
                        If pixels(at + 3) > 0 Then
                            ' Keep calibrated sRGB luminance/CIE L* bins. Rec.2020 luma
                            ' would select different response curves, not improve precision.
                            Dim lum = 0.2126 * PointColorMath.SrgbDecode(pixels(at)) +
                                      0.7152 * PointColorMath.SrgbDecode(pixels(at + 1)) +
                                      0.0722 * PointColorMath.SrgbDecode(pixels(at + 2))
                            Dim l = If(lum > 0.008856, 116.0 * Math.Pow(lum, 1.0 / 3.0) - 16.0, 903.3 * lum)
                            histogram(Math.Clamp(CInt(l * 10.0), 0, 1000)) += 1
                            total += 1
                        End If
                        cellColumn += 1
                    Next
                    cellRow += 1
                Next
                Dim points = CLng((bounds.Height + stepSize - 1) \ stepSize) * ((bounds.Width + stepSize - 1) \ stepSize)
                If stepSize > 1 AndAlso total * 4 < points Then
                    stepSize = 1
                    Continue Do
                End If
                Return ToneReferenceFromHistogram(histogram, total)
            Loop
        End Function

        Private Shared Function FloatingToneGrid(width As Integer, height As Integer, pixels As Single(), bounds As SKRectI,
                                                  chain As PointOpChain) As LocalToneGrid
            If bounds.IsEmpty Then bounds = New SKRectI(0, 0, width, height)
            Dim edge = Math.Max(bounds.Width, bounds.Height)
            Dim stepSize = Math.Max(1, CInt(Math.Ceiling(edge / CDbl(LocalToneGridEdge))))
            Dim gw = (bounds.Width + stepSize - 1) \ stepSize
            Dim gh = (bounds.Height + stepSize - 1) \ stepSize
            Dim gray(gw * gh - 1) As Single, coverage(gw * gh - 1) As Single
            For gy = 0 To gh - 1
                Dim yStart = bounds.Top + gy * stepSize, yEnd = Math.Min(bounds.Bottom, yStart + stepSize)
                For gx = 0 To gw - 1
                    Dim xStart = bounds.Left + gx * stepSize, xEnd = Math.Min(bounds.Right, xStart + stepSize)
                    Dim sr = 0.0F, sg = 0.0F, sb = 0.0F, alpha = 0.0F
                    For y = yStart To yEnd - 1
                        For x = xStart To xEnd - 1
                            Dim at = (y * width + x) * 4, weight = pixels(at + 3)
                            sr += pixels(at) * weight : sg += pixels(at + 1) * weight : sb += pixels(at + 2) * weight
                            alpha += weight
                        Next
                    Next
                    If alpha > 0 Then
                        sr /= alpha : sg /= alpha : sb /= alpha
                        ' Mean encoded color first, then saturation/scalar stages, then
                        ' linear luminance. Linear Rec.2020 averaging changes this filter.
                        ApplyStagesBeforeToneZones(sr, sg, sb, chain, _srgbToLinearTable, _linearToSrgbTable)
                        gray(gy * gw + gx) = LocalToneGray(sr, sg, sb, _srgbToLinearTable, _linearToSrgbTable)
                    End If
                    coverage(gy * gw + gx) = alpha / ((xEnd - xStart) * (yEnd - yStart))
                Next
            Next
            Dim sigmaPixels = LocalToneDetailSigma * edge
            Dim variance = Math.Max(0.0, (sigmaPixels * sigmaPixels - (CDbl(stepSize) * stepSize - 1.0) / 12.0) / (CDbl(stepSize) * stepSize))
            Return New LocalToneGrid With {.Gray = gray, .Coverage = coverage, .Width = gw, .Height = gh,
                .StepSize = stepSize, .OriginX = bounds.Left, .OriginY = bounds.Top,
                .Radius = LocalToneRadius * edge / stepSize, .SmoothRadius = FractionalBoxRadiusForVariance(variance / 3.0)}
        End Function
    End Class
End Namespace
