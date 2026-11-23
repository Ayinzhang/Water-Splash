using UnityEngine;

public class TagRope : MonoBehaviour
{
    public BottleVoyage voyage;
    public BottleBoundary boundary;
    public Material ropeMaterial;
    const int segments = 16, sides = 8, hole = segments, left = segments + 1, right = segments + 2, count = segments + 3;
    const float segment = .36f / segments, radius = .0065f, step = 1f / 240, table = -.11f, glass = .035f, tagWidth = .22f;
    static readonly float diagonal = new Vector2(.11f, .3f).magnitude;
    static readonly Vector3 anchorOffset = new(-1.51f, -.04f, -.28f), windDirection = new Vector3(1, 0, .35f).normalized;
    static readonly Vector2[] outline = { new(-.11f, -.286f), new(-.096f, -.3f), new(.096f, -.3f), new(.11f, -.286f), new(.11f, -.015f), new(.05f, .045f), new(-.05f, .045f), new(-.11f, -.015f) };

    // Nodes: rope 0..hole, then the tag's lower corners
    readonly Vector3[] p = new Vector3[count], prev = new Vector3[count], v = new Vector3[count];
    readonly Vector3[] vertices = new Vector3[(hole + 1) * sides], normals = new Vector3[(hole + 1) * sides];
    readonly float[] w = new float[count];
    readonly bool[] touching = new bool[count];
    bool isGrab;
    float accumulator, time, grabDepth;
    Vector3 grabTarget, restNormal;
    Mesh mesh;

    Vector3 Anchor => boundary.center + anchorOffset;
    Vector3 TagCenter => (p[hole] + .5f * (p[left] + p[right])) * .5f;

    void Start()
    {
        for (int i = 1; i < count; i++) w[i] = 1;
        w[hole] = 1 / 1.6f; w[left] = w[right] = 1 / 1.5f; // the tag is heavier
        var uv = new Vector2[vertices.Length];
        var triangles = new int[hole * sides * 6];
        for (int r = 0, t = 0; r <= hole; r++)
            for (int s = 0; s < sides; s++)
            {
                uv[r * sides + s] = new Vector2((float)s / sides, r * segment * 20);
                if (r == hole) continue;
                int a = r * sides + s, b = r * sides + (s + 1) % sides;
                triangles[t++] = a; triangles[t++] = b; triangles[t++] = a + sides; triangles[t++] = b; triangles[t++] = b + sides; triangles[t++] = a + sides;
            }
        mesh = new Mesh { vertices = vertices, normals = normals, uv = uv, triangles = triangles };
        var cord = new GameObject("Tag cord", typeof(MeshFilter), typeof(MeshRenderer)); // world space vertices
        cord.GetComponent<MeshFilter>().sharedMesh = mesh;
        cord.GetComponent<MeshRenderer>().sharedMaterial = ropeMaterial;

        Vector3 facing = -Camera.main.transform.forward; facing.y = 0;
        restNormal = facing.normalized;
        Vector3 side = Vector3.Cross(Vector3.up, restNormal) * tagWidth * .5f;
        for (int i = 0; i <= hole; i++) p[i] = Anchor + Vector3.down * (segment * i) + restNormal * (.01f * i);
        p[left] = p[hole] + Vector3.down * .3f - side; p[right] = p[hole] + Vector3.down * .3f + side;
        for (int i = 0; i < 240; i++) Simulate(); // settle
        LateUpdate();
    }

    void Update()
    {
        Vector2 pointer = Input.mousePosition;
        if (Input.GetMouseButtonDown(0) && !voyage.isDrag && !BottleVoyage.OnUI(pointer)) Grab(pointer);
        if (!Input.GetMouseButton(0)) isGrab = false;
        else if (isGrab) grabTarget = Camera.main.ScreenPointToRay(pointer).GetPoint(grabDepth);
        accumulator = Mathf.Min(accumulator + Time.deltaTime, step * 4);
        while (accumulator >= step) { Simulate(); accumulator -= step; }
    }

    void Grab(Vector2 pointer)
    {
        Ray ray = Camera.main.ScreenPointToRay(pointer);
        if (!new Plane(transform.forward, transform.position).Raycast(ray, out float hit) || !Inside(transform.InverseTransformPoint(ray.GetPoint(hit)))) return;
        grabDepth = Vector3.Dot(TagCenter - ray.origin, ray.direction);
        grabTarget = ray.GetPoint(grabDepth);
        isGrab = true;
    }

    bool Inside(Vector3 local) // tag outline from its mesh, counter-clockwise
    {
        for (int i = 0; i < outline.Length; i++)
        {
            Vector2 a = outline[i], b = outline[(i + 1) % outline.Length];
            if ((b.x - a.x) * (local.y - a.y) - (b.y - a.y) * (local.x - a.x) < 0) return false;
        }
        return true;
    }

    Vector3 Wind()
    {
        float slow = Mathf.PerlinNoise(time * .21f, .37f), gust = Mathf.Pow(Mathf.PerlinNoise(time * .63f + 11.3f, 4.1f), 3) * 2.2f;
        return Quaternion.Euler(0, (Mathf.PerlinNoise(time * .11f, 7.9f) - .5f) * 70, 0) * windDirection * (.55f * (.35f + slow) + .7f * gust);
    }

    void Simulate() // PBD
    {
        time += step;
        Vector3 wind = Wind(), gravity = Physics.gravity * 1.4f;
        p[0] = prev[0] = Anchor;
        Vector3 normal = Vector3.Cross(p[right] - p[left], p[hole] - .5f * (p[left] + p[right])).normalized;
        Vector3 plate = normal * (4.5f * Vector3.Dot(wind - (v[hole] + v[left] + v[right]) / 3, normal)); // wind pressure on the card
        for (int i = 1; i < count; i++)
        {
            v[i] += (gravity + (i < hole ? (wind * .4f - v[i]) * .9f : plate + (wind * .25f - v[i]) * .27f)) * step;
            prev[i] = p[i]; p[i] += v[i] * step;
        }
        for (int k = 0; k < 2; k++)
        {
            for (int i = 0; i < segments; i++) Distance(i, i + 1, segment, 0);
            for (int i = 0; i < segments - 1; i++) Distance(i, i + 2, segment * 2, .35f); // bending
            Distance(hole, left, diagonal, 0); Distance(hole, right, diagonal, 0); Distance(left, right, tagWidth, 0);
            Twist();
            if (isGrab) { Vector3 shift = (grabTarget - TagCenter) * .35f; p[hole] += shift; p[left] += shift; p[right] += shift; }
        }
        for (int i = 1; i < count; i++)
        {
            touching[i] = p[i].y < table + (i >= hole ? .006f : radius);
            if (touching[i]) p[i].y = table + (i >= hole ? .006f : radius);
            Vector3 local = p[i] - boundary.center;
            if (Outside(ref local, i >= hole ? .006f : radius)) { p[i] = boundary.center + local; touching[i] = true; }
        }
        Vector3 card = TagCenter - boundary.center; // keep the card body out of the glass too
        if (Outside(ref card, .01f))
        {
            Vector3 push = boundary.center + card - TagCenter;
            p[hole] += push; p[left] += push; p[right] += push;
            touching[left] = touching[right] = true;
        }
        for (int i = 1; i < count; i++) v[i] = (p[i] - prev[i]) / step * (touching[i] ? .82f : 1);
    }

    void Distance(int a, int b, float rest, float softness)
    {
        Vector3 d = p[b] - p[a], c = d * ((1 - rest / d.magnitude) / ((w[a] + w[b]) * (1 + softness * 8)));
        p[a] += c * w[a]; p[b] -= c * w[b];
    }

    void Twist() // turn the card back toward the camera
    {
        Vector3 axis = (p[hole] - .5f * (p[left] + p[right])).normalized, current = Vector3.ProjectOnPlane(p[right] - p[left], axis);
        Vector3 change = (Vector3.Slerp(current, Vector3.Cross(axis, restNormal).normalized * current.magnitude, .9f * step) - current) * .5f;
        p[right] += change; p[left] -= change;
    }

    bool Outside(ref Vector3 local, float margin) // push out of the glass, lip and cork
    {
        if (local.x > 1.95f || local.x < -1.97f - margin) return false;
        float outer = (local.x < -1.72f ? .265f : local.x < -1.66f ? .33f : boundary.Radius(local.x) + glass) + margin;
        Vector2 radial = new(local.y, local.z);
        if (radial.magnitude >= outer) return false;
        if (local.x < -1.72f && local.x + 1.97f + margin < outer - radial.magnitude) local.x = -1.97f - margin; // out the cork end
        else { radial = radial.normalized * outer; local.y = radial.x; local.z = radial.y; }
        return true;
    }

    void LateUpdate()
    {
        Vector3 up = (p[hole] - .5f * (p[left] + p[right])).normalized;
        transform.SetPositionAndRotation(p[hole], Quaternion.LookRotation(-Vector3.Cross(p[right] - p[left], up), up));
        Vector3 n = Vector3.Cross(p[1] - p[0], Vector3.forward).normalized;
        for (int r = 0; r <= hole; r++) // tube around the rope with a transported frame
        {
            Vector3 t = (p[Mathf.Min(r + 1, hole)] - p[Mathf.Max(r - 1, 0)]).normalized;
            n = Vector3.ProjectOnPlane(n, t).normalized;
            Vector3 b = Vector3.Cross(t, n);
            for (int s = 0; s < sides; s++)
            {
                float angle = s * Mathf.PI * 2 / sides;
                normals[r * sides + s] = n * Mathf.Cos(angle) + b * Mathf.Sin(angle);
                vertices[r * sides + s] = p[r] + normals[r * sides + s] * radius;
            }
        }
        mesh.vertices = vertices; mesh.normals = normals; mesh.RecalculateBounds();
    }
}
