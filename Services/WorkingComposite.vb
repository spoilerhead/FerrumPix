Imports System
Imports SkiaSharp

Namespace Services
    ''' <summary>Immutable normalized coverage, not RGB image data. Keeping masks
    ''' in F32 avoids adding a second 8-bit boundary at the blend itself.</summary>
    Friend NotInheritable Class WorkingCoverage
        Private ReadOnly _values As Single()
        Friend ReadOnly Property Width As Integer
        Friend ReadOnly Property Height As Integer

        Friend Sub New(width As Integer, height As Integer, values As Single())
            ArgumentNullException.ThrowIfNull(values)
            If width <= 0 OrElse height <= 0 OrElse CLng(width) * height <> values.LongLength Then
                Throw New ArgumentException("Coverage dimensions do not match.")
            End If
            For Each value In values
                If Not Single.IsFinite(value) OrElse value < 0 OrElse value > 1 Then
                    Throw New ArgumentException("Coverage must be finite and in [0,1].")
                End If
            Next
            Me.Width = width : Me.Height = height
            _values = CType(values.Clone(), Single())
        End Sub

        Friend Function At(x As Integer, y As Integer) As Single
            Return _values(y * Width + x)
        End Function
    End Class

    ''' <summary>Linear Rec.2020 normal compositing and adjustment-mask mixing.
    ''' Both work on premultiplied samples with one final F16 packing. They do not
    ''' impose the RGB <= alpha rule, which would discard signed/HDR colors.
    ''' Artistic blend modes retain their own color-space contracts and are not
    ''' silently reinterpreted as linear normal compositing here.</summary>
    Friend NotInheritable Class WorkingComposite
        Private Sub New()
        End Sub

        Friend Shared Function SourceOver(foreground As LinearWorkingFrame, background As LinearWorkingFrame,
                                          Optional opacity As Single = 1,
                                          Optional coverage As WorkingCoverage = Nothing) As LinearWorkingFrame
            Return Combine(background, foreground, opacity, coverage, sourceOver:=True)
        End Function

        Friend Shared Function Mix(original As LinearWorkingFrame, adjusted As LinearWorkingFrame,
                                   Optional opacity As Single = 1,
                                   Optional coverage As WorkingCoverage = Nothing) As LinearWorkingFrame
            ' Adjustment masks interpolate two complete render results. Using
            ' source-over here instead would increase coverage at partial-alpha
            ' edges each time an adjustment layer was added.
            Return Combine(original, adjusted, opacity, coverage, sourceOver:=False)
        End Function

        Private Shared Function Combine(bottom As LinearWorkingFrame, top As LinearWorkingFrame, opacity As Single,
                                         coverage As WorkingCoverage, sourceOver As Boolean) As LinearWorkingFrame
            Validate(bottom, top, opacity, coverage)
            Dim upper(top.Width * 4 - 1) As Single
            Return LinearWorkingFrame.Create(top.Width, top.Height, top.Domain,
                Sub(y, row)
                    bottom.ReadLinearRow(y, row) : top.ReadLinearRow(y, upper)
                    For x = 0 To top.Width - 1
                        Dim at = x * 4
                        Dim weight = CDbl(opacity) * If(coverage Is Nothing, 1.0, CDbl(coverage.At(x, y)))
                        Dim retained = If(sourceOver, 1 - upper(at + 3) * weight, 1 - weight)
                        For c = 0 To 3
                            row(at + c) = CSng(upper(at + c) * weight + row(at + c) * retained)
                        Next
                        row(at + 3) = Math.Clamp(row(at + 3), 0, 1)
                    Next
                End Sub)
        End Function

        Friend Shared Sub Validate(bottom As LinearWorkingFrame, top As LinearWorkingFrame, opacity As Single,
                                   coverage As WorkingCoverage)
            ArgumentNullException.ThrowIfNull(bottom)
            ArgumentNullException.ThrowIfNull(top)
            If bottom.Width <> top.Width OrElse bottom.Height <> top.Height OrElse bottom.Domain <> top.Domain Then
                Throw New ArgumentException("Composite frames must share dimensions and domain.")
            End If
            If Not Single.IsFinite(opacity) OrElse opacity < 0 OrElse opacity > 1 Then Throw New ArgumentOutOfRangeException(NameOf(opacity))
            If coverage IsNot Nothing AndAlso (coverage.Width <> top.Width OrElse coverage.Height <> top.Height) Then
                Throw New ArgumentException("Coverage and image dimensions differ.", NameOf(coverage))
            End If
        End Sub

        Friend Shared Function FillBackground(source As LinearWorkingFrame, color As String) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then
                Throw New ArgumentException("Document backgrounds belong to rendered light, not RAW scene radiance.", NameOf(source))
            End If
            Dim parsed As SKColor
            If Not SKColor.TryParse(color, parsed) Then Throw New ArgumentException("Invalid background color.", NameOf(color))
            ' The UI color is encoded-sRGB metadata. Decode/convert it once, then
            ' composite in working light; no source image passes through SKColor.
            Dim r = PointColorMath.SrgbDecode(parsed.Red / 255.0)
            Dim g = PointColorMath.SrgbDecode(parsed.Green / 255.0)
            Dim b = PointColorMath.SrgbDecode(parsed.Blue / 255.0)
            Dim alpha = parsed.Alpha / 255.0
            Dim backdrop = New Double() {
                (0.627403896 * r + 0.329283038 * g + 0.043313066 * b) * alpha,
                (0.069097289 * r + 0.919540395 * g + 0.011362316 * b) * alpha,
                (0.016391439 * r + 0.088013308 * g + 0.895595253 * b) * alpha, alpha}
            Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain,
                Sub(y, row)
                    source.ReadLinearRow(y, row)
                    For x = 0 To source.Width - 1
                        Dim at = x * 4, uncovered = 1 - CDbl(row(at + 3))
                        For c = 0 To 3
                            row(at + c) = CSng(row(at + c) + uncovered * backdrop(c))
                        Next
                        row(at + 3) = Math.Clamp(row(at + 3), 0, 1)
                    Next
                End Sub)
        End Function
    End Class
End Namespace
