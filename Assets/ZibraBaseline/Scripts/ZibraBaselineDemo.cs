using System;
using com.zibra.liquid;
using com.zibra.liquid.Manipulators;
using com.zibra.liquid.Solver;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace WaterSplash
{
    public sealed class ZibraBaselineDemo : MonoBehaviour
    {
        public ZibraLiquid liquid;
        public ZibraLiquidEmitter emitter;
        public Transform sphere;
        public Camera viewCamera;
        public UniversalRenderPipelineAsset pipeline;
        public LiquidURPRenderComponent renderFeature;
        [Range(0, 2)] public float foamIntensity = 0.8f;
        [Range(0.1f, 4)] public float foamAmount = 1;
        public bool foamEnabled = true;

        UnityEngine.Rendering.RenderPipelineAsset previousPipeline;
        int previousFrameRate;
        int previousVSync;
        bool ownsSettings;
        bool previousRendering;
        bool dragging;
        bool paused;
        bool rendering = true;
        float frameTime;
        float stirUntil;
        Vector3 sphereStart;
        Vector3 dragTarget;
        GUIStyle label;

        void OnEnable()
        {
            if (!Application.isPlaying) return;
            if (liquid == null || emitter == null || sphere == null || viewCamera == null ||
                pipeline == null || renderFeature == null)
                throw new InvalidOperationException("Assign all Zibra baseline scene references.");
            previousPipeline = QualitySettings.renderPipeline;
            previousFrameRate = Application.targetFrameRate;
            previousVSync = QualitySettings.vSyncCount;
            ownsSettings = true;
            previousRendering = renderFeature.settings.IsEnabled;
            QualitySettings.renderPipeline = pipeline;
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            sphereStart = sphere.position;
            ResetLiquid();
        }

        void OnDisable()
        {
            if (liquid != null && liquid.gameObject.activeSelf) liquid.gameObject.SetActive(false);
            if (!ownsSettings) return;
            QualitySettings.renderPipeline = previousPipeline;
            Application.targetFrameRate = previousFrameRate;
            QualitySettings.vSyncCount = previousVSync;
            renderFeature.settings.IsEnabled = previousRendering;
            ownsSettings = false;
        }

        public void ResetLiquid()
        {
            liquid.gameObject.SetActive(false);
            sphere.position = sphereStart;
            dragging = false;
            stirUntil = 0;
            paused = false;
            emitter.ParticlesPerSec = liquid.MaxNumParticles * 60f;
            liquid.runSimulation = true;
            renderFeature.settings.IsEnabled = rendering;
            ApplyFoam();
            liquid.gameObject.SetActive(true);
            if (!liquid.initialized)
                throw new InvalidOperationException("Zibra initialization failed. See the Console for the plugin error.");
        }

        public void SelectPreset(int count, int grid)
        {
            liquid.gameObject.SetActive(false);
            liquid.MaxNumParticles = count;
            liquid.gridResolution = grid;
            ResetLiquid();
        }

        public void ApplyFoam()
        {
            liquid.materialParameters.FoamIntensity = foamEnabled ? foamIntensity : 0;
            liquid.materialParameters.FoamAmount = foamAmount;
        }

        void Update()
        {
            frameTime = frameTime <= 0 ? Time.unscaledDeltaTime :
                Mathf.Lerp(frameTime, Time.unscaledDeltaTime, 0.05f);
            if (liquid == null || !liquid.initialized) return;
            if (liquid.activeParticleNumber >= liquid.MaxNumParticles)
                emitter.ParticlesPerSec = 0;
            if (Input.GetKeyDown(KeyCode.R)) ResetLiquid();
            if (Input.GetKeyDown(KeyCode.Space)) SetPaused(!paused);
            if (paused) return;

            bool onPanel = Input.mousePosition.x < 558 * GuiScale() &&
                Input.mousePosition.y > Screen.height - 350 * GuiScale();
            if (Input.GetMouseButtonDown(0) && !onPanel)
            {
                dragging = true;
                dragTarget = sphere.position;
                stirUntil = 0;
            }
            if (Input.GetMouseButtonUp(0)) dragging = false;
            if (dragging)
            {
                var plane = new Plane(Vector3.up, sphereStart);
                Ray ray = viewCamera.ScreenPointToRay(Input.mousePosition);
                if (plane.Raycast(ray, out float distance))
                {
                    dragTarget = ray.GetPoint(distance);
                    Vector3 limit = liquid.containerSize * 0.5f - Vector3.one * 0.5f;
                    dragTarget.x = Mathf.Clamp(dragTarget.x, liquid.transform.position.x - limit.x,
                        liquid.transform.position.x + limit.x);
                    dragTarget.z = Mathf.Clamp(dragTarget.z, liquid.transform.position.z - limit.z,
                        liquid.transform.position.z + limit.z);
                    sphere.position = Vector3.MoveTowards(sphere.position, dragTarget, 2 * Time.unscaledDeltaTime);
                }
            }
            else if (Time.unscaledTime < stirUntil)
            {
                float phase = (stirUntil - Time.unscaledTime) * Mathf.PI * 2;
                sphere.position = sphereStart + new Vector3(Mathf.Sin(phase), 0, (1 - Mathf.Cos(phase)) * 0.5f) * 0.7f;
            }
        }

        void SetPaused(bool value)
        {
            paused = value;
            liquid.runSimulation = !value;
            dragging = false;
            stirUntil = 0;
        }

        float GuiScale() => Mathf.Clamp(Screen.width / 1100f, 0.7f, 1.5f);

        void OnGUI()
        {
            if (!ownsSettings) return;
            if (label == null) label = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            Matrix4x4 previous = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * GuiScale());
            try
            {
                GUI.Box(new Rect(8, 8, 540, 332), GUIContent.none);
                GUILayout.BeginArea(new Rect(18, 14, 520, 320));
                GUILayout.Label("Zibra 1.4.2 paid (native " + ZibraLiquidBridge.version + ") | GPU + screen-space", label);
                GUILayout.Label("GPU: " + SystemInfo.graphicsDeviceName + " | " + SystemInfo.graphicsDeviceType, label);
                GUILayout.Label(string.Format("{0:F1} FPS / {1:F1} ms frame (not GPU solver timing)",
                    frameTime > 0 ? 1f / frameTime : 0, frameTime * 1000), label);
                GUILayout.Label(string.Format("{0:N0} / {1:N0} particles | {2} grid | {3} iteration/update",
                    liquid.activeParticleNumber, liquid.MaxNumParticles, liquid.GridSize,
                    liquid.iterationsPerFrame), label);
                GUILayout.Label("Container: " + liquid.containerSize + " m | yellow frame = simulation bounds", label);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(paused ? "Run" : "Pause")) SetPaused(!paused);
                if (GUILayout.Button("Reset [R]")) ResetLiquid();
                if (GUILayout.Button("Stir")) { SetPaused(false); stirUntil = Time.unscaledTime + 1; }
                if (GUILayout.Button(rendering ? "Rendering ON" : "Rendering OFF"))
                {
                    rendering = !rendering;
                    renderFeature.settings.IsEnabled = rendering;
                }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("16K / 40^3")) SelectPreset(16384, 40);
                if (GUILayout.Button("32K / 48^3")) SelectPreset(32768, 48);
                if (GUILayout.Button("64K / 64^3")) SelectPreset(65536, 64);
                if (GUILayout.Button("128K / 80^3")) SelectPreset(131072, 80);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                bool enabled = GUILayout.Toggle(foamEnabled, "Density foam", GUILayout.Width(125));
                if (enabled != foamEnabled) { foamEnabled = enabled; ApplyFoam(); }
                GUILayout.Label("Intensity " + foamIntensity.ToString("F2"), GUILayout.Width(100));
                float intensity = GUILayout.HorizontalSlider(foamIntensity, 0, 2);
                if (intensity != foamIntensity) { foamIntensity = intensity; ApplyFoam(); }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Foam threshold " + foamAmount.ToString("F2"), GUILayout.Width(225));
                float amount = GUILayout.HorizontalSlider(foamAmount, 0.1f, 4);
                if (amount != foamAmount) { foamAmount = amount; ApplyFoam(); }
                GUILayout.EndHorizontal();
                GUILayout.Label("1.4.2 foam is density shading, not persistent foam/spray particles.", label);
                GUILayout.Label("Drag below this panel to move the sphere. Space: pause.", label);
                if (!liquid.initialized) GUILayout.Label("INITIALIZATION FAILED - check the Console.", label);
                GUILayout.EndArea();
            }
            finally
            {
                GUI.matrix = previous;
            }
        }
    }
}
