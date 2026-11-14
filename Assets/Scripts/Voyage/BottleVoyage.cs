using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace WaterSplash
{
    [DefaultExecutionOrder(-120)]
    public sealed class BottleVoyage : MonoBehaviour
    {
        public MlsMpmFluid fluid;
        public Transform boat;
        public Camera waterCamera;
        public BottleBoundary boundary;
        public TagRope tagRope;
        public Cubemap reflection;
        public Texture2D bottleProfile;
        public RectTransform safeArea;
        public GraphicRaycaster uiRaycaster;
        public Slider particleSlider;
        public Text countLabel, statusLabel, footerLabel, modeLabel;
        public bool IsDraggingBoat => dragging;

        readonly Vector4[] probePositions = new Vector4[5], samples = new Vector4[5];
        readonly List<RaycastResult> uiHits = new List<RaycastResult>();
        ComputeShader probe;
        ComputeBuffer partials, results;
        AsyncGPUReadbackRequest pendingRequest;
        Quaternion heading;
        Vector3 initialBoatPosition, desiredPosition, desiredNormal = Vector3.up, boatTarget, dragOffset;
        Renderer[] boatRenderers;
        float nextProbe, meanFrame, dragPlaneHeight, backlog, nextStatus;
        int collectKernel, reduceKernel, blocks, generation, requestedCount;
        bool pending, dragging, skipDelta, resetRequested, paused, suspended;
        string error;
        Rect lastSafeArea;

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
            results = new ComputeBuffer(5, 16);
            requestedCount = fluid.particleCount;
            particleSlider.SetValueWithoutNotify(SliderFor(requestedCount));
            PreviewParticleCount(particleSlider.value);
            statusLabel.gameObject.SetActive(Debug.isDebugBuild);
            footerLabel.text = Application.isMobilePlatform ?
                "DRAG THE SAILBOAT     /     SLIDE TO SET PARTICLES" :
                "DRAG THE SAILBOAT     /     SPACE TO PAUSE     /     R TO RESET";
            UpdateSafeArea();
            UpdateModeLabel();
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
            fluid.SpherePosition = HullCenter();
            fluid.Initialize(boundary);
            probe = fluid.Solver;
            collectKernel = probe.FindKernel("Collect");
            reduceKernel = probe.FindKernel("Reduce");
            probe.SetBuffer(reduceKernel, "_Samples", results);
            probe.SetFloat("_ProbeRadius", .23f);
            int nextBlocks = (fluid.particleCount + 127) / 128;
            if (blocks != nextBlocks)
            {
                partials?.Release();
                blocks = nextBlocks;
                partials = new ComputeBuffer(blocks * 5, 16);
            }
            probe.SetBuffer(collectKernel, "_Partials", partials);
            probe.SetBuffer(reduceKernel, "_Partials", partials);
            probe.SetInt("_BlockCount", blocks);
            Array.Clear(samples, 0, 5);
            nextProbe = Time.unscaledTime;
            backlog = 0;
            skipDelta = true;
        }

        static int CountFor(float slider) => 16384 << Mathf.Clamp(Mathf.RoundToInt(slider), 0, 3);
        static float SliderFor(int count) => Mathf.Log(count / 16384f, 2);

        public void PreviewParticleCount(float value)
        {
            countLabel.text = CountFor(value) / 1024 + "K";
            if (!Input.GetMouseButton(0)) CommitParticleCount();
        }

        public void CommitParticleCount()
        {
            particleSlider.SetValueWithoutNotify(Mathf.Round(particleSlider.value));
            int count = CountFor(particleSlider.value);
            if (count == requestedCount) return;
            requestedCount = count;
            resetRequested = true;
            dragging = false;
        }

        public void ToggleWater()
        {
            fluid.renderSurface = !fluid.renderSurface;
            UpdateModeLabel();
        }

        void UpdateModeLabel() => modeLabel.text = fluid.renderSurface ? "Water  >  Particles" : "Particles  >  Water";

        void UpdateSafeArea()
        {
            lastSafeArea = Screen.safeArea;
            safeArea.anchorMin = new Vector2(lastSafeArea.xMin / Screen.width, lastSafeArea.yMin / Screen.height);
            safeArea.anchorMax = new Vector2(lastSafeArea.xMax / Screen.width, lastSafeArea.yMax / Screen.height);
        }

        void Update()
        {
            if (Screen.safeArea != lastSafeArea) UpdateSafeArea();
            if (error != null) return;
            if (resetRequested && !pending || Input.GetKeyDown(KeyCode.R)) ResetWater();
            if (Input.GetKeyDown(KeyCode.Space)) paused = !paused;
            if (fluid.Error != null) { Fail(fluid.Error); return; }
            meanFrame = Mathf.Lerp(meanFrame, Time.unscaledDeltaTime, .03f);
            if (Debug.isDebugBuild && Time.unscaledTime >= nextStatus)
            {
                nextStatus = Time.unscaledTime + .25f;
                statusLabel.text = $"{fluid.particleCount / 1024}K particles | {1 / Mathf.Max(meanFrame, 1e-4f):F0} FPS | physics backlog {backlog:F3}s";
            }
            if (paused || suspended) { skipDelta = true; dragging = false; return; }
            UpdatePointer();
            if (skipDelta) { skipDelta = false; return; }
            backlog += Time.unscaledDeltaTime;
            int due = Math.Min(4, (int)(backlog / MlsMpmFluid.FixedTick));
            for (int i = 0; i < due; i++)
            {
                AdvanceBoat(MlsMpmFluid.FixedTick);
                // The water collider sweeps at most a quarter cell per substep, even when the visible boat jumps.
                float speed = Mathf.Min(6, .25f * fluid.CellSize * fluid.substeps / MlsMpmFluid.FixedTick);
                fluid.SpherePosition = Vector3.MoveTowards(fluid.SpherePosition, HullCenter(), speed * MlsMpmFluid.FixedTick);
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
            if (error != null || pending || resetRequested || paused || suspended || Time.unscaledTime < nextProbe) return;
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
            float padding = 18 * uiRaycaster.GetComponent<Canvas>().scaleFactor;
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
            uiHits.Clear();
            uiRaycaster.Raycast(new PointerEventData(EventSystem.current) { position = point }, uiHits);
            return uiHits.Count > 0;
        }

        void OnApplicationFocus(bool focus)
        {
            if (focus) return;
            skipDelta = true;
            dragging = false;
            particleSlider.SetValueWithoutNotify(SliderFor(requestedCount));
            countLabel.text = requestedCount / 1024 + "K";
        }

        void OnApplicationPause(bool value) { suspended = value; skipDelta = true; }

        void Fail(string message)
        {
            error = message;
            footerLabel.text = message;
            particleSlider.interactable = false;
            Debug.LogError("Bottle Voyage: " + message, this);
        }

        void OnDestroy()
        {
            generation++;
            if (pending) pendingRequest.WaitForCompletion();
            partials?.Release();
            results?.Release();
        }
    }
}