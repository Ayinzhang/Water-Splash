# Three-dimensional weakly compressible MLS-MPM

Unity 2022.3 / URP 14 experimental GPU liquid. Intended first for Redmi K90 Pro Max,
then Redmi K50. No phone performance has been measured yet.

Open `Assets/MlsMpm/Scenes/MlsMpmDemo.unity`, or use
**Water Splash > Open MLS-MPM Demo** to create it if missing, then enter Play Mode.
The original SampleScene is not replaced. The demo temporarily selects a 60 FPS
cap and disables v-sync, restoring the previous settings when disabled.

## Controls

- Pause / Run, Reset, and Push are available in the on-screen panel.
- Drag below the panel to move the sphere horizontally. Sphere interaction is
  one-way: GPU fluid does not apply forces to a Unity Rigidbody. Sphere movement
  is advanced on simulation ticks, so a slow render frame does not teleport it.
- Select 8K, 16K, 32K, 64K or 128K particles to rebuild the solver.
- Rendering OFF isolates simulation from both rendering modes.
- Surface / Particles switches between continuous water and the particle debug view
  without resetting the solver. Continuous water is the default.
- Water: Low / Medium / High controls surface resolution and smoothing.
- Whitewater ON / OFF independently toggles secondary spray/foam simulation and
  display. Rendering OFF alone still advances both simulations.
- Space pauses; R resets. Increase Substeps in the Inspector for stronger forces.

The frame time/FPS display is **not GPU solver time**. Use the Unity GPU Profiler
and the `MLS-MPM/*` markers for stage costs, where supported by the device/backend.
Backlog is retained and shown: a fast display with a growing backlog is not a
real-time simulation. Application suspension and explicit pause do not accrue time.

## Algorithm and limits

The single-pool comparison defaults to 8 substeps (separate from this demo's
conservative per-preset recommendations). The RTX 5070 Ti Laptop / D3D11 passed
a 20-second 128K/80^3/8-substep moving-sphere run at ~60 FPS average, 720p High.
P95 frame time was 24.02ms. This is an Editor result, not an Android guarantee.
See `BenchmarkResults/RTX-Mobile-2026-10-27.md`.

Advected surface foam uses a continuous density field made from overlapping
flow-aligned footprints, depth-aware filtering and fine world-space detail,
instead of showing each foam seed as a round white sprite. Spray remains ballistic.
The renderer feature's `continuousFoam` toggle preserves the original rendering
for same-state comparisons. No extra whitewater particles or neighbor search are
introduced. Optional experimental affine/divergence damping is zero by default.

Particles store position, velocity and the three rows of the affine velocity
matrix, using five aligned float4 fields (80 bytes). Quadratic B-spline transfers
sample 27 grid nodes. Each substep clears the grid, transfers mass/momentum,
estimates particle density from that mass field and transfers pressure/viscous
stress, updates grid velocity, then performs G2P and advection.

Particle mass is one. Initial spacing determines rest density; changing the
particle preset does not simply pack more mass into unchanged particle spacing.
Pressure uses a cubic equation of state with nonnegative pressure and an exposed
rest sound speed. This is not an incompressible solver or a surface-tension model.
The setup is a visual experimental liquid, not an engineering fluid measurement.

Mass and momentum use separate integer fixed-point scales (65536 and 4096).
Every atomic addition checks signed overflow. Invalid values, invalid interpolation
support, density above four times rest density and motion over 0.75 cell/substep
set sticky GPU fault bits and stop subsequent simulation kernels. Diagnostics
are read back asynchronously about once per second, so the UI may report the
fault later than the GPU stops. On unsupported devices, the explicitly warned
fallback is one synchronous diagnostic readback per second. Reset clears faults.
No wave/subgroup operations, geometry shaders or float atomics are required.

The speed guard is an emergency fault detector, **not** an adaptive acoustic CFL
guarantee. Stiffer water and finer grids can require more substeps. Integer
quantization, compressibility and particle/grid collision discretization remain
sources of error. Diagnostic checks also have a runtime cost.

The container outline and floor are visual; collisions are analytic in compute
shaders. The simulation transform must have identity rotation and unit world scale. Its position is the
usable box minimum; Domain Size is its physical side length. Two lower and three
upper guard cells lie outside that box, so changing grid resolution does not move
the physical walls or floor.
The initial physical water block is also unchanged across grid resolutions.

## Higher-resolution simulation

Particle buttons select paired simulation settings rather than only adding
particles to the old grid. The new demo default is **32K / 48^3 / 8 substeps**.

| Particles | Simulation grid | Substeps per 1/60-second tick | Whitewater slots |
|---|---|---|---|
| 8K | 32^3 | 4 | 8K |
| 16K | 32^3 | 4 | 16K |
| 32K | 48^3 | 8 | 32K |
| 64K | 64^3 | 12 | 32K |
| 128K | 80^3 | 16 | 32K |

More cells permit smaller primary wave structures, with paired particle sampling
and smaller time steps to support the finer grid. This is not a switch to FLIP or
an incompressible pressure solve. Grid storage/clears grow cubically; primary
transfer work grows with particles times substeps. The higher presets are
experiments, not claims of real-time mobile performance.

## Flow-driven whitewater

Secondary particles use a bounded GPU pool (32K slots by default, 48 bytes each).
Emission samples the actual mass/velocity grid near primary particles: it requires
an exposed water-air interface, sufficient world-space speed and deformation or
vorticity. Uniform still water does not continuously emit, and solid walls or the
submerged sphere are not treated as air interfaces.

Spray follows gravity and drag. Foam follows the grid velocity and is projected
towards a low-density surface band. Spray can become foam on re-entering water;
submerged markers die faster. All markers collide with the analytic box/sphere,
fade in/out, expire on simulation time and recycle their slots. The last transfer
grid guides this one-way, 60 Hz secondary update; these are visual subgrid
particles, not additional mass, resolved bubbles, a surface-tension solver or
physically resolved thin sheets.

Live foam and spray indices are compacted into separate GPU append buffers and
indirect draw counts. No per-frame CPU particle/count readback is required; UI
counts share the approximately once-per-second asynchronous diagnostics. Fault
bit 32 reports non-finite secondary state. Reset clears both simulations.

In Surface mode, foam markers accumulate a depth-restricted coverage mask on the
reconstructed water, while airborne spray uses depth-tested soft sphere sprites.
Foam comes from transported markers, not whitening every crest in the shader.
The Particles debug mode displays the primary solver only. Emission, speed/
deformation thresholds, lifetime and radius are Inspector controls; changing pool
capacity requires Reset. Turning whitewater off while simulation advances clears
the pool without resetting the liquid and skips subsequent secondary updates.
Pause freezes secondary transport and lifetime too.

## Continuous water (URP 14)

`MlsMpmSurfaceFeature` is installed on the Universal Renderers referenced by the
project's default/quality pipelines. Existing renderer features are preserved.
Use **Water Splash > Enable Continuous Water** if adding another renderer or
upgrading an older scene. The feature stores explicit shader references so player
builds retain them. Cameras/scenes without active surface-mode fluids do not run
the water passes.

The screen-space pipeline runs before transparents:

1. Render enlarged particle sphere fronts into a linear-eye-depth R32 **color**
   texture with a separate private depth attachment, rejecting opaque occluders.
   URP 14's RTHandle allocator creates a depth-only texture when depth bits are
   requested; the two attachments must not be combined in one RTHandle.
2. Add visible particle chord lengths into an R16 thickness texture. Normalize
   mean overlap using the rest spacing and rendering sphere radius.
3. Filter depth and thickness together in RG32 textures. A coarse-to-fine
   separable bilateral filter uses the particle's projected radius, not a fixed
   pixel radius. Depth-aware interpolation avoids quantized sampling bands,
   retains empty pixels and rejects separate depth layers.
4. Reconstruct central-difference normals at surface resolution and store them
   octahedrally in RG16. Depth-aware upsampling interpolates normals rather than
   differentiating an upsampled, pixel-sized particle pattern.
5. Shade using thickness absorption, opaque-scene
   refraction, Schlick Fresnel and an inexpensive procedural environment/key light.
   Composite into the camera color and depth attachments.

| Water quality | Surface dimensions | Horizontal + vertical filter pairs |
|---|---|---|
| Low | Half width and height | 2 |
| Medium (default) | Half width and height | 3 |
| High | Full width and height | 4 |

The renderer requests URP depth/opaque textures only for participating cameras.
RTHandles are resized with the camera/profile and released with the renderer.
All enabled fluids in one view share the surface buffers and highest selected
quality; they use the feature's common water material settings. Set Surface Radius
Scale on the fluid to adjust particle overlap (default 0.95 times rest spacing).
Water color, absorption and refraction are on the renderer feature.
Surface Smoothing (default 2.5) controls filter reach in projected particle radii:
increase it for a smoother body or decrease it to retain more small-scale detail.
It does not increase the simulation particle count. The wider joint filter and
normal buffer have a bandwidth/memory cost; High uses four times the surface
pixels of Medium. Reconstruction alone does not add missing physical turbulence or
thin sheets; visible whitewater comes from the separate secondary system above.

This is a camera-dependent reconstructed surface, **not a mesh**. It does not
create a collider, shadow-casting surface, reflection-camera surface, SSR,
underwater shading or refraction of transparent objects. Isolated spray stays
droplet-shaped; smoothing deliberately does not bridge arbitrary empty gaps.
Reflection appearance uses a procedural environment, not scene ray tracing.
Near-plane-intersecting particles are excluded. Non-XR base Game cameras are
supported; Scene View/preview/overlay cameras are not surface-rendered.
The device must support R32/RG32/RG16 float render targets and R16 float additive blending;
unsupported configurations report an error rather than silently hiding the water.
The particle debug mode remains available.

## Device experiment

The current target is to approach the desktop Zibra baseline's motion, water
surface and performance with this independent solver, then validate this solver
on Redmi K90 Pro Max. The imported Zibra package is a desktop reference and does
not need to ship on Android. Match water volume, camera, impact and output
resolution before claiming parity; particle count alone is not an equal-work
or equal-quality comparison.

The `32K / 4 steps*` button is an explicitly experimental alternative to the
unchanged 32K/48^3/8-step default. It retains 32K liquid particles and the 32K
secondary pool, reducing time integration work rather than visual resolution.
It passed one ten-second repeated-kick scenario on Intel UHD/D3D11, but fewer
substeps change the trajectory and are not guaranteed stable for arbitrary
forces, stiffness or obstacle movement. Select the normal 32K button to restore
eight substeps. This does not establish equivalent appearance to Zibra.

### Reproducible desktop solver diagnostic

Outside Play Mode, use **Water Splash > Benchmark** for whitewater on/off at
eight substeps or the experimental four-/six-substep runs. Results are saved as
timestamped JSON files in the project-root `BenchmarkResults/` directory.
The temporary hidden objects are destroyed after the run; no scene is saved.
The run warms up for 12 ticks, measures the next 60 ticks in batches of four,
then validates through ten simulated seconds with repeated kicks. Later impacts
are validated but are outside the timing window.

Each measured batch waits for GPU completion using a 32-byte diagnostic readback.
Reported times include CPU submission, completed GPU work and readback overhead;
they are **not isolated GPU times**, rendered-frame times or phone measurements.
The displayed ratio compares completed simulated time with measured batch wall
time, excluding editor-update gaps. Whole-frame performance still needs the
actual renderer and the simulation backlog check. See `BenchmarkResults/README.md`
for this session's measurements and limitations.

For an Android build, include the demo scene in Build Settings and select Vulkan
in Android Player Settings. This prototype does not change the project's build
target, signing settings, render pipeline selection or global quality settings
automatically.
Installing continuous water does add its renderer feature to the referenced
quality renderer assets.
Use a fixed internal rendering resolution when comparing phones, and compare
the same grid, particle count, sound speed and substep settings on both devices.
Presets are convenience starting points, not equal-quality benchmarks.

Start K90 Pro Max at 32K/48^3; start K50 at 8K/16K with the 32^3 grid. Compare both
Rendering and Whitewater ON/OFF separately to isolate their costs.
Run at least ten minutes before reporting sustained performance.

## Validation

EditMode GPU tests in `WaterSplash.MlsMpm.Tests` cover mass transfer, gravity,
zero-force motion, analytic sphere contact, non-multiple dispatch bounds, reset,
unsafe-step fault detection, and a four-second dam break with a push.
Surface tests cover shader compilation, measurable particle-scale depth/thickness
ripple attenuation while retaining broad waves, projected-radius filtering and
separation of empty pixels/depth layers. An EditMode integration test temporarily
enters Play Mode, renders an isolated fluid with the actual URP feature and checks
that the reconstructed depth is measured in metres, not hardware depth.
Whitewater GPU tests cover still-water rejection, impact emission of both roles,
transport/lifetime/expiry, bounded non-multiple capacities, independent disabling,
the higher-detail presets and resolution-invariant initial physical bounds.
`CaptureSnapshot()` is an explicit synchronous diagnostic operation for tests
and investigation; it is not called each frame.

### October 27 stability and performance investigation

The comparison now restores recommended 4/8/12/16 substeps when switching
16K/32K/64K/128K presets. A retained 4-step setting reproduced fault 16 at
64K tick 23 and 128K tick 21. Collider motion is swept through substeps, and
R can reset the standalone solver after a fault. Fault guards remain enabled.
The current stage markers support GPU timestamp recording; moving-sphere and
runtime-switch validation menus write structured reports in BenchmarkResults.
See `BenchmarkResults/Investigation-2026-10-27.md` for measured transmission
bottlenecks, limitations, GPU driver diagnosis, and the PB-MPM/PBF/FLIP assessment.

Algorithm references (this implementation is independently written):

- Hu et al., MLS-MPM: https://yzhu.io/publication/mpmmls2018siggraph/
- Incremental MPM: https://github.com/nialltl/incremental_mpm
- WebGPU-Ocean: https://github.com/matsuoka-601/WebGPU-Ocean
- Splash: https://github.com/matsuoka-601/Splash
