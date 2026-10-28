using System;
using System.Collections;
using System.IO;
using com.zibra.liquid;
using com.zibra.liquid.Solver;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WaterSplash
{
    [DefaultExecutionOrder(-100)]
    public sealed class FluidComparisonController : MonoBehaviour
    {
        public enum ViewMode { Zibra, MlsMpm }
        public ViewMode view = ViewMode.MlsMpm;
        public MlsMpmFluid mls;
        public ZibraLiquid zibra;
        public Transform mlsSphere;
        public Transform zibraSphere;
        public Camera comparisonCamera;
        public Transform mlsStage;
        public Transform zibraStage;
        public UniversalRenderPipelineAsset pipeline;
        public LiquidURPRenderComponent zibraRenderer;
        public MlsMpmSurfaceFeature mlsRenderer;
        public TextAsset[] bakedStates;
        public int particleCount = 32768;
        public int storageGrid = 48;
        [Range(1, 32)] public int mlsSubsteps = 8;
        [Range(1, 10)] public int zibraIterations = 1;
        [Range(1, 4)] public int maxCatchUpTicks = 2;
        public bool whitewater = true;
        public bool renderWater = true;
        public bool paused;
        public bool automaticStir;
        public bool require5070TiForMeasurements = true;
        public float gravity = -9.81f;
        public float sphereRadius = 0.42f;
        public MlsMpmFluid.SurfaceQuality waterQuality = MlsMpmFluid.SurfaceQuality.High;
        public int targetFps = -1;
        public int internalWidth = 1280;
        public int internalHeight = 720;
        public double SimulatedSeconds { get; private set; }
        public double BacklogSeconds { get; private set; }
        public string Error { get; private set; }
        public bool IsRebuilding => initializing;
        public bool ExpectedGpu => SystemInfo.graphicsDeviceName.IndexOf("5070", StringComparison.OrdinalIgnoreCase) >= 0 &&
            SystemInfo.graphicsDeviceName.IndexOf("Ti", StringComparison.OrdinalIgnoreCase) >= 0;

        RenderPipelineAsset previousPipeline;
        int previousFps, previousVsync;
        bool previousZibraRendering;
        RenderTexture output;
        Vector3 mlsOffset, zibraOffset;
        bool ownsSettings, initializing, stepOnce;
        float averageFrame;
        Vector3 sphereTarget = new Vector3(0.95f, 0.55f, 0);
        Vector3 spherePosition;
        bool dragging;
        float[] frameSamples = new float[600];
        int sampleCount, sampleCursor;
        double wallSinceReset;
        int completedTicks;
        int initialTicks;

        void OnEnable()
        {
            if (!Application.isPlaying) return;
            previousPipeline = QualitySettings.renderPipeline;
            previousFps = Application.targetFrameRate;
            previousVsync = QualitySettings.vSyncCount;
            previousZibraRendering = zibraRenderer.settings.IsEnabled;
            ownsSettings = true;
            QualitySettings.renderPipeline = pipeline;
            QualitySettings.vSyncCount = 0;
            ApplyFrameCap();
            SetInternalResolution(internalWidth, internalHeight);
            ResetComparison();
        }

        void ApplyFrameCap() => Application.targetFrameRate = targetFps;

        public void SetInternalResolution(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (output != null)
            {
                if (comparisonCamera != null) comparisonCamera.targetTexture = null;
                output.Release(); Destroy(output); output = null;
            }
            internalWidth = width; internalHeight = height;
            output = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) {
                name = "Fixed resolution fluid comparison", antiAliasing = 1
            };
            output.Create();
            comparisonCamera.targetTexture = output;
        }

        public void SelectView(ViewMode mode) { view = mode; ResetComparison(); }
        public void StepOnce() { paused = true; stepOnce = true; }

        public void SelectPreset(int count, int grid)
        {
            particleCount = count; storageGrid = grid;
            ResetComparison();
        }

        public void ResetComparison()
        {
            if (!ownsSettings) return;
            StopAllCoroutines();
            StartCoroutine(Rebuild());
        }

        IEnumerator Rebuild()
        {
            initializing = true;
            Error = null;
            mls.gameObject.SetActive(false);
            zibra.gameObject.SetActive(false);
            // Let native render-thread teardown finish before creating another instance.
            yield return null;
            try
            {
                // Match the visible floor to the physical 4m footprint, without
                // rescaling either solver or its shared initial water volume.
                var floor = GameObject.Find("Floor (visual only)");
                if (floor != null) floor.transform.localScale = new Vector3(4, .2f, 4);
                var frame = GameObject.Find("Container Frame (visual only)");
                if (frame != null) frame.SetActive(false);
                zibraOffset = mlsOffset = Vector3.zero;
                zibraStage.position = zibraOffset;
                mlsStage.position = mlsOffset;
                zibraStage.gameObject.SetActive(true);
                mlsStage.gameObject.SetActive(true);
                comparisonCamera.transform.position = new Vector3(5.6f, 4, -6.2f);
                comparisonCamera.transform.LookAt(new Vector3(0, 1.25f, 0));
                comparisonCamera.fieldOfView = 42;
                sphereTarget = new Vector3(0.95f, 0.55f, 0);
                spherePosition = sphereTarget;
                PlaceSpheres(sphereTarget);
                int side = Mathf.CeilToInt(Mathf.Pow(particleCount, 1f / 3));
                while (side * side * side < particleCount) side++;
                float blockWidth = 4f * 16 / 27;
                float spacing = blockWidth / side;
                Vector3 blockMin = new Vector3(-2 + 4f / 27, 4f / 27, -2 + 24f / 27);
                var positions = new Vector3[particleCount];
                for (int i = 0; i < particleCount; i++)
                    positions[i] = blockMin + spacing * new Vector3(i % side + 0.5f, (i / side) % side + 0.5f, i / (side * side) + 0.5f);
                mls.externalControl = true;
                mls.showControls = false;
                mls.transform.position = mlsOffset + new Vector3(-2, 0, -2);
                mls.particleCount = particleCount; mls.gridResolution = storageGrid;
                mls.substeps = mlsSubsteps; mls.domainSize = 4;
                mls.whitewaterCapacity = Mathf.Min(particleCount, 32768);
                mls.enableWhitewater = whitewater; mls.showParticles = renderWater;
                mls.surfaceQuality = waterQuality; mls.gravity = new Vector3(0, gravity, 0);
                mls.sphereRadius = sphereRadius;
                if (view != ViewMode.Zibra)
                {
                    mls.gameObject.SetActive(true);
                    if (mls.Error != null) throw new InvalidOperationException(mls.Error);
                    var world = new Vector3[positions.Length];
                    for (int i = 0; i < world.Length; i++) world[i] = positions[i] + mlsOffset;
                    mls.SetComparisonInitialPositions(world, spacing);
                }
                if (view != ViewMode.MlsMpm)
                {
                    int physicalCells = storageGrid - 5;
                    float h = 4f / physicalCells;
                    int preset = particleCount == 16384 ? 0 : particleCount == 32768 ? 1 : particleCount == 65536 ? 2 : 3;
                    if (bakedStates == null || bakedStates.Length != 4 || bakedStates[preset] == null)
                        throw new InvalidOperationException("Comparison baked initial states are missing. Recreate the comparison scene.");
                    zibra.transform.position = zibraOffset + new Vector3(0, 2, 0);
                    zibra.containerSize = Vector3.one * 4;
                    zibra.containerPos = zibra.transform.position;
                    zibra.gridResolution = physicalCells; zibra.MaxNumParticles = particleCount;
                    zibra.InitialState = ZibraLiquid.InitialStateType.BakedLiquidState;
                    zibra.BakedInitialStateAsset = bakedStates[preset];
                    zibra.solverParameters.ParticleDensity = Mathf.Pow(h / spacing, 3);
                    zibra.solverParameters.Gravity = new Vector3(0, gravity, 0);
                    zibra.iterationsPerFrame = zibraIterations;
                    // Native positions are in cells; gravity is divided by 100 by the C# bridge.
                    // Convert native time to seconds so h * (g/100) * scale^2 = g.
                    zibra.simTimePerSec = Mathf.Sqrt(100f / h);
                    zibra.timeStepMax = 1;
                    zibra.useFixedTimestep = true; zibra.runSimulation = false;
                    zibra.EnableDownscale = waterQuality != MlsMpmFluid.SurfaceQuality.High;
                    zibra.DownscaleFactor = 0.5f;
                    zibra.materialParameters.FoamIntensity = whitewater ? 0.8f : 0;
                    zibra.gameObject.SetActive(true);
                    if (!zibra.initialized) throw new InvalidOperationException("Zibra native initialization failed.");
                }
                zibraRenderer.settings.IsEnabled = renderWater;
                SimulatedSeconds = BacklogSeconds = wallSinceReset = 0;
                completedTicks = sampleCount = sampleCursor = 0;
                // Native Zibra reconstructs its first surface only after a real step.
                // Prime BOTH selections with exactly two shared 1/60s ticks, including
                // paused resets, and expose that elapsed time rather than claiming t=0.
                averageFrame = 0; dragging = false; stepOnce = false; initialTicks = 2;
                ApplyFrameCap();
            }
            catch (Exception e) { Error = e.Message; Debug.LogException(e, this); paused = true; }
            initializing = false;
        }

        void PlaceSpheres(Vector3 local)
        {
            mlsSphere.position = local + mlsOffset; zibraSphere.position = local + zibraOffset;
            mlsSphere.localScale = zibraSphere.localScale = Vector3.one * (sphereRadius * 2);
        }

        void Update()
        {
            if (!ownsSettings || initializing) return;
            if (Input.GetKeyDown(KeyCode.R)) { ResetComparison(); return; }
            if (Error != null) return;
            if (mls.gameObject.activeInHierarchy && mls.Error != null) { Error = mls.Error; paused = true; return; }
            if (Input.GetKeyDown(KeyCode.Space)) paused = !paused;
            UpdatePointer();
            bool manualTick = stepOnce || initialTicks > 0;
            if (paused && !manualTick) return;
            double delta = manualTick ? MlsMpmFluid.FixedTick : Time.unscaledDeltaTime;
            if (!manualTick)
            {
                wallSinceReset += delta;
                frameSamples[sampleCursor] = (float)delta * 1000;
                sampleCursor = (sampleCursor + 1) % frameSamples.Length;
                sampleCount = Mathf.Min(sampleCount + 1, frameSamples.Length);
                averageFrame = averageFrame <= 0 ? (float)delta : Mathf.Lerp(averageFrame, (float)delta, 0.05f);
            }
            BacklogSeconds += delta;
            int ticks = manualTick ? 1 : Math.Min(maxCatchUpTicks, (int)(BacklogSeconds / MlsMpmFluid.FixedTick));
            if (initialTicks > 0) initialTicks--;
            stepOnce = false;
            for (int i = 0; i < ticks; i++)
            {
                Vector3 local = Vector3.MoveTowards(spherePosition, sphereTarget, 2 * (float)MlsMpmFluid.FixedTick);
                if (automaticStir)
                {
                    // Identical sphere trajectory evaluated on simulation time, never render time.
                    double t = Math.Max(0, SimulatedSeconds - 1);
                    local = new Vector3(0.95f + 0.55f * Mathf.Sin((float)t * 2), 0.55f, 0.6f * Mathf.Sin((float)t * 1.4f));
                }
                spherePosition = local;
                PlaceSpheres(local);
                if (mls.gameObject.activeInHierarchy) mls.StepSimulation(1);
                if (zibra.gameObject.activeInHierarchy && zibra.initialized)
                {
                    zibra.runSimulation = true;
                    zibra.UpdateSimulation((float)MlsMpmFluid.FixedTick);
                    zibra.runSimulation = false;
                }
                completedTicks++; SimulatedSeconds = completedTicks * MlsMpmFluid.FixedTick;
                BacklogSeconds -= MlsMpmFluid.FixedTick;
            }
        }

        Rect DisplayRect()
        {
            float panel = 245 * GuiScale();
            float areaHeight = Mathf.Max(100, Screen.height - panel);
            float width = Mathf.Min(Screen.width, areaHeight * internalWidth / internalHeight);
            float height = width * internalHeight / internalWidth;
            return new Rect((Screen.width - width) * 0.5f, panel + (areaHeight - height) * 0.5f, width, height);
        }

        void UpdatePointer()
        {
            Vector2 gui = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            Rect rect = DisplayRect();
            if (Input.GetMouseButtonDown(0) && rect.Contains(gui)) { dragging = true; automaticStir = false; }
            if (Input.GetMouseButtonUp(0)) dragging = false;
            if (!dragging) return;
            Vector3 viewport = new Vector3((gui.x - rect.x) / rect.width, 1 - (gui.y - rect.y) / rect.height, 0);
            Ray ray = comparisonCamera.ViewportPointToRay(viewport);
            if (new Plane(Vector3.up, new Vector3(0, 0.65f, 0)).Raycast(ray, out float distance))
            {
                Vector3 p = ray.GetPoint(distance);
                sphereTarget = new Vector3(Mathf.Clamp(p.x, -1.4f, 1.4f), 0.65f, Mathf.Clamp(p.z, -1.4f, 1.4f));
            }
        }

        float GuiScale() => Mathf.Clamp(Screen.width / 1200f, 0.65f, 1.5f);

        void OnGUI()
        {
            if (!ownsSettings) return;
            GUI.depth = 100;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.blackTexture);
            if (output != null) GUI.DrawTexture(DisplayRect(), output, ScaleMode.StretchToFill, false);
            GUI.depth = 0;
            Matrix4x4 old = GUI.matrix;
            float scale = GuiScale(); GUI.matrix = Matrix4x4.Scale(Vector3.one * scale);
            GUILayout.BeginArea(new Rect(10, 8, Screen.width / scale - 20, 235), GUI.skin.box);
            GUILayout.Label("DESKTOP FLUID A/B | ONE POOL | " + view);
            Color original = GUI.color; GUI.color = ExpectedGpu ? Color.green : new Color(1, 0.65f, 0.25f);
            GUILayout.Label("Actual GPU: " + SystemInfo.graphicsDeviceName + " | " + SystemInfo.graphicsDeviceType +
                (ExpectedGpu ? " | 5070 Ti verified" : " | NOT verified as RTX 5070 Ti"));
            GUI.color = original;
            GUILayout.Label(string.Format("{0:N0} particles each | {1} effective cells/axis | MPM storage {2}^3 / Zibra {1}^3 | {3}x{4}",
                particleCount, storageGrid - 5, storageGrid, internalWidth, internalHeight));
            GUILayout.Label(string.Format("4m box | same initial particle positions & volume | gravity {0:F2} | radius {1:F2} | sim {2:F2}s | backlog {3:F2}s | frame {4:F1}ms",
                gravity, sphereRadius, SimulatedSeconds, BacklogSeconds, averageFrame * 1000));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Zibra only")) SelectView(ViewMode.Zibra);
            if (GUILayout.Button("MLS-MPM only")) SelectView(ViewMode.MlsMpm);
            if (GUILayout.Button(paused ? "Run" : "Pause")) paused = !paused;
            if (GUILayout.Button("Reset [R]")) ResetComparison();
            if (GUILayout.Button("Step 1/60s")) StepOnce();
            if (GUILayout.Button(automaticStir ? "Auto stir ON" : "Auto stir OFF")) { automaticStir = !automaticStir; ResetComparison(); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("16K / 27 cells")) SelectPreset(16384, 32);
            if (GUILayout.Button("32K / 43 cells")) SelectPreset(32768, 48);
            if (GUILayout.Button("64K / 59 cells")) SelectPreset(65536, 64);
            if (GUILayout.Button("128K / 75 cells")) SelectPreset(131072, 80);
            if (GUILayout.Button("MPM steps: " + mlsSubsteps))
            {
                int[] choices = { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32 };
                int next = Array.IndexOf(choices, mlsSubsteps) + 1;
                mlsSubsteps = choices[next >= choices.Length ? 0 : next];
                ResetComparison();
            }
            if (GUILayout.Button("MPM recommended")) { mlsSubsteps = MlsMpmFluid.RecommendedSubsteps(storageGrid); ResetComparison(); }
            if (GUILayout.Button("Zibra iterations: " + zibraIterations)) { zibraIterations = zibraIterations % 4 + 1; ResetComparison(); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(whitewater ? "Foam ON*" : "Foam OFF")) { whitewater = !whitewater; ResetComparison(); }
            if (GUILayout.Button(renderWater ? "Water rendering ON" : "Water rendering OFF"))
            { renderWater = !renderWater; ResetComparison(); }
            if (GUILayout.Button("Surface: " + waterQuality)) { waterQuality = (MlsMpmFluid.SurfaceQuality)(((int)waterQuality + 1) % 3); ResetComparison(); }
            if (GUILayout.Button(internalWidth == 1280 ? "1280x720" : "1920x1080")) { SetInternalResolution(internalWidth == 1280 ? 1920 : 1280, internalWidth == 1280 ? 1080 : 720); ResetComparison(); }
            if (GUILayout.Button(targetFps == 60 ? "60 FPS cap" : "Uncapped")) { targetFps = targetFps == 60 ? -1 : 60; ApplyFrameCap(); ResetComparison(); }
            if (GUILayout.Button("Save measurement")) SaveMeasurement();
            GUILayout.EndHorizontal();
            GUILayout.Label("*Low MPM steps are experimental; 1 native iteration is not equivalent to 1 explicit MPM substep. Foam/material units differ.");
            GUILayout.Label(Error ?? (initializing ? "Rebuilding both initial states..." :
                "One solver active at a time. Switching resets the same water block. Drag the sphere. Space: pause."));
            GUILayout.EndArea(); GUI.matrix = old;
        }

        [Serializable] public sealed class Measurement
        {
            public string capturedUtc, gpu, api, mode, status;
            public int particles, effectiveGrid, mlsStorageGrid, mlsSubsteps, zibraIterations, width, height, frameCap, frames;
            public bool foam, rendering;
            public double simulatedSeconds, wallSeconds, backlogSeconds, meanFrameMs, p95FrameMs;
            public string note = "Editor whole-frame wall times, no isolated GPU timing. One solver active at a time. Numerical/material settings are solver-specific.";
        }

        public void SaveMeasurement()
        {
            if (initializing || Error != null || sampleCount == 0) { Debug.LogWarning("Run a valid comparison before saving."); return; }
            if (require5070TiForMeasurements && !ExpectedGpu)
            { Debug.LogWarning("Measurement blocked: active graphics device is " + SystemInfo.graphicsDeviceName + ". RTX 5070 Ti is required."); return; }
            var sorted = new float[sampleCount]; Array.Copy(frameSamples, sorted, sampleCount); Array.Sort(sorted);
            double sum = 0; foreach (float value in sorted) sum += value;
            var record = new Measurement {
                capturedUtc = DateTime.UtcNow.ToString("O"), gpu = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString(),
                mode = view.ToString(), status = "solo", particles = particleCount,
                effectiveGrid = storageGrid - 5, mlsStorageGrid = storageGrid, mlsSubsteps = mlsSubsteps, zibraIterations = zibraIterations,
                width = internalWidth, height = internalHeight, frameCap = targetFps, frames = sampleCount, foam = whitewater, rendering = renderWater,
                simulatedSeconds = SimulatedSeconds, wallSeconds = wallSinceReset, backlogSeconds = BacklogSeconds,
                meanFrameMs = sum / sampleCount, p95FrameMs = sorted[(int)Math.Ceiling(sampleCount * 0.95) - 1]
            };
            string folder = Application.isEditor ? Path.Combine(Directory.GetParent(Application.dataPath).FullName, "BenchmarkResults") : Application.persistentDataPath;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "comparison-" + view + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(record, true));
            Debug.Log("Comparison measurement saved: " + path);
        }

        void OnDisable()
        {
            StopAllCoroutines();
            if (mls != null) mls.gameObject.SetActive(false);
            if (zibra != null) zibra.gameObject.SetActive(false);
            if (output != null)
            {
                if (comparisonCamera != null) comparisonCamera.targetTexture = null;
                output.Release(); Destroy(output); output = null;
            }
            if (!ownsSettings) return;
            if (zibraRenderer != null) zibraRenderer.settings.IsEnabled = previousZibraRendering;
            QualitySettings.renderPipeline = previousPipeline;
            Application.targetFrameRate = previousFps; QualitySettings.vSyncCount = previousVsync; ownsSettings = false;
        }
    }
}
