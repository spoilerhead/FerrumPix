Imports System
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services
    ''' <summary>Linear beschreibt die Transferfunktion, nicht die Bedeutung der Werte.
    ''' Ein bereits tonwertkorrigiertes Bild wird durch Linearisieren nicht wieder zur Szene.</summary>
    Friend Enum WorkingImageDomain
        SceneLinear
        RenderedLinear
    End Enum

    ''' <summary>Unveraenderliches Arbeitsbild: lineares Rec.2020, D65, RGBA F16, vormultipliziert.
    ''' Noch NICHT an die normale Pipeline angeschlossen. Nur benannte Grenzen duerfen es in
    ''' sRGB/Bytes umsetzen; die Ablage selbst begrenzt Farbwerte weder auf null noch auf eins.</summary>
    Friend NotInheritable Class LinearWorkingFrame
        Implements IDisposable

        Private ReadOnly _bitmap As SKBitmap
        Private _disposed As Boolean

        Private Sub New(bitmap As SKBitmap, domain As WorkingImageDomain)
            _bitmap = bitmap
            Me.Domain = domain
            Width = bitmap.Width
            Height = bitmap.Height
            StorageBytes = CLng(bitmap.RowBytes) * bitmap.Height
        End Sub

        Friend ReadOnly Property Width As Integer
        Friend ReadOnly Property Height As Integer
        Friend ReadOnly Property Domain As WorkingImageDomain
        Friend ReadOnly Property ColorType As SKColorType = SKColorType.RgbaF16
        Friend ReadOnly Property AlphaType As SKAlphaType = SKAlphaType.Premul
        Friend ReadOnly Property Primaries As String = "Rec.2020"
        Friend ReadOnly Property WhitePoint As String = "D65"
        Friend ReadOnly Property IsLinear As Boolean = True

        Friend ReadOnly Property StorageBytes As Long

        ''' <summary>Erzeugt aus ausdruecklich deklarierten linearen, vormultiplizierten
        ''' Rec.2020-Werten. Der Aufrufer behaelt und darf seine Eingabe weiter veraendern.</summary>
        Friend Shared Function FromLinearPixels(width As Integer, height As Integer, values As Single(),
                                                domain As WorkingImageDomain) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(values)
            ValidateDimensions(width, height)
            If values.LongLength <> CLng(width) * height * 4 Then Throw New ArgumentException("RGBA dimensions do not match.", NameOf(values))
            Return Create(width, height, domain, Sub(y, row) Array.Copy(values, y * row.Length, row, 0, row.Length))
        End Function

        ''' <summary>Nur ein F32-Bild mit bereits bekannten linearen Rec.2020-Werten. Keine
        ''' automatische Profilwandlung: die Farbbedeutung muss der Aufrufer kennen.</summary>
        Friend Shared Function FromLinearBitmap(source As SKBitmap, domain As WorkingImageDomain) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            If source.ColorType <> SKColorType.RgbaF32 OrElse source.AlphaType <> SKAlphaType.Premul OrElse source.ColorSpace IsNot Nothing Then
                Throw New ArgumentException("Expected untagged, premultiplied linear Rec.2020 RGBA F32.", NameOf(source))
            End If
            Return Create(source.Width, source.Height, domain,
                          Sub(y, row) Marshal.Copy(source.GetPixels() + y * source.RowBytes, row, 0, row.Length))
        End Function

        ''' <summary>Zeilenweiser Aufbau vermeidet einen zusaetzlichen F32-Vollbildpuffer an
        ''' Eingangsgrenzen. fillRow bekommt fremden Zwischenspeicher, niemals die internen Bits.</summary>
        Friend Shared Function Create(width As Integer, height As Integer, domain As WorkingImageDomain,
                                      fillRow As Action(Of Integer, Single())) As LinearWorkingFrame
            ValidateDimensions(width, height)
            ArgumentNullException.ThrowIfNull(fillRow)
            If Not [Enum].IsDefined(domain) Then Throw New ArgumentOutOfRangeException(NameOf(domain))
            Dim bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul))
            Try
                If bitmap.GetPixels() = IntPtr.Zero Then Throw New OutOfMemoryException("Cannot allocate F16 frame.")
                Dim row(width * 4 - 1) As Single
                For y = 0 To height - 1
                    Array.Clear(row)
                    fillRow(y, row)
                    For x = 0 To width - 1
                        Dim at = x * 4
                        Dim alpha = row(at + 3)
                        If Not Single.IsFinite(alpha) OrElse alpha < 0 OrElse alpha > 1 Then Throw New ArgumentException("Alpha must be finite and in [0,1].")
                        ' Farbe darf bei HDR groesser als Alpha oder negativ sein. Nur ein auf
                        ' null gerundetes Alpha muss kanonisch durchsichtiges Schwarz ergeben.
                        If CType(alpha, Half) = CType(0.0F, Half) Then
                            For c = 0 To 2
                                If Not Single.IsFinite(row(at + c)) Then Throw New ArgumentException("Color must be finite.")
                                row(at + c) = 0
                            Next
                        End If
                    Next
                    Dim bits = HalfPixelStorage.Pack(row)
                    Marshal.Copy(bits, 0, bitmap.GetPixels() + y * bitmap.RowBytes, bits.Length)
                Next
                Return New LinearWorkingFrame(bitmap, domain)
            Catch
                bitmap.Dispose()
                Throw
            End Try
        End Function

        Private Shared Sub ValidateDimensions(width As Integer, height As Integer)
            If width <= 0 OrElse height <= 0 OrElse CLng(width) * height > Integer.MaxValue \ 16 Then
                Throw New ArgumentOutOfRangeException(NameOf(width), "Frame dimensions exceed the supported staging size.")
            End If
        End Sub

        Friend Sub ReadLinearRow(y As Integer, row As Single())
            ObjectDisposedException.ThrowIf(_disposed, Me)
            ArgumentNullException.ThrowIfNull(row)
            If y < 0 OrElse y >= Height Then Throw New ArgumentOutOfRangeException(NameOf(y))
            If row.Length <> Width * 4 Then Throw New ArgumentException("Expected one RGBA row.", NameOf(row))
            Dim bits(row.Length - 1) As Short
            Marshal.Copy(_bitmap.GetPixels() + y * _bitmap.RowBytes, bits, 0, bits.Length)
            Dim values = HalfPixelStorage.Unpack(bits)
            Array.Copy(values, row, row.Length)
        End Sub

        ''' <summary>Allocation-free random access for geometric sampling. Read the
        ''' half bits explicitly, just as the row path does: Skia's CPU F16 raster
        ''' conversion would erase small shadow values. Values stay premultiplied.</summary>
        Friend Sub ReadLinearPixel(x As Integer, y As Integer, ByRef r As Double, ByRef g As Double,
                                   ByRef b As Double, ByRef a As Double)
            ObjectDisposedException.ThrowIf(_disposed, Me)
            If x < 0 OrElse x >= Width OrElse y < 0 OrElse y >= Height Then Throw New ArgumentOutOfRangeException(NameOf(x))
            Dim address = _bitmap.GetPixels() + y * _bitmap.RowBytes + x * 8
            r = CSng(BitConverter.Int16BitsToHalf(Marshal.ReadInt16(address)))
            g = CSng(BitConverter.Int16BitsToHalf(Marshal.ReadInt16(address, 2)))
            b = CSng(BitConverter.Int16BitsToHalf(Marshal.ReadInt16(address, 4)))
            a = CSng(BitConverter.Int16BitsToHalf(Marshal.ReadInt16(address, 6)))
        End Sub

        ''' <summary>F32-Staging fuer den Prozessor. Nicht ueber Skias F16-Konverter lesen:
        ''' dessen Subnormalverlust war gerade der Grund fuer den expliziten Speichervertrag.</summary>
        Friend Function ToLinearBitmap() As SKBitmap
            ObjectDisposedException.ThrowIf(_disposed, Me)
            Dim result = New SKBitmap(New SKImageInfo(Width, Height, SKColorType.RgbaF32, SKAlphaType.Premul))
            Try
                If result.GetPixels() = IntPtr.Zero Then Throw New OutOfMemoryException("Cannot allocate F32 staging image.")
                Dim row(Width * 4 - 1) As Single
                For y = 0 To Height - 1
                    ReadLinearRow(y, row)
                    Marshal.Copy(row, 0, result.GetPixels() + y * result.RowBytes, row.Length)
                Next
                Return result
            Catch
                result.Dispose()
                Throw
            End Try
        End Function

        ''' <summary>Native F16-Quelle fuer den GPU-Weg. Der Aufrufer besitzt den Snapshot und
        ''' muss ihn im passenden Kontext freigeben; keine verlustbehaftete F32-GPU-Zwischenquelle.</summary>
        Friend Function CreateNativeSnapshot() As SKImage
            ObjectDisposedException.ThrowIf(_disposed, Me)
            Return SKImage.FromBitmap(_bitmap)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            _bitmap.Dispose()
        End Sub
    End Class
End Namespace
