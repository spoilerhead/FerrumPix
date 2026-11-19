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
                        Return New DecodedImage(FromProfiledPixels(oriented, icc), DescriptionOf(icc),
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
            ValidateProfile(icc)
            Dim tags = ProfileTags(icc)
            Dim linear(2, 255) As Double
            Dim sourceMatrix(8) As Double
            For c = 0 To 2
                Dim prefix = {"r", "g", "b"}(c)
                Dim colorant = tags(prefix & "XYZ")
                If colorant.Size < 20 OrElse Signature(icc, colorant.Offset) <> "XYZ " Then Throw New InvalidDataException("Invalid ICC colorant.")
                For r = 0 To 2
                    sourceMatrix(r * 3 + c) = Fixed(icc, colorant.Offset + 8 + r * 4)
                Next
                ' Direkt aus den ICC-Bytes: native CMMs koennen aehnliche Kurven
                ' vereinheitlichen oder Potenzen approximieren. Eine exakte 256er
                ' Tabelle ist hier verlustfrei fuer die RGB8-Quelle.
                Dim curve = tags(prefix & "TRC")
                For i = 0 To 255
                    linear(c, i) = DecodeCurve(icc, curve.Offset, curve.Size, i / 255.0)
                    If Not Double.IsFinite(linear(c, i)) Then Throw New NotSupportedException("Invalid RGB transfer function.")
                Next
            Next

            ' ICC-Farbmatrizen beziehen sich bereits auf PCS-D50. Auch Skias Rec.2020-
            ' Matrix enthaelt die D65->PCS-Adaption: keine zweite Bradford-Adaption!
            Dim inverse = SKColorSpaceXyz.Rec2020.Invert().Values
            Dim conversion(8) As Double
            For r = 0 To 2
                For c = 0 To 2
                    For k = 0 To 2
                        conversion(r * 3 + c) += CDbl(inverse(r * 3 + k)) * sourceMatrix(k * 3 + c)
                    Next
                Next
            Next

            Dim bytes(source.Width * 4 - 1) As Byte
            Return LinearWorkingFrame.Create(source.Width, source.Height, WorkingImageDomain.RenderedLinear,
                Sub(y, row)
                    Marshal.Copy(source.GetPixels() + y * source.RowBytes, bytes, 0, bytes.Length)
                    For x = 0 To source.Width - 1
                        Dim at = x * 4
                        Dim r = linear(0, bytes(at + 2)), g = linear(1, bytes(at + 1)), b = linear(2, bytes(at))
                        ' Signed und >1 erhalten. Erst die benannte SDR-Ausgangsgrenze darf
                        ' klemmen; F16 wird durch unseren expliziten Half-Speicher geschrieben.
                        For c = 0 To 2
                            row(at + c) = CSng(conversion(c * 3) * r + conversion(c * 3 + 1) * g + conversion(c * 3 + 2) * b)
                        Next
                        row(at + 3) = 1.0F
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
            Dim result = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque))
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
            ValidateProfile(result)
            Return result
        End Function

        Private Shared Sub ValidateProfile(profile As Byte())
            If profile.Length < 132 OrElse U32(profile, 0) <> profile.Length OrElse Signature(profile, 36) <> "acsp" Then Throw New InvalidDataException("Invalid ICC header/size.")
            If Signature(profile, 16) <> "RGB " OrElse Signature(profile, 20) <> "XYZ " Then Throw New NotSupportedException("Only RGB/XYZ matrix ICC profiles are supported in this slice.")
            Dim tags = ProfileTags(profile)
            For Each forbidden In {"A2B0", "A2B1", "A2B2", "B2A0", "B2A1", "B2A2"}
                If tags.ContainsKey(forbidden) Then Throw New NotSupportedException("LUT ICC profiles require a separate validation path.")
            Next
            For Each required In {"rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC"}
                If Not tags.ContainsKey(required) Then Throw New NotSupportedException("Incomplete matrix/TRC ICC profile.")
            Next
        End Sub

        Private Shared Function Fixed(data As Byte(), offset As Integer) As Double
            Dim bits = U32(data, offset)
            Return If(bits >= &H80000000L, bits - &H100000000L, bits) / 65536.0
        End Function

        ''' <summary>Nur analytische ICC-Kurven, keine angenaeherten Tabellen/LUT-Profile.
        ''' So bleiben die Grenzen dieses ersten Dateieingangs klar und pruefbar.</summary>
        Private Shared Function DecodeCurve(data As Byte(), offset As Integer, size As Integer, x As Double) As Double
            Dim kind = Signature(data, offset)
            If size < 12 Then Throw New InvalidDataException("Truncated ICC curve.")
            If kind = "curv" Then
                Dim count = U32(data, offset + 8)
                If count = 0 Then Return x
                If count <> 1 Then Throw New NotSupportedException("Tabulated ICC curves are not supported in this slice.")
                If size < 14 Then Throw New InvalidDataException("Truncated ICC gamma.")
                Dim gamma = (CInt(data(offset + 12)) * 256 + data(offset + 13)) / 256.0
                If gamma <= 0 Then Throw New InvalidDataException("Invalid ICC gamma.")
                Return Math.Pow(x, gamma)
            End If
            If kind <> "para" Then Throw New NotSupportedException("Unsupported ICC curve type.")
            Dim functionType = CInt(data(offset + 8)) * 256 + data(offset + 9)
            If functionType > 4 Then Throw New NotSupportedException("Unsupported ICC parametric curve.")
            Dim countByType = {1, 3, 4, 5, 7}
            Dim countParameters = countByType(functionType)
            If size < 12 + 4 * countParameters Then Throw New InvalidDataException("Truncated ICC curve parameters.")
            Dim p(countParameters - 1) As Double
            For i = 0 To p.Length - 1
                p(i) = Fixed(data, offset + 12 + 4 * i)
            Next
            If p(0) <= 0 OrElse (functionType > 0 AndAlso p(1) <= 0) Then Throw New InvalidDataException("Invalid ICC curve parameters.")
            Select Case functionType
                Case 0 : Return Math.Pow(x, p(0))
                Case 1 : Return If(x >= -p(2) / p(1), Math.Pow(p(1) * x + p(2), p(0)), 0.0)
                Case 2 : Return If(x >= -p(2) / p(1), Math.Pow(p(1) * x + p(2), p(0)) + p(3), p(3))
                Case 3 : Return If(x >= p(4), Math.Pow(p(1) * x + p(2), p(0)), p(3) * x)
                Case 4 : Return If(x >= p(4), Math.Pow(p(1) * x + p(2), p(0)) + p(5), p(3) * x + p(6))
            End Select
            Throw New NotSupportedException("Unsupported ICC curve.")
        End Function

        Friend Shared Function ProfileTags(profile As Byte()) As Dictionary(Of String, (Offset As Integer, Size As Integer))
            Dim count = U32(profile, 128)
            If count > 4096 OrElse 132 + count * 12 > profile.Length Then Throw New InvalidDataException("Invalid ICC tag table.")
            Dim tags As New Dictionary(Of String, (Offset As Integer, Size As Integer))
            For i = 0 To CInt(count) - 1
                Dim at = 132 + i * 12, offset = U32(profile, at + 4), size = U32(profile, at + 8)
                If size < 8 OrElse offset < 132 OrElse offset + size > profile.Length Then Throw New InvalidDataException("ICC tag exceeds profile bounds.")
                Dim name = Signature(profile, at)
                If tags.ContainsKey(name) Then Throw New InvalidDataException("Duplicate ICC tag.")
                tags.Add(name, (CInt(offset), CInt(size)))
            Next
            Return tags
        End Function

        Private Shared Function DescriptionOf(profile As Byte()) As String
            Dim tags = ProfileTags(profile)
            If Not tags.ContainsKey("desc") Then Return "Embedded RGB ICC"
            Dim tag = tags("desc"), kind = Signature(profile, tag.Offset)
            If kind = "desc" AndAlso tag.Size >= 12 Then
                Dim length = U32(profile, tag.Offset + 8)
                If length <= tag.Size - 12 Then Return Encoding.ASCII.GetString(profile, tag.Offset + 12, CInt(length)).TrimEnd(ChrW(0))
            ElseIf kind = "mluc" AndAlso tag.Size >= 28 AndAlso U32(profile, tag.Offset + 8) > 0 AndAlso U32(profile, tag.Offset + 12) >= 12 Then
                Dim length = U32(profile, tag.Offset + 20), offset = U32(profile, tag.Offset + 24)
                If length Mod 2 = 0 AndAlso offset + length <= tag.Size Then Return Encoding.BigEndianUnicode.GetString(profile, tag.Offset + CInt(offset), CInt(length))
            End If
            Return "Embedded RGB ICC"
        End Function
    End Class
End Namespace
