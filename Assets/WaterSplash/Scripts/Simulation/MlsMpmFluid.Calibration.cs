using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace WaterSplash
{
    // Device-agnostic per-kernel timing and 64/128 workgroup selection. The live
    // simulation is frozen while scratch copies run extra kernel repetitions;
    // each kernel's cost is the GPU-frame delta divided by its repetition count.
    public sealed partial class MlsMpmFluid
    {
        const string GroupCachePrefix = "MlsMpm.GroupSizes.v1|";
        const int CalibrationConditions = 11;
        const int CalibrationBlockFrames = 14;
        const int CalibrationDiscardFrames = 5;
        const double GroupSizeMargin = 0.97;
        static readonly string[] StageNames = { "Clear", "P2G mass", "P2G stress", "Grid", "G2P" };
        static readonly FrameTiming[] frameTimings = new FrameTiming[1];

        const int P2gModeCount = 4;
        // Base, each mode unsorted, sort alone, each mode after a sort.
        const int P2gConditions = 2 + 2 * P2gModeCount;
        static readonly string[] P2gModeNames = { "TwoPass", "Unchecked", "Fused", "Packed16" };

        sealed class GroupCalibration
        {
            public bool p2g;
            public bool transfers;
            public int conditions;
            public int rounds;
            public int block;
            public int encoded;
            public int[] order;
            public List<double>[] gpu;
            public List<double>[] cpu;
            public uint[] faults;
            public ComputeShader shader;
            public ComputeBuffer particles, accumulation, grid, diagnostics;
            public CommandBuffer commands;
            public int copyKernel;
        }

        GroupCalibration calibration;

        public bool IsCalibrating => calibration != null;
        public bool IsBenchmarkingP2g => calibration != null && calibration.p2g;
        public float CalibrationProgress => calibration == null ? 1 :
            (calibration.block * CalibrationBlockFrames + calibration.encoded) /
            (float)(calibration.order.Length * CalibrationBlockFrames);
        public string GroupSizeSummary => supportsGroupVariants ? string.Join("/", groupSizes) : "128";
        public string LastCalibrationReport { get; private set; }
        internal IReadOnlyList<int> StageGroupSizes => groupSizes;

        static int CalibrationRepetitions(int stage) => stage == 0 || stage == 3 ? 24 : 8;

        string GroupCacheKey => GroupCachePrefix + SystemInfo.graphicsDeviceName + "|" +
            SystemInfo.graphicsDeviceVersion + "|" + Application.buildGUID + "|" + activeCount + "|" +
            activeGridWindow.size.x + "x" + activeGridWindow.size.y + "x" + activeGridWindow.size.z;

        void ConfigureGroupSizes()
        {
            LastCalibrationReport = null;
            if (!supportsGroupVariants || groupSizeMode == GroupSizeMode.Fixed128) return;
            if (groupSizeMode == GroupSizeMode.Fixed64)
            {
                for (int i = 0; i < groupSizes.Length; i++) groupSizes[i] = 64;
                return;
            }
            if (TryLoadGroupSizes()) return;
            // Editor timings do not represent a phone; keep 128 unless explicitly measured.
            if (Application.isPlaying && !Application.isEditor) StartGroupSizeCalibration(2);
        }

        bool TryLoadGroupSizes()
        {
            string[] values = PlayerPrefs.GetString(GroupCacheKey, "").Split(',');
            if (values.Length != groupSizes.Length) return false;
            var parsed = new int[values.Length];
            for (int i = 0; i < values.Length; i++)
                if (!int.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed[i]) ||
                    (parsed[i] != 64 && parsed[i] != 128)) return false;
            Array.Copy(parsed, groupSizes, parsed.Length);
            if (Debug.isDebugBuild) Debug.Log("MLS-MPM group sizes loaded from cache: " + GroupSizeSummary, this);
            return true;
        }

        // Freezes the live water for about rounds * 2.5 seconds on a phone.
        public bool StartGroupSizeCalibration(int rounds = 3)
        {
            if (!IsInitialized || Error != null || !supportsGroupVariants || !Application.isPlaying) return false;
            rounds = Mathf.Clamp(rounds, 1, 8);
            var order = new int[rounds * CalibrationConditions];
            for (int round = 0, index = 0; round < rounds; round++)
            {
                order[index++] = 0;
                for (int stage = 0; stage < kernels.Length; stage++)
                {
                    // Alternate which variant runs first to cancel slow clock drift.
                    int first = (round + stage) % 2;
                    order[index++] = 1 + stage * 2 + first;
                    order[index++] = 1 + stage * 2 + (1 - first);
                }
            }
            BeginCalibration(false, CalibrationConditions, rounds, order);
            return true;
        }

        // Times one full substep of every P2G mode, unsorted and right after a spatial sort.
        public bool StartP2gBenchmark(int rounds = 3)
        {
            if (!IsInitialized || Error != null || !supportsP2gVariants || !Application.isPlaying) return false;
            rounds = Mathf.Clamp(rounds, 1, 8);
            var order = new int[rounds * P2gConditions];
            for (int round = 0, index = 0; round < rounds; round++)
            {
                order[index++] = 0;
                for (int i = 0; i < P2gConditions - 1; i++)
                    order[index++] = 1 + (i + round * 3) % (P2gConditions - 1);
            }
            BeginCalibration(true, P2gConditions, rounds, order);
            return true;
        }

        public bool StartTransferBenchmark(int rounds = 4)
        {
            if (!IsInitialized || Error != null || !supportsOptimizedTransfers || !Application.isPlaying) return false;
            rounds = Mathf.Clamp(rounds, 2, 8);
            var order = new int[rounds * 4];
            for (int r = 0; r < rounds; r++)
            {
                order[r * 4] = 0;
                for (int i = 0; i < 3; i++) order[r * 4 + i + 1] = 1 + (i + r) % 3;
            }
            BeginCalibration(false, 4, rounds, order);
            calibration.transfers = true;
            return true;
        }

        void BeginCalibration(bool p2g, int conditions, int rounds, int[] order)
        {
            ReleaseCalibration();
            var c = new GroupCalibration
            {
                p2g = p2g, conditions = conditions, rounds = rounds, order = order,
                gpu = new List<double>[conditions], cpu = new List<double>[conditions], faults = new uint[conditions]
            };
            for (int i = 0; i < conditions; i++)
            {
                c.gpu[i] = new List<double>();
                c.cpu[i] = new List<double>();
            }
            c.shader = Instantiate(simulationShader);
            c.shader.name = "MLS-MPM calibration solver";
            SetSolverConstants(c.shader);
            c.particles = new ComputeBuffer(activeCount * 5, sizeof(float) * 4);
            c.accumulation = new ComputeBuffer(activeCellCount * 4, sizeof(int));
            c.grid = new ComputeBuffer(activeCellCount, sizeof(float) * 4);
            c.diagnostics = new ComputeBuffer(8, sizeof(uint));
            ResetCalibrationDiagnostics(c);
            BindMlsKernels(c.shader, c.particles, c.accumulation, c.grid, c.diagnostics);
            c.copyKernel = c.shader.FindKernel("CopyProbeParticles");
            c.shader.SetBuffer(c.copyKernel, "_ProbeSource", particles);
            c.shader.SetBuffer(c.copyKernel, "_Particles", c.particles);
            c.shader.SetInt("_ProbeFloat4Count", activeCount * 5);
            if (p2g)
            {
                for (int i = 0; i < sortKernels.Length; i++)
                {
                    c.shader.SetBuffer(sortKernels[i], "_SortBins", sortBins);
                    if (i == 0 || i == 2) continue;
                    c.shader.SetBuffer(sortKernels[i], "_Particles", c.particles);
                    c.shader.SetBuffer(sortKernels[i], "_SortKeys", sortKeys);
                    c.shader.SetBuffer(sortKernels[i], "_SortRanks", sortRanks);
                    c.shader.SetBuffer(sortKernels[i], "_SortedParticles", sortedParticles);
                }
                c.shader.SetInt("_SortBlocks", sortBlocks);
                c.shader.SetInt("_SortBinCount", sortBlocks * sortBlocks * sortBlocks);
            }
            c.commands = new CommandBuffer { name = p2g ? "MLS-MPM P2G benchmark" : "MLS-MPM workgroup calibration" };
            calibration = c;
        }

        static void ResetCalibrationDiagnostics(GroupCalibration c) =>
            c.diagnostics.SetData(new uint[] { 0, 0, 0, uint.MaxValue, 0, 0, 0, 0 });

        void UpdateCalibration()
        {
            GroupCalibration c = calibration;
            FrameTimingManager.CaptureFrameTimings();
            // Timings lag a few frames, so the first frames of each block are discarded.
            if (c.encoded >= CalibrationDiscardFrames)
            {
                int condition = c.order[c.block];
                if (FrameTimingManager.GetLatestTimings(1, frameTimings) > 0 && frameTimings[0].gpuFrameTime > 0)
                    c.gpu[condition].Add(frameTimings[0].gpuFrameTime);
                c.cpu[condition].Add(Time.unscaledDeltaTime * 1000.0);
            }
            if (c.encoded >= CalibrationBlockFrames)
            {
                c.encoded = 0;
                if (c.p2g || c.transfers)
                {
                    // A faulted scratch solver early-outs and would time as free; attribute and clear.
                    var flags = new uint[8];
                    c.diagnostics.GetData(flags);
                    c.faults[c.order[c.block]] |= flags[0];
                    if (flags[0] != 0) ResetCalibrationDiagnostics(c);
                }
                if (++c.block >= c.order.Length)
                {
                    if (c.transfers) FinishTransferBenchmark();
                    else if (c.p2g) FinishP2gBenchmark(); else FinishCalibration();
                    return;
                }
            }
            EncodeCalibrationFrame(c, c.order[c.block]);
            c.encoded++;
        }

        void EncodeCalibrationFrame(GroupCalibration c, int condition)
        {
            CommandBuffer cb = c.commands;
            cb.Clear();
            // The live solver call also builds the shared static wall cache if needed.
            configureSimulation?.Invoke(cb, solver);
            configureSimulation?.Invoke(cb, c.shader);
            SetStepParameters(cb, c.shader, (float)(FixedTick / substeps));
            SetP2gParameters(cb, c.shader, P2gMode.TwoPass);
            Vector3 sphere = SpherePosition();
            cb.SetComputeVectorParam(c.shader, "_Sphere", new Vector4(sphere.x, sphere.y, sphere.z,
                sphereObstacle == null ? 0 : sphereRadius / CellSize));
            cb.SetComputeVectorParam(c.shader, "_SphereVelocity", Vector4.zero);
            cb.SetComputeVectorParam(c.shader, "_Acceleration", gravity / CellSize);
            cb.DispatchCompute(c.shader, c.copyKernel, (activeCount * 5 + 127) / 128, 1, 1);
            // A full tick of baseline work keeps GPU clocks at their in-game level.
            for (int step = 0; step < substeps; step++)
            for (int stage = 0; stage < kernels.Length; stage++)
                cb.DispatchCompute(c.shader, kernels[stage], StageGroups(stage, 128), 1, 1);
            if (condition > 0 && c.transfers)
            {
                bool previous = optimizeTransfers;
                int[] sizes = (int[])groupSizes.Clone();
                optimizeTransfers = condition != 1;
                for (int i = 0; i < groupSizes.Length; i++) groupSizes[i] = condition == 3 ? 64 : 128;
                SetP2gParameters(cb, c.shader, ActiveP2gMode);
                for (int repetition = 0; repetition < 16; repetition++)
                    EncodeMlsSubstep(cb, c.shader, ActiveP2gMode, false);
                optimizeTransfers = previous;
                Array.Copy(sizes, groupSizes, sizes.Length);
            }
            else if (condition > 0 && c.p2g)
            {
                bool sorted = condition > P2gModeCount;
                if (sorted) EncodeSpatialSort(cb, c.shader);
                int mode = sorted ? condition - P2gModeCount - 2 : condition - 1;
                if (mode >= 0)
                {
                    SetP2gParameters(cb, c.shader, (P2gMode)mode);
                    for (int repetition = 0; repetition < substeps; repetition++)
                        EncodeMlsSubstep(cb, c.shader, (P2gMode)mode, false);
                }
            }
            else if (condition > 0)
            {
                int stage = (condition - 1) / 2;
                int size = (condition - 1) % 2 == 0 ? 128 : 64;
                for (int repetition = 0; repetition < CalibrationRepetitions(stage); repetition++)
                    cb.DispatchCompute(c.shader, StageKernel(stage, size), StageGroups(stage, size), 1, 1);
            }
            Graphics.ExecuteCommandBuffer(cb);
        }

        void FinishTransferBenchmark()
        {
            var c = calibration;
            bool gpu = true;
            for (int i = 0; i < c.conditions; i++) gpu &= c.gpu[i].Count >= 3;
            var medians = new double[c.conditions];
            for (int i = 0; i < medians.Length; i++) medians[i] = Median(gpu ? c.gpu[i] : c.cpu[i]);
            LastCalibrationReport = string.Format(CultureInfo.InvariantCulture,
                "MLS-MPM transfer benchmark: timing={0} gpu='{1}' particles={2} substeps={3} base={4:F3}ms reference128={5:F4}ms optimized128={6:F4}ms optimized64={7:F4}ms faults={8}/{9}/{10}/{11}",
                gpu ? "GPU" : "CPU-interval", SystemInfo.graphicsDeviceName, activeCount, substeps, medians[0],
                (medians[1] - medians[0]) / 16, (medians[2] - medians[0]) / 16, (medians[3] - medians[0]) / 16,
                c.faults[0], c.faults[1], c.faults[2], c.faults[3]);
            Debug.Log(LastCalibrationReport, this);
            ReleaseCalibration();
        }

        // Condition layout: 0 base, 1..4 modes, 5 sort only, 6..9 sort + modes.
        void FinishP2gBenchmark()
        {
            GroupCalibration c = calibration;
            bool useGpu = true;
            for (int i = 0; i < c.conditions; i++) useGpu &= c.gpu[i].Count >= 3;
            var median = new double[c.conditions];
            for (int i = 0; i < c.conditions; i++) median[i] = Median(useGpu ? c.gpu[i] : c.cpu[i]);
            double sortCost = median[P2gModeCount + 1] - median[0];
            var report = new StringBuilder();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "MLS-MPM P2G benchmark: gpu='{0}' api='{1}' timing={2} particles={3} grid={4}x{5}x{6} substeps={7} groups={8} base={9:F2}ms sort={10:F3}ms",
                SystemInfo.graphicsDeviceName, SystemInfo.graphicsDeviceVersion, useGpu ? "GPU" : "CPU-interval",
                activeCount, activeGridWindow.size.x, activeGridWindow.size.y, activeGridWindow.size.z, substeps,
                GroupSizeSummary, median[0], sortCost);
            for (int mode = 0; mode < P2gModeCount; mode++)
            {
                int plain = 1 + mode, sorted = P2gModeCount + 2 + mode;
                double unsortedStep = (median[plain] - median[0]) / substeps;
                double sortedStep = (median[sorted] - median[P2gModeCount + 1]) / substeps;
                report.AppendFormat(CultureInfo.InvariantCulture,
                    " | {0} substep={1:F3} sorted={2:F3} tick={3:F2} sortedTick={4:F2}ms faults={5}/{6}",
                    P2gModeNames[mode], unsortedStep, sortedStep, unsortedStep * substeps,
                    sortedStep * substeps + sortCost, c.faults[plain], c.faults[sorted]);
            }
            report.AppendFormat(" | baseFaults={0} sortFaults={1}", c.faults[0], c.faults[P2gModeCount + 1]);
            LastCalibrationReport = report.ToString();
            Debug.Log(LastCalibrationReport, this);
            ReleaseCalibration();
        }

        void FinishCalibration()
        {
            GroupCalibration c = calibration;
            var faults = new uint[8];
            c.diagnostics.GetData(faults);
            bool useGpu = true;
            for (int i = 0; i < CalibrationConditions; i++) useGpu &= c.gpu[i].Count >= 3;
            var median = new double[CalibrationConditions];
            for (int i = 0; i < CalibrationConditions; i++) median[i] = Median(useGpu ? c.gpu[i] : c.cpu[i]);
            var cost = new double[kernels.Length, 2];
            bool valid = faults[0] == 0;
            for (int stage = 0; stage < kernels.Length; stage++)
            for (int variant = 0; variant < 2; variant++)
            {
                cost[stage, variant] = (median[1 + stage * 2 + variant] - median[0]) / CalibrationRepetitions(stage);
                valid &= !double.IsNaN(cost[stage, variant]) && !double.IsInfinity(cost[stage, variant]);
            }
            double substep128 = 0, substepChosen = 0;
            var report = new StringBuilder();
            report.AppendFormat(CultureInfo.InvariantCulture,
                "MLS-MPM group calibration: gpu='{0}' api='{1}' timing={2} particles={3} grid={4}x{5}x{6} substeps={7} base={8:F2}ms",
                SystemInfo.graphicsDeviceName, SystemInfo.graphicsDeviceVersion, useGpu ? "GPU" : "CPU-interval",
                activeCount, activeGridWindow.size.x, activeGridWindow.size.y, activeGridWindow.size.z, substeps, median[0]);
            for (int stage = 0; stage < kernels.Length; stage++)
            {
                double c128 = cost[stage, 0], c64 = cost[stage, 1];
                int chosen = valid && c64 < c128 * GroupSizeMargin && c128 - c64 > 0.002 ? 64 : 128;
                groupSizes[stage] = chosen;
                substep128 += c128;
                substepChosen += chosen == 64 ? c64 : c128;
                report.AppendFormat(CultureInfo.InvariantCulture, " | {0} 128={1:F3} 64={2:F3} -> {3}",
                    StageNames[stage], c128, c64, chosen);
            }
            report.AppendFormat(CultureInfo.InvariantCulture,
                " | substep 128={0:F3} chosen={1:F3}ms | tick 128={2:F2} chosen={3:F2}ms | faults={4} valid={5}",
                substep128, substepChosen, substep128 * substeps, substepChosen * substeps, faults[0], valid);
            if (!valid) for (int i = 0; i < groupSizes.Length; i++) groupSizes[i] = 128;
            else
            {
                PlayerPrefs.SetString(GroupCacheKey, string.Join(",", groupSizes));
                PlayerPrefs.Save();
            }
            LastCalibrationReport = report.ToString();
            Debug.Log(LastCalibrationReport, this);
            ReleaseCalibration();
        }

        static double Median(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 1 ? values[middle] : .5 * (values[middle - 1] + values[middle]);
        }

        void ReleaseCalibration()
        {
            if (calibration == null) return;
            calibration.particles?.Release();
            calibration.accumulation?.Release();
            calibration.grid?.Release();
            calibration.diagnostics?.Release();
            calibration.commands?.Release();
            if (calibration.shader != null)
            {
                if (Application.isPlaying) Destroy(calibration.shader); else DestroyImmediate(calibration.shader);
            }
            calibration = null;
        }
    }
}
