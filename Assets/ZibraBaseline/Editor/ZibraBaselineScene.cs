using System;
using com.zibra.liquid;
using com.zibra.liquid.DataStructures;
using com.zibra.liquid.Manipulators;
using com.zibra.liquid.SDFObjects;
using com.zibra.liquid.Solver;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    public static class ZibraBaselineScene
    {
        public const string ScenePath = "Assets/ZibraBaseline/Scenes/ZibraBaseline.unity";

        [MenuItem("Water Splash/Open Zibra Baseline")]
        public static void Open()
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Open the baseline outside Play Mode.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null) Create();
            EditorSceneManager.OpenScene(ScenePath);
            var controls = Object.FindObjectOfType<ZibraBaselineDemo>();
            if (controls != null && controls.renderFeature == null) UpgradeCurrentScene();
        }

        [MenuItem("Water Splash/Migrate Baseline to Zibra 1.4.2")]
        public static void UpgradeCurrentScene()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (EditorApplication.isPlaying || scene.path != ScenePath || scene.isDirty)
                throw new InvalidOperationException("Open the saved baseline outside Play Mode before migration.");
            var controls = Object.FindObjectOfType<ZibraBaselineDemo>();
            if (controls == null) throw new InvalidOperationException("Baseline controls are missing.");
            var liquid = controls.liquid;
            liquid.gameObject.SetActive(false);
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(liquid.gameObject);
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(controls.sphere.gameObject);
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(controls.emitter.gameObject);
            var collider = controls.sphere.GetComponent<AnalyticSDFCollider>();
            if (collider == null) collider = controls.sphere.gameObject.AddComponent<AnalyticSDFCollider>();
            collider.chosenSDFType = SDFCollider.SDFType.Sphere;
            collider.ManipType = Manipulator.ManipulatorType.AnalyticCollider;
            var settings = new SerializedObject(liquid);
            settings.FindProperty("sdfColliders").arraySize = 0;
            settings.FindProperty("manipulators").arraySize = 0;
            settings.ApplyModifiedPropertiesWithoutUndo();
            liquid.materialParameters = liquid.GetComponent<ZibraLiquidMaterialParameters>();
            liquid.solverParameters = liquid.GetComponent<ZibraLiquidSolverParameters>();
            liquid.manipulatorManager = liquid.GetComponent<ZibraManipulatorManager>();
            liquid.containerSize = Vector3.one * 4;
            liquid.containerPos = liquid.transform.position;
            liquid.gridResolution = 48;
            liquid.MaxNumParticles = 32768;
            liquid.iterationsPerFrame = 1;
            liquid.useFixedTimestep = false;
            liquid.EnableDownscale = false;
            liquid.reflectionProbeSRP = Object.FindObjectOfType<ReflectionProbe>();
            var water = liquid.materialParameters;
            water.FluidMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                AssetDatabase.GUIDToAssetPath("212140388bc74764ba19ea2bbe4ce94c"));
            water.UpscaleMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                AssetDatabase.GUIDToAssetPath("374557399a8cb1b499aee6a0cc226496"));
            water.Color = new Color(0.08f, 0.45f, 0.6f);
            water.Metalness = 0;
            water.Opacity = 3;
            controls.ApplyFoam();
            controls.emitter.ParticlesPerSec = liquid.MaxNumParticles * 60f;
            liquid.AddManipulator(controls.emitter);
            liquid.AddCollider(collider);
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(
                "Assets/ZibraBaseline/Settings/ZibraRenderer.asset");
            controls.renderFeature = renderer.rendererFeatures.Find(
                item => item is LiquidURPRenderComponent) as LiquidURPRenderComponent;
            if (controls.renderFeature == null)
                throw new InvalidOperationException("The baseline Zibra renderer feature is missing.");
            CreateBounds(liquid);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
        }

        public static void Create()
        {
            if (EditorApplication.isPlaying || UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty)
                throw new InvalidOperationException("Stop Play Mode and save the current scene first.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
                throw new InvalidOperationException("The baseline already exists; open it instead of overwriting it.");
            EnsureFolder("Assets/ZibraBaseline", "Scenes");
            EnsureFolder("Assets/ZibraBaseline", "Settings");
            EnsureFolder("Assets/ZibraBaseline", "Materials");
            var source = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (source == null) throw new InvalidOperationException("This baseline requires URP.");
            var sourceSettings = new SerializedObject(source);
            int defaultIndex = sourceSettings.FindProperty("m_DefaultRendererIndex").intValue;
            var sourceRenderer = sourceSettings.FindProperty("m_RendererDataList")
                .GetArrayElementAtIndex(defaultIndex).objectReferenceValue as UniversalRendererData;
            if (sourceRenderer == null) throw new InvalidOperationException("The active pipeline needs a Universal Renderer.");
            var renderer = Object.Instantiate(sourceRenderer);
            renderer.name = "Zibra Baseline Renderer";
            renderer.rendererFeatures.Clear();
            AssetDatabase.CreateAsset(renderer, "Assets/ZibraBaseline/Settings/ZibraRenderer.asset");
            var feature = ScriptableObject.CreateInstance<LiquidURPRenderComponent>();
            feature.name = "Zibra Native Liquid";
            AssetDatabase.AddObjectToAsset(feature, renderer);
            renderer.rendererFeatures.Add(feature);
            var rendererSettings = new SerializedObject(renderer);
            var featureMap = rendererSettings.FindProperty("m_RendererFeatureMap");
            featureMap.arraySize = 1;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string guid, out long id);
            featureMap.GetArrayElementAtIndex(0).longValue = id;
            rendererSettings.ApplyModifiedPropertiesWithoutUndo();
            renderer.SetDirty();
            var pipeline = Object.Instantiate(source);
            pipeline.name = "Zibra Baseline URP";
            AssetDatabase.CreateAsset(pipeline, "Assets/ZibraBaseline/Settings/ZibraPipeline.asset");
            var pipelineSettings = new SerializedObject(pipeline);
            var renderers = pipelineSettings.FindProperty("m_RendererDataList");
            renderers.arraySize = 1;
            renderers.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            pipelineSettings.FindProperty("m_DefaultRendererIndex").intValue = 0;
            pipelineSettings.ApplyModifiedPropertiesWithoutUndo();
            pipeline.msaaSampleCount = 1;
            pipeline.renderScale = 1;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener),
                typeof(UniversalAdditionalCameraData));
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.GetComponent<Camera>();
            camera.transform.position = new Vector3(5.6f, 4, -6.2f);
            camera.transform.LookAt(new Vector3(0, 1.25f, 0));
            camera.fieldOfView = 42;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 50;
            camera.allowMSAA = false;
            camera.allowHDR = false;
            cameraObject.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing = false;
            var sun = new GameObject("Sun", typeof(Light)).GetComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            sun.transform.rotation = Quaternion.Euler(45, -35, 0);
            sun.shadows = LightShadows.None;
            var sky = new Material(Shader.Find("Skybox/Procedural"));
            sky.SetFloat("_SunDisk", 1);
            sky.SetFloat("_Exposure", 1);
            AssetDatabase.CreateAsset(sky, "Assets/ZibraBaseline/Materials/Sky.mat");
            RenderSettings.skybox = sky;
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1;

            Material floorMaterial = MakeMaterial("Floor", new Color(0.35f, 0.37f, 0.4f));
            var checker = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            checker.name = "Floor Checker";
            checker.filterMode = FilterMode.Point;
            checker.wrapMode = TextureWrapMode.Repeat;
            var pixels = new Color32[64 * 64];
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                pixels[y * 64 + x] = ((x / 32 + y / 32) & 1) == 0 ?
                    new Color32(215, 215, 215, 255) : new Color32(125, 125, 125, 255);
            checker.SetPixels32(pixels);
            checker.Apply();
            AssetDatabase.CreateAsset(checker, "Assets/ZibraBaseline/Materials/FloorChecker.asset");
            floorMaterial.SetTexture("_BaseMap", checker);
            floorMaterial.SetTextureScale("_BaseMap", Vector2.one * 5);
            Primitive("Floor (visual only)", PrimitiveType.Cube, new Vector3(0, -0.1f, 0),
                new Vector3(5.5f, 0.2f, 5.5f), floorMaterial);
            var sphere = Primitive("Drag Sphere", PrimitiveType.Sphere, new Vector3(0.8f, 0.65f, 0),
                Vector3.one * 0.84f, MakeMaterial("Sphere", new Color(1, 0.32f, 0.04f)));
            var collider = sphere.AddComponent<AnalyticSDFCollider>();
            collider.chosenSDFType = SDFCollider.SDFType.Sphere;

            var fluidObject = new GameObject("Zibra Liquid");
            var liquid = fluidObject.AddComponent<ZibraLiquid>();
            liquid.transform.position = new Vector3(0, 2, 0);
            liquid.containerSize = Vector3.one * 4;
            liquid.gridResolution = 48;
            liquid.MaxNumParticles = 32768;
            liquid.iterationsPerFrame = 1;
            liquid.useFixedTimestep = false;
            liquid.EnableDownscale = false;
            var water = fluidObject.GetComponent<ZibraLiquidMaterialParameters>();
            water.Color = new Color(0.08f, 0.45f, 0.6f);
            water.Metalness = 0;
            water.Opacity = 3;
            water.FoamIntensity = 0.8f;
            water.FoamAmount = 1;
            liquid.materialParameters = water;
            liquid.solverParameters = fluidObject.GetComponent<ZibraLiquidSolverParameters>();
            liquid.manipulatorManager = fluidObject.GetComponent<ZibraManipulatorManager>();
            var emitterObject = new GameObject("Initial Water Block");
            emitterObject.transform.position = new Vector3(-0.65f, 2.4f, 0);
            emitterObject.transform.localScale = Vector3.one * 2.3f;
            var emitter = emitterObject.AddComponent<ZibraLiquidEmitter>();
            emitter.ParticlesPerSec = liquid.MaxNumParticles * 60f;
            liquid.AddManipulator(emitter);
            liquid.AddCollider(collider);
            var probe = new GameObject("Water Reflection", typeof(ReflectionProbe)).GetComponent<ReflectionProbe>();
            probe.transform.position = new Vector3(0, 2, 0);
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.OnAwake;
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
            probe.resolution = 128;
            probe.size = Vector3.one * 20;
            probe.farClipPlane = 40;
            liquid.reflectionProbeSRP = probe;
            fluidObject.SetActive(false);

            var controls = new GameObject("Zibra Baseline Controls").AddComponent<ZibraBaselineDemo>();
            controls.liquid = liquid;
            controls.emitter = emitter;
            controls.sphere = sphere.transform;
            controls.viewCamera = camera;
            controls.pipeline = pipeline;
            controls.renderFeature = feature;
            CreateBounds(liquid);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
        }

        static void CreateBounds(ZibraLiquid liquid)
        {
            if (GameObject.Find("Container Frame (visual only)") != null) return;
            const string materialPath = "Assets/ZibraBaseline/Materials/Container.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                material.SetColor("_BaseColor", new Color(0.8f, 0.65f, 0.15f));
                AssetDatabase.CreateAsset(material, materialPath);
            }
            var frame = new GameObject("Container Frame (visual only)");
            frame.transform.position = liquid.transform.position;
            for (int axis = 0; axis < 3; axis++)
            for (int a = -1; a <= 1; a += 2)
            for (int b = -1; b <= 1; b += 2)
            {
                var position = liquid.transform.position;
                position[(axis + 1) % 3] += a * liquid.containerSize[(axis + 1) % 3] * 0.5f;
                position[(axis + 2) % 3] += b * liquid.containerSize[(axis + 2) % 3] * 0.5f;
                var scale = Vector3.one * 0.018f;
                scale[axis] = liquid.containerSize[axis];
                Primitive("Edge", PrimitiveType.Cube, position, scale, material).transform.SetParent(frame.transform);
            }
        }

        static GameObject Primitive(string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
        {
            var obj = GameObject.CreatePrimitive(type);
            obj.name = name;
            obj.transform.position = position;
            obj.transform.localScale = scale;
            obj.GetComponent<Renderer>().sharedMaterial = material;
            Object.DestroyImmediate(obj.GetComponent<Collider>());
            return obj;
        }

        static Material MakeMaterial(string name, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit shader is missing.");
            var material = new Material(shader);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.25f);
            AssetDatabase.CreateAsset(material, "Assets/ZibraBaseline/Materials/" + name + ".mat");
            return material;
        }

        static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + name)) AssetDatabase.CreateFolder(parent, name);
        }
    }
}
