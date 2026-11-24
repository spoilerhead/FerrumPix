Imports System
Imports System.Linq
Imports System.IO
Imports System.Collections.Generic
Imports System.Runtime.InteropServices
Imports System.Threading
Imports SkiaSharp

Namespace Services
    Partial Public Class ImageProcessor
        ' Maintained native examples have their own shipped variants. This keeps
        ' installed legacy copies intact when the test switch is turned off.
        Private Shared ReadOnly NativeExamples As New Lazy(Of Dictionary(Of String, ExtensionDefinition))(
            Function()
                Dim definitions As New Dictionary(Of String, ExtensionDefinition)(StringComparer.Ordinal)
                Dim folder = Path.Combine(AppContext.BaseDirectory, "NativeExtensions")
                If Directory.Exists(folder) Then
                    For Each file In Directory.GetFiles(folder, "*.json")
                        Dim loaded = ExtensionService.LoadFile(file)
                        If Not loaded.Succeeded Then Throw New InvalidOperationException(loaded.ErrorText)
                        definitions.Add(loaded.Definition.Id, loaded.Definition)
                    Next
                End If
                Return definitions
            End Function)

        Friend Shared Function WorkingExtensionDefinition(id As String) As ExtensionDefinition
            Dim definition As ExtensionDefinition = Nothing
            If NativeExamples.Value.TryGetValue(id, definition) Then Return definition
            Return ExtensionService.Find(id)
        End Function

        Friend Shared Function ApplyWorkingExtensions(source As LinearWorkingFrame, recipe As ImageAdjustments,
                                                       stage As String, cancel As CancellationToken,
                                                       Optional allowGpu As Boolean = False) As LinearWorkingFrame
            ' Validate enabled references explicitly. A missing extension must not
            ' make a native preview look successful with part of its recipe absent.
            Dim runs As New List(Of (Entry As ExtensionEntry, Definition As ExtensionDefinition))()
            For Each entry In ExtensionRecipe.Parse(recipe.ExtensionSettings).Where(Function(e) e.Enabled)
                Dim definition = WorkingExtensionDefinition(entry.Id)
                If definition Is Nothing Then Throw New NotSupportedException("Extension not loaded: " & entry.Id)
                If String.Equals(definition.Stage, stage, StringComparison.Ordinal) Then runs.Add((entry, definition))
            Next
            Dim current = source
            Try
                For Each run In runs.OrderBy(Function(r) r.Definition.Priority).ThenBy(Function(r) r.Definition.Id, StringComparer.Ordinal)
                    cancel.ThrowIfCancellationRequested()
                    If run.Definition.NativeEffect Is Nothing Then Throw New NotSupportedException("Native extension shader unavailable: " & run.Definition.Id)
                    Dim inputs = ExtensionInputsFor(run.Definition, run.Entry)
                    Dim nextFrame As LinearWorkingFrame = Nothing
                    If allowGpu AndAlso GpuImageService.Enabled AndAlso GpuImageService.IsAvailable Then
                        Dim snapshot = current.CreateNativeSnapshot(), domain = current.Domain
                        Dim effect = run.Definition.NativeEffect
                        GpuImageService.TryRun(Function(context) RenderWorkingExtensionGpu(context, snapshot, domain, effect, inputs),
                                               nextFrame, owned:=snapshot)
                    End If
                    If nextFrame IsNot Nothing Then
                        If current IsNot source Then current.Dispose()
                        current = nextFrame
                        Continue For
                    End If
                    cancel.ThrowIfCancellationRequested()
                    ' F32 raster transport avoids the measured CPU F16 subnormal
                    ' flush. The authored shader sees straight native RGB, with
                    ' alpha handled by the new wrapper and no automatic conversion.
                    Using input As New SKBitmap(New SKImageInfo(current.Width, current.Height, SKColorType.RgbaF32, SKAlphaType.Premul))
                        Dim row(current.Width * 4 - 1) As Single
                        For y = 0 To current.Height - 1
                            cancel.ThrowIfCancellationRequested()
                            current.ReadLinearRow(y, row)
                            Marshal.Copy(row, 0, input.GetPixels() + y * input.RowBytes, row.Length)
                        Next
                        Using output = RunExtensionOnBitmap(input, run.Definition.NativeEffect, inputs)
                            If output Is Nothing Then Throw New InvalidOperationException("Native extension render failed: " & run.Definition.Id)
                            nextFrame = LinearWorkingFrame.FromLinearBitmap(output, current.Domain)
                            If current IsNot source Then current.Dispose()
                            current = nextFrame
                        End Using
                    End Using
                Next
                cancel.ThrowIfCancellationRequested()
                Dim result = If(current Is source, LinearWorkingFrame.Create(source.Width, source.Height, source.Domain, Sub(y, row) source.ReadLinearRow(y, row)), current)
                current = Nothing
                Return result
            Finally
                If current IsNot Nothing AndAlso current IsNot source Then current.Dispose()
            End Try
        End Function
        Friend Shared Function RenderWorkingExtensionGpu(context As GRContext, snapshot As SKImage, domain As WorkingImageDomain,
                                                          effect As SKRuntimeEffect, inputs As ExtensionInputs) As LinearWorkingFrame
            Dim uniforms As New SKRuntimeEffectUniforms(effect)
            inputs.SetUniforms(uniforms, snapshot.Width, snapshot.Height)
            Dim curves = inputs.CurveImages()
            Try
                Dim children As New List(Of (Name As String, Image As SKImage)) From {("src", snapshot)}
                children.AddRange(curves)
                Return WorkingPreviewGpu.Render(context, snapshot.Width, snapshot.Height, domain, effect, uniforms, children)
            Finally
                DisposeCurveImages(curves)
            End Try
        End Function
    End Class
End Namespace
