using UnityEngine;

namespace WaterSplash
{
    /// <summary>
    /// Small position-based cord with a rigid paper tag at its end. The cord is
    /// tied to the bottle neck, collides with the outside of the glass, cork and
    /// tabletop, sways in a gentle breeze and can be grabbed or flicked.
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public sealed class TagRope : MonoBehaviour
    {
        public BottleVoyage voyage;
        [Tooltip("Tie point on the neck cord, in bottle-profile space.")]
        public Vector3 anchorProfile = new Vector3(1.51f, -.04f, -.28f);
        public new Transform tag;
        public Material ropeMaterial;
        public float length = .36f;
        [Range(6, 32)] public int segments = 16;
        public float radius = .0065f;
        public Vector2 tagSize = new Vector2(.22f, .30f);
        public float tableHeight = -.11f;
        public float gravityScale = 1.4f;
        public float tagMass = 3;
        public float ropeDrag = .9f;
        public float plateDrag = 4.5f;
        public float twistStiffness = .9f;
        public Vector3 windDirection = new Vector3(1, 0, .35f);
        public float windSpeed = .55f;
        public float gustSpeed = .7f;
        public bool interactive = true;

        const float Step = 1f / 240f;
        const int MaxSteps = 8;
        const float GlassThickness = .035f;

        Vector3[] p, prev, v;
        float[] w;
        bool[] touching;
        int hole, left, right, count, grabbed = -1;
        float segment, diagonal, accumulator, simTime, grabDepth;
        Vector3 grabTarget, restNormal, lastPointer;
        Mesh mesh;
        Vector3[] smooth, vertices, normals, frameNormal;
        Transform frame;

        public bool IsGrabbing => grabbed >= 0;
        public Vector3 HolePosition => p != null ? p[hole] : Vector3.zero;
        public Vector3 TagCenter => p != null ? (p[hole] + .5f * (p[left] + p[right])) * .5f : Vector3.zero;

        void OnEnable()
        {
            if (voyage == null || voyage.boundary == null || tag == null)
            {
                Debug.LogError("TagRope needs the bottle voyage, boundary and tag.", this);
                enabled = false;
                return;
            }
            Build();
            ResetRope();
        }

        void OnDisable()
        {
            if (mesh != null) Destroy(mesh);
            if (frame != null) Destroy(frame.gameObject);
            mesh = null; frame = null; grabbed = -1;
        }

        Vector3 Anchor => voyage.boundary.FromProfile(anchorProfile);
        Vector3 Gravity => Physics.gravity * gravityScale;

        void Build()
        {
            hole = segments; left = segments + 1; right = segments + 2; count = segments + 3;
            p = new Vector3[count]; prev = new Vector3[count]; v = new Vector3[count];
            w = new float[count]; touching = new bool[count];
            for (int i = 1; i <= segments; i++) w[i] = 1;
            float cornerMass = tagMass * .5f;
            w[hole] = 1 / (1 + tagMass * .2f);
            w[left] = w[right] = 1 / cornerMass;
            segment = length / segments;
            diagonal = new Vector2(tagSize.x * .5f, tagSize.y).magnitude;

            int rings = segments * 2 + 1;
            const int sides = 8;
            smooth = new Vector3[rings]; frameNormal = new Vector3[rings];
            vertices = new Vector3[rings * sides]; normals = new Vector3[rings * sides];
            var uv = new Vector2[rings * sides];
            var triangles = new int[(rings - 1) * sides * 6];
            for (int r = 0, t = 0; r < rings; r++)
                for (int s = 0; s < sides; s++)
                {
                    uv[r * sides + s] = new Vector2(s / (float)sides, r * length / (rings - 1) * 20);
                    if (r == rings - 1) continue;
                    int a = r * sides + s, b = r * sides + (s + 1) % sides;
                    triangles[t++] = a; triangles[t++] = b; triangles[t++] = a + sides;
                    triangles[t++] = b; triangles[t++] = b + sides; triangles[t++] = a + sides;
                }
            mesh = new Mesh { name = "Simulated tag cord" };
            mesh.MarkDynamic();
            mesh.vertices = vertices; mesh.normals = normals; mesh.uv = uv; mesh.triangles = triangles;
            // Vertices are written in world space, so the renderer lives at the origin.
            frame = new GameObject("Simulated tag cord", typeof(MeshFilter), typeof(MeshRenderer)).transform;
            frame.gameObject.hideFlags = HideFlags.DontSave;
            frame.GetComponent<MeshFilter>().sharedMesh = mesh;
            frame.GetComponent<MeshRenderer>().sharedMaterial = ropeMaterial;
        }

        public void ResetRope()
        {
            Camera camera = voyage.waterCamera;
            Vector3 facing = camera != null ? -camera.transform.forward : Vector3.back;
            facing.y = 0;
            restNormal = facing.sqrMagnitude > 1e-4f ? facing.normalized : Vector3.back;
            Vector3 anchor = Anchor, side = Vector3.Cross(Vector3.up, restNormal).normalized;
            for (int i = 0; i <= segments; i++) p[i] = anchor + Vector3.down * (segment * i) + restNormal * (.01f * i);
            p[left] = p[hole] + Vector3.down * tagSize.y - side * tagSize.x * .5f;
            p[right] = p[hole] + Vector3.down * tagSize.y + side * tagSize.x * .5f;
            for (int i = 0; i < count; i++) { prev[i] = p[i]; v[i] = Vector3.zero; }
            grabbed = -1;
            for (int i = 0; i < 480; i++) Simulate(Step);
            UpdateVisuals();
        }

        void Update()
        {
            if (p == null) return;
            if (interactive) UpdatePointer();
            accumulator = Mathf.Min(accumulator + Time.deltaTime, Step * MaxSteps);
            while (accumulator >= Step) { Simulate(Step); accumulator -= Step; }
        }

        void LateUpdate()
        {
            if (p != null) UpdateVisuals();
        }

        void UpdatePointer()
        {
            Vector2 pointer = Input.mousePosition;
            if (Input.GetMouseButtonDown(0) && !voyage.IsDraggingBoat && !voyage.IsPointerOnControls(pointer))
                TryGrab(pointer);
            if (grabbed >= 0)
            {
                if (!Input.GetMouseButton(0)) grabbed = -1;
                else
                {
                    Ray ray = voyage.waterCamera.ScreenPointToRay(pointer);
                    grabTarget = ray.GetPoint(grabDepth);
                    lastPointer = pointer;
                }
            }
        }

        /// <summary>Grabs the tag or cord when the screen point is close to it.</summary>
        public bool TryGrab(Vector2 pointer)
        {
            Camera camera = voyage.waterCamera;
            if (camera == null || p == null) return false;
            float reach = 34 * Mathf.Max(1, Screen.height / 720f);
            int best = -1; float bestDistance = reach;
            if (PointInTag(camera, pointer)) { best = left; bestDistance = 0; }
            for (int i = 3; i < count && bestDistance > 0; i++)
            {
                Vector3 screen = camera.WorldToScreenPoint(p[i]);
                if (screen.z <= 0) continue;
                float d = Vector2.Distance(screen, pointer);
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            if (best < 0) return false;
            if (best == left || best == right)
            {
                // Grabbing the card holds both lower corners through their midpoint.
                best = left;
            }
            Ray ray = camera.ScreenPointToRay(pointer);
            Vector3 point = best == left ? .5f * (p[left] + p[right]) : p[best];
            grabDepth = Vector3.Dot(point - ray.origin, ray.direction);
            grabTarget = ray.GetPoint(grabDepth);
            grabbed = best;
            lastPointer = pointer;
            return true;
        }

        bool PointInTag(Camera camera, Vector2 pointer)
        {
            Vector3 a = camera.WorldToScreenPoint(p[hole]);
            Vector3 b = camera.WorldToScreenPoint(p[left]);
            Vector3 c = camera.WorldToScreenPoint(p[right]);
            Vector2 centroid = (a + b + c) / 3;
            // Enlarge the triangle so the whole rectangular card and a finger margin count.
            float grow = 1.45f;
            Vector2 A = centroid + ((Vector2)a - centroid) * grow, B = centroid + ((Vector2)b - centroid) * grow, C = centroid + ((Vector2)c - centroid) * grow;
            float s1 = Cross(B - A, pointer - A), s2 = Cross(C - B, pointer - B), s3 = Cross(A - C, pointer - C);
            return (s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0);
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        Vector3 Wind(float time)
        {
            float slow = Mathf.PerlinNoise(time * .21f, .37f);
            float gust = Mathf.Pow(Mathf.PerlinNoise(time * .63f + 11.3f, 4.1f), 3) * 2.2f;
            Vector3 direction = Quaternion.Euler(0, (Mathf.PerlinNoise(time * .11f, 7.9f) - .5f) * 70, 0) * windDirection.normalized;
            return direction * (windSpeed * (.35f + slow) + gustSpeed * gust);
        }

        void Simulate(float h)
        {
            simTime += h;
            Vector3 wind = Wind(simTime), gravity = Gravity;
            p[0] = prev[0] = Anchor; v[0] = Vector3.zero;
            Vector3 up = p[hole] - .5f * (p[left] + p[right]);
            Vector3 normal = Vector3.Cross(p[right] - p[left], up).normalized;
            Vector3 tagVelocity = (v[hole] + v[left] + v[right]) / 3;
            Vector3 plate = normal * (plateDrag * Vector3.Dot(wind - tagVelocity, normal));
            for (int i = 1; i < count; i++)
            {
                Vector3 accel = gravity;
                if (i < hole) accel += (wind * .4f - v[i]) * ropeDrag;
                else accel += plate + (wind * .25f - v[i]) * (ropeDrag * .3f);
                v[i] += accel * h;
                prev[i] = p[i];
                p[i] += v[i] * h;
            }
            for (int iteration = 0; iteration < 2; iteration++)
            {
                for (int i = 0; i < segments; i++) Distance(i, i + 1, segment, 0);
                for (int i = 0; i < segments - 1; i++) Distance(i, i + 2, segment * 2, .35f);
                Distance(hole, left, diagonal, 0);
                Distance(hole, right, diagonal, 0);
                Distance(left, right, tagSize.x, 0);
                Twist(h);
                if (grabbed >= 0) Grab();
            }
            for (int i = 1; i < count; i++) touching[i] = Collide(i, i >= hole ? .006f : radius);
            CollideCard();
            for (int i = 1; i < count; i++)
            {
                Vector3 velocity = (p[i] - prev[i]) / h;
                if (touching[i]) velocity *= .82f;
                v[i] = velocity.sqrMagnitude > 400 ? velocity.normalized * 20 : velocity;
            }
        }

        void Distance(int a, int b, float rest, float softness)
        {
            float wa = w[a], wb = w[b], sum = wa + wb;
            if (sum <= 0) return;
            Vector3 delta = p[b] - p[a];
            float d = delta.magnitude;
            if (d < 1e-6f) return;
            float error = (d - rest) / (sum * (1 + softness * 8));
            Vector3 correction = delta * (error / d);
            p[a] += correction * wa;
            p[b] -= correction * wb;
        }

        void Twist(float h)
        {
            Vector3 mid = .5f * (p[left] + p[right]);
            Vector3 axis = (p[hole] - mid).normalized;
            Vector3 across = p[right] - p[left];
            Vector3 current = Vector3.ProjectOnPlane(across, axis);
            Vector3 desired = Vector3.Cross(axis, restNormal);
            if (current.sqrMagnitude < 1e-8f || desired.sqrMagnitude < 1e-4f) return;
            desired = desired.normalized * current.magnitude;
            Vector3 target = Vector3.Slerp(current, desired, Mathf.Clamp01(twistStiffness * h));
            Vector3 change = (target - current) * .5f;
            p[right] += change; p[left] -= change;
        }

        void Grab()
        {
            if (grabbed == left)
            {
                Vector3 mid = .5f * (p[left] + p[right]);
                Vector3 shift = (grabTarget - mid) * .35f;
                p[left] += shift; p[right] += shift;
            }
            else p[grabbed] += (grabTarget - p[grabbed]) * .5f;
        }

        bool Collide(int i, float margin)
        {
            bool hit = false;
            if (p[i].y < tableHeight + margin) { p[i].y = tableHeight + margin; hit = true; }
            Vector3 local = voyage.boundary.ToProfile(p[i]);
            if (Outside(ref local, margin)) { p[i] = voyage.boundary.FromProfile(local); hit = true; }
            return hit;
        }

        void CollideCard()
        {
            Vector3 center = TagCenter;
            Vector3 local = voyage.boundary.ToProfile(center);
            if (!Outside(ref local, .01f)) return;
            Vector3 push = voyage.boundary.FromProfile(local) - center;
            p[hole] += push; p[left] += push; p[right] += push;
            touching[left] = touching[right] = true;
        }

        /// <summary>Pushes a profile-space point out of the glass, lip and cork.</summary>
        static bool Outside(ref Vector3 local, float margin)
        {
            const float back = -1.95f, corkEnd = 1.97f;
            if (local.x < back || local.x > corkEnd + margin) return false;
            float outer;
            if (local.x > 1.72f) outer = .265f;
            else if (local.x > 1.66f) outer = .33f;
            else outer = BottleBoundary.Radius(local.x, out _) + GlassThickness;
            outer += margin;
            Vector2 radial = new Vector2(local.y, local.z);
            float r = radial.magnitude;
            if (r >= outer) return false;
            float throughEnd = corkEnd + margin - local.x;
            if (local.x > 1.72f && throughEnd < outer - r) { local.x = corkEnd + margin; return true; }
            Vector2 direction = r > 1e-5f ? radial / r : Vector2.down;
            radial = direction * outer;
            local.y = radial.x; local.z = radial.y;
            return true;
        }

        void UpdateVisuals()
        {
            Vector3 mid = .5f * (p[left] + p[right]);
            Vector3 up = (p[hole] - mid).normalized;
            Vector3 across = p[right] - p[left];
            Vector3 forward = Vector3.Cross(across, up);
            if (forward.sqrMagnitude > 1e-8f) tag.SetPositionAndRotation(p[hole], Quaternion.LookRotation(-forward.normalized, up));

            int rings = smooth.Length;
            for (int r = 0; r < rings; r++)
            {
                int i = r / 2;
                if ((r & 1) == 0) { smooth[r] = p[i]; continue; }
                Vector3 a = p[Mathf.Max(0, i - 1)], b = p[i], c = p[i + 1], d = p[Mathf.Min(hole, i + 2)];
                smooth[r] = .5f * (b + c) + .0625f * ((b - a) + (c - d));
            }
            const int sides = 8;
            Vector3 tangent = (smooth[1] - smooth[0]).normalized;
            Vector3 n = Vector3.Cross(tangent, Vector3.forward);
            if (n.sqrMagnitude < 1e-4f) n = Vector3.Cross(tangent, Vector3.right);
            n.Normalize();
            Vector3 min = smooth[0], max = smooth[0];
            for (int r = 0; r < rings; r++)
            {
                Vector3 next = (smooth[Mathf.Min(r + 1, rings - 1)] - smooth[Mathf.Max(r - 1, 0)]).normalized;
                if (next.sqrMagnitude > .5f)
                {
                    n = Vector3.ProjectOnPlane(n, next);
                    if (n.sqrMagnitude < 1e-6f) n = Vector3.Cross(next, Vector3.up);
                    n.Normalize();
                    tangent = next;
                }
                Vector3 b = Vector3.Cross(tangent, n);
                for (int s = 0; s < sides; s++)
                {
                    float angle = s * Mathf.PI * 2 / sides;
                    Vector3 radial = n * Mathf.Cos(angle) + b * Mathf.Sin(angle);
                    vertices[r * sides + s] = smooth[r] + radial * radius;
                    normals[r * sides + s] = radial;
                }
                min = Vector3.Min(min, smooth[r]); max = Vector3.Max(max, smooth[r]);
            }
            mesh.vertices = vertices;
            mesh.normals = normals;
            var bounds = new Bounds(); bounds.SetMinMax(min - Vector3.one * radius, max + Vector3.one * radius);
            mesh.bounds = bounds;
        }
    }
}
