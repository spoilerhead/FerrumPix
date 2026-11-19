Imports System

Namespace Services
    ''' <summary>Kompatibler Ein-Regler-Einstieg des akzeptierten Kontrast-Checkpoints.
    ''' Die Grund-Tonstufe besitzt die Rechnung jetzt zentral, damit Helligkeit und
    ''' entwickelte Belichtung davor wirken, ohne getrennte begrenzte Adapter zu stapeln.</summary>
    Friend NotInheritable Class RenderedContrast
        Private Sub New()
        End Sub

        Friend Shared Function Apply(source As LinearWorkingFrame, slider As Single) As LinearWorkingFrame
            Return RenderedBasicTone.Apply(source, brightness:=0, contrast:=slider)
        End Function
    End Class
End Namespace
