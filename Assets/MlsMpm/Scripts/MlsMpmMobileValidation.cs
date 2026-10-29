using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WaterSplash
{
    [DefaultExecutionOrder(-110)]
    public sealed class MlsMpmMobileValidation : MonoBehaviour
    {
        public MlsMpmFluid fluid;
        public Camera waterCamera;
        public Transform sphere;
        public UniversalRenderPipelineAsset pipeline;
        public MlsMpmSurfaceFeature surfaceFeature;
        public int particles = 32768, grid = 48, steps = 8;
        [Range(1,4)] public int maxCatchUpTicks = 4;
        [Range(0,16)] public int spatialSortInterval = 1;
        public MlsMpmFluid.SolverMethod solverMethod;
        [Range(1,16)] public int positionIterations = 2;
        public bool foam = true, rendering = true, paused, automaticStir;
        public int width = 1280, height = 720;
        public bool adaptiveResolution = true;
        [Range(.25f,1)] public float renderScale = 1;
        public MlsMpmFluid.SurfaceQuality quality = MlsMpmFluid.SurfaceQuality.High;
        RenderTexture output; RenderPipelineAsset previousPipeline;
        Camera presentationCamera;
        int previousFps, previousVsync, previousSleep; bool ownsSettings, suspended, skipDelta, dragging;
        double backlog, wall, simulated; int ticks; float meanFrame; string error, saved;
        int lastFrameTicks;
        float nextTelemetry;
        Vector3 sphereTarget = new Vector3(.95f,.55f,0), spherePosition;
        readonly float[] samples = new float[600]; int sampleCount, cursor;
        readonly int[] tickSamples = new int[600];
        string profileSettings;
        int profileParticles, profileGrid, profileSteps, profileFlags = -1;
        void RecordProfileSettings()
        {
            int sort=solverMethod==MlsMpmFluid.SolverMethod.PositionBasedMpm?spatialSortInterval:0;
            int flags=(rendering?1:0)|(foam?2:0)|(maxCatchUpTicks<<2)|(sort<<5)|
                ((int)solverMethod<<11)|(positionIterations<<12);
            if(profileSettings==null || profileParticles!=particles || profileGrid!=grid || profileSteps!=steps || profileFlags!=flags)
            {
                profileParticles=particles;profileGrid=grid;profileSteps=steps;profileFlags=flags;
                profileSettings=string.Format("MPM Config/{0}p grid{1} steps{2} render{3} foam{4} catchup{5} sort{6} solver{7} iter{8}",particles,grid,steps,rendering?1:0,foam?1:0,maxCatchUpTicks,sort,solverMethod,positionIterations);
            }
            UnityEngine.Profiling.Profiler.BeginSample(profileSettings);
            UnityEngine.Profiling.Profiler.EndSample();
        }
        GUIStyle statusStyle;

        void Awake()
        {
            ApplyDevelopmentLaunchSettings();
            if (fluid != null) fluid.externalControl = true;
            previousPipeline = QualitySettings.renderPipeline; previousFps = Application.targetFrameRate;
            previousVsync = QualitySettings.vSyncCount; previousSleep = Screen.sleepTimeout; ownsSettings = true;
            QualitySettings.renderPipeline = pipeline; QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = Application.isMobilePlatform ? 1000 : -1; Application.runInBackground = true;
            Screen.sleepTimeout = SleepTimeout.NeverSleep; Input.simulateMouseWithTouches = true;
            // A camera targeting only an RT does not initialize the Android
            // swapchain. Clear it explicitly before IMGUI composites the water.
            var screenCamera=new GameObject("Mobile presentation clear");
            screenCamera.transform.SetParent(transform,false);
            presentationCamera=screenCamera.AddComponent<Camera>();
            presentationCamera.depth=-100; presentationCamera.clearFlags=CameraClearFlags.SolidColor;
            presentationCamera.backgroundColor=Color.black; presentationCamera.cullingMask=0;
            presentationCamera.allowHDR=presentationCamera.allowMSAA=presentationCamera.useOcclusionCulling=false;
            var screenData=presentationCamera.GetUniversalAdditionalCameraData();
            screenData.renderPostProcessing=false;screenData.renderShadows=false;
            screenData.requiresColorOption=screenData.requiresDepthOption=CameraOverrideOption.Off;
            UpdateResolution(); ResetWater();
            Debug.Log("Water Splash Android validation | " + SystemInfo.deviceModel + " | " + SystemInfo.graphicsDeviceName +
                " | " + SystemInfo.graphicsDeviceType + " | fixed tick 1/60s | targetFrameRate="+Application.targetFrameRate+" | vSync=0 | Development=" + Debug.isDebugBuild);
        }
        void ApplyDevelopmentLaunchSettings()
        {
#if UNITY_ANDROID && DEVELOPMENT_BUILD && !UNITY_EDITOR
            // Repeatable USB profiling without injecting touches or changing
            // system input permissions. Extras apply only at player launch.
            using(var player=new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using(var activity=player.GetStatic<AndroidJavaObject>("currentActivity"))
            using(var intent=activity.Call<AndroidJavaObject>("getIntent"))
            {
                particles=Mathf.Clamp(intent.Call<int>("getIntExtra","mpmParticles",particles),512,262144);
                grid=MlsMpmFluid.PresetGridResolution(particles);
                steps=Mathf.Clamp(intent.Call<int>("getIntExtra","mpmSubsteps",steps),1,32);
                maxCatchUpTicks=Mathf.Clamp(intent.Call<int>("getIntExtra","mpmCatchUpTicks",maxCatchUpTicks),1,4);
                rendering=intent.Call<int>("getIntExtra","mpmRendering",rendering?1:0)!=0;
                foam=intent.Call<int>("getIntExtra","mpmFoam",foam?1:0)!=0;
                spatialSortInterval=Mathf.Clamp(intent.Call<int>("getIntExtra","mpmSort",spatialSortInterval),0,16);
                solverMethod=(MlsMpmFluid.SolverMethod)Mathf.Clamp(intent.Call<int>("getIntExtra","mpmSolver",(int)solverMethod),0,1);
                positionIterations=Mathf.Clamp(intent.Call<int>("getIntExtra","mpmIterations",positionIterations),1,16);
                adaptiveResolution=intent.Call<int>("getIntExtra","mpmAdaptiveResolution",adaptiveResolution?1:0)!=0;
                renderScale=Mathf.Clamp(intent.Call<float>("getFloatExtra","mpmRenderScale",renderScale),.25f,1);
                width=Mathf.Max(2,intent.Call<int>("getIntExtra","mpmWidth",width));
                height=Mathf.Max(2,intent.Call<int>("getIntExtra","mpmHeight",height));
            }
#endif
        }
        public void SetResolution(int w, int h)
        {
            w=Mathf.Max(2,w);h=Mathf.Max(2,h);
            if(output!=null&&output.width==w&&output.height==h)return;
            if (output != null) { waterCamera.targetTexture = null; output.Release(); Destroy(output); }
            width=w; height=h; output = new RenderTexture(w,h,24,RenderTextureFormat.ARGB32) { name="Mobile adaptive water", antiAliasing=1 };
            output.Create(); waterCamera.targetTexture=output;waterCamera.aspect=(float)w/h;
        }
        void UpdateResolution()
        {
            if(adaptiveResolution){var rect=ViewRect();var size=AdaptiveSize(rect.size,renderScale);SetResolution(size.x,size.y);}
            else SetResolution(width,height);
        }
        public static Vector2Int AdaptiveSize(Vector2 viewport,float scale)
        {
            scale=Mathf.Clamp(scale,.25f,1);
            return new Vector2Int(Mathf.Max(2,Mathf.RoundToInt(viewport.x*scale/2)*2),Mathf.Max(2,Mathf.RoundToInt(viewport.y*scale/2)*2));
        }
        public void ResetWater()
        {
            try
            {
                error = null; saved = null;
                fluid.gameObject.SetActive(false); fluid.externalControl = true; fluid.showControls = false;
                fluid.particleCount=particles; fluid.gridResolution=grid; fluid.substeps=steps; fluid.domainSize=4;
                fluid.whitewaterCapacity=Mathf.Min(particles,32768); fluid.enableWhitewater=foam; fluid.spatialSortInterval=spatialSortInterval;
                fluid.solverMethod=solverMethod;fluid.positionIterations=positionIterations;
                fluid.showParticles=rendering; fluid.renderSurface=true; fluid.surfaceQuality=quality;
                fluid.affineDamping=fluid.divergenceDamping=0; fluid.gravity=new Vector3(0,-9.81f,0);
                sphereTarget=spherePosition=new Vector3(.95f,.55f,0); sphere.position=spherePosition;
                fluid.gameObject.SetActive(true);
                if (!fluid.IsInitialized || fluid.Error != null) throw new InvalidOperationException(fluid.Error ?? "GPU initialization failed.");
                int side=Mathf.CeilToInt(Mathf.Pow(particles,1f/3)); while(side*side*side<particles) side++;
                float spacing=4f*16/27/side; var positions=new Vector3[particles];
                for(int i=0;i<particles;i++) positions[i]=new Vector3(-2+4f/27,4f/27,-2+24f/27)+
                    spacing*new Vector3(i%side+.5f,i/side%side+.5f,i/(side*side)+.5f);
                fluid.SetComparisonInitialPositions(positions,spacing);
                // OnEnable of the solver may change frame settings; apply the profiling policy last.
                Application.targetFrameRate=Application.isMobilePlatform?1000:-1; QualitySettings.vSyncCount=0;
                ticks=lastFrameTicks=sampleCount=cursor=0; simulated=wall=backlog=0; meanFrame=0; skipDelta=true; dragging=false;nextTelemetry=Time.realtimeSinceStartup+5;
            }
            catch(Exception e) { error=e.Message; paused=true; Debug.LogException(e,this); }
        }
        void Update()
        {
            UpdateResolution();
            lastFrameTicks=0;
            RecordProfileSettings();
            if(Input.GetKeyDown(KeyCode.R)) ResetWater();
            if(Input.GetKeyDown(KeyCode.Space)) paused=!paused;
            if(error != null || fluid == null || !fluid.IsInitialized) return;
            if(fluid.Error != null || fluid.RenderingError != null) { error=fluid.Error ?? fluid.RenderingError; paused=true; return; }
            Pointer();
            if(suspended || paused) { skipDelta=true; return; }
            if(skipDelta) { skipDelta=false; return; }
            float dt=Time.unscaledDeltaTime; wall+=dt; backlog+=dt;
            meanFrame=meanFrame<=0?dt:Mathf.Lerp(meanFrame,dt,.04f);
            // Render frames can contain zero, one, or several fixed physics ticks.
            // Backlog remains visible; no elapsed simulation time is discarded.
            int due=Math.Min(Mathf.Clamp(maxCatchUpTicks,1,4),(int)(backlog/MlsMpmFluid.FixedTick));
            for(int i=0;i<due;i++)
            {
                spherePosition=Vector3.MoveTowards(spherePosition,sphereTarget,2*(float)MlsMpmFluid.FixedTick);
                if(automaticStir) { float t=(float)Math.Max(0,simulated-1); spherePosition=new Vector3(.95f+.55f*Mathf.Sin(t*2),.55f,.6f*Mathf.Sin(t*1.4f)); }
                sphere.position=spherePosition;
                UnityEngine.Profiling.Profiler.BeginSample("MLS-MPM/Fixed 60Hz tick");
                try { fluid.StepSimulation(1); } finally { UnityEngine.Profiling.Profiler.EndSample(); }
                ticks++; simulated=ticks*MlsMpmFluid.FixedTick; backlog-=MlsMpmFluid.FixedTick;
                lastFrameTicks++;
            }
            samples[cursor]=dt*1000; tickSamples[cursor]=lastFrameTicks;
            cursor=(cursor+1)%samples.Length; sampleCount=Mathf.Min(sampleCount+1,samples.Length);
            if(Debug.isDebugBuild&&Time.realtimeSinceStartup>=nextTelemetry){nextTelemetry=Time.realtimeSinceStartup+5;WriteStats(false);}
        }
        Rect SafeRect(){var r=Screen.safeArea;return new Rect(r.x,Screen.height-r.yMax,r.width,r.height);}
        float Scale()=>Mathf.Max(.5f,Mathf.Min(Screen.safeArea.width/1280f,Screen.safeArea.height/720f));
        Rect ViewRect()
        {
            var safe=SafeRect();float top=220*Scale();
            return new Rect(safe.x,safe.y+top,safe.width,Mathf.Max(2,safe.height-top));
        }
        void Pointer()
        {
            var mouse=new Vector2(Input.mousePosition.x,Screen.height-Input.mousePosition.y); Rect rect=ViewRect();
            if(Input.GetMouseButtonDown(0)&&rect.Contains(mouse)) { dragging=true; automaticStir=false; }
            if(Input.GetMouseButtonUp(0)) dragging=false;
            if(!dragging) return;
            Ray ray=waterCamera.ViewportPointToRay(new Vector3((mouse.x-rect.x)/rect.width,1-(mouse.y-rect.y)/rect.height,0));
            if(new Plane(Vector3.up,new Vector3(0,.65f,0)).Raycast(ray,out float distance))
            { var p=ray.GetPoint(distance); sphereTarget=new Vector3(Mathf.Clamp(p.x,-1.4f,1.4f),.65f,Mathf.Clamp(p.z,-1.4f,1.4f)); }
        }
        void OnGUI()
        {
            GUI.depth=0;var oldColor=GUI.color;GUI.color=Color.white;
            GUI.DrawTexture(new Rect(0,0,Screen.width,Screen.height),Texture2D.blackTexture,ScaleMode.StretchToFill,false);
            if(output!=null) GUI.DrawTexture(ViewRect(),output,ScaleMode.StretchToFill,false); GUI.depth=0;
            Matrix4x4 old=GUI.matrix; float scale=Scale(); GUI.matrix=Matrix4x4.Scale(Vector3.one*scale);
            if(statusStyle==null)statusStyle=new GUIStyle(GUI.skin.label){wordWrap=false};
            var safe=SafeRect();GUILayout.BeginArea(new Rect(safe.x/scale+8,safe.y/scale+6,safe.width/scale-16,208),GUI.skin.box);
            GUILayout.Label((solverMethod==MlsMpmFluid.SolverMethod.MlsMpm?"MLS-MPM":"PB-MPM / "+positionIterations+" iterations")+" PROFILE | "+SystemInfo.graphicsDeviceName+" | "+SystemInfo.graphicsDeviceType+" | "+(Debug.isDebugBuild?"Development":"Release"),statusStyle,GUILayout.Height(20));
            GUILayout.Label(string.Format("{0:N0} particles | grid {1}^3 | {2} substeps | {3}x{4} {5} | frame {6:F2}ms ({7:F1} FPS)",particles,grid,steps,width,height,quality,meanFrame*1000,meanFrame>0?1/meanFrame:0),statusStyle,GUILayout.Height(20));
            GUILayout.Label(string.Format("Physics 60Hz | sim {0:F2}s / wall {1:F2}s ({2:F2}x) | backlog {3:F3}s | ticks/frame {4}/{5}",simulated,wall,wall>0?simulated/wall:0,backlog,lastFrameTicks,maxCatchUpTicks),statusStyle,GUILayout.Height(20));
            GUILayout.BeginHorizontal();
            if(GUILayout.Button(paused?"Run":"Pause",GUILayout.Height(30))) paused=!paused;
            if(GUILayout.Button("Reset",GUILayout.Height(30))) ResetWater();
            if(GUILayout.Button(solverMethod==MlsMpmFluid.SolverMethod.MlsMpm?"Try PB-MPM":"Try MLS-MPM",GUILayout.Height(30)))
            {solverMethod=solverMethod==MlsMpmFluid.SolverMethod.MlsMpm?MlsMpmFluid.SolverMethod.PositionBasedMpm:MlsMpmFluid.SolverMethod.MlsMpm;ResetWater();}
            if(GUILayout.Button(automaticStir?"Auto stir ON":"Auto stir OFF",GUILayout.Height(30))) {automaticStir=!automaticStir;ResetWater();}
            if(GUILayout.Button(foam?"Foam ON":"Foam OFF",GUILayout.Height(30))) {foam=!foam;fluid.enableWhitewater=foam;}
            if(GUILayout.Button(rendering?"Rendering ON":"Rendering OFF",GUILayout.Height(30))) {rendering=!rendering;fluid.showParticles=rendering;}
            if(GUILayout.Button("Save stats",GUILayout.Height(30))) SaveStats();
            GUILayout.EndHorizontal(); GUILayout.BeginHorizontal();
            foreach(int count in new[]{8192,16384,32768,65536,131072}) if(GUILayout.Button((count/1024)+"K",GUILayout.Height(30)))
            {particles=count;grid=MlsMpmFluid.PresetGridResolution(count);ResetWater();}
            if(GUILayout.Button("Steps "+steps,GUILayout.Height(30))) {int[] s={1,2,3,4,6,8,12,16,24,32};int n=Array.IndexOf(s,steps)+1;steps=s[n>=s.Length?0:n];ResetWater();}
            if(GUILayout.Button("Catch-up "+maxCatchUpTicks+"t",GUILayout.Height(30))) {maxCatchUpTicks=maxCatchUpTicks==4?1:4;ResetWater();}
            if(solverMethod==MlsMpmFluid.SolverMethod.PositionBasedMpm)
            {
                if(GUILayout.Button(spatialSortInterval>0?"PB sort ON":"PB sort OFF",GUILayout.Height(30))) {spatialSortInterval=spatialSortInterval>0?0:1;fluid.spatialSortInterval=spatialSortInterval;}
                if(GUILayout.Button("PB iter "+positionIterations,GUILayout.Height(30))) {positionIterations=positionIterations>=8?1:positionIterations*2;ResetWater();}
            }
            if(GUILayout.Button("Quality "+quality,GUILayout.Height(30))) {quality=(MlsMpmFluid.SurfaceQuality)(((int)quality+1)%3);fluid.surfaceQuality=quality;}
            if(GUILayout.Button(adaptiveResolution?"Auto "+Mathf.RoundToInt(renderScale*100)+"%":height+"p",GUILayout.Height(30)))
            {if(adaptiveResolution){if(renderScale>.8f)renderScale=.75f;else if(renderScale>.6f)renderScale=.5f;else{adaptiveResolution=false;SetResolution(1280,720);}}else if(height==720)SetResolution(960,540);else{adaptiveResolution=true;renderScale=1;}UpdateResolution();ResetWater();}
            GUILayout.EndHorizontal();
            GUILayout.Label(error??saved??(backlog>.5?"BEHIND REAL TIME: reduce workload. No physics time dropped.":"Drag sphere below panel. Connect Unity Profiler via USB. Low substeps are experimental; Reset recovers faults."));
            GUILayout.EndArea(); GUI.matrix=old;GUI.color=oldColor;
        }
        [Serializable] sealed class Stats
        {
            public string utc,device,gpu,api; public int particles,grid,substeps,width,height,frames,targetFrameRate,displayRefresh;
            public bool foam,rendering,development; public double meanMs,p95Ms,wallSeconds,simulationSeconds,backlogSeconds;
            public int maxCatchUpTicks,submittedTicksInWindow,spatialSortInterval;
            public string solver;public int positionIterations;public float positionRelaxation,positionViscosity,gridVolumeBlend;
            public double meanTicksPerFrame,submittedTickHz,simulationToWallRatio;
            public bool adaptiveResolution;public float renderScale;public int screenWidth,screenHeight;
        }
        void SaveStats()=>WriteStats(true);
        void WriteStats(bool manual)
        {
            if(sampleCount==0) return; var sorted=new float[sampleCount];Array.Copy(samples,sorted,sampleCount);Array.Sort(sorted);double sum=0;foreach(float x in sorted)sum+=x;
            int windowTicks=0;for(int i=0;i<sampleCount;i++)windowTicks+=tickSamples[i];
            var s=new Stats{utc=DateTime.UtcNow.ToString("O"),device=SystemInfo.deviceModel,gpu=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),
                particles=particles,grid=grid,substeps=steps,width=width,height=height,frames=sampleCount,targetFrameRate=Application.targetFrameRate,displayRefresh=Screen.currentResolution.refreshRate,
                foam=foam,rendering=rendering,development=Debug.isDebugBuild,meanMs=sum/sampleCount,p95Ms=sorted[(int)Math.Ceiling(sampleCount*.95)-1],wallSeconds=wall,simulationSeconds=simulated,backlogSeconds=backlog,
                maxCatchUpTicks=maxCatchUpTicks,spatialSortInterval=solverMethod==MlsMpmFluid.SolverMethod.PositionBasedMpm?spatialSortInterval:0,submittedTicksInWindow=windowTicks,meanTicksPerFrame=(double)windowTicks/sampleCount,submittedTickHz=sum>0?windowTicks*1000/sum:0,simulationToWallRatio=wall>0?simulated/wall:0,
                solver=solverMethod.ToString(),positionIterations=positionIterations,positionRelaxation=fluid.positionRelaxation,positionViscosity=fluid.positionViscosity,gridVolumeBlend=fluid.gridVolumeBlend,
                adaptiveResolution=adaptiveResolution,renderScale=renderScale,screenWidth=Screen.width,screenHeight=Screen.height};
            string path=Path.Combine(Application.persistentDataPath,manual?"profile-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".json":"profile-live.json");
            File.WriteAllText(path,JsonUtility.ToJson(s,true));if(manual)saved="Saved: "+path;Debug.Log("MOBILE PROFILE: "+JsonUtility.ToJson(s)+" | "+path);
        }
        void OnApplicationPause(bool value) {suspended=value;skipDelta=true;}
        void OnDestroy()
        {
            if(output!=null){if(waterCamera!=null)waterCamera.targetTexture=null;output.Release();Destroy(output);}
            if(presentationCamera!=null)Destroy(presentationCamera.gameObject);
            if(!ownsSettings)return;QualitySettings.renderPipeline=previousPipeline;Application.targetFrameRate=previousFps;QualitySettings.vSyncCount=previousVsync;Screen.sleepTimeout=previousSleep;
        }
    }
}
