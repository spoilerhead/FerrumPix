Imports System
Imports SkiaSharp

Namespace Services
    ''' <summary>Immutable film calibration in encoded-sRGB coordinates, from
    ''' explicit legacy swatches or a floating measurement. Neither path borrows an
    ''' RGB8 analysis image or remeasures implicitly during rendering.</summary>
    Friend NotInheritable Class RenderedNegativeSettings
        Friend ReadOnly Property Enabled As Boolean
        Friend ReadOnly Property Monochrome As Boolean
        ' Legacy swatches are populated only by the explicit-string constructor.
        ' Measured settings use their fractional arrays, selected by HasMeasuredCalibration.
        Friend ReadOnly Property BaseColor As SKColor
        Friend ReadOnly Property DensityColor As SKColor
        Friend ReadOnly Property Gamma As Single
        Private ReadOnly _measuredBase As Double()
        Private ReadOnly _measuredDensity As Double()

        Friend ReadOnly Property HasMeasuredCalibration As Boolean
            Get
                Return _measuredBase IsNot Nothing
            End Get
        End Property

        ' Measurement is an explicit snapshot, not an implicit per-render fallback.
        ' Keep fractional encoded-sRGB values: SKColor is only a legacy UI swatch.
        Friend Shared Function FromMeasurement(measurement As FloatingFilmMeasurement,
                                               Optional gamma As Single = 0,
                                               Optional monochrome As Boolean = False) As RenderedNegativeSettings
            ArgumentNullException.ThrowIfNull(measurement)
            Return New RenderedNegativeSettings(measurement.CopyBase(), measurement.CopyDensity(), gamma, monochrome)
        End Function

        Private Sub New(filmBase As Double(), filmDensity As Double(), gamma As Single, monochrome As Boolean)
            RenderedLateColorSettings.Check(gamma, NameOf(gamma), -100, 100)
            For c = 0 To 2
                If filmBase(c) - Math.Max(FloatingFilmMeasurement.LogFloor, filmDensity(c)) < 0.000001 Then
                    Throw New ArgumentException("Film measurement has no usable density span in one or more channels.")
                End If
            Next
            Enabled = True : Me.Gamma = gamma : Me.Monochrome = monochrome
            _measuredBase = CType(filmBase.Clone(), Double())
            _measuredDensity = CType(filmDensity.Clone(), Double())
        End Sub

        Friend Function CopyMeasuredBase() As Double()
            Return If(_measuredBase Is Nothing, Nothing, CType(_measuredBase.Clone(), Double()))
        End Function

        Friend Function CopyMeasuredDensity() As Double()
            Return If(_measuredDensity Is Nothing, Nothing, CType(_measuredDensity.Clone(), Double()))
        End Function

        Friend Sub New(Optional enabled As Boolean = False, Optional baseColor As String = Nothing,
                       Optional densityColor As String = Nothing, Optional gamma As Single = 0,
                       Optional monochrome As Boolean = False)
            RenderedLateColorSettings.Check(gamma, NameOf(gamma), -100, 100)
            Me.Enabled = enabled : Me.Monochrome = monochrome : Me.Gamma = gamma
            If Not enabled Then Return
            Dim filmBase, filmDensity As SKColor
            If String.IsNullOrWhiteSpace(baseColor) OrElse String.IsNullOrWhiteSpace(densityColor) Then
                Throw New NotSupportedException("Supply explicit swatches or a floating film measurement snapshot.")
            End If
            If Not SKColor.TryParse(baseColor, filmBase) OrElse Not SKColor.TryParse(densityColor, filmDensity) Then
                Throw New ArgumentException("Invalid film-base/density swatch.")
            End If
            If filmBase.Red < 2 OrElse filmBase.Green < 2 OrElse filmBase.Blue < 2 OrElse
               filmDensity.Red >= filmBase.Red OrElse filmDensity.Green >= filmBase.Green OrElse filmDensity.Blue >= filmBase.Blue Then
                Throw New ArgumentException("Each film base must exceed its density point and be at least two codes.")
            End If
            Me.BaseColor = filmBase : Me.DensityColor = filmDensity
        End Sub
    End Class
End Namespace
