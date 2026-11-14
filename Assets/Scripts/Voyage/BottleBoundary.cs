using System.IO;
using UnityEngine;

namespace WaterSplash
{
    // Fixed BottleVoyage profile; dynamic boat and tag contacts still query it.
    public sealed class BottleBoundary : MonoBehaviour
    {
        public TextAsset initialState;
        public Vector3 center => new Vector3(0, .91f, 0);
        public float AxisSign => -1;
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

        public float LoadInitialState(int count, ComputeBuffer particles, ComputeBuffer boundary)
        {
            using (var reader = new BinaryReader(new MemoryStream(initialState.bytes)))
            {
                var cells = new Vector4[boundary.count];
                for (int i = 0; i < cells.Length; i++) cells[i] = ReadVector(reader);
                boundary.SetData(cells);
                // Grid float4s, then [spacing, position float4s] for 16K/32K/64K/128K.
                int tier = Mathf.RoundToInt(Mathf.Log(count / 16384f, 2));
                reader.BaseStream.Position += (count - 16384) * 16 + tier * 4;
                float spacing = reader.ReadSingle();
                var data = new Vector4[count * 5];
                for (int i = 0; i < count; i++) data[i] = ReadVector(reader);
                particles.SetData(data);
                return spacing;
            }
        }

        static Vector4 ReadVector(BinaryReader reader) =>
            new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
