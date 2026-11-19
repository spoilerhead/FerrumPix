Imports System
Imports System.Linq
Imports SkiaSharp

Namespace Services
    ' These snapshots admit only the migrated controls. A full mutable editor
    ' recipe must not silently activate unimplemented HSL, presets or LUT stages.
    Friend NotInheritable Class RenderedLateColorSettings
        Friend Sub New(Optional grade As RenderedGradeSettings = Nothing,
                       Optional mixer As RenderedMixerSettings = Nothing,
                       Optional gradient As RenderedGradientSettings = Nothing,
                       Optional shadowTint As Single = 0, Optional posterizeLevels As Integer = 0,
                       Optional thresholdLevel As Integer = 0)
            Check(shadowTint, NameOf(shadowTint), -100, 100)
            If posterizeLevels < 0 OrElse posterizeLevels > 64 Then Throw New ArgumentOutOfRangeException(NameOf(posterizeLevels))
            If thresholdLevel < 0 OrElse thresholdLevel > 255 Then Throw New ArgumentOutOfRangeException(NameOf(thresholdLevel))
            Me.Grade = If(grade, New RenderedGradeSettings())
            Me.Mixer = If(mixer, New RenderedMixerSettings())
            Me.Gradient = If(gradient, New RenderedGradientSettings())
            Me.ShadowTint = shadowTint
            ' The GUI permits one level, which the existing renderer treats as off.
            Me.PosterizeLevels = If(posterizeLevels < 2, 0, posterizeLevels)
            Me.ThresholdLevel = thresholdLevel
        End Sub

        Friend ReadOnly Property Grade As RenderedGradeSettings
        Friend ReadOnly Property Mixer As RenderedMixerSettings
        Friend ReadOnly Property Gradient As RenderedGradientSettings
        Friend ReadOnly Property ShadowTint As Single
        Friend ReadOnly Property PosterizeLevels As Integer
        Friend ReadOnly Property ThresholdLevel As Integer
        Friend ReadOnly Property IsIdentity As Boolean
            Get
                Return Grade.IsIdentity AndAlso Mixer.IsIdentity AndAlso Gradient.Amount = 0 AndAlso
                       ShadowTint = 0 AndAlso PosterizeLevels = 0 AndAlso ThresholdLevel = 0
            End Get
        End Property

        ' A bounded channel mix or palette replacement must not leave colored/HDR
        ' reserves outside its intended output. A partial gradient fades the linear
        ' reserve with the unchanged-image contribution; at 100% only the map remains.
        Friend ReadOnly Property ResidualRetention As Double
            Get
                If Not Mixer.IsIdentity OrElse PosterizeLevels <> 0 OrElse ThresholdLevel <> 0 Then Return 0
                Return 1.0 - Gradient.Amount / 100.0
            End Get
        End Property

        Friend Sub CopyTo(target As ImageAdjustments)
            ArgumentNullException.ThrowIfNull(target)
            Grade.CopyTo(target)
            Mixer.CopyTo(target)
            Gradient.CopyTo(target)
            target.CalibrationShadowTint = ShadowTint
            target.PosterizeLevels = PosterizeLevels
            target.ThresholdLevel = ThresholdLevel
        End Sub

        Friend Shared Sub Check(value As Single, parameter As String, minimum As Single, maximum As Single)
            If Not Single.IsFinite(value) OrElse value < minimum OrElse value > maximum Then Throw New ArgumentOutOfRangeException(parameter)
        End Sub
    End Class

    Friend NotInheritable Class RenderedGradeZone
        Friend Sub New(Optional hue As Single = 0, Optional saturation As Single = 0, Optional luminance As Single = 0)
            RenderedLateColorSettings.Check(hue, NameOf(hue), 0, 360)
            RenderedLateColorSettings.Check(saturation, NameOf(saturation), 0, 100)
            RenderedLateColorSettings.Check(luminance, NameOf(luminance), -100, 100)
            Me.Hue = hue : Me.Saturation = saturation : Me.Luminance = luminance
        End Sub
        Friend ReadOnly Property Hue As Single
        Friend ReadOnly Property Saturation As Single
        Friend ReadOnly Property Luminance As Single
        Friend ReadOnly Property IsIdentity As Boolean
            Get
                Return Saturation = 0 AndAlso Luminance = 0
            End Get
        End Property
    End Class

    Friend NotInheritable Class RenderedGradeSettings
        Friend Sub New(Optional shadow As RenderedGradeZone = Nothing, Optional midtone As RenderedGradeZone = Nothing,
                       Optional highlight As RenderedGradeZone = Nothing, Optional globalZone As RenderedGradeZone = Nothing,
                       Optional balance As Single = 0, Optional blending As Single = 50)
            RenderedLateColorSettings.Check(balance, NameOf(balance), -100, 100)
            RenderedLateColorSettings.Check(blending, NameOf(blending), 0, 100)
            Me.Shadow = If(shadow, New RenderedGradeZone())
            Me.Midtone = If(midtone, New RenderedGradeZone())
            Me.Highlight = If(highlight, New RenderedGradeZone())
            Me.GlobalZone = If(globalZone, New RenderedGradeZone())
            Me.Balance = balance : Me.Blending = blending
        End Sub
        Friend ReadOnly Property Shadow As RenderedGradeZone
        Friend ReadOnly Property Midtone As RenderedGradeZone
        Friend ReadOnly Property Highlight As RenderedGradeZone
        Friend ReadOnly Property GlobalZone As RenderedGradeZone
        Friend ReadOnly Property Balance As Single
        Friend ReadOnly Property Blending As Single
        Friend ReadOnly Property IsIdentity As Boolean
            Get
                Return Shadow.IsIdentity AndAlso Midtone.IsIdentity AndAlso Highlight.IsIdentity AndAlso GlobalZone.IsIdentity
            End Get
        End Property
        Friend Sub CopyTo(target As ImageAdjustments)
            target.ColorGradeShadowHue = Shadow.Hue
            target.ColorGradeShadowSaturation = Shadow.Saturation
            target.ColorGradeShadowLuminance = Shadow.Luminance
            target.ColorGradeMidtoneHue = Midtone.Hue
            target.ColorGradeMidtoneSaturation = Midtone.Saturation
            target.ColorGradeMidtoneLuminance = Midtone.Luminance
            target.ColorGradeHighlightHue = Highlight.Hue
            target.ColorGradeHighlightSaturation = Highlight.Saturation
            target.ColorGradeHighlightLuminance = Highlight.Luminance
            target.ColorGradeGlobalHue = GlobalZone.Hue
            target.ColorGradeGlobalSaturation = GlobalZone.Saturation
            target.ColorGradeGlobalLuminance = GlobalZone.Luminance
            target.ColorGradeBalance = Balance
            target.ColorGradeBlending = Blending
        End Sub
    End Class

    Friend NotInheritable Class RenderedMixerSettings
        Private Shared ReadOnly Identity As Single() = {100, 0, 0, 0, 0, 100, 0, 0, 0, 0, 100, 0}
        Private ReadOnly _matrix As Single()
        Private ReadOnly _monochrome As Boolean
        Private ReadOnly _gray As Single()

        ' Row-major RGB, four values per row: three percentages and a constant.
        ' Copy caller-owned arrays so an in-flight render cannot change halfway.
        Friend Sub New(Optional matrix As IEnumerable(Of Single) = Nothing, Optional monochrome As Boolean = False,
                       Optional grayRed As Single = 40, Optional grayGreen As Single = 40,
                       Optional grayBlue As Single = 20, Optional grayConstant As Single = 0)
            _matrix = If(matrix Is Nothing, Identity.ToArray(), matrix.ToArray())
            If _matrix.Length <> 12 Then Throw New ArgumentException("Expected three rows of four mixer coefficients.", NameOf(matrix))
            For i = 0 To 11
                Dim limit As Single = If(i Mod 4 = 3, 100, 200)
                RenderedLateColorSettings.Check(_matrix(i), NameOf(matrix), -limit, limit)
            Next
            _gray = {grayRed, grayGreen, grayBlue, grayConstant}
            For i = 0 To 3
                Dim limit As Single = If(i = 3, 100, 200)
                RenderedLateColorSettings.Check(_gray(i), "gray", -limit, limit)
            Next
            _monochrome = monochrome
        End Sub
        Friend ReadOnly Property IsIdentity As Boolean
            Get
                Return Not _monochrome AndAlso _matrix.SequenceEqual(Identity)
            End Get
        End Property
        Friend Sub CopyTo(target As ImageAdjustments)
            target.ChannelMixerRedRed = _matrix(0)
            target.ChannelMixerRedGreen = _matrix(1)
            target.ChannelMixerRedBlue = _matrix(2)
            target.ChannelMixerRedConstant = _matrix(3)
            target.ChannelMixerGreenRed = _matrix(4)
            target.ChannelMixerGreenGreen = _matrix(5)
            target.ChannelMixerGreenBlue = _matrix(6)
            target.ChannelMixerGreenConstant = _matrix(7)
            target.ChannelMixerBlueRed = _matrix(8)
            target.ChannelMixerBlueGreen = _matrix(9)
            target.ChannelMixerBlueBlue = _matrix(10)
            target.ChannelMixerBlueConstant = _matrix(11)
            target.ChannelMixerMonochrome = _monochrome
            target.ChannelMixerGrayRed = _gray(0)
            target.ChannelMixerGrayGreen = _gray(1)
            target.ChannelMixerGrayBlue = _gray(2)
            target.ChannelMixerGrayConstant = _gray(3)
        End Sub
    End Class

    Friend NotInheritable Class RenderedGradientSettings
        Friend Sub New(Optional shadow As String = "#FF000000", Optional highlight As String = "#FFFFFFFF", Optional amount As Single = 0)
            RenderedLateColorSettings.Check(amount, NameOf(amount), 0, 100)
            Me.Shadow = ColorString(shadow, NameOf(shadow))
            Me.Highlight = ColorString(highlight, NameOf(highlight))
            Me.Amount = amount
        End Sub
        Friend ReadOnly Property Shadow As String
        Friend ReadOnly Property Highlight As String
        Friend ReadOnly Property Amount As Single
        Private Shared Function ColorString(value As String, parameter As String) As String
            Dim parsed As SKColor
            If value Is Nothing OrElse Not SKColor.TryParse(value, parsed) Then Throw New ArgumentException("Expected an RGB/ARGB color.", parameter)
            ' Like the editor, map swatches supply RGB only. Image alpha is separate;
            ' a swatch alpha never changes the working frame's coverage.
            Return parsed.ToString()
        End Function
        Friend Sub CopyTo(target As ImageAdjustments)
            target.GradientMapShadowColor = Shadow
            target.GradientMapHighlightColor = Highlight
            target.GradientMapAmount = Amount
        End Sub
    End Class
End Namespace
