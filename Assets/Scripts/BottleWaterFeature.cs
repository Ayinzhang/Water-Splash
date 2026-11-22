using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class BottleWaterFeature : ScriptableRendererFeature
{
    WaterPass pass;

    public override void Create() { pass?.Dispose(); pass = new() { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents }; }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (BottleFluid.Instance == null || BottleFluid.Instance.particles == null) return;
        pass.ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color);
        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing) => pass?.Dispose();

    class WaterPass : ScriptableRenderPass
    {
        class PassData { public WaterPass pass; public Camera camera; public TextureHandle color, depth; public TextureHandle[] t; }

        // raw depth, particle depth, smooth A, smooth B, thickness, normals, foam
        static readonly GraphicsFormat[] formats = { GraphicsFormat.R32_SFloat, GraphicsFormat.None, GraphicsFormat.R32G32_SFloat, GraphicsFormat.R32G32_SFloat, GraphicsFormat.R16_SFloat, GraphicsFormat.R16G16_SFloat, GraphicsFormat.R16G16_SFloat };
        readonly RTHandle[] handles = new RTHandle[7];
        BottleFluid fluid;
        Material material;
        int width, height;

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            var camera = frameData.Get<UniversalCameraData>();
            var resources = frameData.Get<UniversalResourceData>();
            fluid = BottleFluid.Instance; material = fluid.particleMaterial;
            var desc = camera.cameraTargetDescriptor;
            width = desc.width; height = desc.height;
            desc.msaaSamples = 1; desc.bindMS = false; desc.useMipMap = false; desc.useDynamicScale = false;
            using var builder = graph.AddUnsafePass<PassData>("Bottle Water", out var data);
            data.pass = this; data.camera = camera.camera; data.color = resources.activeColorTexture; data.depth = resources.activeDepthTexture; data.t = new TextureHandle[7];
            builder.UseTexture(data.color, AccessFlags.ReadWrite); builder.UseTexture(data.depth, AccessFlags.ReadWrite);
            for (int i = 0; i < 7; i++)
            {
                desc.graphicsFormat = formats[i]; desc.depthBufferBits = i == 1 ? 32 : 0;
                RenderingUtils.ReAllocateHandleIfNeeded(ref handles[i], desc, i == 4 || i == 6 ? FilterMode.Bilinear : FilterMode.Point, TextureWrapMode.Clamp, name: "_BottleWater" + i);
                data.t[i] = graph.ImportTexture(handles[i]);
                builder.UseTexture(data.t[i], AccessFlags.ReadWrite);
            }
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (PassData d, UnsafeGraphContext c) => d.pass.Execute(d, c.cmd));
        }

        void Execute(PassData d, UnsafeCommandBuffer cmd)
        {
            var t = d.t;
            if (!fluid.renderSurface) { cmd.SetRenderTarget(d.color, d.depth); cmd.SetViewport(d.camera.pixelRect); Particles(cmd, 5); return; }
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(d.camera.projectionMatrix, true);
            cmd.SetGlobalVector("_FluidBufferSize", new Vector4(width, height, 1f / width, 1f / height));
            cmd.SetGlobalMatrix("_FluidInverseProjection", projection.inverse);
            cmd.SetGlobalVector("_FluidProjectionScale", new Vector2(Mathf.Abs(projection.m00) * width * .5f, Mathf.Abs(projection.m11) * height * .5f));
            // Sphere depth and thickness
            cmd.SetRenderTarget(t[0], t[1]); cmd.ClearRenderTarget(true, true, Color.clear); Particles(cmd, 0);
            Target(cmd, t[4], true); Particles(cmd, 1);
            cmd.SetGlobalTexture("_FluidThickness", t[4]);
            // Separable bilateral smoothing
            for (int i = 0; i < 4; i++)
            {
                cmd.SetGlobalFloat("_FluidFilterScale", 2.5f * Mathf.Pow(.75f, i));
                Smooth(cmd, i == 0 ? t[0] : t[3], t[2], Vector2.right, i == 0);
                Smooth(cmd, t[2], t[3], Vector2.up, false);
            }
            cmd.SetGlobalTexture("_FluidDepth", t[3]);
            Target(cmd, t[5], false); Fullscreen(cmd, 4);
            cmd.SetGlobalTexture("_FluidNormals", t[5]);
            Target(cmd, t[6], true); Whitewater(cmd, 6, fluid.foamIndices, fluid.foamArgs);
            cmd.SetGlobalTexture("_FluidFoam", t[6]);
            // Composite, then spray on top
            cmd.SetRenderTarget(d.color, d.depth); cmd.SetViewport(d.camera.pixelRect);
            Fullscreen(cmd, 3);
            Whitewater(cmd, 7, fluid.sprayIndices, fluid.sprayArgs);
        }

        void Particles(UnsafeCommandBuffer cmd, int shaderPass) => cmd.DrawProcedural(Matrix4x4.identity, material, shaderPass, MeshTopology.Triangles, 6, fluid.particleCount);
        void Fullscreen(UnsafeCommandBuffer cmd, int shaderPass) => cmd.DrawProcedural(Matrix4x4.identity, material, shaderPass, MeshTopology.Triangles, 3, 1);

        void Whitewater(UnsafeCommandBuffer cmd, int shaderPass, ComputeBuffer indices, ComputeBuffer args)
        {
            cmd.SetGlobalBuffer("_WhitewaterIndices", indices);
            cmd.DrawProceduralIndirect(Matrix4x4.identity, material, shaderPass, MeshTopology.Triangles, args);
        }

        void Smooth(UnsafeCommandBuffer cmd, TextureHandle source, TextureHandle target, Vector2 direction, bool first)
        {
            cmd.SetGlobalTexture("_FluidSource", source);
            cmd.SetGlobalVector("_FluidBlurDirection", direction);
            cmd.SetGlobalFloat("_FluidFirstFilter", first ? 1 : 0);
            Target(cmd, target, false); Fullscreen(cmd, 2);
        }

        static void Target(UnsafeCommandBuffer cmd, TextureHandle target, bool clear) { cmd.SetRenderTarget(target); if (clear) cmd.ClearRenderTarget(false, true, Color.clear); }

        public void Dispose() { foreach (var h in handles) h?.Release(); }
    }
}
