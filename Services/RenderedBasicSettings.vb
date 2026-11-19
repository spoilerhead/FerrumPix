Imports System

Namespace Services
    ''' <summary>Nur die migrierten Grundregler, kein stillschweigend teilweise gelesenes
    ''' Editorrezept. Die Herkunft der Belichtung bleibt eine ausdrueckliche Aufruferpflicht.</summary>
    Friend NotInheritable Class RenderedBasicSettings
        Friend Sub New(Optional highlights As Single = 0, Optional shadowLevel As Single = 0,
                       Optional whites As Single = 0, Optional blacks As Single = 0,
                       Optional saturation As Single = 0, Optional vibrance As Single = 0,
                       Optional brightness As Single = 0, Optional contrast As Single = 0,
                       Optional renderedExposureEv As Double = 0, Optional exposureAlreadyAppliedEv As Double = 0)
            For Each item In {(highlights, NameOf(highlights)), (shadowLevel, NameOf(shadowLevel)),
                              (whites, NameOf(whites)), (blacks, NameOf(blacks)),
                              (saturation, NameOf(saturation)), (vibrance, NameOf(vibrance)),
                              (brightness, NameOf(brightness)), (contrast, NameOf(contrast))}
                If Not Single.IsFinite(item.Item1) OrElse Math.Abs(item.Item1) > 100 Then Throw New ArgumentOutOfRangeException(item.Item2)
            Next
            If Not Double.IsFinite(renderedExposureEv) OrElse Math.Abs(renderedExposureEv) > 4 Then Throw New ArgumentOutOfRangeException(NameOf(renderedExposureEv))
            If Not Double.IsFinite(exposureAlreadyAppliedEv) OrElse Math.Abs(exposureAlreadyAppliedEv) > 10 Then Throw New ArgumentOutOfRangeException(NameOf(exposureAlreadyAppliedEv))
            Me.Highlights = highlights : Me.ShadowLevel = shadowLevel
            Me.Whites = whites : Me.Blacks = blacks
            Me.Saturation = saturation : Me.Vibrance = vibrance
            Me.Brightness = brightness : Me.Contrast = contrast
            Me.RenderedExposureEv = renderedExposureEv
            Me.ExposureAlreadyAppliedEv = exposureAlreadyAppliedEv
        End Sub

        Friend ReadOnly Property Highlights As Single
        Friend ReadOnly Property ShadowLevel As Single
        Friend ReadOnly Property Whites As Single
        Friend ReadOnly Property Blacks As Single
        Friend ReadOnly Property Saturation As Single
        Friend ReadOnly Property Vibrance As Single
        Friend ReadOnly Property Brightness As Single
        Friend ReadOnly Property Contrast As Single
        Friend ReadOnly Property RenderedExposureEv As Double
        Friend ReadOnly Property ExposureAlreadyAppliedEv As Double

        Friend ReadOnly Property IsIdentity As Boolean
            Get
                Return Highlights = 0 AndAlso ShadowLevel = 0 AndAlso Whites = 0 AndAlso Blacks = 0 AndAlso
                       Saturation = 0 AndAlso Vibrance = 0 AndAlso Brightness = 0 AndAlso Contrast = 0 AndAlso RenderedExposureEv = 0
            End Get
        End Property

        Friend Function ToAdjustments() As ImageAdjustments
            ' Die lokale Detailverstaerkung kennt die GANZE Belichtung, die Punktkette
            ' aber nur den noch nicht entwickelten Rest. RAW-EV niemals zweimal rechnen.
            Return New ImageAdjustments With {
                .Highlights = Highlights, .ShadowsLevel = ShadowLevel, .Whites = Whites, .Blacks = Blacks,
                .Saturation = Saturation, .Vibrance = Vibrance, .Brightness = Brightness, .Contrast = Contrast,
                .Exposure = CSng((RenderedExposureEv + ExposureAlreadyAppliedEv) * ImageAdjustments.ExposurePointsPerStop),
                .ExposureInSource = CSng(ExposureAlreadyAppliedEv * ImageAdjustments.ExposurePointsPerStop),
                .SourceFollowsRawExposure = False}
        End Function
    End Class
End Namespace
