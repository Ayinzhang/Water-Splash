using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace WaterSplash
{
    // Screen-space fluid surface: particle depth/thickness -> bilateral smoothing -> normals -> shading,
    // plus foam and spray from the whitewater buffers.
    public sealed class MlsMpmSurfaceFeature : ScriptableRendererFeature
    {
        const int SmoothingIterations = 4;

        public Shader waterShader;
        [ColorUsage(false)] public Color waterColor = new Color(.01f, .15f, .32f);
        public Vector3 absorption = new Vector3(2.5f, .7f, .3f);
        [Range(0, .2f)] public float refraction = .08f;
        [Range(.5f, 4)] public float surfaceSmoothing = 2.5f;
        public bool continuousFoam = true;
        public bool detailedWhitewater = true;

        Material waterMaterial;
        SurfacePass pass;

        public override void Create()
        {
            pass?.Dispose();
            CoreUtils.Destroy(waterMaterial);
            waterMaterial = CoreUtils.CreateEngineMaterial(waterShader);
            pass = new SurfacePass(this) { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            MlsMpmFluid fluid = MlsMpmFluid.Active;
            if (renderingData.cameraData.camera.cameraType != CameraType.Game || renderingData.cameraData.renderType != CameraRenderType.Base ||
                fluid == null || !fluid.IsInitialized) return;
            pass.ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color);
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            pass?.Dispose();
            CoreUtils.Destroy(waterMaterial);
        }

        sealed class SurfacePass : ScriptableRenderPass
        {
            sealed class PassData
            {
                public SurfacePass pass;
                public Camera camera;
                public TextureHandle color, depth, raw, particleDepth, smoothA, smoothB, thickness, normals, foam;
            }

            readonly MlsMpmSurfaceFeature owner;
            readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
            readonly ProfilingSampler profiler = new ProfilingSampler("MLS-MPM/Continuous water");
            RTHandle rawDepth, particleDepth, smoothA, smoothB, thickness, normals, foam;
            MlsMpmFluid fluid;
            int width, height;

            public SurfacePass(MlsMpmSurfaceFeature owner) => this.owner = owner;

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                var resources = frameData.Get<UniversalResourceData>();
                fluid = MlsMpmFluid.Active;
                var desc = cameraData.cameraTargetDescriptor;
                width = desc.width; height = desc.height;
                if (!fluid.renderSurface)
                {
                    using var particles = graph.AddUnsafePass<PassData>("MLS-MPM Particles", out var p, profiler);
                    p.pass = this; p.camera = cameraData.camera;
                    p.color = resources.activeColorTexture; p.depth = resources.activeDepthTexture;
                    particles.UseTexture(p.color, AccessFlags.ReadWrite);
                    particles.UseTexture(p.depth, AccessFlags.ReadWrite);
                    particles.AllowPassCulling(false);
                    particles.SetRenderFunc(static (PassData d, UnsafeGraphContext c) => d.pass.Execute(d, c.cmd));
                    return;
                }
                desc.msaaSamples = 1; desc.bindMS = false; desc.useMipMap = false; desc.autoGenerateMips = false;
                desc.useDynamicScale = false; desc.depthBufferBits = 0;
                Allocate(ref rawDepth, desc, GraphicsFormat.R32_SFloat, FilterMode.Point, "_MpmRawDepth");
                desc.graphicsFormat = GraphicsFormat.None; desc.depthBufferBits = 32;
                RenderingUtils.ReAllocateHandleIfNeeded(ref particleDepth, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_MpmParticleDepth");
                desc.depthBufferBits = 0;
                Allocate(ref smoothA, desc, GraphicsFormat.R32G32_SFloat, FilterMode.Point, "_MpmSmoothA");
                Allocate(ref smoothB, desc, GraphicsFormat.R32G32_SFloat, FilterMode.Point, "_MpmSmoothB");
                Allocate(ref thickness, desc, GraphicsFormat.R16_SFloat, FilterMode.Bilinear, "_MpmThickness");
                Allocate(ref normals, desc, GraphicsFormat.R16G16_SFloat, FilterMode.Point, "_MpmNormals");
                Allocate(ref foam, desc, owner.detailedWhitewater ? GraphicsFormat.R16G16_SFloat : GraphicsFormat.R16_SFloat,
                    FilterMode.Bilinear, "_MpmFoam");

                using var builder = graph.AddUnsafePass<PassData>("MLS-MPM Continuous Water", out var data, profiler);
                data.pass = this; data.camera = cameraData.camera;
                data.color = resources.activeColorTexture; data.depth = resources.activeDepthTexture;
                data.raw = graph.ImportTexture(rawDepth); data.particleDepth = graph.ImportTexture(particleDepth);
                data.smoothA = graph.ImportTexture(smoothA); data.smoothB = graph.ImportTexture(smoothB);
                data.thickness = graph.ImportTexture(thickness); data.normals = graph.ImportTexture(normals);
                data.foam = graph.ImportTexture(foam);
                foreach (var texture in new[] { data.color, data.depth, data.raw, data.particleDepth, data.smoothA,
                    data.smoothB, data.thickness, data.normals, data.foam })
                    builder.UseTexture(texture, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData d, UnsafeGraphContext c) => d.pass.Execute(d, c.cmd));
            }

            static void Allocate(ref RTHandle handle, RenderTextureDescriptor desc, GraphicsFormat format, FilterMode filter, string name)
            {
                desc.graphicsFormat = format;
                RenderingUtils.ReAllocateHandleIfNeeded(ref handle, desc, filter, TextureWrapMode.Clamp, name: name);
            }

            void Execute(PassData d, UnsafeCommandBuffer cmd)
            {
                if (!fluid.renderSurface)
                {
                    cmd.SetRenderTarget(d.color, d.depth);
                    cmd.SetViewport(d.camera.pixelRect);
                    DrawParticles(cmd, 7);
                    return;
                }
                float radius = fluid.SurfaceParticleRadius;
                Matrix4x4 projection = GL.GetGPUProjectionMatrix(d.camera.projectionMatrix, true);
                cmd.SetGlobalVector("_FluidBufferSize", new Vector4(width, height, 1f / width, 1f / height));
                cmd.SetGlobalMatrix("_FluidInverseProjection", projection.inverse);
                cmd.SetGlobalVector("_FluidProjectionScale", new Vector4(Mathf.Abs(projection.m00) * width * .5f,
                    Mathf.Abs(projection.m11) * height * .5f, d.camera.orthographic ? 0 : 1, 0));
                cmd.SetGlobalFloat("_FluidRadius", radius);
                cmd.SetGlobalFloat("_FluidDepthRange", Mathf.Max(radius * 4, .001f));
                cmd.SetGlobalVector("_FluidAbsorption", owner.absorption);
                cmd.SetGlobalColor("_FluidWaterColor", owner.waterColor);
                cmd.SetGlobalFloat("_FluidRefraction", owner.refraction);
                cmd.SetGlobalFloat("_ContinuousFoam", owner.continuousFoam ? 1 : 0);
                cmd.SetGlobalFloat("_DetailedWhitewater", owner.detailedWhitewater ? 1 : 0);

                cmd.SetRenderTarget(d.raw, d.particleDepth);
                cmd.ClearRenderTarget(true, true, Color.clear);
                DrawParticles(cmd, 0);
                SetTarget(cmd, d.thickness, true);
                DrawParticles(cmd, 1);
                cmd.SetGlobalTexture("_FluidThickness", d.thickness);
                TextureHandle source = d.raw;
                for (int i = 0; i < SmoothingIterations; i++)
                {
                    cmd.SetGlobalFloat("_FluidFilterScale", owner.surfaceSmoothing * Mathf.Pow(.75f, i));
                    Smooth(cmd, source, d.smoothA, Vector2.right, i == 0);
                    Smooth(cmd, d.smoothA, d.smoothB, Vector2.up, false);
                    source = d.smoothB;
                }
                cmd.SetGlobalTexture("_FluidDepth", source);
                SetTarget(cmd, d.normals, false);
                cmd.DrawProcedural(Matrix4x4.identity, owner.waterMaterial, 6, MeshTopology.Triangles, 3, 1);
                SetTarget(cmd, d.foam, true);
                DrawWhitewater(cmd, 2, true);
                cmd.SetGlobalTexture("_FluidFoam", d.foam);
                cmd.SetGlobalTexture("_FluidNormals", d.normals);
                cmd.SetRenderTarget(d.color, d.depth);
                cmd.SetViewport(d.camera.pixelRect);
                cmd.DrawProcedural(Matrix4x4.identity, owner.waterMaterial, 5, MeshTopology.Triangles, 3, 1);
                DrawWhitewater(cmd, 3, false);
            }

            void DrawParticles(UnsafeCommandBuffer cmd, int shaderPass)
            {
                float scale = fluid.surfaceRadiusScale;
                properties.Clear();
                properties.SetBuffer("_Particles", fluid.Particles);
                properties.SetInt("_ParticlePlaneStride", fluid.particleCount);
                properties.SetVector("_Origin", fluid.GridOrigin);
                properties.SetFloat("_CellSize", fluid.CellSize);
                properties.SetFloat("_Radius", shaderPass == 7 ? fluid.DisplayRadius : fluid.SurfaceParticleRadius);
                // Correct the mean overlap of the enlarged rendering spheres at rest density.
                properties.SetFloat("_ThicknessScale", 3f / (4 * Mathf.PI * scale * scale * scale));
                cmd.DrawProcedural(Matrix4x4.identity, owner.waterMaterial, shaderPass, MeshTopology.Triangles, 6, fluid.particleCount, properties);
            }

            void DrawWhitewater(UnsafeCommandBuffer cmd, int shaderPass, bool foamPass)
            {
                if (!fluid.enableWhitewater) return;
                properties.Clear();
                properties.SetBuffer("_Whitewater", fluid.Whitewater);
                properties.SetBuffer("_WhitewaterIndices", foamPass ? fluid.FoamIndices : fluid.SprayIndices);
                properties.SetVector("_Origin", fluid.GridOrigin);
                properties.SetFloat("_CellSize", fluid.CellSize);
                properties.SetFloat("_WhitewaterRadius", fluid.WhitewaterRadius);
                properties.SetFloat("_WhitewaterDepthTolerance", fluid.SurfaceParticleRadius * 2.5f);
                cmd.DrawProceduralIndirect(Matrix4x4.identity, owner.waterMaterial, shaderPass, MeshTopology.Triangles,
                    foamPass ? fluid.FoamArguments : fluid.SprayArguments, 0, properties);
            }

            void Smooth(UnsafeCommandBuffer cmd, TextureHandle source, TextureHandle target, Vector2 direction, bool first)
            {
                cmd.SetGlobalTexture("_FluidSource", source);
                cmd.SetGlobalVector("_FluidBlurDirection", direction);
                cmd.SetGlobalFloat("_FluidFirstFilter", first ? 1 : 0);
                SetTarget(cmd, target, false);
                cmd.DrawProcedural(Matrix4x4.identity, owner.waterMaterial, 4, MeshTopology.Triangles, 3, 1);
            }

            static void SetTarget(UnsafeCommandBuffer cmd, TextureHandle target, bool clear)
            {
                cmd.SetRenderTarget(target);
                if (clear) cmd.ClearRenderTarget(false, true, Color.clear);
            }

            public void Dispose()
            {
                rawDepth?.Release(); particleDepth?.Release(); smoothA?.Release(); smoothB?.Release();
                thickness?.Release(); normals?.Release(); foam?.Release();
            }
        }
    }
}