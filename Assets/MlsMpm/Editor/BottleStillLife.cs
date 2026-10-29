using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    public static class BottleStillLife
    {
        const string Art = BottleVoyageScene.Root + "/Art";
        const string DetailRoot = "Voyage still-life details";

        [MenuItem("Water Splash/Detail Current Bottle Voyage")]
        public static void Apply()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (EditorApplication.isPlayingOrWillChangePlaymode || scene.path != BottleVoyageScene.ScenePath)
                throw new InvalidOperationException("Open Bottle Voyage in Edit Mode first.");
            var voyage = Object.FindObjectOfType<BottleVoyage>();
            var bottle = GameObject.Find("Horizontal drift bottle");
            if (voyage == null || voyage.boundary == null || bottle == null)
                throw new InvalidOperationException("The current scene needs its horizontal bottle and curved boundary.");
            if (GameObject.Find(DetailRoot) != null)
                throw new InvalidOperationException("Still-life details already exist; edit them in the scene instead of overwriting them.");
            Undo.RecordObject(voyage.boundary, "Turn bottle mouth left");
            Undo.RecordObject(bottle.transform, "Turn bottle mouth left");
            voyage.boundary.mouthFacesLeft = true;
            Vector3 scale = bottle.transform.localScale;
            scale.x = -Mathf.Abs(scale.x);
            bottle.transform.localScale = scale;
            Undo.RecordObject(voyage.fluid, "Bottle whitewater");
            var fluid = voyage.fluid;
            fluid.enableWhitewater = true;
            fluid.whitewaterCapacity = 8192;
            fluid.whitewaterEmissionRate = 8;
            fluid.whitewaterSpeedThreshold = 1.2f;
            fluid.whitewaterDeformationThreshold = 4;
            fluid.whitewaterLifetime = 2.5f;
            fluid.whitewaterRadiusScale = .4f;
            MirrorView(voyage);

            var root = new GameObject(DetailRoot).transform;
            Undo.RegisterCreatedObjectUndo(root.gameObject, "Bottle still-life details");
            var undersea = new GameObject("Miniature seabed (visual only)").transform;
            undersea.SetParent(root, false);
            var desk = new GameObject("Mariner's desktop").transform;
            desk.SetParent(root, false);
            Material paleStone = Mat("Shore limestone", new Color(.52f,.52f,.41f));
            Material darkStone = Mat("Tumbled slate", new Color(.25f,.34f,.32f));
            Material sand = Mat("Shell sand", new Color(.65f,.59f,.42f));
            Material shell = Mat("Shell ivory", new Color(.89f,.78f,.60f));
            Material paper = Mat("Chart parchment", new Color(.82f,.72f,.52f));
            Material ink = Mat("Chart sepia", new Color(.30f,.24f,.18f));
            Material brass = AssetDatabase.LoadAssetAtPath<Material>(Art+"/Weathered brass.mat");
            Material wood = AssetDatabase.LoadAssetAtPath<Material>(Art+"/Oiled walnut.mat");
            Material cord = AssetDatabase.LoadAssetAtPath<Material>(Art+"/Cotton rigging.mat");
            Material green = Mat("Kelp jade",new Color(.14f,.34f,.24f),"WaterSplash/Bottle Seaweed");
            Material olive = Mat("Kelp olive",new Color(.30f,.37f,.16f),"WaterSplash/Bottle Seaweed");
            Material tabletop = Mat("Sun-worn walnut tabletop",new Color(.38f,.25f,.15f),"WaterSplash/Bottle Still Life");
            tabletop.SetColor("_GrainColor",new Color(.15f,.085f,.04f));
            tabletop.SetFloat("_Grain",.18f);
            var plinth = GameObject.Find("Porcelain plinth");
            if (plinth != null)
            {
                var renderer = plinth.GetComponent<Renderer>();
                Undo.RecordObject(renderer,"Walnut tabletop");
                renderer.sharedMaterial=tabletop;
            }

            // Low stones are seated just above the curved wall, clear of the boat.
            Mesh rock = Rock();
            var random = new System.Random(7049);
            for(int i=0;i<34;i++)
            {
                float x=Mathf.Lerp(-.4f,1.52f,(float)random.NextDouble());
                float z=Mathf.Lerp(-.38f,.38f,(float)random.NextDouble());
                float radius=BottleBoundary.Radius(-x,out _);
                float bottom=voyage.boundary.center.y-Mathf.Sqrt(Mathf.Max(.01f,radius*radius-z*z));
                float size=Mathf.Lerp(.065f,.145f,(float)random.NextDouble());
                var stone=MeshObject("Rounded seabed stone",undersea,rock,i%3==0?darkStone:paleStone);
                stone.transform.localPosition=new Vector3(x,bottom+size*.22f,z);
                stone.transform.localScale=new Vector3(size*1.6f,size*.65f,size);
                stone.transform.localRotation=Quaternion.Euler(0,(float)random.NextDouble()*360,0);
            }
            for(int i=0;i<55;i++)
            {
                float x=Mathf.Lerp(-.30f,1.5f,(float)random.NextDouble());
                float z=Mathf.Lerp(-.35f,.35f,(float)random.NextDouble());
                float radius=BottleBoundary.Radius(-x,out _);
                float y=voyage.boundary.center.y-Mathf.Sqrt(Mathf.Max(.01f,radius*radius-z*z))+.018f;
                var grain=MeshObject("Shell gravel",undersea,rock,sand);
                grain.transform.localPosition=new Vector3(x,y,z);
                grain.transform.localScale=new Vector3(.045f,.022f,.035f);
            }
            Mesh leaf=Leaf();
            for(int cluster=0;cluster<6;cluster++)
            {
                float x=new[]{1.25f,1.0f,.65f,-.15f,-.36f,.25f}[cluster];
                float z=cluster%2==0?.30f:-.27f;
                float radius=BottleBoundary.Radius(-x,out _);
                float y=voyage.boundary.center.y-Mathf.Sqrt(radius*radius-z*z)+.025f;
                var tuft=new GameObject("Eelgrass tuft").transform;
                tuft.SetParent(undersea,false);tuft.localPosition=new Vector3(x,y,z);
                for(int j=0;j<5;j++)
                {
                    var blade=MeshObject("Curved kelp blade",tuft,leaf,j%2==0?green:olive);
                    blade.transform.localRotation=Quaternion.Euler(0,j*71+cluster*27,0);
                    blade.transform.localScale=new Vector3(.7f+(float)random.NextDouble()*.6f,
                        .55f+(float)random.NextDouble()*.7f,1);
                }
            }
            Mesh seashell=Shell();
            PlaceMesh("Bottle scallop",undersea,seashell,shell,new Vector3(1.05f,.25f,-.22f),Vector3.one*.13f,Quaternion.Euler(0,20,0));

            // Compass, chart and shells leave the center silhouette unobstructed.
            var chart=new GameObject("Hand-drawn nautical chart").transform;
            chart.SetParent(desk,false);
            chart.SetPositionAndRotation(new Vector3(1.55f,-.085f,-1.25f),Quaternion.Euler(0,-18,0));
            Primitive("Parchment sheet",PrimitiveType.Cube,chart,Vector3.zero,new Vector3(.95f,.018f,.66f),paper);
            for(int i=-2;i<=2;i++)
            {
                Line("Latitude",chart,new[]{new Vector3(-.43f,.013f,i*.11f),new Vector3(.43f,.013f,i*.11f)},.0025f,ink);
                Line("Longitude",chart,new[]{new Vector3(i*.15f,.013f,-.28f),new Vector3(i*.15f,.013f,.28f)},.0025f,ink);
            }
            var coast=new Vector3[26];
            for(int i=0;i<coast.Length;i++)
            {
                float a=i*Mathf.PI*2/(coast.Length-1),r=.15f+.035f*Mathf.Sin(a*5)+.02f*Mathf.Cos(a*3);
                coast[i]=new Vector3(Mathf.Cos(a)*r*1.8f,.017f,Mathf.Sin(a)*r);
            }
            Line("Ink coastline",chart,coast,.008f,ink);
            Line("Dotted course",chart,new[]{new Vector3(-.38f,.018f,-.22f),new Vector3(-.20f,.018f,-.06f),
                new Vector3(.12f,.018f,.11f),new Vector3(.32f,.018f,.20f)},.005f,brass);

            var compass=new GameObject("Pocket compass").transform;
            compass.SetParent(desk,false);compass.localPosition=new Vector3(.30f,-.06f,-1.72f);
            Primitive("Brass case",PrimitiveType.Cylinder,compass,Vector3.zero,new Vector3(.50f,.038f,.50f),brass);
            Primitive("Ivory dial",PrimitiveType.Cylinder,compass,new Vector3(0,.039f,0),new Vector3(.43f,.004f,.43f),shell);
            for(int i=0;i<24;i++)
            {
                float a=i*Mathf.PI*2/24;
                float inner=i%3==0?.155f:.178f;
                Line("Compass graduation",compass,new[]{new Vector3(Mathf.Sin(a)*inner,.045f,Mathf.Cos(a)*inner),
                    new Vector3(Mathf.Sin(a)*.20f,.045f,Mathf.Cos(a)*.20f)},.004f,ink);
            }
            Mesh needle=SaveMesh("Compass needle",new List<Vector3>{new Vector3(0,0,.18f),new Vector3(-.033f,0,0),
                new Vector3(0,0,-.18f),new Vector3(.033f,0,0)},new List<int>{0,3,1,1,3,2});
            PlaceMesh("North needle",compass,needle,ink,new Vector3(0,.05f,0),Vector3.one,Quaternion.Euler(0,32,0));
            Primitive("Needle pin",PrimitiveType.Sphere,compass,new Vector3(0,.055f,0),Vector3.one*.038f,brass);
            PlaceMesh("Scallop keepsake",desk,seashell,shell,new Vector3(-1.3f,-.065f,-1.38f),Vector3.one*.27f,Quaternion.Euler(0,-20,0));
            PlaceMesh("Small scallop",desk,seashell,paper,new Vector3(-1.63f,-.07f,-1.10f),Vector3.one*.19f,Quaternion.Euler(0,32,0));
            var ropePoints=new Vector3[190];
            for(int i=0;i<ropePoints.Length;i++)
            {
                float t=(float)i/(ropePoints.Length-1),a=t*Mathf.PI*2*3.2f,r=.09f+t*.17f;
                ropePoints[i]=new Vector3(-1.95f+Mathf.Cos(a)*r,-.055f, .05f+Mathf.Sin(a)*r);
            }
            Line("Coiled mooring rope",desk,ropePoints,.024f,cord);
            Line("Loose rope tail",desk,new[]{ropePoints[ropePoints.Length-1],new Vector3(-1.88f,-.05f,-.35f),
                new Vector3(-1.62f,-.05f,-.49f),new Vector3(-1.52f,-.05f,-.63f)},.024f,cord);
            EditorUtility.SetDirty(voyage.boundary);
            EditorUtility.SetDirty(fluid);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject=root.gameObject;
        }

        public static void MirrorView(BottleVoyage voyage)
        {
            var camera=voyage.waterCamera;
            Undo.RecordObject(camera.transform,"Left-front bottle view");
            Vector3 position=camera.transform.position;
            position.x=voyage.boundary.center.x-Mathf.Abs(position.x-voyage.boundary.center.x);
            camera.transform.position=position;
            camera.transform.LookAt(new Vector3(voyage.boundary.center.x,.78f,voyage.boundary.center.z));
            var backdrop=GameObject.Find("Painted sunset");
            if(backdrop!=null)
            {
                Undo.RecordObject(backdrop.transform,"Mirror backdrop framing");
                backdrop.transform.SetPositionAndRotation(camera.transform.position+camera.transform.forward*30,
                    camera.transform.rotation);
            }
            Undo.RecordObject(voyage.boat,"Mirror initial boat position");
            Vector3 boat=voyage.boat.position;
            boat.x=voyage.boundary.center.x+Mathf.Abs(boat.x-voyage.boundary.center.x);
            voyage.boat.SetPositionAndRotation(boat,Quaternion.Euler(0,-72,0));
            Undo.RecordObject(voyage.fluid.sphereObstacle,"Mirror initial stirrer");
            position=voyage.fluid.sphereObstacle.position;
            position.x=voyage.boundary.center.x-Mathf.Abs(position.x-voyage.boundary.center.x);
            voyage.fluid.sphereObstacle.position=position;
        }

        static Mesh Rock()
        {
            var vertices=new List<Vector3>();var triangles=new List<int>();
            const int rings=7,sides=12;
            for(int y=0;y<=rings;y++)
                for(int j=0;j<sides;j++)
                {
                    float a=j*Mathf.PI*2/sides,b=y*Mathf.PI/rings;
                    float r=.5f*(1+.08f*Mathf.Sin(a*3+b*7));
                    vertices.Add(new Vector3(Mathf.Cos(a)*Mathf.Sin(b)*r,Mathf.Cos(b)*.5f,Mathf.Sin(a)*Mathf.Sin(b)*r));
                }
            for(int y=0;y<rings;y++)for(int j=0;j<sides;j++)
            {
                int a=y*sides+j,b=y*sides+(j+1)%sides;
                triangles.AddRange(new[]{a,b,a+sides,b,b+sides,a+sides});
            }
            return SaveMesh("Tumbled pebble",vertices,triangles);
        }

        static Mesh Leaf()
        {
            var vertices=new List<Vector3>();var triangles=new List<int>();var uv=new List<Vector2>();
            for(int i=0;i<=12;i++)
            {
                float t=i/12f,w=.030f*Mathf.Sin(Mathf.PI*t)*(.7f+.3f*Mathf.Sin(t*19));
                Vector3 center=new Vector3(Mathf.Sin(t*4)*.045f,t*.33f,t*t*.09f);
                vertices.Add(center+Vector3.left*w);vertices.Add(center+Vector3.right*w);
                uv.Add(new Vector2(0,t));uv.Add(new Vector2(1,t));
                if(i<12){int a=i*2;triangles.AddRange(new[]{a,a+2,a+1,a+1,a+2,a+3});}
            }
            Mesh mesh=SaveMesh("Ribbon eelgrass",vertices,triangles);mesh.SetUVs(0,uv);EditorUtility.SetDirty(mesh);return mesh;
        }

        static Mesh Shell()
        {
            var vertices=new List<Vector3>();var triangles=new List<int>();
            const int rings=10,sides=32;
            for(int i=0;i<=rings;i++)for(int j=0;j<=sides;j++)
            {
                float t=i/(float)rings,a=Mathf.Lerp(-1.05f,1.05f,j/(float)sides);
                float r=t*(1+.035f*Mathf.Cos(a*26));
                vertices.Add(new Vector3(Mathf.Sin(a)*r,Mathf.Sin(t*Mathf.PI)*.20f+
                    .025f*(.5f+.5f*Mathf.Cos(a*26))*t,Mathf.Cos(a)*r-.25f));
            }
            for(int i=0;i<rings;i++)for(int j=0;j<sides;j++)
            {
                int a=i*(sides+1)+j,b=a+1;
                triangles.AddRange(new[]{a,a+sides+1,b,b,a+sides+1,b+sides+1});
            }
            return SaveMesh("Ribbed scallop",vertices,triangles);
        }

        static Material Mat(string name,Color color,string shader="Universal Render Pipeline/Lit")
        {
            string path=Art+"/"+name+".mat";var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material!=null)return material;
            Shader source=Shader.Find(shader);
            if(source==null)throw new InvalidOperationException("Missing shader "+shader);
            material=new Material(source){name=name};material.SetColor("_BaseColor",color);
            if(material.HasProperty("_Smoothness"))material.SetFloat("_Smoothness",.22f);
            material.enableInstancing=true;
            AssetDatabase.CreateAsset(material,path);return material;
        }

        static Mesh SaveMesh(string name,List<Vector3> vertices,List<int> indices)
        {
            string path=Art+"/"+name+".asset";
            var existing=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(existing!=null)return existing;
            var mesh=new Mesh{name=name};mesh.SetVertices(vertices);mesh.SetTriangles(indices,0);mesh.RecalculateNormals();
            mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,path);return mesh;
        }
        static GameObject MeshObject(string name,Transform parent,Mesh mesh,Material mat)
        {
            var obj=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));obj.transform.SetParent(parent,false);
            obj.GetComponent<MeshFilter>().sharedMesh=mesh;obj.GetComponent<Renderer>().sharedMaterial=mat;return obj;
        }
        static void PlaceMesh(string name,Transform parent,Mesh mesh,Material mat,Vector3 p,Vector3 scale,Quaternion rotation)
        {
            var obj=MeshObject(name,parent,mesh,mat);obj.transform.localPosition=p;obj.transform.localScale=scale;obj.transform.localRotation=rotation;
        }
        static GameObject Primitive(string name,PrimitiveType type,Transform parent,Vector3 p,Vector3 scale,Material mat)
        {
            var obj=GameObject.CreatePrimitive(type);obj.name=name;obj.transform.SetParent(parent,false);
            obj.transform.localPosition=p;obj.transform.localScale=scale;obj.GetComponent<Renderer>().sharedMaterial=mat;
            Object.DestroyImmediate(obj.GetComponent<Collider>());return obj;
        }
        static void Line(string name,Transform parent,Vector3[] points,float width,Material material)
        {
            var obj=new GameObject(name);obj.transform.SetParent(parent,false);var line=obj.AddComponent<LineRenderer>();
            line.useWorldSpace=false;line.positionCount=points.Length;line.SetPositions(points);line.widthMultiplier=width;
            line.numCornerVertices=3;line.numCapVertices=3;line.sharedMaterial=material;
            line.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }
}
