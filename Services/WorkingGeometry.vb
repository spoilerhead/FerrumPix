Imports System
Imports System.Linq
Imports SkiaSharp

Namespace Services
    ''' <summary>Experimental geometry on linear Rec.2020 premultiplied F16.
    ''' Coordinate and size conventions come from the editor's geometry helpers;
    ''' sampling never passes through an encoded or integer image. Operations remain
    ''' ordered, so a later expanded canvas cannot resurrect an earlier crop.</summary>
    Friend NotInheritable Class WorkingGeometry
        Private Sub New()
        End Sub

        Friend Shared Function Apply(source As LinearWorkingFrame, recipe As ImageAdjustments) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            ArgumentNullException.ThrowIfNull(recipe)
            Dim plan = ImageProcessor.SnapshotWorkingGeometry(source.Width, source.Height, recipe)
            Dim current = source
            Try
                For i = 0 To plan.Steps.Count - 1
                    Dim operation = plan.Steps(i), target = plan.Sizes(i + 1)
                    Dim nextFrame = ApplyStep(current, operation.Kind, plan.Mappings(i), target)
                    If current IsNot source AndAlso current IsNot nextFrame Then current.Dispose()
                    current = nextFrame
                Next
                ' Always return independent ownership, including the empty recipe.
                Dim result = If(current Is source, Copy(source), current)
                current = Nothing
                Return result
            Finally
                If current IsNot Nothing AndAlso current IsNot source Then current.Dispose()
            End Try
        End Function

        Private Shared Function Copy(source As LinearWorkingFrame) As LinearWorkingFrame
            Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain, Sub(y, row) source.ReadLinearRow(y, row))
        End Function

        Private Shared Function ApplyStep(source As LinearWorkingFrame, kind As String, a As ImageAdjustments,
                                          target As SKSizeI) As LinearWorkingFrame
            Dim matrix = SKMatrix.Identity
            Select Case kind.Trim().ToLowerInvariant()
                Case "crop"
                    Dim crop = ImageProcessor.ComputeGeometryCropRect(source.Width, source.Height, a)
                    If crop.Left = 0 AndAlso crop.Top = 0 AndAlso crop.Width = source.Width AndAlso crop.Height = source.Height Then Return source
                    Return LinearWorkingFrame.Create(crop.Width, crop.Height, source.Domain,
                        Sub(y, row)
                            For x = 0 To crop.Width - 1
                                Dim r = 0.0, g = 0.0, b = 0.0, alpha = 0.0
                                source.ReadLinearPixel(x + crop.Left, y + crop.Top, r, g, b, alpha)
                                Store(row, x, r, g, b, alpha)
                            Next
                        End Sub)
                Case "resize"
                    If target.Width = source.Width AndAlso target.Height = source.Height Then Return source
                    Return Resize(source, target.Width, target.Height)
                Case "transform"
                    matrix = ImageGeometryMapper.SourceToDisplayMatrix(source.Width, source.Height,
                                a.RotationDegrees, a.FlipHorizontal, a.FlipVertical)
                    If Math.Abs(a.StraightenDegrees) >= 0.01F Then
                        Dim rotated = ImageGeometryMapper.DisplaySize(source.Width, source.Height, a.RotationDegrees)
                        Dim stage = ImageProcessor.StraightenStageSize(rotated.Width, rotated.Height,
                                        a.StraightenDegrees, a.StraightenExpandCanvas, a.StraightenAutoCrop)
                        Dim radians = a.StraightenDegrees * Math.PI / 180.0
                        Dim cosA = CSng(Math.Cos(radians) * stage.Scale), sinA = CSng(Math.Sin(radians) * stage.Scale)
                        Dim straighten = New SKMatrix(cosA, -sinA, CSng(target.Width / 2.0 - cosA * rotated.Width / 2.0 + sinA * rotated.Height / 2.0),
                                                       sinA, cosA, CSng(target.Height / 2.0 - sinA * rotated.Width / 2.0 - cosA * rotated.Height / 2.0), 0, 0, 1)
                        matrix = SKMatrix.Concat(straighten, matrix)
                    End If
                Case "perspective"
                    matrix = ImageGeometryMapper.WarpMatrix(source.Width, source.Height,
                                    a.PerspectiveHorizontal, a.PerspectiveVertical, a.PerspectiveAspect,
                                    a.PerspectiveScale, ImageGeometryMapper.CornerOffset(a))
                Case "canvas"
                    Dim offset = CanvasOffset(source.Width, source.Height, target.Width, target.Height, a.CanvasAnchor)
                    matrix = SKMatrix.CreateTranslation(offset.X, offset.Y)
                Case "warp"
                    Dim mesh = a.ImageWarp
                    If mesh Is Nothing OrElse mesh.IsEmpty Then Return source
                    If mesh.Kind <> "Gitter" Then Throw New NotSupportedException("Unsupported working geometry mesh.")
                    Dim moved = False
                    For gy = 0 To mesh.Rows
                        For gx = 0 To mesh.Columns
                            Dim index = (gy * (mesh.Columns + 1) + gx) * 2
                            If Math.Abs(mesh.Nodes(index) - gx * 100.0 / mesh.Columns) > 0.01 OrElse
                               Math.Abs(mesh.Nodes(index + 1) - gy * 100.0 / mesh.Rows) > 0.01 Then moved = True
                        Next
                    Next
                    If Not moved Then Return source
                    Return Resample(source, target.Width, target.Height,
                        Function(x, y)
                            Dim point As SKPoint
                            If Not ImageGeometryMapper.MeshInversePoint(mesh.Nodes, mesh.Columns, mesh.Rows,
                                    x, y, source.Width, source.Height, point) Then Return Nothing
                            Return New SKPoint?(point)
                        End Function)
                Case Else
                    Throw New NotSupportedException("Unsupported working geometry step: " & kind)
            End Select
            If matrix.IsIdentity AndAlso target.Width = source.Width AndAlso target.Height = source.Height Then Return source
            Dim inverse As SKMatrix
            If Not matrix.TryInvert(inverse) Then Throw New ArgumentException("Singular working geometry transform.")
            ' Map centers with explicit homogeneous division. A point behind the
            ' projective horizon has no visible source; never clamp it onto an edge.
            Return Resample(source, target.Width, target.Height,
                Function(x, y)
                    Dim w = inverse.Persp0 * x + inverse.Persp1 * y + inverse.Persp2
                    If Not Double.IsFinite(w) OrElse w <= 0.000001 Then Return Nothing
                    Return New SKPoint?(New SKPoint(CSng((inverse.ScaleX * x + inverse.SkewX * y + inverse.TransX) / w),
                                                    CSng((inverse.SkewY * x + inverse.ScaleY * y + inverse.TransY) / w)))
                End Function)
        End Function

        Private Shared Function CanvasOffset(w As Integer, h As Integer, targetW As Integer, targetH As Integer,
                                              anchor As String) As SKPoint
            Dim dx = targetW - w, dy = targetH - h
            Select Case If(anchor, "center").Trim().ToLowerInvariant()
                Case "top-left", "left-top" : Return New SKPoint(0, 0)
                Case "top", "top-center" : Return New SKPoint(dx / 2.0F, 0)
                Case "top-right", "right-top" : Return New SKPoint(dx, 0)
                Case "left", "middle-left" : Return New SKPoint(0, dy / 2.0F)
                Case "right", "middle-right" : Return New SKPoint(dx, dy / 2.0F)
                Case "bottom-left", "left-bottom" : Return New SKPoint(0, dy)
                Case "bottom", "bottom-center" : Return New SKPoint(dx / 2.0F, dy)
                Case "bottom-right", "right-bottom" : Return New SKPoint(dx, dy)
                Case Else : Return New SKPoint(dx / 2.0F, dy / 2.0F)
            End Select
        End Function

        Friend Shared Function CorrectLensDistortion(source As LinearWorkingFrame, correction As LensDataService.Korrektur,
                                                     cancel As System.Threading.CancellationToken) As LinearWorkingFrame
            Dim width = source.Width, height = source.Height
            Dim norm = LensDataService.NormScaleFor(correction, width, height)
            Dim zoom = RawDecodeService.DistortionFillScale(correction, width, height, norm)
            Dim cx = (width - 1) / 2.0, cy = (height - 1) / 2.0
            ' Reuse the legacy inverse lens mapping and framing policy, but
            ' interpolate the premultiplied working pixels in linear light.
            Return LinearWorkingFrame.Create(width, height, source.Domain,
                Sub(y, row)
                    cancel.ThrowIfCancellationRequested()
                    Dim dy = (y - cy) / zoom
                    For x = 0 To width - 1
                        Dim dx = (x - cx) / zoom
                        Dim radius = Math.Sqrt(dx * dx + dy * dy) * norm
                        Dim factor = If(radius > 0, LensDataService.DistortionRadius(correction, radius) / radius, 1)
                        Dim r = 0.0, g = 0.0, b = 0.0, alpha = 0.0
                        SampleBilinear(source, cx + dx * factor, cy + dy * factor, True, r, g, b, alpha)
                        Store(row, x, r, g, b, alpha)
                    Next
                End Sub)
        End Function

        Private Shared Function Resample(source As LinearWorkingFrame, width As Integer, height As Integer,
                                         map As Func(Of Double, Double, SKPoint?)) As LinearWorkingFrame
            Return LinearWorkingFrame.Create(width, height, source.Domain,
                Sub(y, row)
                    For x = 0 To width - 1
                        Dim point = map(x + 0.5, y + 0.5)
                        If Not point.HasValue Then Continue For
                        Dim r = 0.0, g = 0.0, b = 0.0, alpha = 0.0
                        SampleBilinear(source, point.Value.X - 0.5, point.Value.Y - 0.5, False, r, g, b, alpha)
                        Store(row, x, r, g, b, alpha)
                    Next
                End Sub)
        End Function

        ''' <summary>Premultiplied interpolation prevents transparent colors leaking
        ''' into visible edges. RGB is never clamped to alpha or to 0..1: those rules
        ''' are invalid for signed/HDR working values. Only coverage is bounded.</summary>
        Private Shared Sub SampleBilinear(source As LinearWorkingFrame, x As Double, y As Double, clampEdge As Boolean,
                                          ByRef r As Double, ByRef g As Double, ByRef b As Double, ByRef alpha As Double)
            If Not Double.IsFinite(x) OrElse Not Double.IsFinite(y) Then Return
            If clampEdge Then
                x = Math.Clamp(x, 0, source.Width - 1) : y = Math.Clamp(y, 0, source.Height - 1)
            ElseIf x <= -1 OrElse y <= -1 OrElse x >= source.Width OrElse y >= source.Height Then
                Return
            End If
            Dim left = CInt(Math.Floor(x)), top = CInt(Math.Floor(y))
            For dy = 0 To 1
                Dim wy = If(dy = 0, 1 - (y - top), y - top)
                For dx = 0 To 1
                    Dim weight = wy * If(dx = 0, 1 - (x - left), x - left)
                    If weight <= 0 Then Continue For
                    Dim sx = left + dx, sy = top + dy
                    If sx < 0 OrElse sx >= source.Width OrElse sy < 0 OrElse sy >= source.Height Then Continue For
                    Dim pr = 0.0, pg = 0.0, pb = 0.0, pa = 0.0
                    source.ReadLinearPixel(sx, sy, pr, pg, pb, pa)
                    r += weight * pr : g += weight * pg : b += weight * pb : alpha += weight * pa
                Next
            Next
        End Sub

        Private Shared Function Resize(source As LinearWorkingFrame, width As Integer, height As Integer) As LinearWorkingFrame
            ' Each axis chooses its own kernel: a mixed shrink/enlarge must not
            ' become nearest-neighbor on the enlarged axis. Precompute weights
            ' once; this also avoids repeating footprint divisions per channel.
            Dim horizontal = ResizeWeights(source.Width, width), vertical = ResizeWeights(source.Height, height)
            Return LinearWorkingFrame.Create(width, height, source.Domain,
                Sub(y, row)
                    For x = 0 To width - 1
                        Dim r = 0.0, g = 0.0, b = 0.0, alpha = 0.0
                        For Each vy In vertical(y)
                            For Each hx In horizontal(x)
                                Dim weight = vy.Weight * hx.Weight
                                Dim pr = 0.0, pg = 0.0, pb = 0.0, pa = 0.0
                                source.ReadLinearPixel(hx.Index, vy.Index, pr, pg, pb, pa)
                                r += weight * pr : g += weight * pg : b += weight * pb : alpha += weight * pa
                            Next
                        Next
                        Store(row, x, r, g, b, alpha)
                    Next
                End Sub)
        End Function

        Friend Structure FilterTap
            Friend Index As Integer
            Friend Weight As Double
        End Structure

        Friend Shared Function ResizeWeights(sourceSize As Integer, targetSize As Integer) As FilterTap()()
            Dim result(targetSize - 1)() As FilterTap
            Dim scale = sourceSize / CDbl(targetSize)
            For i = 0 To targetSize - 1
                If scale > 1 Then
                    ' Exact area integration of source pixel boxes.
                    Dim first = i * scale, last = (i + 1) * scale
                    Dim left = CInt(Math.Floor(first)), right = Math.Min(sourceSize, CInt(Math.Ceiling(last)))
                    Dim taps(right - left - 1) As FilterTap
                    For j = left To right - 1
                        taps(j - left) = New FilterTap With {.Index = j, .Weight = (Math.Min(last, j + 1) - Math.Max(first, j)) / scale}
                    Next
                    result(i) = taps
                Else
                    ' Linear magnification with source-edge extension.
                    Dim center = Math.Clamp((i + 0.5) * scale - 0.5, 0, sourceSize - 1)
                    Dim left = CInt(Math.Floor(center)), fraction = center - left
                    result(i) = {New FilterTap With {.Index = left, .Weight = 1 - fraction},
                                 New FilterTap With {.Index = Math.Min(sourceSize - 1, left + 1), .Weight = fraction}}
                End If
            Next
            Return result
        End Function

        Private Shared Sub Store(row As Single(), x As Integer, r As Double, g As Double, b As Double, alpha As Double)
            row(x * 4) = CSng(r) : row(x * 4 + 1) = CSng(g) : row(x * 4 + 2) = CSng(b)
            row(x * 4 + 3) = CSng(Math.Clamp(alpha, 0, 1))
        End Sub
    End Class

    Partial Public Class ImageProcessor
        Friend Shared Function SnapshotWorkingGeometry(width As Integer, height As Integer, recipe As ImageAdjustments) As GeometryInverse
            ' Reuse the existing preview/full-size pixel-coordinate scaling, then
            ' clone only geometry fields. No masks, objects or image payloads are copied.
            Dim steps = GeometrySteps(recipe, width, height).Select(Function(s) s.Clone()).ToList()
            For Each stepItem In steps
                If Not {"crop", "transform", "resize", "canvas", "perspective", "warp"}.Contains(If(stepItem.Kind, "").Trim().ToLowerInvariant()) Then
                    Throw New NotSupportedException("Unsupported working geometry step: " & stepItem.Kind)
                End If
                Dim a = stepItem.Adjustments
                Dim values As Double() = {a.CropLeftPercent, a.CropRightPercent, a.CropTopPercent, a.CropBottomPercent,
                    a.StraightenDegrees, a.ResizeScalePercent, a.PerspectiveHorizontal, a.PerspectiveVertical,
                    a.PerspectiveAspect, a.PerspectiveScale, a.PerspectiveCorner0X, a.PerspectiveCorner0Y,
                    a.PerspectiveCorner1X, a.PerspectiveCorner1Y, a.PerspectiveCorner2X, a.PerspectiveCorner2Y,
                    a.PerspectiveCorner3X, a.PerspectiveCorner3Y}
                If values.Any(Function(v) Not Double.IsFinite(v)) Then Throw New ArgumentException("Geometry controls must be finite.")
                If a.ImageWarp IsNot Nothing AndAlso a.ImageWarp.Nodes IsNot Nothing AndAlso
                   a.ImageWarp.Nodes.Any(Function(v) Not Double.IsFinite(v)) Then Throw New ArgumentException("Mesh nodes must be finite.")
            Next
            Return PrepareGeometryInverse(width, height, New ImageAdjustments With {.GeometryOperations = steps})
        End Function
    End Class
End Namespace
