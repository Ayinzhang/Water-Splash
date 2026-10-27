using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace WaterSplash.Tests
{
    public sealed class MlsMpmWhitewaterTests
    {
        GameObject owner;
        MlsMpmFluid fluid;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("A compute-capable GPU is required.");
            owner = new GameObject("Whitewater GPU test");
            owner.SetActive(false);
            fluid = owner.AddComponent<MlsMpmFluid>();
            fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/MlsMpm.compute");
            fluid.particleCount = 4096;
            fluid.gridResolution = 32;
            fluid.substeps = 4;
            fluid.whitewaterCapacity = 2049;
            fluid.showParticles = false;
            fluid.showControls = false;
            fluid.Initialize();
            Assert.That(fluid.Error, Is.Null);
        }

        [TearDown]
        public void TearDown()
        {
            if (owner == null) return;
            fluid.Release();
            Object.DestroyImmediate(owner);
        }

        [Test]
        public void StillWaterDoesNotContinuouslyGenerateWhitewater()
        {
            fluid.gravity = Vector3.zero;
            fluid.StepSimulation(30);
            MlsMpmFluid.WhitewaterSnapshot state = fluid.CaptureWhitewaterSnapshot();
            AssertHealthy(state);
            Assert.That(state.born, Is.Zero);
            Assert.That(state.foam + state.spray, Is.Zero);
        }

        [Test]
        public void ImpactGeneratesBothSprayAndFoamWithinTheBoundedPool()
        {
            int peakFoam = 0, peakSpray = 0;
            for (int step = 0; step < 12; step++)
            {
                fluid.StepSimulation(10);
                MlsMpmFluid.WhitewaterSnapshot state = fluid.CaptureWhitewaterSnapshot();
                AssertHealthy(state);
                Assert.That(state.foam + state.spray, Is.LessThanOrEqualTo(fluid.whitewaterCapacity));
                peakFoam = Mathf.Max(peakFoam, state.foam);
                peakSpray = Mathf.Max(peakSpray, state.spray);
            }
            Assert.That(peakFoam, Is.GreaterThan(0));
            Assert.That(peakSpray, Is.GreaterThan(0));
            Assert.That(fluid.CaptureSnapshot().faults, Is.Zero);
        }

        [Test]
        public void WhitewaterMovesAndExpiresWhenEmissionStops()
        {
            fluid.StepSimulation(60);
            MlsMpmFluid.WhitewaterSnapshot before = fluid.CaptureWhitewaterSnapshot();
            Assert.That(before.born, Is.GreaterThan(0));
            fluid.whitewaterEmissionRate = 0;
            fluid.StepSimulation(5);
            MlsMpmFluid.WhitewaterSnapshot after = fluid.CaptureWhitewaterSnapshot();
            AssertHealthy(after);
            int moved = 0;
            for (int i = 0; i < after.states.Length; i++)
            {
                MlsMpmFluid.WhitewaterState a = before.states[i], b = after.states[i];
                if (a.position.w <= 0 || b.position.w <= 0) continue;
                Assert.That(b.position.w, Is.LessThan(a.position.w));
                if (((Vector3)a.position - (Vector3)b.position).sqrMagnitude > 1e-6) moved++;
            }
            Assert.That(moved, Is.GreaterThan(0));
            fluid.StepSimulation(120);
            fluid.StepSimulation(120);
            MlsMpmFluid.WhitewaterSnapshot expired = fluid.CaptureWhitewaterSnapshot();
            AssertHealthy(expired);
            Assert.That(expired.born, Is.EqualTo(before.born), "No new whitewater when emission is zero.");
            Assert.That(expired.foam + expired.spray, Is.Zero);
            Assert.That(expired.expired, Is.GreaterThan(0));
        }

        [Test]
        public void DisablingWhitewaterClearsItWithoutResettingTheLiquid()
        {
            fluid.StepSimulation(60);
            Assert.That(fluid.CaptureWhitewaterSnapshot().born, Is.GreaterThan(0));
            double time = fluid.SimulatedSeconds;
            fluid.enableWhitewater = false;
            fluid.StepSimulation(1);
            MlsMpmFluid.WhitewaterSnapshot state = fluid.CaptureWhitewaterSnapshot();
            AssertHealthy(state);
            Assert.That(state.foam + state.spray, Is.Zero);
            Assert.That(fluid.SimulatedSeconds, Is.EqualTo(time + MlsMpmFluid.FixedTick).Within(1e-8));
            fluid.Initialize();
            Assert.That(fluid.CaptureWhitewaterSnapshot().born, Is.Zero);
        }

        [Test]
        public void NonMultipleWhitewaterCapacityDoesNotWriteOutOfBounds()
        {
            fluid.whitewaterCapacity = 513;
            fluid.Initialize();
            fluid.StepSimulation(90);
            MlsMpmFluid.WhitewaterSnapshot state = fluid.CaptureWhitewaterSnapshot();
            AssertHealthy(state);
            Assert.That(state.states.Length, Is.EqualTo(513));
            Assert.That(state.foam + state.spray, Is.LessThanOrEqualTo(513));
            Assert.That(fluid.CaptureSnapshot().faults, Is.Zero);
        }

        [TestCase(8192, 32, 4)]
        [TestCase(16384, 32, 4)]
        [TestCase(32768, 48, 8)]
        [TestCase(65536, 64, 12)]
        public void BalancedPresetsRemainFinite(int count, int grid, int steps)
        {
            fluid.SelectPreset(count);
            Assert.That(fluid.gridResolution, Is.EqualTo(grid));
            Assert.That(fluid.substeps, Is.EqualTo(steps));
            Assert.That(fluid.whitewaterCapacity, Is.EqualTo(Mathf.Min(count, 32768)));
            fluid.StepSimulation(90);
            MlsMpmFluid.Snapshot state = fluid.CaptureSnapshot();
            Assert.That(state.faults, Is.Zero);
            Assert.That(state.nonFiniteParticles, Is.Zero);
            Assert.That(state.outsideParticles, Is.Zero);
            Assert.That(state.gridMass, Is.EqualTo(count).Within(count * 0.0005));
            AssertHealthy(fluid.CaptureWhitewaterSnapshot());
        }

        [Test]
        public void GridResolutionDoesNotChangeTheInitialPhysicalWaterBlock()
        {
            MlsMpmFluid.Snapshot a = fluid.CaptureSnapshot();
            Vector3 minimum = fluid.GridOrigin + fluid.CellSize * a.minimum;
            Vector3 maximum = fluid.GridOrigin + fluid.CellSize * a.maximum;
            float density = fluid.RestDensity / Mathf.Pow(fluid.CellSize, 3);
            fluid.gridResolution = 48;
            fluid.Initialize();
            MlsMpmFluid.Snapshot b = fluid.CaptureSnapshot();
            Assert.That(Vector3.Distance(minimum, fluid.GridOrigin + fluid.CellSize * b.minimum), Is.LessThan(0.0001));
            Assert.That(Vector3.Distance(maximum, fluid.GridOrigin + fluid.CellSize * b.maximum), Is.LessThan(0.0001));
            Assert.That(fluid.RestDensity / Mathf.Pow(fluid.CellSize, 3), Is.EqualTo(density).Within(density * 0.0001));
        }

        static void AssertHealthy(MlsMpmFluid.WhitewaterSnapshot state)
        {
            Assert.That(state.nonFinite, Is.Zero);
            Assert.That(state.outside, Is.Zero);
            Assert.That(state.insideSphere, Is.Zero);
        }
    }
}
