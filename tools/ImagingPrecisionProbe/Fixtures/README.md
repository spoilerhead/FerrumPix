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
