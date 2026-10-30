using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Profiling;

namespace WaterSplash
{
    public sealed class MlsMpmFluid : MonoBehaviour
    {
        public enum SurfaceQuality { Low, Medium, High }
        public enum SolverMethod { MlsMpm, PositionBasedMpm }

        static readonly List<MlsMpmFluid> instances = new List<MlsMpmFluid>();
        internal static IReadOnlyList<MlsMpmFluid> ActiveInstances => instances;

        [StructLayout(LayoutKind.Sequential)]
        public struct ParticleState
        {
            public Vector4 position;
            public Vector4 velocity;
            public Vector4 affineX;
            public Vector4 affineY;
            public Vector4 affineZ;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WhitewaterState
        {
            public Vector4 position;
            public Vector4 velocity;
            public Vector4 normalType;
        }

        [Serializable]
        public sealed class WhitewaterSnapshot
        {
            public int foam;
            public int spray;
            public int nonFinite;
            public int outside;
            public int insideSphere;
            public uint born;
            public uint expired;
            public WhitewaterState[] states;
        }

        [Serializable]
        public sealed class Snapshot
        {
            public int particleCount;
            public int nonFiniteParticles;
            public int outsideParticles;
            public int insideSphereParticles;
            public double gridMass;
            public Vector3 center;
            public Vector3 minimum;
            public Vector3 maximum;
            public float maximumSpeed;
            public float peakDensityRatio;
            public uint faults;
            public double simulatedSeconds;
            public double meanKineticEnergy, meanAffineKineticEnergy, meanPotentialEnergy, meanCompressionEnergy;
            public float maximumWorldHeight, p99WorldHeight;
            public float minimumVolumeRatio, maximumVolumeRatio;
            public int flooredVolumeParticles;
        }

        public ComputeShader simulationShader;
        public Material particleMaterial;
        [Range(512, 262144)] public int particleCount = 32768;
        [Range(16, 96)] public int gridResolution = 48;
        [Min(0.1f)] public float domainSize = 4;
        [Range(1, 32)] public int substeps = 8;
        [Range(1, 8)] public int maxCatchUpTicks = 2;
        [Range(1, 12)] public float soundSpeed = 6;
        [Range(1, 7)] public float pressureExponent = 3;
        [Range(0, 2)] public float viscosity = 0.15f;
        [Min(0)] public float affineDamping = 0;
        [Min(0)] public float divergenceDamping = 0;
        public Vector3 gravity = new Vector3(0, -9.81f, 0);
        public Transform sphereObstacle;
        [Min(0.01f)] public float sphereRadius = 0.42f;
        public Camera interactionCamera;
        public bool showParticles = true;
        public bool renderSurface = true;
        [Tooltip("Individual-particle display radius in grid cells; does not affect simulation or surface reconstruction.")]
        [Range(0.05f, 0.5f)] public float particleRadiusScale = 0.3f;
        public SurfaceQuality surfaceQuality = SurfaceQuality.Medium;
        [Range(0.6f, 1.5f)] public float surfaceRadiusScale = 0.95f;
        public bool enableWhitewater = true;
        [Tooltip("Store position, velocity and affine columns in separate contiguous float4 streams. Reset after changing.")]
        public bool useParticleSoA;
        [Tooltip("Bottle solver only: cache exact static grid-wall distances and normals. Reset after changing.")]
        public bool cacheGridBoundary;
        [Tooltip("PB-MPM only: reorder particles by 4^3 grid blocks every N ticks. MLS uses the original unsorted two-pass solver.")]
        [Range(0, 16)] public int spatialSortInterval = 1;
        public SolverMethod solverMethod;
        [Range(1, 16)] public int positionIterations = 2;
        [Range(0.1f, 2)] public float positionRelaxation = 1.5f;
        [Range(0, 1)] public float positionViscosity = 0.01f;
        [Range(0, 1)] public float gridVolumeBlend = 0.1f;
        [Range(512, 131072)] public int whitewaterCapacity = 32768;
        [Range(0, 30)] public float whitewaterEmissionRate = 8;
        [Range(0.2f, 8)] public float whitewaterLifetime = 2.5f;
        [Range(0.1f, 5)] public float whitewaterSpeedThreshold = 1.2f;
        [Range(0, 30)] public float whitewaterDeformationThreshold = 4;
        [Range(0.1f, 0.8f)] public float whitewaterRadiusScale = 0.4f;
        public bool showControls = true;
        public bool paused;
        [NonSerialized] public bool externalControl;
        // Optional scene-local boundary parameters; baseline solvers leave this unset.
        [NonSerialized] public Action<CommandBuffer, ComputeShader> configureSimulation;
        // Scene-local storage window; positions retain the original global grid coordinates.
        [NonSerialized] internal BoundsInt? gridWindow;

        public const double FixedTick = 1.0 / 60.0;
        public bool IsInitialized => particles != null;
        public double SimulatedSeconds { get; private set; }
        public double BacklogSeconds => accumulator;
        public string Error { get; private set; }
        public float CellSize => domainSize / (gridResolution - 5);
        public Vector3 GridOrigin => transform.position - Vector3.one * (2 * CellSize);
        public BoundsInt GridWindow => gridWindow ?? new BoundsInt(Vector3Int.zero, Vector3Int.one * gridResolution);
        public Vector3Int GridDimensions => GridWindow.size;
        public float RestDensity { get; private set; }
        public string RenderingError { get; private set; }
        public float SurfaceParticleRadius => IsInitialized ?
            CellSize * surfaceRadiusScale / Mathf.Pow(RestDensity, 1f / 3f) : 0;
        public float DisplayParticleRadius => CellSize * particleRadiusScale;
        internal ComputeBuffer ParticleBuffer => particles;
        internal int ParticlePlaneStride => activeParticleSoA ? activeCount : 0;
        internal Bounds SurfaceBounds
        {
            get
            {
                var bounds = new Bounds(GridOrigin + (Vector3)activeGridWindow.position * CellSize +
                    (Vector3)activeGridWindow.size * (CellSize * .5f), (Vector3)activeGridWindow.size * CellSize);
                // Include the largest stretched foam/spray billboard, not only seed centres.
                bounds.Expand(2 * Mathf.Max(SurfaceParticleRadius, WhitewaterRadius * 16));
                return bounds;
            }
        }
        internal int RenderedParticleCount => activeCount;
        internal ComputeBuffer WhitewaterBuffer => whitewater;
        internal ComputeBuffer FoamIndices => foamIndices;
        internal ComputeBuffer SprayIndices => sprayIndices;
        internal ComputeBuffer FoamArguments => foamArguments;
        internal ComputeBuffer SprayArguments => sprayArguments;
        internal float WhitewaterRadius => CellSize * whitewaterRadiusScale / Mathf.Pow(RestDensity, 1f / 3f);

        ComputeShader solver;
        Material drawingMaterial;
        ComputeBuffer particles;
        ComputeBuffer accumulation;
        ComputeBuffer grid;
        ComputeBuffer diagnostics;
        ComputeBuffer whitewater;
        ComputeBuffer foamIndices;
        ComputeBuffer sprayIndices;
        ComputeBuffer foamArguments;
        ComputeBuffer sprayArguments;
        ComputeBuffer sortKeys;
        ComputeBuffer sortRanks;
        ComputeBuffer sortBins;
        ComputeBuffer sortedParticles;
        ComputeBuffer staticGridBoundary;
        int cacheGridKernel;
        bool gridBoundaryReady;
        bool activeParticleSoA;
        bool activeCacheGridBoundary;
        readonly int[] sortKernels = new int[5];
        int sortBlocks;
        CommandBuffer commands;
        MaterialPropertyBlock drawProperties;
        readonly int[] kernels = new int[5];
        readonly int[] positionKernels = new int[7];
        SolverMethod activeSolverMethod;
        static readonly CustomSampler positionSampler = CustomSampler.Create("PB-MPM GPU/Displacement solve", true);
        readonly uint[] diagnosticData = new uint[8];
        static readonly CustomSampler sortSampler = CustomSampler.Create("PB-MPM GPU/Spatial sort", true);
        static readonly CustomSampler[] gpuStages = {
            CustomSampler.Create("MLS-MPM GPU/Clear", true),
            CustomSampler.Create("MLS-MPM GPU/Mass + momentum", true),
            CustomSampler.Create("MLS-MPM GPU/Stress", true),
            CustomSampler.Create("MLS-MPM GPU/Grid velocity", true),
            CustomSampler.Create("MLS-MPM GPU/G2P + advection", true),
            CustomSampler.Create("MLS-MPM GPU/Whitewater", true)
        };
        int activeCount;
        int activeResolution;
        BoundsInt activeGridWindow;
        int activeCellCount;
        int activeWhitewaterCapacity;
        int clearWhitewaterKernel;
        int clearWhitewaterCountsKernel;
        int updateWhitewaterKernel;
        int whitewaterStep;
        uint foamCount;
        uint sprayCount;
        bool whitewaterWasEnabled;
        int generation;
        double accumulator;
        double kickUntil;
        float nextReadback;
        float averageFrame;
        float peakSpeed;
        float peakDensity;
        float activeDomainSize;
        Vector3 previousSphere;
        Vector3 sphereVelocity;
        Vector3 dragTarget;
        bool dragging;
        bool readbackPending;
        bool backlogWarning;
        bool applicationPaused;
        bool skipNextDelta;
        int previousFrameRate;
        int previousVSync;
        bool ownsFrameSettings;
        int initializedFrame;
        int lastSurfaceFrame;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetRegistry() => instances.Clear();

        void OnEnable()
        {
            if (!Application.isPlaying) return;
            instances.Add(this);
            previousFrameRate = Application.targetFrameRate;
            previousVSync = QualitySettings.vSyncCount;
            ownsFrameSettings = true;
            Application.targetFrameRate = Application.isMobilePlatform ? 1000 : -1;
            QualitySettings.vSyncCount = 0;
            Initialize();
        }

        void OnDisable()
        {
            instances.Remove(this);
            Release();
            if (!ownsFrameSettings) return;
            Application.targetFrameRate = previousFrameRate;
            QualitySettings.vSyncCount = previousVSync;
            ownsFrameSettings = false;
        }

        public void Initialize()
        {
            Release();
            Error = null;
            RenderingError = null;
            initializedFrame = Time.frameCount;
            lastSurfaceFrame = Time.frameCount;
            if (!ValidateConfiguration(out string error))
            {
                Fail(error);
                return;
            }

            activeCount = particleCount;
            activeResolution = gridResolution;
            activeGridWindow = GridWindow;
            activeWhitewaterCapacity = whitewaterCapacity;
            activeDomainSize = domainSize;
            activeSolverMethod = solverMethod;
            activeParticleSoA = useParticleSoA;
            activeCacheGridBoundary = cacheGridBoundary;
            solver = Instantiate(simulationShader);
            solver.name = "MLS-MPM runtime solver";
            Vector3Int dimensions = activeGridWindow.size;
            int cells = activeCellCount = checked(dimensions.x * dimensions.y * dimensions.z);
            solver.SetInts("_GridSize", dimensions.x, dimensions.y, dimensions.z);
            Vector3Int offset = activeGridWindow.position;
            solver.SetInts("_GridOffset", offset.x, offset.y, offset.z);
            particles = new ComputeBuffer(particleCount * 5, sizeof(float) * 4);
            solver.SetInt("_ParticlePlaneStride", ParticlePlaneStride);
            accumulation = new ComputeBuffer(cells * 4, sizeof(int));
            grid = new ComputeBuffer(cells, sizeof(float) * 4);
            diagnostics = new ComputeBuffer(8, sizeof(uint));
            diagnostics.SetData(new uint[] { 0, 0, 0, uint.MaxValue, 0, 0, 0, 0 });

            int side = Mathf.CeilToInt(Mathf.Pow(particleCount, 1f / 3f));
            while (side * side * side < particleCount) side++;
            // Preserve the original 32-grid demo's physical water block across resolutions.
            float usable = gridResolution - 5;
            float width = usable * (16f / 27);
            float lower = 2 + usable / 27;
            float zLower = 2 + usable * (6f / 27);
            float spacing = width / side;
            RestDensity = 1f / (spacing * spacing * spacing);
            var initial = new ParticleState[particleCount];
            for (int i = 0; i < initial.Length; i++)
            {
                int x = i % side;
                int y = (i / side) % side;
                int z = i / (side * side);
                initial[i].position = new Vector4(
                    lower + (x + 0.5f) * spacing,
                    lower + (y + 0.5f) * spacing,
                    zLower + (z + 0.5f) * spacing, 1);
            }
            UploadParticles(initial);

            string[] names = { "ClearGrid", "TransferMass", "TransferStress", "UpdateGrid", "UpdateParticles" };
            for (int i = 0; i < kernels.Length; i++)
            {
                kernels[i] = solver.FindKernel(names[i]);
                if (i != 4) solver.SetBuffer(kernels[i], "_Accumulation", accumulation);
                if (i != 0) solver.SetBuffer(kernels[i], "_Diagnostics", diagnostics);
                if (i == 0 || i == 3 || i == 4) solver.SetBuffer(kernels[i], "_Grid", grid);
                if (i == 1 || i == 2 || i == 4) solver.SetBuffer(kernels[i], "_Particles", particles);
            }
            cacheGridKernel = solver.HasKernel("CacheBottleBoundary") ? solver.FindKernel("CacheBottleBoundary") : -1;
            gridBoundaryReady = false;
            if (cacheGridKernel >= 0)
            {
                staticGridBoundary = new ComputeBuffer(activeCacheGridBoundary ? cells : 1, sizeof(float) * 4);
                solver.SetBuffer(cacheGridKernel, "_StaticGridBoundary", staticGridBoundary);
                solver.SetBuffer(kernels[3], "_StaticGridBoundary", staticGridBoundary);
                solver.SetInt("_UseCachedGridBoundary", activeCacheGridBoundary ? 1 : 0);
            }
            string[] positionNames = { "PbPrepare", "PbProject", "PbScatter", "PbGrid", "PbGather", "PbIntegrate", "PbExportGrid" };
            for (int i = 0; i < positionKernels.Length; i++)
            {
                int kernel = positionKernels[i] = solver.FindKernel(positionNames[i]);
                solver.SetBuffer(kernel, "_Diagnostics", diagnostics);
                if (i != 3 && i != 6) solver.SetBuffer(kernel, "_Particles", particles);
                if (i == 2 || i == 3) solver.SetBuffer(kernel, "_Accumulation", accumulation);
                if (i == 3 || i == 4 || i == 6) solver.SetBuffer(kernel, "_Grid", grid);
            }
            if (solverMethod == SolverMethod.PositionBasedMpm)
            {
                sortBlocks = (gridResolution + 3) / 4;
                sortKeys = new ComputeBuffer(particleCount, sizeof(uint));
                sortRanks = new ComputeBuffer(particleCount, sizeof(uint));
                sortBins = new ComputeBuffer(sortBlocks * sortBlocks * sortBlocks, sizeof(uint));
                sortedParticles = new ComputeBuffer(particleCount, Marshal.SizeOf<ParticleState>());
                string[] sortNames = { "ClearSortBins", "CountSortBins", "ScanSortBins", "ScatterSortedParticles", "CopySortedParticles" };
                for (int i = 0; i < sortKernels.Length; i++)
                {
                    sortKernels[i] = solver.FindKernel(sortNames[i]);
                    solver.SetBuffer(sortKernels[i], "_SortBins", sortBins);
                    if (i != 0 && i != 2)
                    {
                        solver.SetBuffer(sortKernels[i], "_Particles", particles);
                        solver.SetBuffer(sortKernels[i], "_SortKeys", sortKeys);
                        solver.SetBuffer(sortKernels[i], "_SortRanks", sortRanks);
                        solver.SetBuffer(sortKernels[i], "_SortedParticles", sortedParticles);
                    }
                }
                solver.SetInt("_SortBlocks", sortBlocks);
                solver.SetInt("_SortBinCount", sortBlocks * sortBlocks * sortBlocks);
            }
            whitewater = new ComputeBuffer(activeWhitewaterCapacity, Marshal.SizeOf<WhitewaterState>());
            foamIndices = new ComputeBuffer(activeWhitewaterCapacity, sizeof(uint), ComputeBufferType.Append);
            sprayIndices = new ComputeBuffer(activeWhitewaterCapacity, sizeof(uint), ComputeBufferType.Append);
            foamArguments = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            sprayArguments = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            foamIndices.SetCounterValue(0);
            sprayIndices.SetCounterValue(0);
            foamArguments.SetData(new uint[] { 6, 0, 0, 0 });
            sprayArguments.SetData(new uint[] { 6, 0, 0, 0 });
            clearWhitewaterKernel = solver.FindKernel("ClearWhitewater");
            clearWhitewaterCountsKernel = solver.FindKernel("ClearWhitewaterCounts");
            updateWhitewaterKernel = solver.FindKernel("UpdateWhitewater");
            solver.SetBuffer(clearWhitewaterKernel, "_Whitewater", whitewater);
            solver.SetBuffer(clearWhitewaterKernel, "_Diagnostics", diagnostics);
            solver.SetBuffer(clearWhitewaterCountsKernel, "_Diagnostics", diagnostics);
            solver.SetBuffer(updateWhitewaterKernel, "_Whitewater", whitewater);
            solver.SetBuffer(updateWhitewaterKernel, "_Particles", particles);
            solver.SetBuffer(updateWhitewaterKernel, "_Grid", grid);
            solver.SetBuffer(updateWhitewaterKernel, "_Diagnostics", diagnostics);
            solver.SetBuffer(updateWhitewaterKernel, "_FoamIndices", foamIndices);
            solver.SetBuffer(updateWhitewaterKernel, "_SprayIndices", sprayIndices);
            solver.SetInt("_WhitewaterCapacity", activeWhitewaterCapacity);
            solver.Dispatch(clearWhitewaterKernel, (activeWhitewaterCapacity + 127) / 128, 1, 1);
            whitewaterStep = 0;
            foamCount = sprayCount = 0;
            whitewaterWasEnabled = enableWhitewater;
            commands = new CommandBuffer { name = "Weakly compressible MLS-MPM" };
            drawProperties = new MaterialPropertyBlock();
            if (particleMaterial != null)
            {
                drawingMaterial = new Material(particleMaterial);
                drawProperties.SetBuffer("_Particles", particles);
                drawProperties.SetInt("_ParticlePlaneStride", ParticlePlaneStride);
            }
            SimulatedSeconds = 0;
            accumulator = 0;
            kickUntil = 0;
            peakDensity = 0;
            peakSpeed = 0;
            nextReadback = Time.realtimeSinceStartup + 0.5f;
            previousSphere = sphereObstacle == null ? Vector3.zero : sphereObstacle.position;
            sphereVelocity = Vector3.zero;
            dragging = false;
            backlogWarning = false;
            readbackPending = false;
            paused = false;
            ClearInitialGrid(cells);
            if (!SystemInfo.supportsAsyncGPUReadback)
                Debug.LogWarning("MLS-MPM: async GPU readback is unavailable; diagnostics will synchronize once per second.", this);
        }

        bool ValidateConfiguration(out string error)
        {
            error = null;
            if (!SystemInfo.supportsComputeShaders || SystemInfo.maxComputeWorkGroupSize < 128)
                error = "MLS-MPM requires compute shaders with at least 128 invocations per workgroup.";
            else if (simulationShader == null)
                error = "Assign the MLS-MPM compute shader.";
            else if (cacheGridBoundary && (!simulationShader.HasKernel("CacheBottleBoundary") || configureSimulation == null))
                error = "Static grid boundary caching requires the bottle shader and its boundary configuration.";
            else if (showParticles && !renderSurface && (particleMaterial == null || !particleMaterial.shader.isSupported))
                error = "Assign a supported MLS-MPM particle material.";
            else if (!Finite(particleRadiusScale) || particleRadiusScale <= 0)
                error = "Particle display radius must be finite and positive.";
            else if (particleCount < 512 || particleCount > 262144 || gridResolution < 16 || gridResolution > 96)
                error = "Particle count must be 512..262144 and grid resolution 16..96.";
            else if (!ValidGridWindow())
                error = "A scene-local grid window requires MLS, at least 8 cells per axis and bounds inside the original grid.";
            else if (!Finite(domainSize) || domainSize <= 0 || !Finite(soundSpeed) || soundSpeed <= 0 ||
                     !Finite(pressureExponent) || pressureExponent < 1 || pressureExponent > 7 ||
                     !Finite(viscosity) || viscosity < 0 || !Finite(affineDamping) || affineDamping < 0 ||
                     !Finite(divergenceDamping) || divergenceDamping < 0 ||
                     !Finite(gravity.x) || !Finite(gravity.y) || !Finite(gravity.z))
                error = "Domain, sound speed, viscosity and gravity must be finite, with positive domain and sound speed.";
            else if (substeps < 1 || substeps > 32 || maxCatchUpTicks < 1 || maxCatchUpTicks > 8)
                error = "Substeps must be 1..32 and maximum catch-up ticks 1..8.";
            else if (!Enum.IsDefined(typeof(SolverMethod), solverMethod) ||
                     positionIterations < 1 || positionIterations > 16 ||
                     !Finite(positionRelaxation) || positionRelaxation < 0.1f || positionRelaxation > 2 ||
                     !Finite(positionViscosity) || positionViscosity < 0 || positionViscosity > 1 ||
                     !Finite(gridVolumeBlend) || gridVolumeBlend < 0 || gridVolumeBlend > 1)
                error = "PB-MPM needs 1..16 iterations, relaxation 0.1..2 and viscosity/volume blend 0..1.";
            else if (whitewaterCapacity < 512 || whitewaterCapacity > 131072 ||
                     !Finite(whitewaterEmissionRate) || whitewaterEmissionRate < 0 ||
                     !Finite(whitewaterLifetime) || whitewaterLifetime <= 0 || whitewaterLifetime > 30 ||
                     !Finite(whitewaterSpeedThreshold) || whitewaterSpeedThreshold <= 0 ||
                     !Finite(whitewaterDeformationThreshold) || whitewaterDeformationThreshold < 0 ||
                     !Finite(whitewaterRadiusScale) || whitewaterRadiusScale <= 0)
                error = "Whitewater needs capacity 512..131072, finite nonnegative emission/deformation, " +
                        "positive speed/radius and lifetime in (0,30] seconds.";
            else if (sphereObstacle != null && (!Finite(sphereRadius) || sphereRadius <= 0 ||
                     sphereRadius >= (gridResolution - 5) * CellSize * 0.5f))
                error = "The sphere must have a positive radius smaller than half the usable container.";
            else if (Quaternion.Angle(transform.rotation, Quaternion.identity) > 0.01f ||
                     (transform.lossyScale - Vector3.one).sqrMagnitude > 0.0001f)
                error = "Keep the simulation transform unrotated and at unit world scale; translation is supported.";
            return error == null;
        }

        bool ValidGridWindow()
        {
            if (!gridWindow.HasValue) return true;
            BoundsInt window = gridWindow.Value;
            return solverMethod == SolverMethod.MlsMpm &&
                window.size.x >= 8 && window.size.y >= 8 && window.size.z >= 8 &&
                window.xMin >= 0 && window.yMin >= 0 && window.zMin >= 0 &&
                window.xMax <= gridResolution && window.yMax <= gridResolution && window.zMax <= gridResolution;
        }

        void ClearInitialGrid(int cells)
        {
            solver.SetInt("_CellCount", cells);
            solver.Dispatch(kernels[0], (cells + 127) / 128, 1, 1);
        }

        void Update()
        {
            float delta = Time.unscaledDeltaTime;
            averageFrame = averageFrame <= 0 ? delta : Mathf.Lerp(averageFrame, delta, 0.05f);
            if (!externalControl && Input.GetKeyDown(KeyCode.R)) { Initialize(); return; }
            if (!IsInitialized || Error != null) return;
            if (particleCount != activeCount || gridResolution != activeResolution || GridWindow != activeGridWindow ||
                domainSize != activeDomainSize || whitewaterCapacity != activeWhitewaterCapacity || solverMethod != activeSolverMethod ||
                useParticleSoA != activeParticleSoA || cacheGridBoundary != activeCacheGridBoundary)
            {
                Fail("Solver, particle count/layout, grid, boundary cache, domain or whitewater capacity changed. Press Reset to rebuild GPU buffers.");
                return;
            }
            if (!ValidateConfiguration(out string error))
            {
                Fail(error);
                return;
            }
            if (externalControl) { PollDiagnostics(); return; }
            if (Input.GetKeyDown(KeyCode.Space)) paused = !paused;
            UpdateInteraction();
            if (skipNextDelta) { skipNextDelta = false; delta = 0; }
            if (!paused && !applicationPaused)
            {
                accumulator += delta;
                int ticks = Math.Min(maxCatchUpTicks, (int)(accumulator / FixedTick));
                if (ticks > 0)
                {
                    StepSimulation(ticks);
                    accumulator -= ticks * FixedTick;
                }
                if (accumulator > 0.5 && !backlogWarning)
                {
                    backlogWarning = true;
                    Debug.LogWarning("MLS-MPM is behind real time. No simulation time was discarded; reduce the preset to measure real-time operation.", this);
                }
            }
            PollDiagnostics();
        }

        public void StepSimulation(int ticks)
        {
            if (!IsInitialized) throw new InvalidOperationException("Initialize the solver before stepping.");
            if (ticks < 1 || ticks > 120) throw new ArgumentOutOfRangeException(nameof(ticks));
            if (substeps < 1 || substeps > 32) throw new ArgumentOutOfRangeException(nameof(substeps));
            if (Error != null) throw new InvalidOperationException(Error);
            if (particleCount != activeCount || gridResolution != activeResolution || GridWindow != activeGridWindow ||
                domainSize != activeDomainSize || whitewaterCapacity != activeWhitewaterCapacity || solverMethod != activeSolverMethod ||
                useParticleSoA != activeParticleSoA || cacheGridBoundary != activeCacheGridBoundary)
                throw new InvalidOperationException("Reset after changing solver, particle count/layout, grid, boundary cache, domain or whitewater capacity.");
            if (!ValidateConfiguration(out string error)) throw new ArgumentException(error);
            commands.Clear();
            configureSimulation?.Invoke(commands, solver);
            float dt = (float)(FixedTick / substeps);
            float h = CellSize;
            float soundInCells = soundSpeed / h;
            commands.SetComputeIntParam(solver, "_ParticleCount", activeCount);
            commands.SetComputeIntParam(solver, "_Resolution", activeResolution);
            commands.SetComputeIntParam(solver, "_CellCount", activeCellCount);
            commands.SetComputeFloatParam(solver, "_Dt", dt);
            commands.SetComputeFloatParam(solver, "_RestDensity", RestDensity);
            commands.SetComputeFloatParam(solver, "_BulkModulus", RestDensity * soundInCells * soundInCells / pressureExponent);
            commands.SetComputeFloatParam(solver, "_PressureExponent", pressureExponent);
            commands.SetComputeFloatParam(solver, "_Viscosity", viscosity);
            commands.SetComputeFloatParam(solver, "_AffineRetention", Mathf.Exp(-affineDamping * dt));
            commands.SetComputeFloatParam(solver, "_DivergenceRetention", Mathf.Exp(-divergenceDamping * dt));
            commands.SetComputeFloatParam(solver, "_PbRelaxation", positionRelaxation);
            commands.SetComputeFloatParam(solver, "_PbViscosity", positionViscosity);
            commands.SetComputeFloatParam(solver, "_PbVolumeBlend", gridVolumeBlend);
            commands.SetComputeFloatParam(solver, "_WhitewaterDt", (float)FixedTick);
            commands.SetComputeFloatParam(solver, "_WhitewaterCellSize", h);
            commands.SetComputeFloatParam(solver, "_WhitewaterSpacing", 1f / Mathf.Pow(RestDensity, 1f / 3f));
            commands.SetComputeFloatParam(solver, "_WhitewaterEmission", whitewaterEmissionRate);
            commands.SetComputeFloatParam(solver, "_WhitewaterLifetime", whitewaterLifetime);
            commands.SetComputeFloatParam(solver, "_WhitewaterSpeed", whitewaterSpeedThreshold);
            commands.SetComputeFloatParam(solver, "_WhitewaterDeformation", whitewaterDeformationThreshold);
            commands.SetComputeVectorParam(solver, "_WhitewaterGravity", gravity / h);
            if (whitewaterWasEnabled != enableWhitewater)
            {
                commands.DispatchCompute(solver, clearWhitewaterKernel, (activeWhitewaterCapacity + 127) / 128, 1, 1);
                ClearWhitewaterDrawCounts();
                whitewaterWasEnabled = enableWhitewater;
            }
            int particleGroups = (activeCount + 127) / 128;
            int cellGroups = (activeCellCount + 127) / 128;
            for (int tick = 0; tick < ticks; tick++)
            {
                Vector3 sphereStart = sphereObstacle == null ? Vector3.zero : (previousSphere - GridOrigin) / h;
                UpdateSphereForTick();
                Vector3 sphere = SpherePosition();
                commands.SetComputeVectorParam(solver, "_SphereVelocity", sphereVelocity / h);
                Vector3 acceleration = gravity;
                if (SimulatedSeconds < kickUntil) acceleration += new Vector3(12, 3, 0);
                commands.SetComputeVectorParam(solver, "_Acceleration", acceleration / h);
                if (solverMethod == SolverMethod.PositionBasedMpm && spatialSortInterval > 0 && whitewaterStep % spatialSortInterval == 0)
                {
                    commands.BeginSample(sortSampler);
                    int binGroups = (sortBlocks * sortBlocks * sortBlocks + 127) / 128;
                    commands.DispatchCompute(solver, sortKernels[0], binGroups, 1, 1);
                    commands.DispatchCompute(solver, sortKernels[1], particleGroups, 1, 1);
                    commands.DispatchCompute(solver, sortKernels[2], 1, 1, 1);
                    commands.DispatchCompute(solver, sortKernels[3], particleGroups, 1, 1);
                    commands.DispatchCompute(solver, sortKernels[4], particleGroups, 1, 1);
                    commands.EndSample(sortSampler);
                }
                for (int step = 0; step < substeps; step++)
                {
                    // Sweep the collider through the tick instead of teleporting it to
                    // its final pose before all substeps, which creates deep overlaps.
                    Vector3 substepSphere = Vector3.Lerp(sphereStart, sphere, (step + 1f) / substeps);
                    commands.SetComputeVectorParam(solver, "_Sphere", new Vector4(substepSphere.x, substepSphere.y,
                        substepSphere.z, sphereObstacle == null ? 0 : sphereRadius / h));
                    if (solverMethod == SolverMethod.PositionBasedMpm)
                    {
                        commands.BeginSample(positionSampler);
                        commands.DispatchCompute(solver, positionKernels[0], particleGroups, 1, 1);
                        for (int iteration = 0; iteration < positionIterations; iteration++)
                        {
                            commands.DispatchCompute(solver, positionKernels[1], particleGroups, 1, 1);
                            commands.DispatchCompute(solver, kernels[0], cellGroups, 1, 1);
                            commands.DispatchCompute(solver, positionKernels[2], particleGroups, 1, 1);
                            commands.DispatchCompute(solver, positionKernels[3], cellGroups, 1, 1);
                            commands.DispatchCompute(solver, positionKernels[4], particleGroups, 1, 1);
                        }
                        commands.DispatchCompute(solver, positionKernels[5], particleGroups, 1, 1);
                        if (step == substeps - 1)
                            commands.DispatchCompute(solver, positionKernels[6], cellGroups, 1, 1);
                        commands.EndSample(positionSampler);
                    }
                    else for (int stage = 0; stage < kernels.Length; stage++)
                    {
                        commands.BeginSample(gpuStages[stage]);
                        commands.DispatchCompute(solver, kernels[stage],
                            stage == 0 || stage == 3 ? cellGroups : particleGroups, 1, 1);
                        commands.EndSample(gpuStages[stage]);
                    }
                }
                if (enableWhitewater)
                {
                    commands.BeginSample(gpuStages[5]);
                    commands.SetComputeIntParam(solver, "_WhitewaterStep", whitewaterStep);
                    commands.DispatchCompute(solver, clearWhitewaterCountsKernel, 1, 1, 1);
                    commands.SetBufferCounterValue(foamIndices, 0);
                    commands.SetBufferCounterValue(sprayIndices, 0);
                    commands.DispatchCompute(solver, updateWhitewaterKernel, (activeWhitewaterCapacity + 127) / 128, 1, 1);
                    commands.CopyCounterValue(foamIndices, foamArguments, sizeof(uint));
                    commands.CopyCounterValue(sprayIndices, sprayArguments, sizeof(uint));
                    commands.EndSample(gpuStages[5]);
                }
                whitewaterStep++;
                SimulatedSeconds += FixedTick;
            }
            Graphics.ExecuteCommandBuffer(commands);
        }

        internal void PrepareGridBoundary(CommandBuffer cmd, bool geometryChanged)
        {
            if (!activeCacheGridBoundary) return;
            if (gridBoundaryReady && !geometryChanged) return;
            cmd.SetComputeIntParam(solver, "_CellCount", activeCellCount);
            cmd.DispatchCompute(solver, cacheGridKernel, (activeCellCount + 127) / 128, 1, 1);
            gridBoundaryReady = true;
        }

        void ClearWhitewaterDrawCounts()
        {
            commands.SetBufferCounterValue(foamIndices, 0);
            commands.SetBufferCounterValue(sprayIndices, 0);
            commands.CopyCounterValue(foamIndices, foamArguments, sizeof(uint));
            commands.CopyCounterValue(sprayIndices, sprayArguments, sizeof(uint));
        }

        void LateUpdate()
        {
            if (!IsInitialized || !showParticles) return;
            if (renderSurface)
            {
                if (RenderingError == null && Time.frameCount > initializedFrame + 5 &&
                    Time.frameCount > lastSurfaceFrame + 5)
                    ReportSurfaceError("No active MLS-MPM Surface renderer feature on the game camera. Use Water Splash > Enable Continuous Water.");
                return;
            }
            if (drawingMaterial == null) return;
            drawProperties.SetVector("_Origin", GridOrigin);
            drawProperties.SetFloat("_CellSize", CellSize);
            drawProperties.SetFloat("_Radius", DisplayParticleRadius);
            var bounds = new Bounds(transform.position + Vector3.one * (domainSize * 0.5f),
                                    Vector3.one * (domainSize + CellSize));
            Graphics.DrawProcedural(drawingMaterial, bounds, MeshTopology.Triangles, 6, activeCount,
                null, drawProperties, ShadowCastingMode.Off, false, gameObject.layer);
        }

        internal void MarkSurfaceRendered()
        {
            lastSurfaceFrame = Time.frameCount;
            RenderingError = null;
        }

        public void SetSurfaceRendering(bool enabled)
        {
            if (renderSurface == enabled) return;
            renderSurface = enabled;
            // A returning surface pass gets a frame to render before the
            // missing-feature watchdog checks its previous render timestamp.
            lastSurfaceFrame = Time.frameCount;
            RenderingError = null;
        }

        internal void ReportSurfaceError(string message)
        {
            if (RenderingError == message) return;
            RenderingError = message;
            Debug.LogError("MLS-MPM surface: " + message, this);
        }

        void UpdateInteraction()
        {
            if (sphereObstacle == null) return;
            Camera cameraToUse = interactionCamera != null ? interactionCamera : Camera.main;
            float guiScale = GuiScale();
            bool pointerOnUi = showControls && Input.mousePosition.y > Screen.height - 230 * guiScale;
            if (Input.GetMouseButtonDown(0) && !pointerOnUi && cameraToUse != null)
            {
                dragging = true;
                dragTarget = sphereObstacle.position;
            }
            if (Input.GetMouseButtonUp(0)) dragging = false;
            if (dragging && cameraToUse != null)
            {
                var plane = new Plane(Vector3.up, sphereObstacle.position);
                Ray ray = cameraToUse.ScreenPointToRay(Input.mousePosition);
                if (plane.Raycast(ray, out float distance)) dragTarget = ray.GetPoint(distance);
                Vector3 lower = transform.position + Vector3.one * sphereRadius;
                Vector3 upper = transform.position + Vector3.one * (domainSize - sphereRadius);
                dragTarget.x = Mathf.Clamp(dragTarget.x, lower.x, upper.x);
                dragTarget.z = Mathf.Clamp(dragTarget.z, lower.z, upper.z);
            }
        }

        void UpdateSphereForTick()
        {
            if (sphereObstacle == null) return;
            if (dragging)
                sphereObstacle.position = Vector3.MoveTowards(sphereObstacle.position, dragTarget, 2 * (float)FixedTick);
            sphereVelocity = (sphereObstacle.position - previousSphere) / (float)FixedTick;
            previousSphere = sphereObstacle.position;
        }

        Vector3 SpherePosition()
        {
            return sphereObstacle == null ? Vector3.zero : (sphereObstacle.position - GridOrigin) / CellSize;
        }

        void PollDiagnostics()
        {
            if (readbackPending || Time.realtimeSinceStartup < nextReadback) return;
            nextReadback = Time.realtimeSinceStartup + 1;
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                diagnostics.GetData(diagnosticData);
                ApplyDiagnostics(diagnosticData[0], diagnosticData[1], diagnosticData[2], diagnosticData[3],
                    diagnosticData[4], diagnosticData[5]);
                return;
            }
            readbackPending = true;
            int requestGeneration = generation;
            AsyncGPUReadback.Request(diagnostics, request =>
            {
                if (this == null || generation != requestGeneration) return;
                readbackPending = false;
                if (request.hasError)
                {
                    Fail("GPU diagnostic readback failed.");
                    return;
                }
                var values = request.GetData<uint>();
                ApplyDiagnostics(values[0], values[1], values[2], values[3], values[4], values[5]);
            });
        }

        void ApplyDiagnostics(uint flags, uint density, uint speed, uint particle, uint foam, uint spray)
        {
            peakDensity = density / 1024f;
            peakSpeed = speed / 1024f * CellSize;
            foamCount = foam;
            sprayCount = spray;
            if (flags != 0)
                Fail("GPU fault " + flags + " at particle " + particle +
                     ". Bits: 1=non-finite, 2=fixed-point overflow, 4=invalid support, " +
                     "8=density >4x or invalid, 16=displacement >0.75 cell, 32=non-finite whitewater. " +
                     "Increase substeps or reduce forces, then Reset.");
        }

        void Fail(string message)
        {
            if (Error == message) return;
            Error = message;
            paused = true;
            Debug.LogError("MLS-MPM: " + message, this);
        }

        public void Kick() => kickUntil = SimulatedSeconds + 0.3;

        // Explicit benchmark/test synchronization only; never call this from the frame loop.
        // Reads 32 bytes, waiting for previously submitted simulation work to complete.
        public uint SynchronizeDiagnostics()
        {
            if (!IsInitialized) throw new InvalidOperationException("No simulation to synchronize.");
            diagnostics.GetData(diagnosticData);
            return diagnosticData[0];
        }

        // Import identical physical initial positions for solver A/B comparisons.
        // Call immediately after Initialize, before the first simulation tick.
        public void SetComparisonInitialPositions(Vector3[] worldPositions, float worldSpacing)
        {
            if (!IsInitialized || worldPositions == null || worldPositions.Length != activeCount ||
                SimulatedSeconds != 0 || !Finite(worldSpacing) || worldSpacing <= 0)
                throw new ArgumentException("Supply one valid world position per particle before stepping.");
            var initial = new ParticleState[activeCount];
            for (int i = 0; i < initial.Length; i++)
            {
                Vector3 p = (worldPositions[i] - GridOrigin) / CellSize;
                if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z) ||
                    p.x < 2 || p.y < 2 || p.z < 2 || p.x > activeResolution - 3 ||
                    p.y > activeResolution - 3 || p.z > activeResolution - 3 || !HasGridSupport(p))
                    throw new ArgumentException("Initial particle is outside the physical container.");
                initial[i].position = new Vector4(p.x, p.y, p.z, 1);
            }
            float spacingInCells = worldSpacing / CellSize;
            RestDensity = 1f / (spacingInCells * spacingInCells * spacingInCells);
            UploadParticles(initial);
        }

        void UploadParticles(ParticleState[] states)
        {
            if (states.Length != activeCount) throw new ArgumentException("Supply exactly one state per particle.");
            var data = new Vector4[activeCount * 5];
            for (int i = 0; i < activeCount; i++)
            {
                int start = activeParticleSoA ? i : i * 5;
                int stride = activeParticleSoA ? activeCount : 1;
                data[start] = states[i].position;
                data[start + stride] = states[i].velocity;
                data[start + stride * 2] = states[i].affineX;
                data[start + stride * 3] = states[i].affineY;
                data[start + stride * 4] = states[i].affineZ;
            }
            particles.SetData(data);
        }

        // Explicit synchronous inspection only; rendering/probes consume the streams directly.
        public ParticleState[] CaptureParticleStates()
        {
            if (!IsInitialized) throw new InvalidOperationException("No simulation to capture.");
            var data = new Vector4[activeCount * 5];
            particles.GetData(data);
            var states = new ParticleState[activeCount];
            for (int i = 0; i < activeCount; i++)
            {
                int start = activeParticleSoA ? i : i * 5;
                int stride = activeParticleSoA ? activeCount : 1;
                states[i] = new ParticleState {
                    position = data[start], velocity = data[start + stride],
                    affineX = data[start + stride * 2], affineY = data[start + stride * 3],
                    affineZ = data[start + stride * 4]
                };
            }
            return states;
        }

        public static int RecommendedSubsteps(int resolution)
        {
            return resolution <= 32 ? 4 : resolution <= 48 ? 8 : resolution <= 64 ? 12 : 16;
        }

        public void SelectPreset(int count)
        {
            particleCount = count;
            gridResolution = PresetGridResolution(count);
            substeps = RecommendedSubsteps(gridResolution);
            whitewaterCapacity = Mathf.Min(count, 32768);
            Initialize();
        }

        public void SelectExperimental32K()
        {
            particleCount = 32768;
            gridResolution = 48;
            substeps = 4;
            whitewaterCapacity = 32768;
            Initialize();
        }

        public static int PresetGridResolution(int count) => count <= 16384 ? 32 :
            count <= 32768 ? 48 : count <= 65536 ? 64 : 80;

        bool HasGridSupport(Vector3 position)
        {
            Vector3Int lower = Vector3Int.FloorToInt(position - Vector3.one * .5f);
            Vector3Int upper = lower + Vector3Int.one * 2;
            return activeGridWindow.Contains(lower) && activeGridWindow.Contains(upper);
        }

        public WhitewaterSnapshot CaptureWhitewaterSnapshot()
        {
            if (!IsInitialized) throw new InvalidOperationException("No simulation to capture.");
            var result = new WhitewaterSnapshot { states = new WhitewaterState[activeWhitewaterCapacity] };
            whitewater.GetData(result.states);
            diagnostics.GetData(diagnosticData);
            result.born = diagnosticData[6];
            result.expired = diagnosticData[7];
            Vector3 sphere = SpherePosition();
            float radius = sphereRadius / CellSize;
            foreach (WhitewaterState state in result.states)
            {
                if (!Finite(state.position) || !Finite(state.velocity) || !Finite(state.normalType))
                    result.nonFinite++;
                if (state.position.w <= 0) continue;
                if (state.normalType.w > 0.5f) result.foam++; else result.spray++;
                Vector3 p = state.position;
                if (p.x < 1.999f || p.y < 1.999f || p.z < 1.999f ||
                    p.x > activeResolution - 2.999f || p.y > activeResolution - 2.999f ||
                    p.z > activeResolution - 2.999f || !HasGridSupport(p)) result.outside++;
                if (sphereObstacle != null && (p - sphere).sqrMagnitude < (radius - 0.001f) * (radius - 0.001f))
                    result.insideSphere++;
            }
            return result;
        }

        public Snapshot CaptureSnapshot()
        {
            if (!IsInitialized) throw new InvalidOperationException("No simulation to capture.");
            var states = CaptureParticleStates();
            var cells = new Vector4[activeCellCount];
            grid.GetData(cells);
            diagnostics.GetData(diagnosticData);
            var result = new Snapshot {
                particleCount = activeCount, faults = diagnosticData[0],
                peakDensityRatio = diagnosticData[1] / 1024f,
                simulatedSeconds = SimulatedSeconds,
                minimumVolumeRatio = solverMethod == SolverMethod.PositionBasedMpm ? float.PositiveInfinity : 0,
                minimum = Vector3.one * float.PositiveInfinity,
                maximum = Vector3.one * float.NegativeInfinity
            };
            Vector3 sphere = SpherePosition();
            float radius = sphereRadius / CellSize;
            var heights = new float[activeCount];
            int heightIndex = 0;
            foreach (ParticleState state in states)
            {
                if (solverMethod == SolverMethod.PositionBasedMpm)
                {
                    float volume = state.affineX.w == 0 ? 1 : state.affineX.w;
                    result.minimumVolumeRatio = Mathf.Min(result.minimumVolumeRatio, volume);
                    result.maximumVolumeRatio = Mathf.Max(result.maximumVolumeRatio, volume);
                    if (volume <= 0.10001f) result.flooredVolumeParticles++;
                }
                Vector3 p = state.position;
                Vector3 v = state.velocity;
                if (!Finite(state.position) || !Finite(state.velocity) || !Finite(state.affineX) ||
                    !Finite(state.affineY) || !Finite(state.affineZ)) result.nonFiniteParticles++;
                if (p.x < 1.999f || p.y < 1.999f || p.z < 1.999f ||
                    p.x > activeResolution - 2.999f || p.y > activeResolution - 2.999f ||
                    p.z > activeResolution - 2.999f || !HasGridSupport(p)) result.outsideParticles++;
                if (sphereObstacle != null && (p - sphere).sqrMagnitude < (radius - 0.001f) * (radius - 0.001f))
                    result.insideSphereParticles++;
                result.center += p / activeCount;
                result.minimum = Vector3.Min(result.minimum, p);
                result.maximum = Vector3.Max(result.maximum, p);
                result.maximumSpeed = Mathf.Max(result.maximumSpeed, v.magnitude * CellSize);
                float worldY = GridOrigin.y + p.y * CellSize;
                heights[heightIndex++] = worldY;
                result.meanKineticEnergy += .5 * v.sqrMagnitude * CellSize * CellSize / activeCount;
                double affineNorm = ((Vector3)state.affineX).sqrMagnitude + ((Vector3)state.affineY).sqrMagnitude +
                    ((Vector3)state.affineZ).sqrMagnitude;
                result.meanAffineKineticEnergy += .125 * affineNorm * CellSize * CellSize / activeCount;
                result.meanPotentialEnergy += -gravity.y * (worldY - transform.position.y) / activeCount;
                double ratio = Math.Max(1, state.velocity.w);
                double stored = pressureExponent == 1 ? Math.Log(ratio) + 1 / ratio - 1 :
                    (Math.Pow(ratio, pressureExponent - 1) - 1) / (pressureExponent - 1) + 1 / ratio - 1;
                if (solverMethod == SolverMethod.MlsMpm)
                    result.meanCompressionEnergy += soundSpeed * soundSpeed / pressureExponent * stored / activeCount;
            }
            Array.Sort(heights);
            result.maximumWorldHeight = heights[heights.Length - 1];
            result.p99WorldHeight = heights[(int)Math.Floor((heights.Length - 1) * .99)];
            foreach (Vector4 cell in cells) result.gridMass += cell.w;
            return result;
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        static float GuiScale() => Mathf.Max(0.45f, Mathf.Min(Screen.width / 1000f, Screen.height / 650f));

        void OnGUI()
        {
            if (!showControls) return;
            Matrix4x4 previous = GUI.matrix;
            float scale = GuiScale();
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            GUILayout.BeginArea(new Rect(12, 12, Screen.width / scale - 24, 215), GUI.skin.box);
            GUILayout.Label((solverMethod == SolverMethod.MlsMpm ? "Weakly compressible MLS-MPM" : "PB-MPM | " + positionIterations + " iterations") +
                " | " + SystemInfo.graphicsDeviceType + " | " + SystemInfo.graphicsDeviceName);
            GUILayout.Label(string.Format("{0:N0} particles | {1} grid | {2} substeps | frame {3:F1} ms ({4:F0} FPS)",
                activeCount, activeGridWindow.size, substeps, averageFrame * 1000, averageFrame > 0 ? 1 / averageFrame : 0));
            GUILayout.Label(string.Format("Simulated {0:F2}s | backlog {1:F2}s | peak density {2:F2}x | peak speed {3:F2}m/s",
                SimulatedSeconds, accumulator, peakDensity, peakSpeed));
            GUILayout.Label(enableWhitewater ?
                string.Format("Whitewater: {0:N0} foam / {1:N0} spray | bounded pool {2:N0} (counts update about once/second)",
                    foamCount, sprayCount, activeWhitewaterCapacity) : "Whitewater simulation and rendering OFF");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(paused ? "Run" : "Pause", GUILayout.Height(30))) paused = !paused;
            if (GUILayout.Button("Reset", GUILayout.Height(30))) Initialize();
            if (GUILayout.Button(solverMethod == SolverMethod.MlsMpm ? "Try PB-MPM" : "Try MLS-MPM", GUILayout.Height(30)))
            {
                solverMethod = solverMethod == SolverMethod.MlsMpm ? SolverMethod.PositionBasedMpm : SolverMethod.MlsMpm;
                Initialize();
            }
            if (GUILayout.Button("Push", GUILayout.Height(30))) Kick();
            if (GUILayout.Button(showParticles ? "Rendering ON" : "Rendering OFF", GUILayout.Height(30)))
                showParticles = !showParticles;
            if (GUILayout.Button(renderSurface ? "Surface" : "Particles", GUILayout.Height(30)))
            {
                SetSurfaceRendering(!renderSurface);
            }
            if (renderSurface && GUILayout.Button("Water: " + surfaceQuality, GUILayout.Height(30)))
                surfaceQuality = (SurfaceQuality)(((int)surfaceQuality + 1) % 3);
            if (GUILayout.Button(enableWhitewater ? "Whitewater ON" : "Whitewater OFF", GUILayout.Height(30)))
                enableWhitewater = !enableWhitewater;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            foreach (int count in new[] { 8192, 16384, 32768, 65536, 131072 })
                if (GUILayout.Button((count / 1024) + "K / " + PresetGridResolution(count) + "^3",
                    GUILayout.Height(28))) SelectPreset(count);
            if (GUILayout.Button("32K / 4 steps*", GUILayout.Height(28))) SelectExperimental32K();
            GUILayout.EndHorizontal();
            GUILayout.Label(Error ?? RenderingError ?? (accumulator > 0.5 ?
                "BEHIND REAL TIME: no time dropped. Reduce preset. Frame time is NOT solver GPU time." :
                particleCount == 32768 && substeps == 4 ?
                "*Experimental: tested for a 10s impact scenario only; watch density, faults and backlog. 32K restores 8 steps." :
                "Drag below this panel to move the sphere. Space: pause; R: reset. Surface is screen-space, not a mesh."));
            GUILayout.EndArea();
            GUI.matrix = previous;
        }

        void OnApplicationPause(bool value)
        {
            applicationPaused = value;
            skipNextDelta = true;
        }

        public void Release()
        {
            generation++;
            readbackPending = false;
            particles?.Release();
            accumulation?.Release();
            grid?.Release();
            diagnostics?.Release();
            whitewater?.Release();
            foamIndices?.Release();
            sprayIndices?.Release();
            foamArguments?.Release();
            sprayArguments?.Release();
            commands?.Release();
            sortKeys?.Release();
            sortRanks?.Release();
            sortBins?.Release();
            sortedParticles?.Release();
            staticGridBoundary?.Release();
            staticGridBoundary = null;
            gridBoundaryReady = false;
            sortKeys = sortRanks = sortBins = sortedParticles = null;
            particles = null;
            accumulation = null;
            grid = null;
            diagnostics = null;
            whitewater = null;
            foamIndices = null;
            sprayIndices = null;
            foamArguments = null;
            sprayArguments = null;
            commands = null;
            if (solver != null)
            {
                if (Application.isPlaying) Destroy(solver); else DestroyImmediate(solver);
                solver = null;
            }
            if (drawingMaterial != null)
            {
                if (Application.isPlaying) Destroy(drawingMaterial); else DestroyImmediate(drawingMaterial);
                drawingMaterial = null;
            }
        }
    }
}
