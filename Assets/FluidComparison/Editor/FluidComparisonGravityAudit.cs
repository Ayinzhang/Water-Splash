using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    public static class FluidComparisonGravityAudit
    {
        [Serializable] sealed class Point { public double seconds, meanWorldY; }
        [Serializable] sealed class Case { public string mode; public double measuredAcceleration; public List<Point> samples = new List<Point>(); }
        [Serializable] sealed class Report { public string gpu; public List<Case> cases = new List<Case>(); }
        static FluidComparisonController controller; static Report report; static int index, lastFrame;
        [MenuItem("Water Splash/Validation/Calibrate Freefall Gravity")]
        public static void Start()
        {
            controller = Object.FindObjectOfType<FluidComparisonController>();
            if (!EditorApplication.isPlaying || controller == null) throw new InvalidOperationException("Play comparison first.");
            report = new Report { gpu = SystemInfo.graphicsDeviceName }; index = 0; Begin();
            EditorApplication.update -= Advance; EditorApplication.update += Advance;
        }
        static void Begin()
        {
            controller.paused = true; controller.automaticStir = false; controller.particleCount = 32768; controller.storageGrid = 48; controller.mlsSubsteps = 8;
            controller.whitewater = false; controller.gravity = -9.81f;
            controller.SelectView(index == 0 ? FluidComparisonController.ViewMode.Zibra : FluidComparisonController.ViewMode.MlsMpm);
            report.cases.Add(new Case { mode = index == 0 ? "Zibra" : "MlsMpm" }); lastFrame = -1;
        }
        static void Advance()
        {
            if (!EditorApplication.isPlaying || controller == null) { EditorApplication.update -= Advance; return; }
            if (controller.IsRebuilding || lastFrame == Time.frameCount || controller.SimulatedSeconds < 2 * MlsMpmFluid.FixedTick - 1e-6) return;
            lastFrame = Time.frameCount; var c = report.cases[index]; double mean;
            if (index == 0)
            {
                var p = new Vector4[controller.particleCount]; controller.zibra.PositionMass.GetData(p); mean = 0;
                foreach (var v in p) mean += v.y * 4 / controller.zibra.GridSize.y; mean /= p.Length;
            }
            else { var s = controller.mls.CaptureSnapshot(); mean = (s.center * controller.mls.CellSize + controller.mls.GridOrigin).y; }
            if (c.samples.Count > 0 && Math.Abs(c.samples[c.samples.Count - 1].seconds - controller.SimulatedSeconds) < 1e-6) return;
            c.samples.Add(new Point { seconds = controller.SimulatedSeconds, meanWorldY = mean });
            if (controller.SimulatedSeconds < .099) { controller.StepOnce(); return; }
            // Consecutive mean positions: acceleration from second finite differences.
            double acceleration = 0;
            for (int i = 2; i < c.samples.Count; i++) acceleration += (c.samples[i].meanWorldY - 2 * c.samples[i-1].meanWorldY + c.samples[i-2].meanWorldY) / (MlsMpmFluid.FixedTick * MlsMpmFluid.FixedTick);
            c.measuredAcceleration = acceleration / (c.samples.Count - 2);
            if (++index < 2) { Begin(); return; }
            Directory.CreateDirectory("BenchmarkResults"); File.WriteAllText("BenchmarkResults/freefall-calibration.json", JsonUtility.ToJson(report,true));
            EditorApplication.update -= Advance;
        }
    }
}
