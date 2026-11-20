Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors

Namespace Services
    Partial Public NotInheritable Class DenoiseModelService
        Friend Shared Function SuggestWorking(source As LinearWorkingFrame, Optional grain As Double = 50,
                                               Optional rawStage As Boolean = False) As DenoiseSuggestion
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Rendered input required.")
            If Not Double.IsFinite(grain) OrElse grain < 0 OrElse grain > 100 Then Throw New ArgumentOutOfRangeException(NameOf(grain))
            If CLng(source.Width) * source.Height < AutoMinimumPixels Then
                Dim small = SuggestFor(Single.NaN, grain, rawStage)
                small.TooSmall = True
                Return small
            End If
            Return SuggestFor(ImageProcessor.EstimateWorkingNoiseLevel(source), grain, rawStage)
        End Function

        ''' <summary>The existing NAFNet session, with a continuous encoded-sRGB
        ''' tensor adapter. The trained space is unchanged; Rec.2020 is not fed to
        ''' a model trained on sRGB. No RGB8 image, dither or integer output exists
        ''' in this path. The source's out-of-sRGB/HDR reserve is retained.</summary>
        Friend Shared Function DenoiseWorkingImage(source As LinearWorkingFrame, strength As Single,
                                                  Optional cancel As CancellationToken = Nothing,
                                                  Optional progress As Action(Of Integer, Integer) = Nothing,
                                                  Optional tileEdge As Integer = 0) As LinearWorkingFrame
            ValidateWorkingDenoise(source, strength)
            If tileEdge <> 0 AndAlso (tileEdge < 64 OrElse tileEdge > 2048 OrElse tileEdge Mod 64 <> 0) Then
                Throw New ArgumentOutOfRangeException(NameOf(tileEdge))
            End If
            cancel.ThrowIfCancellationRequested()
            If Not ImageAvailable Then Throw New NotSupportedException("The image denoise model is not installed.")
            Dim loaded = AiModelService.SessionAndDeviceFor(ImageModelFile)
            If loaded Is Nothing OrElse loaded.Session Is Nothing Then Throw New NotSupportedException("The image denoise model session is unavailable.")
            Dim session = loaded.Session, name = session.InputMetadata.Keys.First(), edge = TileEdgeForRun(loaded.Accelerator)
            If tileEdge > 0 Then
                If tileEdge > edge Then Throw New ArgumentOutOfRangeException(NameOf(tileEdge), "Tile override cannot exceed the selected device's normal tile.")
                edge = tileEdge
            End If
            Return DenoiseWorkingTiles(source, strength, edge,
                Function(values, w, h)
                    Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 3, h, w})
                    values.AsSpan().CopyTo(tensor.Buffer.Span)
                    Using result = session.Run(New List(Of NamedOnnxValue) From {NamedOnnxValue.CreateFromTensor(name, tensor)})
                        Dim output = TryCast(result.First().Value, DenseTensor(Of Single))
                        Dim dimensions = If(output Is Nothing, Array.Empty(Of Integer)(), output.Dimensions.ToArray())
                        If dimensions.Length <> 4 OrElse dimensions(0) <> 1 OrElse dimensions(1) <> 3 OrElse
                           dimensions(2) <> h OrElse dimensions(3) <> w Then
                            Throw New InvalidOperationException("Unexpected image denoise model output layout.")
                        End If
                        Return output.Buffer.ToArray()
                    End Using
                End Function, cancel, progress)
        End Function

        Private Shared Sub ValidateWorkingDenoise(source As LinearWorkingFrame, strength As Single)
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Rendered input required.")
            RenderedLateColorSettings.Check(strength, NameOf(strength), 0, 1)
        End Sub

        ''' <summary>Separately testable tensor/tiling boundary. A run callback
        ''' borrows CHW input and returns CHW output in the same dimensions. Errors
        ''' and cancellation discard the whole result, rather than returning a
        ''' partially denoised image. Strength zero still retains model chroma.</summary>
        Friend Shared Function DenoiseWorkingTiles(source As LinearWorkingFrame, strength As Single, edge As Integer,
                                                   run As Func(Of Single(), Integer, Integer, Single()),
                                                   Optional cancel As CancellationToken = Nothing,
                                                   Optional progress As Action(Of Integer, Integer) = Nothing) As LinearWorkingFrame
            ValidateWorkingDenoise(source, strength)
            ArgumentNullException.ThrowIfNull(run)
            If edge < 64 OrElse edge > 2048 OrElse edge Mod 64 <> 0 Then Throw New ArgumentOutOfRangeException(NameOf(edge))
            cancel.ThrowIfCancellationRequested()
            Dim p = WorkingEffectPlanes.Load(source), target = p.ClonePlanes()
            Dim stride = edge - 2 * ImageTileOverlap, plane = edge * edge
            Dim count = ((p.Width + stride - 1) \ stride) * ((p.Height + stride - 1) \ stride), done = 0
            Dim mirror = Function(value As Integer, length As Integer) As Integer
                             Dim index = value Mod (2 * length)
                             Return If(index < length, index, 2 * length - index - 1)
                         End Function
            For y = 0 To p.Height - 1 Step stride
                For x = 0 To p.Width - 1 Step stride
                    cancel.ThrowIfCancellationRequested()
                    Dim left = Math.Max(0, Math.Min(x - ImageTileOverlap, Math.Max(p.Width, edge) - edge))
                    Dim top = Math.Max(0, Math.Min(y - ImageTileOverlap, Math.Max(p.Height, edge) - edge))
                    Dim values(plane * 3 - 1) As Single, lumaSum = 0.0
                    For yy = 0 To edge - 1
                        For xx = 0 To edge - 1
                            Dim sx = mirror(left + xx, p.Width), sy = mirror(top + yy, p.Height), i = sy * p.Width + sx
                            Dim r = 0.0, g = 0.0, b = 0.0
                            ImageProcessor.WorkingSurveySrgb(p.R(i) / 255.0, p.G(i) / 255.0, p.B(i) / 255.0, r, g, b)
                            Dim at = yy * edge + xx
                            values(at) = CSng(r) : values(plane + at) = CSng(g) : values(plane * 2 + at) = CSng(b)
                            lumaSum += 0.2126 * r + 0.7152 * g + 0.0722 * b
                        Next
                    Next
                    Dim lift = Math.Max(0, DarkTileLevel / 255.0 - lumaSum / plane)
                    If lift > 0 Then
                        For i = 0 To values.Length - 1
                            values(i) = CSng(values(i) + lift)
                        Next
                    End If
                    Dim output = run(values, edge, edge)
                    cancel.ThrowIfCancellationRequested()
                    If output Is Nothing OrElse output.Length <> values.Length OrElse output.Any(Function(v) Not Single.IsFinite(v)) Then
                        Throw New InvalidOperationException("Invalid denoise model output.")
                    End If
                    For yy = y To Math.Min(p.Height, y + stride) - 1
                        For xx = x To Math.Min(p.Width, x + stride) - 1
                            Dim i = yy * p.Width + xx, at = (yy - top) * edge + xx - left
                            If p.A(i) <= 0 Then Continue For
                            Dim r = 0.0, g = 0.0, b = 0.0
                            ImageProcessor.WorkingSurveySrgb(p.R(i) / 255.0, p.G(i) / 255.0, p.B(i) / 255.0, r, g, b)
                            Dim nr = output(at) - lift, ng = output(plane + at) - lift, nb = output(plane * 2 + at) - lift
                            Dim oldLuma = 0.2126 * r + 0.7152 * g + 0.0722 * b, newLuma = 0.2126 * nr + 0.7152 * ng + 0.0722 * nb
                            Dim shift = (oldLuma - newLuma) * (1 - strength)
                            Dim delta = ImageProcessor.WorkingSrgbToRec(PointColorMath.SrgbDecode(nr + shift) - PointColorMath.SrgbDecode(r),
                                                                       PointColorMath.SrgbDecode(ng + shift) - PointColorMath.SrgbDecode(g),
                                                                       PointColorMath.SrgbDecode(nb + shift) - PointColorMath.SrgbDecode(b))
                            target.R(i) = CSng(p.R(i) + delta(0) * 255)
                            target.G(i) = CSng(p.G(i) + delta(1) * 255)
                            target.B(i) = CSng(p.B(i) + delta(2) * 255)
                        Next
                    Next
                    done += 1 : progress?.Invoke(done, count)
                Next
            Next
            cancel.ThrowIfCancellationRequested()
            Return target.ToFrame()
        End Function
    End Class
End Namespace
