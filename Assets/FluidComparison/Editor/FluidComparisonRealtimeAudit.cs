using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    public static class FluidComparisonRealtimeAudit
    {
        [Serializable] sealed class Report
        {
            public string status, gpu, api, utc; public int particles = 131072, grid = 80, steps = 8;
            public double wallSeconds, simulatedSeconds, backlogSeconds; public uint faults;
            public MlsMpmFluid.Snapshot finalState;
            public string note = "128K 80^3 8 substeps, 720p High, whitewater and deterministic moving sphere, 60 FPS cap. Editor wall frame measurements exported separately. No diagnostic sync inside timing window; final sync after pausing.";
        }
        static Report report; static FluidComparisonController controller; static double start; static bool ready;
        [MenuItem("Water Splash/Benchmark/Run 128K Eight Steps Realtime")]
        public static void Start()
        {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Play comparison first.");
            controller = Object.FindObjectOfType<FluidComparisonController>();
            report = new Report { status = "running", gpu = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString(), utc = DateTime.UtcNow.ToString("O") };
            controller.paused = true; controller.mlsSubsteps = 8; controller.particleCount = 131072; controller.storageGrid = 80;
            controller.mls.affineDamping = controller.mls.divergenceDamping = 0;
            controller.whitewater = controller.renderWater = controller.automaticStir = true;
            controller.targetFps = 60; controller.waterQuality = MlsMpmFluid.SurfaceQuality.High; controller.SetInternalResolution(1280,720);
            controller.SelectView(FluidComparisonController.ViewMode.MlsMpm); ready = false;
            EditorApplication.update -= Advance; EditorApplication.update += Advance; Save();
        }
        static void Advance()
        {
            if (!EditorApplication.isPlaying || controller == null) { report.status = "cancelled"; Save(); EditorApplication.update -= Advance; return; }
            if (controller.IsRebuilding) return;
            if (!ready) { ready = true; start = EditorApplication.timeSinceStartup; controller.paused = false; }
            report.wallSeconds = EditorApplication.timeSinceStartup - start;
            if (controller.Error == null && report.wallSeconds < 20) return;
            controller.paused = true; report.status = controller.Error ?? "completed";
            report.simulatedSeconds = controller.SimulatedSeconds; report.backlogSeconds = controller.BacklogSeconds;
            report.faults = controller.mls.SynchronizeDiagnostics(); report.finalState = controller.mls.CaptureSnapshot();
            if (report.faults != 0) report.status = "fault " + report.faults;
            controller.SaveMeasurement(); Save(); EditorApplication.update -= Advance;
        }
        static void Save() { Directory.CreateDirectory("BenchmarkResults"); File.WriteAllText("BenchmarkResults/128k-eight-realtime.json", JsonUtility.ToJson(report,true)); }
    }
}
