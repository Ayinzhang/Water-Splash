# Bottle Voyage: rewrite starting point

Open `Assets/Scenes/BottleVoyage.unity`.

The rewrite retains models, textures, material assets, camera,
lighting, post-processing, reflection probe and native uGUI controls are retained.
The roots are Main Camera, Boat, Bottle, Table and UI.

At the rewrite starting point, project C# scripts, assembly definitions, shaders, the custom renderer feature,
UI callbacks and baked simulation state have been removed. Custom materials now
use URP Lit or Unlit placeholders; glass is transparent. Water, boat interaction,
tag simulation, animated seaweed and custom material effects are absent.

`Tools/Blender` retains the model-generation sources; `Tools/UnityBridge.ps1`
retains the editor connection helper. Neither is runtime gameplay code.
Installed packages and project graphics / Android settings are unchanged.

## Suggested rebuild order

Current progress: `BottleVoyage.cs` drives the native uGUI and initializes
`BottleFluid.cs`. `BottleBoundary.cs` supplies mesh-derived initial positions
for all four tiers, using the integrated water volume and a fill level of 0.73.
`BottleFluid.compute` now restores the archived MLS-MPM core: ClearGrid,
P2G (Packed16), UpdateGrid and G2P. Shared particle and boundary functions are inlined.
The kernel order is CacheBoundary, ClearGrid, P2G, UpdateGrid, G2P.
CacheBoundary calculates grid wall normals/distances on the GPU once per Init;
only the remaining four kernels run each simulation substep.
The driver uses 60 Hz ticks with 8 substeps and an 80 x 32 x 30 grid
(offset 0, 4, 25); the extra Y padding supports the current finer 128K fill.
Particle storage is five float4 streams: grid positions, velocity/density ratio,
and three affine rows. Rest density follows particle count / fixed water volume,
not the denser candidate lattice from which CreateFill samples.
`BottleWater.shader` displays particle billboards directly from grid positions.
There are no custom GPU diagnostics, fault readbacks or automatic fault stops.
Dispatch thread-count guards and physical collision constraints remain. There is no
continuous surface, whitewater, buoyancy feedback or tag simulation yet.
Boat dragging lives in BottleVoyage, excludes UI hits and keeps the hull inside
the bottle at a fixed height. The Boat CapsuleCollider is the editable hull shape:
its scaled world endpoints/radius are uploaded to the fluid solver. All substeps
use the current capsule position directly, without interpolation or speed limits,
constrain grid velocities and project particles out of the
hull. No Rigidbody is required; water does not yet move or tilt the boat.
The boundary is attached to Bottle and assigned to BottleFluid in the saved scene.

1. **Scene controller and UI:** create `Assets/Scripts/BottleVoyage.cs`.
   Bind the existing Slider and Button through the Inspector. First implement
   particle-tier selection, reset and display-mode selection without fluid.
   Commit particle-count changes on pointer release, not every drag update.
2. **Bottle boundary:** `BottleBoundary.cs` derives the inner profile from the
   Bottle MeshFilter and supplies world/profile transforms and contact queries.
   Fill positions, volume and GPU boundary caching share that instance profile.
3. **Particle display:** create `BottleFluid.cs`, `BottleFluid.compute` and
   `BottleWater.shader`. Start with 16K stationary particles and one billboard pass.
   Establish buffer layout, grid/world coordinates and depth before simulation.
4. **MLS-MPM:** implement ClearGrid, P2G, UpdateGrid and G2P. Start with a simple
   container and float particle state, then add bottle contacts. Establish stable
   motion and density before reproducing Packed16 accumulation.
5. **Continuous water:** add particle depth, thickness, bilateral smoothing,
   normals and water composition to `BottleWater.shader`; connect them through
   `BottleWaterFeature.cs` on the existing URP renderer.
6. **Boat:** add water-height probes, async readback, buoyancy, drag input and the
   swept hull obstacle. Keep probe kernels in the fluid compute file.
7. **Tag:** implement `TagRope.cs` with particle integration, distance constraints,
   bottle/table contacts, pointer interaction and a dynamic rope mesh.
8. **Appearance:** replace the placeholder glass, tabletop, seaweed and background
   materials with your rewritten shaders, preserving existing texture assets.
9. **Whitewater and mobile tuning:** add foam / spray last, then restore 32K,
   64K and 128K tiers and bake the finalized initial positions and grid boundary.

Archived baseline: 60 Hz simulation ticks, 8 substeps, 80 x 30 x 30 grid,
cell size 4 / 75, default 64K particles. Preserve particle volume when changing tiers.
Vulkan Pre Transform remains disabled in the existing Android configuration.

## Mesh-derived bottle boundary

All extraction lives in `BottleBoundary.cs`; no extra runtime script, physics
collider or SDF package is required. `BottleBoundary.Awake` builds the profile,
volume and bounds once. `BottleVoyage.Start` then calls `BottleFluid.Init` to
generate the fill and upload the profile to `CacheBoundary`. Callers read the
initialized `profile`, `center`, `bounds` and
`waterVolume` fields directly. There is no lazy initialization or rebuild on reset.
Restart Play Mode after editing the mesh or its transform.

The current readable, double-walled rotation mesh supplies 16 inner sections.
Triangle winding identifies inner versus outer walls in mesh-local coordinates;
the transform is then applied, including the bottle's negative X scale. Sections
are assumed to be concentric circles about local X. The axis remains aligned
with world X; circular
radial scaling and rotation about that axis are supported, not elliptical scaling,
shear or arbitrary bottle rotation. The shader supports at most 32 sections.
This demo assumes a suitable mesh and transform without defensive validation.
This is an analytic rotation-profile approximation, not triangle-mesh collision;
the two profile endpoints retain the existing closed-container end constraints.

The center follows the Bottle transform. The fill level remains 0.73 in world
space, while volume, fill extents and draw bounds follow the extracted profile.
Each boundary owns its own data. Containers must fit the fixed 80 x 32 x 30
storage/interpolation support and physical simulation box; using larger or
relocated bottles also requires updating the simulation grid.
