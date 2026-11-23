Imports System
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services
    ''' <summary>Benannte Grenzen zwischen dem bisherigen kodierten sRGB und dem neuen linearen
    ''' Rec.2020-Bild. Noch kein Pipeline-Umbau: der Pruefstand ruft diese Adapter ausdruecklich.
    ''' Die Ausgabe ist nur eine SDR-Diagnose mit Clipping, keine HDR-Tonabbildung.</summary>
    Friend NotInheritable Class WorkingImageBoundary
        Private Sub New()
        End Sub

        ''' <summary>Ein bereits entwickeltes sRGB-Bild wird RenderedLinear, niemals SceneLinear.
        ''' Vor der Transferfunktion Alpha entfernen, danach im linearen Raum vormultiplizieren.</summary>
        Friend Shared Function FromEncodedSrgb(source As SKBitmap) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            If source.ColorSpace IsNot Nothing AndAlso Not source.ColorSpace.IsSrgb Then
                Throw New ArgumentException("Convert the source profile to sRGB before this boundary.", NameOf(source))
            End If
            If source.AlphaType <> SKAlphaType.Premul AndAlso source.AlphaType <> SKAlphaType.Unpremul AndAlso source.AlphaType <> SKAlphaType.Opaque Then
                Throw New ArgumentException("Unknown alpha interpretation.", NameOf(source))
            End If
            Dim deep = source.ColorType = SKColorType.Rgba16161616
            If Not deep AndAlso source.ColorType <> SKColorType.Bgra8888 AndAlso source.ColorType <> SKColorType.Rgba8888 Then
                Throw New ArgumentException("Expected encoded sRGB BGRA8, RGBA8 or RGBA16.", NameOf(source))
            End If
            Dim redIndex = If(source.ColorType = SKColorType.Bgra8888, 2, 0)
            Dim blueIndex = If(source.ColorType = SKColorType.Bgra8888, 0, 2)
            Dim bytes(source.Width * 4 - 1) As Byte
            Dim words(source.Width * 4 - 1) As Short
            Return LinearWorkingFrame.Create(source.Width, source.Height, WorkingImageDomain.RenderedLinear,
                Sub(y, row)
                    Dim address = source.GetPixels() + y * source.RowBytes
                    If deep Then
                        Marshal.Copy(address, words, 0, words.Length)
                    Else
                        Marshal.Copy(address, bytes, 0, bytes.Length)
                    End If
                    For x = 0 To source.Width - 1
                        Dim at = x * 4
                        Dim alpha = If(source.AlphaType = SKAlphaType.Opaque, 1.0,
                                       If(deep, (CInt(words(at + 3)) And &HFFFF) / 65535.0, bytes(at + 3) / 255.0))
                        row(at + 3) = CSng(alpha)
                        If alpha <= 0 Then Continue For
                        Dim r = If(deep, (CInt(words(at + redIndex)) And &HFFFF) / 65535.0, bytes(at + redIndex) / 255.0)
                        Dim g = If(deep, (CInt(words(at + 1)) And &HFFFF) / 65535.0, bytes(at + 1) / 255.0)
                        Dim b = If(deep, (CInt(words(at + blueIndex)) And &HFFFF) / 65535.0, bytes(at + blueIndex) / 255.0)
                        If source.AlphaType = SKAlphaType.Premul Then
                            r /= alpha : g /= alpha : b /= alpha
                        End If
                        r = DecodeSrgb(Math.Clamp(r, 0.0, 1.0))
                        g = DecodeSrgb(Math.Clamp(g, 0.0, 1.0))
                        b = DecodeSrgb(Math.Clamp(b, 0.0, 1.0))
                        ' Beide Raeume haben D65. Keine zusaetzliche Weisspunkt-Adaption.
                        row(at) = CSng((0.627403896 * r + 0.329283038 * g + 0.043313066 * b) * alpha)
                        row(at + 1) = CSng((0.069097289 * r + 0.919540395 * g + 0.011362316 * b) * alpha)
                        row(at + 2) = CSng((0.016391439 * r + 0.088013308 * g + 0.895595253 * b) * alpha)
                    Next
                End Sub)
        End Function

        ''' <summary>Explizite SDR-Diagnosegrenze. Erst in sRGB umrechnen und DANN klemmen;
        ''' negative Rec.2020-Kanaele vorher zu klemmen wuerde die Farbe veraendern.</summary>
        Friend Shared Function ToDiagnosticSdrSrgb8(frame As LinearWorkingFrame) As SKBitmap
            Return ToDisplaySdrSrgb8(frame)
        End Function

        ' Shared explicit SDR boundary for diagnostic images and the experimental
        ' editor preview. Native frame storage is never modified by display output.
        Friend Shared Function ToDisplaySdrSrgb8(frame As LinearWorkingFrame) As SKBitmap
            ArgumentNullException.ThrowIfNull(frame)
            If frame.Domain <> WorkingImageDomain.RenderedLinear Then
                Throw New ArgumentException("Scene-linear data requires an explicit rendering step before SDR output.", NameOf(frame))
            End If
            Dim result = New SKBitmap(New SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
            Try
                If result.GetPixels() = IntPtr.Zero Then Throw New OutOfMemoryException("Cannot allocate SDR output.")
                Dim row(frame.Width * 4 - 1) As Single
                Dim bytes(frame.Width * 4 - 1) As Byte
                For y = 0 To frame.Height - 1
                    frame.ReadLinearRow(y, row)
                    For x = 0 To frame.Width - 1
                        Dim at = x * 4
                        Dim alpha = CDbl(row(at + 3))
                        If alpha <= 0 Then
                            bytes(at) = 0 : bytes(at + 1) = 0 : bytes(at + 2) = 0 : bytes(at + 3) = 0
                            Continue For
                        End If
                        Dim sr As Double, sg As Double, sb As Double
                        ProjectSrgb(row, at, alpha, sr, sg, sb)
                        ' Dies ist keine neue Renderkurve. Clipping und Quantisierung sind an
                        ' dieser benannten Ausgangsgrenze erlaubt, im Arbeitsbild selbst nicht.
                        Dim a = ToByte(alpha)
                        bytes(at) = CByte(Math.Min(CInt(a), CInt(ToByte(EncodeSrgb(Math.Clamp(sb, 0.0, 1.0)) * alpha))))
                        bytes(at + 1) = CByte(Math.Min(CInt(a), CInt(ToByte(EncodeSrgb(Math.Clamp(sg, 0.0, 1.0)) * alpha))))
                        bytes(at + 2) = CByte(Math.Min(CInt(a), CInt(ToByte(EncodeSrgb(Math.Clamp(sr, 0.0, 1.0)) * alpha))))
                        bytes(at + 3) = a
                    Next
                    Marshal.Copy(bytes, 0, result.GetPixels() + y * result.RowBytes, bytes.Length)
                Next
                Return result
            Catch
                result.Dispose()
                Throw
            End Try
        End Function

        Friend Shared Function ToExportSdrSrgb16(frame As LinearWorkingFrame) As SKBitmap
            ArgumentNullException.ThrowIfNull(frame)
            If frame.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Rendered input required.")
            ' The encoders consume straight RGBA. Unpremultiply in floating
            ' point before quantizing so translucent edges keep 16-bit color.
            Dim result = New SKBitmap(New SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba16161616, SKAlphaType.Unpremul))
            Try
                If result.GetPixels() = IntPtr.Zero Then Throw New OutOfMemoryException("Cannot allocate RGB16 export.")
                Dim row(frame.Width * 4 - 1) As Single, words(row.Length - 1) As UShort
                Dim bytes(words.Length * 2 - 1) As Byte
                For y = 0 To frame.Height - 1
                    frame.ReadLinearRow(y, row)
                    For at = 0 To row.Length - 1 Step 4
                        Dim alpha = CDbl(row(at + 3))
                        Dim sr = 0.0, sg = 0.0, sb = 0.0
                        If alpha > 0 Then ProjectSrgb(row, at, alpha, sr, sg, sb)
                        words(at) = Quantize16(EncodeSrgb(Math.Clamp(sr, 0.0, 1.0)))
                        words(at + 1) = Quantize16(EncodeSrgb(Math.Clamp(sg, 0.0, 1.0)))
                        words(at + 2) = Quantize16(EncodeSrgb(Math.Clamp(sb, 0.0, 1.0)))
                        words(at + 3) = Quantize16(alpha)
                    Next
                    Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length)
                    Marshal.Copy(bytes, 0, result.GetPixels() + y * result.RowBytes, bytes.Length)
                Next
                Return result
            Catch
                result.Dispose()
                Throw
            End Try
        End Function

        Private Shared Sub ProjectSrgb(row As Single(), at As Integer, alpha As Double,
                                       ByRef sr As Double, ByRef sg As Double, ByRef sb As Double)
            Dim r = row(at) / alpha, g = row(at + 1) / alpha, b = row(at + 2) / alpha
            sr = 1.660491002 * r - 0.587641139 * g - 0.072849863 * b
            sg = -0.124550475 * r + 1.132899897 * g - 0.008349422 * b
            sb = -0.018150763 * r - 0.100578898 * g + 1.118729661 * b
        End Sub

        Private Shared Function Quantize16(value As Double) As UShort
            Return CUShort(Math.Round(value * 65535.0, MidpointRounding.AwayFromZero))
        End Function

        Private Shared Function DecodeSrgb(v As Double) As Double
            Return If(v <= 0.04045, v / 12.92, Math.Pow((v + 0.055) / 1.055, 2.4))
        End Function

        Private Shared Function EncodeSrgb(v As Double) As Double
            Return If(v <= 0.0031308, 12.92 * v, 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055)
        End Function

        Private Shared Function ToByte(v As Double) As Byte
            Return CByte(Math.Clamp(CInt(Math.Round(v * 255.0, MidpointRounding.AwayFromZero)), 0, 255))
        End Function
    End Class
End Namespace
