using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nanite
{
    /// <summary>
    /// Fixed-slot packed Page pool. The table and residency/request masks are the stable ABI
    /// consumed by future GPU traversal, streaming and RT code. Geometry rendering still has
    /// a compatibility decoded path until packed-page GPU decode is introduced.
    /// </summary>
    public sealed class NaniteGpuPagePool : IDisposable
    {
        public const int SlotBytes = 256 * 1024;
        public const int PageDecodeEntryBytes = 64;
        public const int ResidentPageEntryBytes = 32;
        // Draw-time layout: position.xyz + uv.xy + normal.xyz + tangent.xyzw.
        // NPG1 quantization/oct decode happens once on residency, never per pixel.
        public const int ResidentVertexBytes = 48;
        public const uint InvalidSlot = 0xFFFFFFFFu;

        public const uint FlagResident = 1u << 0;
        public const uint FlagPinned = 1u << 1;
        public const uint FlagRoot = 1u << 2;
        public const uint FlagBinaryV1 = 1u << 3;
        public const uint FlagResidentGeometryReady = 1u << 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuPageTableEntry
        {
            public uint slotIndex;
            public uint byteAddress;
            public uint packedBytes;
            public uint flags;
            public uint meshIndex;
            public uint localPageIndex;
            public uint generation;
            public uint reserved;
        }

        /// <summary>
        /// Hot-path decode metadata for one resident NPG1 Page. This deliberately mirrors a
        /// 64-byte HLSL structure so raster/resolve shaders never have to parse the Page header.
        /// Addresses are absolute byte offsets into <see cref="PackedPoolBuffer"/>.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct GpuPageDecodeEntry
        {
            public uint byteAddress;
            public uint packedBytes;
            public uint vertexSectionAddress;
            public uint indexSectionAddress;

            public uint flags;
            public uint vertexCount;
            public uint indexCount;
            public uint vertexRecordBytes;

            public Vector3 positionMin;
            public uint generation;

            public Vector3 positionExtent;
            public uint reserved;
        }

        /// <summary>
        /// Draw-time geometry view produced once when an NPG1 Page becomes resident. Offsets
        /// are element indices, not byte addresses. The table is published in lockstep with
        /// the Page/Decode tables so a draw can never observe a half-transcoded Page.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct GpuResidentPageEntry
        {
            public uint vertexBase;
            public uint indexBase;
            public uint vertexCount;
            public uint indexCount;
            public uint flags;
            public uint generation;
            public uint reserved0;
            public uint reserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PageTranscodeTask
        {
            public uint pageId;
            public uint vertexBase;
            public uint indexBase;
            public uint vertexCount;
            public uint indexCount;
            public uint reserved0;
            public uint reserved1;
            public uint reserved2;
        }

        readonly struct PageKey : IEquatable<PageKey>
        {
            public readonly int meshId;
            public readonly int localPageIndex;

            public PageKey(int meshId, int localPageIndex)
            {
                this.meshId = meshId;
                this.localPageIndex = localPageIndex;
            }

            public bool Equals(PageKey other) =>
                meshId == other.meshId && localPageIndex == other.localPageIndex;

            public override bool Equals(object obj) => obj is PageKey other && Equals(other);

            public override int GetHashCode() => unchecked(meshId * 397 ^ localPageIndex);
        }

        struct CpuPageRecord
        {
            public NaniteMesh mesh;
            public NaniteMeshPage page;
            public int meshIndex;
            public int localPageIndex;
            public int slotIndex;
            public int packedBytes;
            public int residentVertexBase;
            public int residentIndexBase;
            public int vertexCount;
            public int indexCount;
            public bool root;
            public bool pinned;
            public bool resident;
            public bool residentGeometryReady;
            public int lastTouchedFrame;
        }

        struct RetiredAllocation
        {
            public int slotIndex;
            public int vertexBase;
            public int vertexCount;
            public int indexBase;
            public int indexCount;
            public int fallbackReusableFrame;
            public GraphicsFence fence;
            public bool hasFence;
        }

        sealed class GpuRangeAllocator
        {
            struct Range
            {
                public int offset;
                public int count;
            }

            readonly List<Range> freeRanges = new List<Range>(64);

            public int Capacity { get; private set; }
            public int FreeCount { get; private set; }

            public void Reset(int capacity)
            {
                Capacity = Mathf.Max(0, capacity);
                FreeCount = Capacity;
                freeRanges.Clear();
                if (Capacity > 0)
                    freeRanges.Add(new Range { offset = 0, count = Capacity });
            }

            public bool TryAllocate(int count, out int offset)
            {
                offset = -1;
                if (count <= 0)
                    return true;

                int bestIndex = -1;
                int bestWaste = int.MaxValue;
                for (int i = 0; i < freeRanges.Count; i++)
                {
                    Range range = freeRanges[i];
                    if (range.count < count)
                        continue;
                    int waste = range.count - count;
                    if (waste >= bestWaste)
                        continue;
                    bestWaste = waste;
                    bestIndex = i;
                    if (waste == 0)
                        break;
                }

                if (bestIndex < 0)
                    return false;
                Range selected = freeRanges[bestIndex];
                offset = selected.offset;
                if (selected.count == count)
                {
                    freeRanges.RemoveAt(bestIndex);
                }
                else
                {
                    selected.offset += count;
                    selected.count -= count;
                    freeRanges[bestIndex] = selected;
                }
                FreeCount -= count;
                return true;
            }

            public void Free(int offset, int count)
            {
                if (count <= 0)
                    return;
                if (offset < 0 || offset > Capacity - count)
                    throw new ArgumentOutOfRangeException(
                        nameof(offset),
                        $"GPU range [{offset}, {offset + count}) exceeds capacity {Capacity}.");

                int insertIndex = 0;
                while (insertIndex < freeRanges.Count && freeRanges[insertIndex].offset < offset)
                    insertIndex++;
                if (insertIndex > 0)
                {
                    Range previous = freeRanges[insertIndex - 1];
                    if (previous.offset + previous.count > offset)
                        throw new InvalidOperationException("GPU range allocator detected an overlapping free.");
                }
                if (insertIndex < freeRanges.Count && offset + count > freeRanges[insertIndex].offset)
                    throw new InvalidOperationException("GPU range allocator detected an overlapping free.");

                freeRanges.Insert(insertIndex, new Range { offset = offset, count = count });
                FreeCount += count;

                if (insertIndex > 0)
                {
                    Range previous = freeRanges[insertIndex - 1];
                    Range current = freeRanges[insertIndex];
                    if (previous.offset + previous.count == current.offset)
                    {
                        previous.count += current.count;
                        freeRanges[insertIndex - 1] = previous;
                        freeRanges.RemoveAt(insertIndex);
                        insertIndex--;
                    }
                }
                if (insertIndex + 1 < freeRanges.Count)
                {
                    Range current = freeRanges[insertIndex];
                    Range next = freeRanges[insertIndex + 1];
                    if (current.offset + current.count == next.offset)
                    {
                        current.count += next.count;
                        freeRanges[insertIndex] = current;
                        freeRanges.RemoveAt(insertIndex + 1);
                    }
                }
            }
        }

        readonly List<CpuPageRecord> pages = new List<CpuPageRecord>(256);
        readonly Dictionary<PageKey, int> pageIdByKey = new Dictionary<PageKey, int>(256);
        readonly Stack<int> freeSlots = new Stack<int>(256);
        readonly List<RetiredAllocation> retiredAllocations = new List<RetiredAllocation>(32);
        readonly GpuRangeAllocator residentVertexAllocator = new GpuRangeAllocator();
        readonly GpuRangeAllocator residentIndexAllocator = new GpuRangeAllocator();

        GraphicsBuffer packedPoolBuffer;
        GraphicsBuffer residentVertexBuffer;
        GraphicsBuffer residentIndexBuffer;
        readonly ComputeBuffer[] pageTableBuffers = new ComputeBuffer[2];
        readonly ComputeBuffer[] pageDecodeBuffers = new ComputeBuffer[2];
        readonly ComputeBuffer[] residentPageTableBuffers = new ComputeBuffer[2];
        readonly ComputeBuffer[] residencyBitsetBuffers = new ComputeBuffer[2];
        readonly ComputeBuffer[] requestBitsetBuffers = new ComputeBuffer[2];
        ComputeBuffer transcodeTaskBuffer;
        GpuPageTableEntry[] pageTableCpu = Array.Empty<GpuPageTableEntry>();
        GpuPageDecodeEntry[] pageDecodeCpu = Array.Empty<GpuPageDecodeEntry>();
        GpuResidentPageEntry[] residentPageTableCpu = Array.Empty<GpuResidentPageEntry>();
        readonly List<int> pendingTranscodePageIds = new List<int>(64);
        uint[] residencyBitsCpu = Array.Empty<uint>();
        uint[] requestBitsCpu = Array.Empty<uint>();
        int signature;
        uint generation;
        bool requestReadbackPending;
        int activeTableBufferIndex;
        int activeRequestBufferIndex;
        int pendingRequestBufferIndex = -1;
        int lastRequestReadbackFrame = -100000;
        bool loggedPoolFull;
        bool loggedFullyResident;
        bool loggedFirstStreamUpload;
        ComputeShader residentTranscodeShader;
        bool dynamicResidentRanges;

        public int PageCount => pages.Count;
        public int SlotCount { get; private set; }
        public int ResidentPageCount { get; private set; }
        public int PinnedPageCount { get; private set; }
        public int RootPageCount { get; private set; }
        public int ResidentBytes { get; private set; }
        public int PoolBytes => SlotCount * SlotBytes;
        public long ResidentGeometryBytes { get; private set; }
        public bool NeedsRetirementFence
        {
            get
            {
                if (!SystemInfo.supportsGraphicsFence)
                    return false;
                for (int i = 0; i < retiredAllocations.Count; i++)
                {
                    if (!retiredAllocations[i].hasFence)
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Associates every allocation retired before this point with a fence recorded after
        /// the frame's Nanite consumers. The packed slot and decoded ranges remain quarantined
        /// until that fence passes, so a table generation can never alias in-flight geometry.
        /// </summary>
        public void AttachRetirementFence(GraphicsFence fence)
        {
            if (!SystemInfo.supportsGraphicsFence)
                return;

            for (int i = 0; i < retiredAllocations.Count; i++)
            {
                RetiredAllocation retired = retiredAllocations[i];
                if (retired.hasFence)
                    continue;
                retired.fence = fence;
                retired.hasFence = true;
                retiredAllocations[i] = retired;
            }
        }
        public bool RequiresEviction => SlotCount > 0 && SlotCount < PageCount;
        public bool CanFitAllPages => IsReady && SlotCount >= PageCount;
        public bool AllPagesResident => IsReady && ResidentPageCount == PageCount;
        public uint Generation => generation;
        public GraphicsBuffer PackedPoolBuffer => packedPoolBuffer;
        public GraphicsBuffer ResidentVertexBuffer => residentVertexBuffer;
        public GraphicsBuffer ResidentIndexBuffer => residentIndexBuffer;
        public ComputeBuffer PageTableBuffer => pageTableBuffers[activeTableBufferIndex];
        public ComputeBuffer PageDecodeBuffer => pageDecodeBuffers[activeTableBufferIndex];
        public ComputeBuffer ResidentPageTableBuffer => residentPageTableBuffers[activeTableBufferIndex];
        public ComputeBuffer ResidencyBitsetBuffer => residencyBitsetBuffers[activeTableBufferIndex];
        public ComputeBuffer RequestBitsetBuffer => requestBitsetBuffers[activeRequestBufferIndex];
        public bool IsReady =>
            packedPoolBuffer != null &&
            residentVertexBuffer != null &&
            residentIndexBuffer != null &&
            pageTableBuffers[0] != null &&
            pageTableBuffers[1] != null &&
            pageDecodeBuffers[0] != null &&
            pageDecodeBuffers[1] != null &&
            residentPageTableBuffers[0] != null &&
            residentPageTableBuffers[1] != null &&
            residencyBitsetBuffers[0] != null &&
            residencyBitsetBuffers[1] != null &&
            requestBitsetBuffers[0] != null &&
            requestBitsetBuffers[1] != null &&
            PageCount > 0 &&
            SlotCount > 0;

        /// <param name="maxPoolMiB">Hard upper budget. Actual allocation is capped to the
        /// number of registered pages, so small scenes do not reserve the entire budget.</param>
        public bool EnsureInitialized(IReadOnlyList<NaniteMesh> uniqueMeshes, int maxPoolMiB)
        {
            if (uniqueMeshes == null || uniqueMeshes.Count == 0)
                return false;

            int nextSignature = ComputeSignature(uniqueMeshes, maxPoolMiB);
            if (nextSignature == signature && IsReady)
                return true;

            return Rebuild(uniqueMeshes, maxPoolMiB, nextSignature);
        }

        public bool TryGetPageId(NaniteMesh mesh, int localPageIndex, out int pageId)
        {
            pageId = -1;
            return mesh != null &&
                   pageIdByKey.TryGetValue(
                       new PageKey(mesh.GetInstanceID(), localPageIndex),
                   out pageId);
        }

        public bool TryGetResidentGeometryRange(
            NaniteMesh mesh,
            int localPageIndex,
            out int vertexBase,
            out int indexBase)
        {
            vertexBase = -1;
            indexBase = -1;
            if (!TryGetPageId(mesh, localPageIndex, out int pageId) ||
                (uint)pageId >= (uint)pages.Count)
            {
                return false;
            }

            CpuPageRecord record = pages[pageId];
            if (!record.residentGeometryReady ||
                record.residentVertexBase < 0 ||
                record.residentIndexBase < 0)
            {
                return false;
            }

            vertexBase = record.residentVertexBase;
            indexBase = record.residentIndexBase;
            return true;
        }

        public bool IsResident(int pageId) =>
            (uint)pageId < (uint)pages.Count && pages[pageId].resident;

        public bool IsPinned(int pageId) =>
            (uint)pageId < (uint)pages.Count && pages[pageId].pinned;

        /// <summary>
        /// Transitional packed-raster admission. Until residency-aware parent fallback is wired
        /// into traversal, direct Page consumers may only run when the complete Page set fits in
        /// the pool. Uploads happen once during a scene rebuild; no per-frame readback is used.
        /// </summary>
        public bool EnsureAllPagesResidentForPackedRaster(
            ComputeShader transcodeShader,
            out string error)
        {
            error = null;
            if (!IsReady)
            {
                error = "Page Pool is not initialized.";
                return false;
            }
            // Keep the transcode resource even when the complete-set admission below fails.
            // Constrained pools use it when asynchronous requests populate their working set.
            residentTranscodeShader = transcodeShader;
            if (!CanFitAllPages)
            {
                error = $"working set requires {PageCount} slots but the pool has {SlotCount}.";
                return false;
            }

            bool tableChanged = false;
            for (int pageId = 0; pageId < pages.Count; pageId++)
            {
                if ((pageTableCpu[pageId].flags & FlagBinaryV1) == 0u)
                {
                    error = $"Page {pageId} is not an NPG1/V1 payload.";
                    if (tableChanged)
                        PublishTables();
                    return false;
                }
                if (pages[pageId].resident)
                    continue;
                if (!MakeResident(pageId, pin: false, out error))
                {
                    error = $"Page {pageId} could not become resident: {error}";
                    if (tableChanged)
                        PublishTables();
                    return false;
                }
                tableChanged = true;
            }

            if (tableChanged)
                PublishTables();
            if (!AllPagesResident)
            {
                error = $"residency ended at {ResidentPageCount}/{PageCount} Pages.";
                return false;
            }
            if (!TranscodePendingResidentPages(out error))
            {
                error = $"resident geometry transcode failed: {error}";
                return false;
            }
            for (int pageId = 0; pageId < pages.Count; pageId++)
            {
                if (!pages[pageId].residentGeometryReady)
                {
                    error = $"resident geometry cache is incomplete at Page {pageId}.";
                    return false;
                }
            }

            if (!loggedFullyResident)
            {
                loggedFullyResident = true;
                Debug.Log(
                    $"[Nanite][PagePool] packed-raster working set resident: " +
                    $"pages={ResidentPageCount}/{PageCount}, payload={ResidentBytes / (1024f * 1024f):F2} MiB, " +
                    $"residentCache={ResidentGeometryBytes / (1024f * 1024f):F2} MiB, " +
                    $"slots={SlotCount}, generation={generation}.");
            }
            return true;
        }

        public void Touch(int pageId, int frameIndex)
        {
            if ((uint)pageId >= (uint)pages.Count)
                return;
            CpuPageRecord record = pages[pageId];
            record.lastTouchedFrame = frameIndex;
            pages[pageId] = record;
        }

        public void Dispose()
        {
            packedPoolBuffer?.Dispose();
            residentVertexBuffer?.Dispose();
            residentIndexBuffer?.Dispose();
            transcodeTaskBuffer?.Release();
            for (int i = 0; i < pageTableBuffers.Length; i++)
            {
                pageTableBuffers[i]?.Release();
                pageDecodeBuffers[i]?.Release();
                residentPageTableBuffers[i]?.Release();
                residencyBitsetBuffers[i]?.Release();
                pageTableBuffers[i] = null;
                pageDecodeBuffers[i] = null;
                residentPageTableBuffers[i] = null;
                residencyBitsetBuffers[i] = null;
            }
            for (int i = 0; i < requestBitsetBuffers.Length; i++)
            {
                requestBitsetBuffers[i]?.Release();
                requestBitsetBuffers[i] = null;
            }
            packedPoolBuffer = null;
            residentVertexBuffer = null;
            residentIndexBuffer = null;
            transcodeTaskBuffer = null;
            pages.Clear();
            pageIdByKey.Clear();
            freeSlots.Clear();
            retiredAllocations.Clear();
            residentVertexAllocator.Reset(0);
            residentIndexAllocator.Reset(0);
            pageTableCpu = Array.Empty<GpuPageTableEntry>();
            pageDecodeCpu = Array.Empty<GpuPageDecodeEntry>();
            residentPageTableCpu = Array.Empty<GpuResidentPageEntry>();
            pendingTranscodePageIds.Clear();
            residencyBitsCpu = Array.Empty<uint>();
            requestBitsCpu = Array.Empty<uint>();
            signature = 0;
            generation = 0;
            requestReadbackPending = false;
            activeTableBufferIndex = 0;
            activeRequestBufferIndex = 0;
            pendingRequestBufferIndex = -1;
            lastRequestReadbackFrame = -100000;
            loggedPoolFull = false;
            loggedFullyResident = false;
            loggedFirstStreamUpload = false;
            residentTranscodeShader = null;
            dynamicResidentRanges = false;
            SlotCount = 0;
            ResidentPageCount = 0;
            PinnedPageCount = 0;
            RootPageCount = 0;
            ResidentBytes = 0;
            ResidentGeometryBytes = 0;
        }

        bool Rebuild(IReadOnlyList<NaniteMesh> uniqueMeshes, int maxPoolMiB, int nextSignature)
        {
            uint nextGeneration = generation + 1u;
            Dispose();
            generation = nextGeneration;

            for (int meshIndex = 0; meshIndex < uniqueMeshes.Count; meshIndex++)
            {
                NaniteMesh mesh = uniqueMeshes[meshIndex];
                if (mesh == null || mesh.pageArray == null)
                    continue;

                int meshPageStart = pages.Count;
                int meshRootStart = RootPageCount;
                for (int localPageIndex = 0; localPageIndex < mesh.pageArray.Length; localPageIndex++)
                {
                    NaniteMeshPage page = mesh.pageArray[localPageIndex];
                    if (page == null)
                        continue;

                    bool root = ResolveRootPage(mesh, page, localPageIndex);
                    ResolvePackedGeometryCounts(page, out int vertexCount, out int indexCount);
                    int pageId = pages.Count;
                    pages.Add(new CpuPageRecord
                    {
                        mesh = mesh,
                        page = page,
                        meshIndex = meshIndex,
                        localPageIndex = localPageIndex,
                        slotIndex = -1,
                        packedBytes = page.BinaryStats.blobBytes > 0
                            ? page.BinaryStats.blobBytes
                            : (page.BinaryPayload != null ? page.BinaryPayload.bytes.Length : 0),
                        residentVertexBase = -1,
                        residentIndexBase = -1,
                        vertexCount = vertexCount,
                        indexCount = indexCount,
                        root = root,
                        pinned = root,
                        resident = false,
                        residentGeometryReady = false,
                        lastTouchedFrame = root ? int.MaxValue : -1
                    });
                    pageIdByKey[new PageKey(mesh.GetInstanceID(), localPageIndex)] = pageId;
                    if (root)
                        RootPageCount++;
                }

                if (pages.Count > meshPageStart && RootPageCount == meshRootStart)
                {
                    int fallbackPageId = pages.Count - 1;
                    CpuPageRecord fallbackRoot = pages[fallbackPageId];
                    fallbackRoot.root = true;
                    fallbackRoot.pinned = true;
                    fallbackRoot.lastTouchedFrame = int.MaxValue;
                    pages[fallbackPageId] = fallbackRoot;
                    RootPageCount++;
                    Debug.LogWarning(
                        $"[Nanite][PagePool] Mesh '{mesh.name}' has no root-page flag; " +
                        $"pinning local page {fallbackRoot.localPageIndex} as a safety root. Re-Bake recommended.");
                }
            }

            if (pages.Count == 0)
                return false;

            int maxSlots = Mathf.Max(1, (Mathf.Max(1, maxPoolMiB) * 1024 * 1024) / SlotBytes);
            if (RootPageCount > maxSlots)
            {
                Debug.LogError(
                    $"[Nanite][PagePool] Root working set exceeds the pool budget: " +
                    $"roots={RootPageCount}, slots={maxSlots}, budget={maxPoolMiB} MiB. " +
                    "Increase the pool budget or fix Bake root-page grouping.");
                return false;
            }

            // Capacity is immutable until scene membership changes, but is not over-reserved
            // for a small scene.
            int decodeEntryBytes = Marshal.SizeOf<GpuPageDecodeEntry>();
            int residentEntryBytes = Marshal.SizeOf<GpuResidentPageEntry>();
            if (decodeEntryBytes != PageDecodeEntryBytes)
            {
                Debug.LogError(
                    $"[Nanite][PagePool] Page decode ABI is {decodeEntryBytes} bytes; " +
                    $"HLSL requires {PageDecodeEntryBytes} bytes.");
                Dispose();
                return false;
            }
            if (residentEntryBytes != ResidentPageEntryBytes)
            {
                Debug.LogError(
                    $"[Nanite][PagePool] Resident Page ABI is {residentEntryBytes} bytes; " +
                    $"HLSL requires {ResidentPageEntryBytes} bytes.");
                Dispose();
                return false;
            }
            SlotCount = Mathf.Min(maxSlots, Mathf.Max(RootPageCount, pages.Count));
            dynamicResidentRanges = SlotCount < pages.Count;
            int poolWordCount = checked(SlotCount * (SlotBytes / sizeof(uint)));
            packedPoolBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw,
                poolWordCount,
                sizeof(uint));
            long totalResidentVertices = 0;
            long totalResidentIndices = 0;
            if (dynamicResidentRanges)
            {
                // Size the decoded working set independently for vertices and indices. Taking
                // the largest SlotCount ranges guarantees that any SlotCount resident Pages fit,
                // while avoiding a decoded allocation for the entire virtual Page set.
                var vertexCounts = new int[pages.Count];
                var indexCounts = new int[pages.Count];
                for (int pageId = 0; pageId < pages.Count; pageId++)
                {
                    CpuPageRecord record = pages[pageId];
                    vertexCounts[pageId] = Mathf.Max(0, record.vertexCount);
                    indexCounts[pageId] = Mathf.Max(0, record.indexCount);
                    record.residentVertexBase = -1;
                    record.residentIndexBase = -1;
                    pages[pageId] = record;
                }
                Array.Sort(vertexCounts);
                Array.Sort(indexCounts);
                for (int i = 0; i < SlotCount; i++)
                {
                    totalResidentVertices += vertexCounts[vertexCounts.Length - 1 - i];
                    totalResidentIndices += indexCounts[indexCounts.Length - 1 - i];
                }
            }
            else
            {
                // Preserve the fixed-address full-resident layout used by the production path.
                for (int pageId = 0; pageId < pages.Count; pageId++)
                {
                    CpuPageRecord record = pages[pageId];
                    record.residentVertexBase = checked((int)totalResidentVertices);
                    record.residentIndexBase = checked((int)totalResidentIndices);
                    totalResidentVertices += Mathf.Max(0, record.vertexCount);
                    totalResidentIndices += Mathf.Max(0, record.indexCount);
                    pages[pageId] = record;
                }
            }
            if (totalResidentVertices > int.MaxValue || totalResidentIndices > int.MaxValue)
            {
                Debug.LogError("[Nanite][PagePool] Resident geometry cache exceeds the 32-bit buffer address space.");
                Dispose();
                return false;
            }
            residentVertexAllocator.Reset(dynamicResidentRanges ? (int)totalResidentVertices : 0);
            residentIndexAllocator.Reset(dynamicResidentRanges ? (int)totalResidentIndices : 0);
            residentVertexBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                Mathf.Max(1, (int)totalResidentVertices),
                ResidentVertexBytes);
            residentIndexBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                Mathf.Max(1, (int)totalResidentIndices),
                sizeof(uint));
            ResidentGeometryBytes =
                totalResidentVertices * ResidentVertexBytes + totalResidentIndices * sizeof(uint);
            int bitWordCount = Mathf.Max(1, (pages.Count + 31) / 32);
            for (int i = 0; i < pageTableBuffers.Length; i++)
            {
                pageTableBuffers[i] = new ComputeBuffer(
                    pages.Count,
                    Marshal.SizeOf<GpuPageTableEntry>(),
                    ComputeBufferType.Structured);
                pageDecodeBuffers[i] = new ComputeBuffer(
                    pages.Count,
                    decodeEntryBytes,
                    ComputeBufferType.Structured);
                residentPageTableBuffers[i] = new ComputeBuffer(
                    pages.Count,
                    residentEntryBytes,
                    ComputeBufferType.Structured);
                residencyBitsetBuffers[i] = new ComputeBuffer(bitWordCount, sizeof(uint));
            }
            transcodeTaskBuffer = new ComputeBuffer(
                Mathf.Max(1, pages.Count),
                Marshal.SizeOf<PageTranscodeTask>(),
                ComputeBufferType.Structured);
            requestBitsetBuffers[0] = new ComputeBuffer(bitWordCount, sizeof(uint));
            requestBitsetBuffers[1] = new ComputeBuffer(bitWordCount, sizeof(uint));
            pageTableCpu = new GpuPageTableEntry[pages.Count];
            pageDecodeCpu = new GpuPageDecodeEntry[pages.Count];
            residentPageTableCpu = new GpuResidentPageEntry[pages.Count];
            residencyBitsCpu = new uint[bitWordCount];
            requestBitsCpu = new uint[bitWordCount];
            freeSlots.Clear();
            for (int slotIndex = SlotCount - 1; slotIndex >= 0; slotIndex--)
                freeSlots.Push(slotIndex);

            for (int pageId = 0; pageId < pages.Count; pageId++)
            {
                CpuPageRecord record = pages[pageId];
                uint flags = record.root ? FlagRoot : 0u;
                if (record.page.BinaryStats.formatVersion == NanitePageBinaryCodec.CurrentVersion)
                    flags |= FlagBinaryV1;
                pageTableCpu[pageId] = new GpuPageTableEntry
                {
                    slotIndex = InvalidSlot,
                    byteAddress = InvalidSlot,
                    packedBytes = (uint)Mathf.Max(0, record.packedBytes),
                    flags = flags,
                    meshIndex = (uint)record.meshIndex,
                    localPageIndex = (uint)record.localPageIndex,
                    generation = generation,
                    reserved = 0u
                };
                pageDecodeCpu[pageId] = new GpuPageDecodeEntry
                {
                    byteAddress = InvalidSlot,
                    packedBytes = (uint)Mathf.Max(0, record.packedBytes),
                    vertexSectionAddress = InvalidSlot,
                    indexSectionAddress = InvalidSlot,
                    flags = 0u,
                    vertexCount = 0u,
                    indexCount = 0u,
                    vertexRecordBytes = 0u,
                    positionMin = Vector3.zero,
                    generation = generation,
                    positionExtent = Vector3.zero,
                    reserved = 0u
                };
                residentPageTableCpu[pageId] = new GpuResidentPageEntry
                {
                    vertexBase = record.residentVertexBase >= 0
                        ? (uint)record.residentVertexBase
                        : InvalidSlot,
                    indexBase = record.residentIndexBase >= 0
                        ? (uint)record.residentIndexBase
                        : InvalidSlot,
                    vertexCount = (uint)Mathf.Max(0, record.vertexCount),
                    indexCount = (uint)Mathf.Max(0, record.indexCount),
                    flags = 0u,
                    generation = generation,
                    reserved0 = 0u,
                    reserved1 = 0u
                };
            }

            for (int pageId = 0; pageId < pages.Count; pageId++)
            {
                if (!pages[pageId].root)
                    continue;
                if (!MakeResident(pageId, pin: true, out string error))
                {
                    Debug.LogError($"[Nanite][PagePool] Failed to pin root page {pageId}: {error}");
                    Dispose();
                    return false;
                }
            }

            UploadTables();
            signature = nextSignature;
            Debug.Log(
                $"[Nanite][PagePool] ready: pages={PageCount}, roots={RootPageCount}, " +
                $"resident={ResidentPageCount}, pinned={PinnedPageCount}, " +
                $"pool={PoolBytes / (1024f * 1024f):F2} MiB/{SlotCount} slots, " +
                $"residentCache={ResidentGeometryBytes / (1024f * 1024f):F2} MiB, " +
                $"rootPayload={ResidentBytes / 1024f:F1} KiB, slot={SlotBytes / 1024} KiB.");
            return true;
        }

        bool MakeResident(int pageId, bool pin, out string error)
        {
            error = null;
            if ((uint)pageId >= (uint)pages.Count)
            {
                error = "Page ID is outside the page table.";
                return false;
            }

            CpuPageRecord record = pages[pageId];
            if (record.resident)
            {
                if (pin && !record.pinned)
                {
                    record.pinned = true;
                    pages[pageId] = record;
                    PinnedPageCount++;
                    pageTableCpu[pageId].flags |= FlagPinned;
                }
                return true;
            }

            byte[] storageBlob = record.page != null && record.page.BinaryPayload != null
                ? record.page.BinaryPayload.bytes
                : null;
            if (storageBlob == null || storageBlob.Length == 0)
            {
                error = "Missing binary Page payload.";
                return false;
            }
            if (!NanitePageStorageCodec.TryUnpack(storageBlob, out byte[] blob, out error))
                return false;
            if (blob.Length > SlotBytes)
            {
                error = $"Packed Page is {blob.Length} bytes, larger than the {SlotBytes}-byte slot.";
                return false;
            }
            if (freeSlots.Count == 0)
            {
                error = "No free Page Pool slot (eviction is not active during root pinning).";
                return false;
            }

            int slotIndex = freeSlots.Peek();
            uint pageByteAddress = checked((uint)(slotIndex * SlotBytes));
            if (!TryBuildDecodeEntry(blob, pageByteAddress, generation, out GpuPageDecodeEntry decodeEntry, out error))
                return false;
            if (decodeEntry.vertexCount != (uint)record.vertexCount ||
                decodeEntry.indexCount != (uint)record.indexCount)
            {
                error =
                    $"NPG1 geometry count changed after cache allocation: " +
                    $"vertices={decodeEntry.vertexCount}/{record.vertexCount}, " +
                    $"indices={decodeEntry.indexCount}/{record.indexCount}.";
                return false;
            }

            int residentVertexBase = record.residentVertexBase;
            int residentIndexBase = record.residentIndexBase;
            if (dynamicResidentRanges)
            {
                if (!residentVertexAllocator.TryAllocate(record.vertexCount, out residentVertexBase))
                {
                    error =
                        $"No contiguous resident vertex range for {record.vertexCount} vertices " +
                        $"({residentVertexAllocator.FreeCount}/{residentVertexAllocator.Capacity} free).";
                    return false;
                }
                if (!residentIndexAllocator.TryAllocate(record.indexCount, out residentIndexBase))
                {
                    residentVertexAllocator.Free(residentVertexBase, record.vertexCount);
                    error =
                        $"No contiguous resident index range for {record.indexCount} indices " +
                        $"({residentIndexAllocator.FreeCount}/{residentIndexAllocator.Capacity} free).";
                    return false;
                }
            }
            freeSlots.Pop();
            int wordCount = (blob.Length + 3) / 4;
            var words = new uint[wordCount];
            Buffer.BlockCopy(blob, 0, words, 0, blob.Length);
            packedPoolBuffer.SetData(
                words,
                0,
                slotIndex * (SlotBytes / sizeof(uint)),
                wordCount);

            record.slotIndex = slotIndex;
            record.residentVertexBase = residentVertexBase;
            record.residentIndexBase = residentIndexBase;
            record.packedBytes = blob.Length;
            record.resident = true;
            record.residentGeometryReady = false;
            record.pinned = pin || record.pinned;
            record.lastTouchedFrame = record.pinned ? int.MaxValue : Time.frameCount;
            pages[pageId] = record;

            GpuPageTableEntry entry = pageTableCpu[pageId];
            entry.slotIndex = (uint)slotIndex;
            entry.byteAddress = (uint)(slotIndex * SlotBytes);
            entry.packedBytes = (uint)blob.Length;
            entry.flags |= FlagResident;
            if (record.pinned)
                entry.flags |= FlagPinned;
            pageTableCpu[pageId] = entry;
            pageDecodeCpu[pageId] = decodeEntry;
            GpuResidentPageEntry residentEntry = residentPageTableCpu[pageId];
            residentEntry.vertexBase = (uint)residentVertexBase;
            residentEntry.indexBase = (uint)residentIndexBase;
            residentEntry.vertexCount = (uint)Mathf.Max(0, record.vertexCount);
            residentEntry.indexCount = (uint)Mathf.Max(0, record.indexCount);
            residentEntry.flags &= ~FlagResidentGeometryReady;
            residentPageTableCpu[pageId] = residentEntry;
            if (!pendingTranscodePageIds.Contains(pageId))
                pendingTranscodePageIds.Add(pageId);
            residencyBitsCpu[pageId >> 5] |= 1u << (pageId & 31);
            ResidentPageCount++;
            ResidentBytes += blob.Length;
            if (record.pinned)
                PinnedPageCount++;
            return true;
        }

        /// <summary>
        /// Polls the GPU request mask without a synchronous readback. V1 payloads are currently
        /// TextAssets, so the callback uploads already-imported bytes; chunk IO replaces that
        /// source in the next streaming stage without changing the GPU table ABI.
        /// </summary>
        public void UpdateStreaming(
            int frameIndex,
            int readbackIntervalFrames = 3,
            int maxUploadsPerPoll = 8)
        {
            if (!IsReady)
                return;
            CollectRetiredAllocations(frameIndex);
            if (requestReadbackPending || pendingRequestBufferIndex == activeRequestBufferIndex)
                return;
            if (ResidentPageCount >= PageCount)
                return;
            if (frameIndex - lastRequestReadbackFrame < Mathf.Max(1, readbackIntervalFrames))
                return;

            requestReadbackPending = true;
            lastRequestReadbackFrame = frameIndex;
            uint capturedGeneration = generation;
            int uploadBudget = Mathf.Max(1, maxUploadsPerPoll);
            int readbackIndex = activeRequestBufferIndex;
            int nextWriteIndex = 1 - readbackIndex;
            // Clear the inactive mask before publishing it to subsequent cull dispatches.
            // The readback buffer is never cleared or rebound until its callback completes.
            requestBitsetBuffers[nextWriteIndex].SetData(requestBitsCpu);
            activeRequestBufferIndex = nextWriteIndex;
            pendingRequestBufferIndex = readbackIndex;
            AsyncGPUReadback.Request(requestBitsetBuffers[readbackIndex], request =>
            {
                if (capturedGeneration != generation || !IsReady)
                    return;

                requestReadbackPending = false;
                pendingRequestBufferIndex = -1;
                if (request.hasError)
                {
                    Debug.LogWarning("[Nanite][PagePool] GPU Page request readback failed.");
                    return;
                }

                var words = request.GetData<uint>();
                // Under a constrained pool the request mask also carries resident-page
                // touches. Update every touch before choosing an eviction victim so the
                // current working set cannot be displaced by an older request in this poll.
                for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
                {
                    uint word = words[wordIndex];
                    while (word != 0u)
                    {
                        int bitIndex = FirstSetBit(word);
                        int pageId = (wordIndex << 5) + bitIndex;
                        word &= word - 1u;
                        if ((uint)pageId < (uint)pages.Count && pages[pageId].resident)
                            Touch(pageId, frameIndex);
                    }
                }

                int uploads = 0;
                bool tableChanged = false;
                for (int wordIndex = 0; wordIndex < words.Length && uploads < uploadBudget; wordIndex++)
                {
                    uint word = words[wordIndex];
                    while (word != 0u && uploads < uploadBudget)
                    {
                        int bitIndex = FirstSetBit(word);
                        int pageId = (wordIndex << 5) + bitIndex;
                        word &= word - 1u;
                        if ((uint)pageId >= (uint)pages.Count || pages[pageId].resident)
                            continue;

                        if (!MakeResident(pageId, pin: false, out string error))
                        {
                            if (freeSlots.Count == 0)
                            {
                                if (TryRetireLruPage(frameIndex, out int retiredPageId))
                                {
                                    tableChanged = true;
                                    if (!loggedPoolFull)
                                    {
                                        loggedPoolFull = true;
                                        Debug.LogWarning(
                                            $"[Nanite][PagePool] pool pressure at {ResidentPageCount}/{SlotCount} pages; " +
                                            $"retired LRU page {retiredPageId}. Its slot is quarantined before reuse.");
                                    }
                                    // The retired slot deliberately cannot be reused in this
                                    // callback. A later poll will collect it after the safety
                                    // window and service a fresh GPU request.
                                    wordIndex = words.Length;
                                    break;
                                }

                                if (!loggedPoolFull)
                                {
                                    loggedPoolFull = true;
                                    Debug.LogWarning(
                                        $"[Nanite][PagePool] pool full at {ResidentPageCount}/{SlotCount} pages; " +
                                        "LRU fence-safe eviction is required before more requests can be served.");
                                }
                                wordIndex = words.Length;
                                break;
                            }

                            Debug.LogWarning(
                                $"[Nanite][PagePool] Page {pageId} request could not be uploaded: {error}");
                            continue;
                        }

                        uploads++;
                        tableChanged = true;
                    }
                }

                if (tableChanged)
                {
                    PublishTables();
                    if (residentTranscodeShader != null &&
                        !TranscodePendingResidentPages(out string transcodeError))
                    {
                        Debug.LogWarning(
                            $"[Nanite][PagePool] streamed Page transcode failed: {transcodeError}");
                    }
                }
                if (!loggedFirstStreamUpload && uploads > 0)
                {
                    loggedFirstStreamUpload = true;
                    Debug.Log(
                        $"[Nanite][PagePool] async requests active: uploaded={uploads}, " +
                        $"resident={ResidentPageCount}/{PageCount}, requestBuffers=double.");
                }
                if (!loggedFullyResident && ResidentPageCount >= PageCount)
                {
                    loggedFullyResident = true;
                    Debug.Log(
                        $"[Nanite][PagePool] streaming settled: resident={ResidentPageCount}/{PageCount}, " +
                        $"payload={ResidentBytes / (1024f * 1024f):F2} MiB, slots={SlotCount}.");
                }
            });
        }

        bool TryRetireLruPage(int frameIndex, out int pageId)
        {
            pageId = -1;
            int oldestFrame = int.MaxValue;
            for (int i = 0; i < pages.Count; i++)
            {
                CpuPageRecord candidate = pages[i];
                if (!candidate.resident || candidate.pinned)
                    continue;
                // Pages touched by this readback are the current working set. Until
                // residency-aware parent fallback supplies a priority signal, prefer
                // stability over cycling equally visible pages.
                if (candidate.lastTouchedFrame >= frameIndex)
                    continue;
                if (candidate.lastTouchedFrame >= oldestFrame)
                    continue;
                oldestFrame = candidate.lastTouchedFrame;
                pageId = i;
            }

            if (pageId < 0)
                return false;

            CpuPageRecord record = pages[pageId];
            int retiredSlot = record.slotIndex;
            int retiredVertexBase = record.residentVertexBase;
            int retiredIndexBase = record.residentIndexBase;
            record.slotIndex = -1;
            if (dynamicResidentRanges)
            {
                record.residentVertexBase = -1;
                record.residentIndexBase = -1;
            }
            record.resident = false;
            record.pinned = false;
            record.residentGeometryReady = false;
            pages[pageId] = record;

            GpuPageTableEntry entry = pageTableCpu[pageId];
            entry.slotIndex = InvalidSlot;
            entry.byteAddress = InvalidSlot;
            entry.flags &= ~(FlagResident | FlagPinned);
            pageTableCpu[pageId] = entry;
            GpuPageDecodeEntry decodeEntry = pageDecodeCpu[pageId];
            decodeEntry.byteAddress = InvalidSlot;
            decodeEntry.vertexSectionAddress = InvalidSlot;
            decodeEntry.indexSectionAddress = InvalidSlot;
            pageDecodeCpu[pageId] = decodeEntry;
            GpuResidentPageEntry residentEntry = residentPageTableCpu[pageId];
            if (dynamicResidentRanges)
            {
                residentEntry.vertexBase = InvalidSlot;
                residentEntry.indexBase = InvalidSlot;
            }
            residentEntry.flags &= ~FlagResidentGeometryReady;
            residentPageTableCpu[pageId] = residentEntry;
            pendingTranscodePageIds.Remove(pageId);
            residencyBitsCpu[pageId >> 5] &= ~(1u << (pageId & 31));
            ResidentPageCount = Mathf.Max(0, ResidentPageCount - 1);
            ResidentBytes = Mathf.Max(0, ResidentBytes - record.packedBytes);

            // PublishTables is called by the request callback before any of these ranges can
            // be recycled. A later end-of-frame fence proves that readers of the old table
            // generation have finished. Platforms without fences retain a conservative delay.
            retiredAllocations.Add(new RetiredAllocation
            {
                slotIndex = retiredSlot,
                vertexBase = dynamicResidentRanges ? retiredVertexBase : -1,
                vertexCount = dynamicResidentRanges ? record.vertexCount : 0,
                indexBase = dynamicResidentRanges ? retiredIndexBase : -1,
                indexCount = dynamicResidentRanges ? record.indexCount : 0,
                fallbackReusableFrame = frameIndex + 4,
                hasFence = false
            });
            return true;
        }

        void CollectRetiredAllocations(int frameIndex)
        {
            for (int i = retiredAllocations.Count - 1; i >= 0; i--)
            {
                RetiredAllocation retired = retiredAllocations[i];
                bool reusable = false;
                if (retired.hasFence)
                {
                    try
                    {
                        reusable = retired.fence.passed;
                    }
                    catch (InvalidOperationException)
                    {
                        reusable = frameIndex >= retired.fallbackReusableFrame;
                    }
                }
                else if (!SystemInfo.supportsGraphicsFence)
                {
                    reusable = frameIndex >= retired.fallbackReusableFrame;
                }
                if (!reusable)
                    continue;

                freeSlots.Push(retired.slotIndex);
                if (retired.vertexCount > 0)
                    residentVertexAllocator.Free(retired.vertexBase, retired.vertexCount);
                if (retired.indexCount > 0)
                    residentIndexAllocator.Free(retired.indexBase, retired.indexCount);
                retiredAllocations.RemoveAt(i);
            }
        }

        void UploadTables()
        {
            for (int i = 0; i < pageTableBuffers.Length; i++)
            {
                pageTableBuffers[i].SetData(pageTableCpu);
                pageDecodeBuffers[i].SetData(pageDecodeCpu);
                residentPageTableBuffers[i].SetData(residentPageTableCpu);
                residencyBitsetBuffers[i].SetData(residencyBitsCpu);
            }
            requestBitsetBuffers[0].SetData(requestBitsCpu);
            requestBitsetBuffers[1].SetData(requestBitsCpu);
        }

        void PublishTables()
        {
            int nextIndex = 1 - activeTableBufferIndex;
            pageTableBuffers[nextIndex].SetData(pageTableCpu);
            pageDecodeBuffers[nextIndex].SetData(pageDecodeCpu);
            residentPageTableBuffers[nextIndex].SetData(residentPageTableCpu);
            residencyBitsetBuffers[nextIndex].SetData(residencyBitsCpu);
            activeTableBufferIndex = nextIndex;
        }

        bool TranscodePendingResidentPages(out string error)
        {
            error = null;
            if (pendingTranscodePageIds.Count == 0)
                return true;
            if (residentTranscodeShader == null)
            {
                error = "NanitePageTranscode.compute is not assigned.";
                return false;
            }
            if (transcodeTaskBuffer == null || residentVertexBuffer == null || residentIndexBuffer == null)
            {
                error = "Resident geometry cache buffers are not initialized.";
                return false;
            }

            int vertexKernel;
            int indexKernel;
            try
            {
                vertexKernel = residentTranscodeShader.FindKernel("CSTranscodeVertices");
                indexKernel = residentTranscodeShader.FindKernel("CSTranscodeIndices");
            }
            catch (Exception exception)
            {
                error = $"Transcode kernels are unavailable: {exception.Message}";
                return false;
            }

            var tasks = new PageTranscodeTask[pendingTranscodePageIds.Count];
            int taskCount = 0;
            int maxVertexCount = 0;
            int maxIndexCount = 0;
            for (int i = 0; i < pendingTranscodePageIds.Count; i++)
            {
                int pageId = pendingTranscodePageIds[i];
                if ((uint)pageId >= (uint)pages.Count)
                    continue;
                CpuPageRecord record = pages[pageId];
                if (!record.resident || record.residentGeometryReady)
                    continue;
                tasks[taskCount++] = new PageTranscodeTask
                {
                    pageId = (uint)pageId,
                    vertexBase = (uint)record.residentVertexBase,
                    indexBase = (uint)record.residentIndexBase,
                    vertexCount = (uint)record.vertexCount,
                    indexCount = (uint)record.indexCount,
                    reserved0 = 0u,
                    reserved1 = 0u,
                    reserved2 = 0u
                };
                maxVertexCount = Mathf.Max(maxVertexCount, record.vertexCount);
                maxIndexCount = Mathf.Max(maxIndexCount, record.indexCount);
            }
            if (taskCount == 0)
            {
                pendingTranscodePageIds.Clear();
                return true;
            }

            transcodeTaskBuffer.SetData(tasks, 0, 0, taskCount);
            residentTranscodeShader.SetInt("_NanitePageTranscodeTaskCount", taskCount);
            residentTranscodeShader.SetBuffer(vertexKernel, "_NanitePackedPagePool", packedPoolBuffer);
            residentTranscodeShader.SetBuffer(vertexKernel, "_NanitePageDecodeTable", PageDecodeBuffer);
            residentTranscodeShader.SetBuffer(vertexKernel, "_NanitePageTranscodeTasks", transcodeTaskBuffer);
            residentTranscodeShader.SetBuffer(vertexKernel, "_NaniteResidentVertices", residentVertexBuffer);
            residentTranscodeShader.SetBuffer(indexKernel, "_NanitePackedPagePool", packedPoolBuffer);
            residentTranscodeShader.SetBuffer(indexKernel, "_NanitePageDecodeTable", PageDecodeBuffer);
            residentTranscodeShader.SetBuffer(indexKernel, "_NanitePageTranscodeTasks", transcodeTaskBuffer);
            residentTranscodeShader.SetBuffer(indexKernel, "_NaniteResidentIndices", residentIndexBuffer);

            const int threadsPerGroup = 64;
            if (maxVertexCount > 0)
            {
                residentTranscodeShader.Dispatch(
                    vertexKernel,
                    Mathf.Max(1, (maxVertexCount + threadsPerGroup - 1) / threadsPerGroup),
                    taskCount,
                    1);
            }
            if (maxIndexCount > 0)
            {
                residentTranscodeShader.Dispatch(
                    indexKernel,
                    Mathf.Max(1, (maxIndexCount + threadsPerGroup - 1) / threadsPerGroup),
                    taskCount,
                    1);
            }

            for (int i = 0; i < taskCount; i++)
            {
                int pageId = (int)tasks[i].pageId;
                CpuPageRecord record = pages[pageId];
                record.residentGeometryReady = true;
                pages[pageId] = record;
                GpuResidentPageEntry entry = residentPageTableCpu[pageId];
                entry.flags |= FlagResidentGeometryReady;
                residentPageTableCpu[pageId] = entry;
            }
            pendingTranscodePageIds.Clear();

            // Dispatch is submitted before the ready table is consumed by subsequent draws.
            // Publishing the inactive table preserves one coherent generation for all readers.
            PublishTables();
            return true;
        }

        static void ResolvePackedGeometryCounts(
            NaniteMeshPage page,
            out int vertexCount,
            out int indexCount)
        {
            vertexCount = 0;
            indexCount = 0;
            if (page == null)
                return;

            NanitePageBinaryStats stats = page.BinaryStats;
            if (stats.formatVersion != NanitePageBinaryCodec.CurrentVersion)
                return;
            NanitePageBinaryFlags flags = (NanitePageBinaryFlags)stats.flags;
            int vertexRecordBytes = (flags & NanitePageBinaryFlags.FloatUv) != 0 ? 24 : 20;
            int indexRecordBytes = (flags & NanitePageBinaryFlags.Index16) != 0 ? 2 : 4;
            if (stats.vertexBytes <= 0 || stats.vertexBytes % vertexRecordBytes != 0 ||
                stats.indexBytes <= 0 || stats.indexBytes % indexRecordBytes != 0)
                return;

            int encodedVertexCount = stats.vertexBytes / vertexRecordBytes;
            if (page.vertexCount > 0 && page.vertexCount != encodedVertexCount)
                return;
            vertexCount = encodedVertexCount;
            indexCount = stats.indexBytes / indexRecordBytes;
        }

        static bool TryBuildDecodeEntry(
            byte[] blob,
            uint pageByteAddress,
            uint pageGeneration,
            out GpuPageDecodeEntry entry,
            out string error)
        {
            entry = default;
            error = null;
            if (blob == null || blob.Length < NanitePageBinaryCodec.HeaderSize)
            {
                error = "NPG1 Page is smaller than its fixed header.";
                return false;
            }

            uint flags = ReadUInt32LittleEndian(blob, 8);
            uint vertexCount = ReadUInt32LittleEndian(blob, 20);
            uint indexCount = ReadUInt32LittleEndian(blob, 28);
            uint vertexSectionOffset = ReadUInt32LittleEndian(blob, 80);
            uint indexSectionOffset = ReadUInt32LittleEndian(blob, 88);
            uint vertexRecordBytes =
                (flags & (uint)NanitePageBinaryFlags.FloatUv) != 0u ? 24u : 20u;

            // TryUnpack has already performed the full NPG1/CRC/section validation. Keep these
            // local checks as a hard ABI guard in case the storage implementation changes.
            ulong vertexEnd = (ulong)vertexSectionOffset + (ulong)vertexCount * vertexRecordBytes;
            uint indexRecordBytes =
                (flags & (uint)NanitePageBinaryFlags.Index16) != 0u ? 2u : 4u;
            ulong indexEnd = (ulong)indexSectionOffset + (ulong)indexCount * indexRecordBytes;
            if (vertexEnd > (ulong)blob.Length || indexEnd > (ulong)blob.Length)
            {
                error = "NPG1 geometry section exceeds the Page payload.";
                return false;
            }

            entry = new GpuPageDecodeEntry
            {
                byteAddress = pageByteAddress,
                packedBytes = (uint)blob.Length,
                vertexSectionAddress = checked(pageByteAddress + vertexSectionOffset),
                indexSectionAddress = checked(pageByteAddress + indexSectionOffset),
                flags = flags,
                vertexCount = vertexCount,
                indexCount = indexCount,
                vertexRecordBytes = vertexRecordBytes,
                positionMin = new Vector3(
                    ReadSingleLittleEndian(blob, 56),
                    ReadSingleLittleEndian(blob, 60),
                    ReadSingleLittleEndian(blob, 64)),
                generation = pageGeneration,
                positionExtent = new Vector3(
                    ReadSingleLittleEndian(blob, 68),
                    ReadSingleLittleEndian(blob, 72),
                    ReadSingleLittleEndian(blob, 76)),
                reserved = 0u
            };
            return true;
        }

        static uint ReadUInt32LittleEndian(byte[] data, int offset) =>
            (uint)(data[offset] |
                   data[offset + 1] << 8 |
                   data[offset + 2] << 16 |
                   data[offset + 3] << 24);

        static float ReadSingleLittleEndian(byte[] data, int offset) =>
            BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32LittleEndian(data, offset)));

        static bool ResolveRootPage(NaniteMesh mesh, NaniteMeshPage page, int localPageIndex)
        {
            if (mesh.pageStreamingInfo != null)
            {
                for (int i = 0; i < mesh.pageStreamingInfo.Length; i++)
                {
                    NanitePageStreamingInfo info = mesh.pageStreamingInfo[i];
                    if (info.pageIndex == localPageIndex)
                        return info.IsRootPage;
                }
            }

            int maxMip = -1;
            if (page.clusterMip != null)
            {
                for (int i = 0; i < page.clusterMip.Length; i++)
                    maxMip = Mathf.Max(maxMip, page.clusterMip[i]);
            }
            return maxMip >= mesh.maxMipLevel;
        }

        static int ComputeSignature(IReadOnlyList<NaniteMesh> uniqueMeshes, int maxPoolMiB)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + Mathf.Max(1, maxPoolMiB);
                for (int i = 0; i < uniqueMeshes.Count; i++)
                {
                    NaniteMesh mesh = uniqueMeshes[i];
                    hash = hash * 31 + (mesh != null ? mesh.GetInstanceID() : 0);
                    int pageCount = mesh != null && mesh.pageArray != null ? mesh.pageArray.Length : 0;
                    hash = hash * 31 + pageCount;
                    for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
                    {
                        NaniteMeshPage page = mesh.pageArray[pageIndex];
                        hash = hash * 31 + (page != null ? page.GetInstanceID() : 0);
                        hash = hash * 31 +
                               (page != null && page.BinaryPayload != null
                                   ? page.BinaryPayload.GetInstanceID()
                                   : 0);
                    }
                }
                return hash;
            }
        }

        static int FirstSetBit(uint value)
        {
            int bit = 0;
            while ((value & 1u) == 0u)
            {
                value >>= 1;
                bit++;
            }
            return bit;
        }
    }
}
