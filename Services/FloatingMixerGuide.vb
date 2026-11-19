Imports System

Namespace Services
    ''' <summary>Encoded-sRGB Gaussian guide filtered as premultiplied RGBA F32.
    ''' Alpha excludes hidden color; full precision avoids RGB8 selection jumps.
    ''' Only the output is full-frame; horizontal rows use a bounded ring buffer.</summary>
    Friend NotInheritable Class FloatingMixerGuide
        Private ReadOnly _pixels As Single()
        Friend ReadOnly Property Width As Integer
        Friend ReadOnly Property Height As Integer
        Private Sub New(width As Integer, height As Integer)
            Me.Width = width : Me.Height = height
            _pixels = New Single(width * height * 4 - 1) {}
        End Sub
        Friend Shared Function Build(width As Integer, height As Integer, source As Single(), sigma As Single) As FloatingMixerGuide
            ArgumentNullException.ThrowIfNull(source)
            If width <= 0 OrElse height <= 0 OrElse CLng(width) * height > Integer.MaxValue \ 16 OrElse
               CLng(width) * height * 4 <> source.LongLength Then Throw New ArgumentException("Invalid guide dimensions.")
            RenderedLateColorSettings.Check(sigma, NameOf(sigma), 0, 8)
            If sigma = 0 Then Return Nothing
            For Each value In source
                If Not Single.IsFinite(value) OrElse value < 0 OrElse value > 1 Then Throw New ArgumentException("Expected bounded straight RGBA.")
            Next
            Dim radius = CInt(Math.Ceiling(3 * CDbl(sigma)))
            Dim weights(2 * radius) As Double
            Dim total As Double = 0
            For i = -radius To radius
                weights(i + radius) = Math.Exp(-i * CDbl(i) / (2 * sigma * CDbl(sigma)))
                total += weights(i + radius)
            Next
            For i = 0 To weights.Length - 1
                weights(i) /= total
            Next
            Dim rows(weights.Length - 1)() As Single
            Dim tags(weights.Length - 1) As Integer
            Dim window(weights.Length - 1)() As Single
            For i = 0 To rows.Length - 1
                rows(i) = New Single(width * 4 - 1) {}
                tags(i) = -1
            Next
            Dim result As New FloatingMixerGuide(width, height)
            For y = 0 To height - 1
                For i = -radius To radius
                    Dim sourceY = Math.Clamp(y + i, 0, height - 1)
                    Dim slot = sourceY Mod rows.Length
                    If tags(slot) <> sourceY Then
                        Horizontal(source, width, sourceY, rows(slot), weights, radius)
                        tags(slot) = sourceY
                    End If
                    window(i + radius) = rows(slot)
                Next
                For x = 0 To width - 1
                    Dim r = 0.0, g = 0.0, b = 0.0, a = 0.0
                    For i = 0 To weights.Length - 1
                        Dim row = window(i), at = x * 4, weight = weights(i)
                        r += row(at) * weight : g += row(at + 1) * weight
                        b += row(at + 2) * weight : a += row(at + 3) * weight
                    Next
                    Dim dest = (y * width + x) * 4
                    result._pixels(dest) = CSng(r) : result._pixels(dest + 1) = CSng(g)
                    result._pixels(dest + 2) = CSng(b) : result._pixels(dest + 3) = CSng(a)
                Next
            Next
            Return result
        End Function
        Private Shared Sub Horizontal(source As Single(), width As Integer, y As Integer,
                                      target As Single(), weights As Double(), radius As Integer)
            For x = 0 To width - 1
                Dim r = 0.0, g = 0.0, b = 0.0, a = 0.0
                For i = -radius To radius
                    Dim at = (y * width + Math.Clamp(x + i, 0, width - 1)) * 4
                    Dim weight = weights(i + radius), alpha = CDbl(source(at + 3))
                    r += source(at) * alpha * weight : g += source(at + 1) * alpha * weight
                    b += source(at + 2) * alpha * weight : a += alpha * weight
                Next
                target(x * 4) = CSng(r) : target(x * 4 + 1) = CSng(g)
                target(x * 4 + 2) = CSng(b) : target(x * 4 + 3) = CSng(a)
            Next
        End Sub
        Friend Function ReadPremultiplied(x As Integer, y As Integer) As (R As Single, G As Single, B As Single, A As Single)
            If x < 0 OrElse x >= Width OrElse y < 0 OrElse y >= Height Then Throw New ArgumentOutOfRangeException(NameOf(x))
            Dim at = (y * Width + x) * 4
            Return (_pixels(at), _pixels(at + 1), _pixels(at + 2), _pixels(at + 3))
        End Function
        Friend Function ReadStraight(x As Integer, y As Integer, ByRef r As Single, ByRef g As Single, ByRef b As Single) As Boolean
            Dim value = ReadPremultiplied(x, y)
            If value.A <= 0 Then Return False
            r = Math.Clamp(value.R / value.A, 0.0F, 1.0F)
            g = Math.Clamp(value.G / value.A, 0.0F, 1.0F)
            b = Math.Clamp(value.B / value.A, 0.0F, 1.0F)
            Return True
        End Function
    End Class
End Namespace
