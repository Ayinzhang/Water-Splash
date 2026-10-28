using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    public static class MlsMpmAndroidBuild
    {
        public const string Root="Assets/MlsMpm/MobileValidation";
        public const string ScenePath=Root+"/Scenes/AndroidProfile.unity";
        const string StatusPath="BenchmarkResults/android-build.json";
        [Serializable] sealed class Status
        {
            public string state,utc,apk,backup,scene=ScenePath,message; public long bytes; public double seconds;
            public bool development=true,connectProfiler=true,deepProfiling=false;
            public string architecture="ARM64 IL2CPP",graphics="Vulkan",package="com.watersplash.mpmprofile";
        }
        static Status status;
        [InitializeOnLoadMethod]
        static void ResumeQueued()
        {
            if(SessionState.GetBool("WaterSplash.AndroidBuildQueued",false))EditorApplication.delayCall+=Build;
        }
        [MenuItem("Water Splash/Android/Create Or Open Profile Scene")]
        public static void CreateScene()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("Stop Play Mode first.");
            if(UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty)throw new InvalidOperationException("Save current scene before creating Android scene.");
            if(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath)!=null)
            {EditorSceneManager.OpenScene(ScenePath);EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(ScenePath,true)};return;}
            Folder("Assets/MlsMpm","MobileValidation"); Folder(Root,"Scenes"); Folder(Root,"Settings");
            var source=AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/FluidComparison/Settings/ComparisonRenderer.asset");
            var renderer=Object.Instantiate(source);renderer.name="Android MPM Renderer";renderer.rendererFeatures.Clear();
            AssetDatabase.CreateAsset(renderer,Root+"/Settings/AndroidRenderer.asset");
            var feature=ScriptableObject.CreateInstance<MlsMpmSurfaceFeature>();feature.name="Mobile continuous water and foam";
            feature.particleShader=Shader.Find("Hidden/WaterSplash/Surface Particles");feature.surfaceShader=Shader.Find("Hidden/WaterSplash/Continuous Surface");feature.continuousFoam=true;
            AssetDatabase.AddObjectToAsset(feature,renderer);renderer.rendererFeatures.Add(feature);
            var data=new SerializedObject(renderer);var map=data.FindProperty("m_RendererFeatureMap");map.arraySize=1;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature,out string guid,out long local);map.GetArrayElementAtIndex(0).longValue=local;data.ApplyModifiedPropertiesWithoutUndo();renderer.SetDirty();
            EditorUtility.SetDirty(renderer);EditorUtility.SetDirty(feature);AssetDatabase.SaveAssets();
            var pipeline=Object.Instantiate(AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/FluidComparison/Settings/ComparisonPipeline.asset"));
            pipeline.name="Android MPM Pipeline";AssetDatabase.CreateAsset(pipeline,Root+"/Settings/AndroidPipeline.asset");
            var p=new SerializedObject(pipeline);var list=p.FindProperty("m_RendererDataList");list.arraySize=1;list.GetArrayElementAtIndex(0).objectReferenceValue=renderer;
            p.FindProperty("m_DefaultRendererIndex").intValue=0;p.ApplyModifiedPropertiesWithoutUndo();
            pipeline.msaaSampleCount=1;pipeline.renderScale=1;pipeline.supportsHDR=false;
            EditorUtility.SetDirty(pipeline);AssetDatabase.SaveAssets();
            var scene=EditorSceneManager.OpenScene("Assets/FluidComparison/Scenes/FluidComparison.unity");
            EditorSceneManager.SaveScene(scene,ScenePath,true);scene=EditorSceneManager.OpenScene(ScenePath);
            // Separate Android scene contains no comparison controller or native Zibra component.
            foreach(var root in scene.GetRootGameObjects()) if(root.name=="Single Pool Comparison"||root.name.StartsWith("Zibra (")||root.name=="Container Frame (visual only)") Object.DestroyImmediate(root);
            foreach(var component in Object.FindObjectsOfType<MonoBehaviour>(true))
                if(component!=null&&component.gameObject.scene==scene&&component.GetType().Namespace!=null&&component.GetType().Namespace.StartsWith("com.zibra"))Object.DestroyImmediate(component);
            var fluid=Object.FindObjectOfType<MlsMpmFluid>(true);fluid.gameObject.SetActive(false);fluid.name="Android MLS-MPM";
            fluid.externalControl=true;fluid.showControls=false;fluid.substeps=8;fluid.particleCount=32768;fluid.gridResolution=48;
            var camera=Object.FindObjectOfType<Camera>();camera.targetTexture=null;camera.allowHDR=false;camera.allowMSAA=false;camera.GetUniversalAdditionalCameraData().SetRenderer(0);
            var panel=new GameObject("Android Profile Controls").AddComponent<MlsMpmMobileValidation>();
            panel.fluid=fluid;panel.waterCamera=camera;panel.sphere=fluid.sphereObstacle;panel.pipeline=pipeline;panel.surfaceFeature=feature;
            panel.particles=32768;panel.grid=48;panel.steps=8;panel.quality=MlsMpmFluid.SurfaceQuality.High;
            foreach(var t in Object.FindObjectsOfType<Transform>(true))if(t.gameObject.scene==scene&&t.name=="Floor (visual only)")t.localScale=new Vector3(4,.2f,4);
            EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);AssetDatabase.SaveAssets();
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(ScenePath,true)};
            Selection.activeGameObject=panel.gameObject;Debug.Log("Android profile scene created: "+ScenePath);
        }
        [MenuItem("Water Splash/Android/Build Development APK To Downloads")]
        public static void QueueBuild()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("Stop Play Mode first.");
            if(BuildPipeline.isBuildingPlayer)throw new InvalidOperationException("Build already running.");
            CreateScene();
            string downloads=Environment.GetEnvironmentVariable("WATER_SPLASH_DOWNLOADS");
            if(string.IsNullOrEmpty(downloads))downloads=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
            Directory.CreateDirectory(downloads);
            status=new Status{state="queued",utc=DateTime.UtcNow.ToString("O"),apk=Path.Combine(downloads,"1.apk")};SaveStatus();
            SessionState.SetBool("WaterSplash.AndroidBuildQueued",true);
            EditorApplication.delayCall+=Build;
        }
        [MenuItem("Water Splash/Android/Execute Queued APK Build")]
        static void Build()
        {
            if(BuildPipeline.isBuildingPlayer)return;
            SessionState.SetBool("WaterSplash.AndroidBuildQueued",false);
            if(status==null&&File.Exists(StatusPath))status=JsonUtility.FromJson<Status>(File.ReadAllText(StatusPath));
            if(status==null)throw new InvalidOperationException("Queue the APK build first.");
            var previousDefault=GraphicsSettings.defaultRenderPipeline;
            var quality=new RenderPipelineAsset[QualitySettings.names.Length];for(int i=0;i<quality.Length;i++)quality[i]=QualitySettings.GetRenderPipelineAssetAt(i);
            DateTime start=DateTime.UtcNow;
            try
            {
                status.state="building";SaveStatus();
                if(!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android,BuildTarget.Android))throw new InvalidOperationException("Android build support is missing.");
                PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android,status.package);
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android,ScriptingImplementation.IL2CPP);
                PlayerSettings.Android.targetArchitectures=AndroidArchitecture.ARM64;
                PlayerSettings.Android.minSdkVersion=AndroidSdkVersions.AndroidApiLevel29;
                PlayerSettings.Android.targetSdkVersion=AndroidSdkVersions.AndroidApiLevelAuto;
                PlayerSettings.Android.useCustomKeystore=false;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android,false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,new[]{GraphicsDeviceType.Vulkan});
                PlayerSettings.defaultInterfaceOrientation=UIOrientation.LandscapeLeft;PlayerSettings.productName="Water Splash Profile";
                PlayerSettings.runInBackground=true;PlayerSettings.Android.optimizedFramePacing=false;
                EditorUserBuildSettings.buildAppBundle=false;EditorUserBuildSettings.development=true;
                EditorUserBuildSettings.connectProfiler=true;EditorUserBuildSettings.allowDebugging=false;EditorUserBuildSettings.buildWithDeepProfilingSupport=false;
                var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/Settings/AndroidPipeline.asset");
                GraphicsSettings.defaultRenderPipeline=pipeline;SetQualityPipelines(quality,pipeline);
                AssetDatabase.SaveAssets();
                if(File.Exists(status.apk))
                {status.backup=Path.Combine(Path.GetDirectoryName(status.apk),"1-before-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".apk");File.Copy(status.apk,status.backup);SaveStatus();}
                var build=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{ScenePath},locationPathName=status.apk,target=BuildTarget.Android,
                    options=BuildOptions.Development|BuildOptions.ConnectWithProfiler});
                status.state=build.summary.result==BuildResult.Succeeded?"succeeded":"failed";
                status.message=build.summary.result+" | errors "+build.summary.totalErrors+" | warnings "+build.summary.totalWarnings;
                if(File.Exists(status.apk))status.bytes=new FileInfo(status.apk).Length;
                foreach(var step in build.steps)foreach(var message in step.messages)if(message.type==LogType.Error||message.type==LogType.Exception)status.message+="\n"+message.content;
                Debug.Log("ANDROID BUILD: "+JsonUtility.ToJson(status));
            }
            catch(Exception e){status.state="failed";status.message=e.ToString();Debug.LogException(e);}
            finally
            {
                GraphicsSettings.defaultRenderPipeline=previousDefault;SetQualityPipelines(quality,null);
                AssetDatabase.SaveAssets();status.seconds=(DateTime.UtcNow-start).TotalSeconds;SaveStatus();
            }
        }
        static void SaveStatus(){Directory.CreateDirectory("BenchmarkResults");File.WriteAllText(StatusPath,JsonUtility.ToJson(status,true));}
        static void SetQualityPipelines(RenderPipelineAsset[] saved,RenderPipelineAsset replacement)
        {
            var settings=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
            var levels=settings.FindProperty("m_QualitySettings");
            for(int i=0;i<saved.Length;i++)levels.GetArrayElementAtIndex(i).FindPropertyRelative("customRenderPipeline").objectReferenceValue=replacement!=null?replacement:saved[i];
            settings.ApplyModifiedPropertiesWithoutUndo();
        }
        static void Folder(string parent,string name){if(!AssetDatabase.IsValidFolder(parent+"/"+name))AssetDatabase.CreateFolder(parent,name);}
    }
}
