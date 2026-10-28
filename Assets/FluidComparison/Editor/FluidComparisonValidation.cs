using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    public static class FluidComparisonValidation
    {
        [Serializable] sealed class SwitchCase
        {
            public string mode, status; public int particles, grid, substeps, ticks;
            public MlsMpmFluid.Snapshot snapshot;
        }
        [Serializable] sealed class SwitchReport
        {
            public string utc, gpu, status;
            public List<SwitchCase> cases = new List<SwitchCase>();
        }
        static FluidComparisonController controller;
        static SwitchReport switching;
        static int caseIndex, lastFrame;
        static double startTime, nextTick;
        static readonly int[] counts = { 32768, 32768, 65536, 131072, 16384, 32768, 32768, 32768 };
        static readonly int[] grids = { 48, 48, 64, 80, 32, 48, 48, 48 };
        static readonly bool[] zibra = { true, false, false, false, false, true, false, false };
        const string SwitchPath = "BenchmarkResults/runtime-switch-regression.json";

        [MenuItem("Water Splash/Validation/Run Runtime Switch Regression")]
        public static void RunSwitches()
        {
            if (switching != null && switching.status == "running") throw new InvalidOperationException("Switch regression already running.");
            controller = Object.FindObjectOfType<FluidComparisonController>();
            if (!EditorApplication.isPlaying || controller == null) throw new InvalidOperationException("Play the single pool comparison first.");
            switching = new SwitchReport { utc = DateTime.UtcNow.ToString("O"), gpu = SystemInfo.graphicsDeviceName, status = "running" };
            controller.paused = true; caseIndex = 0; lastFrame = -1;
            EditorApplication.update += AdvanceSwitch;
            EditorApplication.playModeStateChanged += OnPlayMode;
            BeginSwitch();
        }
        static void BeginSwitch()
        {
            controller.mlsSubsteps = MlsMpmFluid.RecommendedSubsteps(grids[caseIndex]);
            controller.SelectPreset(counts[caseIndex], grids[caseIndex]);
            controller.SelectView(zibra[caseIndex] ? FluidComparisonController.ViewMode.Zibra : FluidComparisonController.ViewMode.MlsMpm);
            // Two reset requests in one frame intentionally exercise cancelled rebuilds.
            controller.ResetComparison();
            switching.cases.Add(new SwitchCase { mode = controller.view.ToString(), particles = controller.particleCount,
                grid = controller.storageGrid, substeps = controller.mlsSubsteps, status = "rebuilding" });
            startTime = EditorApplication.timeSinceStartup; nextTick = -1; SaveSwitch();
        }
        static void AdvanceSwitch()
        {
            try
            {
                if (!EditorApplication.isPlaying || controller == null) throw new InvalidOperationException("Play Mode ended.");
                if (EditorApplication.timeSinceStartup - startTime > 150) throw new TimeoutException("Switch scenario exceeded 150 wall seconds.");
                if (controller.IsRebuilding || lastFrame == Time.frameCount) return;
                lastFrame = Time.frameCount;
                if (controller.Error != null) throw new InvalidOperationException(controller.Error);
                var c = switching.cases[caseIndex];
                if (controller.mls.gameObject.activeInHierarchy == controller.zibra.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("Exactly one solver must be active.");
                if (controller.mlsSubsteps != MlsMpmFluid.RecommendedSubsteps(c.grid))
                    throw new InvalidOperationException("Explicit regression substep setting was not preserved.");
                if (controller.SimulatedSeconds < nextTick) return;
                if (controller.view == FluidComparisonController.ViewMode.MlsMpm)
                {
                    uint faults = controller.mls.SynchronizeDiagnostics();
                    if (faults != 0) throw new InvalidOperationException("GPU fault " + faults + " in scenario " + caseIndex);
                }
                c.status = "running"; c.ticks = (int)Math.Round(controller.SimulatedSeconds / MlsMpmFluid.FixedTick);
                int target = zibra[caseIndex] ? 12 : 180;
                if (c.ticks >= target)
                {
                    if (!zibra[caseIndex])
                    {
                        c.snapshot = controller.mls.CaptureSnapshot();
                        if (c.snapshot.faults != 0 || c.snapshot.nonFiniteParticles != 0 || c.snapshot.outsideParticles != 0)
                            throw new InvalidOperationException("Invalid final particle state.");
                    }
                    c.status = "passed"; caseIndex++; SaveSwitch();
                    if (caseIndex == counts.Length) { switching.status = "passed"; SaveSwitch(); StopSwitch(); return; }
                    BeginSwitch(); return;
                }
                nextTick = controller.SimulatedSeconds + MlsMpmFluid.FixedTick * .9;
                controller.StepOnce();
            }
            catch (Exception e) { switching.status = e.ToString(); SaveSwitch(); StopSwitch(); Debug.LogException(e); }
        }
        static void SaveSwitch() { Directory.CreateDirectory("BenchmarkResults"); File.WriteAllText(SwitchPath, JsonUtility.ToJson(switching, true)); }
        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode && switching != null && switching.status == "running")
            { switching.status = "cancelled"; SaveSwitch(); StopSwitch(); }
        }
        static void StopSwitch() { EditorApplication.update -= AdvanceSwitch; EditorApplication.playModeStateChanged -= OnPlayMode; if (controller != null) controller.paused = true; }

        [Serializable] sealed class Stage
        {
            public string name; public int frames, blocks; public double totalMs, msPerBlock;
        }
        [Serializable] sealed class StageReport
        {
            public string utc, gpu, api, status; public int particles, grid, substeps;
            public string note = "Unity GPU timestamp recorders, delayed three frames. msPerBlock is per dispatch, not per frame. No CPU submission time is mislabeled GPU time. Capture only one solver, steady workload.";
            public List<Stage> stages = new List<Stage>();
        }
        static readonly string[] stageNames = { "MLS-MPM GPU/Clear", "MLS-MPM GPU/Mass + momentum", "MLS-MPM GPU/Stress",
            "MLS-MPM GPU/Grid velocity", "MLS-MPM GPU/G2P + advection", "MLS-MPM GPU/Whitewater", "MLS-MPM/Continuous water" };
        static StageReport timing; static Recorder[] recorders; static double captureStart;
        static bool wasProfiler, wasGpu; static int captureFrame, framesSeen;
        [MenuItem("Water Splash/Benchmark/Capture MLS GPU Stages")]
        public static void CaptureStages()
        {
            if (recorders != null) throw new InvalidOperationException("GPU capture already running.");
            controller = Object.FindObjectOfType<FluidComparisonController>();
            if (!EditorApplication.isPlaying || controller == null || controller.view != FluidComparisonController.ViewMode.MlsMpm || controller.paused)
                throw new InvalidOperationException("Run the MLS-MPM comparison first.");
            timing = new StageReport { utc = DateTime.UtcNow.ToString("O"), gpu = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString(),
                particles = controller.particleCount, grid = controller.storageGrid, substeps = controller.mlsSubsteps, status = "running" };
            wasProfiler = UnityEditorInternal.ProfilerDriver.enabled;
            wasGpu = UnityEditorInternal.ProfilerDriver.IsAreaEnabled(ProfilerArea.GPU);
            UnityEditorInternal.ProfilerDriver.SetAreaEnabled(ProfilerArea.GPU, true);
            UnityEditorInternal.ProfilerDriver.enabled = true;
            recorders = new Recorder[stageNames.Length];
            for (int i = 0; i < recorders.Length; i++)
            {
                recorders[i] = Recorder.Get(stageNames[i]); recorders[i].enabled = true;
                timing.stages.Add(new Stage { name = stageNames[i] });
            }
            captureStart = EditorApplication.timeSinceStartup; captureFrame = -1; framesSeen = 0;
            EditorApplication.update += AdvanceCapture;
        }
        static void AdvanceCapture()
        {
            if (!EditorApplication.isPlaying || controller == null || controller.Error != null || controller.IsRebuilding || controller.paused ||
                controller.particleCount != timing.particles || controller.mlsSubsteps != timing.substeps)
            { FinishCapture("cancelled: workload changed or stopped"); return; }
            if (captureFrame == Time.frameCount) return;
            captureFrame = Time.frameCount; framesSeen++;
            if (framesSeen > 15) for (int i = 0; i < recorders.Length; i++)
            {
                long ns = recorders[i].gpuElapsedNanoseconds; int blocks = recorders[i].gpuSampleBlockCount;
                if (ns <= 0 || blocks <= 0) continue;
                var s = timing.stages[i]; s.frames++; s.blocks += blocks; s.totalMs += ns / 1000000.0; s.msPerBlock = s.totalMs / s.blocks;
            }
            if (framesSeen >= 150 || EditorApplication.timeSinceStartup - captureStart > 40)
                FinishCapture(timing.stages[1].frames > 0 ? "captured" : "GPU timestamps unavailable; no stage timing measured");
        }
        static void FinishCapture(string status)
        {
            timing.status = status; EditorApplication.update -= AdvanceCapture;
            foreach (var r in recorders) r.enabled = false; recorders = null;
            UnityEditorInternal.ProfilerDriver.enabled = wasProfiler; UnityEditorInternal.ProfilerDriver.SetAreaEnabled(ProfilerArea.GPU, wasGpu);
            Directory.CreateDirectory("BenchmarkResults"); File.WriteAllText("BenchmarkResults/gpu-stages.json", JsonUtility.ToJson(timing, true));
        }
    }
}
