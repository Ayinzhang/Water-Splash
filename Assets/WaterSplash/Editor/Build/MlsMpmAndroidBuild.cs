using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace WaterSplash.Editor
{
    public static class MlsMpmAndroidBuild
    {
        [MenuItem("Water Splash/Android/Build Development APK To Downloads")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer)
                throw new InvalidOperationException("Stop Play Mode and wait for any current build.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            string downloads = Environment.GetEnvironmentVariable("WATER_SPLASH_DOWNLOADS");
            if (string.IsNullOrEmpty(downloads))
                downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);

            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.development = true;
            EditorUserBuildSettings.connectProfiler = true;
            EditorUserBuildSettings.allowDebugging = false;
            EditorUserBuildSettings.buildWithDeepProfilingSupport = false;
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(BottleVoyageScene.ScenePath, true) };

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { BottleVoyageScene.ScenePath },
                locationPathName = Path.Combine(downloads, "1.apk"),
                target = BuildTarget.Android,
                options = BuildOptions.Development | BuildOptions.ConnectWithProfiler
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Android build " + report.summary.result);
            Debug.Log("Bottle Voyage APK: " + report.summary.outputPath);
        }
    }
}
