Imports System
Imports System.Globalization
Imports System.Linq

Namespace Services
    ''' <summary>Immutable curve snapshot for the floating basic group. Coordinates keep
    ''' the editor's 0..255 convention; they are control points, not an RGB8 image.</summary>
    Friend NotInheritable Class RenderedCurveSettings
        Private Const Identity As String = "0,0;255,255"

        Friend Sub New(Optional master As String = Identity, Optional red As String = Identity,
                       Optional green As String = Identity, Optional blue As String = Identity,
                       Optional luminance As String = Identity)
            Me.Master = Normalize(master, NameOf(master))
            Me.Red = Normalize(red, NameOf(red))
            Me.Green = Normalize(green, NameOf(green))
            Me.Blue = Normalize(blue, NameOf(blue))
            Me.Luminance = Normalize(luminance, NameOf(luminance))
        End Sub

        Friend ReadOnly Property Master As String
        Friend ReadOnly Property Red As String
        Friend ReadOnly Property Green As String
        Friend ReadOnly Property Blue As String
        Friend ReadOnly Property Luminance As String

        Friend ReadOnly Property IsIdentity As Boolean
            Get
                Return Master = Identity AndAlso Red = Identity AndAlso Green = Identity AndAlso
                       Blue = Identity AndAlso Luminance = Identity
            End Get
        End Property

        Friend Sub CopyTo(adjustments As ImageAdjustments)
            ArgumentNullException.ThrowIfNull(adjustments)
            adjustments.CurveRgbPoints = Master
            adjustments.CurveRedPoints = Red
            adjustments.CurveGreenPoints = Green
            adjustments.CurveBluePoints = Blue
            adjustments.CurveLuminancePoints = Luminance
        End Sub

        Private Shared Function Normalize(value As String, parameter As String) As String
            If String.IsNullOrWhiteSpace(value) Then Return Identity
            Dim pairs = value.Split(";"c)
            ' Each legacy spline-table sample visits the control points. Bound that
            ' preparation cost while allowing densely sampled imported point curves.
            If pairs.Length < 2 OrElse pairs.Length > 1024 Then
                Throw New ArgumentException("A curve requires 2..1024 control points.", parameter)
            End If

            ' The legacy parser repairs malformed strings silently. At this new boundary
            ' reject them instead: NaN, clipping or discarded points must not quietly
            ' change an imported high-precision recipe. Interior endpoints remain valid.
            Dim points As New List(Of (X As Double, Y As Double))()
            For Each pair In pairs
                Dim coordinates = pair.Split(","c)
                Dim x, y As Double
                If coordinates.Length <> 2 OrElse
                   Not Double.TryParse(coordinates(0), NumberStyles.Float, CultureInfo.InvariantCulture, x) OrElse
                   Not Double.TryParse(coordinates(1), NumberStyles.Float, CultureInfo.InvariantCulture, y) OrElse
                   Not Double.IsFinite(x) OrElse Not Double.IsFinite(y) OrElse
                   x < 0 OrElse x > 255 OrElse y < 0 OrElse y > 255 Then
                    Throw New ArgumentException("Curve coordinates must be finite and within 0..255.", parameter)
                End If
                points.Add((x, y))
            Next
            points.Sort(Function(a, b) a.X.CompareTo(b.X))
            For i = 1 To points.Count - 1
                If points(i).X = points(i - 1).X Then Throw New ArgumentException("Curve X coordinates must be unique.", parameter)
            Next

            ' A redundant diagonal is mathematically identity. Avoid an unnecessary
            ' primaries/transfer roundtrip that would otherwise perturb neutral input.
            If points(0).X = 0 AndAlso points(points.Count - 1).X = 255 AndAlso
               points.All(Function(point) point.X = point.Y) Then Return Identity
            Return String.Join(";", points.Select(Function(point) point.X.ToString("R", CultureInfo.InvariantCulture) &
                                                     "," & point.Y.ToString("R", CultureInfo.InvariantCulture)))
        End Function
    End Class
End Namespace
