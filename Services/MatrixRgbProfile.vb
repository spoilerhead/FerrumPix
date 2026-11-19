Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports SkiaSharp

Namespace Services
    ''' <summary>Explizite Matrix/TRC-Eingangsgrenze fuer entwickelte RGB-Dateien.
    ''' JPEG und TIFF teilen die Farbrechnung, nicht ihre Decoder oder Quellpraezision.
    ''' Es gibt keinen stillen sRGB-Ersatz und keine A2B/B2A-LUT-Interpretation.</summary>
    Friend NotInheritable Class MatrixRgbProfile
        Private ReadOnly _linear As Double(,)
        Private ReadOnly _conversion As Double()

        Friend Sub New(icc As Byte(), bits As Integer, Optional allowTables As Boolean = False)
            ArgumentNullException.ThrowIfNull(icc)
            If bits <> 8 AndAlso bits <> 16 Then Throw New ArgumentOutOfRangeException(NameOf(bits))
            Dim maximumCode = If(bits = 16, 65535, 255)
            ValidateProfile(icc)
            Dim tags = ProfileTags(icc)
            _linear = New Double(2, maximumCode) {}
            Dim sourceMatrix(8) As Double
            For c = 0 To 2
                Dim prefix = {"r", "g", "b"}(c)
                Dim colorant = tags(prefix & "XYZ")
                If colorant.Size < 20 OrElse Signature(icc, colorant.Offset) <> "XYZ " Then Throw New InvalidDataException("Invalid ICC colorant.")
                For r = 0 To 2
                    sourceMatrix(r * 3 + c) = Fixed(icc, colorant.Offset + 8 + r * 4)
                Next
                ' Direkt aus den ICC-Bytes: native CMMs koennen aehnliche Kurven
                ' vereinheitlichen oder Potenzen approximieren. Eine quellcodegenaue
                ' Tabelle vermeidet eine zusaetzliche Rasterung der Quellpraezision.
                Dim curve = tags(prefix & "TRC")
                For i = 0 To maximumCode
                    _linear(c, i) = DecodeCurve(icc, curve.Offset, curve.Size, i / CDbl(maximumCode), allowTables)
                    If Not Double.IsFinite(_linear(c, i)) Then Throw New NotSupportedException("Invalid RGB transfer function.")
                    If i > 0 AndAlso _linear(c, i) < _linear(c, i - 1) Then Throw New InvalidDataException("Non-monotonic ICC transfer curve.")
                Next
            Next

            ' ICC-Farbmatrizen beziehen sich bereits auf PCS-D50. Auch Skias Rec.2020-
            ' Matrix enthaelt die D65->PCS-Adaption: keine zweite Bradford-Adaption!
            Dim inverse = SKColorSpaceXyz.Rec2020.Invert().Values
            _conversion = New Double(8) {}
            For r = 0 To 2
                For c = 0 To 2
                    For k = 0 To 2
                        _conversion(r * 3 + c) += CDbl(inverse(r * 3 + k)) * sourceMatrix(k * 3 + c)
                    Next
                Next
            Next

        End Sub

        ''' <summary>Deckende Quellcodes nach linearem Rec.2020, ohne Gamut-Clipping.
        ''' Der Frame uebernimmt danach das explizite Runden auf Half.</summary>
        Friend Sub WritePixel(red As Integer, green As Integer, blue As Integer, row As Single(), offset As Integer)
            Dim r = _linear(0, red), g = _linear(1, green), b = _linear(2, blue)
            For c = 0 To 2
                row(offset + c) = CSng(_conversion(c * 3) * r + _conversion(c * 3 + 1) * g + _conversion(c * 3 + 2) * b)
            Next
            row(offset + 3) = 1.0F
        End Sub

        Private Shared Function U32(data As Byte(), offset As Integer) As Long
            If offset < 0 OrElse offset > data.Length - 4 Then Throw New InvalidDataException("Truncated ICC value.")
            Return (CLng(data(offset)) << 24) Or (CLng(data(offset + 1)) << 16) Or (CLng(data(offset + 2)) << 8) Or data(offset + 3)
        End Function

        Private Shared Function Signature(data As Byte(), offset As Integer) As String
            If offset < 0 OrElse offset > data.Length - 4 Then Throw New InvalidDataException("Truncated signature.")
            Return Encoding.ASCII.GetString(data, offset, 4)
        End Function

        Friend Shared Sub ValidateProfile(profile As Byte())
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

        ''' <summary>Analytische ICC-Kurven oder explizit freigegebene 16-Bit-TRC-Tabellen.
        ''' So bleiben die Grenzen dieses ersten Dateieingangs klar und pruefbar.</summary>
        Private Shared Function DecodeCurve(data As Byte(), offset As Integer, size As Integer, x As Double, allowTables As Boolean) As Double
            Dim kind = Signature(data, offset)
            If size < 12 Then Throw New InvalidDataException("Truncated ICC curve.")
            If kind = "curv" Then
                Dim count = U32(data, offset + 8)
                If count = 0 Then Return x
                If count > 1 Then
                    If Not allowTables Then Throw New NotSupportedException("Tabulated ICC curves are not supported for this input.")
                    If count > 65536 OrElse 12 + count * 2 > size Then Throw New InvalidDataException("Invalid ICC curve table.")
                    Dim position = x * (count - 1)
                    Dim index = Math.Min(CInt(count) - 2, CInt(Math.Floor(position)))
                    Dim at = offset + 12 + index * 2
                    Dim low = (CInt(data(at)) * 256 + data(at + 1)) / 65535.0
                    Dim high = (CInt(data(at + 2)) * 256 + data(at + 3)) / 65535.0
                    Return low + (high - low) * (position - index)
                End If
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

        Friend Shared Function DescriptionOf(profile As Byte()) As String
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
