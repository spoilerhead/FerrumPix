Imports System
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports SkiaSharp

Namespace Services
    ''' <summary>Opt-in, single-image editor preview. This orchestrates the same
    ''' working-frame operators as the probe; it never starts from the editor's
    ''' already-developed RGB8 preview or enters its legacy intermediate caches.</summary>
    Friend NotInheritable Class WorkingEditorPipeline
        Private Sub New()
        End Sub

        Friend Shared Sub Validate(recipe As ImageAdjustments)
            ArgumentNullException.ThrowIfNull(recipe)
            Dim unsupported As New Collections.Generic.List(Of String)
            If recipe.Annotations IsNot Nothing AndAlso recipe.Annotations.Any(Function(a) a IsNot Nothing AndAlso a.IsVisible) Then unsupported.Add("objects")
            If recipe.MaskedAdjustmentLayers IsNot Nothing AndAlso recipe.MaskedAdjustmentLayers.Count > 0 Then unsupported.Add("adjustment layers")
            If recipe.RetouchSpots IsNot Nothing AndAlso recipe.RetouchSpots.Count > 0 Then unsupported.Add("retouch")
            If recipe.RasterPaintStrokes IsNot Nothing AndAlso recipe.RasterPaintStrokes.Count > 0 Then unsupported.Add("paint")
            If recipe.BakedOperations IsNot Nothing AndAlso recipe.BakedOperations.Count > 0 Then unsupported.Add("baked operations")
            If recipe.SelectionScopeEnabled Then unsupported.Add("selection-scoped adjustments")
            If recipe.PixelLayerHidden OrElse recipe.BackgroundHidden Then unsupported.Add("hidden source layers")
            If recipe.RawSensorEdgeCrop OrElse recipe.RawCameraAspectCrop Then unsupported.Add("RAW sensor/aspect crop")
            If Not String.IsNullOrWhiteSpace(recipe.LutPath) AndAlso recipe.LutStrength > 0 Then unsupported.Add("LUT")
            If Not {"", "keine", "none", "weich"}.Contains(If(recipe.FilterPreset, "").ToLowerInvariant()) AndAlso recipe.FilterStrength > 0 Then unsupported.Add("filter preset")
            If recipe.LightSources IsNot Nothing AndAlso recipe.LightSources.Any(Function(l) l IsNot Nothing AndAlso l.Enabled AndAlso l.UseDepth) Then unsupported.Add("depth-aware lighting")
            If unsupported.Count > 0 Then
                Throw New NotSupportedException(LocalizationService.T("Die neue Pipeline unterstützt diese Vorschau noch nicht:") & " " & String.Join(", ", unsupported))
            End If
        End Sub

        Friend Shared Function Render(path As String, recipe As ImageAdjustments, maximum As Integer,
                                      cancel As CancellationToken, Optional before As Boolean = False) As LinearWorkingFrame
            If maximum < 0 Then Throw New ArgumentOutOfRangeException(NameOf(maximum))
            Dim a = recipe.Clone()
            Validate(a)
            ' Some older recipes have Kelvin values but no model number. Preserve
            ' relative legacy controls; absolute controls require the current model.
            If a.WhiteBalanceModel < 2 AndAlso (a.WhiteBalanceKelvin <> 0 OrElse a.WhiteBalanceKelvinTint <> 0) Then
                a.WhiteBalanceModel = ImageProcessor.CurrentWhiteBalanceModel
            End If
            cancel.ThrowIfCancellationRequested()
            Dim raw = RawPreviewService.IsSupportedRaw(path), exposure = a.Exposure / CDbl(ImageAdjustments.ExposurePointsPerStop)
            Dim original As LinearWorkingFrame = Nothing
            Try
                Dim wbOnRamp = False
                If raw Then
                    Dim wb As RawWhiteBalanceSettings = Nothing, calibration As RawPrimaryCalibrationSettings = Nothing
                    Dim primarySet = HasPrimaryCalibration(a)
                    If Not before AndAlso Not a.GlobalAdjustmentsHidden AndAlso Not a.NegativeEnabled AndAlso
                       (a.WhiteBalanceModel >= 4 OrElse a.WhiteBalanceModel = 2 OrElse primarySet) Then
                        If a.WhiteBalanceAnchorX <= 0 OrElse a.WhiteBalanceAnchorY <= 0 Then
                            Dim capture = CaptureWhiteBalanceService.ForRawFile(path)
                            If capture IsNot Nothing Then
                                a.WhiteBalanceAnchorX = capture.X : a.WhiteBalanceAnchorY = capture.Y : a.WhiteBalanceCamera = capture.Camera
                            End If
                        End If
                        a.SourceFollowsRawExposure = True
                        ImageProcessor.UpgradeWhiteBalanceModel(a)
                        If primarySet AndAlso Not ImageProcessor.WhiteBalanceOnRamp(a) Then
                            Throw New NotSupportedException("Primary calibration requires a current RAW white balance with a capture anchor.")
                        End If
                        If ImageProcessor.WhiteBalanceOnRamp(a) Then
                            wb = RawWhiteBalanceSettings.FromAdjustments(a)
                            If primarySet Then calibration = RawPrimaryCalibrationSettings.FromAdjustments(a)
                        End If
                    End If
                    wbOnRamp = wb IsNot Nothing
                    Using scene = RawDecodeService.ReadSceneSource(path, modelDenoiseStrength:=If(a.RawModelDenoise AndAlso Not before, CDbl(a.RawModelDenoiseStrength), -1),
                                                                  lensChoice:=ImageProcessor.LensChoiceFrom(a))
                        original = RawSceneDevelopment.Render(scene, If(before OrElse a.GlobalAdjustmentsHidden, 0, exposure), a.RawHighlightRecovery,
                                                              If(before OrElse a.GlobalAdjustmentsHidden, RawDecodeService.HighlightRecoveryKnee, a.RawHighlightKnee()), whiteBalance:=wb, primaryCalibration:=calibration)
                        If scene.Distortion IsNot Nothing Then
                            Dim corrected = WorkingGeometry.CorrectLensDistortion(original, scene.Distortion, cancel)
                            original.Dispose()
                            original = corrected
                        End If
                    End Using
                Else
                    original = DecodeRendered(path)
                    If HasPrimaryCalibration(a) Then
                        Throw New NotSupportedException("Native primary calibration currently requires a positive RAW source.")
                    End If
                End If
                cancel.ThrowIfCancellationRequested()
                Using geometry = WorkingGeometry.Apply(original, a), background = WorkingComposite.FillBackground(geometry, a.CanvasBackgroundColor),
                      preview = Fit(background, If(maximum = 0, 0, Math.Max(1, CInt(Math.Round(Math.Max(background.Width, background.Height) * Math.Min(1, maximum / CDbl(Math.Max(original.Width, original.Height))))))))
                    If before OrElse a.GlobalAdjustmentsHidden Then Return Copy(preview)
                    Dim effects As New WorkingEffectSettings(a)
                    Using prepared = WorkingEffects.Prepare(preview, effects)
                        Dim input = prepared
                        Dim ownInput As LinearWorkingFrame = Nothing
                        Try
                            If Not wbOnRamp Then
                                Dim film As New RenderedNegativeSettings(a.NegativeEnabled, a.NegativeBaseColor, a.NegativeDensityColor, a.NegativeGamma, a.NegativeMonochrome)
                                ownInput = RenderedInputControls.Apply(input, New RenderedWhiteBalanceSettings(a.WhiteBalanceModel, a.Temperature, a.Tint, a.WhiteBalanceKelvin, a.WhiteBalanceKelvinTint), film)
                                input = ownInput
                            End If
                            cancel.ThrowIfCancellationRequested()
                            Using lit = ImageProcessor.ApplyWorkingLightSources(input, a.LightSources, a.LightAmbient),
                                  toned = RenderedBasicControls.Apply(lit,
                                      New RenderedBasicSettings(a.Highlights, a.ShadowsLevel, a.Whites, a.Blacks, a.Saturation, a.Vibrance, a.Brightness, a.Contrast,
                                                                If(raw, 0, exposure), If(raw, exposure, 0)),
                                      New RenderedCurveSettings(a.CurveRgbPoints, a.CurveRedPoints, a.CurveGreenPoints, a.CurveBluePoints, a.CurveLuminancePoints),
                                      LateSettings(a), HslSettings(a))
                                cancel.ThrowIfCancellationRequested()
                                Dim result = WorkingEffects.Apply(toned, effects,
                                    Function(frame) ImageProcessor.ApplyWorkingExtensions(frame, a, "before-grain", cancel),
                                    Function(frame) ImageProcessor.ApplyWorkingExtensions(frame, a, "after-grain", cancel))
                                If cancel.IsCancellationRequested Then result.Dispose() : cancel.ThrowIfCancellationRequested()
                                Return result
                            End Using
                        Finally
                            ownInput?.Dispose()
                        End Try
                    End Using
                End Using
            Finally
                original?.Dispose()
            End Try
        End Function

        Private Shared Function HasPrimaryCalibration(a As ImageAdjustments) As Boolean
            Return a.CalibrationRedHue <> 0 OrElse a.CalibrationRedSaturation <> 0 OrElse
                   a.CalibrationGreenHue <> 0 OrElse a.CalibrationGreenSaturation <> 0 OrElse
                   a.CalibrationBlueHue <> 0 OrElse a.CalibrationBlueSaturation <> 0
        End Function

        Friend Shared Function RenderDisplay(path As String, recipe As ImageAdjustments, maximum As Integer,
                                             cancel As CancellationToken, Optional before As Boolean = False) As SKBitmap
            Using frame = Render(path, recipe, maximum, cancel, before), display = WorkingImageBoundary.ToDisplaySdrSrgb8(frame)
                ' The persistent editor scene/blitter expects RGBA8888. This is
                ' after the sole display conversion, never an image working stage.
                Return display.Copy(SKColorType.Rgba8888)
            End Using
        End Function

        Private Shared Function DecodeRendered(path As String) As LinearWorkingFrame
            If TiffPreviewService.IsSupportedTiff(path) Then
                Using decoded = ProfiledTiffInput.Decode(path)
                    Return Copy(decoded.Frame)
                End Using
            End If
            Dim extension = IO.Path.GetExtension(path).ToLowerInvariant()
            If {".jpg", ".jpeg"}.Contains(extension) Then
                Try
                    Using decoded = ProfiledJpegInput.Decode(path)
                        Return Copy(decoded.Frame)
                    End Using
                Catch ex As Exception When TypeOf ex Is NotSupportedException OrElse TypeOf ex Is InvalidDataException
                    If Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)).Contains("ICC_PROFILE", StringComparison.Ordinal) Then Throw
                    ' Only untagged/sRGB originals may use the native source RGB8
                    ' boundary. Never hide an unsupported wide profile in sRGB.
                    Return DecodeSrgb(path)
                End Try
            End If
            If extension = ".png" Then Return DecodeSrgb(path)
            Throw New NotSupportedException("Native preview input currently supports RAW, matrix-profile JPEG, supported RGB16 TIFF and sRGB PNG.")
        End Function

        Private Shared Function DecodeSrgb(path As String) As LinearWorkingFrame
            Using codec = SKCodec.Create(path)
                If codec Is Nothing OrElse (codec.Info.ColorSpace IsNot Nothing AndAlso Not codec.Info.ColorSpace.IsSrgb) Then
                    Throw New NotSupportedException("This source profile needs an additional native input adapter.")
                End If
                If codec.EncodedOrigin <> SKEncodedOrigin.TopLeft Then Throw New NotSupportedException("Oriented unprofiled input needs an additional native input adapter.")
                Using bitmap As New SKBitmap(New SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
                    If codec.GetPixels(bitmap.Info, bitmap.GetPixels()) <> SKCodecResult.Success Then Throw New InvalidDataException("Native source decode failed.")
                    Return WorkingImageBoundary.FromEncodedSrgb(bitmap)
                End Using
            End Using
        End Function

        Private Shared Function Copy(source As LinearWorkingFrame) As LinearWorkingFrame
            Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain, Sub(y, row) source.ReadLinearRow(y, row))
        End Function

        Private Shared Function Fit(source As LinearWorkingFrame, maximum As Integer) As LinearWorkingFrame
            Dim factor = If(maximum = 0, 1, Math.Min(1, maximum / CDbl(Math.Max(source.Width, source.Height))))
            If factor = 1 Then Return Copy(source)
            Return WorkingEffectPlanes.Load(source).ResizeTo(Math.Max(1, CInt(Math.Round(source.Width * factor))), Math.Max(1, CInt(Math.Round(source.Height * factor)))).ToFrame()
        End Function

        Private Shared Function HslSettings(a As ImageAdjustments) As RenderedHslPointSettings
            Dim values = {"Red", "Orange", "Yellow", "Green", "Aqua", "Blue", "Purple", "Magenta"}.
                SelectMany(Function(band) {"Hue", "Saturation", "Luminance"}.Select(Function(channel) CSng(GetType(ImageAdjustments).GetProperty(band & channel).GetValue(a))))
            Return New RenderedHslPointSettings(values, PointColorSwatch.ParseList(a.PointColors))
        End Function

        Private Shared Function LateSettings(a As ImageAdjustments) As RenderedLateColorSettings
            Return New RenderedLateColorSettings(
                New RenderedGradeSettings(New RenderedGradeZone(a.ColorGradeShadowHue, a.ColorGradeShadowSaturation, a.ColorGradeShadowLuminance),
                    New RenderedGradeZone(a.ColorGradeMidtoneHue, a.ColorGradeMidtoneSaturation, a.ColorGradeMidtoneLuminance),
                    New RenderedGradeZone(a.ColorGradeHighlightHue, a.ColorGradeHighlightSaturation, a.ColorGradeHighlightLuminance),
                    New RenderedGradeZone(a.ColorGradeGlobalHue, a.ColorGradeGlobalSaturation, a.ColorGradeGlobalLuminance), a.ColorGradeBalance, a.ColorGradeBlending),
                New RenderedMixerSettings({a.ChannelMixerRedRed, a.ChannelMixerRedGreen, a.ChannelMixerRedBlue, a.ChannelMixerRedConstant,
                    a.ChannelMixerGreenRed, a.ChannelMixerGreenGreen, a.ChannelMixerGreenBlue, a.ChannelMixerGreenConstant,
                    a.ChannelMixerBlueRed, a.ChannelMixerBlueGreen, a.ChannelMixerBlueBlue, a.ChannelMixerBlueConstant},
                    a.ChannelMixerMonochrome, a.ChannelMixerGrayRed, a.ChannelMixerGrayGreen, a.ChannelMixerGrayBlue, a.ChannelMixerGrayConstant),
                New RenderedGradientSettings(a.GradientMapShadowColor, a.GradientMapHighlightColor, a.GradientMapAmount), a.CalibrationShadowTint,
                If(a.PosterizeLevels >= 2, CInt(Math.Round(a.PosterizeLevels)), 0), If(a.ThresholdLevel >= 1, CInt(Math.Round(a.ThresholdLevel)), 0))
        End Function
    End Class
End Namespace
