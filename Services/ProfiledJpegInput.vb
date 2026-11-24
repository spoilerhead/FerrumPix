Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Security.Cryptography
Imports System.Text
Imports SkiaSharp

Namespace Services
    ''' <summary>Diagnostischer Eingang fuer profilierte RGB-JPEGs. Die JPEG-Quelle ist bereits
    ''' entwickelt und wird RenderedLinear, niemals SceneLinear. Keine Entwicklung ueber sRGB8.
    ''' Vorerst nur eingebettete RGB-Matrixprofile mit analytischen Transferfunktionen; fehlende,
    ''' unlesbare oder komplexere Profile werden nicht still durch sRGB ersetzt.</summary>
    Friend NotInheritable Class ProfiledJpegInput
        Private Sub New()
        End Sub

        Friend NotInheritable Class DecodedImage
            Implements IDisposable
            Friend Sub New(frame As LinearWorkingFrame, description As String, hash As String, origin As SKEncodedOrigin)
                Me.Frame = frame
                Me.ProfileDescription = description
                Me.ProfileSha256 = hash
                Me.OriginalOrientation = origin
            End Sub
            Friend ReadOnly Property Frame As LinearWorkingFrame
            Friend ReadOnly Property ProfileDescription As String
            Friend ReadOnly Property ProfileSha256 As String
            Friend ReadOnly Property OriginalOrientation As SKEncodedOrigin
            Public Sub Dispose() Implements IDisposable.Dispose
                Frame.Dispose()
            End Sub
        End Class

        Friend Shared Function Decode(path As String) As DecodedImage
            Dim bytes = File.ReadAllBytes(path)
            Dim icc = ReadEmbeddedProfile(bytes)
            Using profile = SKColorSpace.CreateIcc(icc), data = SKData.CreateCopy(bytes), codec = SKCodec.Create(data)
                If profile Is Nothing OrElse codec Is Nothing Then Throw New NotSupportedException("Cannot decode the embedded JPEG/profile.")
                Dim xyz As SKColorSpaceXyz = Nothing
                If Not profile.ToColorSpaceXyz(xyz) Then
                    Throw New NotSupportedException("A matrix RGB profile is required.")
                End If
                Dim info = codec.Info
                If info.Width <= 0 OrElse info.Height <= 0 OrElse CLng(info.Width) * info.Height > Integer.MaxValue \ 16 Then
                    Throw New ArgumentOutOfRangeException(NameOf(path), "JPEG dimensions exceed supported staging size.")
                End If
                ' Farbraumlos dekodieren: die RGB8-Zahlen gehoeren dem QUELLPROFIL. Dies ist die
                ' native JPEG-Praezision, kein quantisierter Zwischenweg in sRGB.
                Using original = New SKBitmap(New SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Opaque))
                    If original.GetPixels() = IntPtr.Zero Then Throw New OutOfMemoryException("Cannot allocate JPEG source.")
                    If codec.GetPixels(original.Info, original.GetPixels()) <> SKCodecResult.Success Then Throw New InvalidDataException("Incomplete or unsupported JPEG decode.")
                    Using oriented = OrientEncodedPixels(original, codec.EncodedOrigin)
                        Return New DecodedImage(FromProfiledPixels(oriented, icc), MatrixRgbProfile.DescriptionOf(icc),
                                                Convert.ToHexString(SHA256.HashData(icc)), codec.EncodedOrigin)
                    End Using
                End Using
            End Using
        End Function

        Friend Shared Function FromProfiledPixels(source As SKBitmap, icc As Byte()) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            ArgumentNullException.ThrowIfNull(icc)
            If source.ColorType <> SKColorType.Bgra8888 OrElse source.AlphaType <> SKAlphaType.Opaque Then
                Throw New ArgumentException("Expected opaque source-profile JPEG RGB8 pixels.", NameOf(source))
            End If
            If source.Width <= 0 OrElse source.Height <= 0 OrElse CLng(source.Width) * source.Height > Integer.MaxValue \ 16 Then
                Throw New ArgumentOutOfRangeException(NameOf(source), "Source dimensions exceed supported staging size.")
            End If
            Dim transform As New MatrixRgbProfile(icc, 8)
            Dim bytes(source.Width * 4 - 1) As Byte
            Return LinearWorkingFrame.Create(source.Width, source.Height, WorkingImageDomain.RenderedLinear,
                Sub(y, row)
                    Marshal.Copy(source.GetPixels() + y * source.RowBytes, bytes, 0, bytes.Length)
                    For x = 0 To source.Width - 1
                        Dim at = x * 4
                        transform.WritePixel(bytes(at + 2), bytes(at + 1), bytes(at), row, at)
                    Next
                End Sub)
        End Function

        ''' <summary>Orientierung als reine Pixelpermutation vor der Farbraumwandlung, ohne Canvas/F16-Raster,
        ''' Interpolation oder Gammawandlung. Auch TopLeft liefert eine eigene Bitmap.</summary>
        Friend Shared Function OrientEncodedPixels(source As SKBitmap, origin As SKEncodedOrigin) As SKBitmap
            ArgumentNullException.ThrowIfNull(source)
            If source.ColorType <> SKColorType.Bgra8888 OrElse source.AlphaType <> SKAlphaType.Opaque Then Throw New ArgumentException("Expected opaque encoded RGB8.", NameOf(source))
            Dim code = CInt(origin)
            If code < 1 OrElse code > 8 Then Throw New ArgumentOutOfRangeException(NameOf(origin))
            Dim swap = code >= 5
            Dim width = If(swap, source.Height, source.Width), height = If(swap, source.Width, source.Height)
            Dim result = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, source.AlphaType, source.ColorSpace))
            Try
                If result.GetPixels() = IntPtr.Zero Then Throw New OutOfMemoryException("Cannot allocate oriented JPEG source.")
                Dim input(source.ByteCount - 1) As Byte
                Marshal.Copy(source.GetPixels(), input, 0, input.Length)
                Dim row(width * 4 - 1) As Byte
                For y = 0 To height - 1
                    For x = 0 To width - 1
                        Dim sx As Integer, sy As Integer
                        Select Case code
                            Case 1 : sx = x : sy = y
                            Case 2 : sx = source.Width - 1 - x : sy = y
                            Case 3 : sx = source.Width - 1 - x : sy = source.Height - 1 - y
                            Case 4 : sx = x : sy = source.Height - 1 - y
                            Case 5 : sx = y : sy = x
                            Case 6 : sx = y : sy = source.Height - 1 - x
                            Case 7 : sx = source.Width - 1 - y : sy = source.Height - 1 - x
                            Case 8 : sx = source.Width - 1 - y : sy = x
                        End Select
                        Array.Copy(input, sy * source.RowBytes + sx * 4, row, x * 4, 4)
                    Next
                    Marshal.Copy(row, 0, result.GetPixels() + y * result.RowBytes, row.Length)
                Next
                Return result
            Catch
                result.Dispose()
                Throw
            End Try
        End Function

        Private Shared Function U32(data As Byte(), offset As Integer) As Long
            If offset < 0 OrElse offset > data.Length - 4 Then Throw New InvalidDataException("Truncated ICC value.")
            Return (CLng(data(offset)) << 24) Or (CLng(data(offset + 1)) << 16) Or (CLng(data(offset + 2)) << 8) Or data(offset + 3)
        End Function

        Private Shared Function Signature(data As Byte(), offset As Integer) As String
            If offset < 0 OrElse offset > data.Length - 4 Then Throw New InvalidDataException("Truncated signature.")
            Return Encoding.ASCII.GetString(data, offset, 4)
        End Function

        Friend Shared Function ReadEmbeddedProfile(jpeg As Byte()) As Byte()
            ArgumentNullException.ThrowIfNull(jpeg)
            If jpeg.Length < 4 OrElse jpeg(0) <> &HFF OrElse jpeg(1) <> &HD8 Then Throw New InvalidDataException("Expected JPEG.")
            Dim chunks As New SortedDictionary(Of Integer, Byte())
            Dim total = 0, size = 0, position = 2, components = 0, precision = 0
            While position < jpeg.Length
                If jpeg(position) <> &HFF Then Throw New InvalidDataException("Invalid JPEG marker.")
                While position < jpeg.Length AndAlso jpeg(position) = &HFF
                    position += 1
                End While
                If position >= jpeg.Length Then Exit While
                Dim marker = CInt(jpeg(position)) : position += 1
                If marker = &HDA OrElse marker = &HD9 Then Exit While
                If marker = 1 OrElse (marker >= &HD0 AndAlso marker <= &HD7) Then Continue While
                If position > jpeg.Length - 2 Then Throw New InvalidDataException("Truncated JPEG segment.")
                Dim length = CInt(jpeg(position)) * 256 + jpeg(position + 1)
                If length < 2 OrElse length > jpeg.Length - position Then Throw New InvalidDataException("Truncated JPEG segment.")
                Dim start = position + 2, count = length - 2
                If marker >= &HC0 AndAlso marker <= &HCF AndAlso marker <> &HC4 AndAlso marker <> &HC8 AndAlso marker <> &HCC Then
                    If count < 6 Then Throw New InvalidDataException("Truncated JPEG frame header.")
                    precision = jpeg(start) : components = jpeg(start + 5)
                    If count < 6 + 3 * components Then Throw New InvalidDataException("Truncated JPEG component header.")
                End If
                If marker = &HE2 AndAlso count >= 12 AndAlso Encoding.ASCII.GetString(jpeg, start, 12) = "ICC_PROFILE" & ChrW(0) Then
                    If count < 14 Then Throw New InvalidDataException("Truncated ICC chunk.")
                    Dim index = CInt(jpeg(start + 12)), number = CInt(jpeg(start + 13))
                    If index < 1 OrElse number < 1 OrElse index > number OrElse (total <> 0 AndAlso number <> total) OrElse chunks.ContainsKey(index) Then
                        Throw New InvalidDataException("Invalid or duplicate ICC chunk sequence.")
                    End If
                    total = number : size += count - 14
                    If size > 4 * 1024 * 1024 Then Throw New NotSupportedException("ICC profile exceeds diagnostic size limit.")
                    Dim chunk(count - 15) As Byte
                    Array.Copy(jpeg, start + 14, chunk, 0, chunk.Length)
                    chunks.Add(index, chunk)
                End If
                position += length
            End While
            If precision <> 8 OrElse components <> 3 Then Throw New NotSupportedException("Only 8-bit three-component RGB JPEG sources are supported here.")
            If total = 0 OrElse chunks.Count <> total Then Throw New InvalidDataException("Missing or incomplete embedded ICC profile; no implicit sRGB fallback.")
            Dim result(size - 1) As Byte, offset = 0
            For i = 1 To total
                Array.Copy(chunks(i), 0, result, offset, chunks(i).Length)
                offset += chunks(i).Length
            Next
            MatrixRgbProfile.ValidateProfile(result)
            Return result
        End Function

    End Class
End Namespace
