Imports System
Imports System.Collections.Generic
Imports System.Linq

Namespace Services
    Partial Public Class ImageProcessor
        ' Shape, falloff, temperature, relief and shadow preparation are shared
        ' with the editor. The image itself is already linear: no inverse SDR
        ' scene table or byte staging is needed. Colored gains retain their
        ' calibrated linear-sRGB basis, with explicit unclipped primaries changes.
        Friend Shared Function ApplyWorkingLightSources(source As LinearWorkingFrame, lights As IEnumerable(Of LightSource),
                                                       ambient As Double, Optional depth As WorkingCoverage = Nothing) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            If Not Double.IsFinite(ambient) OrElse ambient < -6 OrElse ambient > 3 Then Throw New ArgumentOutOfRangeException(NameOf(ambient))
            Dim snapshot = If(lights, Enumerable.Empty(Of LightSource)()).Where(Function(l) l IsNot Nothing).Select(Function(l) l.Clone()).ToArray()
            For Each light In snapshot
                For Each member In GetType(LightSource).GetProperties().Where(Function(m) m.PropertyType = GetType(Double))
                    If Not Double.IsFinite(CDbl(member.GetValue(light))) Then Throw New ArgumentException("Light controls must be finite.")
                Next
            Next
            Dim active = snapshot.Where(Function(l) l.Enabled AndAlso (Math.Abs(l.Intensity) > 0.001 OrElse l.Glow > 0.01)).ToArray()
            If active.Length = 0 AndAlso ambient = 0 Then Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain, Sub(y, row) source.ReadLinearRow(y, row))
            Dim width = source.Width, height = source.Height, ambientGain = Math.Pow(2, ambient), hasAmbient = ambient <> 0
            Dim field As DepthField = Nothing
            If depth IsNot Nothing AndAlso active.Any(Function(l) l.UseDepth) Then
                field = New DepthField(depth.CopyValues(), depth.Width, depth.Height, width, height)
            End If
            Dim prepared = active.Select(Function(l) PrepareLight(l, width, height, field)).ToArray()
            Return LinearWorkingFrame.Create(width, height, source.Domain,
                Sub(y, row)
                    source.ReadLinearRow(y, row)
                    Dim py = y + 0.5, v = py / height
                    For x = 0 To width - 1
                        Dim px = x + 0.5, u = px / width
                        Dim gr = ambientGain, gg = ambientGain, gb = ambientGain
                        Dim ar = 0.0, ag = 0.0, ab = 0.0
                        Dim touched = hasAmbient
                        Dim depthHere = -1.0
                        For li = 0 To prepared.Length - 1
                            Dim light = prepared(li)
                            Dim f = LightShapeAt(light, px, py)
                            If light.UseDepth AndAlso f > 0 Then
                                If depthHere < 0 Then depthHere = field.FineAt(u, v)
                                Dim z = (depthHere - light.DepthAtLight) / light.DepthSigma
                                f *= Math.Exp(-z * z)
                            End If
                            If f > 0.0005 Then
                                touched = True
                                If light.Ev >= 0 Then
                                    ' Form und Schatten nur fuer Licht: ein Schatten wird nicht von
                                    ' einer Flaeche abgewandt.
                                    If light.Modulation IsNot Nothing Then
                                        f *= Math.Max(0.0, field.SampleJoint(light.Modulation, u, v, depthHere))
                                    End If
                                    Dim k = (Math.Pow(2.0, light.Ev) - 1.0) * f
                                    If light.GlowMode Then
                                        Dim glow = light.GlowAdd * f
                                        ar += glow * light.Wr : ag += glow * light.Wg : ab += glow * light.Wb
                                        ' Leuchten hellt auch an, nur schwaecher als Beleuchten.
                                        k *= 0.5
                                    End If
                                    gr += k * light.Wr : gg += k * light.Wg : gb += k * light.Wb
                                Else
                                    ' Schatten: die Farbe toent ihn, ein warmes Licht weggenommen
                                    ' laesst eher Blau zurueck. Gleichmaessig dunkler fuer Weiss.
                                    Dim k = (Math.Pow(2.0, light.Ev) - 1.0) * f
                                    gr += k * (1.5 - 0.5 * light.Cr)
                                    gg += k * (1.5 - 0.5 * light.Cg)
                                    gb += k * (1.5 - 0.5 * light.Cb)
                                End If
                            End If
                            If light.HaloStrength > 0 Then
                                Dim hx = (px - light.Cx) / light.HaloRadius, hy = (py - light.Cy) / light.HaloRadius
                                Dim h2 = hx * hx + hy * hy
                                If h2 < 16.0 Then
                                    Dim hk = light.HaloStrength * Math.Exp(-h2) * 0.6
                                    ar += hk * light.Wr : ag += hk * light.Wg : ab += hk * light.Wb
                                    touched = True
                                End If
                            End If
                        Next
                        If Not touched Then Continue For
                        Dim at = x * 4, alpha = CDbl(row(at + 3))
                        If alpha <= 0 Then Continue For
                        Dim r = row(at) / alpha, g = row(at + 1) / alpha, b = row(at + 2) / alpha
                        Dim sr = 1.660491002 * r - 0.587641139 * g - 0.072849863 * b
                        Dim sg = -0.124550475 * r + 1.132899897 * g - 0.008349422 * b
                        Dim sb = -0.018150763 * r - 0.100578898 * g + 1.118729661 * b
                        Dim value = WorkingSrgbToRec(sr * Math.Max(0, gr) + ar, sg * Math.Max(0, gg) + ag, sb * Math.Max(0, gb) + ab)
                        row(at) = CSng(value(0) * alpha) : row(at + 1) = CSng(value(1) * alpha) : row(at + 2) = CSng(value(2) * alpha)
                    Next
                End Sub)
        End Function
    End Class
End Namespace
