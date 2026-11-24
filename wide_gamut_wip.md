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

See [Remaining work](#remaining-work) for the next iterations and their
acceptance checks. The [implementation log](wide_gammut_wip.md) records
the earlier experiments and their validation.


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

### RAW sensor edge and camera aspect crops

Both RAW crop switches now use the same oriented crop rectangle in the legacy
and F16 decode paths. A camera aspect crop takes precedence when present.
Without aspect metadata, the sensor edge is trimmed only if that option is
enabled. The F16 renderer copies the selected pixel rows after RAW development
and before lens distortion, so distortion uses the cropped center and no
intermediate conversion or resampling alters the crop. Preview, comparison,
zoom detail and export all use this ordering.

### Color tools and Before/After on rendered files

The Before side waits for its unadjusted native render instead of showing the
current working bitmap while the comparison is unavailable. Rendered JPEG and
TIFF ignore RAW-only crop flags. Untagged RGB TIFF uses the editor's sRGB
convention. Tagged TIFF continues to use its supported embedded profile.
The PNG and JPEG RGB8 path applies orientation as a pixel permutation.

Rendered primary calibration uses the existing linear calibration matrix after
white balance, with the matrix expressed in Rec.2020 to avoid an intermediate
SDR conversion. RAW with current anchored white balance keeps calibration on
the development ramp. The probe checks each color adjustment independently on
JPEG and untagged TIFF and compares the Before samples against the neutral image.

### RAW comparison parity and GPU preview

At fit and zoom, RAW Before uses the legacy comparison decode and its default
lens policy. It also honors the embedded camera JPEG option. Lens edits affect
After independently. Both sides still receive the editor's comparison geometry
so that the crop and orientation can be compared in the same view.

With GPU image processing enabled, the F16 editor preview can send area
downscaling, polynomial lens distortion and extension shaders to the existing
GPU worker. The resamplers work in premultiplied linear Rec.2020. Extensions
still receive straight linear Rec.2020 through the existing host interface.
The worker reads back through F32 and explicitly packs to F16 because direct
CPU Skia F16 readback has lost subnormal shadow values in the precision probe.

Area downscaling uses the CPU for reductions over seven to one on either axis
to keep the shader's sample count bounded. Measured lens profiles represented
as knots retain the CPU interpolator. The renderer also falls back to the CPU
if the GPU is unavailable or fails to produce a frame. Export still uses the
CPU renderer.

TIFF decoding and conversion from the input profile still run on the CPU
before the preview can downscale. RAW development, other geometry and effects,
global color and tone, sharpening and denoise also remain on the CPU. This
checkpoint checks output parity; end-to-end preview timing and caching remain
separate work.

The precision probe checks the area filter, each of the three polynomial lens
models and two extension shaders against their CPU equivalents. The fixtures
include signed and HDR channels, subnormal shadows and partial alpha. A separate
check exercises the preview entry points and verifies that they dispatch the
three jobs through the normal GPU worker.

### Integration with main 0.9.90-3

The published extension catalog retains its sRGB shaders and checksums. The
experimental editor and precision probe package the linear variants from
`docs/extensions/native`, so catalog updates and native recipes can use their
respective input contracts. Native TIFF export retains its embedded sRGB ICC
profile and shares main's parallel strip encoder. Native lighting uses main's
edge-aware depth and shadow calculation with floating-point depth samples.
The legacy tone and lighting fixtures were captured from an independent build
of main at `17d6ce39`. All five affected fixture sets matched the merged
renderer before their references were updated.

The GPU image-processing switch controls the image kernels. Window rendering
and optional GPU AI inference have independent paths. TIFF input still reads
scanlines and builds the F16 surface on the CPU row by row, so GPU preview
downscaling starts after that work.

## Remaining work

The opt-in single-image path covers the supported inputs, global adjustments,
and full-resolution SDR sRGB export. Interactive performance, advanced editor
features, and color-managed output still need work before wider rollout.

### 1. Measure and reuse preview sources

- **Measure before optimizing.** Compare cold open, repeated slider changes,
  zoom, and export on the same RAW and film-scan TIFF in both pipelines, with
  GPU image processing on and off. Record elapsed time, peak memory, and
  GPU dispatches. Time decode and color conversion, resize, adjustments, GPU
  transfers, and presentation separately. Task Manager utilization alone
  cannot distinguish which stage is limiting responsiveness.
- **Cache decoded TIFF and prepared RAW.** The original editor can reuse a
  preview source during slider changes. The F16 renderer currently goes back
  to the file. Start by reusing the decoded, oriented linear Rec.2020 TIFF so
  exposure or saturation changes can skip decompression and ICC conversion.
  Cache RAW at a boundary before editable development controls, with
  invalidation when upstream RAW options change. Account for fit, zoom,
  Before/After, external file changes, and cancellation. Bound cache memory
  and preserve the legacy preview path.
- **Reduce full-frame copies and scratch memory.** Profile TIFF open, zoom,
  and full-resolution export. Target the buffers that dominate peak memory
  with reuse or tiling. Evaluate parallel TIFF profile conversion if the
  remaining cold-open time warrants it.

Check that ordinary slider edits reuse the prepared source, source changes
invalidate it, and cached and uncached renders agree. Repeat the timing and
memory measurements after each change.

### 2. Close the GPU coverage gap

- **Port the fused tone and color pass first.** The original editor can run
  eligible exposure, tone, curves, and color operations together on the GPU.
  The F16 editor still runs these on the CPU. Preserve operation order and
  the placement of RAW exposure, white balance, and calibration. Include the
  cost of any statistics that remain on the CPU.
- **Port detail tools and effects in groups.** Start with sharpening and
  traditional luminance/chroma denoise, then port blur, clarity, structure,
  haze, glow, vignette, and grain as measurements justify. Keep compatible
  stages and extensions on a shared GPU frame to avoid repeated transfers.
  Keep the CPU path for unsupported devices and operations. Extensions
  continue to receive straight linear Rec.2020 with no automatic gamma or
  primaries conversion.
- **Evaluate the other CPU paths after profiling.** Measured lens profiles,
  larger resize ratios, general geometry, and native export have CPU paths.
  Port them when end-to-end gains justify the transfer and memory costs.

Check each group against the CPU reference using signed and HDR values,
subnormal shadows, partial alpha, and the wide-gamut cubes. Test CPU fallback
with the GPU disabled or unavailable, and measure editor responsiveness with
real photos.

### 3. Connect the remaining editor features

- **Integrate layers, masks, and local edits.** Native blending primitives
  exist. Connect them to editor layer order, selection scopes, and regional
  updates. Wire retouch, paint, baked edits, objects, and project documents
  to the working-image state and undo history.
- **Complete depth and model routing.** Connect depth-aware lighting, blur,
  fog, and masks to the current depth-map service. Keep each learned model's
  required color and numeric ranges when integrating remaining tools such as
  AI upscaling.
- **Connect LUTs and presets.** Define the color space a LUT or preset expects
  and preserve its placement in saved recipes. Verify interactions such as
  eyedroppers and keep a clear legacy path for unsupported recipes.

Verify each feature through the editor, including save/reopen, undo/redo,
Before/After at fit and zoom, and full-resolution export where applicable.

### 4. Complete color-managed input and output

- **Broaden input support.** Add adapters for remaining ICC profile types
  and file layouts as needed, including TIFF beyond the supported contiguous
  RGB cases and additional PNG16 variants. Verify embedded profiles,
  orientation, alpha, and preservation of samples between 8-bit codes.
- **Add wide-gamut and HDR display and export.** Current preview and exports
  use SDR sRGB, including 16-bit PNG/TIFF. Define output profiles, transfer
  functions, gamut and tone mapping, and HDR signaling. Verify tagged
  output by reopening it and checking a color-managed viewer.

Keep the Pipeline setting opt-in while these iterations land. Revisit the
default after representative recipes round trip, preview and export agree,
and interactive latency, memory, and CPU fallback meet the agreed targets.
