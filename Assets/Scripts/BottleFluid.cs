using UnityEngine;
using UnityEngine.Rendering;

public class BottleFluid : MonoBehaviour
{
    public Material particleMaterial;
    public BottleBoundary boundary;
    public ComputeShader simulation;

    const float cellSize = 4f / 75, deltaTime = 1f / 60;
    const int substeps = 8, cellCount = 80 * 32 * 30;
    static readonly Vector3 gridOrigin = new Vector3(-2, 0, -2) - Vector3.one * (2 * cellSize);
    static readonly Vector3Int gridSize = new Vector3Int(80, 32, 30), gridOffset = new Vector3Int(0, 4, 25);
    static readonly string[] kernelNames = { "CacheBoundary", "ClearGrid", "P2G", "UpdateGrid", "G2P" };

    readonly int[] kernels = new int[5];
    ComputeBuffer particleBuf, accumulationBuf, gridBuf, boundaryBuf;
    Material material;
    ComputeShader solver;
    CommandBuffer commands;
    int particleCount;
    float radius, backlog;

    void Awake() => material = new Material(particleMaterial);

    public void Init(int cnt)
    {
        Release();
        particleCount = cnt; backlog = 0;

        var positions = boundary.CreateFill(particleCount, out float spacing);
        radius = spacing * .34f;
        var data = new Vector4[particleCount * 5];
        for (int i = 0; i < particleCount; i++)
        {
            Vector3 world = boundary.FromProfile(positions[i]);
            Vector3 p = (world - gridOrigin) / cellSize;
            data[i] = new Vector4(p.x, p.y, p.z, 1);
        }

        particleBuf = new ComputeBuffer(data.Length, 16); // SoA: positions, velocities/density ratios, then the three affine rows.
        particleBuf.SetData(data);
        accumulationBuf = new ComputeBuffer(cellCount * 2, 4);
        gridBuf = new ComputeBuffer(cellCount, 16);
        boundaryBuf = new ComputeBuffer(cellCount, 16);

        solver = Instantiate(simulation);
        for (int i = 0; i < kernels.Length; i++)
        {
            int k = kernels[i] = solver.FindKernel(kernelNames[i]);
            solver.SetBuffer(k, "_Particles", particleBuf);
            solver.SetBuffer(k, "_Accumulation", accumulationBuf);
            solver.SetBuffer(k, "_Grid", gridBuf);
            solver.SetBuffer(k, "_StaticGridBoundary", boundaryBuf);
        }

        float restDensity = particleCount * cellSize * cellSize * cellSize / boundary.waterVolume;
        float soundSpeed = 6 / cellSize;
        solver.SetInt("_ParticleCount", particleCount);
        solver.SetInt("_ParticlePlaneStride", particleCount);
        solver.SetInt("_CellCount", cellCount);
        solver.SetInt("_Resolution", 80);
        solver.SetInts("_GridSize", gridSize.x, gridSize.y, gridSize.z);
        solver.SetInts("_GridOffset", gridOffset.x, gridOffset.y, gridOffset.z);
        solver.SetFloat("_Dt", deltaTime / substeps);
        solver.SetFloat("_RestDensity", restDensity);
        solver.SetFloat("_BulkModulus", restDensity * soundSpeed * soundSpeed / 3);
        solver.SetFloat("_PressureExponent", 3);
        solver.SetFloat("_Viscosity", .15f);
        solver.SetVector("_Acceleration", Physics.gravity / cellSize);

        var profile = new Vector4[BottleBoundary.maxProfileCount];
        var points = boundary.profile;
        for (int i = 0; i < points.Length; i++) profile[i] = points[i] / cellSize;
        solver.SetVectorArray("_BottleProfile", profile);
        solver.SetInt("_BottleProfileCount", points.Length);
        solver.SetVector("_BottleCenter", (boundary.center - gridOrigin) / cellSize);
        solver.SetFloat("_BottleClearance", .75f * spacing / cellSize);
        solver.Dispatch(kernels[0], (cellCount + 127) / 128, 1, 1);

        commands = new CommandBuffer { name = "Bottle MLS-MPM" };
        for (int step = 0; step < substeps; step++)
        {
            commands.DispatchCompute(solver, kernels[1], (cellCount + 127) / 128, 1, 1);
            commands.DispatchCompute(solver, kernels[2], (particleCount + 127) / 128, 1, 1);
            commands.DispatchCompute(solver, kernels[3], (cellCount + 127) / 128, 1, 1);
            commands.DispatchCompute(solver, kernels[4], (particleCount + 127) / 128, 1, 1);
        }
        material.SetBuffer("_Particles", particleBuf);
        material.SetVector("_Origin", gridOrigin);
        material.SetFloat("_CellSize", cellSize);
        material.SetFloat("_Radius", radius);
    }

    void Update()
    {
        if (commands == null) return;
        backlog += Time.deltaTime;
        int ticks = Mathf.Min(4, (int)(backlog / deltaTime));
        for (int i = 0; i < ticks; i++)
        {
            Graphics.ExecuteCommandBuffer(commands);
            backlog -= deltaTime;
        }
    }

    void LateUpdate()
    {
        if (particleBuf == null) return;
        Bounds drawBounds = boundary.bounds;
        drawBounds.Expand(2 * radius);
        Graphics.DrawProcedural(material, drawBounds, MeshTopology.Triangles, 6, particleCount, null, null, ShadowCastingMode.Off, false, gameObject.layer);
    }

    void Release()
    {
        particleBuf?.Release();
        accumulationBuf?.Release();
        gridBuf?.Release();
        boundaryBuf?.Release();
        particleBuf = null;
        commands?.Release();
        commands = null;
        if (solver != null) Destroy(solver);
    }

    void OnDestroy()
    {
        Release();
        Destroy(material);
    }
}
