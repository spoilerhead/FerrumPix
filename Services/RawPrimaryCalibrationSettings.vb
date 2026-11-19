Imports System
Imports System.Linq

Namespace Services
    ''' <summary>Nur die sechs Primaerfarben-Regler des aktuellen verankerten RAW-Modells.
    ''' Die spaetere, helligkeitsabhaengige Schattentoenung ist KEINE Primaerfarbenmatrix.
    ''' Der gemeinsame Matrixbauer behaelt seine geeichten Rampenwerte und Weissnormierung.</summary>
    Friend NotInheritable Class RawPrimaryCalibrationSettings
        Private ReadOnly _toneMatrix As Single()

        Private Sub New(matrix As Single())
            _toneMatrix = matrix
        End Sub

        Friend ReadOnly Property IsIdentity As Boolean
            Get
                Return _toneMatrix Is Nothing
            End Get
        End Property

        Friend Shared Function FromAdjustments(adj As ImageAdjustments) As RawPrimaryCalibrationSettings
            ArgumentNullException.ThrowIfNull(adj)
            If adj.WhiteBalanceModel <> ImageProcessor.CurrentWhiteBalanceModel OrElse Not ImageProcessor.WhiteBalanceOnRamp(adj) Then
                Throw New NotSupportedException("Primary calibration requires the current anchored RAW model.")
            End If
            Dim values = {adj.CalibrationRedHue, adj.CalibrationRedSaturation, adj.CalibrationGreenHue,
                          adj.CalibrationGreenSaturation, adj.CalibrationBlueHue, adj.CalibrationBlueSaturation}
            If values.Any(Function(v) Not Single.IsFinite(v) OrElse v < -100 OrElse v > 100) Then
                Throw New ArgumentOutOfRangeException(NameOf(adj), "Calibration controls must be finite in [-100,100].")
            End If
            If Not Double.IsFinite(adj.WhiteBalanceAnchorX) OrElse Not Double.IsFinite(adj.WhiteBalanceAnchorY) OrElse
               adj.WhiteBalanceAnchorX + adj.WhiteBalanceAnchorY >= 1 Then Throw New ArgumentException("Invalid capture anchor.", NameOf(adj))
            Dim matrix = ImageProcessor.BuildCalibrationMatrix(adj)
            If matrix Is Nothing Then
                If values.Any(Function(v) v <> 0) Then Throw New ArgumentException("Singular primary calibration.", NameOf(adj))
                Return New RawPrimaryCalibrationSettings(Nothing)
            End If
            Dim rgb = {matrix(0), matrix(1), matrix(2), matrix(5), matrix(6), matrix(7), matrix(10), matrix(11), matrix(12)}
            Return New RawPrimaryCalibrationSettings(RawWhiteBalanceSettings.ToToneMatrix(rgb))
        End Function

        ''' <summary>Kalibrierung MAL WB: auf den Bildpunkt wirkt erst WB, dann Kalibrierung.
        ''' Kein zusaetzlicher F16-Zwischenpuffer und keine Gamma-Grenze zwischen beiden.</summary>
        Friend Function CombineAfter(whiteBalance As Single()) As Single()
            If _toneMatrix Is Nothing Then Return whiteBalance
            If whiteBalance Is Nothing Then Return CType(_toneMatrix.Clone(), Single())
            Dim result(8) As Single
            For r = 0 To 2
                For c = 0 To 2
                    Dim value As Double = 0
                    For k = 0 To 2
                        value += CDbl(_toneMatrix(r * 3 + k)) * whiteBalance(k * 3 + c)
                    Next
                    result(r * 3 + c) = CSng(value)
                Next
            Next
            Return result
        End Function
    End Class
End Namespace
