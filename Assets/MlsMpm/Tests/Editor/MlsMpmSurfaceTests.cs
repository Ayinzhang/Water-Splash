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
                var foamField = Shader.GetGlobalTexture("_FluidFoam") as RenderTexture;
                Assert.That(foamField, Is.Not.Null);
                Assert.That(foamField.graphicsFormat, Is.EqualTo(
                    Shader.GetGlobalFloat("_DetailedWhitewater") > .5f ?
                    GraphicsFormat.R16G16_SFloat : GraphicsFormat.R16_SFloat));
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
        public void DetailedWhitewaterIsOptInAndEnabledOnlyForTheBottleRenderer()
        {
            var feature = ScriptableObject.CreateInstance<MlsMpmSurfaceFeature>();
            try { Assert.That(feature.detailedWhitewater, Is.False); }
            finally { Object.DestroyImmediate(feature); }
            foreach (string path in new[] {
                "Assets/MlsMpm/BottleDemo/Settings/BottleRenderer.asset",
                "Assets/MlsMpm/MobileValidation/Settings/AndroidRenderer.asset"
            })
            {
                var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                Assert.That(renderer, Is.Not.Null, path);
                bool found = false;
                foreach (var rendererFeature in renderer.rendererFeatures)
                {
                    if (!(rendererFeature is MlsMpmSurfaceFeature water)) continue;
                    found = true;
                    Assert.That(water.detailedWhitewater, Is.EqualTo(path.Contains("BottleDemo")), path);
                }
                Assert.That(found, Is.True, path);
            }
        }

        [Test]
        public void DetailedFoamAddsHolesWithoutChangingSeedDensity()
        {
            Color[] original = RenderWhitewater(false, true, Vector3.zero);
            Color[] detailed = RenderWhitewater(true, true, Vector3.zero);
            int holes = 0, rims = 0, interior = 0;
            for (int i = 0; i < original.Length; i++)
            {
                Assert.That(detailed[i].r, Is.EqualTo(original[i].r).Within(.001));
                if (detailed[i].r < .1f) continue;
                interior++;
                float detail = detailed[i].g / detailed[i].r;
                if (detail < .1f) holes++;
                if (detail > .65f) rims++;
            }
            Assert.That(interior, Is.GreaterThan(100));
            Assert.That(holes, Is.GreaterThan(10), "Sparse foam must contain actual gaps, not just brighter noise.");
            Assert.That(rims, Is.GreaterThan(10));
        }

        [Test]
        public void FoamDetailMovesWithTheSeedAndOpensAsItAges()
        {
            const int size = 128, shift = 16;
            Color[] young = RenderWhitewater(true, true, Vector3.zero, .2f);
            Color[] moved = RenderWhitewater(true, true, new Vector3(.25f, 0, 0), .2f);
            Color[] old = RenderWhitewater(true, true, Vector3.zero, .8f);
            float youngDetail = 0, oldDetail = 0;
            int samples = 0;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size - shift; x++)
            {
                int i = y * size + x;
                if (young[i].r < .1f) continue;
                Assert.That(moved[i + shift].g, Is.EqualTo(young[i].g).Within(.004),
                    "Foam detail must translate with its seed rather than sample stationary world noise.");
                youngDetail += young[i].g / young[i].r;
                oldDetail += old[i].g / Mathf.Max(old[i].r, .0001f);
                samples++;
            }
            Assert.That(samples, Is.GreaterThan(100));
            Assert.That(oldDetail / samples, Is.LessThan(youngDetail / samples - .05f));
        }

        [Test]
        public void DetailedSprayIsTranslucentStretchedAndOccludedByWater()
        {
            Color[] original = RenderWhitewater(false, false, Vector3.zero);
            Color[] detailed = RenderWhitewater(true, false, Vector3.zero);
            Color[] submerged = RenderWhitewater(true, false, Vector3.zero, .2f, 1.5f);
            double originalEnergy = 0, detailedEnergy = 0, hiddenEnergy = 0;
            int minX = 128, maxX = -1, minY = 128, maxY = -1;
            for (int i = 0; i < original.Length; i++)
            {
                originalEnergy += original[i].r + original[i].g + original[i].b;
                detailedEnergy += detailed[i].r + detailed[i].g + detailed[i].b;
                hiddenEnergy += submerged[i].r + submerged[i].g + submerged[i].b;
                if (detailed[i].b < .003f) continue;
                minX = Mathf.Min(minX, i % 128); maxX = Mathf.Max(maxX, i % 128);
                minY = Mathf.Min(minY, i / 128); maxY = Mathf.Max(maxY, i / 128);
            }
            Assert.That(detailedEnergy, Is.GreaterThan(1));
            Assert.That(detailedEnergy, Is.LessThan(originalEnergy * .75), "Spray must no longer resemble opaque white beads.");
            Assert.That(maxX - minX, Is.GreaterThan((maxY - minY) * 2));
            Assert.That(hiddenEnergy, Is.LessThan(.001), "Spray behind the visible water surface must not show through it.");
        }

        static Color[] RenderWhitewater(bool detailed, bool foam, Vector3 offset, float age = .2f, float waterDepth = 2)
        {
            if (!SystemInfo.supportsComputeShaders ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16_SFloat, FormatUsage.Blend))
                Assert.Ignore("Structured buffers and RG16 float additive blending are required.");
            const int size = 128;
            var states = new ComputeBuffer(1, 48);
            var indices = new ComputeBuffer(1, sizeof(uint));
            var surface = new Texture2D(1, 1, TextureFormat.RFloat, false, true);
            var scene = new Texture2D(1, 1, TextureFormat.RFloat, false, true);
            var target = new RenderTexture(size, size, 24, foam ? RenderTextureFormat.RGHalf : RenderTextureFormat.ARGBFloat);
            var readback = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true);
            var material = new Material(Shader.Find("Hidden/WaterSplash/Surface Particles"));
            var commands = new CommandBuffer();
            RenderTexture previous = RenderTexture.active;
            Texture previousDepth = Shader.GetGlobalTexture("_CameraDepthTexture");
            Texture previousFluidDepth = Shader.GetGlobalTexture("_FluidDepth");
            Vector4 previousOrtho = Shader.GetGlobalVector("unity_OrthoParams");
            Vector4 previousProjection = Shader.GetGlobalVector("_ProjectionParams");
            Vector4 previousScreen = Shader.GetGlobalVector("_ScaledScreenParams");
            Vector4 previousSize = Shader.GetGlobalVector("_FluidBufferSize");
            float previousContinuous = Shader.GetGlobalFloat("_ContinuousFoam");
            float previousDetailed = Shader.GetGlobalFloat("_DetailedWhitewater");
            try
            {
                states.SetData(new[] { new MlsMpmFluid.WhitewaterState {
                    position = new Vector4(offset.x, offset.y, -2 + offset.z, 1 - age),
                    velocity = new Vector4(foam ? 0 : 4, 0, 0, 1),
                    normalType = new Vector4(0, 0, 1, foam ? 1 : 0)
                } });
                indices.SetData(new uint[] { 0 });
                surface.SetPixelData(new[] { waterDepth }, 0); surface.Apply();
                scene.SetPixelData(new[] { SystemInfo.usesReversedZBuffer ? 0f : 1f }, 0); scene.Apply();
                target.Create();
                material.SetBuffer("_Whitewater", states);
                material.SetBuffer("_WhitewaterIndices", indices);
                material.SetVector("_Origin", Vector3.zero);
                material.SetFloat("_CellSize", 1);
                material.SetFloat("_WhitewaterRadius", foam ? .16f : .22f);
                material.SetFloat("_WhitewaterDepthTolerance", .5f);
                commands.SetViewProjectionMatrices(Matrix4x4.identity,
                    GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-1, 1, -1, 1, .1f, 10), true));
                commands.SetGlobalVector("unity_OrthoParams", new Vector4(2, 2, 0, 1));
                commands.SetGlobalVector("_ProjectionParams", new Vector4(1, .1f, 10, .1f));
                commands.SetGlobalVector("_ScaledScreenParams", new Vector4(size, size, 1 + 1f / size, 1 + 1f / size));
                commands.SetGlobalVector("_FluidBufferSize", new Vector4(size, size, 1f / size, 1f / size));
                commands.SetGlobalFloat("_ContinuousFoam", 1);
                commands.SetGlobalFloat("_DetailedWhitewater", detailed ? 1 : 0);
                commands.SetGlobalTexture("_FluidDepth", surface);
                commands.SetGlobalTexture("_CameraDepthTexture", scene);
                commands.SetRenderTarget(target);
                commands.ClearRenderTarget(true, true, Color.clear);
                commands.DrawProcedural(Matrix4x4.identity, material, foam ? 2 : 3, MeshTopology.Triangles, 6, 1);
                Graphics.ExecuteCommandBuffer(commands);
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                readback.Apply();
                return readback.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                Shader.SetGlobalTexture("_CameraDepthTexture", previousDepth);
                Shader.SetGlobalTexture("_FluidDepth", previousFluidDepth);
                Shader.SetGlobalVector("unity_OrthoParams", previousOrtho);
                Shader.SetGlobalVector("_ProjectionParams", previousProjection);
                Shader.SetGlobalVector("_ScaledScreenParams", previousScreen);
                Shader.SetGlobalVector("_FluidBufferSize", previousSize);
                Shader.SetGlobalFloat("_ContinuousFoam", previousContinuous);
                Shader.SetGlobalFloat("_DetailedWhitewater", previousDetailed);
                states.Release(); indices.Release(); commands.Release(); target.Release();
                Object.DestroyImmediate(surface); Object.DestroyImmediate(scene);
                Object.DestroyImmediate(target); Object.DestroyImmediate(readback); Object.DestroyImmediate(material);
            }
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
