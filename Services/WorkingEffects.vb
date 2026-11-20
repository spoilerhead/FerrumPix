Imports System

Namespace Services
    ''' <summary>Experimental rendered effects group. Fine chroma denoise has its
    ''' own pre-tone entry point; the other tools retain their actual detail-chain
    ''' order. Native F32 planes stay live between stages, with one F16 write at the
    ''' group boundary unless an extension hook needs an intermediate frame.</summary>
    Friend NotInheritable Class WorkingEffects
        Private Sub New()
        End Sub

        Private Shared Sub Validate(source As LinearWorkingFrame)
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Rendered input required.", NameOf(source))
        End Sub

        Private Shared Function Copy(source As LinearWorkingFrame) As LinearWorkingFrame
            Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain, Sub(y, row) source.ReadLinearRow(y, row))
        End Function

        Friend Shared Function Prepare(source As LinearWorkingFrame, settings As WorkingEffectSettings) As LinearWorkingFrame
            Validate(source)
            ArgumentNullException.ThrowIfNull(settings)
            Dim a = settings.Snapshot()
            If a.ColorNoiseReduction = 0 Then Return Copy(source)
            Dim planes = WorkingEffectPlanes.Load(source)
            ImageProcessor.WorkingColorDenoise(planes, a.ColorNoiseReduction / 100.0F, 0, rootSpace:=True)
            Return planes.ToFrame()
        End Function

        Friend Shared Function Apply(source As LinearWorkingFrame, settings As WorkingEffectSettings,
                                     Optional beforeGrain As Func(Of LinearWorkingFrame, LinearWorkingFrame) = Nothing,
                                     Optional afterGrain As Func(Of LinearWorkingFrame, LinearWorkingFrame) = Nothing,
                                     Optional trace As Action(Of String) = Nothing) As LinearWorkingFrame
            Validate(source)
            ArgumentNullException.ThrowIfNull(settings)
            Dim a = settings.Snapshot()
            Dim active = (a.FilterPreset = "weich" AndAlso a.FilterStrength > 0) OrElse a.Clarity <> 0 OrElse a.Structure <> 0 OrElse
                a.Haze <> 0 OrElse a.NoiseReduction > 0 OrElse a.Blur > 0 OrElse a.FarbrauschGrob > 0 OrElse a.ColorNoiseAdd > 0 OrElse
                a.DustScratches <> 0 OrElse a.Glow <> 0 OrElse a.Sharpness > 0 OrElse a.TiltShiftAmount > 0 OrElse a.Vignette <> 0 OrElse
                a.Grain > 0 OrElse a.AddNoise <> 0
            If Not active AndAlso beforeGrain Is Nothing AndAlso afterGrain Is Nothing Then Return Copy(source)
            Dim p = WorkingEffectPlanes.Load(source)
            Dim stage = Sub(name As String) trace?.Invoke(name)
            If a.FilterPreset = "weich" AndAlso a.FilterStrength > 0 Then
                stage("soft-focus") : p.Mix(p.Blurred(0.25F + 0.3F * 2.2F), a.FilterStrength / 100.0F, mixAlpha:=True)
            End If
            If a.Clarity <> 0 Then stage("clarity") : ImageProcessor.WorkingClarity(p, a.Clarity / 100.0F)
            If a.Structure <> 0 Then stage("structure") : ImageProcessor.WorkingStructure(p, a.Structure / 100.0F)
            If a.Haze <> 0 Then stage("haze") : ImageProcessor.WorkingHaze(p, a.Haze / 100.0F)
            If a.NoiseReduction > 0 Then
                stage("noise-" & a.NoiseReductionMethod.ToString())
                Select Case a.NoiseReductionMethod
                    Case NoiseReductionMethod.Median
                        p = ImageProcessor.WorkingMedian(p, a.NoiseReduction / 100.0F)
                    Case NoiseReductionMethod.Guided, NoiseReductionMethod.AdaptiveGuided
                        ImageProcessor.WorkingGuided(p, a.NoiseReduction / 100.0F, a.NoiseReductionDetail / 100.0F,
                                                     a.NoiseReductionMethod = NoiseReductionMethod.AdaptiveGuided)
                    Case NoiseReductionMethod.Gaussian
                        ImageProcessor.WorkingGaussianNoise(p, a.NoiseReduction / 100.0F, a.NoiseReductionDetail / 100.0F)
                End Select
            End If
            If a.Blur > 0 Then
                stage("blur")
                Dim sigma = ImageProcessor.WorkingImageBlurSigma(p.Width, p.Height, a.Blur)
                If sigma >= 0.5F Then p = p.Blurred(sigma)
            End If
            If a.FarbrauschGrob > 0 Then
                stage("color-coarse") : ImageProcessor.WorkingColorDenoise(p, a.FarbrauschGrob / 100.0F, a.ColorNoiseCoarseScale / 100.0F, rootSpace:=False)
            End If
            If a.ColorNoiseAdd > 0 Then stage("color-add") : ImageProcessor.WorkingAddedNoise(p, a.ColorNoiseAdd / 100.0F, colorOnly:=True)
            If a.DustScratches <> 0 Then stage("dust") : ImageProcessor.WorkingDust(p, a.DustScratches / 100.0F)
            If a.Glow <> 0 Then stage("glow") : ImageProcessor.WorkingGlow(p, a.Glow / 100.0F)
            If a.Sharpness > 0 Then stage("sharpen-" & a.SharpenMethod.ToString()) : ImageProcessor.WorkingSharpen(p, a)
            If a.TiltShiftAmount > 0 Then stage("tilt-shift") : ImageProcessor.WorkingTiltShift(p, a)
            If a.Vignette <> 0 Then stage("vignette") : ImageProcessor.WorkingVignette(p, a)
            If beforeGrain IsNot Nothing Then stage("before-grain") : p = Hook(p, beforeGrain)
            If a.Grain > 0 Then stage("grain") : ImageProcessor.WorkingGrain(p, a)
            If afterGrain IsNot Nothing Then stage("after-grain") : p = Hook(p, afterGrain)
            If a.AddNoise > 0 Then
                stage("add-noise") : ImageProcessor.WorkingAddedNoise(p, a.AddNoise / 100.0F, colorOnly:=False)
            ElseIf a.AddNoise < 0 Then
                stage("minus-noise") : ImageProcessor.WorkingGaussianNoise(p, -a.AddNoise / 100.0F, 0)
            End If
            Return p.ToFrame()
        End Function

        Private Shared Function Hook(p As WorkingEffectPlanes, effect As Func(Of LinearWorkingFrame, LinearWorkingFrame)) As WorkingEffectPlanes
            ' Hook borrows its input and returns an owned result, or that same
            ' input for identity. Never transfer ownership of the caller's source.
            Using input = p.ToFrame()
                Dim output = effect(input)
                If output Is Nothing Then Throw New InvalidOperationException("Effect hook returned no image.")
                Try
                    If output.Width <> p.Width OrElse output.Height <> p.Height OrElse output.Domain <> p.Domain Then
                        Throw New ArgumentException("Effect hook changed dimensions or domain.")
                    End If
                    Return WorkingEffectPlanes.Load(output)
                Finally
                    If output IsNot input Then output.Dispose()
                End Try
            End Using
        End Function
    End Class
End Namespace
