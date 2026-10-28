using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    public static class FluidComparisonGpuAudit
    {
        [Serializable] public sealed class Stage { public string name; public int frames, blocks; public double totalMs, msPerBlock; }
        [Serializable] public sealed class Sample { public double seconds; public float maximum, p99, mean; }
        [Serializable] public sealed class Case
        {
            public string name, status; public bool zibra, foam, rendering = true; public int steps, frames;
            public int particles = 32768, grid = 48; public bool stir;
            public float damping; public double solverMsPerTick, renderMsPerFrame;
            public List<Stage> stages = new List<Stage>();
            public List<Sample> heights = new List<Sample>();
        }
        [Serializable] public sealed class Report
        {
            public string gpu, api, utc, status; public int particles = 32768, mpmGrid = 48, zibraGrid = 43, width = 1280, height = 720;
            public string note = "Editor D3D11 GPU timestamps; one requested 1/60s tick/render frame. 24 warmup frames; at least 180 capture frames and 60 valid solver timestamp frames, up to 1200 frames. GPU timestamps may be sparse. msPerBlock counts actual dispatches; MPM solver sum times substeps, Zibra native sum times iterations. Native internals opaque. Foam is particle whitewater vs native density whitening. Diagnostic readbacks and PNG captures occur outside timing window. Case particle/grid override report defaults.";
            public List<Case> cases = new List<Case>();
        }
        static readonly string[] names = { "MLS-MPM GPU/Clear", "MLS-MPM GPU/Mass + momentum", "MLS-MPM GPU/Stress",
            "MLS-MPM GPU/Grid velocity", "MLS-MPM GPU/G2P + advection", "MLS-MPM GPU/Whitewater",
            "MPM Render/Depth", "MPM Render/Thickness", "MPM Render/Smoothing", "MPM Render/Normals",
            "MPM Render/Foam", "MPM Render/Shading", "MPM Render/Spray",
            "Zibra GPU/Clear SDF", "Zibra GPU/Native solver", "Zibra GPU/Background copy",
            "Zibra GPU/Native surface", "Zibra GPU/Shading", "Zibra GPU/Upscale" };
        static FluidComparisonController controller; static Report report; static Recorder[] recorders;
        static int index, lastFrame, frames; static bool profiler, gpu, preparing; static double started;
        [MenuItem("Water Splash/Comparison/Apply Pool Defaults")]
        public static void Defaults()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode first.");
            var c = Object.FindObjectOfType<FluidComparisonController>();
            c.mlsSubsteps = 8; c.mls.affineDamping = c.mls.divergenceDamping = 0;
            c.targetFps = -1;
            c.mlsRenderer.continuousFoam = true; EditorUtility.SetDirty(c.mlsRenderer); AssetDatabase.SaveAssets();
            c.automaticStir = false; c.paused = false;
            GameObject.Find("Floor (visual only)").transform.localScale = new Vector3(4, .2f, 4);
            foreach (var t in Object.FindObjectsOfType<Transform>(true)) if (t.name == "Container Frame (visual only)") t.gameObject.SetActive(false);
            EditorSceneManager.MarkSceneDirty(c.gameObject.scene); EditorSceneManager.SaveScene(c.gameObject.scene);
        }
        [MenuItem("Water Splash/Benchmark/Run RTX A B GPU Audit")]
        public static void Start()
        {
            if (report != null && report.status == "running") throw new InvalidOperationException("Audit running.");
            controller = Object.FindObjectOfType<FluidComparisonController>();
            if (!EditorApplication.isPlaying || controller == null || !controller.ExpectedGpu) throw new InvalidOperationException("Play comparison on RTX 5070 Ti.");
            report = new Report { gpu = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString(), utc = DateTime.UtcNow.ToString("O"), status = "running" };
            Add("Zibra-1-base", true, 1, false); Add("MPM-4-base", false, 4, false);
            Add("MPM-8-base", false, 8, false); Add("Zibra-1-foam", true, 1, true);
            Add("MPM-4-foam", false, 4, true); Add("Zibra-2-base", true, 2, false);
            Add("MPM-8-foam", false, 8, true);
            Add("MPM-128K-8-foam", false, 8, true);
            report.cases[report.cases.Count - 1].particles = 131072; report.cases[report.cases.Count - 1].grid = 80;
            profiler = UnityEditorInternal.ProfilerDriver.enabled; gpu = UnityEditorInternal.ProfilerDriver.IsAreaEnabled(ProfilerArea.GPU);
            UnityEditorInternal.ProfilerDriver.SetAreaEnabled(ProfilerArea.GPU, true); UnityEditorInternal.ProfilerDriver.enabled = true;
            recorders = new Recorder[names.Length];
            for (int i = 0; i < names.Length; i++) { recorders[i] = Recorder.Get(names[i]); recorders[i].enabled = true; }
            index = 0; Begin(); EditorApplication.update += Advance; EditorApplication.playModeStateChanged += PlayChanged; AssemblyReloadEvents.beforeAssemblyReload += Cancel;
        }
        static void Add(string name, bool zibra, int steps, bool foam, float damping = 0) =>
            report.cases.Add(new Case { name = name, zibra = zibra, steps = steps, foam = foam, damping = damping, status = "pending" });
        static void Begin()
        {
            var c = report.cases[index]; c.status = "rebuilding";
            foreach (string name in names) c.stages.Add(new Stage { name = name });
            controller.paused = true; controller.automaticStir = c.stir; controller.particleCount = c.particles; controller.storageGrid = c.grid;
            controller.mlsSubsteps = c.zibra ? 4 : c.steps; controller.zibraIterations = c.zibra ? c.steps : 1;
            controller.whitewater = c.foam; controller.renderWater = c.rendering; controller.targetFps = 60;
            controller.mls.soundSpeed = 6; controller.mls.pressureExponent = 3; controller.mls.viscosity = .15f;
            controller.mls.affineDamping = controller.mls.divergenceDamping = c.damping;
            controller.waterQuality = MlsMpmFluid.SurfaceQuality.High; controller.SetInternalResolution(1280, 720);
            controller.SelectView(c.zibra ? FluidComparisonController.ViewMode.Zibra : FluidComparisonController.ViewMode.MlsMpm);
            frames = 0; lastFrame = -1; preparing = true; started = EditorApplication.timeSinceStartup; Save();
        }
        static void Advance()
        {
            try
            {
                if (!EditorApplication.isPlaying || controller == null) { Cancel(); return; }
                if (EditorApplication.timeSinceStartup - started > 100) throw new TimeoutException("GPU audit case timeout.");
                if (controller.IsRebuilding || lastFrame == Time.frameCount) return;
                lastFrame = Time.frameCount;
                if (controller.Error != null) throw new InvalidOperationException(controller.Error);
                var c = report.cases[index];
                if (preparing) { if (controller.SimulatedSeconds < 2 * MlsMpmFluid.FixedTick - 1e-6) return; preparing = false; c.status = "capturing"; }
                frames++;
                if (frames > 24)
                    for (int i = 0; i < recorders.Length; i++)
                    {
                        long ns = recorders[i].gpuElapsedNanoseconds; int blocks = recorders[i].gpuSampleBlockCount;
                        if (ns <= 0 || blocks <= 0) continue;
                        var s = c.stages[i]; s.frames++; s.blocks += blocks; s.totalMs += ns / 1e6; s.msPerBlock = s.totalMs / s.blocks;
                    }
                if (frames >= 204 && c.stages[c.zibra ? 14 : 1].frames >= 60 || frames >= 1224)
                {
                    c.frames = frames - 24;
                    for (int i = 0; i < c.stages.Count; i++)
                    {
                        var s = c.stages[i];
                        if (c.zibra && (i == 13 || i == 14)) c.solverMsPerTick += s.msPerBlock * c.steps;
                        if (!c.zibra && i < 5) c.solverMsPerTick += s.msPerBlock * c.steps;
                        if (!c.zibra && i == 5) c.solverMsPerTick += s.msPerBlock;
                        if (c.zibra && i >= 15 || !c.zibra && i >= 6 && i <= 12) c.renderMsPerFrame += s.msPerBlock;
                    }
                    if (!c.zibra && controller.mls.SynchronizeDiagnostics() != 0) throw new InvalidOperationException("MPM final diagnostic failed.");
                    c.status = c.solverMsPerTick > 0 ? "captured" : "GPU timestamps unavailable";
                    CaptureImage(c.name); CaptureHeight(c); Save(); index++;
                    if (index == report.cases.Count) { report.status = "complete"; Save(); Stop(); return; }
                    Begin(); return;
                }
                controller.StepOnce();
            }
            catch (Exception e) { report.status = e.ToString(); Save(); Stop(); Debug.LogException(e); }
        }
        static void CaptureHeight(Case c)
        {
            if (!c.zibra) { var s = controller.mls.CaptureSnapshot(); c.heights.Add(new Sample { seconds = s.simulatedSeconds, maximum = s.maximumWorldHeight, p99 = s.p99WorldHeight }); return; }
            var positions = new Vector4[controller.particleCount]; controller.zibra.PositionMass.GetData(positions);
            var heights = new float[positions.Length]; float sum = 0;
            for (int i = 0; i < heights.Length; i++) { heights[i] = positions[i].y * 4 / controller.zibra.GridSize.y; sum += heights[i]; }
            Array.Sort(heights); c.heights.Add(new Sample { seconds = controller.SimulatedSeconds, maximum = heights[heights.Length - 1], p99 = heights[(int)(heights.Length * .99)], mean = sum / heights.Length });
        }
        public static void CaptureImage(string name)
        {
            var rt = controller.comparisonCamera.targetTexture; var previous = RenderTexture.active; RenderTexture.active = rt;
            var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); texture.Apply();
            Directory.CreateDirectory("BenchmarkResults/Captures"); File.WriteAllBytes("BenchmarkResults/Captures/" + name + ".png", texture.EncodeToPNG());
            Object.DestroyImmediate(texture); RenderTexture.active = previous;
        }
        static int foamCapturePhase; static double foamCaptureTime; static bool previousFoam;
        [MenuItem("Water Splash/Benchmark/Capture Foam Before After")]
        public static void FoamBeforeAfter()
        {
            controller = Object.FindObjectOfType<FluidComparisonController>();
            if (!EditorApplication.isPlaying || controller == null || controller.IsRebuilding) throw new InvalidOperationException("Run MPM comparison first.");
            controller.paused = true; previousFoam = controller.mlsRenderer.continuousFoam;
            controller.mlsRenderer.continuousFoam = false; foamCapturePhase = 0; foamCaptureTime = EditorApplication.timeSinceStartup + .5;
            EditorApplication.update -= AdvanceFoamCapture; EditorApplication.update += AdvanceFoamCapture;
        }
        static void AdvanceFoamCapture()
        {
            if (!EditorApplication.isPlaying || controller == null) { EditorApplication.update -= AdvanceFoamCapture; return; }
            if (EditorApplication.timeSinceStartup < foamCaptureTime) return;
            CaptureImage(foamCapturePhase == 0 ? "Foam-before" : "Foam-after");
            if (foamCapturePhase++ == 0) { controller.mlsRenderer.continuousFoam = true; foamCaptureTime = EditorApplication.timeSinceStartup + .5; }
            else { controller.mlsRenderer.continuousFoam = previousFoam; EditorApplication.update -= AdvanceFoamCapture; }
        }
        static void PlayChanged(PlayModeStateChange state) { if (state == PlayModeStateChange.ExitingPlayMode) Cancel(); }
        static void Cancel() { if (report != null && report.status == "running") { report.status = "cancelled"; Save(); Stop(); } }
        static void Save() { Directory.CreateDirectory("BenchmarkResults"); File.WriteAllText("BenchmarkResults/rtx-ab-gpu.json", JsonUtility.ToJson(report, true)); }
        static void Stop()
        {
            EditorApplication.update -= Advance; EditorApplication.playModeStateChanged -= PlayChanged; AssemblyReloadEvents.beforeAssemblyReload -= Cancel;
            if (recorders != null) foreach (var r in recorders) r.enabled = false; recorders = null;
            UnityEditorInternal.ProfilerDriver.enabled = profiler; UnityEditorInternal.ProfilerDriver.SetAreaEnabled(ProfilerArea.GPU, gpu);
            if (controller != null) controller.paused = true;
        }
    }
}
