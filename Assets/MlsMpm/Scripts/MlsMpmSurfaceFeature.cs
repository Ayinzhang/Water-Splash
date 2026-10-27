using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WaterSplash
{
    public sealed class MlsMpmSurfaceFeature : ScriptableRendererFeature
    {
        public Shader particleShader;
        public Shader surfaceShader;
        [ColorUsage(false)] public Color waterColor = new Color(0.01f, 0.15f, 0.32f);
        public Vector3 absorption = new Vector3(2.5f, 0.7f, 0.3f);
        [Range(0, 0.2f)] public float refraction = 0.08f;
        [Range(0.5f, 4)] public float surfaceSmoothing = 2.5f;

        Material particleMaterial;
        Material surfaceMaterial;
        SurfacePass pass;
        readonly List<MlsMpmFluid> visible = new List<MlsMpmFluid>();

        public override void Create()
        {
            pass?.Dispose();
            CoreUtils.Destroy(particleMaterial);
            CoreUtils.Destroy(surfaceMaterial);
            particleMaterial = particleShader == null ? null : CoreUtils.CreateEngineMaterial(particleShader);
            surfaceMaterial = surfaceShader == null ? null : CoreUtils.CreateEngineMaterial(surfaceShader);
            pass = new SurfacePass(this) { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            if (camera.cameraType != CameraType.Game || renderingData.cameraData.renderType != CameraRenderType.Base) return;
            visible.Clear();
            foreach (MlsMpmFluid fluid in MlsMpmFluid.ActiveInstances)
                if (fluid != null && fluid.IsInitialized && fluid.showParticles && fluid.renderSurface &&
                    (camera.cullingMask & (1 << fluid.gameObject.layer)) != 0)
                    visible.Add(fluid);
            if (visible.Count == 0) return;
            string error = null;
            if (particleMaterial == null || surfaceMaterial == null || !particleShader.isSupported || !surfaceShader.isSupported)
                error = "Assign supported particle and surface shaders on the MLS-MPM renderer feature.";
            else if (renderingData.cameraData.xr.enabled)
                error = "This prototype supports non-XR game cameras only.";
            else if (!ValidOpticalValue(absorption.x) || !ValidOpticalValue(absorption.y) ||
                     !ValidOpticalValue(absorption.z) || !ValidOpticalValue(refraction) ||
                     !ValidOpticalValue(surfaceSmoothing) || surfaceSmoothing <= 0)
                error = "Water absorption/refraction must be finite and nonnegative; surface smoothing must be finite and positive.";
            else if (!SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, FormatUsage.Render) ||
                     !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32_SFloat, FormatUsage.Render) ||
                     !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16_SFloat, FormatUsage.Render) ||
                     !SystemInfo.IsFormatSupported(GraphicsFormat.R16_SFloat, FormatUsage.Render) ||
                     !SystemInfo.IsFormatSupported(GraphicsFormat.R16_SFloat, FormatUsage.Blend))
                error = "Water needs R32/RG32/RG16 float rendering and R16 float additive blending.";
            foreach (MlsMpmFluid fluid in visible)
                if (!ValidOpticalValue(fluid.surfaceRadiusScale) || fluid.surfaceRadiusScale <= 0 ||
                    fluid.surfaceQuality < MlsMpmFluid.SurfaceQuality.Low ||
                    fluid.surfaceQuality > MlsMpmFluid.SurfaceQuality.High)
                    error = "Surface radius scale must be finite and positive, and water quality must be Low, Medium or High.";
            if (error != null)
            {
                foreach (MlsMpmFluid fluid in visible) fluid.ReportSurfaceError(error);
                return;
            }
            pass.ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color);
            renderer.EnqueuePass(pass);
        }

        static bool ValidOpticalValue(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0;

        protected override void Dispose(bool disposing)
        {
            pass?.Dispose();
            CoreUtils.Destroy(particleMaterial);
            CoreUtils.Destroy(surfaceMaterial);
        }

        sealed class SurfacePass : ScriptableRenderPass
        {
            readonly MlsMpmSurfaceFeature owner;
            readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
            readonly ProfilingSampler profiler = new ProfilingSampler("MLS-MPM/Continuous water");
            RTHandle rawDepth;
            RTHandle particleDepth;
            RTHandle smoothA;
            RTHandle smoothB;
            RTHandle thickness;
            RTHandle normals;
            RTHandle foam;
            RTHandle cameraColor;
            RTHandle cameraDepth;
            int width;
            int height;
            int iterations;
            float radius;

            public SurfacePass(MlsMpmSurfaceFeature feature) => owner = feature;

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                var quality = MlsMpmFluid.SurfaceQuality.Low;
                radius = 0;
                foreach (MlsMpmFluid fluid in owner.visible)
                {
                    if (fluid.surfaceQuality > quality) quality = fluid.surfaceQuality;
                    radius = Mathf.Max(radius, fluid.SurfaceParticleRadius);
                }
                int divisor = quality == MlsMpmFluid.SurfaceQuality.High ? 1 : 2;
                iterations = quality == MlsMpmFluid.SurfaceQuality.Low ? 2 :
                    quality == MlsMpmFluid.SurfaceQuality.Medium ? 3 : 4;
                RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
                width = Mathf.Max(1, (descriptor.width + divisor - 1) / divisor);
                height = Mathf.Max(1, (descriptor.height + divisor - 1) / divisor);
                descriptor.width = width;
                descriptor.height = height;
                descriptor.msaaSamples = 1;
                descriptor.bindMS = false;
                descriptor.useMipMap = false;
                descriptor.autoGenerateMips = false;
                descriptor.useDynamicScale = false;
                descriptor.graphicsFormat = GraphicsFormat.R32_SFloat;
                descriptor.depthBufferBits = 0;
                RenderingUtils.ReAllocateIfNeeded(ref rawDepth, descriptor, FilterMode.Point, TextureWrapMode.Clamp,
                    name: "_MpmRawDepth");
                // RTHandles with depth bits are depth-only in URP 14, regardless of graphicsFormat.
                descriptor.graphicsFormat = GraphicsFormat.None;
                descriptor.depthBufferBits = 32;
                RenderingUtils.ReAllocateIfNeeded(ref particleDepth, descriptor, FilterMode.Point, TextureWrapMode.Clamp,
                    name: "_MpmParticleDepth");
                descriptor.depthBufferBits = 0;
                descriptor.graphicsFormat = GraphicsFormat.R32G32_SFloat;
                RenderingUtils.ReAllocateIfNeeded(ref smoothA, descriptor, FilterMode.Point, TextureWrapMode.Clamp,
                    name: "_MpmSmoothA");
                RenderingUtils.ReAllocateIfNeeded(ref smoothB, descriptor, FilterMode.Point, TextureWrapMode.Clamp,
                    name: "_MpmSmoothB");
                descriptor.graphicsFormat = GraphicsFormat.R16_SFloat;
                RenderingUtils.ReAllocateIfNeeded(ref thickness, descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp,
                    name: "_MpmThickness");
                descriptor.graphicsFormat = GraphicsFormat.R16G16_SFloat;
                RenderingUtils.ReAllocateIfNeeded(ref normals, descriptor, FilterMode.Point, TextureWrapMode.Clamp,
                    name: "_MpmNormals");
                descriptor.graphicsFormat = GraphicsFormat.R16_SFloat;
                RenderingUtils.ReAllocateIfNeeded(ref foam, descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp,
                    name: "_MpmFoam");
                cameraColor = renderingData.cameraData.renderer.cameraColorTargetHandle;
                cameraDepth = renderingData.cameraData.renderer.cameraDepthTargetHandle;
                ConfigureTarget(cameraColor, cameraDepth);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                CommandBuffer cmd = CommandBufferPool.Get();
                try
                {
                    using (new ProfilingScope(cmd, profiler))
                    {
                        cmd.SetGlobalVector("_FluidBufferSize", new Vector4(width, height, 1f / width, 1f / height));
                        Matrix4x4 projection = renderingData.cameraData.GetGPUProjectionMatrix();
                        cmd.SetGlobalMatrix("_FluidInverseProjection", projection.inverse);
                        cmd.SetGlobalVector("_FluidProjectionScale", new Vector4(
                            Mathf.Abs(projection.m00) * width * 0.5f, Mathf.Abs(projection.m11) * height * 0.5f,
                            renderingData.cameraData.camera.orthographic ? 0 : 1, 0));
                        cmd.SetGlobalFloat("_FluidRadius", radius);
                        cmd.SetGlobalFloat("_FluidDepthRange", Mathf.Max(radius * 4, 0.001f));
                        cmd.SetGlobalVector("_FluidAbsorption", new Vector4(
                            owner.absorption.x, owner.absorption.y, owner.absorption.z, 0));
                        cmd.SetGlobalColor("_FluidWaterColor", owner.waterColor);
                        cmd.SetGlobalFloat("_FluidRefraction", owner.refraction);

                        cmd.BeginSample("MLS-MPM/Surface depth");
                        CoreUtils.SetRenderTarget(cmd, rawDepth, particleDepth, ClearFlag.All, Color.clear);
                        DrawParticles(cmd, 0);
                        cmd.EndSample("MLS-MPM/Surface depth");
                        cmd.BeginSample("MLS-MPM/Surface thickness");
                        CoreUtils.SetRenderTarget(cmd, thickness, ClearFlag.Color, Color.clear);
                        DrawParticles(cmd, 1);
                        cmd.EndSample("MLS-MPM/Surface thickness");

                        cmd.BeginSample("MLS-MPM/Surface smoothing");
                        cmd.SetGlobalTexture("_FluidThickness", thickness.nameID);
                        RTHandle source = rawDepth;
                        for (int i = 0; i < iterations; i++)
                        {
                            cmd.SetGlobalFloat("_FluidFilterScale", owner.surfaceSmoothing * Mathf.Pow(0.75f, i));
                            Smooth(cmd, source, smoothA, Vector2.right, i == 0);
                            Smooth(cmd, smoothA, smoothB, Vector2.up, false);
                            source = smoothB;
                        }
                        cmd.EndSample("MLS-MPM/Surface smoothing");
                        cmd.SetGlobalTexture("_FluidDepth", source.nameID);
                        cmd.BeginSample("MLS-MPM/Surface normals");
                        CoreUtils.SetRenderTarget(cmd, normals);
                        cmd.DrawProcedural(Matrix4x4.identity, owner.surfaceMaterial, 2, MeshTopology.Triangles, 3, 1);
                        cmd.EndSample("MLS-MPM/Surface normals");
                        cmd.BeginSample("MLS-MPM/Foam coverage");
                        CoreUtils.SetRenderTarget(cmd, foam, ClearFlag.Color, Color.clear);
                        DrawWhitewater(cmd, 2, true);
                        cmd.SetGlobalTexture("_FluidFoam", foam.nameID);
                        cmd.EndSample("MLS-MPM/Foam coverage");
                        cmd.BeginSample("MLS-MPM/Surface shading");
                        cmd.SetGlobalTexture("_FluidNormals", normals.nameID);
                        CoreUtils.SetRenderTarget(cmd, cameraColor, cameraDepth);
                        cmd.DrawProcedural(Matrix4x4.identity, owner.surfaceMaterial, 1, MeshTopology.Triangles, 3, 1);
                        cmd.EndSample("MLS-MPM/Surface shading");
                        cmd.BeginSample("MLS-MPM/Spray rendering");
                        DrawWhitewater(cmd, 3, false);
                        cmd.EndSample("MLS-MPM/Spray rendering");
                    }
                    context.ExecuteCommandBuffer(cmd);
                    foreach (MlsMpmFluid fluid in owner.visible) fluid.MarkSurfaceRendered();
                }
                finally
                {
                    CommandBufferPool.Release(cmd);
                }
            }

            void DrawWhitewater(CommandBuffer cmd, int shaderPass, bool drawFoam)
            {
                foreach (MlsMpmFluid fluid in owner.visible)
                {
                    if (!fluid.enableWhitewater || fluid.WhitewaterBuffer == null) continue;
                    properties.Clear();
                    properties.SetBuffer("_Whitewater", fluid.WhitewaterBuffer);
                    properties.SetBuffer("_WhitewaterIndices", drawFoam ? fluid.FoamIndices : fluid.SprayIndices);
                    properties.SetVector("_Origin", fluid.GridOrigin);
                    properties.SetFloat("_CellSize", fluid.CellSize);
                    properties.SetFloat("_WhitewaterRadius", fluid.WhitewaterRadius);
                    properties.SetFloat("_WhitewaterDepthTolerance", fluid.SurfaceParticleRadius * 2.5f);
                    cmd.DrawProceduralIndirect(Matrix4x4.identity, owner.particleMaterial, shaderPass,
                        MeshTopology.Triangles, drawFoam ? fluid.FoamArguments : fluid.SprayArguments, 0, properties);
                }
            }

            void DrawParticles(CommandBuffer cmd, int shaderPass)
            {
                foreach (MlsMpmFluid fluid in owner.visible)
                {
                    properties.Clear();
                    properties.SetBuffer("_Particles", fluid.ParticleBuffer);
                    properties.SetVector("_Origin", fluid.GridOrigin);
                    properties.SetFloat("_CellSize", fluid.CellSize);
                    properties.SetFloat("_Radius", fluid.SurfaceParticleRadius);
                    // Correct the mean overlap of the enlarged rendering spheres at rest density.
                    properties.SetFloat("_ThicknessScale", 3f / (4 * Mathf.PI *
                        fluid.surfaceRadiusScale * fluid.surfaceRadiusScale * fluid.surfaceRadiusScale));
                    cmd.DrawProcedural(Matrix4x4.identity, owner.particleMaterial, shaderPass,
                        MeshTopology.Triangles, 6, fluid.RenderedParticleCount, properties);
                }
            }

            void Smooth(CommandBuffer cmd, RTHandle source, RTHandle destination, Vector2 direction, bool first)
            {
                cmd.SetGlobalTexture("_FluidSource", source.nameID);
                cmd.SetGlobalVector("_FluidBlurDirection", direction);
                cmd.SetGlobalFloat("_FluidFirstFilter", first ? 1 : 0);
                CoreUtils.SetRenderTarget(cmd, destination);
                cmd.DrawProcedural(Matrix4x4.identity, owner.surfaceMaterial, 0, MeshTopology.Triangles, 3, 1);
            }

            public void Dispose()
            {
                rawDepth?.Release();
                particleDepth?.Release();
                smoothA?.Release();
                smoothB?.Release();
                thickness?.Release();
                normals?.Release();
                foam?.Release();
            }
        }
    }
}
