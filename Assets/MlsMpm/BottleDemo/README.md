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
80^3 grid, eight substeps, bottle geometry, fill height and represented total water
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
- Grid: 80 cubed over the same 4-unit domain; cell width 0.05333. This finer grid
  supports the smaller fill and bottle shoulder, and costs more than the old 48-cubed grid.
- Eight substeps per 1/60-second physics tick; retained backlog; no frame-rate limit.
- High surface reconstruction in this presentation scene; no mobile performance claim.
- The small sailboat retains asynchronous water-height and tilt sampling.

Baseline MLS and PB scene assets and their compute shaders are unchanged. The shared
`MlsMpmFluid` only gains an optional, nonserialized command-configuration hook, which
is unset in those scenes.

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
The 32K liquid particle count, eight substeps and baseline MLS scene are unchanged.

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
