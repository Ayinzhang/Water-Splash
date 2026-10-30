using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    public static class HorizontalBottleScene
    {
        const string Root = BottleVoyageScene.Root;

        [MenuItem("Water Splash/Update Bottle Voyage to Horizontal")]
        public static void Apply()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (EditorApplication.isPlayingOrWillChangePlaymode || scene.path != BottleVoyageScene.ScenePath)
                throw new InvalidOperationException("Open Bottle Voyage in Edit Mode first.");
            var voyage = Object.FindObjectOfType<BottleVoyage>();
            if (voyage == null) throw new InvalidOperationException("Bottle Voyage controller is missing.");
            Undo.RegisterFullObjectHierarchyUndo(voyage.gameObject, "Horizontal drift bottle");
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == "Square-bottom keepsake bottle" || root.name == "Horizontal drift bottle")
                    Undo.DestroyObjectImmediate(root);

            Material glass = LoadMaterial("Sea glass"), cork = LoadMaterial("Natural cork"),
                rope = LoadMaterial("Cotton rigging"), ivory = LoadMaterial("Linen sail"),
                wood = LoadMaterial("Oiled walnut"), brass = LoadMaterial("Weathered brass");
            var boundary = voyage.GetComponent<BottleBoundary>();
            if (boundary == null) boundary = Undo.AddComponent<BottleBoundary>(voyage.gameObject);
            boundary.fluid = voyage.fluid;
            boundary.center = new Vector3(0, .91f, 0);
            boundary.fillHeight = .73f;
            voyage.boundary = boundary;
            var bottle = new GameObject("Horizontal drift bottle").transform;
            Undo.RegisterCreatedObjectUndo(bottle.gameObject, "Horizontal drift bottle");
            bottle.position = boundary.center;
            bottle.localScale = new Vector3(boundary.AxisSign,1,1);
            CreateMesh("Round glass bottle", bottle, GlassMesh(), glass);
            Cylinder("Cork stopper", bottle, new Vector3(1.84f,0,0), .258f,.24f,cork);
            CreateMesh("Rolled glass lip", bottle, Torus(1.72f,.278f,.039f),glass);
            CreateMesh("Cotton neck cord", bottle,Torus(1.51f,.278f,.018f),rope);
            Segment("Tag string",bottle,new Vector3(1.51f,-.04f,-.28f),new Vector3(1.37f,-.40f,-.33f),.009f,rope);
            var tag = Primitive("Paper voyage tag",PrimitiveType.Cube,bottle,new Vector3(1.37f,-.48f,-.33f),
                new Vector3(.23f,.32f,.012f),ivory);
            tag.transform.localRotation = Quaternion.Euler(-8,12,-16);
            Segment("Tag brass mark",tag.transform,new Vector3(0,-.22f,-.65f),new Vector3(0,.22f,-.65f),.024f,brass);

            // Two cradles lift the rounded bottle clear of the plinth.
            foreach (float x in new[] {-1.12f,.55f})
            {
                Primitive("Walnut bottle cradle",PrimitiveType.Cube,bottle,new Vector3(x,-.855f,0),
                    new Vector3(.24f,.12f,1.26f),wood);
                for (int side = -1; side <= 1; side += 2)
                {
                    var support = Primitive("Cradle arm",PrimitiveType.Cube,bottle,new Vector3(x,-.72f,side*.43f),
                        new Vector3(.22f,.30f,.12f),wood);
                    support.transform.localRotation = Quaternion.Euler(-side*30,0,0);
                }
            }
            voyage.boat.localScale = Vector3.one * .39f;
            voyage.boat.SetPositionAndRotation(new Vector3(-.55f*boundary.AxisSign,.70f,-.02f),
                Quaternion.Euler(0,72*boundary.AxisSign,0));
            MlsMpmFluid fluid = voyage.fluid;
            fluid.name = "Bottle MLS-MPM / 32K curved boundary";
            fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/BottleMlsMpm.compute");
            if (fluid.simulationShader == null) throw new InvalidOperationException("Bottle solver has not imported yet.");
            fluid.particleCount = 32768;
            fluid.gridResolution = 80;
            fluid.domainSize = 4;
            fluid.substeps = BottleVoyage.SubstepsForParticleCount(fluid.particleCount);
            fluid.solverMethod = MlsMpmFluid.SolverMethod.MlsMpm;
            fluid.useParticleSoA = true;
            fluid.cacheGridBoundary = true;
            fluid.surfaceQuality = MlsMpmFluid.SurfaceQuality.High;
            fluid.sphereRadius = .12f;
            fluid.sphereObstacle.position = new Vector3(.20f*boundary.AxisSign,.49f,0);
            fluid.showControls = false;
            var camera = voyage.waterCamera;
            camera.transform.position = new Vector3(4.2f*boundary.AxisSign,3.15f,-8.5f);
            camera.transform.LookAt(new Vector3(0,.78f,0));
            camera.orthographicSize = 2.55f;
            var backdrop = GameObject.Find("Painted sunset");
            if (backdrop != null)
                backdrop.transform.SetPositionAndRotation(camera.transform.position + camera.transform.forward * 30,camera.transform.rotation);
            EditorUtility.SetDirty(voyage);
            EditorUtility.SetDirty(boundary);
            EditorUtility.SetDirty(fluid);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = bottle.gameObject;
            SceneView.lastActiveSceneView?.Frame(new Bounds(boundary.center,new Vector3(5,2.5f,2.5f)),false);
        }

        static Mesh GlassMesh()
        {
            const int sides = 96;
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            // A visible 0.035-unit wall, with the exact inner collision profile.
            for (int layer=0;layer<2;layer++)
                foreach(Vector2 p in BottleBoundary.Profile)
                    for(int j=0;j<sides;j++)
                    {
                        float a=j*Mathf.PI*2/sides;
                        float r=p.y+(layer==0?.035f:0);
                        vertices.Add(new Vector3(p.x,Mathf.Cos(a)*r,Mathf.Sin(a)*r));
                    }
            int rings=BottleBoundary.Profile.Length, offset=rings*sides;
            for(int layer=0;layer<2;layer++)
                for(int i=0;i<rings-1;i++)
                    for(int j=0;j<sides;j++)
                    {
                        int a=layer*offset+i*sides+j,b=layer*offset+i*sides+(j+1)%sides;
                        if(layer==0) triangles.AddRange(new[]{a,b,a+sides,b,b+sides,a+sides});
                        else triangles.AddRange(new[]{a,a+sides,b,b,a+sides,b+sides});
                    }
            for(int j=0;j<sides;j++)
            {
                int a=(rings-1)*sides+j,b=(rings-1)*sides+(j+1)%sides;
                triangles.AddRange(new[]{a,a+offset,b,b,a+offset,b+offset});
            }
            int back=vertices.Count;
            vertices.Add(new Vector3(BottleBoundary.Profile[0].x-.025f,0,0));
            for(int j=0;j<sides;j++) triangles.AddRange(new[]{back,(j+1)%sides,j});
            return SaveMesh("Horizontal bottle shell",vertices,triangles);
        }
        static Mesh Torus(float x,float radius,float tube)
        {
            const int rings=80,sides=12;
            var vertices=new List<Vector3>();var triangles=new List<int>();
            for(int i=0;i<rings;i++) for(int j=0;j<sides;j++)
            {
                float a=i*Mathf.PI*2/rings,b=j*Mathf.PI*2/sides,r=radius+Mathf.Cos(b)*tube;
                vertices.Add(new Vector3(x+Mathf.Sin(b)*tube,Mathf.Cos(a)*r,Mathf.Sin(a)*r));
            }
            for(int i=0;i<rings;i++) for(int j=0;j<sides;j++)
            {
                int a=i*sides+j,b=((i+1)%rings)*sides+j,c=i*sides+(j+1)%sides,d=((i+1)%rings)*sides+(j+1)%sides;
                triangles.AddRange(new[]{a,b,c,c,b,d});
            }
            return SaveMesh("Bottle ring "+x.ToString(System.Globalization.CultureInfo.InvariantCulture),vertices,triangles);
        }
        static Mesh SaveMesh(string name,List<Vector3> vertices,List<int> triangles)
        {
            string path=Root+"/Art/"+name+".asset";
            var mesh=new Mesh{name=name};mesh.SetVertices(vertices);mesh.SetTriangles(triangles,0);
            mesh.RecalculateNormals();mesh.RecalculateBounds();
            Mesh existing=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(existing!=null){EditorUtility.CopySerialized(mesh,existing);Object.DestroyImmediate(mesh);return existing;}
            AssetDatabase.CreateAsset(mesh,path);return mesh;
        }
        static void CreateMesh(string name,Transform parent,Mesh mesh,Material material)
        {
            var obj=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));obj.transform.SetParent(parent,false);
            obj.GetComponent<MeshFilter>().sharedMesh=mesh;obj.GetComponent<Renderer>().sharedMaterial=material;
        }
        static GameObject Primitive(string name,PrimitiveType type,Transform parent,Vector3 p,Vector3 scale,Material material)
        {
            var obj=GameObject.CreatePrimitive(type);obj.name=name;obj.transform.SetParent(parent,false);
            obj.transform.localPosition=p;obj.transform.localScale=scale;
            obj.GetComponent<Renderer>().sharedMaterial=material;Object.DestroyImmediate(obj.GetComponent<Collider>());return obj;
        }
        static void Cylinder(string name,Transform parent,Vector3 p,float radius,float length,Material material)
        {
            var obj=Primitive(name,PrimitiveType.Cylinder,parent,p,new Vector3(radius*2,length*.5f,radius*2),material);
            obj.transform.localRotation=Quaternion.Euler(0,0,90);
        }
        static void Segment(string name,Transform parent,Vector3 a,Vector3 b,float radius,Material material)
        {
            var obj=Primitive(name,PrimitiveType.Cylinder,parent,(a+b)*.5f,new Vector3(radius*2,(b-a).magnitude*.5f,radius*2),material);
            obj.transform.localRotation=Quaternion.FromToRotation(Vector3.up,b-a);
        }
        static Material LoadMaterial(string name)=>AssetDatabase.LoadAssetAtPath<Material>(Root+"/Art/"+name+".mat");
    }
}
