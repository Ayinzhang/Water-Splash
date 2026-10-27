using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WaterSplash.Editor
{
    public static class MlsMpmDemo
    {
        public const string ScenePath = "Assets/MlsMpm/Scenes/MlsMpmDemo.unity";

        [MenuItem("Water Splash/Open MLS-MPM Demo")]
        public static void OpenDemo()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            InstallSurfaceFeature();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            {
                EditorSceneManager.OpenScene(ScenePath);
                return;
            }
            CreateDemo();
        }

        public static void CreateDemo()
        {
            if (EditorApplication.isPlaying)
                throw new System.InvalidOperationException("Create the scene outside Play Mode.");
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty)
                throw new System.InvalidOperationException("Save the current scene before creating the demo.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
                throw new System.InvalidOperationException("The demo already exists. Open it instead of overwriting it.");
            EnsureFolder("Assets/MlsMpm", "Materials");
            EnsureFolder("Assets/MlsMpm", "Scenes");
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/MlsMpm.compute");
            Shader particleShader = Shader.Find("WaterSplash/MLS-MPM Particles");
            Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (compute == null || particleShader == null || unlitShader == null)
                throw new System.InvalidOperationException("Import the MLS-MPM shaders and URP before creating the demo.");
            Material particles = GetMaterial("Particles", particleShader, new Color(0.02f, 0.3f, 0.75f));
            Material floor = GetMaterial("Floor", unlitShader, new Color(0.095f, 0.12f, 0.16f));
            Material frame = GetMaterial("Frame", unlitShader, new Color(0.25f, 0.4f, 0.48f));
            Material obstacle = GetMaterial("Obstacle", unlitShader, new Color(1, 0.4f, 0.08f));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.transform.position = new Vector3(5.6f, 4.0f, -6.2f);
            camera.transform.LookAt(new Vector3(0, 1.25f, 0));
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.04f, 0.065f);
            camera.fieldOfView = 42;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 50;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderPostProcessing = false;
            cameraData.renderShadows = false;
            cameraData.requiresColorOption = CameraOverrideOption.Off;
            cameraData.requiresDepthOption = CameraOverrideOption.Off;

            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Visual Floor (GPU collision is analytic)";
            ground.transform.position = new Vector3(0, -0.1f, 0);
            ground.transform.localScale = new Vector3(5.5f, 0.2f, 5.5f);
            ground.GetComponent<Renderer>().sharedMaterial = floor;
            Object.DestroyImmediate(ground.GetComponent<Collider>());

            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "Drag Sphere (one-way GPU collider)";
            sphere.transform.position = new Vector3(0.95f, 0.55f, 0);
            sphere.transform.localScale = Vector3.one * 0.84f;
            sphere.GetComponent<Renderer>().sharedMaterial = obstacle;
            Object.DestroyImmediate(sphere.GetComponent<Collider>());

            var fluidObject = new GameObject("MLS-MPM Fluid");
            fluidObject.transform.position = new Vector3(-2, 0, -2);
            var fluid = fluidObject.AddComponent<MlsMpmFluid>();
            fluid.simulationShader = compute;
            fluid.particleMaterial = particles;
            fluid.sphereObstacle = sphere.transform;
            fluid.interactionCamera = camera;
            fluid.renderSurface = true;

            var wire = new GameObject("Container Frame (visual only)");
            Vector3 min = fluid.transform.position;
            Vector3 max = fluid.transform.position + Vector3.one * fluid.domainSize;
            for (int axis = 0; axis < 3; axis++)
            for (int a = 0; a < 2; a++)
            for (int b = 0; b < 2; b++)
            {
                int otherA = (axis + 1) % 3;
                int otherB = (axis + 2) % 3;
                Vector3 center = (min + max) * 0.5f;
                center[otherA] = a == 0 ? min[otherA] : max[otherA];
                center[otherB] = b == 0 ? min[otherB] : max[otherB];
                Vector3 size = Vector3.one * 0.018f;
                size[axis] = max[axis] - min[axis];
                var edge = GameObject.CreatePrimitive(PrimitiveType.Cube);
                edge.name = "Edge";
                edge.transform.SetParent(wire.transform);
                edge.transform.position = center;
                edge.transform.localScale = size;
                edge.GetComponent<Renderer>().sharedMaterial = frame;
                Object.DestroyImmediate(edge.GetComponent<Collider>());
            }
            InstallSurfaceFeature();
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = fluidObject;
        }

        [MenuItem("Water Splash/Enable Continuous Water")]
        public static void EnableContinuousWater()
        {
            if (EditorApplication.isPlaying)
                throw new System.InvalidOperationException("Install the renderer feature outside Play Mode.");
            InstallSurfaceFeature();
            foreach (MlsMpmFluid fluid in Object.FindObjectsOfType<MlsMpmFluid>())
            {
                Undo.RecordObject(fluid, "Enable MLS-MPM continuous water");
                fluid.renderSurface = true;
                EditorUtility.SetDirty(fluid);
                EditorSceneManager.MarkSceneDirty(fluid.gameObject.scene);
            }
        }

        public static void InstallSurfaceFeature()
        {
            Shader splats = Shader.Find("Hidden/WaterSplash/Surface Particles");
            Shader surface = Shader.Find("Hidden/WaterSplash/Continuous Surface");
            if (splats == null || surface == null)
                throw new System.InvalidOperationException("Import both continuous-water shaders before installing the feature.");
            var pipelines = new HashSet<RenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline != null) pipelines.Add(GraphicsSettings.defaultRenderPipeline);
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                RenderPipelineAsset pipeline = QualitySettings.GetRenderPipelineAssetAt(i);
                if (pipeline != null) pipelines.Add(pipeline);
            }
            var installed = new HashSet<ScriptableRendererData>();
            foreach (RenderPipelineAsset pipeline in pipelines)
            {
                if (!(pipeline is UniversalRenderPipelineAsset)) continue;
                var serialized = new SerializedObject(pipeline);
                SerializedProperty renderers = serialized.FindProperty("m_RendererDataList");
                for (int i = 0; i < renderers.arraySize; i++)
                {
                    var data = renderers.GetArrayElementAtIndex(i).objectReferenceValue as UniversalRendererData;
                    if (data == null || !installed.Add(data)) continue;
                    MlsMpmSurfaceFeature feature = null;
                    foreach (ScriptableRendererFeature candidate in data.rendererFeatures)
                        if (candidate is MlsMpmSurfaceFeature existing) { feature = existing; break; }
                    if (feature == null)
                    {
                        feature = ScriptableObject.CreateInstance<MlsMpmSurfaceFeature>();
                        feature.name = "MLS-MPM Continuous Water";
                        AssetDatabase.AddObjectToAsset(feature, data);
                        data.rendererFeatures.Add(feature);
                    }
                    feature.particleShader = splats;
                    feature.surfaceShader = surface;
                    feature.SetActive(true);
                    feature.Create();
                    EditorUtility.SetDirty(feature);
                    EditorUtility.SetDirty(data);
                    var rendererObject = new SerializedObject(data);
                    SerializedProperty map = rendererObject.FindProperty("m_RendererFeatureMap");
                    map.arraySize = data.rendererFeatures.Count;
                    for (int j = 0; j < data.rendererFeatures.Count; j++)
                    {
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(data.rendererFeatures[j], out string guid, out long id);
                        map.GetArrayElementAtIndex(j).longValue = id;
                    }
                    rendererObject.ApplyModifiedPropertiesWithoutUndo();
                    data.SetDirty();
                }
            }
            if (installed.Count == 0)
                throw new System.InvalidOperationException("No Universal Renderer was found in the project's active quality pipelines.");
            AssetDatabase.SaveAssets();
        }

        static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + name)) AssetDatabase.CreateFolder(parent, name);
        }

        static Material GetMaterial(string name, Shader shader, Color color)
        {
            string path = "Assets/MlsMpm/Materials/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            material = new Material(shader);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
