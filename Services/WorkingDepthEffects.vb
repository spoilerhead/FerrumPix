Imports System
Imports System.Threading
Imports SkiaSharp

Namespace Services
    Friend NotInheritable Class WorkingDepthEffects
        Private Sub New()
        End Sub

        Private Shared Sub Validate(source As LinearWorkingFrame)
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Rendered input required.")
        End Sub

        Friend Shared Function SampleDepth(depth As WorkingCoverage, x As Integer, y As Integer, width As Integer, height As Integer) As Double
            Dim fx = Math.Clamp((x + 0.5) * depth.Width / width - 0.5, 0, depth.Width - 1)
            Dim fy = Math.Clamp((y + 0.5) * depth.Height / height - 0.5, 0, depth.Height - 1)
            Dim left = CInt(Math.Floor(fx)), top = CInt(Math.Floor(fy)), right = Math.Min(depth.Width - 1, left + 1), bottom = Math.Min(depth.Height - 1, top + 1)
            Dim tx = fx - left, ty = fy - top
            Return (depth.At(left, top) * (1 - tx) + depth.At(right, top) * tx) * (1 - ty) +
                   (depth.At(left, bottom) * (1 - tx) + depth.At(right, bottom) * tx) * ty
        End Function

        Friend Shared Function Fog(source As LinearWorkingFrame, depth As WorkingCoverage, density As Single,
                                   near As Single, far As Single, color As String, Optional foreground As Single = 0) As LinearWorkingFrame
            Validate(source)
            For Each control In {(density, NameOf(density)), (near, NameOf(near)), (far, NameOf(far)), (foreground, NameOf(foreground))}
                RenderedLateColorSettings.Check(control.Item1, control.Item2, 0, 100)
            Next
            Dim parsed As SKColor
            If Not SKColor.TryParse(color, parsed) Then Throw New ArgumentException("Invalid fog color.")
            If density = 0 Then Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain, Sub(y, row) source.ReadLinearRow(y, row))
            ArgumentNullException.ThrowIfNull(depth)
            Dim p = WorkingEffectPlanes.Load(source)
            Dim fogColor = ImageProcessor.WorkingSrgbToRec(PointColorMath.SrgbDecode(parsed.Red / 255.0),
                                                          PointColorMath.SrgbDecode(parsed.Green / 255.0), PointColorMath.SrgbDecode(parsed.Blue / 255.0))
            Dim start = (100 - Math.Max(near, far)) / 100.0, full = (100 - Math.Min(near, far)) / 100.0
            Dim onset = If(full - start < 0.0001, 0, 0.05 / (full - start)), transmission = 1 - Math.Min(density / 100.0, 0.999)
            For y = 0 To p.Height - 1
                For x = 0 To p.Width - 1
                    Dim distance = 1 - SampleDepth(depth, x, y, p.Width, p.Height)
                    Dim length = If(full - start < 0.0001, 1, DepthMapService.FogOpticalLength((distance - start) / Math.Max(0.0001, full - start), onset))
                    Dim medium = 1 - Math.Pow(transmission, length)
                    Dim mix = foreground / 100 * density / 100 + (1 - foreground / 100) * medium
                    Dim i = y * p.Width + x
                    p.R(i) = CSng(p.R(i) + (fogColor(0) * 255 - p.R(i)) * mix)
                    p.G(i) = CSng(p.G(i) + (fogColor(1) * 255 - p.G(i)) * mix)
                    p.B(i) = CSng(p.B(i) + (fogColor(2) * 255 - p.B(i)) * mix)
                Next
            Next
            Return p.ToFrame()
        End Function

        Friend Shared Function Blur(source As LinearWorkingFrame, depth As WorkingCoverage, fromPercent As Single, toPercent As Single,
                                    strength As Single, transition As Single, Optional corners As Integer = 0,
                                    Optional highlights As Single = 60, Optional cancel As CancellationToken = Nothing) As LinearWorkingFrame
            Validate(source)
            For Each control In {(fromPercent, NameOf(fromPercent)), (toPercent, NameOf(toPercent)), (strength, NameOf(strength)),
                                 (transition, NameOf(transition)), (highlights, NameOf(highlights))}
                RenderedLateColorSettings.Check(control.Item1, control.Item2, 0, 100)
            Next
            If corners <> 0 AndAlso (corners < 3 OrElse corners > 12) Then Throw New ArgumentOutOfRangeException(NameOf(corners))
            Dim radius = Math.Min(source.Width, source.Height) * strength / 100.0 * 0.08
            If radius < 0.5 Then Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain, Sub(y, row) source.ReadLinearRow(y, row))
            ArgumentNullException.ThrowIfNull(depth)
            Dim p = WorkingEffectPlanes.Load(source), result = p.ClonePlanes()
            Dim fromValue = Math.Min(fromPercent, toPercent), toValue = Math.Max(fromPercent, toPercent)
            For level = 1 To 8
                cancel.ThrowIfCancellationRequested()
                Dim share = level / 8.0, stageRadius = radius * share
                If stageRadius < 0.5 Then Continue For
                Dim blurred = DepthMapService.WorkingDiscBlur(p, stageRadius, corners, highlights, cancel)
                For y = 0 To p.Height - 1
                    For x = 0 To p.Width - 1
                        Dim d = SampleDepth(depth, x, y, p.Width, p.Height) * 100
                        Dim outside = Math.Max(0, Math.Max(fromValue - d, d - toValue))
                        Dim amount = Math.Min(1, outside / Math.Max(1, CDbl(transition)))
                        Dim mix = 1 - Math.Min(1, Math.Abs(amount - share) * 8)
                        If mix <= 0 Then Continue For
                        Dim i = y * p.Width + x, own = result.A(i) * (1 - mix), added = blurred.A(i) * mix, alpha = own + added
                        If alpha > 0 Then
                            result.R(i) = CSng((result.R(i) * own + blurred.R(i) * added) / alpha)
                            result.G(i) = CSng((result.G(i) * own + blurred.G(i) * added) / alpha)
                            result.B(i) = CSng((result.B(i) * own + blurred.B(i) * added) / alpha)
                        End If
                        result.A(i) = CSng(alpha)
                    Next
                Next
            Next
            Return result.ToFrame()
        End Function
    End Class

    Partial Public NotInheritable Class DepthMapService
        ''' <summary>Use the same polygon/disc spans and bounded work radius as the
        ''' existing depth blur, but prefix-sum signed, powered linear F32 colors and
        ''' coverage. No 256-entry highlight table or RGB8 intermediate is used.</summary>
        Friend Shared Function WorkingDiscBlur(source As WorkingEffectPlanes, radius As Double, corners As Integer,
                                                highlights As Single, cancel As CancellationToken) As WorkingEffectPlanes
            Dim scale = Math.Min(1.0, MaxKernelRadius / radius)
            Dim w = Math.Max(1, CInt(Math.Round(source.Width * scale))), h = Math.Max(1, CInt(Math.Round(source.Height * scale)))
            Dim p = source.ResizeTo(w, h), r = Math.Max(1, Math.Min(MaxKernelRadius, CInt(Math.Round(radius * scale))))
            Dim left As Integer() = Nothing, right As Integer() = Nothing, count = 0L
            SpannenFuer(r, corners, 12, left, right, count)
            Dim exponent = 1 + highlights / 100.0 * 3, down = 1 / exponent, stride = w + 1
            Dim prefix = {New Double(stride * h - 1) {}, New Double(stride * h - 1) {},
                          New Double(stride * h - 1) {}, New Double(stride * h - 1) {}}
            Dim channels = {p.R, p.G, p.B}
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim i = y * w + x, at = y * stride + x
                    For c = 0 To 2
                        Dim v = channels(c)(i) / 255.0
                        prefix(c)(at + 1) = prefix(c)(at) + Math.CopySign(Math.Pow(Math.Abs(v), exponent), v) * p.A(i)
                    Next
                    prefix(3)(at + 1) = prefix(3)(at) + p.A(i)
                Next
            Next
            Dim spanSum = Function(values As Double(), y As Integer, first As Integer, last As Integer) As Double
                              Dim row = Math.Clamp(y, 0, h - 1) * stride
                              Dim a = Math.Clamp(first, 0, w), b = Math.Clamp(last + 1, 0, w)
                              Return values(row + b) - values(row + a) +
                                     Math.Max(0, Math.Min(last + 1, 0) - first) * (values(row + 1) - values(row)) +
                                     Math.Max(0, last - Math.Max(first, w) + 1) * (values(row + w) - values(row + w - 1))
                          End Function
            Dim result = p.ClonePlanes()
            For y = 0 To h - 1
                cancel.ThrowIfCancellationRequested()
                For x = 0 To w - 1
                    Dim sums(3) As Double
                    For dy = -r To r
                        Dim index = dy + r
                        If right(index) < left(index) Then Continue For
                        For c = 0 To 3
                            sums(c) += spanSum(prefix(c), y + dy, x + left(index), x + right(index))
                        Next
                    Next
                    Dim at = y * w + x, alpha = sums(3)
                    result.A(at) = CSng(Math.Clamp(alpha / Math.Max(1L, count), 0, 1))
                    If alpha > 0 Then
                        result.R(at) = CSng(Math.CopySign(Math.Pow(Math.Abs(sums(0) / alpha), down) * 255, sums(0)))
                        result.G(at) = CSng(Math.CopySign(Math.Pow(Math.Abs(sums(1) / alpha), down) * 255, sums(1)))
                        result.B(at) = CSng(Math.CopySign(Math.Pow(Math.Abs(sums(2) / alpha), down) * 255, sums(2)))
                    End If
                Next
            Next
            Return result.ResizeTo(source.Width, source.Height)
        End Function
    End Class
End Namespace
