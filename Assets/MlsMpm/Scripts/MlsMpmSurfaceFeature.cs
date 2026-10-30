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
        public bool continuousFoam = true;
        [Tooltip("Advected cellular foam detail and translucent, velocity-stretched spray. Does not change simulation.")]
        public bool detailedWhitewater;
        [Tooltip("Conservatively scissor water passes to the projected simulation bounds. Buffer resolution and filters stay unchanged.")]
        public bool cropToFluidBounds;

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
            else if (detailedWhitewater && !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16_SFloat, FormatUsage.Blend))
                error = "Detailed whitewater needs RG16 float additive blending.";
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

        public static RectInt CalculateSurfaceScissor(Camera camera, Bounds bounds, int width, int height)
        {
            if (camera == null || width < 1 || height < 1)
                throw new System.ArgumentException("A camera and positive render-target dimensions are required.");
            Vector3 minimum = bounds.min, maximum = bounds.max;
            Vector2 lower = Vector2.one * float.PositiveInfinity, upper = Vector2.one * float.NegativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = camera.WorldToViewportPoint(new Vector3(
                    (i & 1) == 0 ? minimum.x : maximum.x,
                    (i & 2) == 0 ? minimum.y : maximum.y,
                    (i & 4) == 0 ? minimum.z : maximum.z));
                // A near-plane crossing can project beyond the corner envelope.
                if (p.z <= camera.nearClipPlane) return new RectInt(0, 0, width, height);
                lower = Vector2.Min(lower, p);
                upper = Vector2.Max(upper, p);
            }
            int x0 = Mathf.Clamp(Mathf.FloorToInt(lower.x * width) - 4, 0, width);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(lower.y * height) - 4, 0, height);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(upper.x * width) + 4, 0, width);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(upper.y * height) + 4, 0, height);
            return new RectInt(x0, y0, Mathf.Max(0, x1 - x0), Mathf.Max(0, y1 - y0));
        }

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
            static readonly UnityEngine.Profiling.CustomSampler[] renderStages = {
                UnityEngine.Profiling.CustomSampler.Create("MPM Render/Depth", true),
                UnityEngine.Profiling.CustomSampler.Create("MPM Render/Thickness", true),
                UnityEngine.Profiling.CustomSampler.Create("MPM Render/Smoothing", true),
                UnityEngine.Profiling.CustomSampler.Create("MPM Render/Normals", true),
                UnityEngine.Profiling.CustomSampler.Create("MPM Render/Foam", true),
                UnityEngine.Profiling.CustomSampler.Create("MPM Render/Shading", true),
                UnityEngine.Profiling.CustomSampler.Create("MPM Render/Spray", true)
            };
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
            RectInt scissor;
            Rect cameraScissor;
            bool cropped;

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
                scissor = new RectInt(0, 0, width, height);
                if (owner.cropToFluidBounds)
                {
                    Bounds bounds = owner.visible[0].SurfaceBounds;
                    for (int i = 1; i < owner.visible.Count; i++) bounds.Encapsulate(owner.visible[i].SurfaceBounds);
                    scissor = CalculateSurfaceScissor(renderingData.cameraData.camera, bounds, width, height);
                }
                cropped = !scissor.Equals(new RectInt(0, 0, width, height));
                float scaleX = (float)descriptor.width / width, scaleY = (float)descriptor.height / height;
                cameraScissor = Rect.MinMaxRect(Mathf.Floor(scissor.xMin * scaleX), Mathf.Floor(scissor.yMin * scaleY),
                    Mathf.Ceil(scissor.xMax * scaleX), Mathf.Ceil(scissor.yMax * scaleY));
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
                descriptor.graphicsFormat = owner.detailedWhitewater ? GraphicsFormat.R16G16_SFloat : GraphicsFormat.R16_SFloat;
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
                    if (scissor.width == 0 || scissor.height == 0)
                    {
                        foreach (MlsMpmFluid fluid in owner.visible) fluid.MarkSurfaceRendered();
                        return;
                    }
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
                        cmd.SetGlobalFloat("_ContinuousFoam", owner.continuousFoam ? 1 : 0);
                        cmd.SetGlobalFloat("_DetailedWhitewater", owner.detailedWhitewater ? 1 : 0);

                        cmd.BeginSample(renderStages[0]);
                        cmd.DisableScissorRect();
                        CoreUtils.SetRenderTarget(cmd, rawDepth, particleDepth, ClearFlag.All, Color.clear);
                        if (cropped) cmd.EnableScissorRect(new Rect(scissor.x, scissor.y, scissor.width, scissor.height));
                        DrawParticles(cmd, 0);
                        cmd.EndSample(renderStages[0]);
                        cmd.BeginSample(renderStages[1]);
                        SetBufferTarget(cmd, thickness, ClearFlag.Color);
                        DrawParticles(cmd, 1);
                        cmd.EndSample(renderStages[1]);

                        cmd.BeginSample(renderStages[2]);
                        cmd.SetGlobalTexture("_FluidThickness", thickness.nameID);
                        RTHandle source = rawDepth;
                        for (int i = 0; i < iterations; i++)
                        {
                            cmd.SetGlobalFloat("_FluidFilterScale", owner.surfaceSmoothing * Mathf.Pow(0.75f, i));
                            Smooth(cmd, source, smoothA, Vector2.right, i == 0);
                            Smooth(cmd, smoothA, smoothB, Vector2.up, false);
                            source = smoothB;
                        }
                        cmd.EndSample(renderStages[2]);
                        cmd.SetGlobalTexture("_FluidDepth", source.nameID);
                        cmd.BeginSample(renderStages[3]);
                        SetBufferTarget(cmd, normals);
                        cmd.DrawProcedural(Matrix4x4.identity, owner.surfaceMaterial, 2, MeshTopology.Triangles, 3, 1);
                        cmd.EndSample(renderStages[3]);
                        cmd.BeginSample(renderStages[4]);
                        SetBufferTarget(cmd, foam, ClearFlag.Color);
                        DrawWhitewater(cmd, 2, true);
                        cmd.SetGlobalTexture("_FluidFoam", foam.nameID);
                        cmd.EndSample(renderStages[4]);
                        cmd.BeginSample(renderStages[5]);
                        cmd.SetGlobalTexture("_FluidNormals", normals.nameID);
                        cmd.DisableScissorRect();
                        CoreUtils.SetRenderTarget(cmd, cameraColor, cameraDepth);
                        if (cropped) cmd.EnableScissorRect(cameraScissor);
                        cmd.DrawProcedural(Matrix4x4.identity, owner.surfaceMaterial, 1, MeshTopology.Triangles, 3, 1);
                        cmd.EndSample(renderStages[5]);
                        cmd.BeginSample(renderStages[6]);
                        DrawWhitewater(cmd, 3, false);
                        cmd.EndSample(renderStages[6]);
                        cmd.DisableScissorRect();
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
                    properties.SetInt("_ParticlePlaneStride", fluid.ParticlePlaneStride);
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
                SetBufferTarget(cmd, destination);
                cmd.DrawProcedural(Matrix4x4.identity, owner.surfaceMaterial, 0, MeshTopology.Triangles, 3, 1);
            }

            void SetBufferTarget(CommandBuffer cmd, RTHandle target, ClearFlag clear = ClearFlag.None)
            {
                cmd.DisableScissorRect();
                // Clear outside the ROI too: filters must never sample last frame's contents.
                CoreUtils.SetRenderTarget(cmd, target, cropped ? ClearFlag.Color : clear, Color.clear);
                if (cropped) cmd.EnableScissorRect(new Rect(scissor.x, scissor.y, scissor.width, scissor.height));
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
