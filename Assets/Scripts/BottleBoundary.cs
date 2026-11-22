using System.Collections.Generic;
using UnityEngine;

public class BottleBoundary : MonoBehaviour
{
    public const float waterLevel = .73f;
    [System.NonSerialized] public Vector2[] profile; // (x, radius) around the bottle axis, relative to center
    [System.NonSerialized] public Vector3 center;
    [System.NonSerialized] public float waterVolume;
    float maxRadius;

    void Awake()
    {
        center = transform.position;
        profile = ExtractProfile(GetComponent<MeshFilter>().sharedMesh);
        foreach (var p in profile) maxRadius = Mathf.Max(maxRadius, p.y);
        float y = waterLevel - center.y, x0 = profile[0].x, dx = (profile[^1].x - x0) / 200;
        for (int i = 0; i < 200; i++) // integrate the circular segment below the water line
        {
            float r = Radius(x0 + (i + .5f) * dx);
            if (y > -r) waterVolume += (y >= r ? Mathf.PI * r * r : r * r * Mathf.Acos(-y / r) + y * Mathf.Sqrt(r * r - y * y)) * dx;
        }
    }

    Vector2[] ExtractProfile(Mesh mesh)
    {
        Vector3[] v = mesh.vertices; int[] t = mesh.triangles;
        var inner = new HashSet<int>();
        for (int i = 0; i < t.Length; i += 3) // inner wall faces the axis, end caps don't
        {
            Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]], n = Vector3.Cross(b - a, c - a), radial = (a + b + c) / 3;
            radial.x = 0;
            if (Vector3.Dot(n, radial) < -n.magnitude * radial.magnitude * 1e-4f) { inner.Add(t[i]); inner.Add(t[i + 1]); inner.Add(t[i + 2]); }
        }
        var points = new List<Vector3>();
        foreach (int i in inner) points.Add(transform.TransformPoint(v[i]) - center);
        points.Sort((a, b) => a.x.CompareTo(b.x));
        var result = new List<Vector2>();
        for (int begin = 0, end; begin < points.Count; begin = end) // average each ring
        {
            Vector2 sum = Vector2.zero;
            for (end = begin; end < points.Count && points[end].x - points[begin].x <= 2e-5f; end++) sum += new Vector2(points[end].x, new Vector2(points[end].y, points[end].z).magnitude);
            result.Add(sum / (end - begin));
        }
        return result.ToArray();
    }

    public float Radius(float x, float margin = 0)
    {
        int i = 0;
        while (i < profile.Length - 2 && x > profile[i + 1].x) i++;
        Vector2 a = profile[i], b = profile[i + 1];
        float slope = (b.y - a.y) / (b.x - a.x);
        return a.y + (x - a.x) * slope - margin * Mathf.Sqrt(1 + slope * slope);
    }

    public bool Contains(Vector3 world, float margin = 0)
    {
        Vector3 p = world - center;
        float r = Radius(p.x, margin);
        return p.x >= profile[0].x + margin && p.x <= profile[^1].x - margin && r >= 0 && p.y * p.y + p.z * p.z <= r * r;
    }

    public Vector3[] CreateFill(int count) // lattice below the water line, evenly subsampled
    {
        float spacing = Mathf.Pow(waterVolume / count, 1f / 3) * .85f, margin = spacing * .85f;
        var candidates = new List<Vector3>();
        for (float x = profile[0].x + margin; x <= profile[^1].x - margin; x += spacing)
        {
            float r = Radius(x, margin);
            if (r < 0) continue;
            for (float y = waterLevel - center.y - spacing * .34f; y >= margin - maxRadius; y -= spacing)
                for (float z = margin - maxRadius; z <= maxRadius - margin; z += spacing)
                    if (y * y + z * z <= r * r) candidates.Add(center + new Vector3(x, y, z));
        }
        var result = new Vector3[count];
        for (int i = 0; i < count; i++) result[i] = candidates[(int)((long)i * candidates.Count / count)];
        return result;
    }
}
