Imports System
Imports System.Linq

Namespace Services
    ''' <summary>Unveraenderliche WB-Einstellungen fuer das aktuelle verankerte RAW-Modell.
    ''' Kein vollstaendiges Editorrezept: Primaerfarben haben eigene Einstellungen, alte WB-Modelle
    ''' und Filmnegative brauchen eigene Pfade. LibRaw hat den Aufnahmeabgleich schon angewandt; hier zaehlt
    ''' nur die Adaption vom behaupteten Licht zurueck auf diesen erhaltenen Aufnahmeanker.</summary>
    Friend NotInheritable Class RawWhiteBalanceSettings
        Private ReadOnly _toneMatrix As Single()

        Private Sub New(matrix As Single())
            _toneMatrix = matrix
        End Sub

        Friend ReadOnly Property IsIdentity As Boolean
            Get
                Return _toneMatrix Is Nothing
            End Get
        End Property

        Friend Function CopyToneMatrix() As Single()
            Return If(_toneMatrix Is Nothing, Nothing, CType(_toneMatrix.Clone(), Single()))
        End Function

        Friend Shared Function FromAdjustments(adj As ImageAdjustments) As RawWhiteBalanceSettings
            ArgumentNullException.ThrowIfNull(adj)
            If adj.WhiteBalanceModel <> ImageProcessor.CurrentWhiteBalanceModel OrElse adj.NegativeEnabled Then
                Throw New NotSupportedException("Only current anchored RAW white balance is migrated; legacy/negative models require their own path.")
            End If
            Dim values = {adj.WhiteBalanceAnchorX, adj.WhiteBalanceAnchorY, adj.WhiteBalanceKelvin,
                          adj.WhiteBalanceKelvinTint, CDbl(adj.Temperature), CDbl(adj.Tint)}
            If values.Any(Function(v) Not Double.IsFinite(v)) Then Throw New ArgumentException("White-balance values must be finite.", NameOf(adj))
            If adj.WhiteBalanceAnchorX <= 0 OrElse adj.WhiteBalanceAnchorY <= 0 OrElse
               adj.WhiteBalanceAnchorX + adj.WhiteBalanceAnchorY >= 1 Then Throw New ArgumentException("A valid capture anchor is required.", NameOf(adj))
            If adj.WhiteBalanceKelvin <> 0 AndAlso adj.WhiteBalanceKelvin < 1000 Then Throw New ArgumentOutOfRangeException(NameOf(adj))

            Dim matrix = ImageProcessor.BuildWhiteBalanceMatrix(adj)
            If matrix Is Nothing Then
                Dim anchor = ImageProcessor.WhiteBalanceAnchorOf(adj)
                Dim claimed = ImageProcessor.ClaimedWhitePointOf(adj)
                ' Gleiche No-op-Toleranz wie der gemeinsame Matrixbauer, kein zweiter
                ' Kelvin-Rueckweg und keine strengere Interpretation desselben Reglers.
                If Not claimed.IsValid OrElse Math.Abs(anchor.X - claimed.X) >= 0.0000005 OrElse Math.Abs(anchor.Y - claimed.Y) >= 0.0000005 Then
                    Throw New ArgumentException("Cannot construct a finite white-balance adaptation.", NameOf(adj))
                End If
                Return New RawWhiteBalanceSettings(Nothing)
            End If

            ' P * M * P^-1 wie BuildWhiteBalanceRamp: dieselbe Adaption im linearen
            ' ProPhoto-Tonraum, inklusive der vorhandenen D65/D50-Weisspunktadaption.
            Return New RawWhiteBalanceSettings(ToToneMatrix(matrix))
        End Function

        Friend Shared Function ToToneMatrix(matrix As Single()) As Single()
            Dim p = PointColorMath.SrgbToProPhoto, q = PointColorMath.ProPhotoToSrgb
            Dim pm(8) As Double, result(8) As Single
            For r = 0 To 2
                For c = 0 To 2
                    For k = 0 To 2
                        pm(r * 3 + c) += p(r * 3 + k) * matrix(k * 3 + c)
                    Next
                Next
            Next
            For r = 0 To 2
                For c = 0 To 2
                    Dim value As Double = 0
                    For k = 0 To 2
                        value += pm(r * 3 + k) * q(k * 3 + c)
                    Next
                    result(r * 3 + c) = CSng(value)
                    If Not Single.IsFinite(result(r * 3 + c)) Then Throw New ArgumentException("Invalid ramp matrix.", NameOf(matrix))
                Next
            Next
            Return result
        End Function
    End Class
End Namespace
