Imports System
Imports System.Collections.Generic
Imports SkiaSharp

Namespace Services
    ''' <summary>Immutable film calibration in normalized, encoded sRGB coordinates.
    ''' The caller supplies the already geometry/crop-corrected rendered frame and
    ''' keeps this snapshot for consistent preview/export. No image is cropped here.</summary>
    Friend NotInheritable Class FloatingFilmMeasurement
        ' A logarithm needs a positive floor. Use a 16-bit-equivalent floor rather
        ' than the old one-byte code so deep scan density is not discarded.
        Friend Const LogFloor As Double = 1.0 / 65535.0
        Private ReadOnly _base As Double()
        Private ReadOnly _density As Double()
        Friend ReadOnly Property MeasurementArea As SKRectI
        Friend ReadOnly Property SampleCount As Integer
        Friend ReadOnly Property BlockSize As Integer

        Private Sub New(filmBase As Double(), density As Double(), area As SKRectI,
                        count As Integer, blockSize As Integer)
            _base = filmBase : _density = density
            MeasurementArea = area : SampleCount = count : Me.BlockSize = blockSize
        End Sub

        Friend Function CopyBase() As Double()
            Return CType(_base.Clone(), Double())
        End Function

        Friend Function CopyDensity() As Double()
            Return CType(_density.Clone(), Double())
        End Function

        Friend Shared Function Analyze(source As LinearWorkingFrame) As FloatingFilmMeasurement
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Rendered input required.", NameOf(source))

            ' About 250k area samples suppress grain without building an RGB8 image
            ' or allocating another full-resolution F32 image. Every source pixel
            ' participates in its block's linear-light average.
            Dim stepPx = Math.Max(1, CInt(Math.Sqrt(source.Width * CDbl(source.Height) / 250000.0)))
            Dim cols = (source.Width + stepPx - 1) \ stepPx
            Dim rows = (source.Height + stepPx - 1) \ stepPx
            Dim grid(cols * rows * 3 - 1) As Double
            Dim luma(cols * rows - 1) As Double
            Array.Fill(luma, -1.0)
            Dim row(source.Width * 4 - 1) As Single
            Dim sums(cols * 3 - 1) As Double
            Dim counts(cols - 1) As Integer

            For gy = 0 To rows - 1
                Array.Clear(sums) : Array.Clear(counts)
                For y = gy * stepPx To Math.Min(source.Height, (gy + 1) * stepPx) - 1
                    source.ReadLinearRow(y, row)
                    For x = 0 To source.Width - 1
                        Dim at = x * 4, alpha = CDbl(row(at + 3))
                        ' Match the legacy visibility cutoff and equal sample weighting.
                        ' Unpremultiply before measuring: coverage is not film density.
                        If alpha < 8.0 / 255.0 Then Continue For
                        Dim r = row(at) / alpha, g = row(at + 1) / alpha, b = row(at + 2) / alpha
                        Dim gx = x \ stepPx, index = gx * 3
                        sums(index) += Math.Clamp(1.660491002 * r - 0.587641139 * g - 0.072849863 * b, 0.0, 1.0)
                        sums(index + 1) += Math.Clamp(-0.124550475 * r + 1.132899897 * g - 0.008349422 * b, 0.0, 1.0)
                        sums(index + 2) += Math.Clamp(-0.018150763 * r - 0.100578898 * g + 1.118729661 * b, 0.0, 1.0)
                        counts(gx) += 1
                    Next
                Next
                For gx = 0 To cols - 1
                    If counts(gx) = 0 Then Continue For
                    Dim cell = gy * cols + gx
                    For c = 0 To 2
                        grid(cell * 3 + c) = PointColorMath.SrgbEncode(sums(gx * 3 + c) / counts(gx))
                    Next
                    ' Holder detection shares the calibration block averages instead
                    ' of a grain-sensitive top-left pixel. Its thresholds retain the
                    ' old encoded-sRGB/BT.601 meaning, not Rec.2020 scene luminance.
                    luma(cell) = 0.299 * grid(cell * 3) + 0.587 * grid(cell * 3 + 1) + 0.114 * grid(cell * 3 + 2)
                Next
            Next

            Dim cells = FindHolderArea(luma, cols, rows)
            Dim channels = {New List(Of Double)(), New List(Of Double)(), New List(Of Double)()}
            For y = cells.Top To cells.Bottom - 1
                For x = cells.Left To cells.Right - 1
                    Dim cell = y * cols + x
                    If luma(cell) < 0 Then Continue For
                    For c = 0 To 2
                        channels(c).Add(grid(cell * 3 + c))
                    Next
                Next
            Next
            If channels(0).Count = 0 Then Throw New InvalidOperationException("No visible film samples to measure.")
            Dim bases(2), densities(2) As Double
            For c = 0 To 2
                channels(c).Sort()
                bases(c) = Percentile(channels(c), 0.995)
                densities(c) = Percentile(channels(c), 0.005)
            Next
            Dim area = New SKRectI(cells.Left * stepPx, cells.Top * stepPx,
                                  Math.Min(source.Width, cells.Right * stepPx),
                                  Math.Min(source.Height, cells.Bottom * stepPx))
            Return New FloatingFilmMeasurement(bases, densities, area, channels(0).Count, stepPx)
        End Function

        Private Shared Function Percentile(sorted As List(Of Double), fraction As Double) As Double
            ' Preserve the old nearest-rank/ties-to-even percentile convention.
            Dim rank = Math.Clamp(CInt(Math.Round(sorted.Count * fraction)), 1, sorted.Count)
            Return sorted(rank - 1)
        End Function

        Private Shared Function FindHolderArea(luma As Double(), cols As Integer, rows As Integer) As SKRectI
            Dim full = New SKRectI(0, 0, cols, rows)
            If cols < 20 OrElse rows < 20 Then Return full
            Dim maxRows = CInt(rows * 0.15), maxCols = CInt(cols * 0.15)
            Dim interior = New List(Of Double)()
            For y = maxRows To rows - maxRows - 1
                For x = maxCols To cols - maxCols - 1
                    Dim v = luma(y * cols + x)
                    If v >= 0 Then interior.Add(v)
                Next
            Next
            If interior.Count = 0 Then Return full
            interior.Sort()
            Dim density = Percentile(interior, 0.005)
            Dim limit = density - Math.Max(8.0 / 255.0, density / 4.0)
            If limit <= 0 Then Return full

            ' Only uniformly darker edge lines qualify. A dark subject extending
            ' into the interior lowers the reference and must not be removed;
            ' bright unexposed film is the base, not an opaque scanner holder.
            Dim mean = Function(isRow As Boolean, index As Integer, first As Integer, last As Integer) As Double
                           Dim sum = 0.0, count = 0
                           For k = first To last - 1
                               Dim v = If(isRow, luma(index * cols + k), luma(k * cols + index))
                               If v < 0 Then Continue For
                               sum += v : count += 1
                           Next
                           Return If(count = 0, Double.MaxValue, sum / Math.Max(1, count))
                       End Function
            Dim top = 0, bottom = 0, left = 0, right = 0
            While top < maxRows AndAlso mean(True, top, 0, cols) < limit
                top += 1
            End While
            While bottom < maxRows AndAlso mean(True, rows - 1 - bottom, 0, cols) < limit
                bottom += 1
            End While
            While left < maxCols AndAlso mean(False, left, top, rows - bottom) < limit
                left += 1
            End While
            While right < maxCols AndAlso mean(False, cols - 1 - right, top, rows - bottom) < limit
                right += 1
            End While
            ' Include a narrow seam only on detected edges: scanner-holder
            ' boundaries are blurred and would otherwise pull the density tail.
            If top > 0 Then top = Math.Min(maxRows, top + Math.Max(1, rows \ 50))
            If bottom > 0 Then bottom = Math.Min(maxRows, bottom + Math.Max(1, rows \ 50))
            If left > 0 Then left = Math.Min(maxCols, left + Math.Max(1, cols \ 50))
            If right > 0 Then right = Math.Min(maxCols, right + Math.Max(1, cols \ 50))
            Return New SKRectI(left, top, cols - right, rows - bottom)
        End Function
    End Class
End Namespace
