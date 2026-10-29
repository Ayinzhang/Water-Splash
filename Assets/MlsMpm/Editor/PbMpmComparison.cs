using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    public static class PbMpmComparison
    {
        [Serializable] public sealed class Result
        {
            public string scenario, solver;
            public int particles, grid, substeps = 8, iterations, completedTicks;
            public double completedWallMsPerTick, rmsWorldSpeed;
            public MlsMpmFluid.Snapshot snapshot;
        }

        [Serializable] public sealed class Report
        {
            public string utc, gpu, api;
            public string timing = "GPU-completed wall time, readback every 10 ticks; excludes rendering, not GPU timestamps or phone FPS.";
            public string settings = "Same positions, spacing and collider trajectory within each pair; 8 substeps, whitewater off, original unsorted two-pass MLS, PB sort every tick / relaxation 1.5 / viscosity 0.01 / grid-volume blend 0.1.";
            public List<Result> cases = new List<Result>();
        }

        [MenuItem("Water Splash/Benchmark/Run PB-MPM Desktop Comparison")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play Mode before running the comparison.");
            var report = new Report { utc = DateTime.UtcNow.ToString("O"), gpu = SystemInfo.graphicsDeviceName,
                api = SystemInfo.graphicsDeviceType.ToString() };
            foreach (int count in new[] { 32768, 65536 })
                foreach (bool pb in new[] { false, true }) RunCase(report, count, pb, true);
            foreach (int count in new[] { 32768, 65536, 131072 })
                foreach (bool pb in new[] { false, true }) RunCase(report, count, pb, false);
            UnityEngine.Debug.Log("PB-MPM comparison saved to BenchmarkResults/pbmpm-original-mls-comparison.json");
        }

        static void RunCase(Report report, int count, bool pb, bool pool)
        {
            GameObject owner = null, sphere = null;
            MlsMpmFluid fluid = null;
            var result = new Result { scenario = pool ? "Resting pool, 20 simulated seconds" : "Dam break and moving sphere, 5 simulated seconds",
                solver = pb ? "PB-MPM" : "MLS-MPM original two-pass", particles = count,
                grid = MlsMpmFluid.PresetGridResolution(count), iterations = pb ? 2 : 0 };
            try
            {
                owner = new GameObject("Temporary solver comparison") { hideFlags = HideFlags.HideAndDontSave };
                owner.SetActive(false);
                owner.transform.position = new Vector3(-2, 0, -2);
                fluid = owner.AddComponent<MlsMpmFluid>();
                fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/MlsMpm.compute");
                fluid.showParticles = fluid.showControls = fluid.enableWhitewater = false;
                fluid.particleCount = count;
                fluid.gridResolution = result.grid;
                fluid.substeps = 8;
                fluid.solverMethod = pb ? MlsMpmFluid.SolverMethod.PositionBasedMpm : MlsMpmFluid.SolverMethod.MlsMpm;
                if (!pool)
                {
                    sphere = new GameObject("Temporary comparison sphere") { hideFlags = HideFlags.HideAndDontSave };
                    sphere.transform.position = new Vector3(.95f, .55f, 0);
                    fluid.sphereObstacle = sphere.transform;
                }
                fluid.Initialize();
                if (fluid.Error != null) throw new InvalidOperationException(fluid.Error);
                if (pool)
                {
                    var positions = new Vector3[count];
                    for (int i = 0; i < count; i++)
                        positions[i] = new Vector3(-2, 0, -2) +
                            .0625f * new Vector3(i % 64 + .5f, i / 4096 + .5f, (i / 64) % 64 + .5f);
                    fluid.SetComparisonInitialPositions(positions, .0625f);
                }
                int target = pool ? 1200 : 300;
                int timedTicks = 0;
                var clock = new Stopwatch();
                for (int tick = 0; tick < target; tick++)
                {
                    if (tick == target - 120) { fluid.SynchronizeDiagnostics(); clock.Start(); }
                    if (sphere != null)
                    {
                        float time = Mathf.Max(0, tick / 60f - 1);
                        sphere.transform.position = new Vector3(.95f + .55f * Mathf.Sin(time * 2), .55f, .6f * Mathf.Sin(time * 1.4f));
                    }
                    fluid.StepSimulation(1);
                    result.completedTicks++;
                    if (clock.IsRunning) timedTicks++;
                    if (tick % 10 == 9 && fluid.SynchronizeDiagnostics() != 0) break;
                }
                fluid.SynchronizeDiagnostics();
                clock.Stop();
                result.completedWallMsPerTick = timedTicks > 0 ? clock.Elapsed.TotalMilliseconds / timedTicks : 0;
                result.snapshot = fluid.CaptureSnapshot();
                result.rmsWorldSpeed = Math.Sqrt(2 * result.snapshot.meanKineticEnergy);
                report.cases.Add(result);
                Directory.CreateDirectory("BenchmarkResults");
                File.WriteAllText("BenchmarkResults/pbmpm-original-mls-comparison.json", JsonUtility.ToJson(report, true));
                if (result.snapshot.faults != 0)
                    UnityEngine.Debug.LogError("Solver comparison fault: " + JsonUtility.ToJson(result));
            }
            finally
            {
                if (fluid != null) fluid.Release();
                if (owner != null) Object.DestroyImmediate(owner);
                if (sphere != null) Object.DestroyImmediate(sphere);
            }
        }
    }
}
