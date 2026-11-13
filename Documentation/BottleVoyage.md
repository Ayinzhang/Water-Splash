# Bottle Voyage

Single-scene Unity 6 (URP 17, Vulkan) demo: a horizontal glass bottle filled with
GPU MLS-MPM water, a draggable sailboat, Blender props and a CPU tag rope.
Open `Assets/WaterSplash/Scenes/BottleVoyage.unity` and press Play.

Drag the boat (hull or sail), flick the neck tag, Space pauses, R resets.
The slider selects 16K / 32K / 64K / 128K particles (reset on release);
the button toggles reconstructed water / raw particles without a reset.

## Code map

| File | Role |
| --- | --- |
| `Scripts/Voyage/BottleVoyage.cs` | Scene controller: input, boat probes, UI, frame pacing |
| `Scripts/Voyage/BottleBoundary.cs` | Analytic bottle profile and initial fill |
| `Scripts/Voyage/TagRope.cs` | CPU position-based cord + rigid tag |
| `Scripts/Simulation/MlsMpmFluid.cs` | Solver driver: buffers, dispatches, diagnostics readback |
| `Shaders/BottleMlsMpm.compute` | ClearGrid, Transfer (P2G), UpdateGrid, UpdateParticles (G2P), boundary cache, whitewater |
| `Shaders/BottleWaterProbe.compute` | Five water-height probes for the boat (80-byte async readback) |
| `Scripts/Rendering/MlsMpmSurfaceFeature.cs` | Screen-space water: depth/thickness, bilateral smoothing, normals, shading, foam |
| `Editor/Art/BottleVoyageModels.cs` | Import settings for the Blender FBX and baked textures |

## Solver

- Weakly compressible MLS-MPM, 60 Hz fixed tick, 8 substeps, retained backlog (no FPS cap, no time scaling).
- Grid window 80 x 30 x 30 (cell 0.0533) around the bottle; static wall flags cached once per reset.
- Every tier keeps the same water volume (鈮?.445 units鲁, fill height 0.73); spacing and radii scale with the cube root of per-particle volume.
- Particles are stored as SoA float4 planes (`field * particleCount + id`).
- Fused Packed16 P2G: one transfer pass, (mass, mx) and (my, mz) packed as signed 16-bit fixed point in two contiguous 32-bit atomics per node.
  Stress uses the density ratio gathered by the previous G2P. 8-bit sums saturate and are not viable.
- An invisible sphere follows the boat hull and sweeps at most a quarter cell per substep.
- Flow-driven whitewater (8K pool) splits into foam and spray append buffers drawn indirectly.
- Diagnostics: two uints (fault flags, min particle) read back asynchronously once per second; a fault stops the run.

## Phone results (Adreno 840, Vulkan, 2608 x 1200, development APK)

| Particles | ms / substep | FPS |
| --- | --- | --- |
| 64K | 0.60 | 鈮?20 |
| 128K | 1.19 | 鈮?8 |

Simulated/wall time 鈮?1.0, zero faults. P2G history at 64K/6 substeps (ms/substep):
two-pass 2.10 鈫?fused 1.22 鈫?Packed16 0.73 鈫?contiguous packed grid 0.60.
Rejected after measurement: particle sorting (+0.58 ms per sort, no P2G gain), 64-thread groups (<1%),
full 27-node unrolling and shared-memory atomic merging (slower), cropped surface targets (marginal).

Analysis tools used: Android Profiler + `FrameTimingManager` GPU deltas (scratch-substep subtraction),
adb logcat.

## Art

Props (cork, saddles, neck cord, sailboat, desk still life) are generated headlessly in Blender 4.2+ (tested 5.0.1):

    blender -b --factory-startup --python Tools/Blender/voyage_models.py -- [rope fittings boat desk]

Output goes to `Assets/WaterSplash/Art/BottleVoyage/Blender` (Cycles-baked atlas: albedo, normal, mask R metal / G AO / A smoothness).
Meshes keep their names, so re-exporting updates the scene in place. The glass, tabletop and seabed are static mesh assets;
the seabed is merged into one mesh per material.

## Android notes

- Keep Player Settings > Vulkan > Pre Transform **disabled**: on the Adreno 840 it clips rendering at x = 1200.
- Install a full Build APK; Patch And Run APKs depend on separately deployed shadow libraries.
