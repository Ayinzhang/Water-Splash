using NUnit.Framework;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace WaterSplash.Tests
{
    public sealed class BottleVoyageTests
    {
        [TestCase(16384, 6)]
        [TestCase(32768, 6)]
        [TestCase(65536, 6)]
        [TestCase(131072, 8)]
        public void BottleSubstepsFollowParticleTier(int count, int steps)
        {
            Assert.That(BottleVoyage.SubstepsForParticleCount(count), Is.EqualTo(steps));
        }

        [Test]
        public void DragMovesBoatImmediatelyAndWaterHeightDoesNotShiftGrab()
        {
            var owner = new GameObject("Immediate boat drag");
            owner.SetActive(false);
            try
            {
                var fluid = owner.AddComponent<MlsMpmFluid>();
                var boundary = owner.AddComponent<BottleBoundary>();
                var voyage = owner.AddComponent<BottleVoyage>();
                var cameraObject = new GameObject("Drag test camera");
                cameraObject.transform.SetParent(owner.transform);
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 4, -6);
                camera.transform.LookAt(new Vector3(0, .7f, 0));
                camera.orthographic = true;
                camera.orthographicSize = 2.5f;
                var boat = GameObject.CreatePrimitive(PrimitiveType.Cube);
                boat.transform.SetParent(owner.transform);
                boat.transform.position = new Vector3(-.4f, .7f, 0);
                boat.transform.localScale = new Vector3(.3f, .15f, .4f);
                voyage.fluid = fluid; voyage.boundary = boundary;
                voyage.boat = boat.transform; voyage.waterCamera = camera;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(BottleVoyage).GetField("ready", flags).SetValue(voyage, true);
                typeof(BottleVoyage).GetField("boatRenderers", flags).SetValue(voyage, boat.GetComponentsInChildren<Renderer>());
                Vector3 start = boat.transform.position;
                Vector3 grab = start + Vector3.up * .05f;
                Assert.That(voyage.BeginBoatDrag(camera.WorldToScreenPoint(grab)), Is.True);
                Vector3 delta = new Vector3(.5f, 0, .1f);
                Vector2 pointer = camera.WorldToScreenPoint(grab + delta);
                voyage.DragBoat(pointer);
                Assert.That(boat.transform.position.x, Is.EqualTo(start.x + delta.x).Within(1e-4));
                Assert.That(boat.transform.position.z, Is.EqualTo(start.z + delta.z).Within(1e-4));
                Assert.That(fluid.SimulatedSeconds, Is.Zero, "Input must move the boat before any physics tick.");
                boat.transform.position += Vector3.up * .1f;
                voyage.DragBoat(pointer);
                Assert.That(boat.transform.position.x, Is.EqualTo(start.x + delta.x).Within(1e-4));
                Assert.That(boat.transform.position.z, Is.EqualTo(start.z + delta.z).Within(1e-4));
                Assert.That(boat.transform.position.y, Is.EqualTo(start.y + .1f).Within(1e-4));
                voyage.EndBoatDrag();
                Vector3 released = boat.transform.position;
                voyage.DragBoat(camera.WorldToScreenPoint(grab - delta));
                Assert.That(boat.transform.position, Is.EqualTo(released));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [Test]
        public void SingleRenderingToggleChangesBothWaysWithoutResettingFluid()
        {
            var owner = new GameObject("Render toggle test");
            owner.SetActive(false);
            try
            {
                var voyage = owner.AddComponent<BottleVoyage>();
                voyage.fluid = owner.AddComponent<MlsMpmFluid>();
                typeof(BottleVoyage).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(voyage, true);
                voyage.ToggleRendering();
                Assert.That(voyage.fluid.renderSurface, Is.False);
                voyage.ToggleRendering();
                Assert.That(voyage.fluid.renderSurface, Is.True);
                Assert.That(voyage.ResetPending, Is.False);
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WaterProbeReducesNonMultipleParticleCountAndReportsEmptySamples(bool soa)
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("Compute shader required.");
            var shader = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/MlsMpm/Shaders/BottleWaterProbe.compute"));
            const int count = 513, blocks = (count + 127) / 128;
            try
            {
                using (var particles = new ComputeBuffer(count * 5, sizeof(float) * 4))
                using (var partials = new ComputeBuffer(blocks * 5, sizeof(float) * 4))
                using (var results = new ComputeBuffer(5, sizeof(float) * 4))
                {
                    var states = new Vector4[count * 5];
                    for (int i = 0; i < count; i++)
                    {
                        states[soa ? i : i * 5] = new Vector4(10, i == count - 1 ? 5 : 4, 10, 1);
                        states[soa ? i + count : i * 5 + 1] = new Vector4(2, 0, -3, 0);
                    }
                    particles.SetData(states);
                    int collect = shader.FindKernel("Collect"), reduce = shader.FindKernel("Reduce");
                    shader.SetBuffer(collect, "_Particles", particles);
                    shader.SetInt("_ParticlePlaneStride", soa ? count : 0);
                    shader.SetBuffer(collect, "_Partials", partials);
                    shader.SetBuffer(reduce, "_Partials", partials);
                    shader.SetBuffer(reduce, "_Samples", results);
                    shader.SetInt("_ParticleCount", count); shader.SetInt("_BlockCount", blocks);
                    shader.SetFloat("_CellSize", .1f); shader.SetFloat("_SurfaceOffset", .02f);
                    shader.SetFloat("_Radius", .23f); shader.SetVector("_GridOrigin", new Vector4(-1, .2f, -1, 0));
                    shader.SetVectorArray("_ProbePositions", new[] {
                        Vector4.zero, new Vector4(.1f,0,0,0), new Vector4(.24f,0,0,0),
                        new Vector4(2,0,2,0),new Vector4(-2,0,-2,0)
                    });
                    shader.Dispatch(collect, blocks, 5, 1); shader.Dispatch(reduce, 5, 1, 1);
                    var samples = new Vector4[5]; results.GetData(samples);
                    Assert.That(samples[0].x, Is.EqualTo(.72f).Within(1e-5));
                    Assert.That(samples[0].y, Is.EqualTo(.2f).Within(1e-5));
                    Assert.That(samples[0].z, Is.EqualTo(-.3f).Within(1e-5));
                    Assert.That(samples[0].w, Is.EqualTo(count));
                    Assert.That(samples[1].x, Is.EqualTo(samples[0].x));
                    Assert.That(samples[2], Is.EqualTo(Vector4.zero));
                    Assert.That(samples[3], Is.EqualTo(Vector4.zero));
                    Assert.That(samples[4], Is.EqualTo(Vector4.zero));
                }
            }
            finally { Object.DestroyImmediate(shader); }
        }

        [Test]
        public void BoatTargetStaysInWideBodyForEitherBottleDirection()
        {
            var owner = new GameObject("Boat target bounds");
            try
            {
                var boundary = owner.AddComponent<BottleBoundary>();
                var voyage = owner.AddComponent<BottleVoyage>();
                voyage.boundary = boundary;
                foreach (bool left in new[] { false, true })
                {
                    boundary.mouthFacesLeft = left;
                    Vector3 target = voyage.ClampBoatTarget(boundary.FromProfile(new Vector3(100, -.2f, 20)));
                    Vector3 local = boundary.ToProfile(target);
                    Assert.That(local.x, Is.EqualTo(.22f).Within(1e-5));
                    Assert.That(local.z, Is.EqualTo(.25f).Within(1e-5));
                    Assert.That(local.y, Is.EqualTo(-.2f).Within(1e-5));
                    target = voyage.ClampBoatTarget(boundary.FromProfile(new Vector3(-100, -.2f, -20)));
                    local = boundary.ToProfile(target);
                    Assert.That(local.x, Is.EqualTo(-1.15f).Within(1e-5));
                    Assert.That(local.z, Is.EqualTo(-.25f).Within(1e-5));
                }
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [Test]
        public void FlatWaterKeepsBoatUpright()
        {
            Assert.That(Vector3.Angle(BottleVoyage.SurfaceNormal(.7f,.7f,.7f,.7f), Vector3.up), Is.LessThan(.001f));
        }

        [Test]
        public void WaveTiltHasCorrectDirectionAndBoundedPresentationAngle()
        {
            Vector3 normal = BottleVoyage.SurfaceNormal(1.2f,.3f,1.1f,.2f);
            Assert.That(normal.x, Is.LessThan(0));
            Assert.That(normal.z, Is.LessThan(0));
            Assert.That(Vector3.Angle(normal, Vector3.up), Is.EqualTo(18).Within(.001f));
        }
    }
}
