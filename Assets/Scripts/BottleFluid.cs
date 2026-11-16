using UnityEngine;
using UnityEngine.Rendering;

public class BottleFluid : MonoBehaviour
{
    public Material particleMaterial;
    public BottleBoundary boundary;
    public ComputeShader simulation;

    const float CellSize = 4f / 75;
    const int Substeps = 8, CellCount = 80 * 32 * 30;
    static readonly Vector3 GridOrigin = new Vector3(-2, 0, -2) - Vector3.one * (2 * CellSize);
    static readonly Vector3Int GridSize = new Vector3Int(80, 32, 30), GridOffset = new Vector3Int(0, 4, 25);
    static readonly string[] KernelNames = { "CacheBoundary", "ClearGrid", "P2G", "UpdateGrid", "G2P" };

    readonly int[] kernels = new int[5];
    ComputeBuffer particleBuf, accumulationBuf, gridBuf, boundaryBuf;
    Material material;
    ComputeShader solver;
    CommandBuffer commands;
    int particleCount;
    float radius, backlog;

    void Awake() => material = new Material(particleMaterial);

    public void Init(int tempCount)
    {
        Release();
        particleCount = tempCount;
        backlog = 0;

        var positions = boundary.CreateFill(particleCount, out float spacing);
        radius = spacing * .34f;
        var data = new Vector4[particleCount * 5];
        for (int i = 0; i < particleCount; i++)
        {
            Vector3 world = boundary.Center + (Vector3)positions[i];
            Vector3 p = (world - GridOrigin) / CellSize;
            data[i] = new Vector4(p.x, p.y, p.z, 1);
        }

        particleBuf = new ComputeBuffer(data.Length, 16); // SoA: positions, velocities/density ratios, then the three affine rows.
        particleBuf.SetData(data);
        accumulationBuf = new ComputeBuffer(CellCount * 2, 4);
        gridBuf = new ComputeBuffer(CellCount, 16);
        boundaryBuf = new ComputeBuffer(CellCount, 16);

        solver = Instantiate(simulation);
        for (int i = 0; i < kernels.Length; i++)
        {
            int k = kernels[i] = solver.FindKernel(KernelNames[i]);
            solver.SetBuffer(k, "_Particles", particleBuf);
            solver.SetBuffer(k, "_Accumulation", accumulationBuf);
            solver.SetBuffer(k, "_Grid", gridBuf);
            solver.SetBuffer(k, "_StaticGridBoundary", boundaryBuf);
        }

        float restDensity = particleCount * CellSize * CellSize * CellSize / boundary.WaterVolume;
        float soundSpeed = 6 / CellSize;
        solver.SetInt("_ParticleCount", particleCount);
        solver.SetInt("_ParticlePlaneStride", particleCount);
        solver.SetInt("_CellCount", CellCount);
        solver.SetInt("_Resolution", 80);
        solver.SetInts("_GridSize", GridSize.x, GridSize.y, GridSize.z);
        solver.SetInts("_GridOffset", GridOffset.x, GridOffset.y, GridOffset.z);
        solver.SetFloat("_Dt", Time.fixedDeltaTime / Substeps);
        solver.SetFloat("_RestDensity", restDensity);
        solver.SetFloat("_BulkModulus", restDensity * soundSpeed * soundSpeed / 3);
        solver.SetFloat("_PressureExponent", 3);
        solver.SetFloat("_Viscosity", .15f);
        solver.SetVector("_Acceleration", Physics.gravity / CellSize);

        var profile = new Vector4[32];
        for (int i = 0; i < BottleBoundary.Profile.Length; i++) profile[i] = BottleBoundary.Profile[i] / CellSize;
        solver.SetVectorArray("_BottleProfile", profile);
        solver.SetInt("_BottleProfileCount", BottleBoundary.Profile.Length);
        solver.SetVector("_BottleCenter", (boundary.Center - GridOrigin) / CellSize);
        solver.SetFloat("_BottleClearance", .75f * spacing / CellSize);
        solver.Dispatch(kernels[0], (CellCount + 127) / 128, 1, 1);

        commands = new CommandBuffer { name = "Bottle MLS-MPM" };
        for (int step = 0; step < Substeps; step++)
        {
            commands.DispatchCompute(solver, kernels[1], (CellCount + 127) / 128, 1, 1);
            commands.DispatchCompute(solver, kernels[2], (particleCount + 127) / 128, 1, 1);
            commands.DispatchCompute(solver, kernels[3], (CellCount + 127) / 128, 1, 1);
            commands.DispatchCompute(solver, kernels[4], (particleCount + 127) / 128, 1, 1);
        }
        material.SetBuffer("_Particles", particleBuf);
        material.SetVector("_Origin", GridOrigin);
        material.SetFloat("_CellSize", CellSize);
        material.SetFloat("_Radius", radius);
    }

    void Update()
    {
        backlog += Time.deltaTime;
        int ticks = Mathf.Min(4, (int)(backlog / Time.fixedDeltaTime));
        for (int i = 0; i < ticks; i++)
        {
            Graphics.ExecuteCommandBuffer(commands);
            backlog -= Time.fixedDeltaTime;
        }
    }

    void LateUpdate()
    {
        Graphics.DrawProcedural(material, boundary.Bounds, MeshTopology.Triangles, 6, particleCount, null, null, ShadowCastingMode.Off, false, gameObject.layer);
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
