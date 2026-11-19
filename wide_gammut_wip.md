# Wide-gamut F16 / Rec.2020 migration — work in progress

## Current position

**The foundation and an experimental RAW-to-preview path are implemented. The
normal editor, its complete tool chain, display path and exports have not switched
to the new contract. Most production integration work remains.**

Latest implementation checkpoint: **editable anchored RAW white balance** (the
commit containing this update), following TCA commit `f349a888`, on `wb_16bit`. All migration commits
so far are local; nothing has been pushed as part of this work.

The initial scene-rendering previews, highlight color-repair comparison and RAW
exposure/black-point, lens vignetting and TCA comparisons have received visual
acceptance. **Editable WB is the current iteration and awaits visual acceptance.**
Passing numerical tests and accepting the appearance remain separate gates.

There is deliberately no percentage based on commit count. A working diagnostic
path is a significant foundation, but migrating the many existing tools and
connecting preview/export consistently is the larger remaining task. This is an
early implementation, not a nearly completed editor conversion.

### Completed in the experimental path

Here, **completed** means implemented and validated in the isolated diagnostic
path. It does not mean connected to normal editing or available on every GPU.

| Completed work | Why it was done | Acceptance / limitation |
|---|---|---|
| Baseline and precision probe | Detect regressions and backend precision loss before changing production behavior | Required checks pass; known optional failures remain visible |
| Immutable F16 / linear Rec.2020 contract and named boundaries | Preserve signed/HDR values and make primaries, encoding, alpha and ownership explicit | Numerical and developed-image round-trip checks pass |
| Explicit-half CPU fallback and native F16 GPU transport | Avoid measured Skia CPU subnormal loss and F32 GPU upload/rendertarget limitations | Validated on the measured Windows backend; not a port of all algorithms to GPU |
| Full-resolution RAW scene input | Avoid developing through RGB8 before entering the new frame | Signed/HDR scene values and exposure checks pass; LibRaw's existing RGB16 cache remains |
| RAW exposure, toe, shoulder and tone rendering | Preserve the app's black-point/exposure policy while eliminating integer tone-output rounding | Visually accepted |
| Highlight color repair | Restore the existing calibrated repair before exposure without wrong-primary thresholds | Visually accepted; inherited heuristic limits remain |
| Lens vignetting / stage-3 gain maps / TCA | Preserve camera-plane gains and calibrated linear-sRGB sampling before working-space conversion | Lens/TCA comparisons visually accepted; real DNG map photographs still missing |

### Current slice: implemented WB, visual acceptance pending

Current anchored RAW WB supports as-shot identity, absolute Kelvin/tint and
relative temperature/tint using the existing control and camera-adaptation
builders. It is evaluated directly on the pre-curve ramp. This removes the old
developed-RGB8/inverse-curve detour and its artificial headroom ceiling. Details
and evidence are below. This is **not yet a migration of legacy WB models,
camera calibration, the eyedropper or the editor's WB UI wiring**.

WB numerical/photographic checks have passed: 97 PASS, two known optional direct
CPU-F16 failures and five skips; selected CPU fallback and existing GPU transport
gates pass. All 21 baseline hashes and all five as-shot PNG hashes match the
preceding accepted checkpoint. Thirty-five WB previews were generated and their
review links verified. **Only visual acceptance of edited WB is pending here;
the old optional CPU-F16 failures are unchanged, not new WB failures.**

### Still to be implemented or integrated

| Remaining work | Why it is still needed |
|---|---|
| Camera calibration, legacy WB semantics and eyedropper | Preserve established recipe appearance and sampling behavior; current WB supports only the current anchored RAW model |
| Geometry/crops and model denoise | Complete RAW preparation without wrong-domain interpolation or violating trained-model input contracts |
| The complete editing tool and mask chain | Prevent later RGB8 conversions/clamps from undoing the precision gained so far |
| Extension/LUT compatibility | Existing code can assume encoded sRGB and specific stage ordering; explicit contracts or adapters are required |
| Color-managed high-precision non-RAW decoding | A declared-sRGB bitmap adapter is not a complete ICC-aware file decoder |
| Production monitor conversion, output gamut/tone policy and export | Diagnostic SDR PNGs are not production color management or HDR presentation |
| Actual GPU implementations and other backends | Transport compatibility alone does not validate tone, WB, lens or other processing shaders |
| Memory/throughput optimization, cache and editor integration | Current diagnostics make temporary full frames and are not a responsive production pipeline |
| Compatibility rollout and full regression campaign | Old recipes, thumbnails, preview and export need consistent, deliberate behavior |

The disabled camera shadow-saturation calibration experiment remains disabled.
Its future compatibility treatment is separate from migrating active corrections.

## How to maintain this document

**Update this file after every F16/Rec.2020 migration iteration**, including an
iteration that stops on a failure. Keep the current-position table, accepted and
pending gates, known limitations, remaining work and checkpoint history current.

Each iteration should record:

1. The concrete behavior implemented and why it belongs at that stage/domain.
2. The source files, checkpoint commit, and any intentional compatibility limits.
3. Which checks ran, their results, and what they do not establish.
4. What the user should inspect and whether visual acceptance is still pending.
5. The next bounded step, or the failure that prevents moving forward.

Implement one reviewable slice, run the relevant checks, stop and investigate
failures, and commit when its checks pass. Do not label an optional skip as a
pass, hide known diagnostic failures, or equate synthetic accuracy with visual
acceptance. Keep the original renderer available until the production rollout
gate is satisfied. Do not push unless separately requested.

The [probe guide](tools/ImagingPrecisionProbe/README.md) contains executable
commands and detailed output descriptions. This file is the design and progress
record; the probe guide is the operational reference.

## Goal and working contract

The goal is improved color fidelity: avoid early gamut clipping and repeated
8-bit quantization, keep scene headroom available to later operations, and make
color-space and encoding transitions explicit. Merely changing a bitmap format
does not achieve this if a later tool silently converts it back to RGB8.

The chosen working representation is:

| Property | Contract and reason |
|---|---|
| Native storage | **RGBA F16**, 8 bytes/pixel, including alpha. This is not packed Float3 or integer RGB16. RGBA fits the existing Skia surface/GPU model and supports masks/transparency. |
| Calculations | F32 or higher precision, then explicit rounding to F16 at storage boundaries. Current CPU matrix/exposure loops use Single; matrix construction and existing scalar curve helpers also use Double. F16 arithmetic is not the goal. |
| Primaries / white point | **Rec.2020 / D65**. This provides a substantially larger working gamut than sRGB, while retaining a D65 working white and a practical display-oriented RGB basis. It does not make every possible color representable. |
| Transfer function | **Linear**, without the Rec.2020 video transfer function or sRGB encoding applied to the stored values. Primaries and transfer function are separate properties. |
| Range | Finite signed RGB, including values above 1. Do not clamp the working frame to `[0,1]`. Operations that intentionally render or bound values must say so. |
| Alpha | Premultiplied in linear light, alpha in `[0,1]`. Alpha that rounds to zero canonicalizes RGB to zero. Color operations unpremultiply where required and restore premultiplication. |
| Meaning | `SceneLinear` and `RenderedLinear` are separate domains. Linearizing a developed sRGB image does not reconstruct scene radiometry or clipped highlights. |
| Ownership | Frames own immutable native storage and expose explicit reads/snapshots. New operations return owned results and must dispose temporary frames. |

F16 was chosen over integer16 because it preserves signed values and useful
above-white range. Integer16 confined to `[0,1]` would not preserve that headroom.
F16 has less uniform precision than integer16 and much less precision than F32;
it is a storage/bandwidth compromise, not lossless arithmetic. F32 everywhere
would increase memory/bandwidth requirements and is not a supported GPU render
target on the backend measured here.

ProPhoto remains a temporary tone-operation space to preserve the existing RAW
look. That is compatible with choosing Rec.2020 as the storage contract: there
is no need to replace every operation's mathematically required space with the
working primaries. These transitions must be explicit and should be combined
where possible, without inserting clipped or quantized intermediate images.

The working-frame metadata declares Rec.2020 and its domain explicitly; callers
must not infer them from an untagged native Skia bitmap. A production output must
eventually carry the correct profile/encoding metadata as well as correct values.

## Original path and why a separate migration path exists

The existing RAW decoder already maintains a full-resolution, demosaiced RGB16
cache. In the wide-tone path, the cache is normally camera RGB with a separate
camera-to-linear-sRGB matrix; an identity matrix handles an already converted
source. LibRaw capture white balance and configured sensor/highlight preparation
have already occurred. The cache also records normalization used to retain
highlight headroom in its unsigned16 storage.

The production RAW conversion subsequently combines corrections, the camera
matrix, tone rendering, clipping and gamma encoding. Normal previews chiefly
enter an encoded-sRGB BGRA8 editing chain. A deeper RGBA16 companion is available
for supported export operations, but that does not make the entire pipeline
floating point or wide gamut.

Changing this path in one operation would make decode, color, tone, GPU and tool
regressions hard to distinguish. The new services therefore exist inside the
application source tree but are invoked explicitly by a separate diagnostic
program. Normal editor processing, recipes and source sidecars remain unchanged.

This isolation is temporary. It reduces migration risk; it is not the final
architecture of a second independent editor renderer.

## Implemented path, in processing order

| Stage | Representation / space | Current implementation and purpose |
|---|---|---|
| 1. Existing LibRaw processing and cache | RGB16; linear camera RGB or linear sRGB according to the stored matrix | Reuse the existing decode and metadata; do not apply capture WB twice. This existing integer16 storage remains a precision boundary. |
| 2. Lens gains, TCA and scene import | RGB16 read -> optional F32 gains/bilinear sampling -> native RGBA F16, scene-linear Rec.2020 D65 | Apply radial/plane gains in their original basis; for TCA compute shifted linear-sRGB red/blue from sampled camera planes, then convert to Rec.2020. No RGB8 development as input. |
| 3. Optional highlight color repair | F16 scene read into F32 rows; temporary cache-relative **linear sRGB D65**; F16 scene result | Use the same production heuristic in its calibrated coordinates before user exposure. Transform only the repair delta back to Rec.2020. |
| 4. User exposure | F32 multiplication by `2^EV`; F16 scene result | Operate on scene values once, retaining signed/HDR values within F16 representability. |
| 5. RAW tone policy | Scalar settings derived from the existing `ToneRampFor` | Convert its old cache-relative black/white points into the coordinates of already normalized/exposed scene values. |
| 6. Explicit scene rendering and editable WB | Temporary **linear ProPhoto D50**, F32 channel calculations and existing scalar curve helpers | Apply black toe/exposure ramp, optional current-model WB adaptation, then shoulder/ACR3 using RGBTone interpolation. Bradford adaptation is included in the color-space matrices. |
| 7. Rendered working frame | Native RGBA F16, **rendered-linear Rec.2020 D65** | A deliberate domain transition. No blanket Rec.2020 RGB clamp at frame storage. |
| 8. Diagnostic output only | Linear sRGB conversion -> SDR gamut/range clipping -> sRGB encoding -> BGRA8 | Produce reviewable SDR images. This is not production monitor management, HDR output or a general tone mapper for arbitrary scene frames. |
| 9. Preview artifact | Encoded sRGB PNG, at most 2048 pixels on the long side | Resize the diagnostic SDR image for review. RAW scene operations and rendering occurred at full resolution beforehand. |

The original camera correction order is not fully represented by this table yet.
Lens gains/TCA now precede scene storage and highlight repair. Remaining geometry,
editable white balance and model stages still require their own order/domain
analysis rather than being appended wherever convenient.

### Lens vignetting and stage-3 gain maps

The optional source-import path now requests the same measured correction as the
app, forcing only vignetting on for the diagnostic comparison. It does not modify
preferences or mutate the RGB16 cache. Radius normalization, corner scaling,
polynomial/knotted falloff and strength reuse `LensDataService`. Radial falloff is
bounded at 0.125 before inversion, preserving the existing 3 EV radial gain cap.
Stage-3 gains reuse the existing sampler and operate on camera planes before
the matrix; their additional gain is not limited by the radial cap, as before.
No intermediate integer rounding/clipping is introduced.

The five RAWs plus TIFF passed all required checks: 93 PASS, two known optional
direct CPU-F16 failures, seven skips. Measured vignetting was available for
`IMG_0002.CR2`, `IMG_0008.CR2` and `IMG_0018.CR2`; the other two RAWs were explicit
profile skips with identical output. All 21 baseline hashes and ten accepted
lens-off preview hashes remained identical. Independent synthetic checks cover
radius/gain/cap, HDR, camera-plane ordering, zero strength and cache ownership.
Actual DNG gain-map photographs are not in this corpus and remain a coverage gap.
These lens comparisons have now received user visual acceptance.

The camera shadow-saturation function was inspected before choosing this step.
It is a disabled calibration experiment (`ShadowSaturationEnabled = False`) whose
comments report worse reference matching when enabled. It was not activated or
migrated as an active correction. Preserving it is a later compatibility decision.

### Transverse chromatic aberration (TCA)

TCA is now optional at the same source-import boundary. It reuses measured profile
factors and the app's manual fine-adjustment model, multiplying the pixel offset
from center by the factor, rather than dividing. TCA's radius normalization is
distinct from the corner-normalized radius for vignetting. Using the vignette
axis for both would change the correction's calibration.

The existing profiles are applied to **linear-sRGB red/blue**, even when the cache
is camera RGB. At a shifted red or blue position, the importer bilinearly reads
all three camera channels, applies per-plane/radial gains and evaluates the
corresponding camera-to-sRGB matrix row. The unshifted destination supplies green.
Those three scalar values then convert to Rec.2020; there is no stored/clipped
sRGB intermediate. Shifting Rec.2020 red/blue or just the camera red/blue channels
would be a different algorithm and can increase fringes.

The existing edge-clamping convention is retained. Interpolated samples are no
longer rounded back to integer16 before the matrix. Unity factors bypass sampling
to preserve exact existing F16 values. Gain maps are evaluated at the destination,
matching the original decoder's approximation, including when sampling shifted
camera positions. No full-frame F32 source copy or new geometry transform is used.

Independent synthetic references cover radial factors, unequal TCA/vignette
scales, direction, border sampling, camera mixing, combined gain maps, HDR and
unity identity. The review page contains automatic correction pairs plus top-right
1024-pixel 1:1 details. A separate red +100 / blue -100 demonstration tests manual
controls without calling it a measured profile or a recommended photo setting.
Real DNG gain maps, more cameras/lenses and GPU execution remain coverage gaps.

The five-RAW-plus-TIFF TCA run passed all required checks: 99 PASS, the two known
optional direct CPU-F16 failures, and seven skips. CPU fallback/GPU compatibility
gates passed. Automatic TCA matched the same three Canon lens records; two RAWs
were explicit missing-profile skips. Manual controls produced changed output in
all five RAWs. All 21 production baseline hashes and ten TCA-off previews matched
the preceding vignetting iteration exactly. Forty previews/details were written,
and the review page links were checked. These TCA comparisons have now received
user visual acceptance.

### Image contract and explicit boundaries

[LinearWorkingFrame](Services/LinearWorkingFrame.vb) and
[HalfPixelStorage](Services/HalfPixelStorage.vb) define ownership, range, alpha,
domain and exact native half conversion.
[WorkingImageBoundary](Services/WorkingImageBoundary.vb) supplies named
declared-sRGB input and diagnostic SDR output boundaries.

The input adapter accepts BGRA8/RGBA8/RGBA16, rejects a conflicting profile, and
handles encoded premultiplied alpha by unpremultiplying before decoding sRGB,
then premultiplying in linear light. Untagged data must be declared sRGB by its
caller; the adapter cannot infer the real color space of arbitrary untagged files.
These developed inputs become `RenderedLinear`, never `SceneLinear`.

The output adapter refuses a scene frame. A scene must first pass through an
explicit rendering operation. Transforming to sRGB before final clipping also
avoids corrupting colors by clipping negative Rec.2020 channels prematurely.

### RAW source and exposure

[RawDecodeServiceScene](Services/RawDecodeServiceScene.vb) snapshots immutable
cache references under the existing cache lock and verifies that they still
belong to the requested source/configuration. It carries normalization and
reference camera base exposure separately. Base exposure is not baked into the
imported frame, and missing correction stages are reported explicitly.

[SceneFrameOperations](Services/SceneFrameOperations.vb) applies exposure to
scene RGB, preserves alpha and rejects invalid settings or F16 overflow. Exposure
is not simulated by reversing a developed image's tone curve.

### CPU and GPU precision decisions

Testing found two important implementation constraints:

* Skia's direct CPU F16 raster route loses tiny subnormal shadow values. Native
  half-bit inspection showed that the values were already zero in storage, not
  merely lost during inspection. The tests were not weakened to accept this.
* On the measured Windows AMD/ANGLE D3D11 backend, F32 render targets are not
  supported, and an F32 GPU source upload can silently lose extended range.
  Native F16 GPU inputs preserve the tested range and precision.

The selected CPU precision fallback explicitly unpacks half storage into F32,
processes at F32 or higher precision, and explicitly packs finite results back.
The prototype F32 shader staging path still allocates a full F32 bitmap; it is a
correctness fallback, not an acceptable final memory design by default. New RAW
CPU operations use row-sized scratch, although repeated row allocations and
multiple full F16 frames still need optimization.

Tests cover all 63,488 finite half bit patterns, tiny shadows including the
smallest positive half value, signed/HDR data, alpha, exposure, matrix operations
and repeated storage. Native F16 GPU transport, sampling and blending passed on
the measured backend. **The new CPU RAW tone/repair/coordinator code has not been
ported to GPU.** Transport validation is not proof of GPU shader equivalence.

The two direct CPU-F16 diagnostic failures remain visible as optional failures.
The explicitly selected CPU fallback must pass. Unsupported optional GPU F32
targets are skipped, not counted as successful F32 support. Other GPU backends
and platforms remain unvalidated.

### Editable RAW white balance: what changed and why

[RawWhiteBalanceSettings](Services/RawWhiteBalanceSettings.vb) builds an immutable
snapshot of **only** the current anchored RAW WB controls. It reuses the existing
`BuildWhiteBalanceMatrix`, capture-anchor/camera selection and claimed-white-point
functions, so absolute Kelvin, tint-only edits, relative temperature/tint and
absolute-control precedence keep their established meanings. The current model
is model 4. Missing capture anchors, non-finite settings, old models and film
negative recipes are rejected by this experimental API instead of being silently
interpreted as the new model. The production handling of those recipes is unchanged.

LibRaw's capture WB is already in the RAW cache. The editable operation therefore
adapts from the user-claimed illuminant back to the retained capture anchor; it
does not apply capture WB again. As-shot uses the exact capture xy coordinates,
not a rounded Kelvin/tint reconstruction. The common builder's identity decision
is retained, and an identity WB takes the original rendering branch exactly.

The existing model-4 editor path starts with developed, clipped/quantized sRGB,
approximately inverts ACR3 and the highlight shoulder, adapts on that recovered
ramp, and reapplies the curve. Its inverse lookup path limits available ramp
headroom to four times white. The new path already has the source values and can
perform WB directly at the intended stage:

**lens-corrected scene -> highlight color repair -> user exposure -> black toe /
exposure ramp -> WB -> shoulder / ACR3 -> rendered-linear Rec.2020**.

The WB matrix is expressed in linear ProPhoto using `P * M * P^-1`, as in the
current model-4 code. The soft toe is first applied by the RGB-extremes rule;
then WB acts on those ramp values. It is not incorrectly applied before black
subtraction. The subsequent RGB-extremes mapping applies shoulder/ACR3. Keeping
WB inside rendering avoids another stored F16 intermediate, gamma encode/decode,
inverse lookup and artificial four-times-white ceiling.

Non-identity WB is intentionally not byte-identical to the old RGB8/inverse-LUT
route, particularly in highlights whose information that route had already lost.
The control model is shared; the higher-precision evaluation path is new. Negative
adapted ProPhoto intensities are bounded at this named SDR-rendering boundary,
while above-white ramp values continue into the shoulder. This is not HDR output.

This iteration does **not** include camera calibration (which the old chain can
combine with WB), old WB model recipe compatibility, film-negative ordering,
eyedropper sampling/UI integration, or GPU WB shaders. Those are explicitly
remaining tasks. `RawSceneDevelopment` still receives explicit RAW exposure and
highlight controls; a WB settings object does not make it a full editor recipe API.

### Tone rendering and temporary ProPhoto

[SceneFrameRenderer](Services/SceneFrameRenderer.vb) makes the scene-to-rendered
transition explicit. The initial migration retains the current RAW tone approach
so a change in look is not confused with a change in storage or primaries.

Combined Rec.2020-to-ProPhoto and return matrices incorporate the existing
D65/D50 Bradford adaptation. There is no stored sRGB intermediate. The curve
uses the current soft black toe, asymptotic highlight shoulder and 129-point
ACR3 look definition, without rounding its output through the old integer16
tone table. RGBTone maps the brightest and darkest channels, then interpolates
the middle channel proportionally, retaining the existing channel relationship.

Negative ProPhoto intensities are bounded intentionally at this SDR rendering
operation, not when importing or storing scene data. The resulting Rec.2020
frame is not globally clamped to `[0,1]`. This checkpoint produces an SDR
rendering of a scene that can contain HDR; it does not implement HDR presentation.

### Highlight color repair

[RawDecodeServiceSceneHighlights](Services/RawDecodeServiceSceneHighlights.vb)
calls the same `RecoverMagentaHighlightFloat` implementation as the production
RAW decoder. Its algorithm and the existing production call were not changed.

Its brightness/green-deficit conditions are calibrated in **linear sRGB at the
cache's normalization**, before user exposure. The adapter therefore transforms
Rec.2020 to linear sRGB and divides out recorded normalization for the repair,
then restores only the correction delta to the working frame. Applying the
heuristic directly to Rec.2020 values or after -2 EV would detect different
pixels. Transforming only the delta keeps unaffected F16 pixels bit-identical.

The original heuristic deliberately moves over-range red/blue toward cache white
where it repairs magenta. This is not reconstruction of missing sensor detail,
nor permission to clamp all scene HDR values.

The five-file comparison changed 2,133,644 pixels in `0M8A0758.CR2`, 67,440 in
`IMG_0002.CR2`, 12,012 in `IMG_0008.CR2`, 626,373 in `IMG_0018.CR2`, and none in
`IMG_0113.CR2`. The first file's strong pink window/reflections visibly improve.
Some pink remains around the lamp in `IMG_0002.CR2`. In `IMG_0113.CR2`, 130,083
pixels pass the original brightness gate but none satisfy its green-deficit
condition. Its unchanged result is a limit of the inherited heuristic, not a
failure to invoke the adapter. Further algorithm changes are a separate task.

### Matching the app's exposure / black-point policy

[RawSceneDevelopment](Services/RawSceneDevelopment.vb) now coordinates repair,
scene exposure and rendering. It accepts explicit RAW controls, not a full
editor recipe that might silently ignore unrelated settings.

The first renderer multiplied exposure into the scene but kept a fixed 0.003
rendering black point. The production decoder instead changes its tone ramp's
black/white coordinates along with exposure. Retaining a fixed black point made
the experimental negative-exposure shadows darker than that existing policy.

Let the old cache-relative ramp use black point B and exposure E, with
`S = normalization * 2^userEV`. Since the new input has already been multiplied
by S, equivalent rendering uses `B' = B*S` and `E' = E-log2(S)`. This is a change
of input coordinates, not a second exposure adjustment. For ordinary cache
normalization >= 1, the effective black point is
`0.003 * min(2^userEV, 1)`.

The coordinator reuses `ToneRampFor` as the policy source. Camera base exposure
is applied once. The existing highlight-recovery switch selects hard clipping
(knee 1) or the requested soft shoulder. It does not disable highlight color
repair. The later editor Highlights tool has not been migrated by doing this.

## Checkpoints and evidence

All output sets below are diagnostic artifacts outside the repository. See the
probe guide for commands. The original reference corpus has 17 sensor RAWs and
one rendered TIFF; the smaller iteration corpus uses the five reported RAWs,
with the TIFF added for the latest baseline run.

| Commit | What changed / why | Validation and acceptance |
|---|---|---|
| `bac42731` | Establish baseline images and CPU/GPU precision/capability probe before changing rendering | Exposed direct CPU-F16 shadow loss and backend limits; did not treat these as acceptable precision |
| `17410f55` | Explicit F16 storage around F32 CPU fallback | Finite-half transport and CPU/GPU exposure reference checks pass; direct-path failures remain visible |
| `b46c3779` | Immutable working frame and named sRGB boundaries | 18-file corpus, 57 baseline hashes unchanged; 19 round trips differ by at most one sRGB code value with exact alpha; visually accepted |
| `e6b1d653` | Full-resolution scene import and scene exposure | 17 RAWs retain signed/HDR values; every pixel checked at -2/+2 EV; all 57 baseline hashes unchanged; TIFF correctly excluded from scene import |
| `d060d228` | Explicit scene rendering and review previews | 51 scene previews, all required checks pass; all 57 baseline hashes unchanged; generally accepted appearance, with missing highlight repair identified |
| `d1f8b404` | Adapt existing highlight color repair and create on/off review page | Full-pixel independent references on five RAWs; 18 subset baseline hashes and 15 repair-off PNG hashes unchanged; CPU fallback/GPU gates pass; comparison visually accepted with known heuristic limits |
| `a89037bc` | Shared exposure/black-point policy and explicit RAW development coordinator | Five RAWs plus TIFF: 129 PASS, two known optional direct CPU-F16 failures, five skips; all required checks and CPU fallback/GPU gates pass. 21 baseline hashes and 15 accepted repair-on reference PNGs unchanged. All 10 neutral/+2 EV policy PNGs byte-identical; all five -2 EV PNGs changed as intended. Visually accepted |
| `f21540cf` | Apply measured lens/gain-map gains in the source channel basis before color conversion | All required checks pass; three real lens-profile matches, two explicit profile skips; 21 baseline and ten lens-off hashes unchanged. Visually accepted |
| `f349a888` | Shift calibrated linear-sRGB red/blue via floating-point camera-plane sampling; preserve vignetting and existing manual controls | 99 PASS, two known optional failures, seven skips; CPU fallback/GPU gates pass. 21 baseline and ten TCA-off hashes unchanged; three automatic profile matches, manual output changes on all five RAWs. Visually accepted |
| Editable WB (this commit) | Reuse current anchored control/camera adaptation directly on the pre-curve ramp; avoid inverse-LUT/clipped RGB8 detour | 97 PASS, two known optional failures, five skips; CPU fallback/GPU transport gates pass. 21 baseline hashes and five as-shot PNG hashes unchanged; 35 WB previews. Edited-WB visual acceptance pending |

The combined diagnostics have measured roughly 1.8–1.9 GiB peak working set in
earlier full-corpus runs. Those figures include decode, the old renderer, full
frames and PNG generation; they are not production memory targets or isolated
new-renderer benchmarks. No end-to-end speed improvement has been demonstrated.

Unchanged baseline hashes establish that the tested original rendering path did
not regress. They do not establish colorimetric accuracy of every camera, every
tool, every saved recipe, or an entire future floating-point chain. Independent
numerical references and visual comparisons address different parts of that risk.

## Remaining implementation and reasons for the order

### 1. Current visual gates

The lens/TCA reviews have been accepted. The current gate is
`raw-white-balance-review.html`: compare as-shot against the accepted TCA-on
neutral output, warm/cool Kelvin, tint-only and relative edits, and the combined
3200 K / -2 EV preview. Check neutral surfaces, skin, saturated colors and smooth
highlight transitions. Each iteration has its own commit and numerical gate.
Do not advance over an unresolved failure.

### 2. Complete RAW preparation and editable white balance in bounded slices

Inventory the actual existing correction order before migrating each item.
Prefer separate checkpoints for:

* **Lens vignetting and DNG gain maps — experimental implementation done.** These require linear light and, for
  per-plane gains, the original channel basis. Applying camera-channel gains to
  Rec.2020 channels would change their meaning. They now run before scene import;
  remaining work is real DNG coverage, more lenses and production integration.
* **Transverse chromatic aberration — experimental implementation done.** The existing calibration uses particular
  channel and coordinate conventions; moving interpolation into a different
  basis without preserving those conventions can increase fringes. Test real
  lens profiles and manual adjustments against the original corrected output.
* **Editable white balance — current anchored RAW model implemented.** Remaining
  work is camera calibration after WB, legacy model/recipe semantics, film-negative
  ordering, eyedropper behavior and editor/GPU integration. The new direct ramp
  evaluation needs visual acceptance, particularly in highlights; it is not a
  promise of parity with the old clipped/inverse-LUT intermediate.
* **Camera-specific shadow saturation.** Its existing luminance/color basis and
  position relative to rendering matter. A Rec.2020 luma substitution is a
  behavioral change, not a harmless coefficient update.
* **Model denoise.** Preserve the model's trained input range, channel space and
  normalization. New working primaries are not automatically valid model input.
* **Geometry and sensor-edge crop.** Interpolation, bounds and coordinate scaling
  must preserve scene/rendered meaning, alpha, signed/HDR values and alignment
  with masks. Match preview/export geometry before enabling production use.

This list identifies required work, not permission to reorder the existing
pipeline. Commit and review each small dependency-aware slice separately.

### 3. Migrate editing operations and their GPU equivalents

Inventory each tool's current input/output format, primaries, encoding, domain,
clamps and position. Migrate coherent groups with reference recipes: light/tone,
white-balance/color operations, channel mixing/grading, negative handling,
spatial detail/denoise/sharpen, vignette/grain and the remaining effects/masks.

An sRGB-calibrated artistic operation cannot always be rewritten by replacing
its luma coefficients or omitting its transfer function. Some operations need
temporary perceptual or legacy spaces to preserve their intended effect. Keep
those conversions explicit and avoid quantization/clipping at intermediate
boundaries. Compare existing recipes as well as synthetic colors and gradients.

Port the new tone/repair/tool stages to GPU using native F16 surfaces and
F32 shader calculations where supported. Compare CPU/GPU results with appropriate
error budgets, including subnormal shadows, alpha, HDR and saturated colors.
GPU transport checks alone are not sufficient. Preserve a validated CPU fallback
and do not require unsupported F32 GPU targets on the measured backend.

### 4. Preserve extension, LUT and preset compatibility

Existing extensions and LUTs can assume encoded sRGB, specific value ranges and
fixed stage ordering. Define explicit contracts/adapters or a versioned opt-in
for new semantics instead of silently feeding Rec.2020 scene values to old code.
Maintain extension priority/order, masks, saved parameters and CPU/GPU parity.
Some compatibility conversions will remain necessary; they are not all NOOPs.

### 5. Complete imported-file, display and export boundaries

High-bit-depth TIFF/other file input must retain its source precision and use its
actual embedded profile before entering the working space. The declared-sRGB
bitmap adapter is not a substitute for a complete color-managed file decoder.

Define production rendered-image/monitor conversion, gamut handling, final
transfer functions, alpha and quantization/dither policy. Keep clipping at named
output or deliberately bounded-operation stages. Do not label the diagnostic
sRGB PNG writer as monitor management or HDR display support.

Exports must use the same intended development/recipe as preview, with proper
output profile metadata and bit depth. JPEG8, TIFF16 and any floating/HDR output
need explicit range/encoding policies. An internal HDR-capable frame alone does
not provide HDR files or display support. Output gamut/tone-mapping choices remain
design decisions, not already implemented features.

### 6. Optimize memory, conversions and cache ownership

Once stage semantics are validated, fuse compatible point operations/matrices,
reuse bounded scratch buffers, consider tiling, and reduce unnecessary full-frame
copies and CPU/GPU transfers. A small-image timing does not establish large-RAW
responsiveness. Measure full-resolution preview/export, sustained slider updates,
peak native/managed/GPU memory and cancellation/lifetime behavior.

Many old encode/decode and RGB8 conversions can disappear when adjacent stages
share the new contract. Required primaries/white-point/perceptual conversions
and legacy extension adapters cannot simply be removed. Track actual eliminated
conversions and measured performance, rather than assuming a net speedup.

Review the multiple temporary F16 frames and per-row allocations in the current
prototype. Keep invalid/overflow handling explicit; never replace an error with
silent integer fallback or global clipping to make a check pass.

### 7. Connect the editor and roll out deliberately

Wire the validated stages into editor preview, full-resolution render, thumbnails,
export, caches and adjustment ownership without double-applying exposure/WB.
Establish a temporary selection/fallback mechanism and decide how old recipes
retain their appearance. Do not silently reinterpret preset parameters in a new
encoding/space. Persisted schema/render-version changes, if needed, require a
separate compatibility decision.

Run the full supplied corpus plus targeted cameras/lenses, saturated/out-of-gamut
colors, deep gradients, clipped highlights, alpha, HDR, extension recipes and
high-bit-depth non-RAW files. Validate available GPU backends/platforms and CPU
fallbacks. Only enable the new default after visual, numerical, performance and
preview/export consistency gates are satisfied.

## Definition of complete

The migration is complete when the production editor's intended high-precision
chain uses the documented working contract, its remaining format/space
boundaries are intentional, current tools/extensions and saved recipes have an
explicit compatibility strategy, preview and export agree, required CPU/GPU
fallbacks pass, and realistic memory/performance behavior is acceptable.

The present diagnostic services and accepted preview slices are evidence toward
that goal. They are not a declaration that the complete implementation is done.
