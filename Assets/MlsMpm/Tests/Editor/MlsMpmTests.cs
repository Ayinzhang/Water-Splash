using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace WaterSplash.Tests
{
    public sealed class MlsMpmTests
    {
        GameObject owner;
        MlsMpmFluid fluid;
        GameObject sphere;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("A compute-capable GPU is required.");
            owner = new GameObject("MLS-MPM GPU test");
            owner.SetActive(false);
            fluid = owner.AddComponent<MlsMpmFluid>();
            fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/MlsMpm.compute");
            fluid.showParticles = false;
            fluid.showControls = false;
            fluid.particleCount = 512;
            fluid.gridResolution = 16;
            fluid.domainSize = 4;
            fluid.substeps = 4;
            fluid.Initialize();
            Assert.That(fluid.Error, Is.Null);
        }

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                fluid.Release();
                Object.DestroyImmediate(owner);
            }
            if (sphere != null) Object.DestroyImmediate(sphere);
        }

        [Test]
        public void TransferConservesMassAndGravityMovesTheLiquidDown()
        {
            MlsMpmFluid.Snapshot start = fluid.CaptureSnapshot();
            fluid.StepSimulation(20);
            MlsMpmFluid.Snapshot end = fluid.CaptureSnapshot();
            AssertHealthy(end);
            Assert.That(end.gridMass, Is.EqualTo(512).Within(0.2));
            Assert.That(end.center.y, Is.LessThan(start.center.y - 0.1f));
            Assert.That(end.simulatedSeconds, Is.EqualTo(20 * MlsMpmFluid.FixedTick).Within(1e-8));
        }

        [Test]
        public void StationaryWaterDoesNotAcquireLargeVelocityWithoutForces()
        {
            fluid.gravity = Vector3.zero;
            fluid.StepSimulation(10);
            MlsMpmFluid.Snapshot state = fluid.CaptureSnapshot();
            AssertHealthy(state);
            Assert.That(state.maximumSpeed, Is.LessThan(0.05f));
        }

        [Test]
        public void ParticleAndGridBoundsHandleNonMultipleOfThreadGroupCount()
        {
            fluid.particleCount = 513;
            fluid.gridResolution = 17;
            fluid.Initialize();
            fluid.StepSimulation(10);
            MlsMpmFluid.Snapshot state = fluid.CaptureSnapshot();
            AssertHealthy(state);
            Assert.That(state.gridMass, Is.EqualTo(513).Within(0.2));
        }

        [Test]
        public void LegacySolverIgnoresPbSortingInterval()
        {
            fluid.enableWhitewater = false;
            fluid.spatialSortInterval = 0;
            fluid.Initialize();
            fluid.StepSimulation(30);
            MlsMpmFluid.Snapshot unsorted = fluid.CaptureSnapshot();
            fluid.spatialSortInterval = 1;
            fluid.Initialize();
            fluid.StepSimulation(30);
            MlsMpmFluid.Snapshot sorted = fluid.CaptureSnapshot();
            AssertHealthy(sorted);
            Assert.That(sorted.gridMass, Is.EqualTo(unsorted.gridMass).Within(1e-4));
            Assert.That((sorted.center - unsorted.center).magnitude, Is.LessThan(1e-4f));
            Assert.That((sorted.minimum - unsorted.minimum).magnitude, Is.LessThan(1e-4f));
            Assert.That((sorted.maximum - unsorted.maximum).magnitude, Is.LessThan(1e-4f));
            Assert.That(sorted.maximumSpeed, Is.EqualTo(unsorted.maximumSpeed).Within(1e-3));
        }

        [Test]
        public void SphereProjectionKeepsParticlesOutsideObstacle()
        {
            sphere = new GameObject("Test sphere");
            sphere.transform.position = new Vector3(1.3f, 1.3f, 2);
            fluid.sphereObstacle = sphere.transform;
            fluid.sphereRadius = 0.5f;
            fluid.Initialize();
            fluid.StepSimulation(20);
            MlsMpmFluid.Snapshot state = fluid.CaptureSnapshot();
            AssertHealthy(state);
            Assert.That(state.insideSphereParticles, Is.Zero);
        }

        [Test]
        public void ResetClearsTimeAndDiagnostics()
        {
            fluid.StepSimulation(3);
            fluid.Initialize();
            MlsMpmFluid.Snapshot state = fluid.CaptureSnapshot();
            Assert.That(state.simulatedSeconds, Is.Zero);
            Assert.That(state.faults, Is.Zero);
            Assert.That(state.maximumSpeed, Is.Zero);
        }

        [Test]
        public void StrongInvalidStepSetsFaultInsteadOfAdvectingOutOfBounds()
        {
            fluid.gravity = new Vector3(0, -10000000, 0);
            fluid.StepSimulation(1);
            MlsMpmFluid.Snapshot state = fluid.CaptureSnapshot();
            Assert.That(state.faults & 16u, Is.Not.Zero);
            Assert.That(state.nonFiniteParticles, Is.Zero);
            Assert.That(state.outsideParticles, Is.Zero);
        }

        [Test]
        public void DamBreakAndPushRemainFiniteForFourSimulatedSeconds()
        {
            fluid.particleCount = 4096;
            fluid.gridResolution = 32;
            fluid.Initialize();
            fluid.StepSimulation(120);
            MlsMpmFluid.Snapshot falling = fluid.CaptureSnapshot();
            AssertHealthy(falling);
            Assert.That(falling.gridMass, Is.EqualTo(4096).Within(2));
            fluid.Kick();
            fluid.StepSimulation(120);
            MlsMpmFluid.Snapshot pushed = fluid.CaptureSnapshot();
            AssertHealthy(pushed);
            Assert.That(pushed.gridMass, Is.EqualTo(4096).Within(2));
            Assert.That(pushed.simulatedSeconds, Is.EqualTo(4).Within(1e-8));
        }

        [Test]
        public void ComparisonImportPreservesWorldPositionsAndRestSpacing()
        {
            fluid.transform.position = new Vector3(-2, 0, -2);
            fluid.Initialize();
            float spacing = 0.2f;
            var positions = new Vector3[512];
            Vector3 mean = Vector3.zero;
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = new Vector3(-1.5f, 0.5f, -1.5f) +
                    spacing * new Vector3(i % 8, (i / 8) % 8, i / 64);
                mean += positions[i];
            }
            mean /= positions.Length;
            fluid.SetComparisonInitialPositions(positions, spacing);
            MlsMpmFluid.Snapshot imported = fluid.CaptureSnapshot();
            Vector3 worldMean = imported.center * fluid.CellSize + fluid.GridOrigin;
            Assert.That(Vector3.Distance(worldMean, mean), Is.LessThan(0.0001f));
            Assert.That(fluid.RestDensity, Is.EqualTo(Mathf.Pow(fluid.CellSize / spacing, 3)).Within(0.0001f));
            Assert.That(imported.maximumSpeed, Is.Zero);
            Assert.That(imported.simulatedSeconds, Is.Zero);
        }

        [Test]
        public void ComparisonImportRejectsChangesAfterSimulationStarts()
        {
            fluid.StepSimulation(1);
            Assert.Throws<System.ArgumentException>(() =>
                fluid.SetComparisonInitialPositions(new Vector3[512], 0.2f));
        }

        [TestCase(32, 4)]
        [TestCase(48, 8)]
        [TestCase(64, 12)]
        [TestCase(80, 16)]
        public void FinerGridPresetsIncreaseSubsteps(int grid, int expected)
        {
            Assert.That(MlsMpmFluid.RecommendedSubsteps(grid), Is.EqualTo(expected));
        }

        [Test]
        public void ReinitializeDropsOldVelocityAndDiagnosticGeneration()
        {
            fluid.StepSimulation(12);
            for (int i = 0; i < 4; i++)
            {
                fluid.gridResolution = i % 2 == 0 ? 32 : 16;
                fluid.particleCount = i % 2 == 0 ? 4096 : 512;
                fluid.Initialize();
                AssertHealthy(fluid.CaptureSnapshot());
                Assert.That(fluid.CaptureSnapshot().maximumSpeed, Is.Zero);
                fluid.StepSimulation(4);
                AssertHealthy(fluid.CaptureSnapshot());
            }
        }

        static void AssertHealthy(MlsMpmFluid.Snapshot state)
        {
            Assert.That(state.faults, Is.Zero, "GPU fault bits");
            Assert.That(state.nonFiniteParticles, Is.Zero);
            Assert.That(state.outsideParticles, Is.Zero);
        }
    }
}
