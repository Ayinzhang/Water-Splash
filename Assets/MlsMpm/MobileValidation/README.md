# Android Profiler validation

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
`mpmHeight`; float extra `mpmRenderScale`. These allow reproducible launches
without touch injection and apply only when starting the player.
