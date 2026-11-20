Imports System

Namespace Services
    ''' <summary>Native linear Rec.2020 blur and a reusable unsharp primitive.
    ''' The UI blur retains the existing sigma/size law and three-box Gaussian
    ''' approximation. Averaging moves to working light; these are not encoded-sRGB
    ''' adapters and do not discard a wide/HDR residual behind a blurred SDR image.</summary>
    Friend NotInheritable Class WorkingSpatial
        Private Sub New()
        End Sub

        Friend Shared Function RenderedBlur(source As LinearWorkingFrame, amount As Single) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            If source.Domain <> WorkingImageDomain.RenderedLinear Then Throw New ArgumentException("Rendered input required.", NameOf(source))
            RenderedLateColorSettings.Check(amount, NameOf(amount), 0, 100)
            Dim sigma = ImageProcessor.WorkingImageBlurSigma(source.Width, source.Height, amount)
            Return Blur(source, If(sigma < 0.5F, 0, sigma))
        End Function

        Friend Shared Function Blur(source As LinearWorkingFrame, sigma As Single) As LinearWorkingFrame
            Return Filter(source, sigma, 0, sharpen:=False)
        End Function

        ''' <summary>Linear unsharp primitive, not yet a replacement for the editor's
        ''' calibrated multiscale/deconvolution/detail/masking sharpening methods.
        ''' Preserve source coverage; compare straight colors only after alpha-aware
        ''' filtering, so an alpha edge is not mistaken for a texture edge.</summary>
        Friend Shared Function Unsharp(source As LinearWorkingFrame, sigma As Single, amount As Single) As LinearWorkingFrame
            RenderedLateColorSettings.Check(amount, NameOf(amount), 0, 5)
            Return Filter(source, sigma, amount, sharpen:=True)
        End Function

        Private Shared Function Filter(source As LinearWorkingFrame, sigma As Single, amount As Single,
                                        sharpen As Boolean) As LinearWorkingFrame
            ArgumentNullException.ThrowIfNull(source)
            RenderedLateColorSettings.Check(sigma, NameOf(sigma), 0, 512)
            If sigma = 0 OrElse (sharpen AndAlso amount = 0) Then
                Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain, Sub(y, row) source.ReadLinearRow(y, row))
            End If
            Dim count = source.Width * source.Height
            Dim planes = {New Single(count - 1) {}, New Single(count - 1) {}, New Single(count - 1) {}, New Single(count - 1) {}}
            Dim input(source.Width * 4 - 1) As Single
            For y = 0 To source.Height - 1
                source.ReadLinearRow(y, input)
                For x = 0 To source.Width - 1
                    For c = 0 To 3
                        planes(c)(y * source.Width + x) = input(x * 4 + c)
                    Next
                Next
            Next
            ' Four premultiplied planes share exactly the same linear kernel.
            ' Keep the horizontal/vertical intermediates in F32; pack F16 only
            ' after the complete blur/unsharp operation. No image-sized RGB8 copy.
            For c = 0 To 3
                planes(c) = ImageProcessor.BlurWorkingPlane(planes(c), source.Width, source.Height, sigma)
            Next
            Return LinearWorkingFrame.Create(source.Width, source.Height, source.Domain,
                Sub(y, row)
                    If sharpen Then source.ReadLinearRow(y, row)
                    For x = 0 To source.Width - 1
                        Dim i = y * source.Width + x, at = x * 4
                        Dim blurredAlpha = Math.Clamp(CDbl(planes(3)(i)), 0, 1)
                        If sharpen Then
                            Dim alpha = CDbl(row(at + 3))
                            If alpha <= 0 OrElse blurredAlpha <= 0 Then Continue For
                            For c = 0 To 2
                                Dim original = row(at + c) / alpha
                                Dim blurred = planes(c)(i) / blurredAlpha
                                row(at + c) = CSng((original + amount * (original - blurred)) * alpha)
                            Next
                        Else
                            For c = 0 To 2
                                row(at + c) = planes(c)(i)
                            Next
                            row(at + 3) = CSng(blurredAlpha)
                        End If
                    Next
                End Sub)
        End Function
    End Class

    Partial Public Class ImageProcessor
        Friend Shared Function BlurWorkingPlane(values As Single(), width As Integer, height As Integer, sigma As Single) As Single()
            Return BlurPlaneLikeSkia(values, width, height, sigma, preciseDifferences:=True)
        End Function

        Friend Shared Function WorkingImageBlurSigma(width As Integer, height As Integer, amount As Single) As Single
            Dim normalized = amount / 100.0F
            Return Math.Min(width, height) * ImageBlurSigmaShare * normalized * normalized
        End Function
    End Class
End Namespace
