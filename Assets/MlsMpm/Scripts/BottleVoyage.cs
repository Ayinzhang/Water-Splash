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
        public TagRope tagRope;
        [Tooltip("Baked surroundings reflected by the glass, water and tabletop.")]
        public Cubemap reflection;
        [Tooltip("Outer bottle radius along its axis, used for the analytic tabletop shadow.")]
        public Texture2D bottleProfile;
        public bool showCaption = true;
        public Color captionColor = new Color(.26f,.22f,.25f);
        public int CompletedReadbacks { get; private set; }
        public string Error { get; private set; }
        public float WaterHeight { get; private set; }
        public double BacklogSeconds => backlog;
        public bool IsDraggingBoat => dragging;
        public bool ResetPending => resetRequested;
        public int RequestedParticleCount => requestedParticleCount;

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
        double resetCompletedAt;
        bool waitingForResetReady;
        float particleSlider;
        bool sliderDragging;

        void Awake()
        {
            PublishLook();
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
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
            {
                int count = intent.Call<int>("getIntExtra", "voyageParticles", requestedParticleCount);
                if (count == 16384 || count == 32768 || count == 65536 || count == 131072)
                    requestedParticleCount = count;
                if (Debug.isDebugBuild)
                    fluid.optimizeTransfers = intent.Call<int>("getIntExtra", "voyageOptimized", fluid.optimizeTransfers ? 1 : 0) != 0;
            }
#endif
            particleSlider = SliderForParticleCount(requestedParticleCount);
            ready = true;
        }

        void Start()
        {
            if (ready) ResetWater();
        }

        void OnValidate() => PublishLook();

        void PublishLook()
        {
            Shader.SetGlobalTexture("_VoyageReflection", reflection != null ? (Texture)reflection : Texture2D.blackTexture);
            Shader.SetGlobalFloat("_VoyageReflectionStrength", reflection != null ? 1 : 0);
            if (bottleProfile != null) Shader.SetGlobalTexture("_VoyageBottleProfile", bottleProfile);
            if (boundary != null) Shader.SetGlobalVector("_VoyageBottle", (Vector4)boundary.center + new Vector4(0, 0, 0, boundary.AxisSign));
        }

        public void ResetWater()
        {
            if (!ready) throw new InvalidOperationException(Error ?? "Bottle is not ready.");
            // Do not release/rebind probe buffers while their previous generation
            // still has a readback in flight. UI requests coalesce until it finishes.
            if (pending) { resetRequested = true; dragging = false; return; }
            var resetTimer = System.Diagnostics.Stopwatch.StartNew();
            resetRequested = false;
            generation++;
            fluid.particleCount = requestedParticleCount;
            fluid.substeps = SubstepsForParticleCount(requestedParticleCount);
            fluid.reduceDiagnosticAtomics = true;
            fluid.groupSizeMode = MlsMpmFluid.GroupSizeMode.Fixed128;
            boundary.tightGrid = true;
            boat.SetPositionAndRotation(initialBoatPosition, heading);
            desiredPosition = boatTarget = initialBoatPosition;
            desiredNormal = Vector3.up;
            fluid.sphereObstacle.position = HullCenter();
            boundary.ConfigureGridWindow();
            double gridMs = resetTimer.Elapsed.TotalMilliseconds;
            fluid.Initialize();
            if (fluid.Error != null) { Fail(fluid.Error); return; }
            double initializeMs = resetTimer.Elapsed.TotalMilliseconds - gridMs;
            Vector3[] positions = boundary.CreateInitialFill(fluid.particleCount, out float spacing);
            double fillMs = resetTimer.Elapsed.TotalMilliseconds - gridMs - initializeMs;
            fluid.SetComparisonInitialPositions(positions, spacing);
            fluid.particleRadiusScale = spacing * .34f / fluid.CellSize;
            if (Debug.isDebugBuild)
                Debug.Log($"Bottle MPM config: particles={fluid.particleCount}, grid={fluid.GridDimensions}, " +
                    $"substeps={fluid.substeps}, diagnosticReduction={fluid.reduceDiagnosticAtomics}, " +
                    $"tightGrid={boundary.tightGrid}, soa={fluid.useParticleSoA}, cachedWall={fluid.cacheGridBoundary}, " +
                    $"groups={fluid.GroupSizeSummary}{(fluid.IsCalibrating ? " (calibrating)" : "")}, " +
                    $"p2g={fluid.ActiveP2gMode}, mlsSort={fluid.mlsSortInterval}", this);
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
            resetCompletedAt = Time.realtimeSinceStartupAsDouble;
            waitingForResetReady = Debug.isDebugBuild;
            if (Debug.isDebugBuild)
                Debug.Log($"Bottle reset timing: particles={fluid.particleCount}, grid={gridMs:F2}ms, " +
                    $"initialize={initializeMs:F2}ms, fill={fillMs:F2}ms, " +
                    $"total={resetTimer.Elapsed.TotalMilliseconds:F2}ms, calibrating={fluid.IsCalibrating}", this);
        }

        public void SelectParticleCount(int count)
        {
            if (count != 16384 && count != 32768 && count != 65536 && count != 131072)
                throw new ArgumentOutOfRangeException(nameof(count), "Choose 16K, 32K, 64K or 128K.");
            if (!ready) throw new InvalidOperationException(Error ?? "Bottle is not ready.");
            if (count == requestedParticleCount) return;
            requestedParticleCount = count;
            particleSlider = SliderForParticleCount(count);
            resetRequested = true;
            dragging = false;
        }

        public void SetSurfaceRendering(bool enabled)
        {
            if (!ready) throw new InvalidOperationException(Error ?? "Bottle is not ready.");
            fluid.SetSurfaceRendering(enabled);
        }

        public void ToggleRendering() => SetSurfaceRendering(!fluid.renderSurface);

        public static int SubstepsForParticleCount(int count) => 8;

        public static int ParticleCountForSlider(float value) => 16384 << Mathf.Clamp(Mathf.RoundToInt(value), 0, 3);
        public static int SliderForParticleCount(int count)
        {
            switch (count)
            {
                case 16384: return 0;
                case 32768: return 1;
                case 65536: return 2;
                case 131072: return 3;
                default: throw new ArgumentOutOfRangeException(nameof(count));
            }
        }

        void Update()
        {
            if (!ready || Error != null) return;
            if (resetRequested && !pending) ResetWater();
            if (Input.GetKeyDown(KeyCode.R)) ResetWater();
            if (Input.GetKeyDown(KeyCode.Space)) fluid.paused = !fluid.paused;
            if (waitingForResetReady && !fluid.IsCalibrating)
            {
                waitingForResetReady = false;
                Debug.Log($"Bottle reset ready: particles={fluid.particleCount}, " +
                    $"afterResetMs={(Time.realtimeSinceStartupAsDouble - resetCompletedAt) * 1000:F2}", this);
            }
            if (fluid.Error != null || fluid.RenderingError != null)
            {
                Fail(fluid.Error ?? fluid.RenderingError);
                return;
            }
            meanFrame = Mathf.Lerp(meanFrame, Time.unscaledDeltaTime, .03f);
            if (fluid.paused || suspended || fluid.IsCalibrating) { skipDelta = true; dragging = false; return; }
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
            probe.SetInt("_ParticlePlaneStride", fluid.ParticlePlaneStride);
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
            if (Input.GetMouseButtonDown(0) && (tagRope == null || !tagRope.IsGrabbing)) BeginBoatDrag(point);
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

        public bool IsPointerOnControls(Vector2 screenPoint) => PointerOnControls(screenPoint);

        bool PointerOnControls(Vector2 screenPoint)
        {
            float scale = UiScale();
            return ControlsRect(scale).Contains(new Vector2(screenPoint.x / scale, (Screen.height - screenPoint.y) / scale));
        }

        void OnApplicationPause(bool value) { suspended = value; skipDelta = true; dragging = false; CancelSliderGesture(); }
        void OnApplicationFocus(bool value) { if (!value) { skipDelta = true; dragging = false; CancelSliderGesture(); } }

        void CancelSliderGesture()
        {
            sliderDragging = false;
            particleSlider = SliderForParticleCount(requestedParticleCount);
        }

        void Fail(string message)
        {
            Error = message;
            if (fluid != null) fluid.paused = true;
            Debug.LogError("Bottle Voyage: " + message, this);
        }

        void OnDestroy()
        {
            Shader.SetGlobalFloat("_VoyageReflectionStrength", 0);
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
            return new Rect(safe.xMax / scale - 610, (Screen.height - safe.yMin) / scale - 88, 574, 58);
        }

        void OnGUI()
        {
            if (!showCaption && Error == null) return;
            if (titleStyle == null)
            {
                titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 25, fontStyle = FontStyle.Normal };
                subtitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14 };
                footStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
                titleStyle.normal.textColor = captionColor;
                subtitleStyle.normal.textColor = Color.Lerp(captionColor,new Color(.5f,.5f,.5f),.25f);
                footStyle.normal.textColor = Color.Lerp(captionColor,new Color(.5f,.5f,.5f),.25f);
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
            GUI.Label(new Rect(x, bottom, 750, 30), Error ?? (Application.isMobilePlatform ?
                "DRAG THE SAILBOAT     /     SLIDE TO SET PARTICLES" :
                "DRAG THE SAILBOAT     /     SPACE TO PAUSE     /     R TO RESET"), footStyle);
            if (Debug.isDebugBuild)
                GUI.Label(new Rect(x, bottom - 22, 750, 25), string.Format(
                    "{0}K particles | {1:F0} FPS | physics backlog {2:F3}s",
                    fluid == null ? 0 : fluid.particleCount / 1024,
                    meanFrame > 0 ? 1 / meanFrame : 0, backlog), footStyle);
            if (ready && Error == null && fluid.IsCalibrating)
                GUI.Label(new Rect(x, bottom - 44, 750, 25), string.Format(fluid.IsBenchmarkingP2g ?
                    "Benchmarking P2G modes  {0:P0}" : "Measuring GPU compute  {0:P0}",
                    fluid.CalibrationProgress), footStyle);
            if (ready && Error == null)
            {
                Rect controls = ControlsRect(scale);
                float bx = controls.x, by = controls.y;
                Rect sliderRect = new Rect(bx + 6, by + 9, 278, 28);
                EventType rawType = Event.current.rawType;
                float previousSlider = particleSlider;
                particleSlider = GUI.HorizontalSlider(sliderRect, particleSlider, 0, 3);
                if (!Mathf.Approximately(previousSlider, particleSlider)) sliderDragging = true;
                GUI.Label(new Rect(bx + 304, by + 5, 66, 30), (ParticleCountForSlider(particleSlider) / 1024) + "K", subtitleStyle);
                GUI.Label(new Rect(bx + 3, by + 32, 48, 20), "16K", footStyle);
                GUI.Label(new Rect(bx + 88, by + 32, 48, 20), "32K", footStyle);
                GUI.Label(new Rect(bx + 177, by + 32, 48, 20), "64K", footStyle);
                GUI.Label(new Rect(bx + 259, by + 32, 48, 20), "128K", footStyle);
                if (sliderDragging && rawType == EventType.MouseUp)
                {
                    sliderDragging = false;
                    particleSlider = Mathf.Round(particleSlider);
                    SelectParticleCount(ParticleCountForSlider(particleSlider));
                }
                if (GUI.Button(new Rect(bx + 383, by + 5, 190, 32),
                    fluid.renderSurface ? "Water  >  Particles" : "Particles  >  Water")) ToggleRendering();
            }
            GUI.matrix = previous;
        }
    }
}
