# Desktop reference investigation — 2026-10-27

Goal: approach Zibra's desktop liquid appearance and throughput with the independent
MLS-MPM + whitewater implementation; validate that implementation on Redmi K90
Pro Max afterward. Android support in the imported Zibra package is not required.

## Actual measurement environment

- Unity 2022.3.62f3, Windows Editor, Direct3D11, **Intel UHD Graphics**.
- These results do not describe the laptop's discrete GPU or the phone.
- 32,768 primary particles, 48^3 grid, fixed 4m box and initial water block.
- Enabled whitewater uses 32,768 secondary slots.
- Timings synchronize completed solver work in four-tick batches; CPU submission
  and a 32-byte diagnostic readback are included. Rendering is excluded.
- Warmup: 12 ticks. Timing window: next 60 ticks. Validation: 600 ticks (10s),
  with kicks at seconds 1, 3, 5, 7 and 9. The later kicks are outside the timing window.
- One run per configuration, executed sequentially. Thermal/clock variation and
  readback overhead remain; these are exploratory results, not certified speedups.

| Substeps | Whitewater | Completed tick (ms) | Simulated time / wall time | Faults | Invalid secondary particles |
|---|---|---:|---:|---:|---:|
| 4 (experimental) | On | 17.05 | 0.978 | 0 | 0 |
| 6 (experimental) | On | 26.84 | 0.621 | 0 | 0 |
| 8 (default) | On | 36.49 | 0.457 | 0 | 0 |
| 8 (default) | Off | 32.48 | 0.513 | 0 | 0 |

The observed whitewater difference is about 4ms per completed tick in these
separate runs; it is not a GPU marker measurement. Four substeps are a promising
performance candidate but change the numerical solution. Final equivalence of
waves, thin sheets, collisions and whitewater has not been established.
Four-substep peak density was 2.117x rest density; six-step 2.373x; eight-step 2.153x.
These values require visual/physical judgment even though no fault threshold fired.

Earlier JSON files without `substeps` in the filename are a shorter four-second
validation: original shader, a slower group-reduction experiment, and an
inconclusive atomic-guard experiment. Both shader experiments were reverted.
The final solver math, diagnostics and default substeps are unchanged.

## Next comparisons

1. Capture Zibra and MLS-MPM at matched physical volume, camera, simulation time,
   collision trajectory and output resolution.
2. Compare 4 vs 8 substeps visually before promoting the experimental setting.
3. Profile solver stages and surface reconstruction on the actual target desktop
   GPU; compare simulation-only and complete rendered workloads separately.
4. Validate on K90 Pro Max with fixed internal resolution and sustained runs.
   Do not extrapolate these Intel Editor timings to the phone.
