# Imaging precision probe: staged F16/Rec.2020 migration

This standalone diagnostic establishes the current renderer's baseline and tests the
proposed **RGBA F16 storage / F32 calculation / linear Rec.2020** path. It does not
enable a new renderer or change image-processing preferences.

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project tools/ImagingPrecisionProbe -c Release -r win-x64 -- --output C:/Temp/ferrumpix-f16-probe
```

On other platforms use the matching runtime identifier, for example `linux-x64`,
`linux-arm64` or `osx-arm64`. Run in a normal desktop session for the GPU tests;
a headless or remote session may not expose the same GPU as the editor.

Use `--cpu-only` to skip graphics initialization. Add `--image <path>` for each
photograph to include in the existing renderer's baseline. RAW input goes through
FerrumPix's normal RAW decoder; development availability depends on the native
libraries installed with the application. Images are decoded at their normal size,
then reduced to at most 2048 pixels on the long side for this preview baseline.
The tool never writes to the source image or its sidecar.
These are fresh diagnostic recipes, not the photo's saved sidecar adjustments.
The build copies an optional LibRaw binary from the application's build output.
RAW input is rejected when LibRaw is unavailable rather than knowingly measuring
only an embedded JPEG preview.

```powershell
dotnet run --project tools/ImagingPrecisionProbe -c Release -r win-x64 -- --output C:/Temp/ferrumpix-f16-photos --image C:/Photos/test.CR3 --image C:/Photos/test.tif
```

## What to inspect

- `report.md` is the readable report; `report.json` contains structured results.
- `*-neutral.png`, `*-tone-color.png`, and `*-details.png` capture the **current**
  8-bit renderer, not the proposed high-precision renderer.
- `*-f16-roundtrip.png` explicitly crosses the new boundary: developed sRGB ->
  native linear Rec.2020 F16 -> diagnostic SDR sRGB. Compare it with the neutral
  image. It should look the same apart from small storage/output rounding.
- View the images for broken colors, bands, alpha fringes and unexpected clipping.
- Check that the renderer name identifies the expected GPU and completed jobs are
  nonzero. A GPU skip is **not** a successful GPU test.
- Repeat into a second output folder. Hashes should agree for repeated runs of the
  same build and settings; timings can vary. Do not overwrite the first report.

## Checks and limits

The precision tests cover F16/F32 allocation, negative and above-white values,
small shadow values, exposure, linear sampling, premultiplied alpha blending,
32 repeated identity writes, and a Rec.2020 matrix against a double-precision
reference. GPU work uses FerrumPix's actual `GpuImageService`; CPU success cannot
silently count as GPU success.

Absolute/relative tolerances are recorded in `PrecisionChecks.Compare`. They allow
F16 storage rounding and small shader arithmetic differences. Repeated identity
writes must introduce no further drift. These small tests do not establish the
precision of a complete future tool chain or quantify visible color differences.

The baseline runs neutral, tone/color and detail recipes on the CPU, warms each
recipe, and reports the median of three timed renders. It separately reports mean
managed allocations and peak process working set. PNG encoding and image decoding
are excluded from render time; GPU checks include setup/readback and **are not GPU
throughput benchmarks**. No full-resolution export performance claim is made.

Exit code `1` means a required check failed or requested GPU execution did not pass;
`2` means invalid command-line arguments.
Unsupported GPU F32 targets are optional comparisons and are recorded as skips;
they do not block F16 support because the F32 reference runs on the CPU. Skipped
GPU execution or any required F16 test failure does block it.

The report must show no **required** failures, a validated CPU fallback, and an
explicitly passed GPU gate before GPU migration proceeds. Direct Skia CPU F16
checks remain visible as optional diagnostics: the selected fallback now uses
F32 shader surfaces plus explicit F16 storage instead. Retain failed reports;
never weaken the shadow-value tolerance to make an unsupported path pass.

Build outputs are temporary; keep result folders outside the source tree. This
tool references the application project, so its build also checks the application.

## Initial checkpoint findings

The first Windows run on an AMD Radeon / ANGLE D3D11 backend showed:

- Native F16 GPU input and output retained negative/HDR values, small shadow
  values, linear sampling and blending. Rec.2020 conversion passed.
- GPU F32 render targets were unsupported. Uploading an F32 image through this
  backend lost extended-range values before the shader. Native F16 input avoided
  that loss; the shadow transport check reports both routes explicitly.
- Skia's CPU F16 shader route lost a linear value of `0.000001`, including with
  native F16 input. Reading the native output half bits confirmed the value was
  already zero, rather than being lost only in conversion to F32 readback.
- CPU F32 tests passed. All three existing-renderer synthetic baseline hashes
  were stable across runs.

Migration stopped at this checkpoint until an alternative CPU path was tested.
The results above describe one backend, not every GPU.

## CPU fallback experiment

`CpuHalfStorage` is an isolated prototype, not a production renderer change. It
unpacks native half bits with .NET's `Half` conversion, runs the shader on an F32
raster surface, then explicitly packs the result back into F16. No intermediate
Skia F16 raster surface is used. Non-finite values and F16 overflow are rejected
rather than silently clipped. Underflow below the representable F16 range still
rounds normally; the adapter does not create extra precision.

Required checks cover all 63,488 finite half bit patterns, including signed zeros
and subnormals; shadow/HDR exposure; sampling; alpha blending; Rec.2020 conversion;
32 repeated shader/storage cycles down to `2^-24`; and invalid input rejection.
The GPU run additionally compares native F16 exposure against this CPU fallback.
On the initial AMD/ANGLE test, this comparison was exact. The original CPU Skia
F16 failures are still printed with `Required: false`, not changed to passes.

The prototype uses full-image F32 scratch allocations. Its storage staging smoke
benchmark reports time and managed allocations explicitly. It proves a viable
precision fallback but does not yet prove acceptable production memory usage or
full-image throughput. Bounded scratch buffers, tiling and integration are later
steps. Photographic baseline PNGs still come from the unchanged production path.

## Image contract and boundary checkpoint

The application now contains internal `LinearWorkingFrame`, `HalfPixelStorage`
and `WorkingImageBoundary` types. They are not connected to normal editing yet.
The probe accesses the internal contract through `InternalsVisibleTo`, rather
than making a diagnostic API part of the public application interface.

The frame owns immutable native **RGBA F16**, premultiplied, **linear Rec.2020
D65** values. Negative and above-white RGB are retained. Alpha is finite in
`[0,1]`; colors at alpha rounded to zero are canonicalized to transparent black.
Domain is explicit: `SceneLinear` and `RenderedLinear` are distinct even though
both use linear encoding. The frame does not infer scene values from a bitmap.

CPU F32 staging uses explicit half conversion; GPU snapshots keep the native
F16 format. The sRGB input boundary accepts declared encoded BGRA8, RGBA8 or
RGBA16, unpremultiplies encoded alpha, decodes sRGB, transforms primaries and
premultiplies in linear light. Inputs with a conflicting profile are rejected.
All developed sRGB inputs become `RenderedLinear`, never `SceneLinear`.

`ToDiagnosticSdrSrgb8` only accepts rendered frames. It converts to sRGB before
clipping and quantizing; it is deliberately not HDR tone mapping or a replacement
for monitor-profile conversion. Scene frames require an explicit rendering stage.
The boundary adapters process rows without an extra full-image F32 array; an F32
bitmap for CPU shader staging still needs a full native allocation.

Required checks cover ownership, format metadata, multiple rows, low-alpha and
zero-alpha pixels, channel ordering, source profiles, 16-bit shadows, lifetime,
invalid input and domain guards. The GPU check uploads an actual application-frame
snapshot. Each supplied photograph also gets a round-trip output, checked for
unchanged alpha and a maximum two-code-value premultiplied sRGB deviation. This
limit measures this boundary only, not accumulated error through a future chain.

## RAW scene-input checkpoint (opt-in)

Add `--raw-scene` to the same command with at least one sensor RAW `--image`.
The standard baseline and boundary tests still run. This additional check reads
the immutable full-resolution RGB16 RAW cache **before** `Convert16Rows` applies
the rendering curve, black subtraction, clipping, gamma encoding and dithering.
It combines the cache's camera-to-sRGB matrix with sRGB-to-Rec.2020 in F32, restores
the cache normalization/headroom, and stores a native F16 `SceneLinear` frame.
No developed RGB8 image is used as the source of that scene frame.

LibRaw demosaicing, its white balance and configured sensor/highlight preparation
have already happened. They are not applied again. Model denoising, lens correction,
sensor-edge crop/distortion and geometry have **not** been migrated into this path
yet; neither the camera base exposure nor any rendering curve is baked into the
frame. Source metadata reports these boundaries explicitly. A TIFF is skipped as
a rendered input, rather than mislabeled as scene-linear data.

For each RAW the report records full dimensions, F16 storage size, normalization,
reference base exposure, observed signed/HDR range and channel counts. Both -2 EV
and +2 EV are checked at every pixel against F32 multiplication with explicit F16
rounding. Synthetic references independently check RGB16 decoding and the combined
camera/Rec.2020 matrix, including a one-code-value shadow and negative/HDR colors.
Invalid source data, normalization, domain and F16 overflow must fail explicitly.

This checkpoint produces **numerical RAW scene results**, not additional RAW scene
preview PNGs. The existing boundary PNGs still show the developed-image round trip.
Scene data intentionally cannot pass the SDR adapter until an explicit rendering
stage is implemented. Normal editor rendering and saved recipes remain unchanged.

The initial 18-file corpus run passed all required checks: all 17 sensor RAWs
retained signed/HDR values at full resolution and matched the explicit F32/F16
reference for both exposure adjustments. The rendered TIFF was skipped only for
scene import. All 57 production baseline hashes remained identical. Native GPU
transport and the selected CPU fallback passed; the two known optional direct
CPU-F16 shadow failures remain visible. Peak process working set was about 1.76
GiB for this combined diagnostic run, not an editor-memory or throughput target.

## Scene-rendering checkpoint (opt-in)

Use `--render-scene` with the same RAW inputs; it also enables `--raw-scene`.
For example, from the repository root after building:

```powershell
$probeArgs = @('--render-scene', '--output', "$env:TEMP/FerrumPix-F16-SceneRendering")
Get-ChildItem -LiteralPath "$env:USERPROFILE/Pictures/raws" -File |
    Where-Object { $_.Extension -in '.CR2', '.CR3', '.tif', '.tiff' } |
    Sort-Object Name |
    ForEach-Object { $probeArgs += @('--image', $_.FullName) }
& ./tools/ImagingPrecisionProbe/bin/Release/net10.0/win-x64/ImagingPrecisionProbe.exe @probeArgs
```

Each sensor RAW gets three additional previews: `image-N-scene-neutral.png`,
`image-N-scene-minus2ev.png`, and `image-N-scene-plus2ev.png`. Match `N` to the
sorted input order. These originate from the **full-resolution scene frame**,
not the developed RGB8 baseline. Exposure is applied before rendering. The
camera's reference base exposure is applied once by the rendering stage;
normalization and LibRaw white balance are not applied again.

`SceneFrameRenderer` explicitly changes `SceneLinear` to `RenderedLinear`:

1. Read native F16 Rec.2020 D65 into row-sized F32 scratch.
2. Transform to linear ProPhoto D50 for the existing RAW tone-curve approach.
   Combined matrices include the existing Bradford white-point adaptation;
   there is no stored sRGB intermediate.
3. Apply black subtraction with its soft toe, the highlight shoulder (knee
   0.45), and the existing ACR3 curve. Map the brightest/darkest channels and
   interpolate the middle channel proportionally, as the current RGBTone does.
   The calculations avoid its integer16 output lookup table.
4. Transform back to linear Rec.2020 D65 and store `RenderedLinear` F16. The
   renderer does not clamp Rec.2020 RGB to `[0,1]`.
5. Cross the diagnostic SDR boundary: transform to sRGB, clip to the SDR gamut,
   encode sRGB, quantize to BGRA8, then resize to a 2048-pixel-long-side PNG.

Inspect neutral colors, bright colored areas, shadow gradients and highlight
rolloff. The +/-2 EV versions should change visibly and smoothly. Compare the
neutral scene preview with `image-N-neutral.png` for obvious color errors, but
do not expect byte parity: the new checkpoint intentionally omits lens/gain-map
corrections, sensor-edge crop, geometry, model denoise, magenta-highlight repair
and camera-specific shadow-saturation treatment. The old exposure-dependent black
point policy has not been migrated either; the diagnostic exposure operation
multiplies scene RGB before a fixed rendering black point. This is an SDR view
of retained HDR scene data, not an HDR output or monitor-profile validation.

Synthetic required checks compare neutral toe/midtones/HDR and colored/partial
alpha data against double-precision references, check neutral monotonicity and
source ownership, and reject repeated rendering or invalid parameters. This CPU
rendering stage is not yet GPU-accelerated. Normal editing remains on the original
path; its existing PNG hashes must still match the previous checkpoint.

The initial full-corpus rendering run passed all required checks (183 PASS;
the two known optional direct CPU-F16 failures and five skips remain explicit).
All 51 scene previews were generated, all 57 production baseline hashes were
unchanged, and the existing GPU transport/CPU fallback gates passed. Peak process
working set was about 1.88 GiB for the combined diagnostic run. This includes
decode, old-renderer baselines, full-resolution scene/exposure/rendered buffers
and PNG output; it is not an isolated rendering benchmark or a production memory
target. Visual acceptance of the new previews remains a separate checkpoint.
