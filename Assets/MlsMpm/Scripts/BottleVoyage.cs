using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WaterSplash
{
    [DefaultExecutionOrder(-120)]
    public sealed class BottleVoyage : MonoBehaviour
    {
        public MlsMpmFluid fluid;
        public Transform boat;
        public Camera waterCamera;
        public UniversalRenderPipelineAsset pipeline;
        public ComputeShader waterProbe;
        public BottleBoundary boundary;
        public bool showCaption = true;
        public int CompletedReadbacks { get; private set; }
        public string Error { get; private set; }
        public float WaterHeight { get; private set; }
        public double BacklogSeconds => backlog;
        public bool IsDraggingBoat => dragging;
        public bool ResetPending => resetRequested;

        readonly Vector4[] probePositions = new Vector4[5];
        readonly Vector4[] samples = new Vector4[5];
        ComputeShader probe;
        ComputeBuffer partials, results;
        AsyncGPUReadbackRequest pendingRequest;
        RenderPipelineAsset previousPipeline;
        Quaternion heading;
        Vector3 initialBoatPosition, desiredPosition, desiredNormal = Vector3.up, boatTarget, dragOffset;
        Renderer[] boatRenderers;
        float nextProbe, meanFrame, dragPlaneHeight;
        double backlog;
        int collectKernel, reduceKernel, blocks, generation, requestedParticleCount;
        bool pending, suspended, ready, dragging, skipDelta, resetRequested;
        GUIStyle titleStyle, subtitleStyle, footStyle;

        void Awake()
        {
            previousPipeline = QualitySettings.renderPipeline;
            if (fluid == null || fluid.sphereObstacle == null || boat == null ||
                waterCamera == null || pipeline == null || waterProbe == null || boundary == null)
            {
                Fail("Assign the bottle fluid, boat, camera, pipeline and water probe.");
                return;
            }
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                Fail("The bottle sailboat requires asynchronous GPU readback; no blocking fallback is used.");
                return;
            }
            QualitySettings.renderPipeline = pipeline;
            Application.runInBackground = true;
            fluid.externalControl = true;
            heading = Quaternion.Euler(0, boat.eulerAngles.y, 0);
            initialBoatPosition = boat.position;
            boatRenderers = boat.GetComponentsInChildren<Renderer>();
            desiredPosition = initialBoatPosition;
            probe = Instantiate(waterProbe);
            collectKernel = probe.FindKernel("Collect");
            reduceKernel = probe.FindKernel("Reduce");
            results = new ComputeBuffer(5, sizeof(float) * 4);
            probe.SetBuffer(reduceKernel, "_Samples", results);
            probe.SetFloat("_Radius", .23f);
            requestedParticleCount = fluid.particleCount;
            ready = true;
        }

        void Start()
        {
            if (ready) ResetWater();
        }

        public void ResetWater()
        {
            if (!ready) throw new InvalidOperationException(Error ?? "Bottle is not ready.");
            // Do not release/rebind probe buffers while their previous generation
            // still has a readback in flight. UI requests coalesce until it finishes.
            if (pending) { resetRequested = true; dragging = false; return; }
            resetRequested = false;
            generation++;
            fluid.particleCount = requestedParticleCount;
            boat.SetPositionAndRotation(initialBoatPosition, heading);
            desiredPosition = boatTarget = initialBoatPosition;
            desiredNormal = Vector3.up;
            fluid.sphereObstacle.position = HullCenter();
            fluid.Initialize();
            if (fluid.Error != null) { Fail(fluid.Error); return; }
            Vector3[] positions = boundary.CreateInitialFill(fluid.particleCount, out float spacing);
            fluid.SetComparisonInitialPositions(positions, spacing);
            fluid.particleRadiusScale = spacing * .34f / fluid.CellSize;
            int nextBlocks = (fluid.particleCount + 127) / 128;
            if (partials == null || blocks != nextBlocks)
            {
                partials?.Release();
                blocks = nextBlocks;
                partials = new ComputeBuffer(blocks * 5, sizeof(float) * 4);
                probe.SetBuffer(collectKernel, "_Partials", partials);
                probe.SetBuffer(reduceKernel, "_Partials", partials);
                probe.SetInt("_BlockCount", blocks);
            }
            Array.Clear(samples, 0, samples.Length);
            CompletedReadbacks = 0;
            nextProbe = Time.unscaledTime;
            backlog = 0;
            dragging = false;
            skipDelta = true;
        }

        public void SelectParticleCount(int count)
        {
            if (count != 16384 && count != 32768 && count != 65536)
                throw new ArgumentOutOfRangeException(nameof(count), "Choose 16K, 32K or 64K.");
            if (!ready) throw new InvalidOperationException(Error ?? "Bottle is not ready.");
            if (count == requestedParticleCount) return;
            requestedParticleCount = count;
            resetRequested = true;
            dragging = false;
        }

        public void SetSurfaceRendering(bool enabled)
        {
            if (!ready) throw new InvalidOperationException(Error ?? "Bottle is not ready.");
            fluid.SetSurfaceRendering(enabled);
        }

        public void ToggleRendering() => SetSurfaceRendering(!fluid.renderSurface);

        void Update()
        {
            if (!ready || Error != null) return;
            if (resetRequested && !pending) ResetWater();
            if (Input.GetKeyDown(KeyCode.R)) ResetWater();
            if (Input.GetKeyDown(KeyCode.Space)) fluid.paused = !fluid.paused;
            if (fluid.Error != null || fluid.RenderingError != null)
            {
                Fail(fluid.Error ?? fluid.RenderingError);
                return;
            }
            meanFrame = Mathf.Lerp(meanFrame, Time.unscaledDeltaTime, .03f);
            if (fluid.paused || suspended) { skipDelta = true; dragging = false; return; }
            UpdatePointer();
            if (skipDelta) { skipDelta = false; return; }
            float dt = Time.unscaledDeltaTime;
            backlog += dt;
            int due = Math.Min(4, (int)(backlog / MlsMpmFluid.FixedTick));
            for (int i = 0; i < due; i++)
            {
                AdvanceBoat((float)MlsMpmFluid.FixedTick);
                AdvanceHull((float)MlsMpmFluid.FixedTick);
                fluid.StepSimulation(1);
                backlog -= MlsMpmFluid.FixedTick;
            }
        }

        void AdvanceBoat(float dt)
        {
            if (!dragging && samples[0].w > 0)
            {
                Vector3 drift = new Vector3(samples[0].y, 0, samples[0].z) * (.35f * dt);
                boatTarget = ClampBoatTarget(boatTarget + drift);
            }
            Vector3 position = boat.position;
            Vector3 planarTarget = new Vector3(boatTarget.x, position.y, boatTarget.z);
            position = dragging ? planarTarget : Vector3.MoveTowards(position, planarTarget, dt);
            if (samples[0].w > 0) position.y = Mathf.Lerp(position.y, desiredPosition.y, 1 - Mathf.Exp(-5 * dt));
            boat.position = position;
            Quaternion rotation = Quaternion.LookRotation(
                Vector3.ProjectOnPlane(heading * Vector3.forward, desiredNormal), desiredNormal);
            boat.rotation = Quaternion.Slerp(boat.rotation, rotation, 1 - Mathf.Exp(-4 * dt));
        }

        Vector3 HullCenter()
        {
            return boundary.ClampInside(boat.position + Vector3.down * (.23f * boat.localScale.y),
                fluid.sphereRadius + fluid.CellSize * .6f);
        }

        void AdvanceHull(float dt)
        {
            // Only the water collider is rate-limited. Its sweep advances at most
            // a quarter cell per substep, even when the visible boat jumps.
            float speed = Mathf.Min(6, .25f * fluid.CellSize * fluid.substeps / (float)MlsMpmFluid.FixedTick);
            fluid.sphereObstacle.position = Vector3.MoveTowards(fluid.sphereObstacle.position, HullCenter(), speed * dt);
        }

        public Vector3 ClampBoatTarget(Vector3 target)
        {
            Vector3 local = boundary.ToProfile(target);
            local.x = Mathf.Clamp(local.x, -1.15f, .22f);
            local.z = Mathf.Clamp(local.z, -.25f, .25f);
            return boundary.FromProfile(local);
        }

        void LateUpdate()
        {
            if (!ready || Error != null || pending || resetRequested || suspended || fluid.paused ||
                !fluid.IsInitialized || Time.unscaledTime < nextProbe) return;
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
            probe.SetInt("_ParticleCount", fluid.RenderedParticleCount);
            probe.SetBuffer(collectKernel, "_Particles", fluid.ParticleBuffer);
            probe.Dispatch(collectKernel, blocks, 5, 1);
            probe.Dispatch(reduceKernel, 5, 1, 1);
            pending = true;
            int requestGeneration = generation;
            pendingRequest = AsyncGPUReadback.Request(results, request =>
            {
                if (this == null) return;
                pending = false;
                if (requestGeneration != generation) return;
                if (request.hasError) { Fail("Bottle water-height GPU readback failed."); return; }
                var data = request.GetData<Vector4>();
                data.CopyTo(samples);
                CompletedReadbacks++;
                if (samples[0].w <= 0) return;
                WaterHeight = samples[0].x;
                desiredPosition.y = Mathf.Clamp(WaterHeight - .07f * boat.localScale.y, .35f, .82f);
                if (samples[1].w > 0 && samples[2].w > 0 && samples[3].w > 0 && samples[4].w > 0)
                {
                    Vector3 normal = SurfaceNormal(samples[1].x, samples[2].x, samples[3].x, samples[4].x, boat.localScale.x);
                    desiredNormal = heading * normal;
                }
            });
        }

        public static Vector3 SurfaceNormal(float front, float back, float right, float left, float scale = 1)
        {
            var normal = new Vector3(-(right - left) / (.5f * scale), 1, -(front - back) / (.9f * scale)).normalized;
            return Vector3.RotateTowards(Vector3.up, normal, 18 * Mathf.Deg2Rad, 0);
        }

        void UpdatePointer()
        {
            Vector2 point = Input.mousePosition;
            if (Input.GetMouseButtonDown(0)) BeginBoatDrag(point);
            if (dragging) DragBoat(point);
            if (Input.GetMouseButtonUp(0)) EndBoatDrag();
        }

        public bool BeginBoatDrag(Vector2 screenPoint)
        {
            if (!ready || Error != null || resetRequested || suspended || fluid.paused || PointerOnControls(screenPoint))
                return false;
            bool hit = false;
            float padding = 18 * UiScale();
            foreach (Renderer renderer in boatRenderers)
            {
                if (renderer == null || !renderer.enabled) continue;
                Bounds bounds = renderer.bounds;
                Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                bool visible = false;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 projected = waterCamera.WorldToScreenPoint(corner);
                    if (projected.z <= 0) continue;
                    visible = true;
                    min = Vector2.Min(min, projected); max = Vector2.Max(max, projected);
                }
                if (visible && Rect.MinMaxRect(min.x - padding, min.y - padding, max.x + padding, max.y + padding).Contains(screenPoint))
                { hit = true; break; }
            }
            dragPlaneHeight = boat.position.y;
            if (!hit || !PointerOnWaterPlane(screenPoint, out Vector3 waterPoint)) return false;
            dragOffset = boat.position - waterPoint;
            boatTarget = boat.position;
            dragging = true;
            return true;
        }

        public void DragBoat(Vector2 screenPoint)
        {
            if (!dragging) return;
            if (PointerOnControls(screenPoint)) { EndBoatDrag(); return; }
            if (PointerOnWaterPlane(screenPoint, out Vector3 waterPoint))
            {
                boatTarget = ClampBoatTarget(waterPoint + dragOffset);
                boat.position = new Vector3(boatTarget.x, boat.position.y, boatTarget.z);
            }
        }

        public void EndBoatDrag() => dragging = false;

        bool PointerOnWaterPlane(Vector2 screenPoint, out Vector3 point)
        {
            Ray ray = waterCamera.ScreenPointToRay(screenPoint);
            if (new Plane(Vector3.up, new Vector3(0, dragPlaneHeight, 0)).Raycast(ray, out float distance))
            {
                point = ray.GetPoint(distance); return true;
            }
            point = default;
            return false;
        }

        bool PointerOnControls(Vector2 screenPoint)
        {
            float scale = UiScale();
            return ControlsRect(scale).Contains(new Vector2(screenPoint.x / scale, (Screen.height - screenPoint.y) / scale));
        }

        void OnApplicationPause(bool value) { suspended = value; skipDelta = true; dragging = false; }
        void OnApplicationFocus(bool value) { if (!value) { skipDelta = true; dragging = false; } }

        void Fail(string message)
        {
            Error = message;
            if (fluid != null) fluid.paused = true;
            Debug.LogError("Bottle Voyage: " + message, this);
        }

        void OnDestroy()
        {
            generation++;
            if (pending) pendingRequest.WaitForCompletion();
            partials?.Release();
            results?.Release();
            if (probe != null) Destroy(probe);
            QualitySettings.renderPipeline = previousPipeline;
        }

        static float UiScale() => Mathf.Max(.5f, Mathf.Min(Screen.width / 1280f, Screen.height / 800f));

        static Rect ControlsRect(float scale)
        {
            Rect safe = Screen.safeArea;
            return new Rect(safe.xMax / scale - 456, (Screen.height - safe.yMin) / scale - 78, 420, 72);
        }

        static bool Choice(Rect rect, string text, bool selected)
        {
            Color previous = GUI.backgroundColor;
            if (selected) GUI.backgroundColor = new Color(.56f, .86f, .82f);
            bool pressed = GUI.Button(rect, text);
            GUI.backgroundColor = previous;
            return pressed;
        }

        void OnGUI()
        {
            if (!showCaption && Error == null) return;
            if (titleStyle == null)
            {
                titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 25, fontStyle = FontStyle.Normal };
                subtitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14 };
                footStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
                titleStyle.normal.textColor = new Color(.26f, .22f, .25f);
                subtitleStyle.normal.textColor = new Color(.4f, .33f, .34f);
                footStyle.normal.textColor = new Color(.4f, .33f, .34f);
            }
            Matrix4x4 previous = GUI.matrix;
            float scale = UiScale();
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            Rect safe = Screen.safeArea;
            float x = safe.x / scale + 36;
            float top = (Screen.height - safe.yMax) / scale + 30;
            GUI.Label(new Rect(x, top, 640, 40), "T I D E   I N   A   B O T T L E", titleStyle);
            GUI.Label(new Rect(x + 2, top + 39, 600, 28), "a little ocean, a long way home.", subtitleStyle);
            float bottom = (Screen.height - safe.yMin) / scale - 38;
            GUI.Label(new Rect(x, bottom, 750, 30), Error ??
                "DRAG THE SAILBOAT     /     SPACE TO PAUSE     /     R TO RESET", footStyle);
            if (Debug.isDebugBuild)
                GUI.Label(new Rect(x, bottom - 22, 750, 25), string.Format(
                    "{0}K MLS-MPM   |   {1:F0} FPS   |   backlog {2:F3}s   |   water {3:F3}",
                    fluid == null ? 0 : fluid.particleCount / 1024,
                    meanFrame > 0 ? 1 / meanFrame : 0, backlog, boundary == null ? 0 : boundary.WaterVolume), footStyle);
            if (ready && Error == null)
            {
                Rect controls = ControlsRect(scale);
                float bx = controls.x, by = controls.y;
                if (Choice(new Rect(bx, by, 64, 30), "16K", fluid.particleCount == 16384)) SelectParticleCount(16384);
                if (Choice(new Rect(bx + 70, by, 64, 30), "32K", fluid.particleCount == 32768)) SelectParticleCount(32768);
                if (Choice(new Rect(bx + 140, by, 64, 30), "64K", fluid.particleCount == 65536)) SelectParticleCount(65536);
                if (GUI.Button(new Rect(bx + 216, by, 198, 30),
                    fluid.renderSurface ? "Water  >  Particles" : "Particles  >  Water")) ToggleRendering();
                if (GUI.Button(new Rect(bx + 216, by + 36, 96, 30), fluid.paused ? "Resume" : "Pause"))
                    fluid.paused = !fluid.paused;
                if (GUI.Button(new Rect(bx + 318, by + 36, 96, 30), "Begin again")) ResetWater();
            }
            GUI.matrix = previous;
        }
    }
}
