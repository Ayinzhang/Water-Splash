using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace WaterSplash.Tests
{
    public sealed class MlsMpmSurfaceTests
    {
        [UnityTearDown]
        public IEnumerator LeavePlayMode()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator RendererStoresLinearEyeDepthRatherThanDeviceDepth()
        {
            yield return new EnterPlayMode();
            Assert.That(GraphicsSettings.currentRenderPipeline, Is.InstanceOf<UniversalRenderPipelineAsset>());
            var root = new GameObject("Water surface regression");
            root.SetActive(false);
            var fluidObject = new GameObject("Test fluid");
            fluidObject.transform.SetParent(root.transform);
            fluidObject.transform.position = new Vector3(1000, 0, 1000);
            fluidObject.layer = 31;
            var fluid = fluidObject.AddComponent<MlsMpmFluid>();
            fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/MlsMpm.compute");
            fluid.particleCount = 512;
            fluid.showControls = false;
            var cameraObject = new GameObject("Surface test camera");
            cameraObject.transform.SetParent(root.transform);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.cullingMask = 1 << 31;
            camera.transform.position = new Vector3(1001.3f, 1.3f, 995);
            camera.transform.LookAt(new Vector3(1001.3f, 1.3f, 1002));
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 30;
            camera.allowMSAA = false;
            camera.allowHDR = false;
            var destination = new RenderTexture(128, 128, 24);
            Texture2D readback = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                root.SetActive(true);
                fluid.paused = true;
                Assert.That(fluid.IsInitialized, Is.True, fluid.Error);
                destination.Create();
                RenderPipeline.SubmitRenderRequest(camera,
                    new UniversalRenderPipeline.SingleCameraRequest { destination = destination });
                Assert.That(fluid.RenderingError, Is.Null);
                var surface = Shader.GetGlobalTexture("_FluidDepth") as RenderTexture;
                Assert.That(surface, Is.Not.Null);
                Assert.That(surface.graphicsFormat, Is.EqualTo(GraphicsFormat.R32G32_SFloat));
                readback = new Texture2D(surface.width, surface.height, TextureFormat.RGFloat, false, true);
                RenderTexture.active = surface;
                readback.ReadPixels(new Rect(0, 0, surface.width, surface.height), 0, 0);
                readback.Apply();
                int visible = 0;
                foreach (Vector2 data in readback.GetPixelData<Vector2>(0))
                {
                    if (data.x <= 0) continue;
                    visible++;
                    Assert.That(data.x, Is.InRange(4f, 12f), "Expected metres of eye depth, not a 0..1 device-depth sample.");
                }
                Assert.That(visible, Is.GreaterThan(100));
            }
            finally
            {
                RenderTexture.active = previous;
                destination.Release();
                Object.DestroyImmediate(destination);
                if (readback != null) Object.DestroyImmediate(readback);
                Object.DestroyImmediate(root);
            }
        }

        [TestCase("Hidden/WaterSplash/Surface Particles")]
        [TestCase("Hidden/WaterSplash/Continuous Surface")]
        public void SurfaceShadersAreAvailableAndCompile(string name)
        {
            Shader shader = Shader.Find(name);
            Assert.That(shader, Is.Not.Null);
            Assert.That(shader.isSupported, Is.True);
            Assert.That(ShaderUtil.GetShaderMessages(shader), Is.Empty);
        }

        [Test]
        public void DepthFilterPreservesEmptyPixelsAndDepthDiscontinuities()
        {
            var values = new Vector2[81];
            for (int y = 0; y < 9; y++)
            for (int x = 0; x < 9; x++) values[y * 9 + x] = new Vector2(x < 4 ? 2 : x == 4 ? 0 : 10, 0.4f);
            Vector2[] filtered = Filter(values, 9, 9);
            Assert.That(filtered[4 * 9 + 2].x, Is.EqualTo(2).Within(0.0001));
            Assert.That(filtered[4 * 9 + 4].x, Is.Zero);
            Assert.That(filtered[4 * 9 + 6].x, Is.EqualTo(10).Within(0.0001));
        }

        [Test]
        public void FilterRemovesParticleRipplesWithoutFlatteningBroadWaves()
        {
            const int width = 256, height = 17;
            var values = new Vector2[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float ripple = Mathf.Cos(2 * Mathf.PI * x / 4);
                values[y * width + x] = new Vector2(
                    2 + 0.15f * Mathf.Sin(2 * Mathf.PI * x / 128) + 0.03f * ripple,
                    0.4f + 0.15f * ripple);
            }
            Vector2[] filtered = Filter(values, width, height);
            Assert.That(Amplitude(filtered, width, 4, false), Is.LessThan(0.03 * 0.15),
                "At least 85% of particle-scale depth ripple must be removed.");
            Assert.That(Amplitude(filtered, width, 4, true), Is.LessThan(0.15 * 0.15),
                "Unfiltered thickness must not reintroduce the particle pattern.");
            Assert.That(Amplitude(filtered, width, 128, false), Is.InRange(0.15 * 0.8, 0.15 * 1.05),
                "The broad fluid wave must remain.");
        }

        [Test]
        public void FilterFootprintTracksProjectionAndDistance()
        {
            const int width = 256, height = 9;
            var near = new Vector2[width * height];
            var far = new Vector2[near.Length];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float ripple = 0.03f * Mathf.Cos(2 * Mathf.PI * x / 16);
                near[y * width + x] = new Vector2(2 + ripple, 0.4f);
                far[y * width + x] = new Vector2(8 + ripple, 0.4f);
            }
            Vector2[] nearFiltered = Filter(near, width, height);
            Vector2[] farFiltered = Filter(far, width, height);
            Vector2[] zoomed = Filter(far, width, height, 640);
            float nearAmplitude = Amplitude(nearFiltered, width, 16, false);
            Assert.That(Amplitude(farFiltered, width, 16, false), Is.GreaterThan(nearAmplitude * 3));
            Assert.That(Amplitude(zoomed, width, 16, false), Is.EqualTo(nearAmplitude).Within(0.001));
        }

        [Test]
        public void FilterDoesNotMixThicknessAcrossSeparateDepthLayers()
        {
            const int width = 33, height = 9;
            var values = new Vector2[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                values[y * width + x] = x < 16 ? new Vector2(2, 0.2f) : new Vector2(10, 1);
            Vector2[] filtered = Filter(values, width, height);
            Assert.That(filtered[4 * width + 14].x, Is.EqualTo(2).Within(0.0001));
            Assert.That(filtered[4 * width + 14].y, Is.EqualTo(0.2).Within(0.0001));
            Assert.That(filtered[4 * width + 18].x, Is.EqualTo(10).Within(0.0001));
            Assert.That(filtered[4 * width + 18].y, Is.EqualTo(1).Within(0.0001));
        }

        static float Amplitude(Vector2[] values, int width, int period, bool thickness)
        {
            double sine = 0, cosine = 0;
            int row = values.Length / width / 2;
            for (int x = 64; x < 192; x++)
            {
                float value = thickness ? values[row * width + x].y : values[row * width + x].x;
                sine += value * System.Math.Sin(2 * System.Math.PI * x / period);
                cosine += value * System.Math.Cos(2 * System.Math.PI * x / period);
            }
            return (float)(2 * System.Math.Sqrt(sine * sine + cosine * cosine) / 128);
        }

        static Vector2[] Filter(Vector2[] values, int width, int height, float projectionScale = 160)
        {
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R32G32_SFloat, FormatUsage.Render))
                Assert.Ignore("RG32 float rendering is required.");
            var source = new Texture2D(width, height, TextureFormat.RGFloat, false, true);
            var horizontal = new RenderTexture(width, height, 0, RenderTextureFormat.RGFloat);
            var destination = new RenderTexture(width, height, 0, RenderTextureFormat.RGFloat);
            var readback = new Texture2D(width, height, TextureFormat.RGFloat, false, true);
            var material = new Material(Shader.Find("Hidden/WaterSplash/Continuous Surface"));
            var commands = new CommandBuffer();
            RenderTexture previous = RenderTexture.active;
            try
            {
                source.SetPixelData(values, 0);
                source.filterMode = FilterMode.Point;
                source.wrapMode = TextureWrapMode.Clamp;
                source.Apply();
                horizontal.Create();
                destination.Create();
                commands.SetGlobalVector("_FluidBufferSize", new Vector4(width, height, 1f / width, 1f / height));
                commands.SetGlobalVector("_FluidProjectionScale", new Vector4(projectionScale, projectionScale, 1, 0));
                commands.SetGlobalFloat("_FluidRadius", 0.086f);
                commands.SetGlobalFloat("_FluidDepthRange", 0.086f * 4);
                commands.SetGlobalFloat("_FluidFirstFilter", 0);
                for (int i = 0; i < 3; i++)
                {
                    commands.SetGlobalFloat("_FluidFilterScale", 2.5f * Mathf.Pow(0.75f, i));
                    commands.SetGlobalTexture("_FluidSource", i == 0 ? (Texture)source : destination);
                    commands.SetGlobalVector("_FluidBlurDirection", Vector2.right);
                    commands.SetRenderTarget(horizontal);
                    commands.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
                    commands.SetGlobalTexture("_FluidSource", horizontal);
                    commands.SetGlobalVector("_FluidBlurDirection", Vector2.up);
                    commands.SetRenderTarget(destination);
                    commands.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
                }
                Graphics.ExecuteCommandBuffer(commands);
                RenderTexture.active = destination;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readback.Apply();
                return readback.GetPixelData<Vector2>(0).ToArray();
            }
            finally
            {
                RenderTexture.active = previous;
                commands.Release();
                horizontal.Release();
                destination.Release();
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(horizontal);
                Object.DestroyImmediate(destination);
                Object.DestroyImmediate(readback);
                Object.DestroyImmediate(material);
            }
        }
    }
}
