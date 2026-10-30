# Horizontal Bottle Voyage

Open `BottleVoyage.unity`, then Play. Pick and drag the sailboat (hull or sail),
Space to pause, R to reset. Dragging empty water no longer moves an invisible stirrer.

The boat follows the pointer immediately on a fixed water-height drag plane,
retaining the initial grab offset without a speed cap or fixed-tick input delay.
Positions stay in the wide bottle body; vertical motion/tilt still follow actual
water probes. Changing water height during a drag does not shift its horizontal
pointer mapping.
The existing analytic sphere now follows underneath the boat as a simplified hull
collider, so moving the boat pushes water. The visible boat is immediate, while
the water collider sweeps at up to 6 world units/second and at most a quarter grid
cell per substep, rather than teleporting through the liquid on fast gestures.
Energetic motion feeds the original flow-driven whitewater, not a painted trail
or an always-on foam emitter. This is not a full rigid-body hull solve.
Input over the on-screen controls is excluded from boat dragging.

Buttons select **16K / 32K / 64K**, one **Water > Particles / Particles > Water**
toggle, pause and reset. The particle-count buttons reset the run; the display toggle preserves the current
simulation. Selected options are highlighted. Changing presets keeps the same
compact grid, six substeps, bottle geometry, fill height and represented total water
volume. Per-particle volume is total volume divided by count; particle spacing and
display/surface radius scale with the cube root of that volume. The 32K initial
fill supplies the reference volume, rather than letting each lattice's rounding
change the amount of water.

Water probes resize their reduction buffers for each preset. A preset/reset request
waits for any in-flight readback without blocking the CPU, and rapid selections
coalesce to the latest requested preset. The particle mode draws individual opaque
particle spheres rather than the reconstructed water surface; it omits the
surface-only foam composite without disabling whitewater simulation.

The bottle lies along world X. `BottleBoundary.Profile` defines the round inner wall,
rounded heel, narrowing shoulder and capped neck. The glass mesh uses the same profile.
The separate `BottleMlsMpm.compute` applies the boundary to grid velocities, liquid
particle positions/velocities and whitewater. Wall contact removes outward normal
velocity; particle projection keeps spray and liquid inside the bottle. The sphere
hull collider is also constrained inside the curved wall.

- Main particles: exactly 32,768; initial fill height: 0.73 world units.
- Initial spacing: approximately 0.03533 (previous upright scene: 0.07407).
- Represented water volume: approximately 1.445 world units cubed, 10.85% of the old
  upright fill. The particles are resampled inside the curved volume, not scaled visuals.
- Grid: 80 x 40 x 40 stored cells with the original 0.05333 cell width and
  unchanged world-space lattice (80-cubed reference domain). Empty Y/Z slabs are
  omitted; the entire bottle, including spray space, remains covered.
- Six substeps per 1/60-second physics tick for 16K/32K/64K; a configured 128K
  run uses eight. Reset and particle-tier changes reapply this bottle-only policy.
  Retained backlog; no frame-rate limit or simulation-time scaling.
- High surface reconstruction in this presentation scene; no mobile performance claim.
- The small sailboat retains asynchronous water-height and tilt sampling.

Baseline MLS and PB scene assets retain their original solver settings and AoS
layout. Their shaders now share the same particle-access helpers as the bottle;
the numerical algorithms are unchanged. The shared `MlsMpmFluid` has optional,
nonserialized scene-local boundary and storage-window configuration, which is
unset in those scenes. MLS no longer allocates the
PB-only particle sorting buffers; switching to PB allocates them on reset.

`Water Splash > Update Bottle Voyage to Horizontal` regenerates only the active bottle
scene's presentation. Its generated meshes live under `BottleDemo/Art`.

Validation: `BottleBoundaryTests` covers the initial fill, shoulder normals, gravity,
GPU contact at the shoulder/heel/cork, spray containment, moving stirrer and reset.

As of 2026-10-28.

## Left-facing still life

The current scene keeps the bottle proportions, with the mouth facing left-front,
mirroring the original right-front composition. The camera's X position and initial
boat/stirrer placement mirror with the bottle. `BottleBoundary.mouthFacesLeft` mirrors the X profile for the
initial fill, CPU stirrer projection and GPU liquid/whitewater contacts, matching
the mirrored glass rather than just flipping the artwork.

`Water Splash > Detail Current Bottle Voyage` adds the authored still-life group
once and refuses to replace it on subsequent calls. It preserves existing scene
objects. The scene includes low seabed stones, shell gravel, ribbon eelgrass,
scallops, a brass compass, inked chart and coiled rope on a wood-grained tabletop.
Seabed decorations are visual only, not additional fluid colliders. Seaweed sway
is a lightweight vertex animation, not simulated plant dynamics.

Whitewater remains flow-driven GPU foam, not white dots painted onto the glass.
The bottle scene retains the original emission parameters: 8 emission rate,
1.2 m/s speed threshold, 4 deformation threshold, 2.5-second lifetime and 0.4
radius scale, with an 8,192-seed bounded pool. Foam follows actual energetic
surface motion; a completely still surface does not continuously manufacture foam.
The 32K liquid particle count and baseline MLS scene are unchanged.

## Detailed whitewater rendering

The bottle renderer enables **Detailed Whitewater** on its `Turquoise water`
renderer feature. Disable it to compare with the original rendering without
resetting the simulation. Other renderer assets retain the original appearance.

Foam seeds now carry two scales of antialiased cellular detail in their local
surface footprint. Sparse foam has broken bubble rims and holes; overlapping
seeds merge into denser foam. Bubble interiors open as seeds age. The seeded
surface frame moves with each seed, rather than sampling static world-space
noise. Flow stretches the footprint. This is rendering detail, not individually
simulated bubbles or a new foam transport model.

Spray uses smaller, velocity-stretched translucent droplets with restrained
Fresnel/glints and soft intersections against opaque geometry and the water
surface. Foam gains a second half-float accumulation channel; no history buffer,
extra full-screen pass, additional whitewater particles or emission changes are
introduced. The existing surface classifier and particle motion are unchanged.
Mobile GPU cost has not been measured.

## 64K optimization baseline

The user-reported starting point is 64K at 14 FPS. This is not an Editor
measurement and must not be replaced with desktop simulation-only timings.
Keep particle count, six substeps, water volume, rendering quality and real-time
tick duration fixed when comparing.

`BottleBoundary.compactGrid` enables the grid storage window. Change it in Edit
Mode or reset after changing it in Play Mode. Disabling restores 80 x 80 x 80
storage for comparison. The compact window is aligned to four-cell boundaries
and includes transfer/whitewater sampling padding around the full bottle profile,
not just the initial water level. CPU/GPU particle coordinates and grid origin
do not move; only the mapping of global cell indices to storage changes.
The two primary grid buffers shrink from 15.625 MiB to 3.90625 MiB (75%).
The compact path is MLS-only; PB and baseline solvers retain their cubic grids.

The bottle stress pass writes only `velocity.w` (density ratio), rather than
assigning the complete 80-byte particle back after changing one field.
Per-particle diagnostics and overflow checks remain enabled. A group-reduced
diagnostic experiment did not establish a local timing win and was not retained.
No particle sorting, P2G fusion, precision reduction, time scaling or render-quality
reduction is introduced. Particle storage remains AoS in this first optimization.

Local comparison (RTX 5070 Ti Laptop, Editor, 64K/6, 120 warm-up ticks followed
by nine GPU-fenced batches of 30 ticks): the original shader's two median tick
times were 3.370/3.371 ms; retained changes measured 3.189/3.121 ms. These are
simulation-only wall times, excluding rendering, not GPU timestamp timings or
phone FPS. Individual batches fluctuate, and the gain is modest. After all 390
ticks, all 64K particle states matched the original shader exactly. Raw timings
are preserved in the session artifacts, not written into the repository.

## Static walls, grouped SoA and screen-space cropping

The bottle enables `MlsMpmFluid.cacheGridBoundary` and `useParticleSoA`. Change
either option before initialization, or reset after changing it. Both can be
disabled independently for a fixed-six-substep comparison.

The wall cache stores the exact GPU-computed normal and signed distance for
each grid cell, using one float4 per cell (1.953125 MiB for 80 x 40 x 40).
It is rebuilt on reset or when the profile, bottle centre, axis or grid origin
changes. Clearance is applied at use time, so changing particle spacing cannot
leave a stale collision margin. Liquid and whitewater particle contacts still
use the original analytic profile and iterative projection; no sampled SDF
approximation is introduced for particles.

Grouped SoA stores five contiguous float4 streams: position, velocity/density and
the three affine columns. Total particle storage is still 80 bytes per particle.
All streams share one allocation; shaders address either interleaved AoS or
separated streams through one common helper. Surface/particle drawing, boat
probes, whitewater sourcing and explicit diagnostic snapshots use the same
layout. There is no per-frame AoS conversion or duplicate rendering buffer.
CPU `ParticleState` snapshots remain available through `CaptureParticleStates`.
PB sorting retains its separate AoS scratch buffer and uses the shared accessors.

Only the bottle renderer enables `MlsMpmSurfaceFeature.cropToFluidBounds`.
It conservatively projects the full stored simulation bounds, expanded for
the largest stretched foam/spray footprint, and scissors the existing passes.
Resolution, reconstruction radii and filter iterations are unchanged. Near-plane
crossings fall back to the full screen. Intermediate targets are cleared outside
the rectangle too, preventing stale depths/normals after camera or fluid motion.
This reduces fragment work, not render-target allocations.

Do not attribute the user's earlier 14-to-26 FPS improvement to any one change.
That comparison also changed eight substeps to six, with variable fixed-tick
catch-up per display frame. The existing Android capture has six substeps only
and cannot isolate the two contributions. CPU-side dispatch markers, including
those named "GPU", are not per-kernel GPU execution timings.

Local follow-up (2026-10-29, RTX 5070 Ti Laptop/D3D11): all four combinations
of cache/SoA, run in forward and reverse order at 64K/6 with the same moving-hull
warm-up, retained identical particle states after 390 ticks. The simulation-only
median wall times overlapped: both disabled 3.609--3.644 ms/tick, cache only
3.694--3.711, SoA only 3.567--3.646, both enabled 3.555--3.661. This does **not**
establish a stable desktop solver speedup, let alone a phone speedup.

On one paused 1280 x 720 bottle frame with 123 foam and 53 spray seeds, the
scissor covered 750 x 497 pixels (40.45% of the screen). Full-frame and cropped
RGB readbacks were identical. GPU-fenced whole-scene render-request medians were
1.034--1.075 ms full-frame versus 1.018--1.022 ms cropped; these include CPU
submission, exclude simulation and are not GPU timestamp timings. The primary
verified result is fewer rasterized water pixels without a visual change.
