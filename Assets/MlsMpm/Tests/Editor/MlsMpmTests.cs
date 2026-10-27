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

        static void AssertHealthy(MlsMpmFluid.Snapshot state)
        {
            Assert.That(state.faults, Is.Zero, "GPU fault bits");
            Assert.That(state.nonFiniteParticles, Is.Zero);
            Assert.That(state.outsideParticles, Is.Zero);
        }
    }
}
