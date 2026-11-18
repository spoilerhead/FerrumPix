Imports System

Namespace Services
    ''' <summary>Diagnostische RAW-Entwicklung auf dem F16-Vertrag: Farbkorrektur vor Belichtung,
    ''' danach die vorhandene RAW-Rampenpolitik und Tonkurve. Noch kein Anschluss an den Editor;
    ''' Objektivkorrekturen liegen optional schon im Quellbild. Geometrie, Modell-Entrauschung
    ''' und die deaktivierte Schattenfarbkalibrierung sind hier nicht enthalten.</summary>
    Friend NotInheritable Class RawSceneDevelopment
        Private Sub New()
        End Sub

        Friend Structure ToneParameters
            Friend Sub New(exposureEv As Double, blackLevel As Double, knee As Double)
                Me.ExposureEv = exposureEv
                Me.BlackLevel = blackLevel
                Me.Knee = knee
            End Sub

            Friend ReadOnly Property ExposureEv As Double
            Friend ReadOnly Property BlackLevel As Double
            Friend ReadOnly Property Knee As Double
        End Structure

        ''' <summary>Die bisherige Rampe arbeitet auf dem heruntergeregelten RGB16-Cache,
        ''' die neue auf bereits normierten UND belichteten Szenenwerten. Beide muessen dieselbe
        ''' Kurve sehen: Weiss- und Schwarzpunkt mit genau diesem Koordinatenwechsel mitnehmen.
        ''' ToneRampFor bleibt die gemeinsame Quelle der Kamerabelichtungs-/Schwarzpunktpolitik.</summary>
        Friend Shared Function ParametersFor(baseExposureEv As Double, normalization As Double,
                                             exposureEv As Double, recoverHighlights As Boolean,
                                             knee As Double) As ToneParameters
            If Not Double.IsFinite(baseExposureEv) OrElse Math.Abs(baseExposureEv) > 10 Then Throw New ArgumentOutOfRangeException(NameOf(baseExposureEv))
            If Not Double.IsFinite(normalization) OrElse normalization <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(normalization))
            If Not Double.IsFinite(exposureEv) OrElse Math.Abs(exposureEv) > 10 Then Throw New ArgumentOutOfRangeException(NameOf(exposureEv))
            If Not Double.IsFinite(knee) OrElse knee < 0 OrElse knee > 1 Then Throw New ArgumentOutOfRangeException(NameOf(knee))

            Dim ramp = RawDecodeService.ToneRampFor(baseExposureEv + exposureEv, normalization, exposureEv)
            Dim scale = normalization * 2.0 ^ exposureEv
            If Not Double.IsFinite(scale) OrElse scale <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(normalization))
            Return New ToneParameters(ramp.ExposureEv - Math.Log(scale, 2.0),
                                      ramp.BlackLevel * scale, If(recoverHighlights, knee, 1.0))
        End Function

        ''' <summary>Nur RAW-Belichtung und RAW-Lichterparameter, kein vollstaendiges Editorrezept.
        ''' Die Benutzerbelichtung wirkt genau einmal, vor der Kurve. Farb-Lichterreparatur und
        ''' Schulter sind unabhaengig, wie im bisherigen Decode: Schulter aus schaltet nicht die
        ''' automatische Rosa-Lichterkorrektur aus.</summary>
        Friend Shared Function Render(source As RawDecodeService.SceneSource, exposureEv As Double,
                                      recoverHighlights As Boolean,
                                      Optional knee As Double = RawDecodeService.HighlightRecoveryKnee,
                                      Optional repairHighlightColor As Boolean = True) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            Dim parameters = ParametersFor(source.BaseExposureEv, source.Normalization, exposureEv, recoverHighlights, knee)
            Dim repaired As LinearWorkingFrame = Nothing
            Try
                If repairHighlightColor Then repaired = RawDecodeService.RepairSceneHighlights(source)
                Using exposed = SceneFrameOperations.ApplyExposure(If(repaired, source.Frame), exposureEv)
                    Return SceneFrameRenderer.Render(exposed, parameters.ExposureEv, parameters.BlackLevel, parameters.Knee)
                End Using
            Finally
                repaired?.Dispose()
            End Try
        End Function
    End Class
End Namespace
