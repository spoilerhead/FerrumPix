Imports System
Imports System.IO
Imports System.Linq

Namespace Services
    ''' <summary>Diagnostischer Einstieg VOR Convert16Rows: keine Entwicklung ueber ein RGB8-Bild.
    ''' Der vorhandene lineare LibRaw-Speicher bleibt unveraendert. Noch kein Anschluss an Editor,
    ''' Objektivkorrekturen oder die Tonstufe; diese Grenzen muessen einzeln geprueft werden.</summary>
    Partial Public NotInheritable Class RawDecodeService
        Friend NotInheritable Class SceneSource
            Implements IDisposable

            Friend Sub New(frame As LinearWorkingFrame, normalization As Double, baseExposureEv As Double,
                           Optional lensVignettingApplied As Boolean = False, Optional lensName As String = "")
                Me.Frame = frame
                Me.Normalization = normalization
                Me.BaseExposureEv = baseExposureEv
                Me.LensCorrectionsApplied = lensVignettingApplied
                Me.LensVignettingApplied = lensVignettingApplied
                Me.LensName = lensName
            End Sub

            Friend ReadOnly Property Frame As LinearWorkingFrame
            Friend ReadOnly Property Normalization As Double
            Friend ReadOnly Property BaseExposureEv As Double
            ' LibRaw hat Weissabgleich und Demosaic bereits ausgefuehrt. Ein spaeterer Regler ist
            ' eine Aenderung relativ zu diesem Stand und darf den Kameraabgleich nicht verdoppeln.
            Friend ReadOnly Property LibRawWhiteBalanceAlreadyApplied As Boolean = True
            Friend ReadOnly Property LensCorrectionsApplied As Boolean
            Friend ReadOnly Property LensVignettingApplied As Boolean
            Friend ReadOnly Property LensName As String
            Friend ReadOnly Property LensChromaticAberrationApplied As Boolean = False
            Friend ReadOnly Property GeometryApplied As Boolean = False
            Friend ReadOnly Property ToneCurveApplied As Boolean = False
            Friend ReadOnly Property ModelDenoiseApplied As Boolean = False

            Public Sub Dispose() Implements IDisposable.Dispose
                Frame.Dispose()
            End Sub
        End Class

        Friend Shared Function ReadSceneSource(path As String, Optional lensVignetting As Boolean = False) As SceneSource
            If String.IsNullOrWhiteSpace(path) Then Throw New ArgumentException("A RAW path is required.", NameOf(path))
            path = IO.Path.GetFullPath(path)
            If Not RawPreviewService.SupportedExtensions.Contains(IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) Then
                Throw New NotSupportedException("Only sensor RAW sources can enter this scene path.")
            End If
            If Not DevelopsThroughRawStage(path) Then Throw New NotSupportedException("A linear LibRaw decode is unavailable for this source.")
            If Not WarmRawCache(path) Then Throw New InvalidOperationException("Cannot prepare the full-resolution linear RAW cache.")

            Dim bytes As Byte(), width As Integer, height As Integer, normalization As Double, matrix As Single()
            SyncLock _cacheLock
                ' Ein Bildwechsel darf nicht versehentlich Daten der naechsten Datei liefern.
                ' Die Referenzen bleiben gueltig: Cache-Inhalte sind nach dem Fuellen unveraenderlich.
                If Not HasRawCacheFor(path) OrElse _cachedRawToneMatrix Is Nothing Then
                    Throw New InvalidOperationException("The current full-resolution wide-tone RAW cache is unavailable or changed.")
                End If
                bytes = _cachedRawBytes
                width = _cachedRawWidth : height = _cachedRawHeight
                normalization = _cachedRawNormalization
                matrix = CType(_cachedRawToneMatrix.Clone(), Single())
            End SyncLock
            Dim baseEv = BaseExposureForFile(path)
            ' Explizite Diagnosewahl statt Aenderung der gespeicherten App-Vorgabe. TCA und
            ' Verzeichnung bleiben aus; deren Abtastung wird in einer eigenen Iteration migriert.
            Dim lens = If(lensVignetting, LensCorrectionForFile(path, New LensDataService.Wahl With {
                .Vignetting = True, .ChromaticAberration = False, .Distortion = False}), Nothing)
            Dim frame = BuildSceneFrame(width, height, bytes, matrix, normalization, lens)
            Return New SceneSource(frame, normalization, baseEv, lens IsNot Nothing AndAlso lens.HasVignetting, If(lens?.LensName, ""))
        End Function

        ''' <summary>Cache-RGB16 nach linearem Rec.2020 D65. Die Normalisierung stellt den von
        ''' LibRaw zurueckgenommenen Kopfraum wieder her: 1 bleibt Referenzweiss, Werte darueber
        ''' bleiben erhalten. Grundbelichtung, Schwarzabzug und Tonkurve werden NICHT eingebrannt.</summary>
        Friend Shared Function BuildSceneFrame(width As Integer, height As Integer, bytes As Byte(),
                                               cameraToSrgb As Single(), normalization As Double,
                                               Optional lens As LensDataService.Korrektur = Nothing) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(bytes)
            ArgumentNullException.ThrowIfNull(cameraToSrgb)
            If width <= 0 OrElse height <= 0 OrElse CLng(width) * height > Integer.MaxValue \ 16 Then
                Throw New ArgumentOutOfRangeException(NameOf(width), "RAW scene dimensions exceed the supported staging size.")
            End If
            If bytes.LongLength <> CLng(width) * height * 6 Then
                Throw New ArgumentException("RGB16 dimensions do not match the RAW cache.")
            End If
            If cameraToSrgb.Length <> 9 OrElse cameraToSrgb.Any(Function(v) Not Single.IsFinite(v)) Then
                Throw New ArgumentException("A finite camera-to-sRGB matrix is required.", NameOf(cameraToSrgb))
            End If
            If Not Double.IsFinite(normalization) OrElse normalization <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(normalization))

            ' D65 -> D65: mit der Kameramatrix zusammenfassen, damit zwischen beiden Raeumen
            ' weder quantisiert noch geklemmt wird. Die Rechnung pro Bildpunkt bleibt F32.
            Dim toRec = {0.627403896, 0.329283038, 0.043313066,
                         0.069097289, 0.919540395, 0.011362316,
                         0.016391439, 0.088013308, 0.895595253}
            Dim combined(8) As Single
            For r = 0 To 2
                For c = 0 To 2
                    Dim value As Double = 0
                    For k = 0 To 2
                        value += toRec(r * 3 + k) * cameraToSrgb(k * 3 + c)
                    Next
                    combined(r * 3 + c) = CSng(value * normalization / 65535.0)
                    If Not Single.IsFinite(combined(r * 3 + c)) Then Throw New ArgumentOutOfRangeException(NameOf(normalization))
                Next
            Next

            Dim vignette = lens IsNot Nothing AndAlso lens.HasVignetting
            Dim normScale = LensDataService.NormScaleFor(lens, width, height)
            Dim cx = (width - 1) / 2.0, cy = (height - 1) / 2.0
            Dim planeGains = If(vignette AndAlso lens.GainMaps IsNot Nothing,
                                lens.GainMaps.SamplerFor(width, height, lens.VignettingStrength), Nothing)

            Return LinearWorkingFrame.Create(width, height, WorkingImageDomain.SceneLinear,
                Sub(y, row)
                    Dim start = y * width * 6
                    For x = 0 To width - 1
                        Dim at = start + x * 6
                        ' LibRaw liefert native RGB16; den Cache lesen, ohne einen zweiten
                        ' vollstaendigen 16-Bit- oder F32-Puffer anzulegen.
                        Dim r = CSng(BitConverter.ToUInt16(bytes, at))
                        Dim g = CSng(BitConverter.ToUInt16(bytes, at + 2))
                        Dim b = CSng(BitConverter.ToUInt16(bytes, at + 4))
                        If vignette Then
                            Dim dx = x - cx, dy = y - cy
                            Dim radius = Math.Sqrt(dx * dx + dy * dy) * normScale * lens.CornerScale
                            ' Dieselbe Achse und 3-EV-Begrenzung wie Convert16Rows. Die Korrektur
                            ' ist eine Verstaerkung, daher durch den gemessenen Lichtabfall teilen.
                            Dim falloff = Math.Max(0.125, LensDataService.VignettingFactor(lens, radius))
                            Dim gain = CSng(1.0 / falloff)
                            Dim gr = 1.0F, gg = 1.0F, gb = 1.0F
                            If planeGains IsNot Nothing Then planeGains.GainsAt(x, y, gr, gg, gb)
                            ' Ebenenkarten gehoeren VOR die Kameramatrix. Auf Rec.2020-Kanaele
                            ' angewandt aenderten sie den Farbton statt die Sensorebenen zu korrigieren.
                            r *= gain : g *= gain : b *= gain
                            r *= gr : g *= gg : b *= gb
                        End If
                        Dim target = x * 4
                        row(target) = combined(0) * r + combined(1) * g + combined(2) * b
                        row(target + 1) = combined(3) * r + combined(4) * g + combined(5) * b
                        row(target + 2) = combined(6) * r + combined(7) * g + combined(8) * b
                        row(target + 3) = 1
                    Next
                End Sub)
        End Function
    End Class
End Namespace
