using UnityEngine;
using UnityEngine.Rendering;

namespace WaterSplash
{
    // Weakly compressible MLS-MPM water on an 80 x 30 x 30 grid window around the bottle.
    public sealed class MlsMpmFluid : MonoBehaviour
    {
        public const float FixedTick = 1f / 60;
        static readonly string[] KernelNames = {
            "ClearGrid", "Transfer", "UpdateGrid", "UpdateParticles", "CacheBottleBoundary", "ClearWhitewater", "UpdateWhitewater" };

        public ComputeShader simulationShader;
        public Material particleMaterial;
        public int particleCount = 65536;
        public int gridResolution = 80;
        public float domainSize = 4;
        public int substeps = 8;
        public float soundSpeed = 6;
        public float pressureExponent = 3;
        public float viscosity = .15f;
        public Vector3 gravity = new Vector3(0, -9.81f, 0);
        public Transform sphereObstacle;
        public float sphereRadius = .12f;
        public bool renderSurface = true;
        public float surfaceRadiusScale = .95f;
        public bool enableWhitewater = true;
        public int whitewaterCapacity = 8192;
        public float whitewaterEmissionRate = 8;
        public float whitewaterLifetime = 2.5f;
        public float whitewaterSpeedThreshold = 1.2f;
        public float whitewaterDeformationThreshold = 4;
        public float whitewaterRadiusScale = .4f;

        public static MlsMpmFluid Active { get; private set; }
        public string Error { get; private set; }
        public bool IsInitialized => Particles != null;
        public float CellSize => domainSize / (gridResolution - 5);
        public Vector3 GridOrigin => transform.position - Vector3.one * (2 * CellSize);
        public float RestDensity { get; private set; }
        public float SurfaceParticleRadius => CellSize * surfaceRadiusScale / Mathf.Pow(RestDensity, 1f / 3);
        public float WhitewaterRadius => CellSize * whitewaterRadiusScale / Mathf.Pow(RestDensity, 1f / 3);
        public ComputeBuffer Particles { get; private set; }
        public ComputeBuffer Whitewater { get; private set; }
        public ComputeBuffer FoamIndices { get; private set; }
        public ComputeBuffer SprayIndices { get; private set; }
        public ComputeBuffer FoamArguments { get; private set; }
        public ComputeBuffer SprayArguments { get; private set; }

        readonly int[] kernels = new int[KernelNames.Length];
        ComputeShader solver;
        ComputeBuffer accumulation, grid, gridBoundary, diagnostics;
        CommandBuffer commands;
        MaterialPropertyBlock drawProperties;
        Vector3 previousSphere;
        float displayRadius, nextReadback;
        int cellCount, whitewaterStep, generation;
        bool readbackPending;

        void OnEnable() { Active = this; drawProperties = new MaterialPropertyBlock(); }
        void OnDisable() { if (Active == this) Active = null; Release(); }

        public void Initialize(BottleBoundary bottle)
        {
            Release();
            Error = null;
            float h = CellSize;
            Vector3[] fill = bottle.CreateInitialFill(particleCount, out float spacing);
            RestDensity = Mathf.Pow(h / spacing, 3);
            displayRadius = spacing * .34f;
            // Tight storage window: full X span, 30 x 30 cells around the bottle axis.
            Vector3 center = (bottle.center - GridOrigin) / h;
            var offset = new Vector3Int(0, Mathf.CeilToInt(center.y - 15), Mathf.CeilToInt(center.z - 15));
            var size = new Vector3Int(gridResolution, 30, 30);
            cellCount = size.x * size.y * size.z;

            var data = new Vector4[particleCount * 5];
            for (int i = 0; i < particleCount; i++)
            {
                Vector3 p = (fill[i] - GridOrigin) / h;
                data[i] = new Vector4(p.x, p.y, p.z, 1);
            }
            Particles = new ComputeBuffer(particleCount * 5, 16);
            Particles.SetData(data);
            accumulation = new ComputeBuffer(cellCount * 2, 4);
            grid = new ComputeBuffer(cellCount, 16);
            gridBoundary = new ComputeBuffer(cellCount, 16);
            diagnostics = new ComputeBuffer(2, 4);
            diagnostics.SetData(new[] { 0u, uint.MaxValue });
            Whitewater = new ComputeBuffer(whitewaterCapacity, 48);
            FoamIndices = new ComputeBuffer(whitewaterCapacity, 4, ComputeBufferType.Append);
            SprayIndices = new ComputeBuffer(whitewaterCapacity, 4, ComputeBufferType.Append);
            FoamArguments = new ComputeBuffer(4, 4, ComputeBufferType.IndirectArguments);
            SprayArguments = new ComputeBuffer(4, 4, ComputeBufferType.IndirectArguments);
            FoamArguments.SetData(new uint[] { 6, 0, 0, 0 });
            SprayArguments.SetData(new uint[] { 6, 0, 0, 0 });

            solver = Instantiate(simulationShader);
            for (int k = 0; k < kernels.Length; k++)
            {
                int kernel = kernels[k] = solver.FindKernel(KernelNames[k]);
                solver.SetBuffer(kernel, "_Particles", Particles);
                solver.SetBuffer(kernel, "_Accumulation", accumulation);
                solver.SetBuffer(kernel, "_Grid", grid);
                solver.SetBuffer(kernel, "_StaticGridBoundary", gridBoundary);
                solver.SetBuffer(kernel, "_Diagnostics", diagnostics);
                solver.SetBuffer(kernel, "_Whitewater", Whitewater);
                solver.SetBuffer(kernel, "_FoamIndices", FoamIndices);
                solver.SetBuffer(kernel, "_SprayIndices", SprayIndices);
            }
            float sound = soundSpeed / h;
            solver.SetInts("_GridSize", size.x, size.y, size.z);
            solver.SetInts("_GridOffset", offset.x, offset.y, offset.z);
            solver.SetInt("_ParticlePlaneStride", particleCount);
            solver.SetInt("_ParticleCount", particleCount);
            solver.SetInt("_Resolution", gridResolution);
            solver.SetInt("_CellCount", cellCount);
            solver.SetFloat("_Dt", FixedTick / substeps);
            solver.SetFloat("_RestDensity", RestDensity);
            solver.SetFloat("_BulkModulus", RestDensity * sound * sound / pressureExponent);
            solver.SetFloat("_PressureExponent", pressureExponent);
            solver.SetFloat("_Viscosity", viscosity);
            solver.SetVector("_Acceleration", gravity / h);
            solver.SetInt("_WhitewaterCapacity", whitewaterCapacity);
            solver.SetFloat("_WhitewaterDt", FixedTick);
            solver.SetFloat("_WhitewaterCellSize", h);
            solver.SetFloat("_WhitewaterSpacing", 1 / Mathf.Pow(RestDensity, 1f / 3));
            solver.SetFloat("_WhitewaterEmission", whitewaterEmissionRate);
            solver.SetFloat("_WhitewaterLifetime", whitewaterLifetime);
            solver.SetFloat("_WhitewaterSpeed", whitewaterSpeedThreshold);
            solver.SetFloat("_WhitewaterDeformation", whitewaterDeformationThreshold);
            solver.SetVector("_WhitewaterGravity", gravity / h);
            var profile = new Vector4[32];
            for (int i = 0; i < BottleBoundary.Profile.Length; i++) profile[i] = BottleBoundary.Profile[i] / h;
            solver.SetInt("_BottleProfileCount", BottleBoundary.Profile.Length);
            solver.SetVectorArray("_BottleProfile", profile);
            solver.SetVector("_BottleCenter", center);
            solver.SetFloat("_BottleClearance", .75f * spacing / h);
            solver.SetFloat("_BottleAxisSign", bottle.AxisSign);
            solver.Dispatch(kernels[0], (cellCount + 127) / 128, 1, 1);
            solver.Dispatch(kernels[4], (cellCount + 127) / 128, 1, 1);
            solver.Dispatch(kernels[5], (whitewaterCapacity + 127) / 128, 1, 1);

            commands = new CommandBuffer { name = "MLS-MPM" };
            previousSphere = sphereObstacle.position;
            whitewaterStep = 0;
            nextReadback = Time.realtimeSinceStartup + .5f;
        }

        public void StepSimulation()
        {
            if (Error != null) return;
            float h = CellSize;
            Vector3 sphereStart = (previousSphere - GridOrigin) / h, sphereEnd = (sphereObstacle.position - GridOrigin) / h;
            commands.Clear();
            commands.SetComputeVectorParam(solver, "_SphereVelocity", (sphereObstacle.position - previousSphere) / (FixedTick * h));
            previousSphere = sphereObstacle.position;
            int particleGroups = (particleCount + 127) / 128, cellGroups = (cellCount + 127) / 128;
            for (int step = 0; step < substeps; step++)
            {
                // Sweep the collider through the tick instead of teleporting it.
                Vector3 sphere = Vector3.Lerp(sphereStart, sphereEnd, (step + 1f) / substeps);
                commands.SetComputeVectorParam(solver, "_Sphere", new Vector4(sphere.x, sphere.y, sphere.z, sphereRadius / h));
                commands.DispatchCompute(solver, kernels[0], cellGroups, 1, 1);
                commands.DispatchCompute(solver, kernels[1], particleGroups, 1, 1);
                commands.DispatchCompute(solver, kernels[2], cellGroups, 1, 1);
                commands.DispatchCompute(solver, kernels[3], particleGroups, 1, 1);
            }
            if (enableWhitewater)
            {
                commands.SetComputeIntParam(solver, "_WhitewaterStep", whitewaterStep++);
                commands.SetBufferCounterValue(FoamIndices, 0);
                commands.SetBufferCounterValue(SprayIndices, 0);
                commands.DispatchCompute(solver, kernels[6], (whitewaterCapacity + 127) / 128, 1, 1);
                commands.CopyCounterValue(FoamIndices, FoamArguments, 4);
                commands.CopyCounterValue(SprayIndices, SprayArguments, 4);
            }
            Graphics.ExecuteCommandBuffer(commands);
        }

        void Update()
        {
            if (!IsInitialized || readbackPending || Time.realtimeSinceStartup < nextReadback) return;
            nextReadback = Time.realtimeSinceStartup + 1;
            readbackPending = true;
            int requestGeneration = generation;
            AsyncGPUReadback.Request(diagnostics, request =>
            {
                if (this == null || generation != requestGeneration) return;
                readbackPending = false;
                if (request.hasError) return;
                var values = request.GetData<uint>();
                if (values[0] != 0)
                    Error = "GPU fault " + values[0] + " at particle " + values[1] +
                        " (1 non-finite, 2 overflow, 4 support, 8 density, 16 displacement, 32 whitewater).";
            });
        }

        void LateUpdate()
        {
            if (!IsInitialized || renderSurface) return;
            drawProperties.SetBuffer("_Particles", Particles);
            drawProperties.SetInt("_ParticlePlaneStride", particleCount);
            drawProperties.SetVector("_Origin", GridOrigin);
            drawProperties.SetFloat("_CellSize", CellSize);
            drawProperties.SetFloat("_Radius", displayRadius);
            var bounds = new Bounds(transform.position + Vector3.one * (domainSize * .5f), Vector3.one * (domainSize + CellSize));
            Graphics.DrawProcedural(particleMaterial, bounds, MeshTopology.Triangles, 6, particleCount,
                null, drawProperties, ShadowCastingMode.Off, false, gameObject.layer);
        }

        void Release()
        {
            generation++;
            readbackPending = false;
            Particles?.Release(); accumulation?.Release(); grid?.Release(); gridBoundary?.Release(); diagnostics?.Release();
            Whitewater?.Release(); FoamIndices?.Release(); SprayIndices?.Release(); FoamArguments?.Release(); SprayArguments?.Release();
            commands?.Release();
            Particles = null;
            if (solver != null) Destroy(solver);
        }
    }
}