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
        static string outputPath = "BenchmarkResults/energy-audit-rtx.json";
        [MenuItem("Water Splash/Validation/Run Affine Damping Audit")]
        public static void StartDamping()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || fluid != null) throw new InvalidOperationException("Stop other runs first.");
            outputPath = "BenchmarkResults/affine-damping-rtx.json";
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
            outputPath = "BenchmarkResults/energy-audit-rtx.json";
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
            fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/MlsMpm.compute");
            fluid.particleCount = report.particles; fluid.gridResolution = report.grid; fluid.substeps = c.steps; fluid.soundSpeed = c.sound; fluid.pressureExponent = c.exponent;
            fluid.affineDamping = c.affineDamping; fluid.divergenceDamping = c.divergenceDamping;
            fluid.showParticles = fluid.showControls = fluid.enableWhitewater = false; fluid.Initialize();
            int side = 32; float spacing = (4f * 16 / 27) / side; var positions = new Vector3[report.particles];
            for (int i = 0; i < positions.Length; i++) positions[i] = new Vector3(-2 + 4f / 27, 4f / 27, -2 + 24f / 27) + spacing * new Vector3(i % side + .5f, i / side % side + .5f, i / (side * side) + .5f);
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
        static void Write() { Directory.CreateDirectory("BenchmarkResults"); File.WriteAllText(outputPath, JsonUtility.ToJson(report, true)); }
        static void Cleanup() { if (fluid != null) fluid.Release(); if (owner != null) Object.DestroyImmediate(owner); owner = null; fluid = null; }
        static void Stop() { EditorApplication.update -= Advance; AssemblyReloadEvents.beforeAssemblyReload -= Cancel; Cleanup(); }
        static void Cancel() { if (report != null) { report.status = "cancelled"; Write(); } Stop(); }
    }
}
