Imports System.Buffers.Binary
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Text
Imports SkiaSharp

Namespace Services
    ''' <summary>Reads straight 16-bit RGB(A) codes from non-interlaced SDR sRGB
    ''' PNGs. Skia's integer RGBA16 decode is unavailable on the current backend.
    ''' Keep the codes intact until the working-space boundary decodes the transfer.</summary>
    Friend NotInheritable Class SrgbPng16Input
        Private Sub New()
        End Sub

        Friend Shared Function Decode(path As String, width As Integer, height As Integer) As LinearWorkingFrame
            If width <= 0 OrElse height <= 0 OrElse CLng(width) * height > Integer.MaxValue \ 16 Then
                Throw New InvalidDataException("Unsupported PNG dimensions.")
            End If
            Using input = File.OpenRead(path), compressed As New MemoryStream()
                Dim signature(7) As Byte
                input.ReadExactly(signature, 0, signature.Length)
                If Not signature.SequenceEqual(New Byte() {137, 80, 78, 71, 13, 10, 26, 10}) Then Throw New InvalidDataException("Invalid PNG signature.")
                Dim channels = ReadChunks(input, compressed, width, height)
                compressed.Position = 0
                Using pixels As New SKBitmap(New SKImageInfo(width, height, SKColorType.Rgba16161616, SKAlphaType.Unpremul)),
                      zlib As New ZLibStream(compressed, CompressionMode.Decompress)
                    If pixels.GetPixels() = IntPtr.Zero Then Throw New OutOfMemoryException("Cannot allocate PNG16 pixels.")
                    Dim bytesPerPixel = channels * 2, rowLength = width * bytesPerPixel
                    Dim row(rowLength - 1), previous(rowLength - 1) As Byte
                    Dim words(width * 4 - 1) As UShort, rgba(width * 8 - 1) As Byte
                    For y = 0 To height - 1
                        Dim filter = zlib.ReadByte()
                        If filter < 0 OrElse filter > 4 Then Throw New InvalidDataException("Invalid PNG scanline filter.")
                        zlib.ReadExactly(row, 0, row.Length)
                        Unfilter(row, previous, bytesPerPixel, filter)
                        For x = 0 To width - 1
                            Dim at = x * bytesPerPixel
                            For c = 0 To channels - 1
                                words(x * 4 + c) = CUShort((CInt(row(at + c * 2)) << 8) Or row(at + c * 2 + 1))
                            Next
                            If channels = 3 Then words(x * 4 + 3) = UShort.MaxValue
                        Next
                        Buffer.BlockCopy(words, 0, rgba, 0, rgba.Length)
                        Marshal.Copy(rgba, 0, pixels.GetPixels() + y * pixels.RowBytes, rgba.Length)
                        Dim swap = previous
                        previous = row
                        row = swap
                    Next
                    If zlib.ReadByte() <> -1 Then Throw New InvalidDataException("Extra PNG scanline data.")
                    Return WorkingImageBoundary.FromEncodedSrgb(pixels)
                End Using
            End Using
        End Function

        Private Shared Function ReadChunks(input As FileStream, compressed As MemoryStream, width As Integer, height As Integer) As Integer
            Dim lengthBytes(3), kindBytes(3), block(65535) As Byte
            Dim channels = 0, seenData = False, dataEnded = False
            Do
                input.ReadExactly(lengthBytes, 0, 4)
                Dim length = CLng(BinaryPrimitives.ReadUInt32BigEndian(lengthBytes))
                input.ReadExactly(kindBytes, 0, 4)
                Dim kind = Encoding.ASCII.GetString(kindBytes)
                If length > input.Length - input.Position - 4 Then Throw New InvalidDataException("Truncated PNG chunk.")
                If channels = 0 AndAlso kind <> "IHDR" Then Throw New InvalidDataException("Missing PNG header.")
                If seenData AndAlso kind <> "IDAT" Then dataEnded = True
                If kind = "IDAT" AndAlso dataEnded Then Throw New InvalidDataException("PNG data chunks must be consecutive.")
                Select Case kind
                    Case "IHDR"
                        If channels <> 0 OrElse length <> 13 Then Throw New InvalidDataException("Invalid PNG header.")
                    Case "acTL", "fdAT", "fcTL", "tRNS"
                        Throw New NotSupportedException("Animated PNG or transparent color keys need a separate PNG16 adapter.")
                    Case "IDAT", "IEND", "PLTE"
                    Case Else
                        If (kindBytes(0) And &H20) = 0 Then Throw New NotSupportedException("Unknown critical PNG chunk.")
                End Select
                Dim crc = PngEncoder.UpdateCrc(&HFFFFFFFFUI, kindBytes, 4)
                Dim remaining = length
                While remaining > 0
                    Dim count = CInt(Math.Min(remaining, block.Length))
                    input.ReadExactly(block, 0, count)
                    crc = PngEncoder.UpdateCrc(crc, block, count)
                    If kind = "IDAT" Then compressed.Write(block, 0, count)
                    remaining -= count
                End While
                input.ReadExactly(lengthBytes, 0, 4)
                If (crc Xor &HFFFFFFFFUI) <> BinaryPrimitives.ReadUInt32BigEndian(lengthBytes) Then
                    Throw New InvalidDataException("PNG chunk checksum mismatch.")
                End If
                If kind = "IHDR" Then
                    If BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0, 4)) <> width OrElse
                       BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4, 4)) <> height OrElse
                       block(8) <> 16 OrElse (block(9) <> 2 AndAlso block(9) <> 6) OrElse
                       block(10) <> 0 OrElse block(11) <> 0 OrElse block(12) <> 0 Then
                        Throw New NotSupportedException("Expected a non-interlaced 16-bit RGB or RGBA PNG.")
                    End If
                    channels = If(block(9) = 6, 4, 3)
                ElseIf kind = "IDAT" Then
                    seenData = True
                ElseIf kind = "IEND" Then
                    If length <> 0 OrElse Not seenData Then Throw New InvalidDataException("Incomplete PNG image.")
                    Return channels
                End If
            Loop
        End Function

        Private Shared Sub Unfilter(row As Byte(), previous As Byte(), stride As Integer, filter As Integer)
            If filter = 0 Then Return
            For i = 0 To row.Length - 1
                Dim left = If(i >= stride, CInt(row(i - stride)), 0)
                Dim up = CInt(previous(i)), upperLeft = If(i >= stride, CInt(previous(i - stride)), 0)
                Dim predictor As Integer
                Select Case filter
                    Case 1 : predictor = left
                    Case 2 : predictor = up
                    Case 3 : predictor = (left + up) \ 2
                    Case Else
                        Dim p = left + up - upperLeft
                        Dim dl = Math.Abs(p - left), du = Math.Abs(p - up), dc = Math.Abs(p - upperLeft)
                        predictor = If(dl <= du AndAlso dl <= dc, left, If(du <= dc, up, upperLeft))
                End Select
                row(i) = CByte((CInt(row(i)) + predictor) And 255)
            Next
        End Sub
    End Class
End Namespace
