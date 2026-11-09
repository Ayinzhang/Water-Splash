using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    public static class MlsMpmEnergyAudit
    {
        [Serializable] public sealed class Case
        {
            public int steps, completed; public float exponent, sound, affineDamping, divergenceDamping; public string status;
            public float initialHeight, maxHeightAfterImpact, maxP99AfterImpact;
            public double initialEnergy, maxEnergyRatio;
            public List<MlsMpmFluid.Snapshot> samples = new List<MlsMpmFluid.Snapshot>();
        }
        [Serializable] public sealed class Report
        {
            public string gpu, utc, status; public int particles = 32768, grid = 48;
            public string note = "No moving collider, no kick, no rendering/whitewater. Same comparison initial block. Specific translational + APIC affine + gravitational + EOS compression energy diagnostic, not an exact conserved discretization. No snapshot readback in performance captures. Heights after 0.5s, 6s runs.";
            public List<Case> cases = new List<Case>();
        }
        static Report report; static GameObject owner; static MlsMpmFluid fluid; static int index;
        static string outputPath = "build/Reports/energy-audit-rtx.json";
        [MenuItem("Water Splash/Validation/Run Affine Damping Audit")]
        public static void StartDamping()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || fluid != null) throw new InvalidOperationException("Stop other runs first.");
            outputPath = "build/Reports/affine-damping-rtx.json";
            report = new Report { gpu = SystemInfo.graphicsDeviceName, utc = DateTime.UtcNow.ToString("O"), status = "running" };
            foreach (float damping in new[] { 6f, 20f, 60f })
                report.cases.Add(new Case { steps = 4, exponent = 3, sound = 6, affineDamping = damping, divergenceDamping = damping, status = "pending" });
            index = 0; Begin(); EditorApplication.update += Advance; AssemblyReloadEvents.beforeAssemblyReload += Cancel;
        }
        [MenuItem("Water Splash/Validation/Run Energy And Low Steps Audit")]
        public static void Start()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode first.");
            if (fluid != null) throw new InvalidOperationException("Audit already running.");
            outputPath = "build/Reports/energy-audit-rtx.json";
            report = new Report { gpu = SystemInfo.graphicsDeviceName, utc = DateTime.UtcNow.ToString("O"), status = "running" };
            foreach (int steps in new[] { 1, 2, 3, 4, 8, 16 }) Add(steps, 3, 6);
            foreach (int steps in new[] { 1, 2, 3, 4 }) Add(steps, 1, 6);
            Add(1, 3, 3); Add(2, 3, 3);
            index = 0; Begin(); EditorApplication.update += Advance; AssemblyReloadEvents.beforeAssemblyReload += Cancel;
        }
        static void Add(int steps, float exponent, float sound) => report.cases.Add(new Case { steps = steps, exponent = exponent, sound = sound, status = "pending" });
        static void Begin()
        {
            Cleanup(); var c = report.cases[index];
            owner = new GameObject("Temporary energy audit") { hideFlags = HideFlags.HideAndDontSave }; owner.SetActive(false); owner.transform.position = new Vector3(-2, 0, -2);
            fluid = owner.AddComponent<MlsMpmFluid>();
            fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/WaterSplash/Shaders/MlsMpm.compute");
            fluid.particleCount = report.particles; fluid.gridResolution = report.grid; fluid.substeps = c.steps; fluid.soundSpeed = c.sound; fluid.pressureExponent = c.exponent;
            fluid.affineDamping = c.affineDamping; fluid.divergenceDamping = c.divergenceDamping;
            fluid.showParticles = fluid.showControls = fluid.enableWhitewater = false; fluid.Initialize();
            var positions = MlsMpmFluid.CreateComparisonBlock(report.particles, out float spacing);
            fluid.SetComparisonInitialPositions(positions, spacing); var state = fluid.CaptureSnapshot();
            c.initialHeight = state.maximumWorldHeight; c.initialEnergy = Energy(state); c.status = "running"; c.samples.Add(state); Write();
        }
        static double Energy(MlsMpmFluid.Snapshot s) => s.meanKineticEnergy + s.meanAffineKineticEnergy + s.meanPotentialEnergy + s.meanCompressionEnergy;
        static void Advance()
        {
            try
            {
                var c = report.cases[index]; fluid.StepSimulation(1); c.completed++;
                uint fault = fluid.SynchronizeDiagnostics();
                if (fault != 0 || c.completed % 6 == 0)
                {
                    var s = fluid.CaptureSnapshot(); c.samples.Add(s); c.maxEnergyRatio = Math.Max(c.maxEnergyRatio, Energy(s) / c.initialEnergy);
                    if (c.completed >= 30) { c.maxHeightAfterImpact = Mathf.Max(c.maxHeightAfterImpact, s.maximumWorldHeight); c.maxP99AfterImpact = Mathf.Max(c.maxP99AfterImpact, s.p99WorldHeight); }
                }
                if (fault == 0 && c.completed < 360) return;
                c.status = fault == 0 ? "passed" : "fault " + fault; Write(); index++;
                if (index < report.cases.Count) { Begin(); return; }
                report.status = "complete"; Write(); Stop();
            }
            catch (Exception e) { report.status = e.ToString(); Write(); Stop(); Debug.LogException(e); }
        }
        static void Write() { Directory.CreateDirectory("build/Reports"); File.WriteAllText(outputPath, JsonUtility.ToJson(report, true)); }
        static void Cleanup() { if (fluid != null) fluid.Release(); if (owner != null) Object.DestroyImmediate(owner); owner = null; fluid = null; }
        static void Stop() { EditorApplication.update -= Advance; AssemblyReloadEvents.beforeAssemblyReload -= Cancel; Cleanup(); }
        static void Cancel() { if (report != null) { report.status = "cancelled"; Write(); } Stop(); }
    }
}

namespace WaterSplash.Editor
{
    // Incremental, deterministic GPU stress test; no scene assets are changed.
    public static class MlsMpmStabilityRegression
    {
        [Serializable] public sealed class Result
        {
            public int particles, grid, substeps, completedTicks, targetTicks;
            public uint faults;
            public string status;
            public MlsMpmFluid.Snapshot snapshot;
        }
        [Serializable] public sealed class Report
        {
            public string utc, gpu, api, status;
            public string scenario = "Same comparison initial volume and sphere trajectory; moving collider, gravity and whitewater. Diagnostic readback every tick. Wall times are deliberately not performance results.";
            public List<Result> cases = new List<Result>();
        }
        static GameObject owner, sphere;
        static MlsMpmFluid fluid;
        static Report report;
        static int index;
        const string Output = "build/Reports/stability-regression.json";

        [MenuItem("Water Splash/Validation/Run Moving Sphere Regression")]
        public static void Start()
        {
            if (report != null && report.status == "running") throw new InvalidOperationException("Regression already running.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode first.");
            report = new Report { utc = DateTime.UtcNow.ToString("O"), gpu = SystemInfo.graphicsDeviceName,
                api = SystemInfo.graphicsDeviceType.ToString(), status = "running" };
            Add(32768, 48, 4, 600); // Explicitly unsafe low-step control, never a selectable default.
            Add(65536, 64, 4, 300); // Reproduce the formerly retained 4-step preset at finer grids.
            Add(131072, 80, 4, 300);
            Add(16384, 32, 4, 600);
            Add(32768, 48, 8, 1200);
            Add(65536, 64, 12, 600);
            Add(131072, 80, 16, 300);
            index = 0;
            EditorApplication.update += Advance;
            AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            EditorApplication.quitting += Cancel;
            BeginCase(); Write();
        }
        static void Add(int count, int grid, int steps, int ticks) => report.cases.Add(new Result {
            particles = count, grid = grid, substeps = steps, targetTicks = ticks, status = "pending" });
        static void BeginCase()
        {
            CleanupObjects();
            var c = report.cases[index];
            owner = new GameObject("Temporary moving-sphere regression") { hideFlags = HideFlags.HideAndDontSave };
            owner.SetActive(false); owner.transform.position = new Vector3(-2, 0, -2);
            sphere = new GameObject("Temporary regression sphere") { hideFlags = HideFlags.HideAndDontSave };
            sphere.transform.position = new Vector3(.95f, .55f, 0);
            fluid = owner.AddComponent<MlsMpmFluid>();
            fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/WaterSplash/Shaders/MlsMpm.compute");
            fluid.showParticles = fluid.showControls = false;
            fluid.particleCount = c.particles; fluid.gridResolution = c.grid; fluid.substeps = c.substeps;
            fluid.sphereObstacle = sphere.transform; fluid.whitewaterCapacity = Mathf.Min(c.particles, 32768);
            fluid.Initialize();
            if (fluid.Error != null) throw new InvalidOperationException(fluid.Error);
            var p = MlsMpmFluid.CreateComparisonBlock(c.particles, out float spacing);
            fluid.SetComparisonInitialPositions(p, spacing);
            c.status = "running";
        }
        static void Advance()
        {
            try
            {
                var c = report.cases[index];
                double t = Math.Max(0, c.completedTicks * MlsMpmFluid.FixedTick - 1);
                sphere.transform.position = new Vector3(.95f + .55f * Mathf.Sin((float)t * 2), .55f, .6f * Mathf.Sin((float)t * 1.4f));
                fluid.StepSimulation(1);
                c.faults = fluid.SynchronizeDiagnostics(); c.completedTicks++;
                if (c.faults != 0 || c.completedTicks >= c.targetTicks)
                {
                    c.snapshot = fluid.CaptureSnapshot();
                    c.status = c.faults == 0 ? "passed" : "fault";
                    Write(); index++;
                    if (index == report.cases.Count) { report.status = "complete"; Write(); Stop(); return; }
                    BeginCase();
                }
                if (c.completedTicks % 60 == 0) Write();
            }
            catch (Exception e) { report.status = e.ToString(); Write(); Stop(); Debug.LogException(e); }
        }
        static void Write() { Directory.CreateDirectory("build/Reports"); File.WriteAllText(Output, JsonUtility.ToJson(report, true)); }
        static void CleanupObjects()
        {
            if (fluid != null) fluid.Release();
            if (owner != null) Object.DestroyImmediate(owner);
            if (sphere != null) Object.DestroyImmediate(sphere);
            owner = sphere = null; fluid = null;
        }
        static void Stop()
        {
            EditorApplication.update -= Advance; AssemblyReloadEvents.beforeAssemblyReload -= Cancel;
            EditorApplication.quitting -= Cancel; CleanupObjects();
        }
        static void Cancel() { if (report != null && report.status == "running") { report.status = "cancelled"; Write(); } Stop(); }
    }
}
