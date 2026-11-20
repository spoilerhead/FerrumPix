Imports System
Imports System.Threading.Tasks

Namespace Services
    ''' <summary>Straight linear Rec.2020 F32 planes, scaled by 255 solely to reuse
    ''' the editor's calibrated thresholds. No integer image or gamma encoding is
    ''' involved. Alpha stays normalized. Only final storage is F16.</summary>
    Friend NotInheritable Class WorkingEffectPlanes
        Friend ReadOnly Width As Integer
        Friend ReadOnly Height As Integer
        Friend ReadOnly Domain As WorkingImageDomain
        Friend R As Single()
        Friend G As Single()
        Friend B As Single()
        Friend A As Single()
        Friend Const Kr As Double = 0.2627
        Friend Const Kg As Double = 0.678
        Friend Const Kb As Double = 0.0593

        Private Sub New(width As Integer, height As Integer, domain As WorkingImageDomain)
            Me.Width = width : Me.Height = height : Me.Domain = domain
        End Sub

        Friend Shared Function Load(source As LinearWorkingFrame) As WorkingEffectPlanes
            Dim p = New WorkingEffectPlanes(source.Width, source.Height, source.Domain)
            Dim n = source.Width * source.Height
            p.R = New Single(n - 1) {} : p.G = New Single(n - 1) {} : p.B = New Single(n - 1) {} : p.A = New Single(n - 1) {}
            Dim row(source.Width * 4 - 1) As Single
            For y = 0 To source.Height - 1
                source.ReadLinearRow(y, row)
                For x = 0 To source.Width - 1
                    Dim i = y * source.Width + x, at = x * 4, alpha = CDbl(row(at + 3))
                    p.A(i) = CSng(alpha)
                    If alpha <= 0 Then Continue For
                    p.R(i) = CSng(row(at) * 255.0 / alpha)
                    p.G(i) = CSng(row(at + 1) * 255.0 / alpha)
                    p.B(i) = CSng(row(at + 2) * 255.0 / alpha)
                Next
            Next
            Return p
        End Function

        Friend Function ToFrame() As LinearWorkingFrame
            Return LinearWorkingFrame.Create(Width, Height, Domain,
                Sub(y, row)
                    For x = 0 To Width - 1
                        Dim i = y * Width + x, at = x * 4, alpha = Math.Clamp(CDbl(A(i)), 0, 1)
                        row(at) = CSng(R(i) / 255.0 * alpha)
                        row(at + 1) = CSng(G(i) / 255.0 * alpha)
                        row(at + 2) = CSng(B(i) / 255.0 * alpha)
                        row(at + 3) = CSng(alpha)
                    Next
                End Sub)
        End Function

        Friend Function Luma() As Single()
            Dim values(R.Length - 1) As Single
            For i = 0 To values.Length - 1
                values(i) = CSng(Kr * R(i) + Kg * G(i) + Kb * B(i))
            Next
            Return values
        End Function

        Friend Function ClonePlanes() As WorkingEffectPlanes
            Return New WorkingEffectPlanes(Width, Height, Domain) With {
                .R = CType(R.Clone(), Single()), .G = CType(G.Clone(), Single()),
                .B = CType(B.Clone(), Single()), .A = CType(A.Clone(), Single())}
        End Function

        Friend Function HalfSize() As WorkingEffectPlanes
            Dim w = (Width + 1) \ 2, h = (Height + 1) \ 2, n = w * h
            Dim result = New WorkingEffectPlanes(w, h, Domain) With {
                .R = New Single(n - 1) {}, .G = New Single(n - 1) {}, .B = New Single(n - 1) {}, .A = New Single(n - 1) {}}
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim rr = 0.0, gg = 0.0, bb = 0.0, alpha = 0.0, count = 0
                    For sy = y * 2 To Math.Min(Height, y * 2 + 2) - 1
                        For sx = x * 2 To Math.Min(Width, x * 2 + 2) - 1
                            Dim i = sy * Width + sx
                            rr += R(i) * CDbl(A(i)) : gg += G(i) * CDbl(A(i)) : bb += B(i) * CDbl(A(i))
                            alpha += A(i) : count += 1
                        Next
                    Next
                    Dim at = y * w + x
                    result.A(at) = CSng(alpha / count)
                    If alpha > 0 Then
                        result.R(at) = CSng(rr / alpha) : result.G(at) = CSng(gg / alpha) : result.B(at) = CSng(bb / alpha)
                    End If
                Next
            Next
            Return result
        End Function

        Friend Function ResizeTo(w As Integer, h As Integer) As WorkingEffectPlanes
            If w = Width AndAlso h = Height Then Return ClonePlanes()
            Dim horizontal = WorkingGeometry.ResizeWeights(Width, w), vertical = WorkingGeometry.ResizeWeights(Height, h), n = w * h
            Dim output = New WorkingEffectPlanes(w, h, Domain) With {
                .R = New Single(n - 1) {}, .G = New Single(n - 1) {}, .B = New Single(n - 1) {}, .A = New Single(n - 1) {}}
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim rr = 0.0, gg = 0.0, bb = 0.0, alpha = 0.0
                    For Each vy In vertical(y)
                        For Each hx In horizontal(x)
                            Dim i = vy.Index * Width + hx.Index, weight = vy.Weight * hx.Weight * A(i)
                            rr += weight * R(i) : gg += weight * G(i) : bb += weight * B(i) : alpha += weight
                        Next
                    Next
                    Dim at = y * w + x
                    output.A(at) = CSng(Math.Clamp(alpha, 0, 1))
                    If alpha > 0 Then
                        output.R(at) = CSng(rr / alpha) : output.G(at) = CSng(gg / alpha) : output.B(at) = CSng(bb / alpha)
                    End If
                Next
            Next
            Return output
        End Function

        Friend Function BlurField(field As Single(), sigma As Single) As Single()
            Dim premul(field.Length - 1) As Single
            For i = 0 To field.Length - 1
                premul(i) = field(i) * A(i)
            Next
            Dim filtered = ImageProcessor.BlurWorkingPlane(premul, Width, Height, sigma)
            Dim coverage = ImageProcessor.BlurWorkingPlane(A, Width, Height, sigma)
            For i = 0 To filtered.Length - 1
                filtered(i) = If(coverage(i) > 0, filtered(i) / Math.Max(coverage(i), Single.Epsilon), field(i))
            Next
            Return filtered
        End Function

        Friend Function Blurred(sigma As Single) As WorkingEffectPlanes
            Dim alpha = ImageProcessor.BlurWorkingPlane(A, Width, Height, sigma)
            Dim channel = Function(values As Single()) As Single()
                              Dim premul(values.Length - 1) As Single
                              For i = 0 To values.Length - 1
                                  premul(i) = values(i) * A(i)
                              Next
                              Dim result = ImageProcessor.BlurWorkingPlane(premul, Width, Height, sigma)
                              For i = 0 To result.Length - 1
                                  result(i) = If(alpha(i) > 0, result(i) / Math.Max(alpha(i), Single.Epsilon), 0)
                              Next
                              Return result
                          End Function
            Return New WorkingEffectPlanes(Width, Height, Domain) With {.R = channel(R), .G = channel(G), .B = channel(B), .A = alpha}
        End Function

        Friend Sub AddLumaDelta(delta As Single())
            For i = 0 To R.Length - 1
                If A(i) <= 0 Then Continue For
                R(i) += delta(i) : G(i) += delta(i) : B(i) += delta(i)
            Next
        End Sub

        Friend Sub Mix(other As WorkingEffectPlanes, weight As Single, Optional mixAlpha As Boolean = False)
            For i = 0 To R.Length - 1
                If mixAlpha Then
                    Dim own = A(i) * (1 - CDbl(weight)), added = other.A(i) * CDbl(weight), total = own + added
                    If total > 0 Then
                        R(i) = CSng((R(i) * own + other.R(i) * added) / total)
                        G(i) = CSng((G(i) * own + other.G(i) * added) / total)
                        B(i) = CSng((B(i) * own + other.B(i) * added) / total)
                    End If
                    A(i) = CSng(total)
                Else
                    R(i) += (other.R(i) - R(i)) * weight
                    G(i) += (other.G(i) - G(i)) * weight
                    B(i) += (other.B(i) - B(i)) * weight
                End If
            Next
        End Sub
    End Class
End Namespace
