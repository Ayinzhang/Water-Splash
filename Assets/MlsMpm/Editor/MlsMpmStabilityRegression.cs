using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

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
        const string Output = "BenchmarkResults/stability-regression.json";

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
            fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/MlsMpm.compute");
            fluid.showParticles = fluid.showControls = false;
            fluid.particleCount = c.particles; fluid.gridResolution = c.grid; fluid.substeps = c.substeps;
            fluid.sphereObstacle = sphere.transform; fluid.whitewaterCapacity = Mathf.Min(c.particles, 32768);
            fluid.Initialize();
            if (fluid.Error != null) throw new InvalidOperationException(fluid.Error);
            int side = Mathf.CeilToInt(Mathf.Pow(c.particles, 1f / 3)); while (side * side * side < c.particles) side++;
            float spacing = (4f * 16 / 27) / side;
            var p = new Vector3[c.particles];
            for (int i = 0; i < p.Length; i++) p[i] = new Vector3(-2 + 4f / 27, 4f / 27, -2 + 24f / 27) +
                spacing * new Vector3(i % side + .5f, (i / side) % side + .5f, i / (side * side) + .5f);
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
        static void Write() { Directory.CreateDirectory("BenchmarkResults"); File.WriteAllText(Output, JsonUtility.ToJson(report, true)); }
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
