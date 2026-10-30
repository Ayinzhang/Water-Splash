using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    public static class BottleVoyageScene
    {
        public const string Root = "Assets/MlsMpm/BottleDemo";
        public const string ScenePath = Root + "/BottleVoyage.unity";

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
            Folder("Assets/MlsMpm", "BottleDemo");
            Folder(Root, "Art");
            Folder(Root, "Settings");
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
            var pipeline = CreatePipeline();

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

            var bottle = new GameObject("Square-bottom keepsake bottle").transform;
            var profile = new[] {
                new Vector4(-.1f, 2.08f, 2.08f, .10f),
                new Vector4(-.02f, 2.12f, 2.12f, .12f),
                new Vector4(.08f, 2.12f, 2.12f, .12f),
                new Vector4(2.12f, 2.12f, 2.12f, .12f),
                new Vector4(2.28f, 2.06f, 2.06f, .16f),
                new Vector4(2.98f, .48f, .48f, .48f),
                new Vector4(3.21f, .48f, .48f, .48f),
                new Vector4(3.24f, .55f, .55f, .55f),
                new Vector4(3.36f, .55f, .55f, .55f)
            };
            MeshObject("Hand-blown glass", bottle, Profile(profile, "Bottle shell"), glass);
            MeshObject("Thick glass foot", bottle, Profile(new[] {
                new Vector4(-.08f,2.13f,2.13f,.13f), new Vector4(.03f,2.13f,2.13f,.13f),
                new Vector4(.045f,2.10f,2.10f,.13f)
            }, "Glass base"), glass);
            Primitive("Cork stopper", PrimitiveType.Cylinder, bottle, new Vector3(0, 3.43f, 0),
                new Vector3(.94f, .19f, .94f), cork);
            MeshObject("Neck cord", bottle, Torus(.55f, .032f, 3.22f, "Neck cord"), rope);
            MeshObject("Glass lip", bottle, Torus(.53f, .035f, 3.34f, "Glass lip"), glass);
            Segment("Hanging cord", bottle, new Vector3(.32f,3.2f,-.37f), new Vector3(.76f,2.72f,-.67f), .014f, rope);
            var tag = Primitive("Voyage tag", PrimitiveType.Cube, bottle, new Vector3(.82f,2.57f,-.72f),
                new Vector3(.32f,.48f,.018f), ivory);
            tag.transform.localRotation = Quaternion.Euler(0, -25, -13);
            Segment("Tag emblem", tag.transform, new Vector3(0,-.22f,-.65f), new Vector3(0,.22f,-.65f), .045f, trim);

            var boat = new GameObject("Little sailboat").transform;
            boat.SetPositionAndRotation(new Vector3(-.25f, .87f, -.15f), Quaternion.Euler(0, 15, 0));
            BuildBoat(boat, wood, deck, ivory, coral, rope, trim);
            ScatterDetails(stone, deck, ivory, rope);

            var fluidObject = new GameObject("Accepted MLS-MPM / 32K");
            fluidObject.transform.position = new Vector3(-2, 0, -2);
            var fluid = fluidObject.AddComponent<MlsMpmFluid>();
            fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/MlsMpm.compute");
            fluid.particleMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/MlsMpm/Materials/Particles.mat");
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
            controller.pipeline = pipeline;
            controller.waterProbe = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/BottleWaterProbe.compute");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = controller.gameObject;
            HorizontalBottleScene.Apply();
        }

        static UniversalRenderPipelineAsset CreatePipeline()
        {
            var renderer = Object.Instantiate(AssetDatabase.LoadAssetAtPath<UniversalRendererData>(
                "Assets/MlsMpm/MobileValidation/Settings/AndroidRenderer.asset"));
            renderer.name = "Bottle Renderer";
            renderer.rendererFeatures.Clear();
            AssetDatabase.CreateAsset(renderer, Root + "/Settings/BottleRenderer.asset");
            var feature = ScriptableObject.CreateInstance<MlsMpmSurfaceFeature>();
            feature.name = "Turquoise water";
            feature.detailedWhitewater = true;
            feature.cropToFluidBounds = true;
            feature.particleShader = RequiredShader("Hidden/WaterSplash/Surface Particles");
            feature.surfaceShader = RequiredShader("Hidden/WaterSplash/Continuous Surface");
            feature.waterColor = new Color(.018f, .29f, .30f);
            feature.absorption = new Vector3(1.45f, .38f, .23f);
            feature.refraction = .045f;
            AssetDatabase.AddObjectToAsset(feature, renderer);
            renderer.rendererFeatures.Add(feature);
            var serialized = new SerializedObject(renderer);
            var map = serialized.FindProperty("m_RendererFeatureMap");
            map.arraySize = 1;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string guid, out long id);
            map.GetArrayElementAtIndex(0).longValue = id;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            renderer.SetDirty();
            EditorUtility.SetDirty(renderer);
            EditorUtility.SetDirty(feature);
            var pipeline = Object.Instantiate(AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(
                "Assets/MlsMpm/MobileValidation/Settings/AndroidPipeline.asset"));
            pipeline.name = "Bottle Pipeline";
            AssetDatabase.CreateAsset(pipeline, Root + "/Settings/BottlePipeline.asset");
            serialized = new SerializedObject(pipeline);
            var list = serialized.FindProperty("m_RendererDataList");
            list.arraySize = 1;
            list.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            serialized.FindProperty("m_DefaultRendererIndex").intValue = 0;
            serialized.FindProperty("m_MainLightShadowmapResolution").intValue = 1024;
            serialized.FindProperty("m_AdditionalLightShadowsSupported").boolValue = false;
            serialized.FindProperty("m_AdditionalLightsRenderingMode").intValue = 0;
            serialized.FindProperty("m_ShadowCascadeCount").intValue = 1;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            pipeline.shadowDistance = 25;
            pipeline.msaaSampleCount = 1;
            pipeline.supportsHDR = false;
            EditorUtility.SetDirty(pipeline);
            return pipeline;
        }

        static void BuildBoat(Transform boat, Material wood, Material deck, Material sail, Material flag, Material rope, Material brass)
        {
            Vector3[] outline = {
                new Vector3(0,0,.85f), new Vector3(.3f,0,.48f), new Vector3(.36f,0,-.28f),
                new Vector3(.25f,0,-.66f), new Vector3(0,0,-.78f), new Vector3(-.25f,0,-.66f),
                new Vector3(-.36f,0,-.28f), new Vector3(-.3f,0,.48f)
            };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int ring = 0; ring < 3; ring++)
                foreach (Vector3 p in outline)
                    vertices.Add(new Vector3(p.x, 0, p.z) * (ring == 0 ? 1 : ring == 1 ? .65f : .15f) +
                        Vector3.up * (ring == 0 ? .14f : ring == 1 ? -.13f : -.22f));
            for (int ring = 0; ring < 2; ring++)
                for (int j = 0; j < outline.Length; j++)
                {
                    int a = ring * 8 + j, b = ring * 8 + (j + 1) % 8;
                    triangles.AddRange(new[] { a, a + 8, b, b, a + 8, b + 8 });
                }
            MeshObject("Walnut hull", boat, Mesh("Boat hull", vertices, triangles), wood);
            vertices.Clear(); triangles.Clear();
            vertices.Add(new Vector3(0, .145f, 0));
            foreach (Vector3 p in outline) vertices.Add(p * .96f + Vector3.up * .145f);
            for (int j = 0; j < 8; j++) triangles.AddRange(new[] { 0, j + 1, (j + 1) % 8 + 1 });
            MeshObject("Teak deck", boat, Mesh("Boat deck", vertices, triangles), deck);
            for (int i = -2; i <= 2; i++)
                Primitive("Deck seam", PrimitiveType.Cube, boat, new Vector3(i * .105f,.15f,-.1f),
                    new Vector3(.009f,.004f,.91f), wood);
            Segment("Mast", boat, new Vector3(0,.16f,0), new Vector3(0,1.58f,0), .026f, wood);
            Segment("Boom", boat, new Vector3(0,.35f,0), new Vector3(0,.35f,-.68f), .021f, wood);
            MeshObject("Billowing linen mainsail", boat, Sail("Mainsail", new Vector3(0,1.48f,-.045f),
                new Vector3(0,.40f,-.045f), new Vector3(0,.40f,-.66f), .10f), sail);
            MeshObject("Linen jib", boat, Sail("Jib", new Vector3(0,1.40f,.045f),
                new Vector3(0,.31f,.65f), new Vector3(0,.40f,.10f), .065f), sail);
            MeshObject("Ochre pennant", boat, Mesh("Pennant",
                new List<Vector3> { new Vector3(0,1.56f,0),new Vector3(0,1.4f,0),new Vector3(.015f,1.47f,-.34f) },
                new List<int> { 0,1,2,2,1,0 }), flag);
            Segment("Forestay", boat, new Vector3(0,1.52f,0), new Vector3(0,.18f,.76f), .006f, rope);
            Segment("Backstay", boat, new Vector3(0,1.52f,0), new Vector3(0,.18f,-.70f), .006f, rope);
            Segment("Port rigging", boat, new Vector3(0,1.20f,0), new Vector3(-.32f,.17f,-.12f), .006f, rope);
            Segment("Starboard rigging", boat, new Vector3(0,1.20f,0), new Vector3(.32f,.17f,-.12f), .006f, rope);
            Primitive("Cabin", PrimitiveType.Cube, boat, new Vector3(0,.21f,-.29f), new Vector3(.26f,.13f,.25f), sail);
            Primitive("Cabin hatch", PrimitiveType.Cube, boat, new Vector3(0,.282f,-.29f), new Vector3(.29f,.025f,.28f), wood);
            Primitive("Brass hatch", PrimitiveType.Sphere, boat, new Vector3(.135f,.23f,-.29f), new Vector3(.015f,.058f,.058f), brass);
            var rim = boat.gameObject.AddComponent<LineRenderer>();
            rim.useWorldSpace = false; rim.loop = true; rim.positionCount = outline.Length;
            rim.widthMultiplier = .018f; rim.numCornerVertices = 2; rim.sharedMaterial = sail;
            for (int j = 0; j < outline.Length; j++) rim.SetPosition(j, outline[j] + Vector3.up * .145f);
            rim.shadowCastingMode = ShadowCastingMode.Off;
        }

        static Mesh Sail(string name, Vector3 a, Vector3 b, Vector3 c, float bulge)
        {
            const int divisions = 10;
            var vertices = new List<Vector3>();
            var indices = new Dictionary<Vector2Int, int>();
            var triangles = new List<int>();
            for (int i = 0; i <= divisions; i++)
                for (int j = 0; j <= divisions - i; j++)
                {
                    float u = (float)i / divisions, v = (float)j / divisions, w = 1 - u - v;
                    Vector3 p = a * u + b * v + c * w;
                    p.x += bulge * 27 * u * v * w;
                    indices.Add(new Vector2Int(i,j), vertices.Count); vertices.Add(p);
                }
            for (int i = 0; i < divisions; i++)
                for (int j = 0; j < divisions - i; j++)
                {
                    int p = indices[new Vector2Int(i,j)], q = indices[new Vector2Int(i+1,j)], r = indices[new Vector2Int(i,j+1)];
                    triangles.AddRange(new[] {p,q,r});
                    if (i+j < divisions-1)
                        triangles.AddRange(new[] {q,indices[new Vector2Int(i+1,j+1)],r});
                }
            return Mesh(name,vertices,triangles);
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

        static Mesh Profile(Vector4[] profile, string name)
        {
            const int perCorner=10, count=perCorner*4;
            var vertices=new List<Vector3>();
            var triangles=new List<int>();
            foreach(Vector4 level in profile)
                for(int corner=0;corner<4;corner++)
                    for(int step=0;step<perCorner;step++)
                    {
                        float angle=(corner+(float)step/(perCorner-1))*Mathf.PI*.5f;
                        float cx=(corner==0||corner==3?1:-1)*(level.y-level.w);
                        float cz=(corner<2?1:-1)*(level.z-level.w);
                        vertices.Add(new Vector3(cx+Mathf.Cos(angle)*level.w,level.x,cz+Mathf.Sin(angle)*level.w));
                    }
            for(int ring=0;ring<profile.Length-1;ring++)
                for(int j=0;j<count;j++)
                {
                    int a=ring*count+j,b=ring*count+(j+1)%count;
                    triangles.AddRange(new[]{a,b+count,b,a,a+count,b+count});
                }
            return Mesh(name,vertices,triangles);
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
            AssetDatabase.CreateAsset(mesh,Root+"/Art/"+name+".asset");return mesh;
        }

        static GameObject MeshObject(string name,Transform parent,Mesh mesh,Material material)
        {
            var obj=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));obj.transform.SetParent(parent,false);
            obj.GetComponent<MeshFilter>().sharedMesh=mesh;obj.GetComponent<Renderer>().sharedMaterial=material;return obj;
        }

        static GameObject Primitive(string name,PrimitiveType type,Transform parent,Vector3 position,Vector3 scale,Material material)
        {
            var obj=GameObject.CreatePrimitive(type);obj.name=name;obj.transform.SetParent(parent,false);
            obj.transform.localPosition=position;obj.transform.localScale=scale;
            obj.GetComponent<Renderer>().sharedMaterial=material;Object.DestroyImmediate(obj.GetComponent<Collider>());return obj;
        }

        static void Segment(string name,Transform parent,Vector3 start,Vector3 end,float radius,Material material)
        {
            var obj=Primitive(name,PrimitiveType.Cylinder,parent,(start+end)*.5f,
                new Vector3(radius*2,(end-start).magnitude*.5f,radius*2),material);
            obj.transform.localRotation=Quaternion.FromToRotation(Vector3.up,end-start);
        }

        static Material Material(string name,Shader shader,Color color,float smoothness=0)
        {
            var material=new Material(shader){name=name};
            if(material.HasProperty("_BaseColor"))material.SetColor("_BaseColor",color);
            if(material.HasProperty("_Smoothness"))material.SetFloat("_Smoothness",smoothness);
            AssetDatabase.CreateAsset(material,Root+"/Art/"+name+".mat");return material;
        }

        static Shader RequiredShader(string name)
        {
            Shader shader=Shader.Find(name);
            if(shader==null)throw new InvalidOperationException("Missing shader: "+name);
            return shader;
        }

        static void Folder(string parent,string name)
        {
            if(!AssetDatabase.IsValidFolder(parent+"/"+name))AssetDatabase.CreateFolder(parent,name);
        }
    }
}
