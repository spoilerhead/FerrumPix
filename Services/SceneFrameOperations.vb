Imports System

Namespace Services
    ''' <summary>Erste Operation auf echten Szenenwerten. Keine Umkehr einer schon angewandten
    ''' Tonkurve und keine sRGB-Transferfunktion; das Resultat bleibt ausdruecklich SceneLinear.</summary>
    Friend NotInheritable Class SceneFrameOperations
        Private Sub New()
        End Sub

        Friend Shared Function ApplyExposure(source As LinearWorkingFrame, exposureEv As Double) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.SceneLinear Then Throw New ArgumentException("Scene exposure requires a scene-linear source.", NameOf(source))
            If Not Double.IsFinite(exposureEv) OrElse exposureEv < -10 OrElse exposureEv > 10 Then Throw New ArgumentOutOfRangeException(NameOf(exposureEv))
            Dim gain = CSng(Math.Pow(2.0, exposureEv))
            Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain,
                Sub(y, row)
                    source.ReadLinearRow(y, row)
                    For x = 0 To source.Width - 1
                        Dim at = x * 4
                        row(at) *= gain : row(at + 1) *= gain : row(at + 2) *= gain
                    Next
                End Sub)
        End Function
    End Class
End Namespace
