using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class BottleFluid : MonoBehaviour
{
    public Material particleMaterial;
    public BottleBoundary boundary;
    public ComputeShader simulation;
    public bool renderSurface = true;
    public static BottleFluid Instance;
    [System.NonSerialized] public ComputeBuffer particles, whitewater, foamIndices, sprayIndices, foamArgs, sprayArgs;
    [System.NonSerialized] public int particleCount;
    [System.NonSerialized] public Vector4 waterSample; // height, flow xz, valid
    [System.NonSerialized] public Vector3 waterNormal = Vector3.up;

    const float cellSize = 4f / 75, tickDt = 1f / 60;
    const int substeps = 8, cellCount = 80 * 32 * 30, whitewaterCount = 8192;
    static readonly Vector3 gridOrigin = new Vector3(-2, 0, -2) - Vector3.one * (2 * cellSize);
    // Kernels in declaration order
    const int cacheBoundary = 0, clearGrid = 1, p2g = 2, updateGrid = 3, g2p = 4, probeBoat = 5, updateWhitewater = 6;

    readonly List<ComputeBuffer> buffers = new();
    readonly Vector4[] probes = new Vector4[5]; // center, front, back, left, right
    ComputeBuffer boatSample;
    Vector3 boatA, boatB, lastCenter, lastAxis;
    float boatRadius, backlog, nextProbe;
    int step;

    void Awake() => Instance = this;
    void OnDestroy() => Release();

    static Vector3 Grid(Vector3 world) => (world - gridOrigin) / cellSize;

    public void SetBoat(Vector3 a, Vector3 b, float radius, Vector3 center, Vector3 front, Vector3 right)
    {
        boatA = a; boatB = b; boatRadius = radius;
        probes[0] = Grid(center); probes[1] = Grid(center + front); probes[2] = Grid(center - front); probes[3] = Grid(center - right); probes[4] = Grid(center + right);
    }

    public void Init(int count)
    {
        Release();
        particleCount = count;
        var fill = boundary.CreateFill(count);
        var data = new Vector4[count * 5]; // SoA: position, velocity + density ratio, affine rows
        for (int i = 0; i < count; i++) { data[i] = Grid(fill[i]); data[i + count].w = 1; }
        particles = Buffer(data.Length, 16); particles.SetData(data);
        whitewater = Buffer(whitewaterCount, 48); whitewater.SetData(new float[whitewaterCount * 12]);
        foamIndices = Buffer(whitewaterCount, 4, ComputeBufferType.Append); sprayIndices = Buffer(whitewaterCount, 4, ComputeBufferType.Append);
        foamArgs = Buffer(4, 4, ComputeBufferType.IndirectArguments); foamArgs.SetData(new uint[] { 6, 0, 0, 0 });
        sprayArgs = Buffer(4, 4, ComputeBufferType.IndirectArguments); sprayArgs.SetData(new uint[] { 6, 0, 0, 0 });
        boatSample = Buffer(5, 16);
        var names = new[] { "_Particles", "_Accumulation", "_Grid", "_Boundary", "_BoatSample", "_Whitewater", "_FoamIndices", "_SprayIndices" };
        var values = new[] { particles, Buffer(cellCount * 2, 4), Buffer(cellCount, 16), Buffer(cellCount, 16), boatSample, whitewater, foamIndices, sprayIndices };
        for (int k = 0; k <= updateWhitewater; k++) for (int i = 0; i < names.Length; i++) simulation.SetBuffer(k, names[i], values[i]);

        var profile = new Vector4[32];
        for (int i = 0; i < boundary.profile.Length; i++) profile[i] = boundary.profile[i] / cellSize;
        simulation.SetVectorArray("_Profile", profile);
        simulation.SetInt("_ProfileCount", boundary.profile.Length);
        simulation.SetVector("_BottleCenter", Grid(boundary.center));
        simulation.SetInt("_ParticleCount", count);
        simulation.SetFloat("_RestDensity", count * cellSize * cellSize * cellSize / boundary.waterVolume);
        Dispatch(cacheBoundary, cellCount);

        Shader.SetGlobalBuffer("_Particles", particles); Shader.SetGlobalBuffer("_Whitewater", whitewater);
        Shader.SetGlobalInteger("_ParticleCount", count);
        Shader.SetGlobalVector("_Origin", gridOrigin);
        Shader.SetGlobalFloat("_CellSize", cellSize);
        Shader.SetGlobalFloat("_FluidRadius", .95f * Mathf.Pow(boundary.waterVolume / count, 1f / 3));
        lastCenter = (boatA + boatB) * .5f; lastAxis = boatB - boatA; backlog = 0;
    }

    void Update()
    {
        for (backlog = Mathf.Min(backlog + Time.deltaTime, 4 * tickDt); backlog >= tickDt; backlog -= tickDt) Tick();
        if (Time.time < nextProbe) return;
        nextProbe = Time.time + .1f;
        simulation.SetVectorArray("_BoatProbes", probes);
        simulation.Dispatch(probeBoat, 5, 1, 1);
        AsyncGPUReadback.Request(boatSample, r =>
        {
            if (r.hasError) return;
            var s = r.GetData<Vector4>();
            Vector4 sum = Vector4.zero;
            foreach (var v in s) if (v.w > 0) sum += v;
            waterSample = sum.w > 0 ? sum / sum.w : Vector4.zero;
            if (s[0].w > 0) waterSample.x = s[0].x; // height under the centre, averaged flow
            waterNormal = Vector3.up;
            if (s[1].w * s[2].w * s[3].w * s[4].w == 0) return;
            Vector3 forward = (probes[1] - probes[2]) * cellSize, right = (probes[4] - probes[3]) * cellSize;
            forward.y = s[1].x - s[2].x; right.y = s[4].x - s[3].x;
            waterNormal = Vector3.Cross(forward, right).normalized;
        });
    }

    void Tick()
    {
        Vector3 center = (boatA + boatB) * .5f, axis = boatB - boatA;
        Quaternion.FromToRotation(lastAxis, axis).ToAngleAxis(out float angle, out Vector3 spin);
        Vector4 a = Grid(boatA); a.w = boatRadius / cellSize;
        simulation.SetVector("_BoatA", a); simulation.SetVector("_BoatB", Grid(boatB));
        simulation.SetVector("_BoatVelocity", (center - lastCenter) / (tickDt * cellSize));
        simulation.SetVector("_BoatAngularVelocity", spin * (angle * Mathf.Deg2Rad / tickDt));
        for (int i = 0; i < substeps; i++) { Dispatch(clearGrid, cellCount); Dispatch(p2g, particleCount); Dispatch(updateGrid, cellCount); Dispatch(g2p, particleCount); }
        simulation.SetInt("_Step", step++);
        foamIndices.SetCounterValue(0); sprayIndices.SetCounterValue(0);
        Dispatch(updateWhitewater, whitewaterCount);
        ComputeBuffer.CopyCount(foamIndices, foamArgs, 4); ComputeBuffer.CopyCount(sprayIndices, sprayArgs, 4);
        lastCenter = center; lastAxis = axis;
    }

    void Dispatch(int kernel, int count) => simulation.Dispatch(kernel, count / 128, 1, 1);

    ComputeBuffer Buffer(int count, int stride, ComputeBufferType type = ComputeBufferType.Default)
    {
        var buffer = new ComputeBuffer(count, stride, type);
        buffers.Add(buffer);
        return buffer;
    }

    void Release() { foreach (var b in buffers) b.Release(); buffers.Clear(); particles = null; }
}
