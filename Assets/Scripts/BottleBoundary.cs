using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter))]
public class BottleBoundary : MonoBehaviour
{
    public const int maxProfileCount = 32;
    public const float waterLevel = .73f;

    [System.NonSerialized] public Vector2[] profile;
    [System.NonSerialized] public Vector3 center;
    [System.NonSerialized] public Bounds bounds;
    [System.NonSerialized] public float waterVolume;
    float maximumRadius;

    public Vector3 ToProfile(Vector3 world) => world - center;

    public Vector3 FromProfile(Vector3 p) => center + p;

    void Awake()
    {
        center = transform.position;
        profile = ExtractProfile(GetComponent<MeshFilter>().sharedMesh, transform.localToWorldMatrix, center);
        foreach (Vector2 point in profile) maximumRadius = Mathf.Max(maximumRadius, point.y);
        float waterY = waterLevel - center.y;
        const int samples = 200;
        float xMin = profile[0].x;
        float xMax = profile[profile.Length - 1].x;
        float dx = (xMax - xMin) / samples;

        for (int i = 0; i < samples; i++)
        {
            float x = xMin + (i + 0.5f) * dx;
            float r = Radius(profile, x, 0);
            if (waterY <= -r) continue;
            float area = waterY >= r ? Mathf.PI * r * r :
                r * r * Mathf.Acos(-waterY / r) + waterY * Mathf.Sqrt(Mathf.Max(0, r * r - waterY * waterY));
            waterVolume += area * dx;
        }
        bounds = new Bounds(center + Vector3.right * ((xMin + xMax) * .5f), new Vector3(xMax - xMin, 2 * maximumRadius, 2 * maximumRadius));
    }

    static Vector2[] ExtractProfile(Mesh mesh, Matrix4x4 matrix, Vector3 origin)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        var innerIndices = new HashSet<int>();
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
            Vector3 normal = Vector3.Cross(b - a, c - a);
            Vector3 radial = (a + b + c) / 3;
            radial.x = 0;
            float facing = Vector3.Dot(normal, radial);
            float threshold = normal.magnitude * radial.magnitude * 1e-4f;
            if (facing >= -threshold) continue;
            for (int j = 0; j < 3; j++) innerIndices.Add(triangles[i + j]);
        }

        var inner = new List<Vector3>(innerIndices.Count);
        foreach (int i in innerIndices) inner.Add(matrix.MultiplyPoint3x4(vertices[i]) - origin);
        inner.Sort((a, b) => a.x.CompareTo(b.x));
        float extent = 0;
        foreach (Vector3 point in inner) extent = Mathf.Max(extent, point.magnitude);
        float tolerance = Mathf.Max(1e-6f, extent * 1e-5f);
        var points = new List<Vector2>();
        for (int begin = 0; begin < inner.Count;)
        {
            int end = begin + 1;
            while (end < inner.Count && inner[end].x - inner[begin].x <= tolerance) end++;
            double sumX = 0, sumRadius = 0;
            for (int i = begin; i < end; i++)
            {
                Vector3 p = inner[i];
                sumX += p.x;
                sumRadius += new Vector2(p.y, p.z).magnitude;
            }
            float x = (float)(sumX / (end - begin)), radius = (float)(sumRadius / (end - begin));
            points.Add(new Vector2(x, radius));
            begin = end;
        }
        return points.ToArray();
    }

    static float Radius(Vector2[] points, float x, float margin)
    {
        int i = 0;
        while (i < points.Length - 2 && x > points[i + 1].x) i++;
        Vector2 a = points[i], b = points[i + 1];
        float slope = (b.y - a.y) / (b.x - a.x);
        return Mathf.LerpUnclamped(a.y, b.y, (x - a.x) / (b.x - a.x)) - margin * Mathf.Sqrt(1 + slope * slope);
    }

    public bool Contains(Vector3 world, float margin = 0)
    {
        Vector3 p = ToProfile(world);
        if (p.x < profile[0].x + margin || p.x > profile[profile.Length - 1].x - margin) return false;
        float radius = Radius(profile, p.x, margin);
        return radius >= 0 && p.y * p.y + p.z * p.z <= radius * radius;
    }

    public Vector4[] CreateFill(int count, out float spacing)
    {
        spacing = Mathf.Pow(waterVolume / count, 1f / 3f) * 0.85f;
        float margin = spacing * .85f;
        var candidates = new List<Vector4>(count);

        for (int x = 0; ; x++)
        {
            float px = profile[0].x + margin + x * spacing;
            if (px > profile[profile.Length - 1].x - margin) break;
            float radius = Radius(profile, px, margin);
            if (radius < 0) continue;

            for (int y = 0; ; y++)
            {
                float py = waterLevel - center.y - spacing * .34f - y * spacing;
                if (py < -maximumRadius + margin) break;

                for (int z = 0; ; z++)
                {
                    float pz = -maximumRadius + margin + z * spacing;
                    if (pz > maximumRadius - margin) break;
                    if (py * py + pz * pz > radius * radius) continue;
                    candidates.Add(new Vector4(px, py, pz, 1));
                }
            }
        }

        var positions = new Vector4[count];
        for (int i = 0; i < count; i++)
            positions[i] = candidates[(int)((long)i * candidates.Count / count)];
        return positions;
    }
}
