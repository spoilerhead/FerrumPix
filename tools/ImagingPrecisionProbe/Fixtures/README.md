# Calibration legacy reference

`calibration-legacy.json` records Windows x64 CPU output hashes captured from the
accepted pre-calibration-migration application binary (source checkpoint
`074eea0d`) before rebuilding the shared shadow-tint extraction. It guards the
original editor, not the new F16 appearance. Other platforms skip this byte-exact
fixture while still running numerical F16 references.

The input is a 64x32 BGRA8 premultiplied opaque bitmap. At `(x,y)`, blue is
`y*255/31`, green `x*255/63`, red `(x+y)*255/94`, using integer division. Alpha is
255. Each recipe uses WB model 4, anchor xy `(0.312727,0.329024)`, and otherwise
default adjustments plus the one named property/value in the JSON. Render through
`ImageProcessor.RenderPreviewSkBitmap` with GPU disabled and hash the native
output bytes with SHA256. No photographs or external profiles are embedded.

Regenerate only against an explicitly accepted legacy behavior change; replacing
the expected hashes with current outputs would hide regressions in shared code.


# Full basic-control legacy reference

`full-basic-legacy.json` contains 16 Windows x64 CPU hashes captured from accepted
`9930ef60` BEFORE the histogram-summary, vibrance and color-matrix extractions.
It covers both signs of highlights/shadows/whites/blacks/saturation/vibrance,
neutral, full desaturation with maximum vibrance, and mixed tone/all-control
recipes. `FullBasicChecks.Synthetic()` defines the 64x48 opaque BGRA8 spatial/color
input; `Recipes` defines exact values. Hashes cover native output bytes from
`ImageProcessor.RenderPreviewSkBitmap` with GPU disabled. No photographs are stored.

The explicit `--capture-full-basic-fixture <path>` maintenance command must run
against an **accepted pre-change binary**, not be used to make a failing current
build pass. Other platforms skip only this frozen Windows fixture; floating
analysis/legacy-oracle and independent color/range tests still run.


# Tone-curve legacy reference

`curves-legacy.json` contains ten Windows x64 CPU hashes captured from accepted
`35162ddc` before curve service changes. `RenderedCurveChecks.Recipes` defines
master, individual channels, HSL-lightness, interior endpoints, combined curves,
mixed basic controls and desaturation/vibrance/master recipes. The opaque
64x48 input is `FullBasicChecks.Synthetic()`; hashes cover native output bytes
from `ImageProcessor.RenderPreviewSkBitmap`, with GPU disabled.

The maintenance flag `--capture-curves-fixture <path>` must run against an
accepted pre-change application binary. Do not regenerate a failing fixture
against changed services. Non-Windows/x64 runs skip only the byte-exact fixture,
not independent curve, range, alpha or floating legacy-oracle checks.


# Late color / creative point reference

`late-color-legacy.json` contains twenty Windows x64 CPU hashes captured from
accepted `45de8b6a` before extracting the channel/map and palette kernels.
`LateColorChecks.Recipes` defines four-zone grading/luminance/balance/blending,
both calibration shadow-tint directions, color/monochrome channel mixing,
partial/full gradient mapping, posterize, threshold, map-before-palette and
combined basic/curve/color recipes. Input: `FullBasicChecks.Synthetic()`; hash
native bytes from `ImageProcessor.RenderPreviewSkBitmap` with GPU disabled.

The maintenance flag `--capture-late-color-fixture <path>` must run against an
accepted pre-change application binary. Never replace a failing fixture with
hashes from changed services. Other platforms skip only this frozen byte fixture;
numerical/range/alpha and bounded legacy-oracle tests still run.


# HSL/point-color and rendered-input baselines

`hsl-point-legacy.json` records 29 Windows x64 recipes from accepted `5f403cf7`
before extracting the shared HSL/point-color block. It covers all eight bands on
all three axes, point selections/visualization, curves/grade order and gray bands.
The old guide remains its original Skia RGB8 blur (sigma 1.5). Numerical no-guide
oracles isolate the unchanged color math from the new full-precision Gaussian.

`rendered-input-legacy.json` records ten recipes captured in that same accepted
binary before either new step: legacy encoded WB, relative/absolute Bradford WB,
model-2 load-time upgrade, and explicit color/monochrome negatives with WB after
inversion. It is prepared ahead of the second step, not a completed implementation.
`HslPointChecks` / `RenderedInputChecks` define the recipes, using the common
64x48 `FullBasicChecks.Synthetic()` image and GPU-disabled native output hashes.
`StageLegacyFixture` only shares capture plumbing, not expected numerical results.

Maintenance flags `--capture-hsl-point-fixture` and
`--capture-rendered-input-fixture` require an accepted pre-change application
binary. Do not refresh them to hide a regression. Other platforms skip only
these frozen Windows/x64 hashes, retaining independent numerical checks.
