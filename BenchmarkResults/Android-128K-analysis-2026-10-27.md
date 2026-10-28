# Android 128K capture analysis — 2026-10-27

## Evidence and limitations

Preserved the user's current Editor Profiler buffer before any reload as
`android-128k-user-2026-10-27.data` (28,819,108 bytes). Frames 6453–6752.
`android-user-capture-extracted.json` contains CPU inclusive/self marker totals
and call counts for the main/render threads. `android-user-capture-counters.json`
contains rendering, allocation and buffer counters.

The buffer mixes an earlier Windows Editor comparison capture (6453–6547) with
Android/Vulkan player frames. These must not be averaged together. The Android
player has no usable GPU timestamp samples: GPU time is zero/unavailable, not
zero work. The user identifies the player run as 128K; recorded geometry supports
this (about 527,680 triangles, consistent with two 2-triangle passes per particle).
The old build did not record particle/grid/quality samples. A reset occurs at
6650 and another incomplete/transition frame at 6552. Exact changed settings at
6650 cannot be recovered confidently. The phone was disconnected during analysis.

## Measured results

Selected 97 complete four-tick frames in 6548–6646, excluding transition frames:

| Measurement | Result |
| --- | ---: |
| Mean frame | 551.148 ms (1.81 FPS) |
| Median / P95 | 552.440 / 601.816 ms |
| Main-thread Gfx.WaitForPresentOnGfxThread | 546.587 ms/frame (99.17%) |
| Four fixed-tick CPU submissions combined | 0.388 ms/frame |
| Compute dispatches | 168/frame |
| Actual submitted ticks | 7.258 ticks/wall-second |
| Simulated time / wall time | 0.121x |
| GC collection average | 0.018 ms/frame |
| Graphics buffer bytes | 28,900,944, stable |
| GC allocation | about 9.6–19.4 KB/frame |

The render thread spends almost all of the interval in GfxDeviceVK.Present. This
is a graphics/presentation stall, not proof that the CPU solver or a specific
compute kernel consumes that time. No synchronous diagnostic GetData hotspot was
found. Normal once-per-second asynchronous diagnostic readback is small on CPU.

After reset, frames 6653–6665 average 135.504 ms; later 6700–6751 average
459.707 ms. Both still submit four ticks/frame and maintain roughly the same
geometry and buffer sizes. The large within-preset variation deserves a
controlled replay at the same simulated time; it cannot be attributed solely to
particle count. Particle distribution/atomic contention, surface overdraw, GPU
frequency and presentation/driver behavior remain candidates, not measurements.

## Why a 32K→64K cliff is plausible

The scheduler retains elapsed wall time and requests up to four fixed 1/60 s
ticks per render frame. At 100 FPS, frames average 0.6 ticks if keeping up. Once
a tick cannot meet the 60Hz budget, backlog grows and the workload approaches
four ticks/frame: 8 substeps/tick become 32 substeps/frame. Thus render FPS does
not scale linearly with particles. The recorded 128K run confirms this saturated
four-tick path, but there is no controlled 32K/64K capture here to establish their
exact per-tick ratio.

32K→64K presets also change grid 48^3→64^3 (110,592→262,144 cells, 2.37x)
alongside 2x particles. 128K uses 80^3 (512,000 cells). Main compute has five
dispatches/substep; foam contributes two dispatches/tick, yielding
4*(8*5+2)=168 dispatches/frame, as measured.

## Code cost and optimization priority

1. The two P2G transfers perform 27*(4+3)=189 global integer atomic additions
   per particle/substep. At 128K and eight substeps: 198,180,864 additions/tick,
   792,723,456/frame at four ticks, excluding diagnostic atomics. Prior RTX GPU
   timestamps put the transfers at about 89.8% of solver time; this is a desktop
   result and does not establish Android's stage split. Tile/shared-memory
   aggregation plus spatial binning is the primary algorithmic optimization
   candidate if phone GPU counters confirm transfer bottlenecks. Sorting and
   overflow/fault behavior must be accounted for in its total cost.
2. Two diagnostic maxima make every particle contend on the same two addresses.
   Tried exact workgroup reduction: at 128K this cuts those global maxima from
   2,097,152 to 16,384 per tick. Interleaved RTX experiment measured baseline
   6.590 ms/tick and candidate 6.686 ms/tick (about 1.5% slower, within variability).
   All final snapshot fields were identical and 31 regression tests passed.
   Restored original shader; do not claim a performance improvement. Candidate
   saved under Temp for possible Android-only experimentation. No unproven shader
   optimization is shipped in the updated diagnostic APK.
3. Surface depth/thickness render every primary particle, then full-screen
   smoothing/normal/shading and foam. Rendering OFF and 720p→540p at the same
   simulation state distinguish solver work from fill/overdraw. CPU render
   marker durations do not measure their GPU costs.

## Diagnostic changes

Kept the original shader, default eight substeps, four catch-up ticks and no FPS
cap. Added Catch-up 4t/1t control, visible ticks/frame and sim/wall ratio, saved
window tick rate, and per-frame Profiler configuration samples for particle/grid/substep and
render/foam settings. No simulation time is dropped and physical dt is unchanged.

## Next controlled phone run

At each of 32K/64K/128K reset, keep eight substeps and identical initial state:
record Catch-up 4t and 1t; Rendering OFF and ON; Foam OFF and ON; 720p and 540p.
Compare the same simulation-time window and report both render FPS and tick Hz.
1t isolates scheduling amplification; it is not a promise of real-time speed.
If solver-only remains slow, obtain GPU compute-stage timings with a compatible
device GPU profiler before choosing P2G tiling or another solver architecture.

## Verification

Final original-shader regression suite: 31/31 EditMode tests passed. Updated
controller compiled without errors, ran on RTX 5070 Ti, and captured a UI preview.
Injected 0.1 seconds of backlog in Play Mode: the 1t setting submitted exactly
one 1/60-second tick; 4t submitted exactly four, both retaining positive backlog.
The saved 600-frame desktop sample reported 59.72 submitted ticks/second and
0.999 sim/wall ratio with uncapped rendering, demonstrating that high render FPS
does not accelerate the physical clock. This does not validate phone performance.

Updated diagnostic APK: C:\Users\Ayinzhang\Downloads\1.apk; Succeeded | errors 0 | warnings 0; 36929542 bytes. Original APK retained at C:\Users\Ayinzhang\Downloads\1-before-20261027-231548.apk. Phone disconnected, so no Android speedup claim.

Reloaded the saved original capture into Profiler after verification (Unity reindexes the imported 300 frames to 0-299; report references retain original frame IDs). APK metadata confirms Catch-up control, MPM Config samples and submittedTicksInWindow fields are present.
