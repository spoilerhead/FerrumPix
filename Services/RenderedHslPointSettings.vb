Imports System
Imports System.Linq

Namespace Services
    ''' <summary>Immutable H/S/L bands and point-color selections. Swatches keep the
    ''' editor's six-decimal recipe contract, independently of image precision.</summary>
    Friend NotInheritable Class RenderedHslPointSettings
        Private ReadOnly _bands As Single()
        Private ReadOnly _pointColors As String
        Friend ReadOnly Property GuideSigma As Single
        Friend ReadOnly Property HasHsl As Boolean
        Friend ReadOnly Property HasPoints As Boolean
        Friend ReadOnly Property HasVisualization As Boolean
        Friend ReadOnly Property IsIdentity As Boolean
            Get
                Return Not HasHsl AndAlso Not HasPoints
            End Get
        End Property
        Friend Sub New(Optional bands As IEnumerable(Of Single) = Nothing,
                       Optional pointColors As IEnumerable(Of PointColorSwatch) = Nothing,
                       Optional guideSigma As Single = 1.5F)
            _bands = If(bands Is Nothing, New Single(23) {}, bands.ToArray())
            If _bands.Length <> 24 Then Throw New ArgumentException("Expected eight H/S/L rows.", NameOf(bands))
            For Each value In _bands
                RenderedLateColorSettings.Check(value, NameOf(bands), -100, 100)
            Next
            RenderedLateColorSettings.Check(guideSigma, NameOf(guideSigma), 0, 8)
            Me.GuideSigma = guideSigma
            HasHsl = _bands.Any(Function(value) value <> 0)
            Dim swatches = If(pointColors Is Nothing, Array.Empty(Of PointColorSwatch)(), pointColors.ToArray())
            If swatches.Length > PointColorSwatch.MaxSwatches OrElse
               swatches.Any(Function(swatch) swatch Is Nothing OrElse Not swatch.IsValid()) Then
                Throw New ArgumentException("Invalid point-color swatches or more than eight selections.", NameOf(pointColors))
            End If
            ' Own nested range arrays before formatting; no mutable UI data survives.
            swatches = swatches.Select(Function(swatch) swatch.Clone()).ToArray()
            _pointColors = PointColorSwatch.FormatList(swatches, includeVisualize:=True)
            If PointColorSwatch.ParseList(_pointColors).Count <> swatches.Length Then
                Throw New ArgumentException("Swatches cannot be represented by the existing recipe format.", NameOf(pointColors))
            End If
            HasVisualization = swatches.Any(Function(swatch) swatch.Visualize)
            HasPoints = swatches.Any(Function(swatch) swatch.IsActive() OrElse swatch.Visualize)
        End Sub
        Friend Sub CopyTo(target As ImageAdjustments)
            ArgumentNullException.ThrowIfNull(target)
            target.RedHue = _bands(0) : target.RedSaturation = _bands(1) : target.RedLuminance = _bands(2)
            target.OrangeHue = _bands(3) : target.OrangeSaturation = _bands(4) : target.OrangeLuminance = _bands(5)
            target.YellowHue = _bands(6) : target.YellowSaturation = _bands(7) : target.YellowLuminance = _bands(8)
            target.GreenHue = _bands(9) : target.GreenSaturation = _bands(10) : target.GreenLuminance = _bands(11)
            target.AquaHue = _bands(12) : target.AquaSaturation = _bands(13) : target.AquaLuminance = _bands(14)
            target.BlueHue = _bands(15) : target.BlueSaturation = _bands(16) : target.BlueLuminance = _bands(17)
            target.PurpleHue = _bands(18) : target.PurpleSaturation = _bands(19) : target.PurpleLuminance = _bands(20)
            target.MagentaHue = _bands(21) : target.MagentaSaturation = _bands(22) : target.MagentaLuminance = _bands(23)
            target.PointColors = _pointColors
        End Sub
    End Class
End Namespace
