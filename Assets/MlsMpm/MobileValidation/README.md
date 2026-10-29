# Android Profiler validation

## Bottle Voyage showcase

Open **Water Splash > Open Bottle Voyage** or
`Assets/MlsMpm/BottleDemo/BottleVoyage.unity` for the fixed horizontal glass
bottle, linen-sailed wooden boat and sunset still life. This separate scene uses
unsorted two-pass MLS with scene-local curved bottle contacts: default 32,768
particles, 80^3 storage, eight substeps and 60 Hz simulation. The reduced water
volume supports finer particles; the original profiler/PB scene solvers remain
unchanged. See `BottleDemo/README.md` for the shared glass/collision profile.

Pick and drag the boat, rather than empty water. Space pauses; R or **Begin again**
resets. A simplified sphere follows the boat's submerged hull and moves the water.
The sailboat follows five real GPU particle-height probes with smoothed
heave, current-driven horizontal drift and a maximum 18-degree visual tilt.
This is not a full rigid-body hull solve. Probes use two read-only
reductions and an asynchronous **80-byte** readback at up to 10 Hz; no full
particle download or per-frame CPU/GPU synchronization is used.

The showcase has its own pipeline/materials and a smaller 8K whitewater pool;
the bottle uses High surface quality. Buttons select 16K/32K/64K while preserving
total represented water volume, or individual particles versus reconstructed water.
Changing count resets the run; changing display mode preserves it. Grid and
substeps stay fixed across those presets. The original profiler scene, PB-MPM scene,
Android build menu and installed APK are not replaced. No phone performance
claim is implied by running this scene in the Editor.

The accepted MLS baseline is still unsorted. Earlier device A/B runs showed
time-dependent P2G atomic locality loss as particles mix; restoring that baseline
also restores that cost. This is a known contributor, not proof of the complete
cause of a new 100-to-60 FPS observation. Compare early/late solver timings,
render/whitewater costs, catch-up ticks and device clocks before attributing the
entire change to sorting or thermal throttling. No sorting/fusion experiment is
silently re-enabled in the showcase.

## Profile scene

Open `Scenes/AndroidProfile.unity`. This is an independent MLS-MPM scene with its
own URP renderer/pipeline; Zibra native components and comparison scripts are not
included. Default: 32K particles, storage48^3, 8 substeps, adaptive resolution High,
continuous foam on. Buttons select 8K–128K, substeps, resolution, surface quality, foam, drawing,
and deterministic automatic stirring. Drag the sphere below the panel.

Resolution follows the actual safe-area viewport below the controls, including
aspect ratio changes. Auto 100% uses one target pixel per viewport pixel; the
resolution button cycles Auto 100/75/50%, fixed 720p, fixed 540p. This is viewport
adaptation, not an automatic FPS governor, so profiling does not silently change
quality. The camera projection follows the target aspect. A separate empty black
camera explicitly clears the display swapchain before IMGUI composites the water;
the RT camera alone does not clear exposed Android screen edges.

Physics advances in fixed 1/60 second ticks. Render frames may contain zero or
several ticks. Up to four ticks are submitted per rendered frame; any remaining
elapsed time is retained as visible backlog. Pause/app suspension excludes the
paused duration. Do not interpret high FPS with growing backlog as real time.
`Catch-up 4t/1t` switches the maximum submitted ticks per render frame and resets
the run. Default remains four. The one-tick setting isolates catch-up amplification;
it keeps the same 1/60 second physical tick and all backlog, so higher displayed
FPS alone is not a solver speedup. The panel shows ticks/frame and sim/wall ratio.

Desktop uses targetFrameRate=-1 and vSync=0. Android requests 1000 FPS because
Unity's mobile -1 default is 30 FPS; the OS/display can still constrain presentation
to a refresh-rate divisor. This removes the app's 60 FPS cap, not the physical
display ceiling. Simulation speed never depends on rendered FPS. For remaining
GPU headroom, inspect stages in Profiler rather than inferring it from displayed FPS.

Build menu: **Water Splash > Android > Build Development APK To Downloads**.
The output is `%USERPROFILE%/Downloads/1.apk` (or WATER_SPLASH_DOWNLOADS override).
An existing 1.apk is copied to a timestamped `1-before-*.apk` before overwriting.
Build settings: IL2CPP ARM64, Vulkan, min Android10/API29, Development + Autoconnect
Profiler, no Deep Profiling, no script debugger, debug signing. Only this scene
is included. Build status is in `BenchmarkResults/android-build.json`.

Install `1.apk`, enable Android USB debugging and authorize the computer. In Unity,
open **Window > Analysis > Profiler**, choose the Android player in the target
selector (USB), and enable Record. Development instrumentation has overhead; this
package is for diagnosis, not release acceptance. GPU module availability depends
on Unity/device/API support; no claim that Vulkan GPU timestamps will be available
on every phone. Use the CPU Timeline and external GPU tools if unavailable.

Markers: `MLS-MPM/Fixed 60Hz tick` is CPU submission, **not GPU duration**.
GPU markers: `MLS-MPM GPU/Clear`, `Mass + momentum`, `Stress`, `Grid velocity`,
`G2P + advection`, `Whitewater`. Rendering: `MPM Render/Depth`, `Thickness`,
`Smoothing`, `Normals`, `Foam`, `Shading`, `Spray`. At high display FPS, frames
without a physics tick are expected. Aggregate solver costs over actual ticks.

Save stats writes device/API/settings, mean/P95 of the latest600 render frames,
wall/simulated seconds and backlog to Application.persistentDataPath, and prints
the complete path and JSON to the Unity device log. Android package name:
`com.watersplash.mpmprofile`. No device installation is performed by the build menu.
Stats also contain the catch-up limit, submitted ticks in the window and tick Hz.
CPU Profiler records a cached `MPM Config/...` sample naming particle count,
grid, substeps, rendering, foam and catch-up settings on each frame. Tick counts
come from `MLS-MPM/Fixed 60Hz tick` calls. This identifies preset boundaries
without requiring the separate Unity profiling extension package.

Development players refresh `profile-live.json` in persistentDataPath every five
seconds, with the same settings/window timing plus screen size and render scale.
This low-frequency telemetry performs no GPU readback. USB access: use `adb shell
run-as com.watersplash.mpmprofile cat files/profile-live.json`.
Development Android launch intent integer extras: `mpmParticles`, `mpmSubsteps`,
`mpmCatchUpTicks`, `mpmRendering`, `mpmFoam`, `mpmAdaptiveResolution`, `mpmWidth`,
`mpmHeight`, `mpmSort` (PB-MPM-only spatial sort interval in ticks, 0 = off),
`mpmSolver` (0 = MLS, 1 = PB-MPM), and
`mpmIterations` (1..16 PB constraint iterations per substep);
float extra `mpmRenderScale`. These allow reproducible
launches without touch injection and apply only when starting the player.

## Restored MLS baseline

MLS uses the original, unsorted two-pass P2G implementation from `a0f6ee84`,
before the October 28 sorting/fusion experiments. The original compute shader
bodies (including whitewater) are unchanged; PB-MPM is a separate dispatch path.
The experimental fused-pressure kernel, switch and `mpmFused` extra have been
removed, rather than merely disabled by default. Sorting is available only in PB-MPM.
MLS retains its original sound speed, pressure law, viscosity, zero extra damping,
fixed 60 Hz ticks and 8-substep phone default. No speed clamp, new damping, time
stretching or dropped backlog was introduced to mask visible jumps.

The rollback follows the user's visual acceptance of the earlier MLS version.
Numerical motion measured in separate diagnostic pool tests is not by itself
evidence of the visible discontinuity reported for the experimental versions.

## PB-MPM comparison

Open `Scenes/PbMpmComparison.unity` for the same scene, camera, water surface,
foam and sphere with PB-MPM selected. The original Android scene and build default
remain MLS-MPM. `Try MLS-MPM` / `Try PB-MPM` switches solver and resets to identical
initial conditions; `PB iter` cycles 1/2/4/8 constraint iterations. Both use the selected
substep count and fixed 60 Hz ticks: an iteration is not another physical substep.
The benchmark uses 8 substeps throughout; it never stretches dt or drops backlog.

This is a liquid-only 3D adaptation of
[Chris Lewin / EA, A Position Based Material Point Method (SIGGRAPH 2024)](https://github.com/electronicarts/pbmpm/tree/siggraph2024).
Each substep predicts displacement, alternates local volume projection and
P2G/grid collision/G2P at fixed particle positions, then integrates once.
The 3D hydrostatic projection divides its trace correction by 3. Integrated volume
J travels inside the sorted particle state. Defaults: 2 iterations, relaxation 1.5, per-iteration viscosity 0.01,
compressed grid-volume blend 0.1. The reference's J floor of 0.1 is retained;
snapshots expose minimum/maximum J and the number of particles at that floor.
Nonfinite state, nonpositive integrated J and excessive displacement set GPU faults.
Grid/particle velocities are restored to velocity units for the shared whitewater.
No elastic, granular or viscoplastic materials are implemented.
The adapted shader's BSD-3-Clause notice is in `Shaders/PbMpm-LICENSE.txt`;
retain it in source and binary distribution documentation.

Run **Water Splash > Benchmark > Run PB-MPM Desktop Comparison** in Edit Mode.
It writes `BenchmarkResults/pbmpm-original-mls-comparison.json`. Each pair uses identical
particle positions, spacing and sphere motion. GPU-completed wall time includes
submission/readback overhead, excludes rendering and is not a GPU timestamp.
Whitewater is disabled in this solver benchmark, but enabled in the comparison scene.

Historical experiment before the rollback: RTX 5070 Ti Laptop / D3D11,
8 substeps, 2 PB iterations. These figures compare the removed fused MLS
experiment, not the restored MLS baseline. Original data remains in
`BenchmarkResults/pbmpm-comparison.json`.

| Case | MLS fused | PB-MPM |
|---|---:|---:|
| 32K resting pool, RMS speed at 20s | 0.0884 m/s | 0.0045 m/s |
| 64K resting pool, RMS speed at 20s | 0.6161 m/s | 0.0883 m/s |
| 32K dam break + sphere, completed ms/tick | 1.53 | 3.39 |
| 64K dam break + sphere, completed ms/tick | 3.05 | 6.01 |
| 128K dam break + sphere, completed ms/tick | 5.40 | 11.87 |

These are desktop measurements, not Android predictions or current-baseline timings.
PB reduced measured resting motion relative to the fused experiment but was roughly
twice as expensive; 64K still had residual motion. The pool tests run for 20 simulated seconds;
impact tests run for 5 seconds, not a long-duration stability guarantee. All paired
cases finished without faults, nonfinite/out-of-bounds particles or hitting the J floor.
