using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace WaterSplash
{
    [DefaultExecutionOrder(-120)]
    public sealed class BottleVoyage : MonoBehaviour
    {
        public MlsMpmFluid fluid;
        public Transform boat;
        public Camera waterCamera;
        public ComputeShader waterProbe;
        public BottleBoundary boundary;
        public TagRope tagRope;
        public Cubemap reflection;
        public Texture2D bottleProfile;
        public Color captionColor = new Color(.26f, .22f, .25f);
        public bool IsDraggingBoat => dragging;

        readonly Vector4[] probePositions = new Vector4[5], samples = new Vector4[5];
        ComputeShader probe;
        ComputeBuffer partials, results;
        AsyncGPUReadbackRequest pendingRequest;
        Quaternion heading;
        Vector3 initialBoatPosition, desiredPosition, desiredNormal = Vector3.up, boatTarget, dragOffset;
        Renderer[] boatRenderers;
        float nextProbe, meanFrame, dragPlaneHeight, backlog, particleSlider;
        int collectKernel, reduceKernel, blocks, generation, requestedCount;
        bool pending, dragging, skipDelta, resetRequested, paused, sliderDragging;
        string error;
        GUIStyle titleStyle, subtitleStyle, footStyle;

        void Awake()
        {
            Shader.SetGlobalTexture("_VoyageReflection", reflection);
            Shader.SetGlobalFloat("_VoyageReflectionStrength", 1);
            Shader.SetGlobalTexture("_VoyageBottleProfile", bottleProfile);
            Shader.SetGlobalVector("_VoyageBottle", (Vector4)boundary.center + new Vector4(0, 0, 0, boundary.AxisSign));
            Application.runInBackground = true;
            Application.targetFrameRate = Application.isMobilePlatform ? 1000 : -1;
            QualitySettings.vSyncCount = 0;
            heading = Quaternion.Euler(0, boat.eulerAngles.y, 0);
            initialBoatPosition = boat.position;
            boatRenderers = boat.GetComponentsInChildren<Renderer>();
            probe = Instantiate(waterProbe);
            collectKernel = probe.FindKernel("Collect");
            reduceKernel = probe.FindKernel("Reduce");
            results = new ComputeBuffer(5, 16);
            probe.SetBuffer(reduceKernel, "_Samples", results);
            probe.SetFloat("_Radius", .23f);
            requestedCount = fluid.particleCount;
            particleSlider = SliderFor(requestedCount);
        }

        void Start() => ResetWater();

        void ResetWater()
        {
            // Probe buffers stay bound until an in-flight readback finishes.
            dragging = false;
            if (pending) { resetRequested = true; return; }
            resetRequested = false;
            generation++;
            fluid.particleCount = requestedCount;
            boat.SetPositionAndRotation(initialBoatPosition, heading);
            desiredPosition = boatTarget = initialBoatPosition;
            desiredNormal = Vector3.up;
            fluid.sphereObstacle.position = HullCenter();
            fluid.Initialize(boundary);
            int nextBlocks = (fluid.particleCount + 127) / 128;
            if (blocks != nextBlocks)
            {
                partials?.Release();
                blocks = nextBlocks;
                partials = new ComputeBuffer(blocks * 5, 16);
                probe.SetBuffer(collectKernel, "_Partials", partials);
                probe.SetBuffer(reduceKernel, "_Partials", partials);
                probe.SetInt("_BlockCount", blocks);
            }
            Array.Clear(samples, 0, 5);
            nextProbe = Time.unscaledTime;
            backlog = 0;
            skipDelta = true;
        }

        static int CountFor(float slider) => 16384 << Mathf.Clamp(Mathf.RoundToInt(slider), 0, 3);
        static float SliderFor(int count) => Mathf.Log(count / 16384f, 2);

        void Update()
        {
            if (error != null) return;
            if (resetRequested && !pending || Input.GetKeyDown(KeyCode.R)) ResetWater();
            if (Input.GetKeyDown(KeyCode.Space)) paused = !paused;
            if (fluid.Error != null) { Fail(fluid.Error); return; }
            meanFrame = Mathf.Lerp(meanFrame, Time.unscaledDeltaTime, .03f);
            if (paused) { skipDelta = true; dragging = false; return; }
            UpdatePointer();
            if (skipDelta) { skipDelta = false; return; }
            backlog += Time.unscaledDeltaTime;
            int due = Math.Min(4, (int)(backlog / MlsMpmFluid.FixedTick));
            for (int i = 0; i < due; i++)
            {
                AdvanceBoat(MlsMpmFluid.FixedTick);
                // The water collider sweeps at most a quarter cell per substep, even when the visible boat jumps.
                float speed = Mathf.Min(6, .25f * fluid.CellSize * fluid.substeps / MlsMpmFluid.FixedTick);
                fluid.sphereObstacle.position = Vector3.MoveTowards(fluid.sphereObstacle.position, HullCenter(), speed * MlsMpmFluid.FixedTick);
                fluid.StepSimulation();
                backlog -= MlsMpmFluid.FixedTick;
            }
        }

        void AdvanceBoat(float dt)
        {
            bool wet = samples[0].w > 0;
            if (!dragging && wet) boatTarget = ClampBoatTarget(boatTarget + new Vector3(samples[0].y, 0, samples[0].z) * (.35f * dt));
            Vector3 position = boat.position, planar = new Vector3(boatTarget.x, position.y, boatTarget.z);
            position = dragging ? planar : Vector3.MoveTowards(position, planar, dt);
            if (wet) position.y = Mathf.Lerp(position.y, desiredPosition.y, 1 - Mathf.Exp(-5 * dt));
            boat.position = position;
            Quaternion rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(heading * Vector3.forward, desiredNormal), desiredNormal);
            boat.rotation = Quaternion.Slerp(boat.rotation, rotation, 1 - Mathf.Exp(-4 * dt));
        }

        Vector3 HullCenter() => boundary.ClampInside(boat.position + Vector3.down * (.23f * boat.localScale.y), fluid.sphereRadius + fluid.CellSize * .6f);

        Vector3 ClampBoatTarget(Vector3 target)
        {
            Vector3 local = boundary.ToProfile(target);
            local.x = Mathf.Clamp(local.x, -1.15f, .22f);
            local.z = Mathf.Clamp(local.z, -.25f, .25f);
            return boundary.FromProfile(local);
        }

        void LateUpdate()
        {
            if (error != null || pending || resetRequested || paused || Time.unscaledTime < nextProbe) return;
            nextProbe = Time.unscaledTime + .1f;
            Vector3 center = boat.position;
            Vector3 front = heading * new Vector3(0, 0, .45f * boat.localScale.z);
            Vector3 right = heading * new Vector3(.25f * boat.localScale.x, 0, 0);
            probePositions[0] = center;
            probePositions[1] = center + front;
            probePositions[2] = center - front;
            probePositions[3] = center + right;
            probePositions[4] = center - right;
            probe.SetVectorArray("_ProbePositions", probePositions);
            probe.SetVector("_GridOrigin", fluid.GridOrigin);
            probe.SetFloat("_CellSize", fluid.CellSize);
            probe.SetFloat("_SurfaceOffset", fluid.SurfaceParticleRadius * .5f);
            probe.SetInt("_ParticleCount", fluid.particleCount);
            probe.SetInt("_ParticlePlaneStride", fluid.particleCount);
            probe.SetBuffer(collectKernel, "_Particles", fluid.Particles);
            probe.Dispatch(collectKernel, blocks, 5, 1);
            probe.Dispatch(reduceKernel, 5, 1, 1);
            pending = true;
            int requestGeneration = generation;
            pendingRequest = AsyncGPUReadback.Request(results, request =>
            {
                if (this == null) return;
                pending = false;
                if (requestGeneration != generation) return;
                if (request.hasError) { Fail("Water-height GPU readback failed."); return; }
                request.GetData<Vector4>().CopyTo(samples);
                if (samples[0].w <= 0) return;
                float s = boat.localScale.x;
                desiredPosition.y = Mathf.Clamp(samples[0].x - .07f * boat.localScale.y, .35f, .82f);
                if (samples[1].w > 0 && samples[2].w > 0 && samples[3].w > 0 && samples[4].w > 0)
                {
                    var normal = new Vector3(-(samples[3].x - samples[4].x) / (.5f * s), 1, -(samples[1].x - samples[2].x) / (.9f * s)).normalized;
                    desiredNormal = heading * Vector3.RotateTowards(Vector3.up, normal, 18 * Mathf.Deg2Rad, 0);
                }
            });
        }

        void UpdatePointer()
        {
            Vector2 point = Input.mousePosition;
            if (Input.GetMouseButtonDown(0) && !tagRope.IsGrabbing) BeginBoatDrag(point);
            if (dragging)
            {
                if (IsPointerOnControls(point)) dragging = false;
                else if (PointerOnWaterPlane(point, out Vector3 waterPoint))
                {
                    boatTarget = ClampBoatTarget(waterPoint + dragOffset);
                    boat.position = new Vector3(boatTarget.x, boat.position.y, boatTarget.z);
                }
            }
            if (Input.GetMouseButtonUp(0)) dragging = false;
        }

        void BeginBoatDrag(Vector2 point)
        {
            if (resetRequested || IsPointerOnControls(point)) return;
            float padding = 18 * UiScale();
            foreach (Renderer renderer in boatRenderers)
            {
                Bounds bounds = renderer.bounds;
                Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) * 2 - 1, (i >> 1 & 1) * 2 - 1, (i >> 2 & 1) * 2 - 1));
                    Vector2 projected = waterCamera.WorldToScreenPoint(corner);
                    min = Vector2.Min(min, projected); max = Vector2.Max(max, projected);
                }
                if (!Rect.MinMaxRect(min.x - padding, min.y - padding, max.x + padding, max.y + padding).Contains(point)) continue;
                dragPlaneHeight = boat.position.y;
                if (!PointerOnWaterPlane(point, out Vector3 waterPoint)) return;
                dragOffset = boat.position - waterPoint;
                boatTarget = boat.position;
                dragging = true;
                return;
            }
        }

        bool PointerOnWaterPlane(Vector2 point, out Vector3 hit)
        {
            Ray ray = waterCamera.ScreenPointToRay(point);
            bool ok = new Plane(Vector3.up, new Vector3(0, dragPlaneHeight, 0)).Raycast(ray, out float distance);
            hit = ray.GetPoint(distance);
            return ok;
        }

        public bool IsPointerOnControls(Vector2 point)
        {
            float scale = UiScale();
            return ControlsRect(scale).Contains(new Vector2(point.x / scale, (Screen.height - point.y) / scale));
        }

        void OnApplicationFocus(bool focus)
        {
            if (focus) return;
            skipDelta = true;
            dragging = sliderDragging = false;
            particleSlider = SliderFor(requestedCount);
        }

        void Fail(string message)
        {
            error = message;
            Debug.LogError("Bottle Voyage: " + message, this);
        }

        void OnDestroy()
        {
            generation++;
            if (pending) pendingRequest.WaitForCompletion();
            partials?.Release();
            results?.Release();
            Destroy(probe);
        }

        static float UiScale() => Mathf.Max(.5f, Mathf.Min(Screen.width / 1280f, Screen.height / 800f));

        static Rect ControlsRect(float scale)
        {
            Rect safe = Screen.safeArea;
            return new Rect(safe.xMax / scale - 610, (Screen.height - safe.yMin) / scale - 88, 574, 58);
        }

        void OnGUI()
        {
            if (titleStyle == null)
            {
                Color dim = Color.Lerp(captionColor, Color.gray, .25f);
                titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 25 };
                subtitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14 };
                footStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
                titleStyle.normal.textColor = captionColor;
                subtitleStyle.normal.textColor = footStyle.normal.textColor = dim;
            }
            float scale = UiScale();
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            Rect safe = Screen.safeArea;
            float x = safe.x / scale + 36, top = (Screen.height - safe.yMax) / scale + 30, bottom = (Screen.height - safe.yMin) / scale - 38;
            GUI.Label(new Rect(x, top, 640, 40), "T I D E   I N   A   B O T T L E", titleStyle);
            GUI.Label(new Rect(x + 2, top + 39, 600, 28), "a little ocean, a long way home.", subtitleStyle);
            GUI.Label(new Rect(x, bottom, 750, 30), error ?? (Application.isMobilePlatform ?
                "DRAG THE SAILBOAT     /     SLIDE TO SET PARTICLES" :
                "DRAG THE SAILBOAT     /     SPACE TO PAUSE     /     R TO RESET"), footStyle);
            if (Debug.isDebugBuild)
                GUI.Label(new Rect(x, bottom - 22, 750, 25), $"{fluid.particleCount / 1024}K particles | {1 / Mathf.Max(meanFrame, 1e-4f):F0} FPS | physics backlog {backlog:F3}s", footStyle);
            if (error != null) return;
            Rect controls = ControlsRect(scale);
            float bx = controls.x, by = controls.y, previous = particleSlider;
            particleSlider = GUI.HorizontalSlider(new Rect(bx + 6, by + 9, 278, 28), particleSlider, 0, 3);
            if (previous != particleSlider) sliderDragging = true;
            GUI.Label(new Rect(bx + 304, by + 5, 66, 30), CountFor(particleSlider) / 1024 + "K", subtitleStyle);
            GUI.Label(new Rect(bx + 3, by + 32, 48, 20), "16K", footStyle);
            GUI.Label(new Rect(bx + 88, by + 32, 48, 20), "32K", footStyle);
            GUI.Label(new Rect(bx + 177, by + 32, 48, 20), "64K", footStyle);
            GUI.Label(new Rect(bx + 259, by + 32, 48, 20), "128K", footStyle);
            if (sliderDragging && Event.current.rawType == EventType.MouseUp)
            {
                sliderDragging = false;
                particleSlider = Mathf.Round(particleSlider);
                int count = CountFor(particleSlider);
                if (count != requestedCount) { requestedCount = count; resetRequested = true; dragging = false; }
            }
            if (GUI.Button(new Rect(bx + 383, by + 5, 190, 32), fluid.renderSurface ? "Water  >  Particles" : "Particles  >  Water"))
                fluid.renderSurface = !fluid.renderSurface;
        }
    }
}