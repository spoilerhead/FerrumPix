Imports System

Namespace Services
    ''' <summary>Rendered-input WB only. No capture anchor or camera matrix is
    ''' admitted: current anchored RAW WB belongs before the scene tone curve.</summary>
    Friend NotInheritable Class RenderedWhiteBalanceSettings
        Private ReadOnly _linear As Single()
        Private ReadOnly _encoded As Single()
        Friend Sub New(Optional model As Integer = 4, Optional temperature As Single = 0,
                       Optional tint As Single = 0, Optional kelvin As Double = 0,
                       Optional kelvinTint As Double = 0)
            If model < 0 OrElse model > 4 Then Throw New ArgumentOutOfRangeException(NameOf(model))
            RenderedLateColorSettings.Check(temperature, NameOf(temperature), -100, 100)
            RenderedLateColorSettings.Check(tint, NameOf(tint), -100, 100)
            If Not Double.IsFinite(kelvin) OrElse (kelvin <> 0 AndAlso kelvin < 1000) Then Throw New ArgumentOutOfRangeException(NameOf(kelvin))
            If Not Double.IsFinite(kelvinTint) Then Throw New ArgumentOutOfRangeException(NameOf(kelvinTint))
            If model < 2 Then
                If kelvin <> 0 OrElse kelvinTint <> 0 Then Throw New ArgumentException("Legacy encoded WB accepts relative temperature/tint only.")
                If temperature <> 0 OrElse tint <> 0 Then _encoded = ImageProcessor.RenderedLegacyWhiteBalanceMatrix(temperature, tint)
            Else
                Dim adjustments As New ImageAdjustments With {
                    .WhiteBalanceModel = model, .Temperature = temperature, .Tint = tint,
                    .WhiteBalanceKelvin = kelvin, .WhiteBalanceKelvinTint = kelvinTint,
                    .SourceFollowsRawExposure = False}
                ' Model 2 is a saved-recipe convention. Reuse the editor's existing
                ' load-time conversion rather than reinterpreting old Kelvin/tint.
                ImageProcessor.UpgradeWhiteBalanceModel(adjustments)
                _linear = ImageProcessor.BuildWhiteBalanceMatrix(adjustments)
                If _linear IsNot Nothing Then
                    For Each value In _linear
                        If Not Single.IsFinite(value) Then Throw New ArgumentException("WB adaptation is not finite.")
                    Next
                End If
                If _linear Is Nothing Then
                    Dim anchor = ImageProcessor.WhiteBalanceAnchorOf(adjustments)
                    Dim claimed = ImageProcessor.ClaimedWhitePointOf(adjustments)
                    If Not claimed.IsValid OrElse Math.Abs(anchor.X - claimed.X) >= 0.0000005 OrElse
                       Math.Abs(anchor.Y - claimed.Y) >= 0.0000005 Then Throw New ArgumentException("Cannot construct rendered WB adaptation.")
                End If
            End If
        End Sub
        Friend ReadOnly Property IsIdentity As Boolean
            Get
                Return _linear Is Nothing AndAlso _encoded Is Nothing
            End Get
        End Property
        Friend Function CopyLinearMatrix() As Single()
            Return If(_linear Is Nothing, Nothing, CType(_linear.Clone(), Single()))
        End Function
        Friend Function CopyEncodedMatrix() As Single()
            Return If(_encoded Is Nothing, Nothing, CType(_encoded.Clone(), Single()))
        End Function
    End Class
End Namespace
