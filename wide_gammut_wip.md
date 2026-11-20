# Wide-gamut F16 / Rec.2020 migration — work in progress

## Current position

**The foundation and an experimental RAW-to-preview path are implemented. The
normal editor, its complete tool chain, display path and exports have not switched
to the new contract. Most production integration work remains.**

Latest implementation checkpoint: **native geometry, linear mask/normal blends
and the first actual GPU composite kernels**, on `wb_16bit`.
Migration commits remain local; no pushes. The automatic-film checkpoint was accepted.

The foundation, scene/RAW development, input profiles, basic/color controls,
HSL/point selection, rendered WB, negative inversion and automatic film measurement
have passed their recorded checks and visual gates. This iteration adds ordered
geometry in working light, fractional mask mixing, normal source-over, document
backgrounds and GPU kernels for the unmasked blends. Its visual gates are below.

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
| Editable anchored RAW WB | Apply current controls directly before the curve without RGB8/inverse-LUT loss or duplicate capture WB | Current-model RAW comparisons visually accepted; legacy/eyedropper remain separate |
| Current-model camera calibration | Preserve the six primary controls after WB and distinct late Shadow Tint response | Visually accepted; late tint now joins the prototype group; legacy/non-RAW models and editor/GPU integration remain |
| Profiled JPEG input | Preserve source gamut without developing through sRGB8 or applying a RAW tone curve twice | Matrix/analytic-TRC RGB JPEG8 subset; visually accepted |
| Profiled RGB16 TIFF input | Preserve native source codes and embedded tabulated curves before Rec.2020 conversion | Stripped contiguous unsigned RGB16 subset; visually accepted |
| Rendered Contrast | Preserve the measured artistic tone response without an RGB8 intermediate | Visually accepted; now delegates to the shared basic-tone stage |
| Brightness and rendered-file exposure | Preserve existing scalar/shoulder policy before Contrast without integer image staging | Visually accepted; RAW exposure remains before scene rendering |
| Joint local/global tone zones | Preserve histogram-dependent curves, local base, detail/chroma and min/max tone interactions together | CPU implementation and references pass; visually accepted |
| Saturation / vibrance | Native linear Rec.2020 saturation avoids signed-gamma gamut folds; keep later calibrated vibrance | Visually accepted; new saturation appearance is an explicit compatibility choice |
| Tone curves | Reuse master/channel ProPhoto-primary curves and HSL-lightness curve without an RGB8 intermediate or separate image pass | Numerical checks pass; continuation authorized; CPU-only adapter |
| Late color / creative point controls | Share grading/tint/mixer/map/palette kernels and preserve actual operation order; declare intentional range replacement | Required checks pass; visually accepted at `5f403cf7` |
| HSL bands and point color | Eliminate RGB8 selection-guide quantization while sharing calibrated gates/selection math and deferred curve order | `49214977`: required checks pass; visually accepted; CPU-only |
| Explicit film inversion / rendered WB | Normalize a film scan before WB/tone without an RGB8 image; preserve linear Bradford HDR range and legacy WB recipe semantics | Numerical: 84 PASS; cubes: 102 PASS; four rendered photos: 104 PASS. Visually accepted; legacy RAW routing remains |
| Floating automatic film calibration | Preserve sub-byte base/density percentiles and linear-light grain averaging without an RGB8 measurement image | Required checks pass; visually accepted; CPU-only |
| Native geometry | Keep sampling in linear Rec.2020 with premultiplied coverage, signed/HDR colors and exact integer permutations | Numerical checks and six-photo/three-cube captures pass; visual gate below; CPU-only |
| Fractional masks / normal blends / document backgrounds | Avoid an RGB8 blend boundary and distinguish adjustment-result interpolation from source-over | Independent CPU references pass; CPU galleries ready; existing mask editor/layer orchestration not connected |
| Actual GPU normal composite kernels | Test real source-over/unmasked result mixing with native F16 inputs and targets | 18624 channel comparisons pass on measured AMD backend; coverage stays CPU; most other effect kernels still pending |

### Current slice: basic controls, curves, HSL/point color and late color

[RenderedBasicControls](Services/RenderedBasicControls.vb) adds the requested
highlights/shadows, whites/blacks and saturation/vibrance as a coherent group,
with existing brightness/exposure/Contrast available in mixed recipes. Immutable
[RenderedBasicSettings](Services/RenderedBasicSettings.vb) consumes only these
controls; it cannot silently partially interpret a full editor recipe.
[RenderedCurveSettings](Services/RenderedCurveSettings.vb) adds an optional,
immutable snapshot of the five curves to that same pass. Omitting curves retains
the accepted basic-group API and behavior.
[RenderedLateColorSettings](Services/RenderedLateColorSettings.vb) adds immutable,
validated grading zones, channel rows, gradient swatches and creative-effect
settings. It cannot activate HSL bands, point color, presets or LUTs implicitly.

The stage order follows the editor, with saturation now native: **linear Rec.2020
saturation -> bounded encoded-sRGB adapter -> scalar exposure /
brightness -> joint local/global tone zones -> master RGB / channel curves
(ProPhoto primaries, sRGB transfer) -> HSL shadow-color restoration -> luminance
curve (HSL L) -> vibrance -> HSL bands / point color -> deferred RGB curves when
bands are active -> color grading -> calibration shadow tint -> channel
mixer -> gradient map -> posterize -> threshold**. Saturation belongs before tone, vibrance after it. RAW exposure still
belongs before scene rendering; already-applied EV is carried only as metadata
for the local detail calibration, and is subtracted from the scalar chain's EV.
With active HSL bands, master/channel RGB curves are deferred until after the
bands and point selections; otherwise they retain their earlier position.
The HSL-lightness curve always precedes vibrance/bands, and grading follows the
RGB-curve placement. These are conditional positions, not two curve applications.
The settings argument explicitly distinguishes that value from rendered-file EV.
Source provenance/routing must still be enforced by the eventual editor wrapper.

[ImageProcessorFloatingBasic](Services/ImageProcessorFloatingBasic.vb) replaces
the old byte input reader for analysis, reusing the existing `BuildPointOpChain`,
`PreMixerStages`, local-base smoothing/sampling, tone tables, detail/chroma gains,
white/black chroma correction and restore gates. Three small unchanged numerical
blocks (histogram summary, vibrance, bounded color matrix) were extracted so the
old/new paths share them. Sixteen frozen old-renderer hashes were captured from
accepted `9930ef60` **before** extraction and guard production behavior.

All analysis reads full-precision **straight, bounded encoded-sRGB RGBA F32**,
never an RGB8 proxy. Statistics retain the existing CIE L* 0.1 bins, jittered
roughly-250k sampling, visible bounds, median/99th percentile/bright-share and
sparse-image full-count fallback. These bins describe the calibrated response;
they are not the image's storage precision. Local cells alpha-weight the mean
encoded color after native saturation, run scalar stages, then derive linear-sRGB luminance
and re-encode gray. Existing fractional-radius guided/local-detail filtering and
coverage handling are reused. Averaging linear Rec.2020 instead would change the
calibrated local effect rather than simply improve precision.

Highlights/shadows use that local base plus structure/color terms. Contrast,
whites and blacks use the common min/max tone rule and measured chroma policy.
The local contribution is subtracted from the joint monotone table before being
added at the base: stacking six independent filters would lose these interactions.

Saturation now acts directly on linear Rec.2020 before any bounded projection:

```
Y = 0.2627 R + 0.6780 G + 0.0593 B
RGB' = Y + (1 + saturation / 100) * (RGB - Y)
```

This affine operation preserves CIE luminance, works on premultiplied channels,
keeps signed/HDR values and needs no intermediate F16 frame. Saturation -100
produces a true neutral. Its zero setting preserves the previous input exactly.
**This intentionally changes saturation's appearance even for sRGB inputs in the
new path.** The old encoded Rec.601 control remains unchanged in the legacy
editor; copying old saturation recipes between pipeline modes is therefore a
compatibility decision still to be made before integration.

Tone/vibrance then return a linear-sRGB effect delta to that saturated Rec.2020
baseline. Their analysis remains a bounded encoded-sRGB F32 projection. The old
saturation matrix is disabled in this adapter to prevent double application;
statistics and local bases now observe the already-saturated image. The default
legacy-oracle entry still exercises the original encoded matrix for regression
checks. No global clipping is added to the working frame.

Positive vibrance retains its 18 hue bins (skin protection) and linear-sRGB Y
preservation; negative vibrance keeps the measured exponential law. Statistics,
local gray and vibrance Y still use linear-sRGB Rec.709 weights. These bounded
artistic tone/vibrance operations are not yet native Rec.2020/HDR models.
Working storage remains premultiplied linear Rec.2020 D65 F16.

The existing `RenderedBasicTone`/`RenderedContrast` subset APIs remain unchanged
and tested. The new full group includes the old late shadow-color restore, which
was **not part of the earlier scalar-only subset**. It may therefore differ in
lifted dark colors even when the other newly introduced controls are neutral.
This completes a missing later operation rather than changing those accepted
subset entry points. RGB curves now run inside `PreMixerStages`; the HSL block
restores shadow color before the luminance curve and vibrance. HSL bands and point color now share the legacy color block; their guide uses F32. Color grading and the channel mixer remain in the same group. Their migration must preserve the
existing conditional deferred-RGB-curve path when a mixer is present.

Current limitations: CPU-only; full encoded F32 analysis buffer costs 16 bytes per
pixel in addition to source/output F16 and local grids. There is no production
editor routing, masks, geometry/ROI orchestration, LUT/extension
migration or HDR/display/export policy. The new group is not the whole pipeline.

### Still to be implemented or integrated

| Remaining work | Why it is still needed |
|---|---|
| Legacy anchored RAW WB / non-RAW primary calibration and eyedroppers | Generic rendered WB is implemented; capture/camera-anchor routing, older primary-response models and tool-position sampling still need integration |
| Negative preparation integration and RAW coordination | Floating statistics and inversion are implemented; crop-before-analysis orchestration, eyedroppers, persisted fractional calibration and RAW inversion/WB/exposure routing remain |
| Geometry integration, lens distortion/sensor crops and model denoise | Native ordered geometry exists; complete RAW preparation and preview/mask/ROI routing without wrong-domain interpolation or violating trained-model input contracts |
| Full-chain orchestration with HSL, point color, masks and remaining effects | Normal blends and F32 mask coverage exist; connect actual mask/layer editing, artistic blend modes and operation ordering without later RGB8 boundaries |
| Extension/LUT compatibility | Existing code can assume encoded sRGB and specific stage ordering; explicit contracts or adapters are required |
| Complete color-managed non-RAW decoding | Embedded matrix/TRC JPEG8 and stripped contiguous RGB16 TIFF are experimental; broader TIFF layouts, other formats, profile fallbacks and complex ICC profiles still need explicit contracts and validation |
| Production monitor conversion, output gamut/tone policy and export | Diagnostic SDR PNGs are not production color management or HDR presentation |
| Most GPU algorithms and other backends | Normal composite kernels now pass; tone, WB, lens, geometry, spatial effects, masks and other shaders still need algorithm-specific validation |
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
| 6. Explicit scene rendering, WB and primary calibration | Temporary **linear ProPhoto D50**, F32 channel calculations and existing scalar curve helpers | Apply black toe/exposure ramp, WB, then primary calibration, then shoulder/ACR3. Combined matrices retain the app's control model and Bradford color-space adaptation. |
| 7. Rendered working frame | Native RGBA F16, **rendered-linear Rec.2020 D65** | A deliberate domain transition. No blanket Rec.2020 RGB clamp at frame storage. |
| Rendered-file preparation branch (before 7a; not another RAW WB pass) | F16 linear Rec.2020 input/output. Optional encoded-sRGB F32 log inversion/legacy WB, or one fused Double linear Rec.2020 Bradford matrix | Profile import -> explicit negative normalization/optional monochrome -> rendered WB -> user basic/color group. Source alpha is unchanged. Capture-anchored current RAW WB stays at stage 6. |
| 7a. Full basic/curve group (isolated) | F16 rendered input/output; **straight encoded sRGB D65 RGBA F32** analysis; RGB curves use temporary **F32 ProPhoto primaries with sRGB transfer**; luminance/vibrance use encoded-sRGB HSL | Native linear Rec.2020 saturation -> bounded sRGB scalar exposure/brightness -> joint local/global tone zones -> master/channel curves in encoded ProPhoto -> HSL restore/luminance curve/vibrance -> grading -> calibration shadow tint -> channel mixer -> gradient map -> posterize -> threshold. Linear-sRGB Rec.709 analysis/Y. Preserving controls return a linear delta; mixer/map/palette follow the declared replacement-reserve policy. RAW EV stays at stage 4. |
| Alternative to inline Shadow Tint (older comparison API) | F16 rendered input/output; temporary **encoded sRGB / Rec.601** calculation | Retained for calibration-only comparisons. Do not stack it with the same tint in stage 7a; the eventual coordinator must route this control once in its original late position. |
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

The WB-only checkpoint did not include calibration; the following calibration
iteration now adds its current-model components as described below. Old WB model
recipe compatibility, film-negative ordering, eyedropper sampling/UI integration,
and GPU WB shaders remain tasks. `RawSceneDevelopment` still receives explicit RAW exposure and
highlight controls; a WB settings object does not make it a full editor recipe API.

### Camera calibration: two stages, not one matrix

[RawPrimaryCalibrationSettings](Services/RawPrimaryCalibrationSettings.vb)
supports the six primary controls for the current anchored RAW model. It reuses
the existing `BuildCalibrationMatrix` unchanged, including its measured on-ramp
hue/saturation gains, primary rotation model and column normalization that keeps
white neutral. Replacing those calibrated primaries with Rec.2020 primaries or
using the older non-ramp gain table would change the sliders' meaning.

The existing builder returns a linear-sRGB matrix. We express it in the same
linear ProPhoto ramp basis as WB and compose **calibration * WB**, so pixels see
WB first. Matrix order matters; tests deliberately include a noncommuting warm-WB
recipe. No additional F16 buffer or gamma boundary is introduced between them.
Zero primary calibration bypasses the new matrix, including when WB is edited.
Non-finite/out-of-range controls and unsupported legacy ramp contracts are rejected
by this experimental API rather than silently mapped to a different model.

[CalibrationShadowTint](Services/CalibrationShadowTint.vb) handles the seventh
control separately. The existing point chain evaluates it after its applicable
HSL/point-color/deferred-curve/grading-or-restore block, and before channel mixer,
gradient map and preset/LUT stages. It is **not** the disabled camera
`ShadowSaturationEnabled` experiment. Its nonlinear strength, Rec.601 encoded
brightness weights and asymmetric green/magenta darkening are retained.

The original CPU chain and new adapter call one extracted shadow-tint kernel;
its response/darkening parameters are prepared outside the pixel loop. Eight
synthetic editor hashes were captured from the pre-migration binary before the
shared code was rebuilt. They guard the unchanged legacy path; independent
double references guard the new F16 operations and control ordering.
The [fixture notes](tools/ImagingPrecisionProbe/Fixtures/README.md) describe the
synthetic input, recipes and pre-migration provenance. Byte-exact legacy hashes
are gated to Windows x64; numerical references still run on other platforms.

Shadow Tint's measured coordinates are **encoded sRGB**, so the adapter temporarily
converts a rendered Rec.2020 pixel to bounded sRGB for that operation. It does not
store RGB8 or an intermediate integer image. Only the decoded linear correction
delta is transformed back and added to the original frame. For legacy SDR colors
this follows the original operation; for out-of-range working values it preserves
the source's signed/wide/HDR base instead of globally clipping it. That extension
beyond the legacy SDR domain is an explicit compatibility policy and needs wider
gamut/output testing as those boundaries migrate. Zero tint is exact and alpha is
preserved. Scene-linear input is rejected: this is a rendered-image operation.

The comparisons apply this later adapter immediately after rendering only because
other point tools are not active in these recipes. **The full late-chain hook is
still to be wired when those intervening tools migrate.** The raw coordinator
does not silently consume or move Shadow Tint into its ramp. Current-model RAW
calibration is implemented experimentally; legacy/non-RAW calibration gain models,
full recipes, masks and GPU calibration are not complete. The three supplied
wide-gamut JPEGs now exercise the separate ICC-aware rendered input described
below; they are never mislabeled as anchored RAW scenes.

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

## Additional wide-gamut JPEG corpus

Three user-supplied files are available outside the repository at
`C:\Users\dsteiner\Pictures\raws\widegammut`:

* `Flowers-ProPhoto.jpg`
* `Iceland-P3.jpg`
* `Shoes-AdobeRGB.jpg`

The embedded profiles were inspected rather than inferred from those filenames:

| File | Dimensions | Embedded profile description | ICC size |
|---|---|---|---|
| Flowers-ProPhoto.jpg | 1200 x 795 | ProPhoto RGB | 940 bytes |
| Iceland-P3.jpg | 600 x 400 | Display P3 | 548 bytes |
| Shoes-AdobeRGB.jpg | 600 x 400 | Adobe RGB (1998) | 560 bytes |

[ProfiledJpegInput](Services/ProfiledJpegInput.vb) assembles and validates the
numbered JPEG APP2 ICC chunks, records the actual profile SHA256, and asks Skia
to decode **untagged source-profile BGRA8**. All eight EXIF orientations are exact
pixel permutations, avoiding filtering or color conversion. The profile's XYZ
colorants are already in ICC PCS D50. The target Rec.2020-to-PCS matrix includes
D65 adaptation; applying a second Bradford transform would introduce a cast.

Input TRCs are evaluated from the original ICC bytes, not the native CMM's
normalized transfer metadata or approximate pixel-transform powers. The supported
analytic curves are identity/gamma (`curv` with zero/one entry) and ICC parametric
functions 0..4. Each channel gets an exact 256-entry decode table. This tiny table
is lossless for decoded JPEG8 codes; it is not a coarse tone LUT for floating
working pixels. The matrix output is unclamped and explicitly half-packed.

An independent probe reference reads the original matrix/TRC tags itself, uses a
Double matrix inverse, and checks every real pixel before SDR output. It also
checks signed/out-of-Rec.2020 colors, subnormal shadows, an exact 563/256 gamma
ramp, alpha/domain, eight orientations and malformed ICC packets. SDR review PNGs
are deliberately converted/clipped at the existing diagnostic sRGB boundary;
they cannot display all the retained source gamut. No production monitor-profile
handling or new output gamut-mapping policy is claimed by this input iteration.

The three-JPEG run passes all required checks: **67 PASS**, two unchanged optional
Skia CPU-F16 failures, four skips; CPU fallback and GPU transport gates pass.
All **12 pre-change legacy baseline hashes** are unchanged. Every JPEG pixel
passes the independent ICC reference (0.06% relative / 1.2e-7 absolute tolerance
including F16 rounding); diagnostic SDR output differs from the legacy decoder
by at most **one 8-bit code**. Six paired review PNGs are linked from
`profiled-jpeg-review.html`. JPEG photographic visual acceptance has been received.

| Source | Rec.2020 F16 range | Pixels outside sRGB before output |
|---|---|---|
| ProPhoto flowers | -0.0205688 .. 1.1582 | 457,561 |
| P3 Iceland | -0.000183344 .. 1 | 22,799 |
| Adobe RGB shoes | 0 .. 1 | 57,841 |

The ProPhoto frame retains 4,721 negative and 3,792 above-one channels. These are
coordinate values in the target primaries, not proof of scene HDR in a JPEG.
The P3 negative values are small and follow the supplied ICC matrix; they are
preserved rather than clamped at input. These counts use the probe's 1e-4 sRGB
boundary tolerance, not a perceptual gamut-distance metric.

### RGB16 TIFF input design (accepted)

The film scan now has an isolated input path from its native RGB16 samples,
through its actual embedded matrix/TRC profile, directly into rendered-linear
Rec.2020 F16. It avoids both `ReadRGBAImage` (which yields RGB8) and the existing
deep reader's intermediate sRGB16 color conversion. Already-rendered scans do
not go through RAW exposure, capture WB or the scene development curve.

[MatrixRgbProfile](Services/MatrixRgbProfile.vb) extracts the accepted JPEG
matrix/curve calculation into a shared source-profile boundary. JPEG keeps its
analytic-only policy and 256-code tables. TIFF explicitly enables tabulated ICC
TRCs and builds 65,536 entries per channel (about 1.5 MiB of Double tables).
Each table evaluates every possible source code; interpolation follows the ICC
profile's table, rather than rounding the source image to RGB8. Matrix work
remains Double -> F32 -> explicit F16 storage. Signed/above-one output survives.

[ProfiledTiffInput](Services/ProfiledTiffInput.vb) uses the existing LibTiff.NET
dependency, reads compressed scanlines forward in native 16-bit precision, and
then applies exact pixel permutations for all eight orientations. A full RGB16
source buffer permits rotations without unsupported backward reads into LZW
strips. This is a correctness-first checkpoint, not a streaming/tiling or memory
optimization. The old TIFF decoder/editor path has not been edited.

The supported file subset is one-page, stripped, contiguous, unsigned RGB16,
without alpha, with an embedded RGB/XYZ matrix profile. Tiled/separate-plane,
gray/CMYK/Lab, floating-point/alpha, missing-profile and multi-page policies remain
separate work. Unsupported inputs fail explicitly. A2B/B2A LUT profiles remain
unsupported; JPEG does not silently gain TIFF's table support. All new processing
is CPU-only; existing GPU checks validate transport, not GPU TIFF conversion.


## RGB16 TIFF checkpoint and evidence

`Scan_Film_3600dpi.tif` is a one-page 3072 x 4650 unsigned RGB16, contiguous,
LZW-compressed scan, orientation 1. Its embedded profile is **sRGB IEC61966-2.1**,
3,144 bytes, SHA256 `2B3AA1645779A9E634744FAF9B01E9102B0C9B88FD6DECED7934DF86B949AF7E`.
Each channel TRC contains 1,024 unsigned-16 entries. The profile is read from the
file; the source's name and near-monochrome content do not establish its encoding.

Validation at this checkpoint:

* All **14,284,800 pixels** match the independent original-ICC reference within
  0.06% relative / 1.2e-7 absolute error including F16 rounding. The reference
  uses the same LibTiff decoder for real scan samples but separate profile math;
  generated fixtures use known original words to independently test the decoder.
* TIFF run: **60 PASS**, two unchanged optional Skia CPU-F16 failures, four skips;
  selected CPU fallback and existing GPU transport gates pass. Output range is
  0..1; diagnostic SDR maximum difference from the old TIFF renderer is one code.
* All **six pre-change baseline hashes** are unchanged. The full-image pair and
  top-right 1024-pixel 1:1 detail pair are in `profiled-tiff-review.html`.
* Known-word TIFF fixtures cover little/big endian, uncompressed/LZW/Deflate,
  linear wide-gamut primaries, signed/above-one target values and a one-code
  subnormal shadow. Source codes 129 and 130 remain distinct; RGB8 staging would
  collapse them. F16 still has its own finite precision: this is not a promise
  that every neighboring 16-bit code survives for every input brightness/profile.
* Independent permutations verify all eight orientations, including dimensions
  swapped for 5..8. This intentionally follows actual TIFF orientation semantics;
  the old deep reader's documented 5..8 flip-only behavior is not copied.
* Missing profiles, RGB8, signed/extra-channel/grayscale/tiled/separate-plane and
  multi-page sources are rejected. Truncated/non-monotonic TRC tables are rejected.
  Metadata is interpreted by LibTiff: it can warn and repair malformed tags (an
  invalid orientation was normalized during testing). This is not a forensic
  validator of original directory bytes; parsed layout checks do not undo such
  library repairs. The existing editor's global error handler was not changed.
* JPEG regression: **70 PASS**, the same two optional failures/four skips;
  all 12 legacy baseline hashes and all three accepted F16 preview hashes match
  `e87d7a82` exactly. The JPEG analytic-only source contract is unchanged.

The combined TIFF diagnostic peaked around **825 MiB** process working set. That
includes legacy decode, independent reference buffers, F16/SDR images and PNG
creation; it is not the decoder's isolated footprint or a production performance
target. Real wide-gamut TIFF photography, tiled/separate-plane TIFF input, alpha,
more profiles and production editing/export are still pending. The new input
implementation is CPU-only. **Film-scan visual acceptance has been received.**

### Original Contrast design (accepted checkpoint)

At `c95ef39f`, [RenderedContrast](Services/RenderedContrast.vb) introduced the first
measured rendered tone control, isolated from the production editor. Its numerical
body now lives in `RenderedBasicTone`; this entry point delegates to it. The source is
RenderedLinear Rec.2020 F16: RAWs have already passed scene rendering/WB/primary
calibration, while JPEG/TIFF have entered through their actual profiles. Contrast
belongs after these stages and before the point/channel curves; it is not a
scene exposure operation or a replacement RAW development curve.

The existing contrast response is measured in **encoded sRGB**. Applying it to
linear Rec.2020 channels or changing luma coefficients would change the look.
The adapter shares `BuildToneZoneTable` and `ApplyToneZones` with the editor;
only their assembly visibility changed. The 4,097-entry floating curve, minimum-
slope correction and min/max RGB hue rule remain the existing algorithms.
No production kernel, measured resource, slider range or saved recipe changes.

For compatibility, the adapter measures the effect on the bounded encoded-sRGB
projection of each straight RGB pixel. It decodes the before/after values and
adds only the **linear-sRGB effect delta**, transformed to Rec.2020, to the
original premultiplied working pixel. This follows the explicit Shadow Tint
adapter policy. Signed/wide/HDR source reserves therefore survive; the complete
working image is never replaced by its clipped SDR projection. This is a defined
legacy-look extension, not a newly calibrated HDR-native contrast algorithm.
Colors outside the old unit cube can respond differently from an sRGB8 reference
that permanently discards those reserves. Zero bypasses all color conversions
and must preserve stored values exactly. Alpha and source ownership are unchanged.

The temporary encoded samples and table are F32, the primary conversions and
transfer functions use the existing Double helpers, and output is explicitly
rounded to F16. No RGB8 or integer working-image boundary is introduced. The
4,097-point table represents the artistic response, not a quantized image.

**That checkpoint contained only Contrast.** It did not yet include brightness,
local highlights/shadows, whites/blacks,
RGB/channel curves, masks, full recipes and GPU execution are not included. The
other tone-zone controls must eventually combine their lifts and interactions
in one shared table before the min/max operation. Stacking independent Contrast
and future highlight/shadow adapters would be the wrong order/semantics. The
complete chain must also keep brightness before tonal zones, and Shadow Tint at
its original later hook. The comparison recipes leave these other tools neutral.


## Rendered Contrast checkpoint and evidence

The mixed review corpus contains the five previously reported RAWs, three
profiled wide-gamut JPEGs and RGB16 film scan. RAW source preparation uses the
accepted lens/TCA, highlight-repair and neutral WB/primary-calibration development
path. The rendered files use their accepted ICC-aware entry points. No other
editing controls are active.

* Build succeeds with zero warnings/errors. The nine-image run reports **103
  PASS**, two unchanged optional direct Skia CPU-F16 failures and four skips.
  Selected CPU fallback and existing GPU **transport** gates pass. This does not
  validate a GPU implementation of Contrast: the adapter remains CPU-only.
* All **30 pre-change legacy baseline hashes** are unchanged, including old
  mixed tone/color recipes. Eight pre-migration legacy calibration fixtures also
  pass. The only changes to existing editor kernels are two Private-to-Friend
  declarations and a descriptive comment; their numerical bodies are unchanged.
* All **nine neutral PNGs** match the accepted RAW calibration-zero, profiled-JPEG
  and TIFF preview hashes exactly. Every -40/+40 preview differs from neutral and
  from its opposite edit. There are **45 Contrast review images**, including 18
  old-renderer references from the same developed input. All 109 local review
  links were checked. Edited photographic appearance still needs acceptance.
* Independent Double reference calculations use the measured Contrast lift as
  policy input, then independently construct the 4,097-entry curve, slope budget,
  min/max RGB mapping, transfer functions and linear correction delta. Tests
  cover +/-40/50/100, signed/HDR colors, saturated/neutral colors, tiny shadows,
  partial/zero alpha, exact zero, source ownership and invalid contracts.
* A 4,097-step neutral ramp remains neutral and monotone at both slider extremes
  after F16 storage. Numerical tolerances include F16 rounding; they do not require
  identical output to the old RGB8 input/output quantization.

The legacy references deliberately cross into SDR sRGB8 before the old effect;
otherwise they would not exercise that renderer's real input contract. Their
results use the common diagnostic preview writer (including its F16 boundary),
so these are visual references, not exact golden RGB8-output hashes. The separate
legacy baseline hashes guard unchanged production output. The main numerical
reference never quantizes the new operation's input to RGB8.

The combined diagnostic peaked around **1,658 MiB** process working set, including
RAW caches, old rendering, multiple full frames and PNG generation. No isolated
speed or production memory claim follows from that number. Next work still needs
bounded slices for brightness and the combined tone-zone group, spatial tonal
statistics/local layers, curves/color, masks, GPU algorithms and editor/export
integration. **Contrast visual acceptance has been received.**

### Accepted scalar/Contrast subset

[RenderedBasicTone](Services/RenderedBasicTone.vb) now owns one ordered rendered
point stage: scalar exposure/brightness per encoded-sRGB channel, then the
measured min/max Contrast mapping. The accepted [RenderedContrast](Services/RenderedContrast.vb)
entry point delegates with brightness/exposure zero. Contrast's mathematical
policy has not changed; the group avoids separate bounded projections and an
extra F16 storage boundary between basic controls.

The shared editor `BuildPointOpScalarTable` builds the existing 4,097-entry F32
response: rendered exposure decodes sRGB, applies `2^EV`, re-encodes, adds the
Brightness offset, then uses the existing overflow-dependent toe/shoulder policy.
Brightness is intentionally defined in the encoded domain. Replacing it with a
linear offset or a Rec.2020 channel formula would change the control. The scalar
stage precedes Contrast; the combined linear correction delta is returned once
to the original premultiplied Rec.2020 frame. Original signed/HDR reserves are
retained by the same explicit residual policy accepted for Contrast. This is
legacy-look compatibility, not a newly calibrated HDR-native tone model.

**RAW exposure is separate.** Sensor RAW user EV continues through
`RawSceneDevelopment.Render` before its toe/shoulder/ACR3 rendering, then this
rendered group receives exposure zero. Already-rendered JPEG/TIFF files use the
rendered scalar exposure route. `renderedExposureEv` is an explicit API argument,
limited to +/-4 EV (the old +/-100 exposure slider mapping); brightness/contrast
remain +/-100. The frame's SceneLinear/RenderedLinear domain does not encode
source provenance. Correct RAW/import routing is an explicit caller obligation,
demonstrated by the diagnostic recipes; it still needs editor orchestration.
Never apply both exposure routes to the same RAW edit.

Input/output remain rendered-linear Rec.2020 D65 RGBA F16. Temporary encoded
sRGB samples and response tables are F32; conversions/delta calculation use the
existing higher-precision helpers; storage uses explicit half packing. Zero
bypasses all color-space work and tables. No RGB8 intermediate image is used to
apply the new controls. The normal editor and its shared kernel bodies are
unchanged; only two more internal helper declarations become Friend.

This accepted subset API remains CPU-only and isolated. Local highlights/shadows,
image statistics, whites/blacks and saturation/vibrance are outside this subset
and are now provided by the separate full-basic API above. Curves, masks and full
recipes remain to be migrated. The full-basic API combines the additional tone-zone
lifts in one common table before the min/max rule, preserving their measured
statistics/local-layer interactions rather than stacking independently clipped
adapters. This stable subset continues to provide the earlier scalar/Contrast slot.


## Basic-tone group checkpoint and evidence

This bounded iteration uses **two representative RAWs** (`0M8A0758.CR2` and
`IMG_0018.CR2`), all three profiled wide-gamut JPEGs and the RGB16 film scan.
The two RAWs cover bright lens reflections and skin/window gradients; this does
not replace a later full-RAW-corpus campaign. The other three previously reported
RAWs were not rerun photographically for this group.

* Build succeeds with zero warnings/errors. **90 PASS**, two unchanged optional
  direct Skia CPU-F16 failures, four skips; selected CPU fallback and GPU transport
  gates pass. The new group itself is CPU-only, not a GPU algorithm migration.
* All **21 corresponding legacy baseline hashes** match `c95ef39f`, including
  existing mixed tone/color recipes. The accepted legacy calibration fixture
  checks also pass. Existing editor scalar/table-sampling bodies are unchanged;
  only their assembly visibility changed for reuse.
* All **six neutral previews and six Contrast-only +40 previews** match the
  accepted images exactly. This guards the compatibility wrapper and confirms
  that zero scalar controls add no extra table/curve/storage effect to Contrast.
  All 30 new brightness/exposure/combined previews differ from neutral. All seven
  new-path recipes for each source produce distinct PNGs.
* **72 review images** were generated; all 127 local links are valid. Each source
  has brightness +/-30, exposure +/-1 EV, combined -1 EV / brightness +20 /
  contrast +30, zero and Contrast-only regression. Five old-renderer references
  use the same developed source and common diagnostic preview writer.
* Independent Double references evaluate scalar exposure/brightness, overflow-
  dependent toe/shoulder and curve interpolation before the independent measured
  Contrast reference. Cases cover brightness +/-100, rendered EV +/-4, combined
  settings, order-sensitive colors, signed/HDR/subnormal data and partial/zero
  alpha. Exact zero, immutable ownership, monotone neutral ramps and invalid
  slider/EV/domain contracts pass. The previously accepted Contrast tests still
  run through its delegation entry point.

RAW exposure is routed only through scene development in the photographic
recipes. For raw +/-1 EV references the old rendered chain also gets exposure
zero, because the same developed source already contains that EV. JPEG/TIFF
references receive the rendered exposure control. The source frame carries only
its scene/rendered domain; production provenance/routing enforcement remains
future integration work, not an automatic guarantee of the new point API.

The combined diagnostic peaked around **1,890 MiB**, including RAW caches, old
references, multiple full frames and PNG generation. The group removes an F16
boundary between its scalar and Contrast operations, but no isolated throughput
claim follows from this full diagnostic. **Edited basic-tone visual acceptance
has been received.** At that checkpoint, joint image-statistics/local-layer tone zones and saturation/
vibrance still remained. The full basic group above now implements them. Curves,
masks and GPU/editor/export integration remain unfinished; the production
pipeline has not switched.

## Full basic-control checkpoint and evidence

The main photographic corpus remains the two representative RAWs, three profiled
JPEGs and RGB16 film scan. Sixteen full-resolution recipes per source cover both
directions of all six requested controls, complete desaturation + maximum
vibrance, joint tonal edits and all basic controls together. Four old sRGB8
spatial/mixed references start from the same developed source.

* Main photographic run: **95 PASS**, two unchanged optional direct Skia CPU-F16
  failures, four skips; selected CPU fallback and existing GPU transport pass.
  New effects are CPU-only; this is not shader validation.
* All **21 legacy baseline hashes** match accepted `9930ef60`, and all **six neutral
  previews** match the accepted basic-tone set exactly. The sixteen newly captured
  pre-extraction legacy hashes and earlier calibration fixtures also pass.
* **120 review images** and all **241 local links** are verified. All six complete-
  desaturation/max-vibrance previews are neutral (at most one output code of
  channel difference). Edited look still needs visual acceptance.
* Exact source-code float inputs match the old renderer within its final one-code
  quantization/dither budget; local-base grids match exactly. Separate independent
  references cover saturation/vibrance at -100/-50/0/+50/+100, hue interpolation,
  negative exponential response, positive linear-Y preservation, and the extended
  saturation residue. Whole-frame monochrome, HDR/negative neutral probes,
  partial/zero alpha, identity, ownership, invalid settings, sub-RGB8 local data
  and sparse diagonal statistics pass. Intentional desaturation can move colored
  HDR components into range; range tests do not wrongly require their old channel
  maxima/signs to remain after that edit.

The main diagnostic peaked around **3,520 MiB** including full F32 analysis,
multiple F16 frames, legacy references, local-base caches and PNG output. This is
higher than prior point-only checkpoints and needs row/tile reuse and memory
optimization before production integration. No responsiveness or speedup claim
follows from this diagnostic. The settings rollout remains optional/off by
default as planned below. Current mixed recipes use neutral WB/primary calibration;
full mixed-WB/curve/mask/extension campaigns remain future work.

### Additional profiled color-cube corpus

The user supplied three 1024 x 1024 RGBA8 PNGs under
`C:\Users\dsteiner\Pictures\raws\cube`. Each has 159 distinct alpha values,
including 0 and 255, and a 480-byte embedded RGB matrix/parametric-TRC profile:

| File | Actual embedded description |
|---|---|
| RGB_Colorcube_Corner_White_ProPhotoRGB.png | ROMM |
| RGB_Colorcube_Corner_White_Rec2020.png | 2020 |
| RGB_Colorcube_Corner_White_sRGB.png | sRGB |

Their decoded straight-RGBA bytes are identical (SHA256
`0D40016A1F6F5B6AEBC8C0008FFA146363F1C47373AF2D23BD4B89B0DB55E8A4`): profiles were
assigned to the same code values, not used to make appearance-preserving copies.
Different rendered colors are therefore expected, not a cross-profile mismatch.
The diagnostic Skia decoder's raw-byte hashes match an independent Pillow decode.

[ProfiledPngFixture](tools/ImagingPrecisionProbe/ProfiledPngFixture.cs) is a
**probe-only** reader, not a production PNG migration. It reads and CRC-checks the
original compressed iCCP profile, decodes unpremultiplied RGBA8 without CMM
conversion, evaluates the shared exact matrix/TRC boundary and applies linear
premultiplication before F16 storage. Every original pixel is independently
checked against raw ICC tags plus alpha; there is no sRGB8 intermediate. General
PNG formats/profile fallback/orientation policy remain unimplemented in the new
production input path.

The cube corpus also exposed an assumption in the old boundary diagnostic:
legacy color-managed previews can carry RGB above alpha at translucent edges
(e.g. blue 67 with alpha 63). Those are extended colors, not bounded SDR data.
The existing named SDR boundary intentionally clips them, so comparing those
original bytes to SDR output confuses required range projection with precision
loss. The diagnostic now reports the original extended-source roundtrip as
inapplicable and separately requires the unchanged two-code fidelity limit
against an explicitly canonical bounded-SDR reference. A synthetic 67/63 test
verifies this policy and that source bytes/alpha stay unchanged. This changes
neither the production decoder nor the working boundary's numerical behavior;
the native-profile fixture path bypasses that legacy SDR projection entirely.
Original observations remain visible in the cube report, not hidden by a larger
error tolerance. Required native-profile ICC/alpha checks remain independent.

The final three-cube run reports **84 PASS**, the same two optional direct
CPU-F16 failures and **six skips**; CPU fallback/GPU transport gates pass. Two
additional skips explicitly identify the inapplicable lossless-SDR comparison
for extended legacy input; canonical bounded-SDR and native-profile checks are
still required and pass. The ROMM preview has 3,477 RGB>alpha samples (maximum
excess 67 codes); the Rec.2020 preview has 3,394 (maximum 50). sRGB does not show
that extended-source condition. The original observed 67/63 pixel is retained in
the explanation; no production clamp/decoder behavior was changed.

All **60 cube review images** and **121 local links** are verified. Full
desaturation plus maximum vibrance is neutral across all three cubes, including
transparent edges (premultiplied channel differences at most one SDR code).
All straight decoded payload hashes match the independent Pillow hash above.
Cube peak working set was roughly **706 MiB**. The extra synthetic SDR-range
normalization check was added after the main photographic run; it and the full
numerical/legacy suite pass in the final harness. Edited photographic/cube
appearance still awaits visual acceptance.

Final no-photograph verification after allocation-guard cleanup: **66 PASS**, two
known optional failures, four skips; CPU fallback/GPU transport pass. Build:
zero warnings and zero errors. Core equations and review artifacts are unchanged
by that cleanup; invalid staging dimensions/EV settings are explicitly tested.

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
| `cd8e9e66` | Reuse current anchored control/camera adaptation directly on the pre-curve ramp; avoid inverse-LUT/clipped RGB8 detour | 97 PASS, two known optional failures, five skips; CPU fallback/GPU transport gates pass. 21 baseline hashes and five as-shot PNG hashes unchanged; 35 WB previews. Visually accepted |
| `971fb147` | Preserve six current-model primary controls after WB and the separate late encoded-sRGB Shadow Tint response | 100 PASS, two known optional failures, five skips; CPU fallback/GPU transport gates pass. 21 baseline, eight pre-migration legacy editor and five zero-calibration hashes unchanged. All 45 edited previews differ; 50 review images generated. Visually accepted |
| `e87d7a82` | Decode embedded matrix/analytic-TRC JPEG8 directly into rendered-linear Rec.2020 F16; avoid native approximation and sRGB8 gamut loss | 67 PASS, two known optional failures, four skips; CPU fallback/GPU transport pass. Every source pixel matches independent ICC reference; all 12 legacy baselines unchanged; SDR max difference one code. Three pairs visually accepted |
| `588d7116` | Share exact matrix/TRC conversion while preserving original 16-bit TIFF source codes and tabulated profile curves | Scan: 60 PASS, two known optional failures, four skips; CPU fallback/GPU transport pass. Every scan pixel matches independent reference; six legacy hashes unchanged. JPEG regression retains all 12 baselines and three accepted F16 preview hashes; film-scan visually accepted |
| `c95ef39f` | Reuse the existing measured encoded-sRGB curve and min/max RGB tone rule through an F32 delta adapter, retaining original Rec.2020 reserves | Nine-image corpus: 103 PASS, two known optional failures, four skips; CPU fallback/GPU transport pass. All 30 legacy baselines and nine neutral previews unchanged; all 18 edits differ. 45 review images; visually accepted |
| `9930ef60` | Share exposure/brightness scalar policy before Contrast in one rendered pass; keep RAW exposure before scene rendering | Six-image corpus: 90 PASS, two known optional failures, four skips; CPU fallback/GPU transport pass. 21 legacy baselines, six neutral and six Contrast-only previews unchanged; all 30 new edits differ. 72 review images; visually accepted |
| `d0aff3cc` (saturation superseded below) | F32 statistics/local base, shared joint tonal math and saturation/vibrance; extended saturation also neutralizes color residue | Main: 95 PASS, two known optional failures, four skips; 21 legacy hashes/six neutral previews unchanged, 120 images. Cubes: 84 PASS, two known optional failures, six explicit skips; independent ICC/alpha checks and 60 images. CPU fallback/GPU transport pass; visual review pending |
| `35162ddc` | Replace folded signed-gamma saturation with native linear Rec.2020 Y/gain; keep legacy renderer unchanged | Cubes: 84 PASS; photos: 96 PASS. Known optional failures remain visible; all required checks pass. 49 wide-primary ramps, independent Float64 cube comparison, 21 unchanged legacy baselines, six neutral and 24 legacy PNGs unchanged. Visually accepted |
| `45de8b6a` | Optional immutable curves join the same F32 basic pass; share RGB/master Melissa and PCHIP HSL-lightness kernels without integer image staging | Numerical: 71 PASS. Cubes: 89 PASS; photos: 101 PASS. Known optional failures remain visible; all required checks pass. Ten new frozen curve hashes, 21 photographic / 12 cube baselines, and all nine neutral PNGs unchanged. Visual review pending |
| `5f403cf7` | Six related families share one F32 basic/curve pass and existing kernels; explicit reserve retention/replacement prevents source-color leaks | Numerical: 76 PASS; cubes: 94 PASS; photos: 106 PASS. Known optional failures remain visible; required checks pass. Twenty new frozen recipes; 21 photographic / 12 cube baselines and nine neutral PNGs unchanged. Visual gate open |
| `49214977` | F32 alpha-aware Gaussian guide, all HSL bands, point selections/visualization and shared deferred curve order | Numerical 80 PASS; cubes 98 PASS; photos 110 PASS; required checks pass. 29 frozen recipes, 12/21 baseline hashes and nine neutral PNGs unchanged. Joint visual gate open |
| Rendered-input preparation (this commit) | Explicit log-density inversion followed by rendered WB; fused unclipped linear Rec.2020 Bradford matrix and legacy encoded delta | Numerical 84 PASS; cubes 102 PASS; four rendered photos 104 PASS; required checks pass. Ten frozen recipes, 12/15 baseline hashes and seven neutral PNGs unchanged. Automatic film analysis and RAW coordinator remain |

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

The earlier late-color, HSL/point-color, rendered-input and automatic-film gates
were accepted. The latest gates are geometry-review.html and composite-review.html,
described in the newest checkpoint below. Earlier galleries remain useful: the HSL gate is
**`hsl-point-review.html`**; inspect the F32 guide at noisy edges, band/color
transitions, point visualization and mixed deferred curves. The rendered-input gate is
**`rendered-input-review.html`** for legacy/native WB and explicit negative math;
its illustrative film swatches are not fitted to every source. Both gates are
linked from `f16-two-step-review.html` in the diagnostic output workspace. The previous
**`late-color-review.html`** remains available, with eleven selected recipes
per photographic/cube input and four old-editor references. Inspect smooth
`grade-combined`, shadow tint, `map-half`, skin and highlight/shadow color casts;
check `mixer-monochrome` for source-color leaks, `map-full` for true replacement,
and `mixed-chain` for combined basic/curve/grade/tint/mixer/map behavior.
`gray-toned` intentionally adds color after desaturation. Posterize steps and
threshold's binary edge are intentional, not smoothness failures. Earlier
`curves-review.html` galleries remain available for combined review. All PNGs are
diagnostic SDR sRGB, not monitor-managed/HDR output. Required failures must be
resolved before advancing; visual review is still a separate gate.

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
* **Editable white balance / primary calibration — current anchored RAW model implemented.** Remaining
  work is legacy model/recipe semantics, film-negative ordering, eyedropper behavior
  and editor/GPU integration. The new direct ramp
  evaluation has received visual acceptance; it is not a
  promise of parity with the old clipped/inverse-LUT intermediate. Shadow Tint now has an inline prototype hook; full editor orchestration
  and the legacy/non-RAW calibration models still need integration.
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

The initial RGB16 TIFF subset now retains source precision and its actual
embedded profile before entering the working space. Other TIFF layouts and file
formats still need equally explicit input contracts. The declared-sRGB
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

**User integration requirement:** add a **Pipeline** category in Settings,
positioned **between Editor and Raw development**. The new pipeline must be
optional, with a master on/off switch; default off preserves current behavior.
The user also requested room to enable it in parts where that makes sense.
These are recorded requirements for the later integration phase; no settings UI
or production routing has been added by the current tool migration.

Keep changes to existing code **as small as possible**. Prefer an isolated new
renderer/coordinator behind the existing render entry points, with a small mode
selection boundary **before a legacy byte decode can discard source precision**.
Retrofitting an already-developed sRGB8 bitmap into F16 must not be labeled the
new native-precision pipeline. Continue sharing pure calibrated kernels rather than making
parallel versions of the old algorithms or scattering feature checks through
all existing tools. Keep the legacy path intact and regression-tested.

First integrate a coherent validated bundle behind the master switch. Optional
partial rollout controls may then select dependency-safe bundles (for example,
source/scene development versus rendered tools), with explicit space/format
adapters. Arbitrary per-tool mixing is not yet a design: Rec.2020 F16 must never
silently enter a legacy encoded-sRGB8 kernel. The exact partial-control UI can be
decided after those boundaries are validated; do not imply it already exists or
expose placeholder switches that have no processing effect.

Capture mode/stage configuration once per render request. Include pipeline mode,
version and enabled bundles in cache identities, so toggling cannot reuse an old
RGB8 result as a new wide-gamut frame or change domains halfway through a render.
Route preview, full-resolution output, thumbnails, exports and adjustment
ownership consistently without double exposure/WB. Existing persisted recipes
must retain their meaning; avoid schema changes unless a separately reviewed
compatibility decision requires them. Unsupported stages/formats need an explicit
fallback policy, not a silent claim of wide-gamut processing.

Run the full supplied corpus plus targeted cameras/lenses, saturated/out-of-gamut
colors, deep gradients, clipped highlights, alpha, HDR, extension recipes and
high-bit-depth non-RAW files. Validate available GPU backends/platforms and CPU
fallbacks. Changing the default away from off requires a separate decision after visual,
numerical, performance and
preview/export consistency gates are satisfied.

## Correction after the wide-gamut cube review (`35162ddc`, accepted)

The `d0aff3cc` gray-vibrance recipe was neutral but not smooth. Its signed sRGB
gamma extension followed by an encoded Rec.601 gray sum could produce a negative
gray from a positive-luminance wide-gamut color. That value clipped to black in
the diagnostic SDR preview, producing curved dark lobes. Neutrality alone was an
insufficient acceptance test; the old extended-reference test also validated the
chosen formula without validating its tonal behavior.

An independent Float64 ICC/Bradford/transfer calculation reproduced the defect:
44,820 opaque ProPhoto pixels and 12,069 Rec.2020 pixels had negative signed gray
despite positive CIE Y; the sRGB cube had none. Mean difference from the old F16
preview was below 0.04 output code. This identifies an algorithm problem rather
than F16 precision, a source-profile error or output quantization.

The native linear saturation above replaces that extension. The regression
suite now compares independent Rec.2020 luminance/gain values for signed, HDR and
partial-alpha colors, plus 49 primary-to-primary ramps of 1025 pixels each at
saturation -100/vibrance +100. Those ramps include approximate ROMM primaries
outside Rec.2020 and test gray neutrality, alpha and agreement with luminance
through gamut boundaries, allowing only half-storage rounding. The sixteen
frozen legacy hashes and the bounded legacy-oracle tests remain unchanged.

Cube validation: **84 PASS**, two previously known optional Skia CPU-F16
subnormal failures and six explicit skips; all required checks, explicit-half CPU
fallback and GPU transport pass. The 60 cube review images include the full
sixteen-recipe set and four legacy references per source. Independent Float64
ICC/luminance previews agree with the corrected gray-vibrance result within one
output code on every opaque pixel in all three cubes (mean below 0.021 code).
The new ramp checks and sixteen frozen legacy hashes pass.

The six-image photographic run (two RAWs, three matrix-profile JPEGs and the
RGB16 film scan) has **96 PASS**, the same two optional failures and four skips.
All 21 legacy baseline hashes, six neutral PNGs and 24 legacy comparison PNGs are
unchanged. It generated 120 photographic review images; peak process working set
was about 3.48 GiB, including old-reference rendering and PNG generation, not an
isolated saturation benchmark. The build passes with zero warnings/errors.
Retaining Double accumulation through the final linear effect addition can move
a few tone-only preview samples by one output code; it does not change the legacy
renderer or neutral working-frame identity. All 419 local links in the three
review pages resolve. A refreshed cube run after the final clean build passes the same required
checks, with all 60 cube PNGs byte-identical to the first corrected run. The corrected basic-control slice has since received visual acceptance. No later tool migration, GPU algorithm
port or production settings/routing is part of this correction.

## Tone-curve iteration (`45de8b6a`, continuation authorized)

The preceding wide-gamut saturation correction has received visual acceptance.
This slice adds the master RGB, red/green/blue and luminance curves to the same
floating basic-control pass. Keeping curves in that pass preserves shared scalar
and local-tone preparation and the late saturation-restore/vibrance placement;
a separate bitmap filter would introduce another boundary and could reorder them.

The RGB/master curve kernel is calibrated in ProPhoto primaries with the sRGB
transfer function (the code calls this Melissa). It uses natural cubic splines,
master RGBTone mapping by channel extrema followed by per-channel curves, then
the existing bounded return to encoded sRGB. The separately named luminance curve
uses PCHIP on **encoded-sRGB HSL lightness**, not CIE luminance. These distinct
artistic contracts must be preserved explicitly, not relabeled as native linear
Rec.2020 curves. Working storage remains premultiplied linear Rec.2020 F16.

Implementation: immutable validated curve settings; reuse the existing kernels in the F32
compatibility adapter; suppress duplicate saturation as before; return only the
combined linear effect delta to the saturated working baseline. RGB curves precede
the HSL block in this restricted group. Full HSL/mixer migration must still honor
the existing conditional deferred-curve placement. No mixer, masks, GPU algorithm
port, editor routing, settings UI or output-policy change is included here.

Reference capture completed before service changes: ten legacy curve recipes
from accepted `35162ddc`. Numerical validation covers old/new bounded pixel parity
and joint local-tone preparation, independent curve/primary/transfer references,
neutral ramps, sub-code differences, alpha, signed/HDR preservation, identity and
invalid input. Photographic and three-profile cube comparisons were generated
after those checks passed. The visual gate remains open until reviewed.

The API accepts invariant-culture finite 0..255 coordinates, 2..1024 points per
curve and unique X positions; it sorts points without rounding their values.
The point-count limit bounds repeated legacy spline-table construction. Malformed,
nonfinite or out-of-range strings fail explicitly instead of being silently
repaired by the old parser. Interior endpoints and non-monotone user curves remain
valid. Full-range redundant diagonals normalize to exact identity, avoiding an
unnecessary primaries/transfer roundtrip. This is an isolated API contract, not a
change to legacy saved-recipe parsing or a completed XMP/preset integration.

Validation so far: clean build, zero warnings/errors; **71 PASS**, two known
optional direct Skia-F16 subnormal failures and four skips in the numerical-only
run. All required checks, explicit-half fallback and GPU transport pass. Ten
pre-change curve hashes remain byte-identical. Independent affine master/channel
order plus ProPhoto-primary/sRGB-transfer reference has maximum encoded error
`4.6065e-7`; closed-form natural and PCHIP curves pass at 4097 gray samples.
Mixed local grids/coverage exactly match the legacy oracle. Sub-code precision,
monotone neutral ramps, source ownership, partial alpha, signed/HDR reserves and
strict curve-input contracts pass.

The cube run has **89 PASS**, the same two optional failures and six explicit
skips. All 12 legacy baseline hashes and three neutral cube PNGs are unchanged;
all three gray-master PNGs have zero RGB channel spread. It produced 42 cube
review images. The six-photo run has **101 PASS**, two known optional failures
and four skips; all required checks, explicit-half fallback and GPU transport pass.
All 21 old-editor baseline hashes and six neutral photographic PNGs are unchanged.
It generated 84 full-resolution photographic previews/references. Both galleries
have valid local links (85 cube links, 169 photographic links).

Peak process working set was about **3.87 GiB** for the photographic run, including
decode, old-reference rendering and PNG generation. There is no extra full-frame
image allocation for curves beyond the existing group's 16-byte/pixel F32 staging;
these measurements are not an isolated curve benchmark or a demonstrated speedup.
The final clean build has zero warnings/errors. The final numerical run again has **71 PASS**, with the same two known optional
failures and four skips; all required checks pass. Added regressions confirm
that neutral curves preserve all sixteen accepted basic recipes exactly, interior
diagonal endpoints stay active, and oversized point sets fail explicitly.
No visual acceptance is claimed yet.

## Late color / creative point iteration (`5f403cf7`, accepted)

This deliberately larger iteration migrates six related tool families together:
color grading (four hue/saturation/luminance zones, balance and blending), the
calibration Shadow Tint hook, RGB/monochrome channel mixer, gradient map, posterize
and threshold. They join the same floating basic/curve pass, rather than creating
six intermediate images and six independent conversions. Typed immutable settings
copy mixer arrays and validate the actual GUI ranges (posterize 0/1 normalize to
the existing inactive state); full mutable editor recipes
are not admitted. Inactive hues/blending/gray weights do not activate neutral tools.

| Operation, in order | Calculation space / format | Why this contract remains |
|---|---|---|
| Color grading | F32 encoded sRGB D65; BT.601 Y/Cb/Cr, calibrated 20-sample zone tables | Its measured hue vectors, balance/blending and zone brightness response are not a linear Rec.2020 model. All four zones/luminances retain their existing kernel. |
| Calibration Shadow Tint | F32 encoded sRGB / Rec.601, after grading | Reuse the previously migrated response in its actual late position. Do not apply the standalone adapter as well as this hook. |
| Channel mixer | F32 encoded sRGB; affine RGB percentages/constants or monochrome row, deliberately clamped to 0..1 | Keep the editor's existing channel/offset meaning and bounded output. A native unclipped HDR mixer would require a separate semantic decision. |
| Gradient map | F32 encoded sRGB; Rec.601 brightness to RGB swatches, amount blend | RGB8 swatches are control metadata, not an integer working image. Swatch alpha is ignored as in the editor; image coverage remains separate. |
| Posterize | F32 encoded sRGB, intentionally rounded to the existing byte-code palette | Quantization is the requested creative effect. No RGB8 image buffer is introduced. |
| Threshold | Encoded-sRGB Rec.601 brightness, inclusive integer-code threshold; float 0/1 result | Binary output is intentional; threshold runs after posterize, matching the old chain. |

The mixer/map and palette kernels were extracted verbatim and are shared by the
legacy renderer and floating group. Grading and shadow tint already had shared
kernels. Neutral helper calls are skipped. Twenty frozen Windows x64 legacy
recipes were captured from accepted `45de8b6a` before extraction; earlier curve,
basic and calibration fixtures remain active. There are no new GPU algorithms.

### Explicit working-range policy

Working storage remains premultiplied linear Rec.2020 F16. Basic/curve/grade/tint
operations return their bounded SDR effect delta to the unbounded saturated source
baseline, retaining the existing wide/signed/HDR reserve. **That policy cannot be
used unchanged for replacing effects:** it would leave original color in a
monochrome mixer, non-map colors in a full gradient, and non-palette values in
posterize/threshold. The former signed-gamma saturation extension is not reused.

Define `reserve = native baseline - linear Rec.2020(bounded original projection)`.
The final result is `linear Rec.2020(processed projection) + retention * reserve`,
with alpha handled consistently in premultiplied coordinates:

* Grading/tint only: retention 1 (the accepted delta policy).
* Active RGB/monochrome mixer or posterize/threshold: retention 0, honoring the
  explicitly bounded/replacing artistic operator. These controls intentionally
  constrain working gamut/range; native HDR versions are not claimed.
* Gradient alone after preserving stages: retention `1 - amount/100`; its untouched
  contribution fades the **linear** reserve. Full strength replaces all source
  colors; intermediate amounts retain the corresponding portion of HDR/signed data.

This preserves the old bounded pixel behavior without pretending every artistic
control is an HDR-native operation. Output/display gamut mapping remains a later
project. The new API is isolated; no editor routing, UI/translation changes,
presets/LUTs, spatial HSL guide, point color, masks or settings switch are enabled.
HSL bands, their spatial guide and deferred curves now migrate in the following
checkpoint as one coherent group. The existing future **Pipeline** settings
requirement and default-off rollout remain unchanged.

### Validation and visual gate

The first clean build has zero warnings/errors and all required numerical checks
pass. Tests cover twenty frozen legacy hashes, all twenty bounded pixel recipes
and mixed ordering, independent Double channel matrices/maps/palette/threshold,
2/6/64-level dense palette ramps, exact monochrome/binary replacement on wide/HDR
inputs, partial-map reserve attenuation, partial alpha, source ownership, the
accepted standalone shadow-tint adapter, neutral curve/basic identity and invalid
settings. Numerical-only baseline: **76 PASS**, two previously known optional direct Skia
F16 subnormal failures and four skips. The final cube run, including the neutral
call guards, threshold tie and inactive one-level GUI regression, has **94 PASS**,
the same two optional failures and six explicit skips. All required checks,
explicit-half CPU fallback and GPU transport pass. All three neutral cube PNGs
are byte-identical to the previous checkpoint; all three monochrome-mixer PNGs
have zero RGB channel spread. Forty-five cube previews/references and 91 valid
local gallery links were generated. The photographic run has **106 PASS**, the same two known optional failures
and four skips. All required checks, explicit-half fallback and GPU transport
pass. All 21 photographic and 12 cube legacy baseline hashes are unchanged;
all nine neutral PNGs are byte-identical to the previous checkpoint. Ninety
photographic previews/references were generated, with all 181 gallery links valid.
The photographic peak process working set was about **4.06 GiB**, including RAW
input, old-reference rendering and PNG generation. No isolated kernel speedup is
claimed. The final clean build has zero warnings/errors; the final numerical run again
has **76 PASS**, the same two optional failures and four skips, and all required
checks pass. It also verifies legacy ties-to-even rounding of fractional GUI
counts rather than truncation.
Visual acceptance of this larger group remains open.

## Two-step batch: HSL/point color, then rendered input preparation

The user authorized two further steps together after accepting the late-color
batch. Each gets a required numerical gate, source/profile comparisons, a design
update and a local commit. Visual gates for both remain available for joint review;
there is no push or production-editor routing.

### Step 1: HSL bands, spatial guide and point color

`RenderedHslPointSettings` snapshots eight H/S/L rows and up to eight validated
point-color selections, copying mutable band/range data. Point metadata retains
the existing six-decimal recipe format. The legacy HSL/point-color block was
extracted and shared unchanged: pre-tone saturation restore, HSL-lightness curve,
vibrance, guided band gates, linear-ProPhoto HSV point selections, deferred RGB
curves when bands are active, and grading keep their original order. Selection
visualization remains after the creative tail and explicitly replaces the bounded
preview rather than leaking the source's HDR/color reserve behind the mask.

The guide is now encoded-sRGB RGBA F32, Gaussian sigma 1.5 image pixels, with
clamped edges and premultiplied/alpha-weighted filtering. Hidden RGB cannot bleed
into a selection. It uses one additional 16-byte/pixel output buffer and a bounded
horizontal row ring, not two full-frame Gaussian intermediates. A full Double
2-D convolution independently verifies row-ring wrap, alpha and edge behavior.
The old editor retains Skia's original approximate RGB8 blur. Guide differences
are intentional; no-guide pixel oracles separately guard the calibrated math.
HSL effects otherwise retain the accepted bounded artistic adapter/delta policy,
not a claim of an HDR-native HSL model. Point source/selection calculations use
linear ProPhoto; bands use ProPhoto hue for selection and encoded-sRGB HSL for the
calibrated shifts/gates. Working image storage stays linear Rec.2020 F16.

Initial numerical gate stopped on a new Gaussian accumulator-reset error; no
review images or commit were generated over that failure. Explicit per-pixel
initialization fixes VB loop-local accumulation. The corrected numerical run has
**80 PASS**, two known optional direct Skia-F16 subnormal failures and four skips.
All required checks, explicit-half CPU fallback and GPU transport pass. All 29
pre-change spatial/color hashes and no-guide pixel oracles pass. The full Double
Gaussian reference, sub-code/alpha/hidden-color/ring-wrap and range tests pass.
The clean build has zero warnings/errors.

Cube comparison: **98 PASS**, two known optional failures, six skips. Photographic
comparison: **110 PASS**, two known optional failures, four skips. All required
checks pass; all 12 cube and 21 photographic legacy baseline hashes and all nine
neutral PNGs are unchanged. The galleries contain 33 cube and 66 photographic
previews/references, with all 67/133 local links valid. Peak photographic process
working set is about **5.03 GiB**, including the extra F32 guide, RAW input,
old-reference renders and PNG generation; memory reduction remains required before
production integration. This is not a GPU algorithm port. Visual review remains
open under the explicitly authorized two-step batch.

### Step 2 scope: explicit negative inversion and rendered WB

The second bounded stage now processes rendered inputs before user saturation/tone:
explicit film-base/density inversion, then legacy encoded WB or linear Bradford
adaptation. Current anchored RAW WB remains in its accepted pre-curve scene stage;
it must not also be applied by the rendered adapter. Automatic film-holder/base
estimation, geometry/crop ordering, pipette UI and legacy RAW coordinator routing
remain explicit future integration work. Ten legacy rendered-input recipes were
captured before either extraction for the second step's regression gate.

`RenderedInputControls` consumes immutable `RenderedWhiteBalanceSettings` and
`RenderedNegativeSettings`. It is a distinct **rendered-input preparation** stage:
source profile import -> optional explicit inversion -> WB -> native saturation /
basic/curve/HSL/color group. No actual editor routing has changed.

* **Film inversion:** the existing 4097-node logarithmic density tables and gamma
  response are reused, in the calibrated encoded-sRGB scan model. Base/density
  RGB swatches are explicit legacy control metadata; the working image is never
  RGB8. Inversion deliberately replaces the bounded scan projection, normalizes
  each channel first and optionally produces monochrome before WB. One-code log
  floor and endpoint clamps retain the existing model. Missing/invalid calibration
  fails explicitly; there is no hidden RGB8 automatic estimation fallback.
* **Legacy WB models 0/1:** reuse the encoded-sRGB temperature/tint matrix and the
  accepted linear-delta policy, retaining native wide/signed/HDR reserves outside
  the bounded artistic projection. Unsupported legacy Kelvin controls fail rather
  than being silently ignored.
* **Rendered Bradford models 2/3/4:** use the existing white-point interpretation
  and luminance-normalized chromatic adaptation. Model 2 is first upgraded using
  the editor's existing load-time conversion, preserving its old Kelvin/tint
  meaning. D65 is the rendered-image anchor; no RAW capture/camera anchor is admitted.
  The linear Rec.2020/sRGB/WB conversions fuse once into **one Double 3x3 matrix**.
  It operates on premultiplied pixels directly, retaining signed/HDR values with
  no gamma encode/decode, alpha division or old intermediate 0..1 clamp. Native
  HDR behavior intentionally differs from the old clipped rendered adapter.
* **Inversion plus WB:** inverse tables, optional monochrome, then WB execute in
  one pass with one final F16 store. There is no intermediate F16 image between
  the inversion and adaptation. User exposure/tone belongs on the resulting positive.

The scene-linear/current anchored RAW WB implementation is unchanged. This is not
an alternative call for the same RAW WB adjustment, and does not complete routing
of legacy anchored RAW recipes. Automatic film-holder/base estimation, crop-before-
measurement geometry and WB/film/point eyedropper sampling remain later steps.
Both new paths are CPU-only; no settings UI, exports, masks or GPU kernels are
activated by these diagnostic services.

Numerical gate: clean build, zero warnings/errors; **84 PASS**, two known optional
Skia F16 subnormal failures and four skips. Required checks, explicit-half fallback
and GPU transport pass. Ten frozen legacy WB/negative recipes are unchanged.
Independent Double Bradford/cone/XYZ/normalization and legacy gain references
validate signed/HDR/partial-alpha pixels; 4097-node/three-channel/four-gamma
analytic references guard log inversion. Tests cover model upgrade, table order,
negative-before-WB/monochrome, neutral identity for all models, matrix ownership,
HDR exposure linearity, invalid controls/domain and explicit-film requirements.
Source/profile comparison results: **102 PASS** for the three cubes (two known
optional failures, six skips) and **104 PASS** for the three profiled JPEGs plus
RGB16 scan (two known optional failures, four skips). All required checks,
explicit-half fallback and GPU transport pass. All 12 cube and 15 matching
photographic legacy baseline hashes are unchanged; all seven neutral PNGs are
byte-identical to the prior checkpoint. All three negative-mono cube PNGs have
zero RGB channel spread. The galleries contain 42 cube and 56 photographic
previews/references, with all 85/113 local links valid. Peak process working set
was about **1.02 GiB** for the rendered-file run, including decode, old references
and output. The fused WB matrix is a concrete conversion reduction, not an
end-to-end UI speedup claim. Build: zero warnings/errors. No source changes were
made after these checks; the two known optional Skia subnormal failures remain
visible. This second step is ready for its local commit and joint visual review.

## Checkpoint: floating automatic film calibration

The preceding HSL/point-color and rendered-WB/explicit-negative galleries were
accepted. This next bounded CPU step completes automatic measurement in the
experimental rendered-file branch; it does not enable film handling in the editor.

### What changed and why

* **Native measurement input:** FloatingFilmMeasurement reads premultiplied
  linear Rec.2020 F16 rows, removes coverage, and explicitly projects into bounded
  linear sRGB for the existing scan-calibration model. This is an intentional
  SDR scan operator, not scene-linear RAW density estimation. Signed/out-of-sRGB
  samples are bounded only for measurement/inversion; the source stays immutable.
* **Grain-resistant measurement:** every visible source pixel contributes to a
  linear-light block mean, using the existing approximately 250k-block sampling
  policy and alpha cutoff of 8/255. Blocks have equal percentile weight;
  coverage above that cutoff is not a density weight. Only the block means are
  encoded to sRGB. Fractional 0.5/99.5-percentile values replace 256-bin integer
  histograms, avoiding the old calibration quantization. Partial right/bottom
  blocks participate. No full-resolution F32 or RGB8 analysis image is needed.
* **Holder exclusion:** retain the old interior-relative, encoded-BT.601 dark-edge
  heuristic, 15% maximum removal per side, and narrow blurred-edge seam.
  The detector now uses the same block means rather than top-left pixel samples;
  normalized thresholds are continuous rather than integer-rounded. This is a
  deliberate robustness change, not byte-identical detection. Only the measurement
  rectangle changes; the image is never cropped. Dark subjects extending into the
  interior and bright unexposed film borders remain part of the measurement.
* **Fractional calibration through inversion:** an immutable measurement snapshot
  constructs measured RenderedNegativeSettings, retaining Double base/density
  values without a conversion through SKColor. Explicit legacy swatches still
  use their accepted original table builder. Measured calibration uses the same
  log-density/gamma law and 4097-node interpolation, with a positive floor of
  1/65535 rather than one RGB8 code. This preserves deeper density and permits
  sub-byte calibration spans. The fixed lookup-table spacing is still a precision
  limit for exceptionally narrow spans; this is not unlimited-precision inversion.
* **Explicit failure:** no visible samples fail measurement; insufficient density
  span in any channel fails inversion settings. An arbitrary white/black fallback
  would conceal invalid calibration. Caller-supplied snapshots avoid remeasurement
  on every render and can later keep preview/export calibration consistent.

### Evidence and current visual gate

Final build: **zero warnings/errors**. Numerical suite: **87 PASS**, two unchanged
optional Skia F16 subnormal failures, four skips. All required checks, explicit-half
fallback and GPU transport checks pass. Independent Double tests cover odd-sized
1003x1001 linear block integration, fractional nearest-rank percentiles, alpha,
holder seams, dark subjects, bright film borders, ownership, empty/scene/zero-span
rejection, a density span below one RGB8 code, and analytic measured log tables at
all 4097 nodes for three channels and three gamma settings. Existing frozen legacy
WB/negative and other recipe fixtures remain unchanged.

The three supplied wide-profile JPEGs plus RGB16 TIFF yield **111 PASS**, two known
optional failures and four skips. The three profile cubes yield **108 PASS**, two
known optional failures and six skips. All required and transport checks pass.
All **76 photographic and 58 cube PNGs** present in the prior rendered-input
checkpoint are byte-identical, covering neutral output, explicit film/WB recipes
and old references. New galleries contain 68/51 previews and all 137/103 local
links resolve. Peak process working set: approximately **1.09 GiB** for the
rendered-photo run and **405 MiB** for cubes; these include decoding, old references,
all recipe renders and PNG output, not isolated analyzer allocations or timings.

Review the new rendered-input-review.html in the diagnostic output folders
f16-film-measurement-four and f16-film-measurement-cubes. Focus on auto,
auto-mono and the old auto reference: smooth tones, grain, deep density,
highlight transitions and holder edges. The TIFF measurement excludes a rectangle
of edge strips, giving area x=0,y=112,width=2952,height=4392 with block size 8 and
202581 visible blocks. The supplied TIFF is already a positive image; its inverse
therefore looks negative. These previews validate comparison behavior, not a
finished negative-development workflow. Real negative-scan photographic acceptance
still needs an actual negative source.

### What remains

Geometry/crop must be applied before measurement by the future coordinator;
this API requires an already corrected frame and does not migrate geometry.
Eyedroppers, persistence/UI representation of fractional calibration, optional
partial manual overrides, RAW-negative preparation/WB/exposure order and preview/
export routing remain. There is no GPU measurement kernel or new pipeline setting
in this checkpoint. Spatial effects, denoise/sharpen, vignette/grain, extensions,
LUTs, masks, production monitor/output handling and the default-off Pipeline
settings category remain separate migration work. The normal editor is unchanged.

## Checkpoint: native geometry and linear compositing

This iteration addresses two remaining precision boundaries and adds actual GPU
blend kernels. It does **not** finish the full editor migration. The accepted
automatic film measurement is unchanged. All work remains diagnostic/default-off.

### Ordered geometry: what and why

`WorkingGeometry` accepts either scene-linear or rendered-linear Rec.2020 F16.
It snapshots only the existing geometry-operation fields, including the shared
recipe-to-preview pixel-size scaling, and keeps operations in their recorded order:
crop, quarter-turn/flip/straighten, perspective, mesh, resize and canvas additions
are processed where their entries occur. A later canvas cannot restore a crop.
Unknown operation kinds and nonfinite controls fail explicitly. Source frames and
recipe objects remain owned by the caller; the returned frame has separate ownership.

Size, crop, perspective, rotation, straighten and mesh-coordinate conventions reuse
the editor's helpers. Quarter-turns/flips/crops preserve stored values exactly,
including the smallest F16 subnormal. An unchanged mesh and an empty recipe avoid
resampling. Fractional matrices and moved meshes use bilinear premultiplied
interpolation in linear Rec.2020. Uncovered pixels stay transparent; points behind a
projective horizon are not clamped onto an image edge. A new allocation-free
`ReadLinearPixel` reads half bits directly, avoiding the measured Skia CPU F16 loss.

Resize integrates source pixel areas exactly on each reducing axis and uses linear
reconstruction with edge extension on each enlarging axis. Kernels are chosen
independently for mixed-axis resize. Weights and accumulations are Double, with F32
rows and explicit F16 storage at each actual operation boundary. Averaging black
and white therefore gives **0.5 linear**, instead of averaging encoded codes.
No primaries transform or gamma conversion is needed for geometry, and RGB is
never clamped to alpha or SDR white. This is an intentional interpolation change
from the legacy encoded-sRGB Mitchell renderer, not a promise of pixel parity.

Limits: projective/mesh minification still needs a footprint-aware wider filter or
mipmaps; bilinear alone can alias strong reductions. Geometry is CPU-only and has
not been connected to selection/ROI/editor caches. Lens-distortion remaps and
sensor/aspect crops still require their own correct RAW-stage coordination.
Document background filling is kept separate from scene-light geometry.

### Mask/normal blending and backgrounds: what and why

`WorkingCoverage` snapshots validated normalized F32 coverage. `WorkingComposite`
mixes complete original/adjusted results using that coverage and opacity, or applies
normal premultiplied Porter-Duff source-over for layers. These are deliberately
different equations: mixing two results with alpha 0.5 keeps alpha 0.5; source-over
would increase it to 0.75. RGB stays linear Rec.2020 with signed/HDR range retained;
blending uses Double accumulations and one final F16 packing, without RGB8 staging.
Dimensions and scene/rendered domains must match. Coverage images are metadata, not
RGB images, and are not passed through color transforms.

Rendered document backgrounds decode the encoded-sRGB UI color once, transform
that constant into Rec.2020, premultiply it, and fill only uncovered area. This
prevents alpha fringes and gamma-space energy errors without converting the image
through an 8-bit color type. Scene-radiance background filling is rejected because
a display/document color is not a RAW scene-light contribution.

This implements the blend mathematics, **not** the entire mask/layer subsystem:
mask rasterization, geometry alignment through the editor, feathering, cached mask
payloads, objects, selection tools, per-layer rendering and artistic blend-mode
contracts remain. Existing artistic modes are not silently changed to linear normal.

### Actual GPU blend implementation and limits

`WorkingCompositeGpu` implements normal source-over and unmasked result mixing,
using native F16 input snapshots, float shader arithmetic and an unrestricted F16
target. There is no byte rounding, gamma conversion or SDR clamp in the shader.
F32 readback plus explicit half packing avoids the CPU Skia subnormal conversion.
It runs explicitly on the existing GPU worker; input frames must stay alive until
the work completes. A future asynchronous coordinator must retain snapshot ownership
through timeouts/cancellation, as other GPU work does. No editor dispatch is enabled.

F32 coverage masks remain CPU-only. The measured backend cannot be assumed to
upload arbitrary F32 textures faithfully; silently storing masks as half or bytes
would change their precision. This conservative boundary avoids pretending that
GPU transport compatibility also guarantees arbitrary mask input support.

The real GPU kernels pass **18624 channel comparisons** against the explicit-half
CPU implementation across both domains, opacity 0/0.17/0.5/1, partial transparency,
signed/HDR values and shadows down to 2^-24. Maximum absolute error is 0.0078125
in HDR samples, within the 0.2% relative half-precision budget plus a small
operand-scaled F32 cancellation allowance and two half-subnormal steps. This is an
algorithm check on the measured AMD/ANGLE backend, not a full-image speed benchmark
or validation of other effect kernels/backends. Most GPU migration remains.

### Verification and visual review

Final build: **zero warnings/errors**. Final combined numerical suite: **95 PASS**,
two unchanged optional Skia F16 subnormal failures and four skips. Every required
check passes, including explicit-half CPU fallback, GPU transport and the new GPU
blend kernels. Independent integer and Double references cover exact permutations,
crops, alpha, noninteger/anisotropic area resize, subnormals, source/recipe ownership,
crop/canvas order and invalid controls. Coordinate-field tests compare moved mesh,
perspective and straighten against the existing mask/overlay inverse mapping;
these complement independent sampling references and do not independently prove
the editor's shared perspective model. Separate Double references validate mask
mixing/source-over and sRGB UI-color decoding/matrix/background compositing.

The geometry gallery runs, before adding the subsequent composite checks, recorded
**121 PASS** for six photographs and **106 PASS** for the three profile cubes.
Composite gallery runs recorded **124 PASS** and **109 PASS**, respectively, before
adding the final GPU kernel check. Each retains only the two known optional failures
and four/six skips. All required checks and measured transport passed in every run.
The final numerical suite above includes all new CPU and GPU checks.

Across both groups, all **28 matching photographic and 16 matching cube legacy
PNG baselines** are byte-identical to their previous checkpoints; all six/three
neutral previews are also byte-identical. Geometry galleries contain 78/39 previews
and all 157/79 links resolve. Composite galleries contain 36/18 previews and all
73/37 links resolve. Peak working sets, including decoding, old references, all
recipes and PNG output: geometry approximately **1.69 GiB / 402 MiB** for photos /
cubes; composite approximately **2.09 GiB / 416 MiB**. These are not isolated kernel
memory or throughput measurements. Preview output remains diagnostic SDR sRGB.

Open `f16-geometry-composite-review.html` in the diagnostic workspace for all four
galleries. Check reduced fine detail and bright edges, crop/rotation order,
perspective/mesh and expanded/cropped straighten, transparent canvas boundaries,
smooth original-to-adjusted mask ramps and white/blue document backgrounds.
The operator implementations pass their numerical gates; visual acceptance is open.

### Remaining work toward the complete pipeline

The remaining inventory at the top remains authoritative. Next large groups are
spatial detail/denoise/sharpen and vignette/grain/depth effects, extensions/LUT and
preset stage contracts, full mask/object/layer orchestration, broader profiled
decoding, negative/legacy RAW routing, color-managed display/output and exports,
remaining GPU kernels and performance/cache integration. Then connect the default-off
**Pipeline** category between Editor and Raw development, keeping legacy rendering
isolated and covering saved recipes, thumbnails, previews and exports consistently.
The new services are still isolated; the normal editor has not switched pipelines.

## Definition of complete

The migration is complete when the production editor's intended high-precision
chain uses the documented working contract, its remaining format/space
boundaries are intentional, current tools/extensions and saved recipes have an
explicit compatibility strategy, preview and export agree, required CPU/GPU
fallbacks pass, and realistic memory/performance behavior is acceptable.

The present diagnostic services and accepted preview slices are evidence toward
that goal. They are not a declaration that the complete implementation is done.
