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
            fluid.substeps = 8;
            fluid.showParticles = fluid.showControls = false;
            // Edit-mode tests do not invoke Awake. Use the same scene-local binding.
            var configure = typeof(BottleBoundary).GetMethod("Configure",BindingFlags.Instance|BindingFlags.NonPublic);
            fluid.configureSimulation = (Action<CommandBuffer,ComputeShader>)Delegate.CreateDelegate(
                typeof(Action<CommandBuffer,ComputeShader>),boundary,configure);
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

        [Test]
        [TestCase(16384)]
        [TestCase(32768)]
        [TestCase(65536)]
        public void PresetsRetainReferenceWaterVolumeAndScaleSpacing(int count)
        {
            boundary.CreateInitialFill(32768,out float referenceSpacing);
            float referenceVolume=boundary.WaterVolume;
            fluid.particleCount=count;
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
            ComputeBuffer buffer=ParticleBuffer();
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
            buffer.SetData(states);
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
        [TestCase(16384)]
        [TestCase(32768)]
        [TestCase(65536)]
        public void FastBoatGesturesSweepHullAndGenerateSimulatedWhitewater(int count)
        {
            var sphere=new GameObject("Fast hull");
            var boat=new GameObject("Immediate visual boat");
            try
            {
                fluid.particleCount=count;
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
        MlsMpmFluid.ParticleState[] ReadParticles()
        {
            var states=new MlsMpmFluid.ParticleState[fluid.particleCount];ParticleBuffer().GetData(states);return states;
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
