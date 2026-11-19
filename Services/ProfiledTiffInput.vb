Imports System
Imports System.IO
Imports System.Security.Cryptography
Imports BitMiracle.LibTiff.Classic

Namespace Services
    ''' <summary>Isolierter RGB16-TIFF-Eingang: Quellcodes und ICC-Profil bleiben zusammen,
    ''' bis sie direkt nach RenderedLinear Rec.2020 gewandelt werden. Kein ReadRGBAImage:
    ''' dessen 8-Bit-Ausgabe wuerde die gerade zu erhaltenden Tonabstufungen vernichten.</summary>
    Friend NotInheritable Class ProfiledTiffInput
        Private Sub New()
        End Sub

        Friend NotInheritable Class DecodedImage
            Implements IDisposable

            Friend Sub New(frame As LinearWorkingFrame, icc As Byte(), orientation As Integer)
                Me.Frame = frame
                ProfileDescription = MatrixRgbProfile.DescriptionOf(icc)
                ProfileSha256 = Convert.ToHexString(SHA256.HashData(icc))
                OriginalOrientation = orientation
            End Sub

            Friend ReadOnly Property Frame As LinearWorkingFrame
            Friend ReadOnly Property ProfileDescription As String
            Friend ReadOnly Property ProfileSha256 As String
            Friend ReadOnly Property OriginalOrientation As Integer

            Public Sub Dispose() Implements IDisposable.Dispose
                Frame.Dispose()
            End Sub
        End Class

        Private Shared Function Tag(tif As Tiff, name As TiffTag, Optional defaultValue As Integer = -1) As Integer
            Dim value = tif.GetFieldDefaulted(name)
            Return If(value Is Nothing, defaultValue, value(0).ToInt())
        End Function

        Friend Shared Function Decode(path As String) As DecodedImage
            Using tif = Tiff.Open(path, "r")
                If tif Is Nothing Then Throw New InvalidDataException("Cannot open TIFF.")
                ' Diese erste TIFF-Scheibe bleibt bewusst eng. Weitere Layouts bekommen eigene
                ' Referenzen statt eines stillen Rueckfalls auf die bisherige sRGB8-Vorschau.
                If tif.IsTiled() OrElse Tag(tif, TiffTag.PLANARCONFIG) <> CInt(PlanarConfig.CONTIG) OrElse
                   Tag(tif, TiffTag.BITSPERSAMPLE) <> 16 OrElse Tag(tif, TiffTag.SAMPLESPERPIXEL) <> 3 OrElse
                   Tag(tif, TiffTag.PHOTOMETRIC) <> CInt(Photometric.RGB) OrElse
                   Tag(tif, TiffTag.SAMPLEFORMAT) <> CInt(SampleFormat.UINT) Then
                    Throw New NotSupportedException("Expected stripped, contiguous, unsigned RGB16 TIFF without alpha.")
                End If
                Dim extra = tif.GetField(TiffTag.EXTRASAMPLES)
                If extra IsNot Nothing AndAlso extra(0).ToInt() <> 0 Then Throw New NotSupportedException("TIFF extra channels are not supported here.")
                If tif.NumberOfDirectories() <> 1 Then Throw New NotSupportedException("Multi-page TIFF requires an explicit page policy.")
                Dim width = Tag(tif, TiffTag.IMAGEWIDTH), height = Tag(tif, TiffTag.IMAGELENGTH)
                If width <= 0 OrElse height <= 0 OrElse CLng(width) * height > Integer.MaxValue \ 16 Then
                    Throw New ArgumentOutOfRangeException(NameOf(path), "TIFF dimensions exceed supported staging size.")
                End If
                Dim orientation = Tag(tif, TiffTag.ORIENTATION, 1)
                If orientation < 1 OrElse orientation > 8 Then Throw New InvalidDataException("Invalid TIFF orientation.")
                Dim profile = tif.GetField(TiffTag.ICCPROFILE)
                If profile Is Nothing OrElse profile.Length < 2 Then Throw New InvalidDataException("Embedded RGB ICC profile required; no implicit sRGB fallback.")
                Dim length = profile(0).ToInt()
                If length < 132 OrElse length > 4 * 1024 * 1024 Then Throw New InvalidDataException("Unsupported ICC size.")
                Dim icc = profile(1).ToByteArray()
                If icc Is Nothing OrElse icc.Length <> length Then Throw New InvalidDataException("Truncated TIFF ICC profile.")
                Dim transform As New MatrixRgbProfile(icc, 16, allowTables:=True)

                ' LZW/Deflate-Streifen vorwaerts lesen: beliebige umgekehrte Scanline-Zugriffe
                ' sind nicht bei jedem Codec erlaubt. LibTiff tauscht Datei-Endianness nach Host.
                ' Der RGB16-Puffer erlaubt danach alle acht Orientierungen ohne Interpolation.
                Dim samples(width * height * 3 - 1) As UShort
                Dim stride = tif.ScanlineSize()
                If stride <> width * 6 Then Throw New InvalidDataException("Unexpected RGB16 TIFF scanline size.")
                Dim scanline(stride - 1) As Byte
                For y = 0 To height - 1
                    If Not tif.ReadScanline(scanline, y) Then Throw New InvalidDataException("Incomplete TIFF scanline decode.")
                    Buffer.BlockCopy(scanline, 0, samples, y * stride, stride)
                Next

                Dim swap = orientation >= 5
                Dim outputWidth = If(swap, height, width), outputHeight = If(swap, width, height)
                Dim frame = LinearWorkingFrame.Create(outputWidth, outputHeight, WorkingImageDomain.RenderedLinear,
                    Sub(y, row)
                        For x = 0 To outputWidth - 1
                            Dim sx As Integer, sy As Integer
                            Select Case orientation
                                Case 1 : sx = x : sy = y
                                Case 2 : sx = width - 1 - x : sy = y
                                Case 3 : sx = width - 1 - x : sy = height - 1 - y
                                Case 4 : sx = x : sy = height - 1 - y
                                Case 5 : sx = y : sy = x
                                Case 6 : sx = y : sy = height - 1 - x
                                Case 7 : sx = width - 1 - y : sy = height - 1 - x
                                Case 8 : sx = width - 1 - y : sy = x
                            End Select
                            Dim at = (sy * width + sx) * 3
                            transform.WritePixel(samples(at), samples(at + 1), samples(at + 2), row, x * 4)
                        Next
                    End Sub)
                Try
                    Return New DecodedImage(frame, icc, orientation)
                Catch
                    frame.Dispose()
                    Throw
                End Try
            End Using
        End Function
    End Class
End Namespace
