Imports System
Imports SkiaSharp

Namespace Services
    ''' <summary>Explicit film calibration, in the editor's encoded-sRGB swatch
    ''' convention. Automatic holder/base estimation is not silently borrowed from RGB8.</summary>
    Friend NotInheritable Class RenderedNegativeSettings
        Friend ReadOnly Property Enabled As Boolean
        Friend ReadOnly Property Monochrome As Boolean
        Friend ReadOnly Property BaseColor As SKColor
        Friend ReadOnly Property DensityColor As SKColor
        Friend ReadOnly Property Gamma As Single
        Friend Sub New(Optional enabled As Boolean = False, Optional baseColor As String = Nothing,
                       Optional densityColor As String = Nothing, Optional gamma As Single = 0,
                       Optional monochrome As Boolean = False)
            RenderedLateColorSettings.Check(gamma, NameOf(gamma), -100, 100)
            Me.Enabled = enabled : Me.Monochrome = monochrome : Me.Gamma = gamma
            If Not enabled Then Return
            Dim filmBase, filmDensity As SKColor
            If String.IsNullOrWhiteSpace(baseColor) OrElse String.IsNullOrWhiteSpace(densityColor) Then
                Throw New NotSupportedException("Explicit base/density colors are required; floating automatic film analysis is not migrated.")
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
