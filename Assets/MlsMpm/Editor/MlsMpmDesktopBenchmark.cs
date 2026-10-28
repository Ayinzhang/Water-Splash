using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    // Deliberately measures completed solver work, not a capped editor frame rate.
    // The wall time includes CPU submission and a 32-byte synchronous readback.
    public static class MlsMpmDesktopBenchmark
    {
        [Serializable]
        public sealed class Report
        {
            public string capturedUtc;
            public string unityVersion;
            public string device;
            public string graphicsApi;
            public string operatingSystem;
            public string measurement = "Synchronized solver wall time: CPU submission + GPU completion + 32-byte readback; no rendering. Not isolated GPU timing.";
            public string scenario = "Fixed 4m container, fixed initial water block, sphere at (0.95,0.55,0), kick every two simulation seconds starting at second 1.";
            public string note = "Desktop solver diagnostic, not an equal-volume Zibra comparison or a phone/frame-rate result.";
            public int particles = 32768;
            public int grid = 48;
            public int substeps = 8;
            public int warmupTicks = 12;
            public int measuredTicks = 60;
            public int validatedTicks = 600;
            public bool whitewater = true;
            public string status;
            public double millisecondsPerTick;
            public double simulatedSecondsPerWallSecond;
            public double medianBatchMillisecondsPerTick;
            public double p95BatchMillisecondsPerTick;
            public MlsMpmFluid.Snapshot finalState;
            public int peakFoam;
            public int peakSpray;
            public uint whitewaterBorn;
            public uint whitewaterExpired;
            public int invalidWhitewater;
            public List<double> batchMillisecondsPerTick = new List<double>();
        }

        static GameObject owner;
        static GameObject sphere;
        static MlsMpmFluid fluid;
        static Report report;
        static int completed;
        static int measured;
        static double elapsedMilliseconds;
        static string label;
        const int BatchTicks = 4;

        [MenuItem("Water Splash/Benchmark/Run 32K Solver (Whitewater On)")]
        public static void RunFull() => Start(true, 8);

        [MenuItem("Water Splash/Benchmark/Run 32K Solver (Whitewater Off)")]
        public static void RunSolverOnly() => Start(false, 8);

        [MenuItem("Water Splash/Benchmark/Run 32K Solver (4 Substeps Experimental)")]
        public static void RunFourSubsteps() => Start(true, 4);

        [MenuItem("Water Splash/Benchmark/Run 32K Solver (6 Substeps Experimental)")]
        public static void RunSixSubsteps() => Start(true, 6);

        public static void Start(bool whitewater, int substeps)
        {
            if (fluid != null) throw new InvalidOperationException("A benchmark is already running.");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("Run the solver benchmark outside Play Mode and compilation.");
            try
            {
                report = new Report {
                    capturedUtc = DateTime.UtcNow.ToString("O"),
                    unityVersion = Application.unityVersion,
                    device = SystemInfo.graphicsDeviceName,
                    graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                    operatingSystem = SystemInfo.operatingSystem,
                    whitewater = whitewater, substeps = substeps, status = "running"
                };
                label = (whitewater ? "whitewater-on" : "whitewater-off") + "-substeps-" + substeps;
                completed = measured = 0;
                elapsedMilliseconds = 0;
                owner = new GameObject("Temporary MLS-MPM benchmark") { hideFlags = HideFlags.HideAndDontSave };
                owner.SetActive(false);
                owner.transform.position = new Vector3(-2, 0, -2);
                sphere = new GameObject("Temporary benchmark sphere") { hideFlags = HideFlags.HideAndDontSave };
                sphere.transform.position = new Vector3(0.95f, 0.55f, 0);
                fluid = owner.AddComponent<MlsMpmFluid>();
                fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/MlsMpm.compute");
                fluid.showParticles = fluid.showControls = false;
                fluid.particleCount = report.particles;
                fluid.gridResolution = report.grid;
                fluid.substeps = report.substeps;
                fluid.sphereObstacle = sphere.transform;
                fluid.enableWhitewater = whitewater;
                fluid.Initialize();
                if (fluid.Error != null) throw new InvalidOperationException(fluid.Error);
                fluid.SynchronizeDiagnostics();
                WriteReport();
                EditorApplication.update += Advance;
                AssemblyReloadEvents.beforeAssemblyReload += Cancel;
                EditorApplication.quitting += Cancel;
                Debug.Log("MLS-MPM benchmark started: " + report.device + " / " + report.graphicsApi +
                    ". Validating ten simulation seconds; no scene assets are changed.");
            }
            catch { Cleanup(); throw; }
        }

        static void Advance()
        {
            try
            {
                if (completed >= 60 && (completed - 60) % 120 == 0) fluid.Kick();
                var timer = Stopwatch.StartNew();
                fluid.StepSimulation(BatchTicks);
                uint faults = fluid.SynchronizeDiagnostics();
                timer.Stop();
                if (faults != 0) throw new InvalidOperationException("GPU fault bits: " + faults);
                if (completed >= report.warmupTicks && measured < report.measuredTicks)
                {
                    elapsedMilliseconds += timer.Elapsed.TotalMilliseconds;
                    measured += BatchTicks;
                    report.batchMillisecondsPerTick.Add(timer.Elapsed.TotalMilliseconds / BatchTicks);
                }
                completed += BatchTicks;
                if (completed % 20 == 0)
                {
                    MlsMpmFluid.WhitewaterSnapshot water = fluid.CaptureWhitewaterSnapshot();
                    report.peakFoam = Mathf.Max(report.peakFoam, water.foam);
                    report.peakSpray = Mathf.Max(report.peakSpray, water.spray);
                    report.whitewaterBorn = water.born;
                    report.whitewaterExpired = water.expired;
                    report.invalidWhitewater += water.nonFinite + water.outside + water.insideSphere;
                    if (report.invalidWhitewater != 0) throw new InvalidOperationException("Invalid secondary particles.");
                }
                if (completed < report.validatedTicks) return;
                report.finalState = fluid.CaptureSnapshot();
                if (report.finalState.faults != 0 || report.finalState.nonFiniteParticles != 0 ||
                    report.finalState.outsideParticles != 0 || report.finalState.insideSphereParticles != 0)
                    throw new InvalidOperationException("Invalid final primary particle state.");
                report.millisecondsPerTick = elapsedMilliseconds / measured;
                report.simulatedSecondsPerWallSecond = (measured * MlsMpmFluid.FixedTick) / (elapsedMilliseconds / 1000);
                var sorted = new List<double>(report.batchMillisecondsPerTick);
                sorted.Sort();
                report.medianBatchMillisecondsPerTick = sorted[sorted.Count / 2];
                report.p95BatchMillisecondsPerTick = sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1];
                report.status = "complete";
                WriteReport();
                Debug.Log(string.Format("MLS-MPM benchmark: {0:F2} ms/completed tick, {1:F2}x real-time solver throughput. {2}. Report: {3}",
                    report.millisecondsPerTick, report.simulatedSecondsPerWallSecond, label, ReportPath));
                Cleanup();
            }
            catch (Exception error)
            {
                report.status = "failed: " + error.Message;
                WriteReport();
                Debug.LogException(error);
                Cleanup();
            }
        }

        static string ReportPath => Path.Combine(Directory.GetParent(Application.dataPath).FullName,
            "BenchmarkResults", "mlsmpm-32k-" + label + "-" + report.capturedUtc.Replace(":", "").Replace(".", "") + ".json");

        static void WriteReport()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
        }

        static void Cancel()
        {
            if (report != null && report.status == "running")
            {
                report.status = "cancelled";
                WriteReport();
            }
            Cleanup();
        }

        static void Cleanup()
        {
            EditorApplication.update -= Advance;
            AssemblyReloadEvents.beforeAssemblyReload -= Cancel;
            EditorApplication.quitting -= Cancel;
            if (fluid != null) fluid.Release();
            if (owner != null) Object.DestroyImmediate(owner);
            if (sphere != null) Object.DestroyImmediate(sphere);
            owner = sphere = null;
            fluid = null;
        }
    }
}
