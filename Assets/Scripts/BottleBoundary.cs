using System.Collections.Generic;
using UnityEngine;

public class BottleBoundary : MonoBehaviour
{
    public Vector3 Center => new Vector3(0, .91f, 0);
    public float WaterLevel => .73f;
    public Bounds Bounds => new Bounds(Center, new Vector3(4, 1.6f, 1.6f));

    public float WaterVolume { get; private set; }

    public static readonly Vector2[] Profile =
    {
        new Vector2(-1.75f, .24f), new Vector2(-1.72f, .24f), new Vector2(-1.40f, .24f), new Vector2(-1.27f, .26f),
        new Vector2(-1.15f, .32f), new Vector2(-1.04f, .43f), new Vector2(-.91f, .56f), new Vector2(-.76f, .66f),
        new Vector2(-.59f, .72f), new Vector2(-.40f, .74f), new Vector2(1.28f, .74f), new Vector2(1.50f, .72f),
        new Vector2(1.66f, .65f), new Vector2(1.78f, .51f), new Vector2(1.85f, .30f), new Vector2(1.88f, .07f)
    };

    public Vector3 ToProfile(Vector3 world) => world - Center;

    public Vector3 FromProfile(Vector3 p) => Center + p;

    void Awake()
    {
        float waterY = WaterLevel - Center.y;
        float d = Mathf.Abs(waterY);

        const int samples = 200;
        float xMin = Profile[0].x;
        float xMax = Profile[Profile.Length - 1].x;
        float dx = (xMax - xMin) / samples;

        for (int i = 0; i < samples; i++)
        {
            float x = xMin + (i + 0.5f) * dx;
            float r = Radius(x, 0);
            if (r <= d) continue;
            float theta = Mathf.Acos(d / r);
            WaterVolume += (r * r * theta - d * Mathf.Sqrt(r * r - d * d)) * dx;
        }
    }

    static float Radius(float x, float margin)
    {
        int i = 0;
        while (i < Profile.Length - 2 && x > Profile[i + 1].x) i++;
        Vector2 a = Profile[i], b = Profile[i + 1];
        float slope = (b.y - a.y) / (b.x - a.x);
        return Mathf.LerpUnclamped(a.y, b.y, (x - a.x) / (b.x - a.x)) - margin * Mathf.Sqrt(1 + slope * slope);
    }

    public bool Contains(Vector3 world, float margin = 0)
    {
        Vector3 p = ToProfile(world);
        if (p.x < Profile[0].x + margin || p.x > Profile[Profile.Length - 1].x - margin) return false;
        float radius = Radius(p.x, margin);
        return radius >= 0 && p.y * p.y + p.z * p.z <= radius * radius;
    }

    public Vector4[] CreateFill(int count, out float spacing)
    {
        spacing = Mathf.Pow(WaterVolume / count, 1f / 3f) * 0.85f;
        float margin = spacing * .85f;
        var candidates = new List<Vector4>(count);

        for (int x = 0; ; x++)
        {
            float px = Profile[0].x + margin + x * spacing;
            if (px > Profile[Profile.Length - 1].x - margin) break;
            float radius = Radius(px, margin);
            if (radius < 0) continue;

            for (int y = 0; ; y++)
            {
                float py = WaterLevel - Center.y - spacing * .34f - y * spacing;
                if (py < -.74f + margin) break;

                for (int z = 0; ; z++)
                {
                    float pz = -.74f + margin + z * spacing;
                    if (pz > .74f - margin) break;
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
