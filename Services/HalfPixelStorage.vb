Imports System

Namespace Services
    ''' <summary>Explizite IEEE-Half-Wandlung fuer Bildspeicher. Skias Rasterweg ueber F16
    ''' kann Subnormalwerte zu null machen; deshalb werden die Bits selbst gewandelt und
    ''' die Rechnungen auf dem Prozessor in F32 ausgefuehrt.</summary>
    Friend NotInheritable Class HalfPixelStorage
        Private Sub New()
        End Sub

        Friend Shared Function Pack(values As Single()) As Short()
            ArgumentNullException.ThrowIfNull(values)
            Dim result(values.Length - 1) As Short
            For i = 0 To values.Length - 1
                Dim halfValue = CType(values(i), Half)
                ' Ueberlauf nicht als scheinbar gueltige, geklemmte Farbe weiterreichen.
                ' Unterlauf unter die darstellbare Half-Aufloesung rundet dagegen normal.
                If Not Single.IsFinite(values(i)) OrElse Not Half.IsFinite(halfValue) Then
                    Throw New ArgumentOutOfRangeException(NameOf(values), $"Sample {i} cannot be stored as finite F16.")
                End If
                result(i) = BitConverter.HalfToInt16Bits(halfValue)
            Next
            Return result
        End Function

        Friend Shared Function Unpack(bits As Short()) As Single()
            ArgumentNullException.ThrowIfNull(bits)
            Dim result(bits.Length - 1) As Single
            For i = 0 To bits.Length - 1
                Dim halfValue = BitConverter.Int16BitsToHalf(bits(i))
                If Not Half.IsFinite(halfValue) Then
                    Throw New ArgumentOutOfRangeException(NameOf(bits), $"Sample {i} is not finite F16.")
                End If
                result(i) = CSng(halfValue)
            Next
            Return result
        End Function
    End Class
End Namespace
