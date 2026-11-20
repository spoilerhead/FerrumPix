Imports System
Imports System.Collections.Generic
Imports System.Reflection

Namespace Services
    ''' <summary>Immutable snapshot of the scalar spatial/effect controls only.
    ''' A small explicit schema avoids cloning masks, objects or baked image data
    ''' just to run these stages, and keeps accepted GUI ranges in one place.</summary>
    Friend NotInheritable Class WorkingEffectSettings
        Private ReadOnly _recipe As ImageAdjustments
        Private Shared ReadOnly SignedControls As String() = {"Clarity", "Structure", "Haze", "Glow", "DustScratches", "AddNoise", "VignetteRoundness"}
        Private Shared ReadOnly UnsignedControls As String() = {
            "NoiseReduction", "NoiseReductionDetail", "ColorNoiseReduction", "FarbrauschGrob", "ColorNoiseCoarseScale",
            "ColorNoiseAdd", "Blur", "SharpenRadius", "SharpenDetail", "SharpenMasking", "TiltShiftAmount", "TiltShiftCenterX",
            "TiltShiftCenterY", "TiltShiftFocus", "TiltShiftTransition", "VignetteTransition", "VignetteFeather",
            "VignetteCenterX", "VignetteCenterY", "Grain", "GrainSize", "GrainFrequency", "GrainColor", "FilterStrength"}

        Friend Sub New(Optional source As ImageAdjustments = Nothing)
            source = If(source, New ImageAdjustments())
            _recipe = New ImageAdjustments()
            For Each name In SignedControls
                CopyNumber(source, name, -100, 100)
            Next
            For Each name In UnsignedControls
                CopyNumber(source, name, 0, 100)
            Next
            CopyNumber(source, "Sharpness", 0, 150)
            CopyNumber(source, "Vignette", -150, 150)
            CopyNumber(source, "TiltShiftAngle", -90, 90)
            If Not [Enum].IsDefined(source.SharpenMethod) OrElse Not [Enum].IsDefined(source.NoiseReductionMethod) OrElse
               Not [Enum].IsDefined(source.VignetteStyle) Then Throw New ArgumentException("Unknown effect method.")
            _recipe.SharpenMethod = source.SharpenMethod
            _recipe.NoiseReductionMethod = source.NoiseReductionMethod
            _recipe.VignetteStyle = source.VignetteStyle
            _recipe.FilterPreset = If(String.Equals(source.FilterPreset, "weich", StringComparison.OrdinalIgnoreCase), "weich", "")
        End Sub

        Private Sub CopyNumber(source As ImageAdjustments, name As String, minimum As Single, maximum As Single)
            Dim member = GetType(ImageAdjustments).GetProperty(name, BindingFlags.Instance Or BindingFlags.Public)
            If member Is Nothing OrElse member.PropertyType <> GetType(Single) Then Throw New InvalidOperationException("Invalid effect control schema: " & name)
            Dim value = CSng(member.GetValue(source))
            RenderedLateColorSettings.Check(value, name, minimum, maximum)
            member.SetValue(_recipe, value)
        End Sub

        Friend Function Snapshot() As ImageAdjustments
            ' Only scalar fields were populated, so this clone is small and cannot
            ' give the caller access to the internally validated settings.
            Return _recipe.Clone()
        End Function
    End Class
End Namespace
