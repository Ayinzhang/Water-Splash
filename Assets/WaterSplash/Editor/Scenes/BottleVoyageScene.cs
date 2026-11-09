using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;
using static WaterSplash.Editor.SceneAssets;

namespace WaterSplash.Editor
{
    public static class BottleVoyageScene
    {
        public const string Root = "Assets/WaterSplash";
        public const string Art = Root + "/Art/BottleVoyage";
        public const string Settings = Root + "/Settings/BottleVoyage";
        public const string ScenePath = Root + "/Scenes/BottleVoyage.unity";

        [MenuItem("Water Splash/Open Bottle Voyage")]
        public static void Open()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play Mode before opening the bottle scene.");
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("Save the current scenes before opening Bottle Voyage.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
                EditorSceneManager.OpenScene(ScenePath);
            else Create();
        }

        public static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty)
                throw new InvalidOperationException("Create Bottle Voyage in Edit Mode with the current scene saved.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
                throw new InvalidOperationException("Bottle Voyage already exists; open it instead.");
            EnsureFolder(Art);
            EnsureFolder(Settings);
            EnsureFolder(Root + "/Scenes");
            Shader lit = RequiredShader("Universal Render Pipeline/Lit");
            Shader unlit = RequiredShader("Universal Render Pipeline/Unlit");
            Material wood = Material("Oiled walnut", lit, new Color(.26f, .12f, .065f), .22f);
            Material deck = Material("Honey teak", lit, new Color(.63f, .36f, .16f), .3f);
            Material ivory = Material("Linen sail", lit, new Color(.96f, .87f, .67f), .15f);
            ivory.SetFloat("_Cull", 0);
            Material trim = Material("Weathered brass", lit, new Color(.55f, .32f, .12f), .6f);
            trim.SetFloat("_Metallic", .5f);
            Material coral = Material("Ochre pennant", lit, new Color(.72f, .26f, .12f));
            Material cork = Material("Natural cork", lit, new Color(.46f, .29f, .15f));
            Material stone = Material("Warm porcelain", lit, new Color(.71f, .62f, .52f), .25f);
            Material rope = Material("Cotton rigging", lit, new Color(.67f, .53f, .35f));
            Material glass = Material("Sea glass", RequiredShader("WaterSplash/Bottle Glass"), Color.white);
            Material sunset = Material("Evening sky", RequiredShader("WaterSplash/Bottle Sunset"), Color.white);
            Material shadow = Material("Soft contact shadow", unlit, new Color(.48f, .36f, .29f));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(.76f, .8f, .88f);
            RenderSettings.ambientEquatorColor = new Color(.62f, .53f, .45f);
            RenderSettings.ambientGroundColor = new Color(.32f, .29f, .27f);
            RenderSettings.fog = false;
            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.transform.position = new Vector3(7.7f, 5.2f, -9.3f);
            camera.transform.LookAt(new Vector3(0, 1.15f, 0));
            camera.orthographic = true;
            camera.orthographicSize = 3.55f;
            camera.nearClipPlane = .1f;
            camera.farClipPlane = 60;
            camera.allowHDR = camera.allowMSAA = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.8f, .7f, .62f);
            var cameraData = camera.GetUniversalAdditionalCameraData();
            cameraData.SetRenderer(0);
            cameraData.renderShadows = true;
            cameraData.renderPostProcessing = false;
            cameraData.requiresColorOption = CameraOverrideOption.Off;
            cameraData.requiresDepthOption = CameraOverrideOption.Off;
            var light = new GameObject("Late afternoon sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(38, -32, 0);
            light.color = new Color(1, .83f, .63f);
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = .65f;
            RenderSettings.sun = light;

            var backdrop = Primitive("Painted sunset", PrimitiveType.Quad, null, Vector3.zero, new Vector3(14, 9, 1), sunset);
            backdrop.transform.SetPositionAndRotation(camera.transform.position + camera.transform.forward * 30, camera.transform.rotation);
            var pedestal = Primitive("Porcelain plinth", PrimitiveType.Cylinder, null,
                new Vector3(0, -.29f, 0), new Vector3(6.7f, .18f, 6.7f), stone);
            Primitive("Plinth foot", PrimitiveType.Cylinder, null,
                new Vector3(0, -.45f, 0), new Vector3(6.15f, .035f, 6.15f), shadow);

            var boat = new GameObject("Little sailboat").transform;
            boat.SetPositionAndRotation(new Vector3(-.25f, .87f, -.15f), Quaternion.Euler(0, 15, 0));
            BottleVoyageModels.Boat(boat);
            ScatterDetails(stone, deck, ivory, rope);

            var fluidObject = new GameObject("Accepted MLS-MPM / 32K");
            fluidObject.transform.position = new Vector3(-2, 0, -2);
            var fluid = fluidObject.AddComponent<MlsMpmFluid>();
            fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/WaterSplash/Shaders/MlsMpm.compute");
            fluid.particleMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/WaterSplash/Art/Shared/Materials/Particles.mat");
            fluid.particleCount = 32768;
            fluid.gridResolution = 48;
            fluid.substeps = 8;
            fluid.maxCatchUpTicks = 4;
            fluid.spatialSortInterval = 0;
            fluid.solverMethod = MlsMpmFluid.SolverMethod.MlsMpm;
            fluid.surfaceQuality = MlsMpmFluid.SurfaceQuality.Medium;
            fluid.showControls = false;
            fluid.showParticles = fluid.renderSurface = true;
            fluid.interactionCamera = camera;
            var stirrer = new GameObject("Invisible water stirrer");
            stirrer.transform.position = new Vector3(1.05f, .38f, .3f);
            fluid.sphereObstacle = stirrer.transform;
            fluid.sphereRadius = .24f;
            fluid.enableWhitewater = true;
            fluid.whitewaterCapacity = 8192;
            var controller = new GameObject("Bottle Voyage").AddComponent<BottleVoyage>();
            controller.fluid = fluid;
            controller.boat = boat;
            controller.waterCamera = camera;
            controller.waterProbe = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/WaterSplash/Shaders/BottleWaterProbe.compute");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = controller.gameObject;
            HorizontalBottleScene.Apply();
        }

        static void ScatterDetails(Material stone, Material wood, Material paper, Material rope)
        {
            for (int i = 0; i < 5; i++)
            {
                float angle = (195 + i * 18) * Mathf.Deg2Rad;
                var pebble = Primitive("Beach pebble",PrimitiveType.Sphere,null,
                    new Vector3(Mathf.Cos(angle)*2.65f,-.055f,Mathf.Sin(angle)*2.65f),
                    new Vector3(.16f+i*.025f,.09f,.12f+i*.014f),stone);
                pebble.transform.rotation=Quaternion.Euler(10,i*43,8);
            }
            var postcard = Primitive("Blank voyage postcard",PrimitiveType.Cube,null,
                new Vector3(-2.2f,-.07f,-1.7f),new Vector3(.72f,.025f,.95f),paper);
            postcard.transform.rotation=Quaternion.Euler(0,-18,0);
            Segment("Postcard rule",postcard.transform,new Vector3(-.38f,.6f,.2f),new Vector3(.38f,.6f,.2f),.018f,wood);
            var coil=MeshObject("Cotton coil",null,Torus(.23f,.016f,-.045f,"Deckside coil"),rope);
            coil.transform.position=new Vector3(2.35f,0,-1.25f);
        }

        static Mesh Torus(float radius,float tube,float height,string name)
        {
            const int rings=48,sides=8;
            var vertices=new List<Vector3>();
            var triangles=new List<int>();
            for(int i=0;i<rings;i++)
                for(int j=0;j<sides;j++)
                {
                    float a=i*Mathf.PI*2/rings,b=j*Mathf.PI*2/sides,r=radius+Mathf.Cos(b)*tube;
                    vertices.Add(new Vector3(Mathf.Cos(a)*r,height+Mathf.Sin(b)*tube,Mathf.Sin(a)*r));
                }
            for(int i=0;i<rings;i++)
                for(int j=0;j<sides;j++)
                {
                    int a=i*sides+j,b=((i+1)%rings)*sides+j,c=i*sides+(j+1)%sides,d=((i+1)%rings)*sides+(j+1)%sides;
                    triangles.AddRange(new[]{a,c,b,c,d,b});
                }
            return Mesh(name,vertices,triangles);
        }

        static Mesh Mesh(string name,List<Vector3> vertices,List<int> triangles)
        {
            var mesh=new Mesh{name=name};mesh.SetVertices(vertices);mesh.SetTriangles(triangles,0);mesh.RecalculateNormals();mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh,Art+"/"+name+".asset");return mesh;
        }

        static Material Material(string name,Shader shader,Color color,float smoothness=0)
        {
            var material=new Material(shader){name=name};
            if(material.HasProperty("_BaseColor"))material.SetColor("_BaseColor",color);
            if(material.HasProperty("_Smoothness"))material.SetFloat("_Smoothness",smoothness);
            AssetDatabase.CreateAsset(material,Art+"/"+name+".mat");return material;
        }

    }
}
