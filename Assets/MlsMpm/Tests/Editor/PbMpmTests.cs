using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WaterSplash.Tests
{
    public sealed class PbMpmTests
    {
        GameObject owner, sphere;
        MlsMpmFluid fluid;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("A compute-capable GPU is required.");
            owner = new GameObject("PB-MPM GPU test");
            owner.SetActive(false);
            fluid = owner.AddComponent<MlsMpmFluid>();
            fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/MlsMpm.compute");
            fluid.solverMethod = MlsMpmFluid.SolverMethod.PositionBasedMpm;
            fluid.showParticles = fluid.showControls = fluid.enableWhitewater = false;
            fluid.particleCount = 512;
            fluid.gridResolution = 16;
            fluid.substeps = 8;
            fluid.Initialize();
            Assert.That(fluid.Error, Is.Null);
        }

        [TearDown]
        public void TearDown()
        {
            if (fluid != null) fluid.Release();
            if (owner != null) Object.DestroyImmediate(owner);
            if (sphere != null) Object.DestroyImmediate(sphere);
        }

        [TestCase(512, 16)]
        [TestCase(513, 17)]
        public void GravityConservesMassBoundsAndFullTickTime(int count, int resolution)
        {
            fluid.particleCount = count;
            fluid.gridResolution = resolution;
            fluid.Initialize();
            var start = fluid.CaptureSnapshot();
            fluid.StepSimulation(30);
            var end = fluid.CaptureSnapshot();
            AssertHealthy(end);
            Assert.That(end.gridMass, Is.EqualTo(count).Within(0.25));
            Assert.That(end.center.y, Is.LessThan(start.center.y - 0.1f));
            Assert.That(end.simulatedSeconds, Is.EqualTo(0.5).Within(1e-8));
            Assert.That(end.flooredVolumeParticles, Is.Zero);
        }

        [Test]
        public void NoForcesDoesNotCreateMotion()
        {
            fluid.gravity = Vector3.zero;
            fluid.StepSimulation(60);
            var state = fluid.CaptureSnapshot();
            AssertHealthy(state);
            Assert.That(state.maximumSpeed, Is.LessThan(0.05f));
        }

        [Test]
        public void SolverSwitchRequiresReset()
        {
            fluid.solverMethod = MlsMpmFluid.SolverMethod.MlsMpm;
            Assert.Throws<InvalidOperationException>(() => fluid.StepSimulation(1));
            fluid.Initialize();
            fluid.StepSimulation(10);
            Assert.That(fluid.CaptureSnapshot().faults, Is.Zero);
            fluid.solverMethod = MlsMpmFluid.SolverMethod.PositionBasedMpm;
            fluid.Initialize();
            fluid.StepSimulation(10);
            AssertHealthy(fluid.CaptureSnapshot());
        }

        [Test]
        public void SortingCarriesIntegratedVolumeWithParticles()
        {
            fluid.spatialSortInterval = 0;
            fluid.Initialize();
            fluid.StepSimulation(60);
            var unsorted = fluid.CaptureSnapshot();
            fluid.spatialSortInterval = 1;
            fluid.Initialize();
            fluid.StepSimulation(60);
            var sorted = fluid.CaptureSnapshot();
            AssertHealthy(sorted);
            Assert.That((sorted.center - unsorted.center).magnitude, Is.LessThan(1e-4));
            Assert.That(sorted.minimumVolumeRatio, Is.EqualTo(unsorted.minimumVolumeRatio).Within(1e-4));
            Assert.That(sorted.maximumSpeed, Is.EqualTo(unsorted.maximumSpeed).Within(1e-3));
        }

        [Test]
        public void MovingSphereAndWhitewaterStayFinite()
        {
            sphere = new GameObject("PB-MPM sphere");
            sphere.transform.position = new Vector3(1.3f, 1.3f, 2);
            fluid.sphereObstacle = sphere.transform;
            fluid.sphereRadius = 0.5f;
            fluid.enableWhitewater = true;
            fluid.whitewaterCapacity = 512;
            fluid.Initialize();
            for (int tick = 0; tick < 120; tick++)
            {
                sphere.transform.position = new Vector3(1.3f + 0.3f * Mathf.Sin(tick * 0.04f), 1.3f, 2);
                fluid.StepSimulation(1);
            }
            var state = fluid.CaptureSnapshot();
            AssertHealthy(state);
            Assert.That(state.insideSphereParticles, Is.Zero);
            var foam = fluid.CaptureWhitewaterSnapshot();
            Assert.That(foam.nonFinite, Is.Zero);
            Assert.That(foam.outside, Is.Zero);
        }

        [Test]
        public void RestingPoolSettlesWithoutLosingItsHeight()
        {
            fluid.transform.position = new Vector3(-2, 0, -2);
            fluid.particleCount = 4096;
            fluid.gridResolution = 32;
            fluid.Initialize();
            var positions = new Vector3[4096];
            for (int i = 0; i < positions.Length; i++)
                positions[i] = new Vector3(-2, 0, -2) +
                    0.125f * new Vector3(i % 32 + 0.5f, i / 1024 + 0.5f, (i / 32) % 32 + 0.5f);
            fluid.SetComparisonInitialPositions(positions, 0.125f);
            for (int i = 0; i < 10; i++) fluid.StepSimulation(120);
            var state = fluid.CaptureSnapshot();
            AssertHealthy(state);
            Assert.That(Math.Sqrt(2 * state.meanKineticEnergy), Is.LessThan(0.04), "Resting RMS velocity in m/s");
            Assert.That(state.maximumSpeed, Is.LessThan(0.15));
            Assert.That(state.p99WorldHeight, Is.InRange(0.35f, 0.55f));
            Assert.That(state.flooredVolumeParticles, Is.Zero);
        }

        static void AssertHealthy(MlsMpmFluid.Snapshot state)
        {
            Assert.That(state.faults, Is.Zero);
            Assert.That(state.nonFiniteParticles, Is.Zero);
            Assert.That(state.outsideParticles, Is.Zero);
            Assert.That(state.minimumVolumeRatio, Is.GreaterThanOrEqualTo(0.1f));
        }
    }
}
