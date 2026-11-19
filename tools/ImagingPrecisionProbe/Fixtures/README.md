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
