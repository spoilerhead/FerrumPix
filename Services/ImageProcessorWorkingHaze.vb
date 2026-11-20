Imports System

Namespace Services
    Partial Public Class ImageProcessor
        Friend Shared Sub WorkingHaze(p As WorkingEffectPlanes, amount As Single)
            Dim scale = Math.Min(1.0, DehazeWorkMaxDimension / CDbl(Math.Max(p.Width, p.Height)))
            Dim w = Math.Max(1, CInt(Math.Round(p.Width * scale))), h = Math.Max(1, CInt(Math.Round(p.Height * scale)))
            Dim rr(w * h - 1), gg(w * h - 1), bb(w * h - 1), own(w * h - 1) As Double
            For y = 0 To h - 1
                Dim top = y * p.Height \ h, bottom = (y + 1) * p.Height \ h
                For x = 0 To w - 1
                    Dim left = x * p.Width \ w, right = (x + 1) * p.Width \ w
                    Dim r = 0.0, g = 0.0, b = 0.0, minimum = 0.0, weight = 0.0
                    For sy = top To bottom - 1
                        For sx = left To right - 1
                            Dim i = sy * p.Width + sx, alpha = CDbl(p.A(i))
                            Dim sr = 0.0, sg = 0.0, sb = 0.0
                            WorkingSurveySrgb(p.R(i) / 255.0, p.G(i) / 255.0, p.B(i) / 255.0, sr, sg, sb)
                            r += sr * alpha : g += sg * alpha : b += sb * alpha
                            minimum += Math.Min(sr, Math.Min(sg, sb)) * alpha : weight += alpha
                        Next
                    Next
                    Dim at = y * w + x
                    If weight > 0 Then
                        rr(at) = r / weight : gg(at) = g / weight : bb(at) = b / weight : own(at) = minimum / weight
                    End If
                Next
            Next
            ' Atmospheric survey is calibrated in encoded sRGB. Keep that small
            ' continuous guide explicit; apply its physical transmission in native
            ' working light, never through an RGB8 image or a clipped image reserve.
            Dim strength = Math.Abs(CDbl(amount)), map As Double(), veil As Double() = Nothing, light = 0.0
            Dim atmosphereR = 0.0, atmosphereG = 0.0, atmosphereB = 0.0
            If amount > 0 Then
                map = AddedHazeMap(rr, gg, bb, own, w, h, veil, light)
                For c = 0 To 2
                    veil(c) = PointColorMath.SrgbDecode(Math.Pow(Math.Max(0, veil(c)), 1 / AddedHazePower))
                Next
                veil = WorkingSrgbToRec(veil(0), veil(1), veil(2))
            Else
                map = DehazeTransmission(rr, gg, bb, w, h, strength, atmosphereR, atmosphereG, atmosphereB)
                veil = WorkingSrgbToRec(PointColorMath.SrgbDecode(atmosphereR), PointColorMath.SrgbDecode(atmosphereG), PointColorMath.SrgbDecode(atmosphereB))
            End If
            For y = 0 To p.Height - 1
                For x = 0 To p.Width - 1
                    Dim fx = Math.Clamp((x + 0.5) * w / p.Width - 0.5, 0, w - 1)
                    Dim fy = Math.Clamp((y + 0.5) * h / p.Height - 0.5, 0, h - 1)
                    Dim left = CInt(Math.Floor(fx)), top = CInt(Math.Floor(fy)), right = Math.Min(w - 1, left + 1), bottom = Math.Min(h - 1, top + 1)
                    Dim tx = fx - left, ty = fy - top
                    Dim value = (map(top * w + left) * (1 - tx) + map(top * w + right) * tx) * (1 - ty) +
                                (map(bottom * w + left) * (1 - tx) + map(bottom * w + right) * tx) * ty
                    Dim i = y * p.Width + x
                    If amount > 0 Then
                        Dim mix = Math.Min(1 - Math.Exp(-Math.Pow(strength, AddedHazeExponent) * (AddedHazeFloor + AddedHazeScale * Math.Max(0, value) * light)), AddedHazeCeiling)
                        Dim keep = 1 - AddedHazeGreying * mix * strength
                        Dim r = p.R(i) * (1 - mix) + veil(0) * 255 * mix
                        Dim g = p.G(i) * (1 - mix) + veil(1) * 255 * mix
                        Dim b = p.B(i) * (1 - mix) + veil(2) * 255 * mix
                        Dim luminance = WorkingEffectPlanes.Kr * r + WorkingEffectPlanes.Kg * g + WorkingEffectPlanes.Kb * b
                        p.R(i) = CSng(luminance + (r - luminance) * keep)
                        p.G(i) = CSng(luminance + (g - luminance) * keep)
                        p.B(i) = CSng(luminance + (b - luminance) * keep)
                    Else
                        Dim t = Math.Clamp(value, DehazeMinTransmission, 1)
                        Dim luminance = (WorkingEffectPlanes.Kr * p.R(i) + WorkingEffectPlanes.Kg * p.G(i) + WorkingEffectPlanes.Kb * p.B(i)) / 255
                        Dim u = Math.Clamp(luminance / DehazeShadowProtection, 0, 1), weight = u * u * (3 - 2 * u)
                        p.R(i) = CSng(p.R(i) + ((p.R(i) - veil(0) * 255) / t + veil(0) * 255 - p.R(i)) * weight)
                        p.G(i) = CSng(p.G(i) + ((p.G(i) - veil(1) * 255) / t + veil(1) * 255 - p.G(i)) * weight)
                        p.B(i) = CSng(p.B(i) + ((p.B(i) - veil(2) * 255) / t + veil(2) * 255 - p.B(i)) * weight)
                    End If
                Next
            Next
        End Sub

        Friend Shared Sub WorkingSurveySrgb(r As Double, g As Double, b As Double,
                                            ByRef sr As Double, ByRef sg As Double, ByRef sb As Double)
            sr = PointColorMath.SrgbEncode(Math.Clamp(1.660491002 * r - 0.587641139 * g - 0.072849863 * b, 0, 1))
            sg = PointColorMath.SrgbEncode(Math.Clamp(-0.124550475 * r + 1.132899897 * g - 0.008349422 * b, 0, 1))
            sb = PointColorMath.SrgbEncode(Math.Clamp(-0.018150763 * r - 0.100578898 * g + 1.118729661 * b, 0, 1))
        End Sub

        Friend Shared Function WorkingSrgbToRec(r As Double, g As Double, b As Double) As Double()
            Return {0.627403896 * r + 0.329283038 * g + 0.043313066 * b,
                    0.069097289 * r + 0.919540395 * g + 0.011362316 * b,
                    0.016391439 * r + 0.088013308 * g + 0.895595253 * b}
        End Function
    End Class
End Namespace
