# Single-pool desktop comparison

Open **Water Splash > Open Single Pool Comparison**, then Play. Use **Zibra only**
and **MLS-MPM only** to switch the same pool. Only the selected solver is active;
switching resets simulation time and the initial water block. Each selection then
advances two shared 1/60-second ticks to construct its initial rendered surface,
including while paused; the clock shows this elapsed time. No split-screen or
second pool is used. Existing baseline/demo scenes are unchanged.

Shared inputs: 4m box; identical initial particle positions and zero velocities;
16K/32K/64K/128K particle presets; matched physical cell size; gravity; sphere
radius and simulation-time trajectory; camera; fixed internal output resolution.
The same sphere, floor, lighting and reflection environment remain in the scene.

MLS-MPM stores two lower and three upper guard cells, so its storage grid is
five cells wider than the physical grid. The 32K preset uses MPM storage 48^3
and Zibra 43^3, both covering 4m with 43 physical cells per axis. This matches
spatial resolution rather than falsely treating guard cells as simulated volume.

Zibra's supported baked-state format initializes the shared particle block.
Its particle density is adjusted to the shared spacing to match nominal volume.
The native position coordinates are in cells. Its bridge divides gravity by 100;
the comparison uses sqrt(100/cellSize) native time units per second as the
dimensional conversion. This is inferred from exposed plugin units, not a
guarantee that both solvers have equivalent effective gravity or material behavior.
Wall/contact discretization, viscosity, stiffness, speed limits and surface
reconstruction remain solver-specific. Raw equal slider values do not imply
equivalent physics. Substeps and native iterations are intentionally separate.

Controls include pause, reset, a single 1/60-second step, deterministic automatic
stir, dragging the sphere, foam on/off, water rendering on/off, surface quality,
1280x720 / 1920x1080 and capped/uncapped frame rate. Workload-setting changes reset
the run so samples from different configurations are not mixed. Inspector fields
for gravity/radius need Reset after editing. Simulation backlog is retained and
displayed, including when the renderer runs faster than the simulation.

Foam means different features: Zibra 1.4.2 has density whitening; MLS-MPM has
transported foam/spray particles. Disable both for a base-water comparison.
Low/Medium use half-resolution surfaces; High uses full resolution. Filters and
materials remain different. The current MLS-MPM shader uses its own procedural
reflection environment, while Zibra uses the scene probe; appearance parity is
an optimization goal, not a claim made by this harness.

The panel reports the actual graphics device. Measurement export requires a
device name containing 5070 and Ti by default. It does not switch graphics devices.
Saved JSON records the device, configuration, mean/p95 frame wall time over up
to the latest 600 rendered frames, total simulated/wall time and backlog. This
is an Editor whole-frame diagnostic, not isolated GPU solver timing.

## GPU check on 2026-10-27

**Current result supersedes the historical driver notes below:** the restarted
Unity process uses NVIDIA GeForce RTX 5070 Ti Laptop GPU / D3D11. The comparison
defaults to 8 MPM substeps, preserves manual step selection across particle presets,
and offers 1/2/3/4/6/8/12/16/24/32 steps plus a conservative recommendation button.
The floor footprint is 4m x 4m and the visual container frame is disabled.
Automatic stirring starts disabled. Affine/divergence damping stays zero,
preserving the exaggerated splash the user prefers.

Surface whitewater now accumulates overlapping surface-oriented footprints,
uses a depth-aware coverage filter, and adds world-space fine texture. Airborne
spray stays separate. `continuousFoam` on the renderer feature allows the old
appearance to be compared without changing simulation state or particle count.

RTX results, limitations and primary-source research are recorded in
`BenchmarkResults/RTX-Mobile-2026-10-27.md`. `rtx-ab-gpu.json` records per-stage
GPU samples. `128k-eight-realtime.json` records a separate 20-second 128K/8-step
moving-sphere run: zero faults, 20.00 simulated seconds, 4.8ms retained backlog.
Editor mean frame 16.68ms, P95 24.02ms; average ~60 FPS is not a locked 60 FPS
guarantee. Android performance remains unmeasured.

Menus: Benchmark > Run RTX A B GPU Audit; Run 128K Eight Steps Realtime;
Capture Foam Before After. Validation > Calibrate Freefall Gravity; Run Runtime
Switch Regression. GPU audit requires Play Mode and the expected RTX device.
It requests one 1/60s tick per rendered frame, collects at least 60 valid solver
GPU timestamp frames when available, and reports missing timestamps explicitly.

### Historical notes (superseded where stated above)

The connected Unity process reports Intel UHD / D3D11. Windows exposes the NVIDIA
PCI display function, but its display-driver INF and version are absent in the
queried device properties, and it is not listed as an installed Display adapter.
Unity.exe's Windows graphics preference is now High performance. The NVIDIA
display driver must be available and Unity restarted before RTX 5070 Ti results
can be collected; confirm the panel's actual device after restarting.

## Stability and profiling update (2026-10-27)

Preset changes now restore 4/8/12/16 MPM substeps for 16K/32K/64K/128K.
The steps control cycles upward from that preset's recommendation. The sphere
is interpolated through each simulation substep, and R works after a fault.
These presets are tested starting points, not an adaptive acoustic CFL guarantee.

The old retained 4-step setting reproduces fault 16 at 64K tick 23 and 128K tick
21, before automatic stirring starts. Recommended settings passed moving-sphere
regressions (10/20/10/5 simulated seconds respectively). Eight runtime switch and
rebuild scenarios passed. Detailed results and limitations are in
`BenchmarkResults/Investigation-2026-10-27.md`.

Menus under Water Splash > Validation run deterministic moving-sphere and runtime
switch regressions. The latter requires Play Mode and leaves the comparison paused.
Water Splash > Benchmark > Capture MLS GPU Stages collects Unity GPU timestamps
while MLS is running, and writes `BenchmarkResults/gpu-stages.json`. It restores
the previous profiler settings and reports unavailable timestamps explicitly.
Read GPU ms/dispatch separately from rendered-frame time and simulation-tick time.

Windows logs confirm the NVIDIA display driver was uninstalled on October 25.
A signed matching 617.42 driver package is cached locally. The project script
`Tools/Repair-NvidiaDisplayDriver.ps1` can restore it from an administrator
PowerShell after saving and closing Unity; it has not been run by this session.
