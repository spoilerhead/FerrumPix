Imports System.Threading
Imports FerrumPix.Services

Namespace ViewModels
    Partial Public Class EditorViewModel
        Private ReadOnly Property NativePreviewEnabled As Boolean
            Get
                Return AppSettingsService.Load().NativePipelinePreviewEnabled
            End Get
        End Property

        Public Sub RefreshNativePipelinePreview()
            ' Do not let a completed old-mode render or cached zoom/patch image
            ' replace the newly selected renderer. Native frames never enter the
            ' old base/tone caches, so toggling off can safely resume that path.
            Interlocked.Increment(_previewRequestId)
            CancelAndDisposePreviewCts(Interlocked.Exchange(_previewRenderCts, Nothing))
            ImageProcessor.ClearBaseCache()
            ResetZoomDetail()
            InvalidateCompositorBlitSnapshot()
            If HasDocument Then SchedulePreviewUpdate(markDirty:=False)
        End Sub

        Private Function NativePreviewHasBakedInput() As Boolean
            Return _workingImage.HasBakedContent OrElse Not String.IsNullOrWhiteSpace(_workingImageOverridePath)
        End Function

        Private Shared Sub ValidateNativePreviewInput(baked As Boolean)
            If baked Then Throw New NotSupportedException(LocalizationService.T("Eingebackene Arbeitsbilder sind in der neuen Vorschau noch nicht angeschlossen."))
        End Sub
    End Class
End Namespace
