using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace WaterSplash.Tests
{
    public sealed class BottleBoundaryTests
    {
        GameObject owner;
        MlsMpmFluid fluid;
        BottleBoundary boundary;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("Compute shader required.");
            owner = new GameObject("Curved bottle boundary test");
            owner.SetActive(false);
            fluid = owner.AddComponent<MlsMpmFluid>();
            boundary = owner.AddComponent<BottleBoundary>();
            boundary.fluid = fluid;
            fluid.simulationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MlsMpm/Shaders/BottleMlsMpm.compute");
            fluid.transform.position = new Vector3(-2,0,-2);
            fluid.particleCount = 32768;
            fluid.gridResolution = 80;
            fluid.substeps = BottleVoyage.SubstepsForParticleCount(fluid.particleCount);
            fluid.showParticles = fluid.showControls = false;
            // Edit-mode tests do not invoke Awake. Use the same scene-local binding.
            var configure = typeof(BottleBoundary).GetMethod("Configure",BindingFlags.Instance|BindingFlags.NonPublic);
            fluid.configureSimulation = (Action<CommandBuffer,ComputeShader>)Delegate.CreateDelegate(
                typeof(Action<CommandBuffer,ComputeShader>),boundary,configure);
            boundary.ConfigureGridWindow();
            fluid.Initialize();
            Assert.That(fluid.Error,Is.Null);
            Vector3[] positions = boundary.CreateInitialFill(fluid.particleCount,out float spacing);
            fluid.SetComparisonInitialPositions(positions,spacing);
        }
        [TearDown]
        public void TearDown()
        {
            if(fluid!=null)fluid.Release();
            if(owner!=null)UnityEngine.Object.DestroyImmediate(owner);
        }
        [Test]
        public void LeftFacingBottleMirrorsFillProjectionAndGpuContainment()
        {
            boundary.mouthFacesLeft=true;
            fluid.Initialize();
            var positions=boundary.CreateInitialFill(fluid.particleCount,out float spacing);
            foreach(Vector3 p in positions)
                Assert.That(BottleBoundary.Distance(boundary.ToProfile(p),out _),Is.LessThan(-spacing*.8f));
            Vector3 shoulder=boundary.FromProfile(new Vector3(1.05f,.6f,.2f));
            Assert.That(shoulder.x,Is.LessThan(boundary.center.x));
            Vector3 projected=boundary.ClampInside(shoulder,.03f);
            Assert.That(BottleBoundary.Distance(boundary.ToProfile(projected),out _),Is.LessThanOrEqualTo(-.0299f));
            fluid.SetComparisonInitialPositions(positions,spacing);
            fluid.StepSimulation(120);
            Assert.That(fluid.CaptureSnapshot().faults,Is.Zero);
            foreach(var p in ReadParticles())
                Assert.That(BottleBoundary.Distance(boundary.ToProfile(
                    fluid.GridOrigin+(Vector3)p.position*fluid.CellSize),out _),Is.LessThanOrEqualTo(1e-4f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CompactGridPreserves64KWorldLatticeLiquidAndWhitewater(bool mirrored)
        {
            boundary.mouthFacesLeft = mirrored;
            fluid.particleCount = 65536;
            fluid.substeps = BottleVoyage.SubstepsForParticleCount(fluid.particleCount);
            boundary.compactGrid = false;
            boundary.ConfigureGridWindow();
            Vector3 origin = fluid.GridOrigin;
            float cellSize = fluid.CellSize;
            Vector3[] positions = boundary.CreateInitialFill(fluid.particleCount, out float spacing);
            fluid.Initialize();
            fluid.SetComparisonInitialPositions(positions, spacing);
            var initial = ReadParticles();
            fluid.StepSimulation(120);
            var reference = ReadParticles();
            var referenceWhitewater = fluid.CaptureWhitewaterSnapshot();
            var referenceState = fluid.CaptureSnapshot();
            Assert.That(referenceState.faults, Is.Zero);

            boundary.compactGrid = true;
            boundary.ConfigureGridWindow();
            fluid.Initialize();
            fluid.SetComparisonInitialPositions(positions, spacing);
            Assert.That(fluid.GridOrigin, Is.EqualTo(origin));
            Assert.That(fluid.CellSize, Is.EqualTo(cellSize));
            Vector3Int size = fluid.GridDimensions;
            Assert.That(size.x, Is.EqualTo(80));
            Assert.That(size.x * size.y * size.z, Is.LessThanOrEqualTo(80 * 80 * 80 / 4));
            Assert.That(fluid.GridWindow.zMin, Is.GreaterThan(0));
            var imported = ReadParticles();
            for (int i = 0; i < initial.Length; i++)
                Assert.That(imported[i].position, Is.EqualTo(initial[i].position));
            fluid.StepSimulation(120);
            var compact = ReadParticles();
            for (int i = 0; i < reference.Length; i++)
            {
                Assert.That(compact[i].position, Is.EqualTo(reference[i].position), "Particle " + i);
                Assert.That(compact[i].velocity, Is.EqualTo(reference[i].velocity), "Velocity " + i);
                Assert.That(compact[i].affineX, Is.EqualTo(reference[i].affineX));
                Assert.That(compact[i].affineY, Is.EqualTo(reference[i].affineY));
                Assert.That(compact[i].affineZ, Is.EqualTo(reference[i].affineZ));
            }
            var whitewater = fluid.CaptureWhitewaterSnapshot();
            Assert.That(whitewater.born, Is.EqualTo(referenceWhitewater.born));
            Assert.That(whitewater.foam, Is.EqualTo(referenceWhitewater.foam));
            Assert.That(whitewater.spray, Is.EqualTo(referenceWhitewater.spray));
            for (int i = 0; i < whitewater.states.Length; i++)
            {
                Assert.That(whitewater.states[i].position, Is.EqualTo(referenceWhitewater.states[i].position));
                Assert.That(whitewater.states[i].velocity, Is.EqualTo(referenceWhitewater.states[i].velocity));
                Assert.That(whitewater.states[i].normalType, Is.EqualTo(referenceWhitewater.states[i].normalType));
            }
            var state = fluid.CaptureSnapshot();
            Assert.That(state.faults, Is.Zero);
            Assert.That(state.outsideParticles, Is.Zero);
            Assert.That(state.gridMass, Is.EqualTo(referenceState.gridMass));
            Assert.That(state.peakDensityRatio, Is.EqualTo(referenceState.peakDensityRatio));
            Assert.That(whitewater.nonFinite, Is.Zero);
            Assert.That(whitewater.outside, Is.Zero);
        }

        [Test]
        public void CompactGridHandlesPartialGroupsAndReportsNonFiniteParticles()
        {
            fluid.particleCount = 32769;
            fluid.Initialize();
            fluid.SetComparisonInitialPositions(boundary.CreateInitialFill(fluid.particleCount, out float spacing), spacing);
            fluid.StepSimulation(10);
            var state = fluid.CaptureSnapshot();
            Assert.That(state.faults, Is.Zero);
            Assert.That(state.peakDensityRatio, Is.GreaterThan(0));
            Assert.That(state.maximumSpeed, Is.GreaterThan(0));
            var particles = ReadParticles();
            particles[particles.Length - 1].position.x = float.NaN;
            UploadParticles(particles);
            fluid.StepSimulation(1);
            Assert.That(fluid.SynchronizeDiagnostics() & 1u, Is.EqualTo(1u));
        }

        [Test]
        public void GridWindowChangesRequireResetAndRejectUnsupportedImports()
        {
            boundary.compactGrid = false;
            boundary.ConfigureGridWindow();
            Assert.Throws<InvalidOperationException>(() => fluid.StepSimulation(1));
            boundary.compactGrid = true;
            boundary.ConfigureGridWindow();
            var positions = boundary.CreateInitialFill(fluid.particleCount, out float spacing);
            positions[0] = fluid.GridOrigin + new Vector3(20, 50, 10) * fluid.CellSize;
            Assert.Throws<ArgumentException>(() => fluid.SetComparisonInitialPositions(positions, spacing));
        }

        [Test]
        [TestCase(16384)]
        [TestCase(32768)]
        [TestCase(65536)]
        [TestCase(131072)]
        public void PresetsRetainReferenceWaterVolumeAndScaleSpacing(int count)
        {
            boundary.CreateInitialFill(32768,out float referenceSpacing);
            float referenceVolume=boundary.WaterVolume;
            fluid.particleCount=count;
            fluid.substeps=BottleVoyage.SubstepsForParticleCount(count);
            fluid.Initialize();
            Vector3[] positions=boundary.CreateInitialFill(count,out float spacing);
            Assert.That(positions.Length,Is.EqualTo(count));
            Assert.That(boundary.WaterVolume,Is.EqualTo(referenceVolume).Within(1e-5f));
            Assert.That(spacing/referenceSpacing,Is.EqualTo(Mathf.Pow(32768f/count,1f/3)).Within(1e-5f));
            foreach(Vector3 p in positions)
            {
                Assert.That(p.y,Is.LessThanOrEqualTo(boundary.fillHeight));
                Assert.That(BottleBoundary.Distance(boundary.ToProfile(p),out _),Is.LessThan(0));
            }
            fluid.SetComparisonInitialPositions(positions,spacing);
            fluid.StepSimulation(60);
            var snapshot=fluid.CaptureSnapshot();
            Assert.That(snapshot.faults,Is.Zero);
            Assert.That(snapshot.gridMass,Is.EqualTo(count).Within(2));
            Assert.That(count*fluid.CellSize*fluid.CellSize*fluid.CellSize/fluid.RestDensity,
                Is.EqualTo(referenceVolume).Within(1e-5f));
            AssertInside(ReadParticles());
            fluid.particleRadiusScale=spacing*.34f/fluid.CellSize;
            Assert.That(fluid.DisplayParticleRadius,Is.EqualTo(spacing*.34f).Within(1e-6f));
            var buffer=ParticleBuffer();
            double seconds=fluid.SimulatedSeconds;
            fluid.SetSurfaceRendering(false);
            Assert.That(fluid.renderSurface,Is.False);
            fluid.SetSurfaceRendering(true);
            Assert.That(fluid.renderSurface,Is.True);
            Assert.That(ParticleBuffer(),Is.SameAs(buffer));
            Assert.That(fluid.SimulatedSeconds,Is.EqualTo(seconds));
        }

        [Test]
        public void SmallerFillKeeps32KAndReducesParticleSpacing()
        {
            Vector3[] positions=boundary.CreateInitialFill(32768,out float spacing);
            Assert.That(positions.Length,Is.EqualTo(32768));
            Assert.That(spacing,Is.InRange(.03f,.04f));
            float oldSpacing=4*(16f/27)/32;
            Assert.That(boundary.WaterVolume,Is.LessThan(32768*oldSpacing*oldSpacing*oldSpacing*.2f));
            foreach(Vector3 p in positions)
            {
                Assert.That(p.y,Is.LessThanOrEqualTo(boundary.fillHeight));
                Assert.That(BottleBoundary.Distance(p-boundary.center,out _),Is.LessThan(-spacing*.8f));
            }
        }
        [Test]
        public void ShoulderNormalAndProjectionFollowTheNarrowingBottle()
        {
            float radius=BottleBoundary.Radius(.98f,out float slope);
            Assert.That(slope,Is.LessThan(-.5f));
            BottleBoundary.Distance(new Vector3(.98f,radius,0),out Vector3 normal);
            Assert.That(normal.x,Is.GreaterThan(.5f));
            Vector3 world=boundary.center+new Vector3(1.1f,.62f,.25f);
            Vector3 projected=boundary.ClampInside(world,.03f);
            Assert.That(BottleBoundary.Distance(projected-boundary.center,out _),Is.LessThanOrEqualTo(-.0299f));
        }
        [Test]
        public void CurvedWallsRetainAllParticlesAndGridMassUnderGravity()
        {
            fluid.StepSimulation(120);
            var snapshot=fluid.CaptureSnapshot();
            Assert.That(snapshot.faults,Is.Zero);
            Assert.That(snapshot.nonFiniteParticles,Is.Zero);
            Assert.That(snapshot.outsideParticles,Is.Zero);
            Assert.That(snapshot.gridMass,Is.EqualTo(32768).Within(2));
            AssertInside(ReadParticles());
        }
        [Test]
        public void GpuProjectsShoulderBottomAndCorkAndContainsSpray()
        {
            var states=ReadParticles();
            // Seed finite particles at the shoulder, rounded heel and cork plane,
            // including outward velocities. Keep all within the original grid.
            Vector3[] local={new Vector3(.98f,.59f,0),new Vector3(-1.80f,0,.53f),
                new Vector3(1.78f,0,0),new Vector3(-.5f,-.76f,0)};
            Vector3[] outward={new Vector3(1,1,0).normalized,Vector3.forward,Vector3.right,Vector3.down};
            for(int i=0;i<local.Length;i++)
            {
                Vector3 p=(boundary.center+local[i]-fluid.GridOrigin)/fluid.CellSize;
                states[i].position=new Vector4(p.x,p.y,p.z,1);
                Vector3 v=outward[i]*(2/fluid.CellSize);
                states[i].velocity=new Vector4(v.x,v.y,v.z,0);
            }
            UploadParticles(states);
            var foamBuffer=(ComputeBuffer)typeof(MlsMpmFluid).GetField("whitewater",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(fluid);
            var whitewater=new MlsMpmFluid.WhitewaterState[fluid.whitewaterCapacity];
            for(int i=0;i<local.Length;i++)
            {
                whitewater[i].position=states[i].position;
                whitewater[i].position.w=2;
                whitewater[i].velocity=states[i].velocity;
            }
            foamBuffer.SetData(whitewater);
            fluid.whitewaterEmissionRate=0;
            fluid.StepSimulation(1);
            Assert.That(fluid.CaptureSnapshot().faults,Is.Zero);
            AssertInside(ReadParticles());
            var foam=fluid.CaptureWhitewaterSnapshot();
            Assert.That(foam.spray+foam.foam,Is.GreaterThanOrEqualTo(4));
            foreach(var p in foam.states)
                if(p.position.w>0)
                    Assert.That(BottleBoundary.Distance(fluid.GridOrigin+(Vector3)p.position*fluid.CellSize-boundary.center,out _),
                        Is.LessThanOrEqualTo(1e-4f));
        }
        [Test]
        [TestCase(16384, false)]
        [TestCase(32768, false)]
        [TestCase(65536, false)]
        [TestCase(131072, false)]
        [TestCase(65536, true)]
        [TestCase(131072, true)]
        public void FastBoatGesturesSweepHullAndGenerateSimulatedWhitewater(int count, bool optimized)
        {
            var sphere=new GameObject("Fast hull");
            var boat=new GameObject("Immediate visual boat");
            try
            {
                fluid.particleCount=count;
                fluid.useParticleSoA = fluid.cacheGridBoundary = optimized;
                fluid.reduceDiagnosticAtomics = boundary.tightGrid = optimized;
                boundary.ConfigureGridWindow();
                fluid.substeps=BottleVoyage.SubstepsForParticleCount(count);
                fluid.sphereObstacle=sphere.transform;
                fluid.sphereRadius=.12f;
                fluid.whitewaterCapacity=8192;
                var voyage=owner.AddComponent<BottleVoyage>();
                voyage.fluid=fluid;voyage.boundary=boundary;voyage.boat=boat.transform;
                boat.transform.localScale=Vector3.one*.39f;
                boat.transform.position=new Vector3(-.7f,.7f,0);
                sphere.transform.position=new Vector3(-.7f,.61f,0);
                fluid.Initialize();
                fluid.SetComparisonInitialPositions(boundary.CreateInitialFill(count,out float spacing),spacing);
                fluid.StepSimulation(60);
                uint initialBorn=fluid.CaptureWhitewaterSnapshot().born;
                var advance=typeof(BottleVoyage).GetMethod("AdvanceHull",BindingFlags.Instance|BindingFlags.NonPublic);
                float tick=(float)MlsMpmFluid.FixedTick;
                for(int i=0;i<240;i++)
                {
                    // Teleport the visible boat repeatedly, as with very fast dragging.
                    boat.transform.position=i/24%2==0?new Vector3(.1f,.7f,.18f):new Vector3(-1.0f,.7f,-.18f);
                    Vector3 previous=sphere.transform.position;
                    advance.Invoke(voyage,new object[]{tick});
                    Assert.That(Vector3.Distance(previous,sphere.transform.position)/fluid.substeps,
                        Is.LessThanOrEqualTo(fluid.CellSize*.2501f));
                    fluid.StepSimulation(1);
                }
                var snapshot=fluid.CaptureSnapshot();
                Assert.That(snapshot.faults,Is.Zero);
                Assert.That(snapshot.nonFiniteParticles,Is.Zero);
                AssertInside(ReadParticles());
                var whitewater=fluid.CaptureWhitewaterSnapshot();
                Assert.That(whitewater.nonFinite,Is.Zero);
                Assert.That(whitewater.born,Is.GreaterThan(initialBorn),"Fast hull motion must generate real whitewater.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sphere);
                UnityEngine.Object.DestroyImmediate(boat);
            }
        }

        [Test]
        public void MovingStirrerAndResetKeepBottleWaterFiniteAndContained()
        {
            var sphere=new GameObject("Bottle test stirrer");
            try
            {
                fluid.sphereObstacle=sphere.transform;
                fluid.sphereRadius=.12f;
                sphere.transform.position=new Vector3(.2f,.49f,0);
                fluid.Initialize();
                fluid.SetComparisonInitialPositions(boundary.CreateInitialFill(fluid.particleCount,out float spacing),spacing);
                for(int tick=0;tick<240;tick++)
                {
                    float t=tick*(float)MlsMpmFluid.FixedTick;
                    sphere.transform.position=boundary.ClampInside(new Vector3(-.4f+.8f*Mathf.Cos(t*1.4f),.49f,
                        .30f*Mathf.Sin(t*1.7f)),fluid.sphereRadius+fluid.CellSize*.6f);
                    fluid.StepSimulation(1);
                }
                var snapshot=fluid.CaptureSnapshot();
                Assert.That(snapshot.faults,Is.Zero);
                Assert.That(snapshot.nonFiniteParticles,Is.Zero);
                Assert.That(snapshot.gridMass,Is.EqualTo(32768).Within(2));
                AssertInside(ReadParticles());
                var foam=fluid.CaptureWhitewaterSnapshot();
                Assert.That(foam.nonFinite,Is.Zero);
                foreach(var p in foam.states) if(p.position.w>0)
                    Assert.That(BottleBoundary.Distance(fluid.GridOrigin+(Vector3)p.position*fluid.CellSize-boundary.center,out _),
                        Is.LessThanOrEqualTo(1e-4f));
                fluid.Initialize();
                fluid.SetComparisonInitialPositions(boundary.CreateInitialFill(fluid.particleCount,out spacing),spacing);
                fluid.StepSimulation(10);
                Assert.That(fluid.CaptureSnapshot().faults,Is.Zero);
                AssertInside(ReadParticles());
            }
            finally{UnityEngine.Object.DestroyImmediate(sphere);}
        }
        ComputeBuffer ParticleBuffer()=>(ComputeBuffer)typeof(MlsMpmFluid).GetProperty("ParticleBuffer",
            BindingFlags.Instance|BindingFlags.NonPublic).GetValue(fluid);
        void UploadParticles(MlsMpmFluid.ParticleState[] states) =>
            typeof(MlsMpmFluid).GetMethod("UploadParticles", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(fluid, new object[] { states });
        MlsMpmFluid.ParticleState[] ReadParticles() => fluid.CaptureParticleStates();

        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public void CachedBoundaryAndSoAPreserve64KLiquidAndWhitewater(bool cache, bool soa, bool mirrored)
        {
            fluid.particleCount = 65536;
            boundary.mouthFacesLeft = mirrored;
            Vector3[] positions = boundary.CreateInitialFill(fluid.particleCount, out float spacing);
            fluid.Initialize();
            fluid.SetComparisonInitialPositions(positions, spacing);
            fluid.StepSimulation(60);
            var reference = ReadParticles();
            var referenceFoam = fluid.CaptureWhitewaterSnapshot();
            fluid.cacheGridBoundary = cache;
            fluid.useParticleSoA = soa;
            fluid.Initialize();
            fluid.SetComparisonInitialPositions(positions, spacing);
            fluid.StepSimulation(60);
            var actual = ReadParticles();
            for (int i = 0; i < actual.Length; i++)
            {
                Assert.That(actual[i].position, Is.EqualTo(reference[i].position), "position " + i);
                Assert.That(actual[i].velocity, Is.EqualTo(reference[i].velocity), "velocity " + i);
                Assert.That(actual[i].affineX, Is.EqualTo(reference[i].affineX), "affineX " + i);
                Assert.That(actual[i].affineY, Is.EqualTo(reference[i].affineY), "affineY " + i);
                Assert.That(actual[i].affineZ, Is.EqualTo(reference[i].affineZ), "affineZ " + i);
            }
            var foam = fluid.CaptureWhitewaterSnapshot();
            for (int i = 0; i < foam.states.Length; i++)
            {
                Assert.That(foam.states[i].position, Is.EqualTo(referenceFoam.states[i].position));
                Assert.That(foam.states[i].velocity, Is.EqualTo(referenceFoam.states[i].velocity));
                Assert.That(foam.states[i].normalType, Is.EqualTo(referenceFoam.states[i].normalType));
            }
            Assert.That(fluid.CaptureSnapshot().faults, Is.Zero);
            Assert.That(foam.outside, Is.Zero);
            Assert.That(foam.nonFinite, Is.Zero);
        }

        [Test]
        public void CachedBoundaryRefreshesAfterOriginCenterAndAxisChange()
        {
            fluid.cacheGridBoundary = true;
            fluid.Initialize();
            fluid.SetComparisonInitialPositions(boundary.CreateInitialFill(fluid.particleCount, out float spacing), spacing);
            fluid.StepSimulation(1);
            boundary.center += new Vector3(.01f, .02f, .03f);
            fluid.transform.position += new Vector3(.004f, .003f, .002f);
            boundary.mouthFacesLeft = true;
            var shader = (ComputeShader)typeof(MlsMpmFluid).GetField("solver",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fluid);
            using (var commands = new CommandBuffer())
            {
                fluid.configureSimulation(commands, shader);
                Graphics.ExecuteCommandBuffer(commands);
            }
            var buffer = (ComputeBuffer)typeof(MlsMpmFluid).GetField("staticGridBoundary",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fluid);
            var values = new Vector4[buffer.count];
            buffer.GetData(values);
            Vector3Int size = fluid.GridDimensions;
            for (int i = 0; i < values.Length; i += 997)
            {
                Vector3 cell = (Vector3)fluid.GridWindow.position +
                    new Vector3(i % size.x, i / size.x % size.y, i / (size.x * size.y));
                Vector3 world = fluid.GridOrigin + cell * fluid.CellSize;
                float distance = BottleBoundary.Distance(boundary.ToProfile(world), out Vector3 normal);
                normal.x *= boundary.AxisSign;
                Assert.That(values[i].w, Is.EqualTo(distance / fluid.CellSize).Within(.0002));
                Assert.That(((Vector3)values[i] - normal).magnitude, Is.LessThan(.0002));
            }
            fluid.Release();
            Assert.That(typeof(MlsMpmFluid).GetField("staticGridBoundary",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fluid), Is.Null);
        }

        [Test]
        public void SoAPartialGroupDiagnosticsAndLayoutChangesRemainExplicit()
        {
            fluid.useParticleSoA = fluid.cacheGridBoundary = true;
            fluid.particleCount = 32769;
            fluid.Initialize();
            fluid.SetComparisonInitialPositions(boundary.CreateInitialFill(fluid.particleCount, out float spacing), spacing);
            fluid.StepSimulation(5);
            var states = ReadParticles();
            states[states.Length - 1].affineZ.x = float.NaN;
            UploadParticles(states);
            fluid.StepSimulation(1);
            Assert.That(fluid.SynchronizeDiagnostics() & 1u, Is.EqualTo(1u));
            fluid.useParticleSoA = false;
            Assert.Throws<InvalidOperationException>(() => fluid.StepSimulation(1));
        }

        [TestCase(false, 16384)]
        [TestCase(false, 32768)]
        [TestCase(false, 65536)]
        [TestCase(false, 131072)]
        [TestCase(true, 65536)]
        [TestCase(true, 131072)]
        public void TightGridContainsAllNonzeroTransferNodesAcrossTheFullBottle(bool mirrored, int count)
        {
            fluid.particleCount = count;
            boundary.mouthFacesLeft = mirrored;
            boundary.tightGrid = true;
            boundary.CreateInitialFill(count, out float spacing);
            boundary.ConfigureGridWindow();
            Assert.That(fluid.GridDimensions, Is.EqualTo(new Vector3Int(80, 30, 30)));
            // Particle projection keeps liquid/whitewater at least this far inside the full profile.
            foreach (Vector2 section in BottleBoundary.Profile)
            for (int i = 0; i < 128; i++)
            {
                float angle = i * 2 * Mathf.PI / 128;
                float radial = Mathf.Max(0, section.y - .75f * spacing);
                Vector3 world = boundary.FromProfile(new Vector3(section.x,
                    radial * Mathf.Cos(angle), radial * Mathf.Sin(angle)));
                Vector3 p = (world - fluid.GridOrigin) / fluid.CellSize;
                Vector3Int support = Vector3Int.FloorToInt(p - Vector3.one * .5f);
                for (int z = 0; z < 3; z++)
                for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                    Assert.That(fluid.GridWindow.Contains(support + new Vector3Int(x, y, z)), Is.True);
            }
        }

        [Test]
        public void TightGridRejectsParticleSpacingThatWouldTruncateSupport()
        {
            fluid.particleCount = 262144;
            boundary.tightGrid = true;
            Assert.Throws<InvalidOperationException>(() => boundary.ConfigureGridWindow());
        }

        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public void TightGridAndReducedDiagnosticsPreserveMoving64KStatesAndPeaks(
            bool tight, bool reduced, bool mirrored)
        {
            fluid.particleCount = 65536;
            fluid.useParticleSoA = fluid.cacheGridBoundary = true;
            fluid.whitewaterCapacity = 8192;
            boundary.mouthFacesLeft = mirrored;
            Vector3[] positions = boundary.CreateInitialFill(fluid.particleCount, out float spacing);
            var hull = new GameObject("Diagnostic comparison hull");
            try
            {
                fluid.sphereObstacle = hull.transform;
                fluid.sphereRadius = .12f;
                MlsMpmFluid.ParticleState[] reference = null;
                MlsMpmFluid.WhitewaterSnapshot referenceFoam = null;
                uint[] referenceDiagnostics = null;
                for (int run = 0; run < 2; run++)
                {
                    boundary.tightGrid = run == 1 && tight;
                    fluid.reduceDiagnosticAtomics = run == 1 && reduced;
                    boundary.ConfigureGridWindow();
                    hull.transform.position = boundary.ClampInside(new Vector3(-.7f, .55f, 0), .16f);
                    fluid.Initialize();
                    fluid.SetComparisonInitialPositions(positions, spacing);
                    for (int tick = 0; tick < 240; tick++)
                    {
                        Vector3 target = tick / 24 % 2 == 0 ? new Vector3(.1f, .55f, .18f) :
                            new Vector3(-1, .55f, -.18f);
                        hull.transform.position = Vector3.MoveTowards(hull.transform.position,
                            boundary.ClampInside(target, .16f), .25f * fluid.CellSize * fluid.substeps);
                        fluid.StepSimulation(1);
                    }
                    var states = ReadParticles();
                    var foam = fluid.CaptureWhitewaterSnapshot();
                    var diagnostics = (ComputeBuffer)typeof(MlsMpmFluid).GetField("diagnostics",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fluid);
                    var values = new uint[8];
                    diagnostics.GetData(values);
                    Assert.That(values[0], Is.Zero);
                    Assert.That(foam.nonFinite, Is.Zero);
                    Assert.That(foam.outside, Is.Zero);
                    if (run == 0)
                    {
                        reference = states;
                        referenceFoam = foam;
                        referenceDiagnostics = values;
                        continue;
                    }
                    Assert.That(values, Is.EqualTo(referenceDiagnostics), "All peaks, fault fields and counters");
                    Assert.That(values[1], Is.GreaterThan(0));
                    Assert.That(values[2], Is.GreaterThan(0));
                    Assert.That(foam.born, Is.GreaterThan(0), "Comparison must exercise whitewater");
                    for (int i = 0; i < states.Length; i++)
                    {
                        Assert.That(states[i].position, Is.EqualTo(reference[i].position), "position " + i);
                        Assert.That(states[i].velocity, Is.EqualTo(reference[i].velocity), "velocity " + i);
                        Assert.That(states[i].affineX, Is.EqualTo(reference[i].affineX));
                        Assert.That(states[i].affineY, Is.EqualTo(reference[i].affineY));
                        Assert.That(states[i].affineZ, Is.EqualTo(reference[i].affineZ));
                    }
                    for (int i = 0; i < foam.states.Length; i++)
                    {
                        Assert.That(foam.states[i].position, Is.EqualTo(referenceFoam.states[i].position));
                        Assert.That(foam.states[i].velocity, Is.EqualTo(referenceFoam.states[i].velocity));
                        Assert.That(foam.states[i].normalType, Is.EqualTo(referenceFoam.states[i].normalType));
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(hull); }
        }

        [TestCase(16385)]
        [TestCase(65536)]
        public void WorkgroupSize64MatchesSize128Exactly(int count)
        {
            fluid.particleCount = count;
            fluid.useParticleSoA = fluid.cacheGridBoundary = fluid.reduceDiagnosticAtomics = true;
            fluid.whitewaterCapacity = 8192;
            boundary.tightGrid = true;
            boundary.ConfigureGridWindow();
            Vector3[] positions = boundary.CreateInitialFill(count, out float spacing);
            var hull = new GameObject("Workgroup comparison hull");
            try
            {
                fluid.sphereObstacle = hull.transform;
                fluid.sphereRadius = .12f;
                MlsMpmFluid.ParticleState[] reference = null;
                uint[] referenceDiagnostics = null;
                foreach (var mode in new[] { MlsMpmFluid.GroupSizeMode.Fixed128, MlsMpmFluid.GroupSizeMode.Fixed64 })
                {
                    fluid.groupSizeMode = mode;
                    hull.transform.position = boundary.ClampInside(new Vector3(-.7f, .55f, 0), .16f);
                    fluid.Initialize();
                    Assert.That(fluid.GroupSizeSummary, Is.EqualTo(mode == MlsMpmFluid.GroupSizeMode.Fixed64 ?
                        "64/64/64/64/64" : "128/128/128/128/128"));
                    fluid.SetComparisonInitialPositions(positions, spacing);
                    for (int tick = 0; tick < 90; tick++)
                    {
                        Vector3 target = tick / 24 % 2 == 0 ? new Vector3(.1f, .55f, .18f) : new Vector3(-1, .55f, -.18f);
                        hull.transform.position = Vector3.MoveTowards(hull.transform.position,
                            boundary.ClampInside(target, .16f), .25f * fluid.CellSize * fluid.substeps);
                        fluid.StepSimulation(1);
                    }
                    var states = ReadParticles();
                    var diagnostics = (ComputeBuffer)typeof(MlsMpmFluid).GetField("diagnostics",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fluid);
                    var values = new uint[8];
                    diagnostics.GetData(values);
                    Assert.That(values[0], Is.Zero);
                    if (reference == null)
                    {
                        reference = states;
                        referenceDiagnostics = values;
                        continue;
                    }
                    Assert.That(values, Is.EqualTo(referenceDiagnostics), "Peaks and counters");
                    for (int i = 0; i < states.Length; i++)
                    {
                        Assert.That(states[i].position, Is.EqualTo(reference[i].position), "position " + i);
                        Assert.That(states[i].velocity, Is.EqualTo(reference[i].velocity), "velocity " + i);
                        Assert.That(states[i].affineX, Is.EqualTo(reference[i].affineX));
                        Assert.That(states[i].affineY, Is.EqualTo(reference[i].affineY));
                        Assert.That(states[i].affineZ, Is.EqualTo(reference[i].affineZ));
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(hull); }
        }

        [Test]
        public void AutoWorkgroupSizeStaysAt128InEditor()
        {
            fluid.groupSizeMode = MlsMpmFluid.GroupSizeMode.Auto;
            fluid.Initialize();
            Assert.That(fluid.IsCalibrating, Is.False);
            Assert.That(fluid.GroupSizeSummary, Is.EqualTo("128/128/128/128/128"));
            Assert.That(fluid.StartGroupSizeCalibration(), Is.False, "Calibration requires play mode");
            Assert.That(fluid.StartP2gBenchmark(), Is.False, "Benchmark requires play mode");
        }

        struct P2gRun
        {
            public MlsMpmFluid.ParticleState[] states;
            public uint[] diagnostics;
            public Vector3 meanPosition;
            public float meanSpeed;
        }

        P2gRun RunP2gMode(MlsMpmFluid.P2gMode mode, int sortInterval, int ticks, Vector3[] positions, float spacing,
            Transform hull)
        {
            fluid.p2gMode = mode;
            fluid.mlsSortInterval = sortInterval;
            hull.position = boundary.ClampInside(new Vector3(-.7f, .55f, 0), .16f);
            fluid.Initialize();
            Assert.That(fluid.ActiveP2gMode, Is.EqualTo(mode));
            fluid.SetComparisonInitialPositions(positions, spacing);
            for (int tick = 0; tick < ticks; tick++)
            {
                Vector3 target = tick / 24 % 2 == 0 ? new Vector3(.1f, .55f, .18f) : new Vector3(-1, .55f, -.18f);
                hull.position = Vector3.MoveTowards(hull.position,
                    boundary.ClampInside(target, .16f), .25f * fluid.CellSize * fluid.substeps);
                fluid.StepSimulation(1);
            }
            var run = new P2gRun { states = ReadParticles(), diagnostics = new uint[8] };
            var diagnostics = (ComputeBuffer)typeof(MlsMpmFluid).GetField("diagnostics",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fluid);
            diagnostics.GetData(run.diagnostics);
            foreach (var s in run.states)
            {
                run.meanPosition += (Vector3)s.position;
                run.meanSpeed += ((Vector3)s.velocity).magnitude;
            }
            run.meanPosition /= run.states.Length;
            run.meanSpeed /= run.states.Length;
            return run;
        }

        void ConfigureP2gComparison(int count)
        {
            fluid.particleCount = count;
            fluid.useParticleSoA = fluid.cacheGridBoundary = fluid.reduceDiagnosticAtomics = true;
            fluid.whitewaterCapacity = 8192;
            boundary.tightGrid = true;
            boundary.ConfigureGridWindow();
        }

        [Test]
        public void UncheckedP2gAtomicsMatchTwoPassExactly()
        {
            ConfigureP2gComparison(65536);
            Vector3[] positions = boundary.CreateInitialFill(fluid.particleCount, out float spacing);
            var hull = new GameObject("P2G comparison hull");
            try
            {
                fluid.sphereObstacle = hull.transform;
                fluid.sphereRadius = .12f;
                P2gRun reference = RunP2gMode(MlsMpmFluid.P2gMode.TwoPass, 0, 90, positions, spacing, hull.transform);
                P2gRun run = RunP2gMode(MlsMpmFluid.P2gMode.TwoPassUnchecked, 0, 90, positions, spacing, hull.transform);
                Assert.That(reference.diagnostics[0], Is.Zero);
                Assert.That(run.diagnostics, Is.EqualTo(reference.diagnostics));
                for (int i = 0; i < run.states.Length; i++)
                {
                    Assert.That(run.states[i].position, Is.EqualTo(reference.states[i].position), "position " + i);
                    Assert.That(run.states[i].velocity, Is.EqualTo(reference.states[i].velocity), "velocity " + i);
                    Assert.That(run.states[i].affineX, Is.EqualTo(reference.states[i].affineX));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(hull); }
        }

        [TestCase(MlsMpmFluid.P2gMode.TwoPass, 1)]
        [TestCase(MlsMpmFluid.P2gMode.Fused, 0)]
        [TestCase(MlsMpmFluid.P2gMode.FusedPacked16, 0)]
        [TestCase(MlsMpmFluid.P2gMode.FusedPacked16, 4)]
        public void P2gVariantsStayStableAndCloseToTwoPass(MlsMpmFluid.P2gMode mode, int sortInterval)
        {
            ConfigureP2gComparison(65536);
            Vector3[] positions = boundary.CreateInitialFill(fluid.particleCount, out float spacing);
            var hull = new GameObject("P2G comparison hull");
            try
            {
                fluid.sphereObstacle = hull.transform;
                fluid.sphereRadius = .12f;
                P2gRun reference = RunP2gMode(MlsMpmFluid.P2gMode.TwoPass, 0, 240, positions, spacing, hull.transform);
                P2gRun run = RunP2gMode(mode, sortInterval, 240, positions, spacing, hull.transform);
                Assert.That(run.diagnostics[0], Is.Zero, "No faults");
                foreach (var s in run.states)
                    Assert.That(float.IsNaN(s.position.x) || float.IsNaN(s.velocity.x), Is.False);
                float densityRef = reference.diagnostics[1] / 1024f, density = run.diagnostics[1] / 1024f;
                Debug.Log($"P2G {mode} sort={sortInterval}: meanPos {run.meanPosition} vs {reference.meanPosition}, " +
                    $"meanSpeed {run.meanSpeed:F3} vs {reference.meanSpeed:F3}, densityPeak {density:F3} vs {densityRef:F3}");
                Assert.That(Vector3.Distance(run.meanPosition, reference.meanPosition), Is.LessThan(.5f), "Mean position (cells)");
                Assert.That(density, Is.EqualTo(densityRef).Within(.2f * densityRef), "Peak density ratio");
                Assert.That(run.meanSpeed, Is.EqualTo(reference.meanSpeed).Within(.35f * reference.meanSpeed + .05f));
            }
            finally { UnityEngine.Object.DestroyImmediate(hull); }
        }

        [TestCase(16385)]
        [TestCase(32769)]
        public void ReducedDiagnosticsHandleTailLanesAndFaultsWithoutStrandingBarriers(int count)
        {
            fluid.particleCount = count;
            fluid.useParticleSoA = fluid.cacheGridBoundary = fluid.reduceDiagnosticAtomics = true;
            boundary.tightGrid = true;
            boundary.ConfigureGridWindow();
            fluid.Initialize();
            fluid.SetComparisonInitialPositions(boundary.CreateInitialFill(count, out float spacing), spacing);
            fluid.StepSimulation(5);
            Assert.That(fluid.SynchronizeDiagnostics(), Is.Zero);
            var states = ReadParticles();
            states[count - 1].position.x = float.NaN;
            UploadParticles(states);
            fluid.StepSimulation(1);
            Assert.That(fluid.SynchronizeDiagnostics() & 1u, Is.EqualTo(1u));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(8)]
        [TestCase(16)]
        public void ReducedDiagnosticsKeepStressAndGatherFailurePathsBarrierSafe(int flag)
        {
            fluid.particleCount = 513;
            fluid.reduceDiagnosticAtomics = true;
            fluid.useParticleSoA = true;
            fluid.Initialize();
            fluid.SetComparisonInitialPositions(boundary.CreateInitialFill(513, out float spacing), spacing);
            var shader = (ComputeShader)typeof(MlsMpmFluid).GetField("solver",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fluid);
            var diagnostics = (ComputeBuffer)typeof(MlsMpmFluid).GetField("diagnostics",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fluid);
            var grid = (ComputeBuffer)typeof(MlsMpmFluid).GetField("grid",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fluid);
            var states = ReadParticles();
            bool stress = flag == 2 || flag == 4 || flag == 8;
            if (flag == 4)
                for (int i = 0; i < states.Length; i++) states[i].position.x = -100;
            if (flag == 2)
            {
                for (int i = 0; i < states.Length; i++) states[i].affineX.x = 1e10f;
                shader.SetFloat("_Viscosity", 1e10f);
                var accumulation = (ComputeBuffer)typeof(MlsMpmFluid).GetField("accumulation",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fluid);
                var massValues = new int[accumulation.count];
                for (int i = 0; i < massValues.Length; i += 4)
                    massValues[i] = Mathf.RoundToInt(fluid.RestDensity * 65536);
                accumulation.SetData(massValues);
            }
            UploadParticles(states);
            if (!stress)
            {
                var cells = new Vector4[grid.count];
                for (int i = 0; i < cells.Length; i++)
                    cells[i] = flag == 1 ? new Vector4(float.NaN, 0, 0, 1) : new Vector4(1000, 0, 0, 1);
                grid.SetData(cells);
            }
            shader.SetInt("_ParticleCount", 513);
            shader.SetInt("_Resolution", fluid.gridResolution);
            shader.SetInt("_ReduceDiagnosticAtomics", 1);
            shader.SetFloat("_RestDensity", fluid.RestDensity);
            shader.SetFloat("_Dt", (float)(MlsMpmFluid.FixedTick / fluid.substeps));
            shader.Dispatch(shader.FindKernel(stress ? "TransferStress" : "UpdateParticles"), 5, 1, 1);
            var values = new uint[8];
            diagnostics.GetData(values);
            Assert.That(values[0] & (uint)flag, Is.EqualTo((uint)flag));
            if (flag == 16) Assert.That(values[2], Is.GreaterThan(0), "Displacement faults retain the speed peak");
        }

        void AssertInside(MlsMpmFluid.ParticleState[] states)
        {
            int outside=0;float maximum=float.NegativeInfinity;
            foreach(var p in states)
            {
                float distance=BottleBoundary.Distance(fluid.GridOrigin+(Vector3)p.position*fluid.CellSize-boundary.center,out _);
                if(distance>1e-4f)outside++;maximum=Mathf.Max(maximum,distance);
            }
            Assert.That(outside,Is.Zero,"Particles outside curved bottle; maximum distance "+maximum);
        }
    }
}
