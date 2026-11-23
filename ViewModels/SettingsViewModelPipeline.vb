Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels
    Partial Public Class SettingsViewModel
        Public Property NativePipelinePreviewEnabled As Boolean
            Get
                Return _appSettings.NativePipelinePreviewEnabled
            End Get
            Set(value As Boolean)
                If value = _appSettings.NativePipelinePreviewEnabled Then Return
                _appSettings.NativePipelinePreviewEnabled = value
                ' Like feature switches, this takes effect immediately rather
                ' than waiting for the settings window to close.
                AppSettingsService.Update(Sub(settings) settings.NativePipelinePreviewEnabled = value)
                Me.RaisePropertyChanged(NameOf(NativePipelinePreviewEnabled))
                _mainVm?.Editor?.RefreshNativePipelinePreview()
            End Set
        End Property
    End Class
End Namespace
