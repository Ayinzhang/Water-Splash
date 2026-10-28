using System;
using System.IO;
using com.zibra.liquid;
using com.zibra.liquid.Manipulators;
using com.zibra.liquid.Solver;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    public static class FluidComparisonScene
    {
        public const string ScenePath = "Assets/FluidComparison/Scenes/FluidComparison.unity";

        static FluidComparisonController Running()
        {
            var controller = Object.FindObjectOfType<FluidComparisonController>();
            if (!EditorApplication.isPlaying || controller == null) throw new InvalidOperationException("Run the comparison scene first.");
            return controller;
        }

        [MenuItem("Water Splash/Comparison/Select Zibra")]
        public static void SelectZibra() => Running().SelectView(FluidComparisonController.ViewMode.Zibra);
        [MenuItem("Water Splash/Comparison/Select MLS-MPM")]
        public static void SelectMls() => Running().SelectView(FluidComparisonController.ViewMode.MlsMpm);
        [MenuItem("Water Splash/Comparison/Reset Paused")]
        public static void ResetPaused() { var c = Running(); c.paused = true; c.ResetComparison(); }
        [MenuItem("Water Splash/Comparison/Resume")]
        public static void Resume() => Running().paused = false;
        [MenuItem("Water Splash/Comparison/Pause")]
        public static void Pause() => Running().paused = true;
        [MenuItem("Water Splash/Comparison/Step Once")]
        public static void Step() => Running().StepOnce();
        [MenuItem("Water Splash/Comparison/Log Diagnostic State")]
        public static void Diagnostic()
        {
            var c = Running();
            if (c.view == FluidComparisonController.ViewMode.MlsMpm)
            {
                var state = c.mls.CaptureSnapshot();
                Debug.Log("COMPARISON MPM: " + JsonUtility.ToJson(state));
                return;
            }
            var positions = new Vector4[c.particleCount]; c.zibra.PositionMass.GetData(positions);
            Vector3 min = Vector3.one * float.PositiveInfinity, max = Vector3.one * float.NegativeInfinity, mean = Vector3.zero;
            int invalid = 0;
            foreach (Vector4 p in positions)
            {
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z)) invalid++;
                Vector3 v = p; min = Vector3.Min(min, v); max = Vector3.Max(max, v); mean += v;
            }
            mean /= positions.Length;
            Debug.Log("COMPARISON ZIBRA: " + JsonUtility.ToJson(new NativeDiagnostic {
                min = min, max = max, mean = mean, invalid = invalid, grid = c.zibra.GridSize,
                internalTime = c.zibra.simulationInternalTime, worldSeconds = c.SimulatedSeconds,
                timeScale = c.zibra.simTimePerSec, particleDensity = c.zibra.solverParameters.ParticleDensity
            }));
        }

        [Serializable] sealed class NativeDiagnostic
        {
            public Vector3 min, max, mean; public Vector3Int grid; public int invalid;
            public float internalTime, timeScale, particleDensity; public double worldSeconds;
        }

        [MenuItem("Water Splash/Open Single Pool Comparison")]
        public static void Open()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode first.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null) { EditorSceneManager.OpenScene(ScenePath); return; }
            Create();
        }

        public static void Create()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null) throw new InvalidOperationException("Comparison already exists.");
            Folder("Assets/FluidComparison", "Scenes"); Folder("Assets/FluidComparison", "Settings"); Folder("Assets/FluidComparison", "InitialStates");
            var scene = EditorSceneManager.OpenScene("Assets/ZibraBaseline/Scenes/ZibraBaseline.unity");
            // Work on a new scene file; the baseline assets and scene remain independent.
            EditorSceneManager.SaveScene(scene, ScenePath);
            ZibraLiquid liquid = Object.FindObjectOfType<ZibraLiquid>(true);
            var sphere = Object.FindObjectOfType<com.zibra.liquid.SDFObjects.AnalyticSDFCollider>(true).transform;
            Camera camera = Object.FindObjectOfType<Camera>();
            var serialized = new SerializedObject(liquid);
            serialized.FindProperty("manipulators").arraySize = 0; serialized.ApplyModifiedPropertiesWithoutUndo();
            foreach (ZibraLiquidEmitter emitter in Object.FindObjectsOfType<ZibraLiquidEmitter>(true)) Object.DestroyImmediate(emitter.gameObject);
            GameObject oldControls = GameObject.Find("Zibra Baseline Controls");
            if (oldControls != null) Object.DestroyImmediate(oldControls);
            liquid.gameObject.SetActive(false); liquid.runSimulation = false; liquid.useFixedTimestep = true;
            liquid.name = "Zibra (only active when selected)";

            var sourceRenderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/ZibraBaseline/Settings/ZibraRenderer.asset");
            var renderer = Object.Instantiate(sourceRenderer); renderer.name = "Single Pool Comparison Renderer"; renderer.rendererFeatures.Clear();
            AssetDatabase.CreateAsset(renderer, "Assets/FluidComparison/Settings/ComparisonRenderer.asset");
            var zFeature = ScriptableObject.CreateInstance<LiquidURPRenderComponent>(); zFeature.name = "Zibra liquid";
            AssetDatabase.AddObjectToAsset(zFeature, renderer); renderer.rendererFeatures.Add(zFeature);
            var mFeature = ScriptableObject.CreateInstance<MlsMpmSurfaceFeature>(); mFeature.name = "MLS-MPM liquid";
            mFeature.particleShader = Shader.Find("Hidden/WaterSplash/Surface Particles");
            mFeature.surfaceShader = Shader.Find("Hidden/WaterSplash/Continuous Surface");
            AssetDatabase.AddObjectToAsset(mFeature, renderer); renderer.rendererFeatures.Add(mFeature);
            var rendererObject = new SerializedObject(renderer); var map = rendererObject.FindProperty("m_RendererFeatureMap"); map.arraySize = 2;
            for (int i = 0; i < 2; i++) { AssetDatabase.TryGetGUIDAndLocalFileIdentifier(renderer.rendererFeatures[i], out string guid, out long id); map.GetArrayElementAtIndex(i).longValue = id; }
            rendererObject.ApplyModifiedPropertiesWithoutUndo(); renderer.SetDirty();
            var sourcePipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/ZibraBaseline/Settings/ZibraPipeline.asset");
            var pipeline = Object.Instantiate(sourcePipeline); pipeline.name = "Single Pool Comparison URP";
            AssetDatabase.CreateAsset(pipeline, "Assets/FluidComparison/Settings/ComparisonPipeline.asset");
            var pipelineObject = new SerializedObject(pipeline); var list = pipelineObject.FindProperty("m_RendererDataList"); list.arraySize = 1;
            list.GetArrayElementAtIndex(0).objectReferenceValue = renderer; pipelineObject.FindProperty("m_DefaultRendererIndex").intValue = 0; pipelineObject.ApplyModifiedPropertiesWithoutUndo();
            pipeline.msaaSampleCount = 1; pipeline.renderScale = 1;
            camera.GetComponent<UniversalAdditionalCameraData>().SetRenderer(0);

            var fluidObject = new GameObject("MLS-MPM (only active when selected)"); fluidObject.SetActive(false);
            fluidObject.transform.position = new Vector3(-2, 0, -2);
            var mls = fluidObject.AddComponent<MlsMpmFluid>();
            mls.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/MlsMpm.compute");
            mls.particleMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/MlsMpm/Materials/Particles.mat");
            mls.sphereObstacle = sphere; mls.interactionCamera = camera; mls.showControls = false; mls.externalControl = true;
            var controls = new GameObject("Single Pool Comparison").AddComponent<FluidComparisonController>();
            controls.mls = mls; controls.zibra = liquid; controls.mlsSphere = controls.zibraSphere = sphere;
            controls.comparisonCamera = camera; controls.pipeline = pipeline; controls.zibraRenderer = zFeature; controls.mlsRenderer = mFeature;
            controls.mlsStage = controls.zibraStage = new GameObject("Shared pool origin").transform;
            controls.bakedStates = new TextAsset[4];
            int[] counts = { 16384, 32768, 65536, 131072 }; int[] grids = { 32, 48, 64, 80 };
            for (int preset = 0; preset < counts.Length; preset++)
            {
                int count = counts[preset]; int side = Mathf.CeilToInt(Mathf.Pow(count, 1f / 3)); while (side * side * side < count) side++;
                float width = 4f * 16 / 27; float spacing = width / side; float h = 4f / (grids[preset] - 5);
                Vector3 blockMin = new Vector3(4f / 27, 4f / 27, 24f / 27);
                string path = "Assets/FluidComparison/InitialStates/Shared-" + count + ".bytes";
                using (var stream = File.Create(path)) using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(ZibraLiquid.BAKED_LIQUID_HEADER_VALUE); writer.Write(count);
                    for (int i = 0; i < count; i++)
                    {
                        Vector3 p = (blockMin + spacing * new Vector3(i % side + 0.5f, (i / side) % side + 0.5f, i / (side * side) + 0.5f)) / h;
                        writer.Write(p.x); writer.Write(p.y); writer.Write(p.z);
                    }
                    for (int i = 0; i < count; i++) { writer.Write(0); writer.Write(0); }
                }
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                controls.bakedStates[preset] = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            }
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
            Selection.activeGameObject = controls.gameObject;
            Debug.Log("Single pool comparison created: " + ScenePath);
        }

        static void Folder(string parent, string name) { if (!AssetDatabase.IsValidFolder(parent + "/" + name)) AssetDatabase.CreateFolder(parent, name); }
    }
}
