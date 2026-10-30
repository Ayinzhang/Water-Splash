using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WaterSplash
{
    // The glass mesh, initial fill and GPU boundary use this same axial profile.
    // Bottle axis is world X; the simulation grid remains unrotated.
    [DefaultExecutionOrder(-140)]
    public sealed class BottleBoundary : MonoBehaviour
    {
        public MlsMpmFluid fluid;
        public Vector3 center = new Vector3(0, .91f, 0);
        public float fillHeight = .73f;
        public bool mouthFacesLeft;
        [Tooltip("Omit empty grid slabs without changing cell size or world coordinates. Reset after changing.")]
        public bool compactGrid = true;
        public float AxisSign => mouthFacesLeft ? -1 : 1;
        public float ParticleSpacing { get; private set; }
        public float WaterVolume { get; private set; }
        public static readonly Vector2[] Profile = {
            new Vector2(-1.88f,.07f), new Vector2(-1.85f,.30f),
            new Vector2(-1.78f,.51f), new Vector2(-1.66f,.65f),
            new Vector2(-1.50f,.72f), new Vector2(-1.28f,.74f),
            new Vector2(.40f,.74f), new Vector2(.59f,.72f),
            new Vector2(.76f,.66f), new Vector2(.91f,.56f),
            new Vector2(1.04f,.43f), new Vector2(1.15f,.32f),
            new Vector2(1.27f,.26f), new Vector2(1.40f,.24f),
            new Vector2(1.72f,.24f), new Vector2(1.75f,.24f)
        };
        readonly Vector4[] gpuProfile = new Vector4[32];
        Vector3 lastGpuCenter;
        float lastGpuAxis;

        void Awake()
        {
            if (fluid != null)
            {
                ConfigureGridWindow();
                fluid.configureSimulation += Configure;
            }
        }
        void OnDestroy()
        {
            if (fluid != null) fluid.configureSimulation -= Configure;
        }

        public void ConfigureGridWindow()
        {
            if (fluid == null) throw new InvalidOperationException("Assign the bottle fluid before configuring its grid.");
            fluid.gridWindow = null;
            if (!compactGrid) return;
            // Keep the exact original lattice and full X extent. Only empty Y/Z slabs
            // are omitted, with room for transfer support and whitewater gradient probes.
            float radius = 0;
            foreach (Vector2 section in Profile) radius = Mathf.Max(radius, section.y);
            Vector3 minimum = (center - new Vector3(0, radius, radius) - fluid.GridOrigin) / fluid.CellSize;
            Vector3 maximum = (center + new Vector3(0, radius, radius) - fluid.GridOrigin) / fluid.CellSize;
            Vector3Int lower = Vector3Int.Max(Vector3Int.zero, Vector3Int.FloorToInt(minimum) - Vector3Int.one * 4);
            Vector3Int upper = Vector3Int.Min(Vector3Int.one * fluid.gridResolution,
                Vector3Int.CeilToInt(maximum) + Vector3Int.one * 5);
            lower = new Vector3Int(lower.x / 4 * 4, lower.y / 4 * 4, lower.z / 4 * 4);
            upper = Vector3Int.Min(Vector3Int.one * fluid.gridResolution,
                new Vector3Int((upper.x + 3) / 4 * 4, (upper.y + 3) / 4 * 4, (upper.z + 3) / 4 * 4));
            lower.x = 0;
            upper.x = fluid.gridResolution;
            fluid.gridWindow = new BoundsInt(lower, upper - lower);
        }
        void Configure(CommandBuffer commands, ComputeShader shader)
        {
            float h = fluid.CellSize;
            Vector3 gpuCenter = (center - fluid.GridOrigin) / h;
            bool geometryChanged = !gpuCenter.Equals(lastGpuCenter) || AxisSign != lastGpuAxis;
            for (int i = 0; i < Profile.Length; i++)
            {
                var section = new Vector4(Profile[i].x / h, Profile[i].y / h, 0, 0);
                geometryChanged |= !section.Equals(gpuProfile[i]);
                gpuProfile[i] = section;
            }
            commands.SetComputeIntParam(shader, "_BottleProfileCount", Profile.Length);
            commands.SetComputeVectorArrayParam(shader, "_BottleProfile", gpuProfile);
            commands.SetComputeVectorParam(shader, "_BottleCenter", gpuCenter);
            commands.SetComputeFloatParam(shader, "_BottleClearance", .75f * ParticleSpacing / h);
            commands.SetComputeFloatParam(shader, "_BottleAxisSign", AxisSign);
            fluid.PrepareGridBoundary(commands, geometryChanged);
            lastGpuCenter = gpuCenter;
            lastGpuAxis = AxisSign;
        }

        public static float Radius(float x, out float slope)
        {
            for (int i = 0; i < Profile.Length - 1; i++)
            {
                Vector2 a = Profile[i], b = Profile[i + 1];
                if (x <= b.x || i == Profile.Length - 2)
                {
                    slope = (b.y - a.y) / (b.x - a.x);
                    return Mathf.LerpUnclamped(a.y, b.y, (Mathf.Clamp(x, a.x, b.x) - a.x) / (b.x - a.x));
                }
            }
            slope = 0; return Profile[0].y;
        }
        public static float Distance(Vector3 point, out Vector3 normal)
        {
            float radius = Radius(point.x, out float slope);
            float radial = new Vector2(point.y, point.z).magnitude;
            float norm = Mathf.Sqrt(1 + slope * slope);
            float distance = (radial - radius) / norm;
            normal = new Vector3(-slope, radial > 1e-7f ? point.y / radial : 1,
                radial > 1e-7f ? point.z / radial : 0) / norm;
            float back = Profile[0].x - point.x;
            float front = point.x - Profile[Profile.Length - 1].x;
            if (back > distance) { distance = back; normal = Vector3.left; }
            if (front > distance) { distance = front; normal = Vector3.right; }
            return distance;
        }
        public Vector3 ClampInside(Vector3 world, float margin)
        {
            Vector3 local = ToProfile(world);
            for (int i = 0; i < 8; i++)
            {
                float distance = Distance(local, out Vector3 normal) + margin;
                if (distance <= 0) break;
                local -= normal * (distance + 1e-5f);
            }
            return FromProfile(local);
        }

        public Vector3 ToProfile(Vector3 world)
        {
            Vector3 local = world - center;
            local.x *= AxisSign;
            return local;
        }

        public Vector3 FromProfile(Vector3 local)
        {
            local.x *= AxisSign;
            return local + center;
        }

        public Vector3[] CreateInitialFill(int count, out float spacing)
        {
            if (count < 512 || count > 262144) throw new ArgumentOutOfRangeException(nameof(count));
            float referenceSpacing = FindFillSpacing(32768);
            float latticeSpacing = count == 32768 ? referenceSpacing : FindFillSpacing(count);
            // Particle mass/volume follows one fixed 32K reference, independently
            // of small lattice-count rounding differences between presets.
            spacing = referenceSpacing * Mathf.Pow(32768f / count, 1f / 3);
            var candidates = new List<Vector3>();
            Fill(latticeSpacing, candidates);
            if (candidates.Count < count) throw new InvalidOperationException("Bottle fill cannot fit the requested particles.");
            var positions = new Vector3[count];
            // Distribute any omitted lattice sites instead of truncating one end of the bottle.
            for (int i = 0; i < count; i++) positions[i] = candidates[(int)((long)i * candidates.Count / count)];
            ParticleSpacing = spacing;
            WaterVolume = count * spacing * spacing * spacing;
            return positions;
        }

        float FindFillSpacing(int count)
        {
            float lo = .015f, hi = .075f;
            for (int i = 0; i < 19; i++)
            {
                float mid = (lo + hi) * .5f;
                if (Fill(mid, null) >= count) lo = mid; else hi = mid;
            }
            return lo;
        }
        int Fill(float spacing, List<Vector3> output)
        {
            int count = 0;
            float margin = spacing * .85f;
            for (int y = 0; ; y++)
            {
                float py = center.y - .74f + margin + y * spacing;
                if (py > fillHeight) break;
                for (int z = 0; ; z++)
                {
                    float pz = -.74f + margin + z * spacing;
                    if (pz > .74f - margin) break;
                    for (int x = 0; ; x++)
                    {
                        float px = Profile[0].x + margin + x * spacing;
                        if (px > Profile[Profile.Length - 1].x - margin) break;
                        Vector3 p = new Vector3(px, py - center.y, pz);
                        if (Distance(p, out _) > -margin) continue;
                        count++; output?.Add(FromProfile(p));
                    }
                }
            }
            return count;
        }
    }
}
