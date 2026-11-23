# Wide-gamut F16 / Rec.2020 migration

The experimental editor preview is available under **Settings → Pipeline** on
`wb_16bit`. It defaults to off. The working image uses premultiplied RGBA F16
storage in linear Rec.2020 D65. Operators generally calculate in F32, with
higher-precision accumulation where needed. The editor converts to SDR sRGB
at the display boundary.

The preview connects native RAW development, supported matrix-profile JPEG and
RGB16 TIFF input, geometry, global tone and color, sharpening, denoise, and
extensions. The extension host hands straight linear Rec.2020 RGB to the
shader directly and preserves signed and HDR values through its output.
Extensions keep their stage and priority around grain.

The editor integration checkpoint fixes older white balance recipes that carry
Kelvin or Kelvin-tint values without a current model number. The preview
selects the current model on its temporary recipe. Relative-only recipes keep
their original model, and unedited RAWs can use the white balance LibRaw already
applied. Current anchored RAW adjustments still run before the tone curve.

RAW lens distortion uses the existing profile and framing calculation, then
resamples in linear Rec.2020 before editor geometry. This allows RAW previews
with the lens correction switch active and avoids an encoded RGB8 image between
development and geometry. The Pipeline settings use the normal translation
resources for English and German.

Remaining work includes wide-gamut/HDR export, layer and mask orchestration, retouch,
LUT and preset routing, depth effects, additional input profiles, GPU kernels,
and memory and performance optimization. See the precision probe and its
fixtures for the stage-by-stage checks.


The editor now saves supported single images as JPEG, PNG or TIFF using the
same native renderer at full resolution. JPEG is 8-bit. PNG and TIFF can
write 8 or 16 bits per channel. The 16-bit path converts linear Rec.2020 to
straight encoded sRGB and quantizes directly at the encoder boundary, including
translucent pixels. This first export slice uses the existing SDR sRGB format
and metadata conventions, so wide-gamut/HDR output and broader native input
support remain separate. Project, layered and other image formats still use the
legacy mode. RAW recipe saving continues through the existing sidecar path.

The precision probe verifies the normal save entry point for JPEG, PNG16 and
TIFF16. It checks the output dimensions, compares 16-bit TIFF scanline samples
with the native render, confirms values between 8-bit codes, and checks that a
cancelled export preserves the destination. All required checks pass. The
two previously recorded optional Skia CPU F16 failures remain.


### Export round trip

Native TIFF exports carry a matrix/TRC sRGB ICC profile independently of the
metadata-copy preference. The profile comes from Little CMS's standard sRGB
profile generator. The editor can reopen stripped, contiguous, single-page
8-bit and 16-bit RGB TIFFs with a supported embedded profile. Four-channel TIFF
requires an explicit unassociated alpha tag, so color is decoded before
coverage is applied in linear light.

The Skia backend rejects integer RGBA16 PNG decoding. The native path reads the
16-bit RGB/RGBA scanlines of non-interlaced sRGB PNGs, verifies chunk checksums,
unfilters the rows and passes straight source codes to the working color
boundary. Interlaced, animated, non-sRGB and color-key PNG16 variants need
additional adapters. The native export format remains SDR sRGB.

The precision probe saves, reopens and compares PNG16 and TIFF16 with partial
alpha against the output boundary's straight 16-bit codes. It also checks a
tagged TIFF8 round trip and continues to reject unsupported TIFF layouts.
