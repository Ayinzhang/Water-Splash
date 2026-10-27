# Zibra 1.4.2 paid visual baseline

Open `Scenes/ZibraBaseline.unity`, or use **Water Splash > Open Zibra Baseline**,
then enter Play Mode. This uses the supplied Zibra Liquids 1.4.2 paid native GPU
solver and its screen-space liquid renderer. This paid release is older than the
previous Free 1.5.2 baseline: it does not have that release's mesh renderer.
It does not use the MLS-MPM prototype's solver or whitewater.

The default is 32,768 particles, a 48^3 grid, and the plugin's default single
simulation iteration per update. A box emitter fills the bounded particle pool,
then switches off when the plugin reports the full count. Reset repeats the
drop; the orange sphere is a one-way analytic liquid collider. The container
is 4 x 4 x 4 metres, matching the saved MLS-MPM scene. Its yellow boundary frame
is visual only; the initial emitted block is smaller than the container.

## Controls

- Pause / Run, or Space, freezes/resumes simulation.
- Reset, or R, refills the initial water block.
- Stir moves the sphere through a short loop. Drag below the panel to move it
  horizontally.
- Rendering ON/OFF isolates the native simulation from liquid drawing.
- 16K/40^3, 32K/48^3, 64K/64^3 and 128K/80^3 rebuild the native resources.
- Density foam toggles the plugin's built-in surface whitening; intensity and
  threshold sliders update it without resetting the simulation.

The presets retain Zibra's default resting particle density. They therefore do
not contain exactly equal physical water volumes; they are exploration presets,
not equal-quality benchmarks. Other solver and surface settings retain the plugin
defaults; the scene uses a blue water tint and opacity of 3.

## What this version's foam does

The 1.4.2 shader derives foam from reconstructed low-density regions. It whitens
and roughens thin sheets and isolated liquid, with no additional simulated foam
particles, lifetime or surface advection. It is not persistent floating foam,
spray or the modern Pro whitewater system. Increasing the threshold increases
the regions classified as foam; increasing intensity makes them whiter. The
toggle provides an immediate A/B comparison on the same liquid state.

## Measurement and isolation

The panel displays the actual graphics device/API and smoothed frame time.
Frame time includes rendering, editor overhead and the 60 FPS cap; it is **not**
GPU solver time. Zibra's timestep clamp can slow simulation under load, so a
healthy display FPS alone is not proof of equal simulated-time throughput.

The scene has its own URP asset and renderer containing only Zibra's renderer
feature. The controller temporarily selects that pipeline and a 60 FPS cap with
v-sync disabled, restoring the previous quality pipeline and frame settings when
disabled. Existing project renderer assets and the MLS-MPM scene are untouched.
The floor checkerboard makes refraction visible; a one-shot reflection probe
provides environment reflections, not continuously updated scene reflections.

## Package migration

Importing 1.4.2 over Free 1.5.2 leaves incompatible scripts behind. The mixed
installation was backed up outside Assets before restoring the supplied
1.4.2 archive and removing its incompatible Free-only scripts. No license checks,
compile-time edition flags or feature restrictions are bypassed.
The unused Free-only `ZibraFluidNative_Win_Editor.dll` is disabled in its importer;
the paid solver binds to the supplied `ZibraFluidNative_Win` library.

The baseline scene migrates to `AnalyticSDFCollider`, the old solver/emitter
fields, the paid fluid material and its own renderer feature. The migration
menu is **Water Splash > Migrate Baseline to Zibra 1.4.2** and only operates on
the saved baseline scene. Do not run it over deliberate custom scene changes.

A small vendor initialization fix resets `activeParticleNumber` to zero when
reinitializing. Otherwise its stale/default count can switch off the baseline's
emitter before a fresh simulation has emitted water.
The vendor's existing authentication/generation endpoint uses HTTPS instead of
HTTP so Unity 2022 does not reject the request as an insecure connection.
License validation itself is unchanged. The archive is named 1.4.2 while its
native library reports 1.3.0; the panel identifies both rather than implying
that this is the current Pro release.

This imported package has no enabled Android target. The baseline is not a
phone build. The last connected editor reported Intel UHD / D3D11 rather
than the laptop's RTX 5070 Ti. Check the on-screen GPU name before attributing
results to either device.
