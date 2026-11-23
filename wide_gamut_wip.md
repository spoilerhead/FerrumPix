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

Remaining work includes native export, layer and mask orchestration, retouch,
LUT and preset routing, depth effects, additional input profiles, GPU kernels,
and memory and performance optimization. See the precision probe and its
fixtures for the stage-by-stage checks.
