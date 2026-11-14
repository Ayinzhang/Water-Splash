using UnityEngine;
using UnityEngine.Rendering;

namespace WaterSplash
{
    // Weakly compressible MLS-MPM water on an 80 x 30 x 30 grid window around the bottle.
    public sealed class MlsMpmFluid : MonoBehaviour
    {
        public const float FixedTick = 1f / 60;
        static readonly string[] KernelNames = {
            "ClearGrid", "Transfer", "UpdateGrid", "UpdateParticles", "ClearWhitewater", "UpdateWhitewater" };
        const int CellCount = 80 * 30 * 30;

        public ComputeShader simulationShader;
        public int particleCount = 65536;
        public int substeps = 8;
        public float soundSpeed = 6;
        public float pressureExponent = 3;
        public float viscosity = .15f;
        public Vector3 gravity = new Vector3(0, -9.81f, 0);
        public Vector3 SpherePosition { get; set; }
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
        public float CellSize => 4f / 75;
        public Vector3 GridOrigin => new Vector3(-2, 0, -2) - Vector3.one * (2 * CellSize);
        public float DisplayRadius { get; private set; }
        public ComputeShader Solver => solver;
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
        Vector3 previousSphere;
        float nextReadback;
        int whitewaterStep, generation;
        bool readbackPending;

        void OnEnable() => Active = this;
        void OnDisable() { if (Active == this) Active = null; Release(); }

        public void Initialize(BottleBoundary bottle)
        {
            Release();
            Error = null;
            float h = CellSize;
            Vector3 center = (bottle.center - GridOrigin) / h;
            Particles = new ComputeBuffer(particleCount * 5, 16);
            accumulation = new ComputeBuffer(CellCount * 2, 4);
            grid = new ComputeBuffer(CellCount, 16);
            gridBoundary = new ComputeBuffer(CellCount, 16);
            float spacing = bottle.LoadInitialState(particleCount, Particles, gridBoundary);
            RestDensity = Mathf.Pow(h / spacing, 3);
            DisplayRadius = spacing * .34f;
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
            solver.SetInts("_GridSize", 80, 30, 30);
            solver.SetInts("_GridOffset", 0, 5, 25);
            solver.SetInt("_ParticlePlaneStride", particleCount);
            solver.SetInt("_ParticleCount", particleCount);
            solver.SetInt("_Resolution", 80);
            solver.SetInt("_CellCount", CellCount);
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
            solver.Dispatch(kernels[0], (CellCount + 127) / 128, 1, 1);
            solver.Dispatch(kernels[4], (whitewaterCapacity + 127) / 128, 1, 1);

            commands = new CommandBuffer { name = "MLS-MPM" };
            previousSphere = SpherePosition;
            whitewaterStep = 0;
            nextReadback = Time.realtimeSinceStartup + .5f;
        }

        public void StepSimulation()
        {
            if (Error != null) return;
            float h = CellSize;
            Vector3 sphereStart = (previousSphere - GridOrigin) / h, sphereEnd = (SpherePosition - GridOrigin) / h;
            commands.Clear();
            commands.SetComputeVectorParam(solver, "_SphereVelocity", (SpherePosition - previousSphere) / (FixedTick * h));
            previousSphere = SpherePosition;
            int particleGroups = (particleCount + 127) / 128, cellGroups = (CellCount + 127) / 128;
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
                commands.DispatchCompute(solver, kernels[5], (whitewaterCapacity + 127) / 128, 1, 1);
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
                if (request.hasError) { Error = "GPU diagnostics readback failed."; return; }
                var values = request.GetData<uint>();
                if (values[0] != 0)
                    Error = "GPU fault " + values[0] + " at particle " + values[1] +
                        " (1 non-finite, 2 overflow, 4 support, 8 density, 16 displacement, 32 whitewater).";
            });
        }

        void Release()
        {
            generation++;
            readbackPending = false;
            Particles?.Release(); accumulation?.Release(); grid?.Release(); gridBoundary?.Release(); diagnostics?.Release();
            Whitewater?.Release(); FoamIndices?.Release(); SprayIndices?.Release(); FoamArguments?.Release(); SprayArguments?.Release();
            commands?.Release();
            commands = null;
            Particles = null;
            if (solver != null) Destroy(solver);
        }
    }
}