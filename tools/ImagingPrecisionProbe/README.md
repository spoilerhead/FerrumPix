# Imaging precision probe: phases 0 and 1

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
