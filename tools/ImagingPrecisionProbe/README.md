# Imaging precision probe: staged F16/Rec.2020 migration

This standalone diagnostic establishes the current renderer's baseline and tests the
proposed **RGBA F16 storage / F32 calculation / linear Rec.2020** path. It does not
enable a new renderer or change image-processing preferences.

The living design and progress record is
[wide_gammut_wip.md](../../wide_gammut_wip.md). Update it after every migration
iteration, including failed checkpoints and pending visual acceptance.

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

## Highlight color repair checkpoint (opt-in)

`--compare-highlight-repair` enables scene rendering and writes both the previous
repair-off previews and additional `*-repair-on.png` previews at -2/0/+2 EV.
`highlight-repair-review.html` groups the pairs by original filename and exposure.
For the five reported files, after building, run from the repository root:

```powershell
$probeArgs = @('--compare-highlight-repair', '--output', "$env:TEMP/FerrumPix-F16-HighlightRepair")
foreach ($name in @('0M8A0758.CR2', 'IMG_0002.CR2', 'IMG_0008.CR2', 'IMG_0018.CR2', 'IMG_0113.CR2')) {
    $probeArgs += @('--image', (Join-Path "$env:USERPROFILE/Pictures/raws" $name))
}
& ./tools/ImagingPrecisionProbe/bin/Release/net10.0/win-x64/ImagingPrecisionProbe.exe @probeArgs
```

`RawDecodeService.RepairSceneHighlights` reuses the **same**
`RecoverMagentaHighlightFloat` function as normal RAW development; its existing
algorithm and the normal editor's call remain unchanged. The new adapter works
on an owned copy of the original scene and runs **before user exposure**, because
darkening first would suppress its highlight detection. It converts each pixel
from linear Rec.2020 D65 to linear sRGB D65 and divides out the source's recorded
RAW normalization. Those are the values the existing repair sees after the camera
matrix, before tone mapping (with lens corrections disabled). It then transforms
only the repair delta back into normalized Rec.2020 and stores F16. There is no
encoded-sRGB or integer8/16 intermediate. Unchanged pixels bypass the matrix
round trip, retaining their exact source F16 values; alpha and scene domain remain
unchanged. Repair-off does not call the adapter at all.

This preserves the existing heuristic, including its deliberate movement of
over-range red/blue toward cache white in affected highlights. It is color repair,
not reconstruction of missing sensor detail; unrelated signed/HDR values are not
globally clipped.

The adapter intentionally requires the `SceneSource` metadata instead of guessing
normalization from image content. Applying the heuristic directly in Rec.2020,
using restored scene brightness for the original cache thresholds, or running it
after exposure would each change its behavior. A future lens/gain-map migration
must retain the existing correction order rather than assuming these diagnostic
previews already cover those combinations.

Required tests cover a double-precision repair reference, unchanged shadows,
neutrals and green colors, HDR, partial alpha, ordering and invalid domain/source
normalization. Every photographic repaired pixel is compared with that reference;
the report records changed-pixel counts. The comparison page is for assessing
whether the original pink highlights improve and whether real colored highlights
remain acceptable. Numerical agreement alone does not establish visual acceptance.
The adapter runs on CPU and is still isolated from normal editing.

The initial five-file comparison passed all required checks (104 PASS, two known
optional direct CPU-F16 failures, four skips); CPU fallback and existing GPU
compatibility gates passed. All 18 baseline hashes for this subset and all 15
repair-off preview PNG hashes matched the preceding checkpoint. Thirty scene
previews were generated, with repair on/off at each exposure.

The repair changed 2,133,644 pixels in `0M8A0758.CR2`, 67,440 in `IMG_0002.CR2`,
12,012 in `IMG_0008.CR2`, 626,373 in `IMG_0018.CR2`, and zero in `IMG_0113.CR2`.
A targeted reference run found 130,083 pixels in `IMG_0113.CR2` above the original
cache-relative brightness gate, but none met its green-deficit condition. Its
unchanged output is therefore a limit of this specific heuristic, not evidence
that the adapter failed to run. The strong pink window/reflections in the first
file visibly improve; the lamp in `IMG_0002.CR2` still has some pink around its
now-neutral core. This checkpoint preserves the original algorithm rather than
expanding its correction criteria. Review the pairs before the next migration.

## RAW exposure / black-point policy checkpoint (opt-in)

`--compare-raw-exposure` enables the scene tests and adds
`raw-exposure-review.html`. It compares the accepted repair-on, fixed-black
rendering with `RawSceneDevelopment`, a diagnostic coordinator that retains the
app's actual RAW exposure/black-point policy. It also generates -2 EV previews
with highlight recovery disabled (knee 1) and a strong shoulder (knee 0.05).
Highlight color repair stays enabled in both, as in the normal RAW decoder.
This flag does not enable the new path in the editor.

Use the five-file command above, replacing `--compare-highlight-repair` with
`--compare-raw-exposure` and choosing a fresh output directory. Inspect the new
comparison page: neutral and +2 EV should match the accepted version, while
-2 EV should show a small improvement in deep shadow detail. Compare the final
shoulder pair for hard clipping versus smoother highlight compression. These
are RAW-stage controls only, not a migration of the later Highlights tool.

The old decoder feeds its tone ramp cache-relative values. Our F16 scene already
has cache normalization restored and exposure multiplied in. If the old ramp has
black point B and exposure E, and S = normalization * 2^userEV, the equivalent
new rendering parameters are B' = B*S and E' = E-log2(S). This changes the input
coordinates without adding exposure a second time. `ToneRampFor` remains the
shared source of the app's policy; its existing implementation is unchanged.
For the usual normalization >= 1, the rendering black point becomes
0.003 * min(2^userEV, 1). The earlier diagnostic fixed it at 0.003 for every EV.

`RawSceneDevelopment.Render` owns its repaired/exposed temporary frames and
returns an owned rendered-linear Rec.2020 F16 frame. It accepts explicit RAW
controls, not a complete editor recipe whose unrelated adjustments might be
silently ignored. The coordinator retains the accepted correction order:
source -> highlight color repair -> scene exposure -> adjusted toe/shoulder/ACR3
tone rendering. It does not migrate lens, geometry, model denoise or camera
shadow-saturation correction, and it remains CPU-only.

Required synthetic checks use an independent reference in the **old cache
coordinates**, across camera base exposure, normalization below/above 1, partial
alpha, -2/-0.5/0/+0.5/+2 EV and hard/soft shoulders. They check source ownership,
invalid settings and the app's mapping from exposure/highlight controls to RAW
parameters. Existing baseline and accepted-preview hashes are also compared at
the photographic checkpoint; visual acceptance remains a separate gate.

The first five-RAW-plus-TIFF run passed all required checks (129 PASS; the two
known optional direct CPU-F16 failures and five skips remain explicit). CPU
fallback and existing GPU compatibility gates passed. All 21 baseline hashes and
all 15 accepted repair-on reference PNGs were unchanged. All ten neutral/+2 EV
policy previews matched their references byte-for-byte; all five -2 EV previews
changed as expected. Twenty-five new policy/shoulder previews were generated,
and the comparison page's image links were verified. This validates the isolated
coordinator, not the still-unmigrated editor chain or a GPU tone implementation.

## Lens vignetting checkpoint (opt-in)

`--compare-lens-vignetting` enables scene rendering and writes
`lens-vignette-review.html` with -2 EV and neutral pairs. The off side uses the
accepted RAW exposure/repair coordinator. The on side requests measured lens
vignetting from the same profile/camera-data lookup as the app, without changing
saved settings. Missing corrections are reported as optional skips and produce
identical images; they are not replaced with guessed falloff.

The gain is applied while reading the immutable RGB16 cache, before its camera
matrix and before highlight color repair. Radius normalization, corner scale,
polynomial/knotted falloff, strength and the 0.125 minimum falloff (3 EV maximum
polynomial correction) reuse the existing implementation. Available stage-3 DNG
gain maps are sampled with the existing coordinate/rotation/strength rules and
applied to camera planes before conversion to Rec.2020. Their gain is additional
to the capped radial correction, as in the existing decoder. No integer output
rounding/clipping is inserted. Distortion and TCA remain excluded in this step.

Synthetic checks independently verify center/edge/corner gains, the radial cap,
HDR, gain-map ordering with a non-diagonal camera matrix, alpha, cache ownership
and exact identity at zero strength. Real DNG gain-map files are not included in
the supplied corpus; those combinations still require photographic validation.

Initial five-RAW-plus-TIFF run: all required checks pass (93 PASS, two known
optional direct CPU-F16 failures, seven skips); CPU fallback/GPU gates pass.
Measured corrections match the EF 50mm f/1.8 MkII and EF-S 55-250mm f/4-5.6 IS
in three RAWs. The other two RAWs are explicit profile skips. All 21 baseline
hashes and ten accepted lens-off preview hashes are unchanged.

## Transverse chromatic aberration checkpoint (opt-in)

`--compare-lens-tca` writes `lens-tca-review.html`. Both sides include the previous
vignetting stage and accepted repair/exposure policy; only TCA changes. Automatic
profile correction and an explicitly labeled red +100 / blue -100 manual-control
demonstration are shown. Missing measured TCA is an optional skip; manual controls
reuse the app's existing fine-adjustment model and do not invent a measured profile.
Full-resolution top-right corner details accompany the neutral previews; click
them for 1:1 inspection because preview reduction can hide subpixel corrections.

The profile's radial factor MULTIPLIES the offset from center. The calibrated
channels are linear sRGB red/blue, not camera RGB red/blue or Rec.2020 red/blue.
At each shifted position the importer bilinearly samples all three camera planes,
applies the existing plane/radial gains, and computes the corresponding row of the
camera-to-sRGB matrix. Green stays at the destination position in linear sRGB.
The three scalar results then convert to Rec.2020 without a stored sRGB image.
Gain maps use the destination position, matching the current decoder's convention.

The previous bilinear edge convention is retained, but intermediate samples are
not rounded back to RGB16. F32 results go straight through the matrix to F16.
Unity factors bypass resampling entirely and preserve the previous frame exactly.
Synthetic tests independently cover unequal TCA/vignette radius scales, radial
coefficients, sample direction, borders, a non-diagonal camera matrix, combined
radial/plane gains, HDR, alpha/domain and exact unity. Geometry/distortion and
sensor crop remain separate, unmigrated stages. This implementation is CPU-only.

Initial five-RAW-plus-TIFF run: all required checks pass (99 PASS, two known
optional direct CPU-F16 failures, seven skips); CPU fallback/GPU gates pass.
Three measured automatic TCA matches and two profile skips were recorded; manual
controls change all five RAWs. All 21 baseline hashes and ten TCA-off preview
hashes match the preceding vignetting iteration exactly. Forty TCA previews and
1:1 details were generated. The lens and TCA comparisons have since received
visual acceptance. Editable WB below has since received acceptance as well.

## Editable anchored RAW white balance checkpoint (opt-in)

`--compare-raw-white-balance` writes `raw-white-balance-review.html`, with seven
previews per sensor RAW: as-shot, 3200 K, 7500 K, tint -25/+25 at the capture
anchor, relative temperature +20, and 3200 K combined with -2 EV. Use the same
five-RAW-plus-TIFF invocation as earlier, substituting this flag and a fresh output
directory. Source sidecars/preferences are not changed.

The source includes the accepted lens/TCA stages. Its actual capture xy and
camera identity come from `CaptureWhiteBalanceService`, without a Kelvin round
trip. `RawWhiteBalanceSettings` consumes only current-model WB controls and
reuses the app's adaptation builder; it is not a full adjustment recipe. As-shot
bypasses WB exactly. Absolute Kelvin retains precedence over relative controls.
Tint-only edits work even when the Kelvin field is zero (as-shot).

Non-identity WB acts directly in the linear ProPhoto exposure ramp **after the
soft black toe and before shoulder/ACR3**. This replaces the old model-4 route's
developed-sRGB clipping, inverse-curve LUT and four-times-white headroom cap, not
the control model itself. There is no intermediate gamma or new F16 storage
boundary. Do not expect byte parity with the old WB route in edited highlights.

Unsupported legacy WB models, missing anchors, non-finite settings and film
negative recipes are rejected by the new settings API. Their production paths
are unchanged. Camera calibration, eyedropper, UI/recipe integration and GPU WB
remain separate migration work. Numerical references cover direct ramp/matrix/
curve evaluation, HDR/signed inputs, partial/zero alpha, no-op identity, settings
ownership and unsupported contracts. Photographic as-shot hashes must match the
preceding TCA-on previews; visual acceptance of edited WB is a separate gate.

Initial five-RAW-plus-TIFF run: all required checks pass (97 PASS, two known
optional direct CPU-F16 failures, five skips); selected CPU fallback and existing
GPU transport gates pass. All 21 baseline hashes and all five as-shot PNG hashes
match the accepted TCA checkpoint. Thirty-five WB previews were generated and
their review links verified. Edited WB has since received visual acceptance.

Additional user-supplied JPEG fixtures are recorded in
[the design document](../../wide_gammut_wip.md#additional-wide-gamut-jpeg-corpus).
Their embedded profiles have now been verified and exercised by the separate
profiled-JPEG checkpoint below. They are not inputs for the RAW scene path.

## Camera calibration checkpoint (opt-in)

Use `--compare-camera-calibration` with the five RAWs and TIFF regression input.
It writes `camera-calibration-review.html` and ten previews per RAW: zero, six
individual primary controls (hue +40, saturation -40), Shadow Tint -50/+50, and
a combined 3200 K WB / primary calibration / Shadow Tint recipe. These are
demonstration settings. The zero image must match the accepted WB as-shot output.

The six primaries use the app's existing current-model on-ramp calibration matrix,
including white normalization. Its linear-sRGB matrix is expressed in ProPhoto
and composed after WB, before shoulder/ACR3. Zero calibration adds no matrix or
storage round trip. Legacy/non-RAW calibration gain models are not reinterpreted.

Shadow Tint is a distinct late rendered-image operation. Its shared extracted
kernel retains the existing encoded-sRGB/Rec.601 curve and asymmetric darkening.
Only its decoded linear correction delta is returned to the Rec.2020 frame, so
the entire working image is not clipped to the operation's bounded SDR measuring
domain. In the complete chain its hook belongs after the applicable HSL/point
color/deferred curve/grading-or-restore block and before channel mixer/gradient
map/preset/LUT. The comparison recipes have no intervening tools, so those are
identities here; this does not mean the full editing chain has migrated.

Required references cover all six primary controls at +/-100, WB/calibration
ordering, HDR/alpha, zero identity, invalid contracts, both tint directions and
out-of-range working values. Eight accepted pre-migration legacy output hashes
also guard the shared extraction on Windows x64; see
[fixture provenance](Fixtures/README.md). Other platforms skip only that exact
legacy hash fixture, not the numerical references. Calibration remains CPU-only
and isolated from normal editing; edited previews have received visual acceptance.

Initial five-RAW-plus-TIFF run: all required checks pass (100 PASS, two known
optional direct CPU-F16 failures, five skips); CPU fallback/GPU transport gates
pass. All 21 baseline hashes, eight captured legacy editor hashes and five zero-
calibration preview hashes are unchanged. All 45 edited previews differ from zero;
50 images were generated and review links verified. Visual acceptance has since
been received.


## Embedded-ICC wide-gamut JPEG input (opt-in)

This checkpoint consumes already-rendered photographs. It cannot be combined
with RAW-scene comparison flags and never applies capture WB or the RAW tone
curve. Normal editor decoding is unchanged.

```powershell
$probeArgs = @('--compare-profiled-jpeg', '--output', "$env:TEMP/FerrumPix-ProfiledJpeg")
Get-ChildItem 'C:/Users/dsteiner/Pictures/raws/widegammut' -File |
    Sort-Object Name | ForEach-Object { $probeArgs += @('--image', $_.FullName) }
dotnet tools/ImagingPrecisionProbe/bin/Release/net10.0/win-x64/ImagingPrecisionProbe.dll @probeArgs
```

Open `profiled-jpeg-review.html`: existing color-managed sRGB8 on the left,
embedded-ICC-to-linear-Rec.2020-F16-to-diagnostic-sRGB8 on the right. Inspect
flowers/grass/sunset colors and dark gradients for new casts or artifacts. Both
images are SDR sRGB, so appearance should be close. The numerical report proves
retained source gamut before the explicitly clipped SDR output.

Supported scope: RGB JPEG8 with embedded RGB/XYZ matrix profiles and analytic
identity/gamma or parametric ICC curves (types 0..4). Missing/malformed profiles,
LUTs, tabulated TRCs, CMYK/grayscale, EXIF-only profile declarations and other file
formats are rejected rather than silently substituted with sRGB. APP2 chunks
are validated and all eight EXIF orientations are lossless pixel permutations.

The decoder keeps intrinsic source-profile RGB8 codes, then evaluates exact
256-entry per-channel TRCs and a PCS-D50-to-linear-Rec.2020 matrix. It calculates
in Double, casts rows to F32 and explicitly packs F16. This avoids both native
ICC dark-pixel approximation discrepancies found during the iteration and the
known Skia CPU-F16 subnormal loss. Signed/above-one working values are retained.
The JPEG input implementation is CPU-only; GPU checks still cover transport.

The supplied profiles are ProPhoto RGB (flowers), Display P3 (Iceland), and Adobe
RGB (1998) (shoes). Their actual embedded descriptions/SHA256 are in `report.json`.
Every pixel is checked against independent raw ICC-tag matrix/TRC calculations;
synthetic checks cover extended gamut, subnormal shadows, exact gamma, orientations
and malformed profile contracts. Initial result: all required checks pass (67
PASS, two known optional failures, four skips); CPU fallback/GPU transport pass.
All 12 pre-change legacy hashes match. SDR maximum difference is one code for
each JPEG. The three pairs have received visual acceptance; broader non-RAW input and
production color-managed output remain separate migration work.


## Embedded-ICC RGB16 TIFF input (opt-in)

```powershell
dotnet tools/ImagingPrecisionProbe/bin/Release/net10.0/win-x64/ImagingPrecisionProbe.dll `
  --compare-profiled-tiff `
  --image 'C:/Users/dsteiner/Pictures/raws/Scan_Film_3600dpi.tif' `
  --output "$env:TEMP/FerrumPix-ProfiledTiff16"
```

Open `profiled-tiff-review.html`. Compare the full scan and 1:1 top-right details
for casts, banding, texture loss or new artifacts. Left uses the existing TIFF
rendering; right goes from original RGB16 + embedded ICC directly to rendered-
linear Rec.2020 F16, then through the diagnostic SDR boundary. SDR appearances
should remain close. These PNGs cannot demonstrate 16-bit precision themselves;
the reference/known-word checks exercise values before output quantization.

The isolated decoder uses LibTiff.NET scanlines without its RGBA8 conversion or
an intermediate sRGB16 image. Supported files are one-page stripped, contiguous,
unsigned RGB16 without alpha, with embedded RGB/XYZ matrix/TRC profiles. Analytic
and tabulated ICC curves are evaluated for every possible 16-bit input code.
A2B/B2A profiles, untagged sources and other layouts are rejected rather than
silently sent through the old preview. LibTiff can normalize malformed tags;
this is validation of its parsed layout, not raw-directory forensic validation.

All eight orientations are exact pixel permutations. Known RGB16 words test
little/big endian, uncompressed/LZW/Deflate strips, distinguishable nearby codes,
subnormal shadows and extended gamut. Unsupported-layout and malformed-table
checks run on every probe invocation; generated fixtures stay in the output's
`tiff16-fixtures` directory. Existing JPEG profile calculations were extracted
into `MatrixRgbProfile`; the accepted JPEG output hashes must remain identical.

Initial film-scan result: 60 PASS, two known optional CPU-F16 failures, four skips;
CPU fallback/GPU transport pass. Every pixel matches an independent ICC matrix/
TRC reference; SDR maximum deviation one code. Six legacy baseline hashes match.
The three-JPEG regression yields 70 PASS with the same optional failures/skips;
12 baseline hashes and three accepted F16 preview hashes are unchanged. TIFF
visual acceptance has since been received. Normal editor decoding is unchanged, and this
source conversion is CPU-only. See `wide_gammut_wip.md` for rationale and remaining
layouts, profile policies, production integration and output work.


## Rendered Contrast checkpoint (opt-in)

`--compare-rendered-contrast` accepts a mixed list of sensor RAWs, supported
profiled JPEGs and RGB16 TIFFs. Run it separately from the other comparison flags.
For example, from the repository root:

```powershell
$sourceFolder = 'C:/Users/dsteiner/Pictures/raws'
$names = @('0M8A0758.CR2', 'IMG_0002.CR2', 'IMG_0008.CR2', 'IMG_0018.CR2', 'IMG_0113.CR2',
           'widegammut/Flowers-ProPhoto.jpg', 'widegammut/Iceland-P3.jpg',
           'widegammut/Shoes-AdobeRGB.jpg', 'Scan_Film_3600dpi.tif')
$probeArgs = @('--compare-rendered-contrast', '--output', "$env:TEMP/FerrumPix-RenderedContrast")
foreach ($name in $names) { $probeArgs += @('--image', (Join-Path $sourceFolder $name)) }
dotnet tools/ImagingPrecisionProbe/bin/Release/net10.0/win-x64/ImagingPrecisionProbe.dll @probeArgs
```

Open `rendered-contrast-review.html`. Top row: new -40 / neutral / +40. Second
row: old sRGB8 -40 / same neutral / +40. Inspect skin, dark texture, gradients and
saturated flowers/sky/grass. Old references start from the SAME developed image;
their sRGB8 boundary is required by the old renderer and can discard reserves
that the F16 input retains. SDR previews need not be byte-identical after edits.
Zero previews must match the accepted source/rendering checkpoints.

Only Contrast is active. RAWs first use the accepted lens/TCA, highlight repair
and scene development path at neutral WB/primary calibration. JPEG/TIFF first
use their actual embedded ICC profiles. The contrast adapter runs on rendered-
linear Rec.2020 F16, measures its existing artistic effect in bounded encoded
sRGB F32 and returns only the decoded linear effect delta to the original frame.
No RGB8 image is used to apply the new effect; there is no blanket working-frame
clipping. Slider zero is exact; signed/HDR values, alpha and source ownership
have independent checks. This is the existing measured SDR look extended by an
explicit residual policy, not a new HDR-native contrast calibration.

The editor's 4,097-point measured table/minimum-slope correction and min/max RGB
hue rule are shared unchanged. Independent Double references check +/-40/50/100;
a 4,097-step neutral ramp checks monotonicity/neutrality. Contrast remains CPU-
only; GPU checks cover transport. Brightness, local highlights/shadows, white/
black controls, curves and masks remain separate. Future tone-zone controls must
combine lifts BEFORE the shared min/max application, rather than stack these
single-control adapters. Normal editing and saved recipe semantics are unchanged.


Initial nine-image result: all required checks pass (103 PASS, two known optional
CPU-F16 failures, four skips); CPU fallback/GPU transport pass. All 30 pre-change
legacy baseline hashes and nine accepted neutral previews match exactly. All 18
edits differ from neutral/opposite edits. Forty-five review images were generated
and all local links verified; edited visual acceptance has since been received. Old references
use the common diagnostic preview writer, so exact legacy-output regression is
established by the separate baseline hashes, not by their review PNG bytes.


## Brightness / rendered-file exposure / Contrast group (opt-in)

```powershell
$sourceFolder = 'C:/Users/dsteiner/Pictures/raws'
$names = @('0M8A0758.CR2', 'IMG_0018.CR2', 'widegammut/Flowers-ProPhoto.jpg',
           'widegammut/Iceland-P3.jpg', 'widegammut/Shoes-AdobeRGB.jpg', 'Scan_Film_3600dpi.tif')
$probeArgs = @('--compare-basic-tone', '--output', "$env:TEMP/FerrumPix-BasicTone")
foreach ($name in $names) { $probeArgs += @('--image', (Join-Path $sourceFolder $name)) }
dotnet tools/ImagingPrecisionProbe/bin/Release/net10.0/win-x64/ImagingPrecisionProbe.dll @probeArgs
```

Run separately from other comparison flags. `basic-tone-review.html` links the
neutral image and shows new/old pairs for brightness +/-30, exposure +/-1 EV and
combined -1 EV / brightness +20 / contrast +30. Inspect skin, shadow texture,
light gradients, saturation and shoulder behavior. A contrast-only +40 artifact
is also generated for byte-exact regression against the accepted Contrast set.

RAW exposure runs once through `RawSceneDevelopment.Render` BEFORE its curve;
the rendered group gets EV zero. JPEG/TIFF exposure instead uses the app's
existing rendered-file scalar gain and toe/shoulder policy, limited to +/-4 EV.
It is legacy-look compatibility on rendered inputs, not an HDR-native exposure
operator. The frame domain does not encode RAW provenance; future editor routing
must preserve this explicit distinction. The legacy references start from the
same developed input, so RAW EV is already applied on both sides and is not
repeated inside the old reference's rendered chain.

Brightness and rendered exposure use the shared F32 scalar table before the
shared Contrast min/max rule, in one rendered-linear Rec.2020 F16 pass. The
bounded encoded-sRGB measurement and final linear correction-delta policy remain
explicit. Original signed/HDR reserves and alpha survive; a neutral group is
exact. The accepted Contrast entry point now delegates to this common stage.
Independent Double references cover scalar curves, toe/shoulder, processing
order, combined settings, slider extremes, signed/HDR/alpha and neutral ramps.

The implementation is CPU-only and isolated from normal editing. Highlights,
shadows, whites/blacks, saturation/vibrance, curves, masks and GPU implementations
remain separate work. Tonal zone interactions/statistics/local layers must be
migrated as a coherent group, not by stacking independently bounded adapters.


Initial six-image run: 90 PASS, two unchanged optional direct CPU-F16 failures,
four skips; CPU fallback/GPU transport pass. All 21 corresponding legacy baseline
hashes, six neutral previews and six accepted Contrast-only +40 previews match
`c95ef39f`. All 30 new edits differ from zero; 72 review images and 127 verified
local links. Edited appearance has since received acceptance. This bounded corpus contains
two RAWs, three profiled JPEGs and one RGB16 scan; it is not a repeat of the full
17-RAW corpus. See `wide_gammut_wip.md` for exact scope and remaining controls.


## Joint local/global tone and saturation/vibrance group (opt-in)

Use the six-image command from the previous checkpoint, replacing
`--compare-basic-tone` with `--compare-full-basic` and choosing a fresh output
folder. The source corpus is two representative RAWs, ProPhoto/P3/Adobe RGB JPEGs
and the RGB16 film scan. The full 17-RAW campaign is still a later integration gate.

`full-basic-review.html` shows 16 full-resolution recipes per source: neutral;
highlights +/-60; shadows +/-60; whites +/-50; blacks +/-50; saturation +/-50;
vibrance +/-60; saturation -100 + vibrance +100; a joint tone recipe; and all basic
controls together. Four old sRGB8 references show highlights down, shadows up,
joint tones and all controls. Inspect skin, neutral gray, saturated colors,
highlight/shadow texture and halos around strong edges. Gray+vibrance should be
neutral even on wide-gamut sources. All review images are diagnostic SDR sRGB.

The new API is `RenderedBasicControls` with immutable `RenderedBasicSettings`.
Processing order: native linear Rec.2020 saturation -> bounded encoded-sRGB
adapter -> scalar exposure/brightness -> joint local
highlights/shadows and pixel tone zones -> gated shadow-color restore -> vibrance.
The old measured curves/interactions, histogram shapes, fractional-radius local
filter, detail/chroma gains and late restore/vibrance kernels are reused. Global
statistics and the local grid read straight encoded-sRGB F32, not an RGB8 image.
The calibrated 1001 L* bins and sparse/jittered sampling policy are retained.

Saturation now uses native linear Rec.2020 luminance (0.2627/0.6780/0.0593),
scaling color differences from Y by `1 + saturation / 100` without clipping.
It intentionally changes the new path's saturation look, including sRGB inputs;
the legacy editor remains unchanged. The former signed-gamma residual extension
made positive-luminance wide colors fold through black. Tone/vibrance now return
their bounded sRGB linear delta to the saturated baseline, with the old saturation
matrix disabled in that adapter. At -100, maximum vibrance must stay gray and
smooth across gamut boundaries. Signed/HDR/alpha handling remains explicit.

The earlier `RenderedBasicTone` / `RenderedContrast` subset entry points are
unchanged. The new complete group additionally includes the existing late
shadow-color restore, absent from the scalar-only subset. Lifted dark colors can
therefore differ from that earlier subset while matching the full old basic chain.
Future curves/mixer/HSL migration must retain the original intervening order.
RAW exposure remains before scene rendering; already-applied EV only informs
local-detail calibration, and is subtracted from remaining scalar EV.

Numerical checks include sixteen frozen pre-extraction legacy hashes, exact-code
floating-vs-legacy pixel comparisons, exact local grids, independent saturation/
vibrance math, native Rec.2020 Y/gain and 49 wide-primary ramps, whole-frame monochrome, sub-RGB8 grid detail,
sparse statistics, signed/HDR/alpha, ownership and invalid settings. See
[fixture provenance](Fixtures/README.md). This stage is CPU-only and allocates an
additional full encoded RGBA F32 analysis buffer; production memory optimization,
GPU algorithms, masks, geometry, curves/mixer/LUT/extensions and display/export
integration remain separate work.

The planned Settings **Pipeline** category between Editor and Raw development,
with an optional new-pipeline switch and possible coherent partial bundles, is
recorded in `wide_gammut_wip.md`. No production switch/routing is implemented yet.


For the newly supplied transparent profile cubes, run `--compare-full-basic`
with the three PNGs under `C:/Users/dsteiner/Pictures/raws/cube`. The probe-only
`ProfiledPngFixture` reader retains raw ICC bytes, unpremultiplied RGBA8 and alpha;
every input pixel is checked against an independent matrix/TRC reference. It does
not add general PNG support to the production F16 source path. The three decoded
pixel payloads are identical with ROMM/2020/sRGB profiles assigned, so their
appearances are expected to differ.

The corpus exposed extended RGB>alpha values in two old color-managed previews.
The bounded SDR roundtrip diagnostic now records that original comparison as
inapplicable and tests fidelity against a canonical SDR reference at the same
two-code limit. Original legacy bytes are unchanged; range clipping is not
misreported as F16 precision loss. Native profiled fixture input has a separate
ICC/alpha reference. See the design document for the exact 67/63 observation.


Checkpoint results: main photographs 95 PASS / two known optional failures /
four skips; cubes 84 PASS / two known optional failures / six skips. CPU fallback
and GPU transport gates pass. Two cube skips explicitly concern original legacy
extended RGB>alpha values; canonical SDR and native-profile ICC/alpha checks
remain required and pass. All 21 photographic legacy hashes and six neutral
previews match `9930ef60`. There are 120 photographic + 60 cube review images;
all local links and full-desaturation neutrality are checked. Edited visual
acceptance is pending. The extra range-normalization test is covered by the final
harness; the photograph set was generated before that diagnostic-only addition.
