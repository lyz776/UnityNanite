using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nanite
{
    /// <summary>
    /// Fixed-slot packed Page pool. The table and residency/request masks are the stable ABI
    /// shared by GPU traversal, raster streaming and future RT code. NPG1 payloads are
    /// transcoded once on residency into an aligned draw-time cache; raster never reparses
    /// Page headers in its triangle hot path.
    /// </summary>
    public sealed class NaniteGpuPagePool : IDisposable
    {
        // NVIDIA's streaming path unloads by age after traversal has stopped
        // touching a group. A non-zero grace period prevents a fixed camera from
        // cycling a working set larger than the physical cache every readback.
        const int EvictionGraceFrames = 30;
        public const int SlotBytes = 256 * 1024;
        public const int PageDecodeEntryBytes = 64;
        public const int PageTableEntryBytes = 32;
        public const int ResidentPageEntryBytes = 32;
        // Draw-time layout: position.xyz + uv.xy + normal.xyz + tangent.xyzw.
        // NPG1 quantization/oct decode happens once on residency, never per pixel.
        public const int ResidentVertexBytes = 48;
        public const uint InvalidSlot = 0xFFFFFFFFu;

        public const uint FlagResident = 1u << 0;
        public const uint FlagPinned = 1u << 1;
        public const uint FlagRoot = 1u << 2;
        public const uint FlagBinaryPayload = 1u << 3;
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

        readonly struct PageDecodeWork
        {
            public readonly int pageId;
            public readonly uint generation;
            public readonly uint priority;
            public readonly byte[] bulk;
            public readonly string filePath;
            public readonly long offset;
            public readonly int length;

            public PageDecodeWork(
                int pageId,
                uint generation,
                uint priority,
                byte[] bulk,
                long offset,
                int length)
            {
                this.pageId = pageId;
                this.generation = generation;
                this.priority = priority;
                this.bulk = bulk;
                this.filePath = null;
                this.offset = offset;
                this.length = length;
            }

            public PageDecodeWork(
                int pageId,
                uint generation,
                uint priority,
                string filePath,
                long offset,
                int length)
            {
                this.pageId = pageId;
                this.generation = generation;
                this.priority = priority;
                this.bulk = null;
                this.filePath = filePath;
                this.offset = offset;
                this.length = length;
            }

            public bool UsesFileRange => !string.IsNullOrEmpty(filePath);
        }

        readonly struct DecodedPage
        {
            public readonly int pageId;
            public readonly uint generation;
            public readonly uint priority;
            public readonly byte[] packedPage;
            public readonly string error;

            public DecodedPage(int pageId, uint generation, uint priority, byte[] packedPage, string error)
            {
                this.pageId = pageId;
                this.generation = generation;
                this.priority = priority;
                this.packedPage = packedPage;
                this.error = error;
            }
        }

        readonly struct PrioritizedPageRequest
        {
            public readonly int pageId;
            public readonly uint priority;

            public PrioritizedPageRequest(int pageId, uint priority)
            {
                this.pageId = pageId;
                this.priority = priority;
            }
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
            public uint lastRequestPriority;
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

        readonly List<CpuPageRecord> pages = new List<CpuPageRecord>(256);
        readonly Dictionary<PageKey, int> pageIdByKey = new Dictionary<PageKey, int>(256);
        readonly Dictionary<int, byte[]> bulkPayloadBytesByAssetId = new Dictionary<int, byte[]>(32);
        readonly Stack<int> freeSlots = new Stack<int>(256);
        readonly HashSet<int> desiredPageIds = new HashSet<int>();
        readonly List<RetiredAllocation> retiredAllocations = new List<RetiredAllocation>(32);

        GraphicsBuffer packedPoolBuffer;
        GraphicsBuffer residentVertexBuffer;
        GraphicsBuffer residentIndexBuffer;
        GraphicsBuffer residentTriangleSubMeshBuffer;
        readonly ComputeBuffer[] pageTableBuffers = new ComputeBuffer[2];
        readonly ComputeBuffer[] pageDecodeBuffers = new ComputeBuffer[2];
        readonly ComputeBuffer[] residentPageTableBuffers = new ComputeBuffer[2];
        readonly ComputeBuffer[] residencyBitsetBuffers = new ComputeBuffer[2];
        // RenderGraph can only import GraphicsBuffer.  These tables mirror the published
        // front metadata, not geometry; resident vertex/index storage remains shared directly.
        readonly GraphicsBuffer[] exportedPageTableBuffers = new GraphicsBuffer[2];
        readonly GraphicsBuffer[] exportedResidentPageTableBuffers = new GraphicsBuffer[2];
        readonly GraphicsBuffer[] exportedResidencyBitsetBuffers = new GraphicsBuffer[2];
        readonly ComputeBuffer[] requestBitsetBuffers = new ComputeBuffer[2];
        ComputeBuffer transcodeTaskBuffer;
        GpuPageTableEntry[] pageTableCpu = Array.Empty<GpuPageTableEntry>();
        GpuPageDecodeEntry[] pageDecodeCpu = Array.Empty<GpuPageDecodeEntry>();
        GpuResidentPageEntry[] residentPageTableCpu = Array.Empty<GpuResidentPageEntry>();
        readonly List<int> pendingTranscodePageIds = new List<int>(64);
        readonly Dictionary<int, uint> pendingDecodeGenerationByPageId =
            new Dictionary<int, uint>();
        readonly ConcurrentQueue<DecodedPage> decodedCompletionQueue =
            new ConcurrentQueue<DecodedPage>();
        readonly Queue<DecodedPage> decodedUploadQueue = new Queue<DecodedPage>(64);
        readonly HashSet<int>[] dirtyPageIdsByTable =
        {
            new HashSet<int>(),
            new HashSet<int>()
        };
        readonly HashSet<int>[] dirtyResidencyWordsByTable =
        {
            new HashSet<int>(),
            new HashSet<int>()
        };
        readonly List<int> dirtyRangeScratch = new List<int>(128);
        readonly uint[] packedUploadWords = new uint[SlotBytes / sizeof(uint)];
        PageTranscodeTask[] transcodeTaskCpu = Array.Empty<PageTranscodeTask>();
        uint[] residencyBitsCpu = Array.Empty<uint>();
        uint[] requestBitsCpu = Array.Empty<uint>();
        int signature;
        uint generation;
        ulong publishedTableEpoch;
        int publishedTableFrame = -1;
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
        int residentVerticesPerSlot;
        int residentIndicesPerSlot;
        long totalStorageBytesRead;
        int totalStorageReadOperations;
        int streamingFilePageCount;

        public int PageCount => pages.Count;
        public int SlotCount { get; private set; }
        public int ResidentPageCount { get; private set; }
        public int PinnedPageCount { get; private set; }
        public int RootPageCount { get; private set; }
        public int LastRequestedPageCount { get; private set; }
        public int LastQueuedPageCount { get; private set; }
        public uint LastMaxRequestPriority { get; private set; }
        public int TotalStreamedPageCount { get; private set; }
        public int TotalEvictedPageCount { get; private set; }
        public int TotalRecycledAllocationCount { get; private set; }
        public int RetiredAllocationCount => retiredAllocations.Count;
        public int ResidentBytes { get; private set; }
        public int PoolBytes => SlotCount * SlotBytes;
        public long ResidentGeometryBytes { get; private set; }
        public long TotalStorageBytesRead => Interlocked.Read(ref totalStorageBytesRead);
        public int TotalStorageReadOperations => Volatile.Read(ref totalStorageReadOperations);
        public int StreamingFilePageCount => streamingFilePageCount;
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

        /// <summary>
        /// Returns the stable front table only. The view is read-only and valid for consumers
        /// recorded into the current RenderGraph frame; page reuse remains protected by the
        /// pool's existing retirement fences and per-page generation checks.
        /// </summary>
        public bool TryGetResidentPageReadOnlyView(out NaniteResidentPageReadOnlyView view)
        {
            view = new NaniteResidentPageReadOnlyView(
                residentVertexBuffer,
                residentIndexBuffer,
                residentTriangleSubMeshBuffer,
                exportedPageTableBuffers[activeTableBufferIndex],
                exportedResidentPageTableBuffers[activeTableBufferIndex],
                exportedResidencyBitsetBuffers[activeTableBufferIndex],
                PageCount,
                ResidentPageCount,
                generation,
                publishedTableEpoch,
                publishedTableFrame);
            return IsReady && view.IsValid;
        }

        public bool TryGetMeshIndex(NaniteMesh mesh, out int meshIndex)
        {
            meshIndex = -1;
            if (mesh == null)
                return false;
            for (int pageId = 0; pageId < pages.Count; pageId++)
            {
                CpuPageRecord record = pages[pageId];
                if (record.mesh != mesh)
                    continue;
                meshIndex = record.meshIndex;
                return true;
            }
            return false;
        }

        public bool TryGetMeshPageRange(
            NaniteMesh mesh, out int firstPageId, out int pageCount, out int meshIndex)
        {
            firstPageId = -1;
            pageCount = 0;
            meshIndex = -1;
            if (mesh == null)
                return false;
            for (int pageId = 0; pageId < pages.Count; pageId++)
            {
                CpuPageRecord record = pages[pageId];
                if (record.mesh != mesh)
                    continue;
                if (firstPageId < 0)
                {
                    firstPageId = pageId;
                    meshIndex = record.meshIndex;
                }
                pageCount++;
            }
            return firstPageId >= 0 && pageCount > 0;
        }

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
        /// Fast admission for scenes whose complete virtual Page set fits in the pool.
        /// Constrained pools use the separate demand-streaming path: traversal requests the
        /// fine working set and remains on an atomically complete resident coarse producer.
        /// This method avoids request/readback churn when residency is trivially complete.
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
                if ((pageTableCpu[pageId].flags & FlagBinaryPayload) == 0u)
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

        /// <summary>
        /// Enables the residency-aware packed raster path without forcing the complete
        /// virtual Page set into memory. Root Pages are already pinned by Rebuild; this
        /// method validates the streamable format and transcodes only that resident root
        /// working set. Hierarchy traversal requests finer Pages and falls back to a
        /// resident ancestor until their transcode/table publication completes.
        /// </summary>
        public bool EnsureResidentWorkingSetForPackedRaster(
            ComputeShader transcodeShader,
            out string error)
        {
            error = null;
            if (!IsReady)
            {
                error = "Page Pool is not initialized.";
                return false;
            }
            if (transcodeShader == null)
            {
                error = "NanitePageTranscode.compute is not assigned.";
                return false;
            }

            for (int pageId = 0; pageId < pages.Count; pageId++)
            {
                if ((pageTableCpu[pageId].flags & FlagBinaryPayload) == 0u)
                {
                    error = $"Page {pageId} is not an NPG1/V1 payload.";
                    return false;
                }
            }

            residentTranscodeShader = transcodeShader;
            if (!TranscodePendingResidentPages(out error))
            {
                error = $"resident root geometry transcode failed: {error}";
                return false;
            }

            for (int pageId = 0; pageId < pages.Count; pageId++)
            {
                if (pages[pageId].root && !pages[pageId].residentGeometryReady)
                {
                    error = $"root Page {pageId} has no published resident geometry.";
                    return false;
                }
            }

            Debug.Log(
                $"[Nanite][PagePool] residency-aware packed raster ready: " +
                $"resident={ResidentPageCount}/{PageCount}, roots={RootPageCount}, " +
                $"pinned={PinnedPageCount}, streaming={(ResidentPageCount < PageCount ? "demand" : "settled")}, " +
                $"generation={generation}.");
            return true;
        }

        public void Touch(int pageId, int frameIndex, uint priority)
        {
            if ((uint)pageId >= (uint)pages.Count)
                return;
            CpuPageRecord record = pages[pageId];
            record.lastTouchedFrame = frameIndex;
            record.lastRequestPriority = priority;
            pages[pageId] = record;
        }

        public void Dispose()
        {
            packedPoolBuffer?.Dispose();
            residentVertexBuffer?.Dispose();
            residentIndexBuffer?.Dispose();
            residentTriangleSubMeshBuffer?.Dispose();
            transcodeTaskBuffer?.Release();
            for (int i = 0; i < pageTableBuffers.Length; i++)
            {
                pageTableBuffers[i]?.Release();
                pageDecodeBuffers[i]?.Release();
                residentPageTableBuffers[i]?.Release();
                residencyBitsetBuffers[i]?.Release();
                exportedPageTableBuffers[i]?.Release();
                exportedResidentPageTableBuffers[i]?.Release();
                exportedResidencyBitsetBuffers[i]?.Release();
                pageTableBuffers[i] = null;
                pageDecodeBuffers[i] = null;
                residentPageTableBuffers[i] = null;
                residencyBitsetBuffers[i] = null;
                exportedPageTableBuffers[i] = null;
                exportedResidentPageTableBuffers[i] = null;
                exportedResidencyBitsetBuffers[i] = null;
            }
            for (int i = 0; i < requestBitsetBuffers.Length; i++)
            {
                requestBitsetBuffers[i]?.Release();
                requestBitsetBuffers[i] = null;
            }
            packedPoolBuffer = null;
            residentVertexBuffer = null;
            residentIndexBuffer = null;
            residentTriangleSubMeshBuffer = null;
            transcodeTaskBuffer = null;
            pages.Clear();
            pageIdByKey.Clear();
            bulkPayloadBytesByAssetId.Clear();
            freeSlots.Clear();
            desiredPageIds.Clear();
            retiredAllocations.Clear();
            pageTableCpu = Array.Empty<GpuPageTableEntry>();
            pageDecodeCpu = Array.Empty<GpuPageDecodeEntry>();
            residentPageTableCpu = Array.Empty<GpuResidentPageEntry>();
            pendingTranscodePageIds.Clear();
            pendingDecodeGenerationByPageId.Clear();
            decodedUploadQueue.Clear();
            while (decodedCompletionQueue.TryDequeue(out _))
            {
            }
            transcodeTaskCpu = Array.Empty<PageTranscodeTask>();
            residencyBitsCpu = Array.Empty<uint>();
            requestBitsCpu = Array.Empty<uint>();
            signature = 0;
            generation = 0;
            publishedTableEpoch = 0;
            publishedTableFrame = -1;
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
            residentVerticesPerSlot = 0;
            residentIndicesPerSlot = 0;
            Interlocked.Exchange(ref totalStorageBytesRead, 0L);
            Interlocked.Exchange(ref totalStorageReadOperations, 0);
            streamingFilePageCount = 0;
            SlotCount = 0;
            ResidentPageCount = 0;
            PinnedPageCount = 0;
            RootPageCount = 0;
            LastRequestedPageCount = 0;
            LastQueuedPageCount = 0;
            LastMaxRequestPriority = 0u;
            TotalStreamedPageCount = 0;
            TotalEvictedPageCount = 0;
            TotalRecycledAllocationCount = 0;
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

                    if (page.HasStreamingPayload)
                        streamingFilePageCount++;

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
                            : page.BinaryPayloadSize,
                        residentVertexBase = -1,
                        residentIndexBase = -1,
                        vertexCount = vertexCount,
                        indexCount = indexCount,
                        root = root,
                        pinned = root,
                        resident = false,
                        residentGeometryReady = false,
                        lastTouchedFrame = root ? int.MaxValue : -1,
                        lastRequestPriority = root ? uint.MaxValue : 0u
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
            int pageTableEntryBytes = Marshal.SizeOf<GpuPageTableEntry>();
            int decodeEntryBytes = Marshal.SizeOf<GpuPageDecodeEntry>();
            int residentEntryBytes = Marshal.SizeOf<GpuResidentPageEntry>();
            if (pageTableEntryBytes != PageTableEntryBytes)
            {
                Debug.LogError(
                    $"[Nanite][PagePool] Page table ABI is {pageTableEntryBytes} bytes; " +
                    $"expected {PageTableEntryBytes}.");
                return false;
            }
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
                // Packed and decoded data share one physical Page-slot lifetime. A fixed
                // decoded stride per slot prevents variable-range fragmentation and makes
                // one retirement fence sufficient to recycle the complete Page address.
                for (int pageId = 0; pageId < pages.Count; pageId++)
                {
                    CpuPageRecord record = pages[pageId];
                    residentVerticesPerSlot = Mathf.Max(residentVerticesPerSlot, record.vertexCount);
                    residentIndicesPerSlot = Mathf.Max(residentIndicesPerSlot, record.indexCount);
                    record.residentVertexBase = -1;
                    record.residentIndexBase = -1;
                    pages[pageId] = record;
                }
                totalResidentVertices = (long)residentVerticesPerSlot * SlotCount;
                totalResidentIndices = (long)residentIndicesPerSlot * SlotCount;
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
            residentVertexBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                Mathf.Max(1, (int)totalResidentVertices),
                ResidentVertexBytes);
            residentIndexBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                Mathf.Max(1, (int)totalResidentIndices),
                sizeof(uint));
            residentTriangleSubMeshBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                Mathf.Max(1, (int)totalResidentIndices / 3),
                sizeof(uint)) { name = "Nanite Resident Triangle SubMeshes" };
            ResidentGeometryBytes =
                totalResidentVertices * ResidentVertexBytes + totalResidentIndices * sizeof(uint);
            int bitWordCount = Mathf.Max(1, (pages.Count + 31) / 32);
            for (int i = 0; i < pageTableBuffers.Length; i++)
            {
                pageTableBuffers[i] = new ComputeBuffer(
                    pages.Count,
                    pageTableEntryBytes,
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
                exportedPageTableBuffers[i] = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, pages.Count, pageTableEntryBytes)
                {
                    name = $"Nanite Published Page Table {i}"
                };
                exportedResidentPageTableBuffers[i] = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, pages.Count, residentEntryBytes)
                {
                    name = $"Nanite Published Resident Page Table {i}"
                };
                exportedResidencyBitsetBuffers[i] = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, bitWordCount, sizeof(uint))
                {
                    name = $"Nanite Published Residency Bits {i}"
                };
            }
            transcodeTaskBuffer = new ComputeBuffer(
                Mathf.Max(1, pages.Count),
                Marshal.SizeOf<PageTranscodeTask>(),
                ComputeBufferType.Structured);
            transcodeTaskCpu = new PageTranscodeTask[Mathf.Max(1, pages.Count)];
            // One uint per virtual Page stores the maximum projected-error priority
            // observed by traversal.  It remains a single UAV (unlike a separate
            // priority buffer) and avoids page-id-order streaming under pressure.
            int requestEntryCount = Mathf.Max(1, pages.Count);
            requestBitsetBuffers[0] = new ComputeBuffer(requestEntryCount, sizeof(uint));
            requestBitsetBuffers[1] = new ComputeBuffer(requestEntryCount, sizeof(uint));
            pageTableCpu = new GpuPageTableEntry[pages.Count];
            pageDecodeCpu = new GpuPageDecodeEntry[pages.Count];
            residentPageTableCpu = new GpuResidentPageEntry[pages.Count];
            residencyBitsCpu = new uint[bitWordCount];
            requestBitsCpu = new uint[requestEntryCount];
            freeSlots.Clear();
            for (int slotIndex = SlotCount - 1; slotIndex >= 0; slotIndex--)
                freeSlots.Push(slotIndex);

            for (int pageId = 0; pageId < pages.Count; pageId++)
            {
                CpuPageRecord record = pages[pageId];
                uint flags = record.root ? FlagRoot : 0u;
                if (record.page.BinaryStats.formatVersion >= 1 &&
                    record.page.BinaryStats.formatVersion <= NanitePageBinaryCodec.CurrentVersion)
                    flags |= FlagBinaryPayload;
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
                $"rootPayload={ResidentBytes / 1024f:F1} KiB, slot={SlotBytes / 1024} KiB, " +
                $"fileRanges={streamingFilePageCount}/{PageCount}, io={TotalStorageBytesRead / 1024f:F1} KiB/{TotalStorageReadOperations} reads.");
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
                    MarkPageDirty(pageId);
                }
                return true;
            }

            if (!TryGetPackedPageBlob(record.page, out byte[] blob, out error))
                return false;
            return MakeResidentFromPackedPage(pageId, pin, blob, out error);
        }

        bool MakeResidentFromPackedPage(
            int pageId,
            bool pin,
            byte[] blob,
            out string error)
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
                    MarkPageDirty(pageId);
                }
                return true;
            }
            if (blob == null || blob.Length == 0)
            {
                error = "Packed Page is empty.";
                return false;
            }
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
                residentVertexBase = checked(slotIndex * residentVerticesPerSlot);
                residentIndexBase = checked(slotIndex * residentIndicesPerSlot);
            }
            freeSlots.Pop();
            int wordCount = (blob.Length + 3) / 4;
            packedUploadWords[wordCount - 1] = 0u;
            Buffer.BlockCopy(blob, 0, packedUploadWords, 0, blob.Length);
            packedPoolBuffer.SetData(
                packedUploadWords,
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
            UploadResidentTriangleSubMeshes(record);

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
            MarkPageDirty(pageId);
            MarkResidencyWordDirty(pageId >> 5);
            ResidentPageCount++;
            ResidentBytes += blob.Length;
            if (record.pinned)
                PinnedPageCount++;
            return true;
        }

        void UploadResidentTriangleSubMeshes(CpuPageRecord record)
        {
            if (residentTriangleSubMeshBuffer == null || record.page == null ||
                record.residentIndexBase < 0 || record.indexCount < 3)
                return;
            int triangleCount = record.indexCount / 3;
            var subMeshes = new uint[triangleCount];
            NaniteCluster[] clusters = record.page.clusterArray;
            if (clusters != null)
            {
                for (int clusterIndex = 0; clusterIndex < clusters.Length; clusterIndex++)
                {
                    NaniteCluster cluster = clusters[clusterIndex];
                    int first = Mathf.Clamp(cluster.indiceIndex / 3, 0, triangleCount);
                    int last = Mathf.Clamp(
                        (cluster.indiceIndex + cluster.indiceCount + 2) / 3,
                        first, triangleCount);
                    uint subMesh = (uint)Mathf.Max(0, cluster.subMeshId);
                    for (int triangle = first; triangle < last; triangle++)
                        subMeshes[triangle] = subMesh;
                }
            }
            residentTriangleSubMeshBuffer.SetData(
                subMeshes, 0, record.residentIndexBase / 3, triangleCount);
        }

        bool TryGetPackedPageBlob(
            NaniteMeshPage page,
            out byte[] packedPage,
            out string error)
        {
            packedPage = null;
            if (page == null || !page.HasBinaryPayload)
            {
                error = "Missing binary Page payload.";
                return false;
            }

            if (page.HasStreamingPayload)
            {
                if (!page.TryGetStoragePayload(out byte[] storagePayload, out error))
                    return false;
                Interlocked.Add(ref totalStorageBytesRead, storagePayload.Length);
                Interlocked.Increment(ref totalStorageReadOperations);
                return NanitePageStorageCodec.TryUnpack(
                    storagePayload,
                    0,
                    storagePayload.Length,
                    out packedPage,
                    out error);
            }

            TextAsset payload = page.BinaryPayload;
            int assetId = payload.GetInstanceID();
            if (!bulkPayloadBytesByAssetId.TryGetValue(assetId, out byte[] bulk) || bulk == null)
            {
                bulk = payload.bytes;
                bulkPayloadBytesByAssetId[assetId] = bulk;
            }
            if (!page.TryResolveStorageRange(bulk, out int offset, out int size, out error))
                return false;
            return NanitePageStorageCodec.TryUnpack(
                bulk,
                offset,
                size,
                out packedPage,
                out error);
        }

        /// <summary>
        /// Polls the GPU request mask without a synchronous readback. V1 payloads are currently
        /// New payloads are read as exact StreamingAssets file ranges on a worker; legacy
        /// TextAssets remain a compatibility source without changing the GPU table ABI.
        /// </summary>
        public void UpdateStreaming(
            int frameIndex,
            int readbackIntervalFrames = 3,
            int maxUploadsPerPoll = 8,
            int maxUploadBytesPerFrame = 2 * 1024 * 1024)
        {
            if (!IsReady)
                return;
            CollectRetiredAllocations(frameIndex);
            DrainDecodedPages(
                frameIndex,
                Mathf.Max(1, maxUploadsPerPoll),
                Mathf.Max(SlotBytes, maxUploadBytesPerFrame));
            if (requestReadbackPending || pendingRequestBufferIndex == activeRequestBufferIndex)
                return;
            if (ResidentPageCount >= PageCount && pendingDecodeGenerationByPageId.Count == 0)
                return;
            if (frameIndex - lastRequestReadbackFrame < Mathf.Max(1, readbackIntervalFrames))
                return;

            requestReadbackPending = true;
            lastRequestReadbackFrame = frameIndex;
            uint capturedGeneration = generation;
            int decodeBudget = Mathf.Max(1, maxUploadsPerPoll) * 4;
            int outstandingDecodeBudget = Mathf.Max(32, decodeBudget * 4);
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

                var priorities = request.GetData<uint>();
                var demanded = new List<PrioritizedPageRequest>(Mathf.Min(priorities.Length, 256));
                uint maxPriority = 0u;
                int requestCount = Mathf.Min(priorities.Length, pages.Count);
                for (int pageId = 0; pageId < requestCount; pageId++)
                {
                    uint priority = priorities[pageId];
                    if (priority == 0u)
                        continue;
                    maxPriority = Math.Max(maxPriority, priority);
                    if (!pages[pageId].pinned)
                        demanded.Add(new PrioritizedPageRequest(pageId, priority));
                }
                demanded.Sort((a, b) =>
                {
                    int byPriority = b.priority.CompareTo(a.priority);
                    return byPriority != 0 ? byPriority : a.pageId.CompareTo(b.pageId);
                });

                // Keep the previous desired set while it remains relevant, then
                // admit a challenger only when it is at least 25% more important
                // than the weakest member. A raw top-N changes around the cutoff
                // on almost every camera step and turns streaming into flicker.
                var previousDesired = new List<int>(desiredPageIds);
                desiredPageIds.Clear();
                int desiredBudget = Mathf.Max(0, SlotCount - PinnedPageCount);
                for (int index = 0; index < previousDesired.Count && desiredPageIds.Count < desiredBudget; index++)
                {
                    int pageId = previousDesired[index];
                    if ((uint)pageId < (uint)requestCount && priorities[pageId] > 0u && !pages[pageId].pinned)
                        desiredPageIds.Add(pageId);
                }
                for (int demandIndex = 0;
                    demandIndex < demanded.Count && desiredPageIds.Count < desiredBudget;
                    demandIndex++)
                    desiredPageIds.Add(demanded[demandIndex].pageId);
                if (desiredPageIds.Count >= desiredBudget && desiredBudget > 0)
                {
                    for (int demandIndex = 0; demandIndex < demanded.Count; demandIndex++)
                    {
                        PrioritizedPageRequest demand = demanded[demandIndex];
                        if (desiredPageIds.Contains(demand.pageId))
                            continue;
                        int weakestPageId = -1;
                        uint weakestPriority = uint.MaxValue;
                        foreach (int selectedPageId in desiredPageIds)
                        {
                            uint selectedPriority = priorities[selectedPageId];
                            if (selectedPriority < weakestPriority ||
                                (selectedPriority == weakestPriority && selectedPageId > weakestPageId))
                            {
                                weakestPageId = selectedPageId;
                                weakestPriority = selectedPriority;
                            }
                        }
                        if ((ulong)demand.priority * 4ul <= (ulong)weakestPriority * 5ul)
                            break;
                        desiredPageIds.Remove(weakestPageId);
                        desiredPageIds.Add(demand.pageId);
                    }
                }

                var requested = new List<PrioritizedPageRequest>(Mathf.Min(desiredBudget, demanded.Count));
                for (int demandIndex = 0; demandIndex < demanded.Count; demandIndex++)
                {
                    PrioritizedPageRequest demand = demanded[demandIndex];
                    if (!desiredPageIds.Contains(demand.pageId))
                        continue;
                    if (pages[demand.pageId].resident)
                        Touch(demand.pageId, frameIndex, demand.priority);
                    else
                        requested.Add(demand);
                }
                int forcedStaleFrame = frameIndex - EvictionGraceFrames - 1;
                for (int pageId = 0; pageId < pages.Count; pageId++)
                {
                    CpuPageRecord record = pages[pageId];
                    if (!record.resident || record.pinned || desiredPageIds.Contains(pageId))
                        continue;
                    record.lastRequestPriority = 0u;
                    record.lastTouchedFrame = Math.Min(record.lastTouchedFrame, forcedStaleFrame);
                    pages[pageId] = record;
                }

                LastRequestedPageCount = requested.Count;
                LastMaxRequestPriority = maxPriority;
                var decodeWork = new List<PageDecodeWork>(decodeBudget);
                for (int requestIndex = 0;
                    requestIndex < requested.Count &&
                    decodeWork.Count < decodeBudget &&
                    pendingDecodeGenerationByPageId.Count < outstandingDecodeBudget;
                    requestIndex++)
                {
                    int pageId = requested[requestIndex].pageId;
                    if (pages[pageId].resident ||
                        (pendingDecodeGenerationByPageId.TryGetValue(pageId, out uint pendingGeneration) &&
                         pendingGeneration == capturedGeneration))
                        continue;

                    if (!TryCreateDecodeWork(
                        pageId,
                        capturedGeneration,
                        requested[requestIndex].priority,
                        out PageDecodeWork work,
                        out string error))
                    {
                        Debug.LogWarning(
                            $"[Nanite][PagePool] Page {pageId} request could not be queued: {error}");
                        continue;
                    }
                    pendingDecodeGenerationByPageId[pageId] = capturedGeneration;
                    decodeWork.Add(work);
                }
                LastQueuedPageCount = decodeWork.Count;
                if (decodeWork.Count > 0)
                    ScheduleDecodeBatch(decodeWork.ToArray());
            });
        }

        bool TryCreateDecodeWork(
            int pageId,
            uint workGeneration,
            uint priority,
            out PageDecodeWork work,
            out string error)
        {
            work = default;
            error = null;
            if ((uint)pageId >= (uint)pages.Count)
            {
                error = "Page ID is outside the page table.";
                return false;
            }

            NaniteMeshPage page = pages[pageId].page;
            if (page == null || !page.HasBinaryPayload)
            {
                error = "Missing binary Page payload.";
                return false;
            }
            if (page.HasStreamingPayload)
            {
                if (!page.TryResolveStreamingFilePath(out string filePath, out error))
                    return false;
                int streamOffset = page.BinaryPayloadOffset;
                int streamSize = page.BinaryPayloadSize;
                if (streamOffset < 0 || streamSize <= 0)
                {
                    error = $"Invalid StreamingAssets Page range [{streamOffset}, {(long)streamOffset + streamSize}).";
                    return false;
                }
                try
                {
                    long fileLength = new FileInfo(filePath).Length;
                    if ((long)streamOffset + streamSize > fileLength)
                    {
                        error = $"Page range [{streamOffset}, {(long)streamOffset + streamSize}) exceeds '{filePath}' ({fileLength} bytes).";
                        return false;
                    }
                }
                catch (Exception exception)
                {
                    error = $"Could not inspect Page stream '{filePath}': {exception.Message}";
                    return false;
                }
                work = new PageDecodeWork(
                    pageId,
                    workGeneration,
                    priority,
                    filePath,
                    streamOffset,
                    streamSize);
                return true;
            }

            TextAsset payload = page.BinaryPayload;
            int assetId = payload.GetInstanceID();
            if (!bulkPayloadBytesByAssetId.TryGetValue(assetId, out byte[] bulk) || bulk == null)
            {
                // Unity objects are touched only here on the main thread. The worker receives
                // an immutable managed byte array plus a validated range.
                bulk = payload.bytes;
                bulkPayloadBytesByAssetId[assetId] = bulk;
            }
            if (!page.TryResolveStorageRange(bulk, out int offset, out int size, out error))
                return false;
            work = new PageDecodeWork(pageId, workGeneration, priority, bulk, offset, size);
            return true;
        }

        void ScheduleDecodeBatch(PageDecodeWork[] batch)
        {
            if (batch == null || batch.Length == 0)
                return;
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < batch.Length; i++)
                {
                    PageDecodeWork work = batch[i];
                    bool ok;
                    byte[] packedPage;
                    string error;
                    try
                    {
                        byte[] storage = work.bulk;
                        int storageOffset = checked((int)work.offset);
                        if (work.UsesFileRange)
                        {
                            storage = await ReadFileRangeAsync(
                                work.filePath,
                                work.offset,
                                work.length).ConfigureAwait(false);
                            storageOffset = 0;
                            Interlocked.Add(ref totalStorageBytesRead, work.length);
                            Interlocked.Increment(ref totalStorageReadOperations);
                        }
                        ok = NanitePageStorageCodec.TryUnpack(
                            storage,
                            storageOffset,
                            work.length,
                            out packedPage,
                            out error);
                    }
                    catch (Exception exception)
                    {
                        ok = false;
                        packedPage = null;
                        error = exception.Message;
                    }
                    decodedCompletionQueue.Enqueue(new DecodedPage(
                        work.pageId,
                        work.generation,
                        work.priority,
                        ok ? packedPage : null,
                        ok ? null : error));
                }
            });
        }

        static async Task<byte[]> ReadFileRangeAsync(string filePath, long offset, int length)
        {
            if (string.IsNullOrEmpty(filePath) || offset < 0 || length <= 0)
                throw new ArgumentOutOfRangeException(nameof(length), "Page file range is invalid.");

            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (offset + length > stream.Length)
                throw new EndOfStreamException(
                    $"Page range [{offset}, {offset + length}) exceeds '{filePath}' ({stream.Length} bytes).");

            stream.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[length];
            int read = 0;
            while (read < length)
            {
                int count = await stream.ReadAsync(bytes, read, length - read).ConfigureAwait(false);
                if (count <= 0)
                    throw new EndOfStreamException(
                        $"Unexpected EOF in '{filePath}' after {read}/{length} bytes.");
                read += count;
            }
            return bytes;
        }

        void DrainDecodedPages(int frameIndex, int pageBudget, int byteBudget)
        {
            while (decodedCompletionQueue.TryDequeue(out DecodedPage completed))
                decodedUploadQueue.Enqueue(completed);

            int uploads = 0;
            int uploadedBytes = 0;
            bool tableChanged = false;
            while (decodedUploadQueue.Count > 0 && uploads < pageBudget)
            {
                DecodedPage decoded = decodedUploadQueue.Peek();
                if (decoded.generation != generation || !IsReady)
                {
                    decodedUploadQueue.Dequeue();
                    RemovePendingDecode(decoded.pageId, decoded.generation);
                    continue;
                }
                if (RequiresEviction && !desiredPageIds.Contains(decoded.pageId))
                {
                    decodedUploadQueue.Dequeue();
                    RemovePendingDecode(decoded.pageId, decoded.generation);
                    continue;
                }
                if (!string.IsNullOrEmpty(decoded.error) || decoded.packedPage == null)
                {
                    decodedUploadQueue.Dequeue();
                    RemovePendingDecode(decoded.pageId, decoded.generation);
                    Debug.LogWarning(
                        $"[Nanite][PagePool] Page {decoded.pageId} worker decode failed: {decoded.error}");
                    continue;
                }
                if (uploadedBytes > 0 && uploadedBytes + decoded.packedPage.Length > byteBudget)
                    break;
                if (pages[decoded.pageId].resident)
                {
                    decodedUploadQueue.Dequeue();
                    RemovePendingDecode(decoded.pageId, decoded.generation);
                    continue;
                }
                if (freeSlots.Count == 0)
                {
                    if (TryRetireLruPage(
                        frameIndex,
                        decoded.priority,
                        out int retiredPageId))
                    {
                        tableChanged = true;
                        if (!loggedPoolFull)
                        {
                            loggedPoolFull = true;
                            Debug.LogWarning(
                                $"[Nanite][PagePool] pool pressure at {ResidentPageCount}/{SlotCount} pages; " +
                                $"retired LRU page {retiredPageId}. Its slot remains quarantined until its fence passes.");
                        }
                    }
                    else if (!loggedPoolFull)
                    {
                        loggedPoolFull = true;
                        Debug.LogWarning(
                            $"[Nanite][PagePool] pool full at {ResidentPageCount}/{SlotCount} pages; " +
                            "waiting for a fence-safe slot before publishing decoded requests.");
                    }
                    break;
                }

                if (!MakeResidentFromPackedPage(
                    decoded.pageId,
                    pin: false,
                    decoded.packedPage,
                    out string error))
                {
                    decodedUploadQueue.Dequeue();
                    RemovePendingDecode(decoded.pageId, decoded.generation);
                    Debug.LogWarning(
                        $"[Nanite][PagePool] Page {decoded.pageId} upload failed: {error}");
                    continue;
                }

                decodedUploadQueue.Dequeue();
                RemovePendingDecode(decoded.pageId, decoded.generation);
                uploads++;
                TotalStreamedPageCount++;
                CpuPageRecord uploadedRecord = pages[decoded.pageId];
                uploadedRecord.lastRequestPriority = decoded.priority;
                pages[decoded.pageId] = uploadedRecord;
                uploadedBytes += decoded.packedPage.Length;
                tableChanged = true;
            }

            if (tableChanged)
            {
                // The first publication exposes packed bytes and decode metadata to the GPU
                // transcode. The second publication performed by the transcode exposes only
                // entries whose resident geometry cache is ready.
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
                    $"[Nanite][PagePool] staged requests active: workerDecode=true, uploaded={uploads}, " +
                    $"bytes={uploadedBytes}, resident={ResidentPageCount}/{PageCount}, requestBuffers=double.");
            }
            if (!loggedFullyResident && ResidentPageCount >= PageCount)
            {
                loggedFullyResident = true;
                Debug.Log(
                    $"[Nanite][PagePool] streaming settled: resident={ResidentPageCount}/{PageCount}, " +
                    $"payload={ResidentBytes / (1024f * 1024f):F2} MiB, slots={SlotCount}.");
            }
        }

        void RemovePendingDecode(int pageId, uint workGeneration)
        {
            if (pendingDecodeGenerationByPageId.TryGetValue(pageId, out uint pendingGeneration) &&
                pendingGeneration == workGeneration)
            {
                pendingDecodeGenerationByPageId.Remove(pageId);
            }
        }

        bool TryRetireLruPage(
            int frameIndex,
            uint incomingPriority,
            out int pageId)
        {
            pageId = -1;
            int oldestFrame = int.MaxValue;
            uint lowestPriority = uint.MaxValue;
            for (int i = 0; i < pages.Count; i++)
            {
                CpuPageRecord candidate = pages[i];
                if (!candidate.resident || candidate.pinned)
                    continue;
                // Only pages absent from traversal for a sustained interval are
                // evictable. Missing higher-detail pages continue to render through
                // their complete resident ancestor instead of causing cache churn.
                bool oldEnough = candidate.lastTouchedFrame + EvictionGraceFrames < frameIndex;
                // Active pages are replaced only for a material priority gain.
                // Without hysteresis, continuously changing projected error can
                // exchange equal-value pages every readback and create visible
                // residency flicker. Stale pages still follow the age fallback.
                bool preferredReplacement =
                    (ulong)incomingPriority * 4ul >
                    (ulong)candidate.lastRequestPriority * 5ul;
                if (!oldEnough && !preferredReplacement)
                    continue;
                if (candidate.lastRequestPriority > lowestPriority)
                    continue;
                if (candidate.lastRequestPriority == lowestPriority &&
                    candidate.lastTouchedFrame > oldestFrame)
                    continue;
                if (candidate.lastRequestPriority == lowestPriority &&
                    candidate.lastTouchedFrame == oldestFrame && i < pageId)
                    continue;
                lowestPriority = candidate.lastRequestPriority;
                oldestFrame = candidate.lastTouchedFrame;
                pageId = i;
            }

            if (pageId < 0)
                return false;

            CpuPageRecord record = pages[pageId];
            int retiredSlot = record.slotIndex;
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
            MarkPageDirty(pageId);
            MarkResidencyWordDirty(pageId >> 5);
            ResidentPageCount = Mathf.Max(0, ResidentPageCount - 1);
            TotalEvictedPageCount++;
            ResidentBytes = Mathf.Max(0, ResidentBytes - record.packedBytes);

            // PublishTables is called by the request callback before any of these ranges can
            // be recycled. A later end-of-frame fence proves that readers of the old table
            // generation have finished. Platforms without fences retain a conservative delay.
            retiredAllocations.Add(new RetiredAllocation
            {
                slotIndex = retiredSlot,
                vertexBase = -1,
                vertexCount = 0,
                indexBase = -1,
                indexCount = 0,
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
                retiredAllocations.RemoveAt(i);
                TotalRecycledAllocationCount++;
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
                exportedPageTableBuffers[i].SetData(pageTableCpu);
                exportedResidentPageTableBuffers[i].SetData(residentPageTableCpu);
                exportedResidencyBitsetBuffers[i].SetData(residencyBitsCpu);
            }
            requestBitsetBuffers[0].SetData(requestBitsCpu);
            requestBitsetBuffers[1].SetData(requestBitsCpu);
            for (int i = 0; i < 2; i++)
            {
                dirtyPageIdsByTable[i].Clear();
                dirtyResidencyWordsByTable[i].Clear();
            }
            publishedTableEpoch++;
            publishedTableFrame = Time.frameCount;
        }

        void PublishTables()
        {
            int nextIndex = 1 - activeTableBufferIndex;
            HashSet<int> dirtyPages = dirtyPageIdsByTable[nextIndex];
            HashSet<int> dirtyWords = dirtyResidencyWordsByTable[nextIndex];
            if (dirtyPages.Count == 0 && dirtyWords.Count == 0)
                return;

            UploadDirtyRanges(pageTableBuffers[nextIndex], pageTableCpu, dirtyPages);
            UploadDirtyRanges(pageDecodeBuffers[nextIndex], pageDecodeCpu, dirtyPages);
            UploadDirtyRanges(residentPageTableBuffers[nextIndex], residentPageTableCpu, dirtyPages);
            UploadDirtyRanges(residencyBitsetBuffers[nextIndex], residencyBitsCpu, dirtyWords);
            UploadDirtyRanges(exportedPageTableBuffers[nextIndex], pageTableCpu, dirtyPages);
            UploadDirtyRanges(
                exportedResidentPageTableBuffers[nextIndex], residentPageTableCpu, dirtyPages);
            UploadDirtyRanges(exportedResidencyBitsetBuffers[nextIndex], residencyBitsCpu, dirtyWords);
            dirtyPages.Clear();
            dirtyWords.Clear();
            activeTableBufferIndex = nextIndex;
            publishedTableEpoch++;
            publishedTableFrame = Time.frameCount;
        }

        void MarkPageDirty(int pageId)
        {
            if ((uint)pageId >= (uint)pageTableCpu.Length)
                return;
            dirtyPageIdsByTable[0].Add(pageId);
            dirtyPageIdsByTable[1].Add(pageId);
        }

        void MarkResidencyWordDirty(int wordIndex)
        {
            if ((uint)wordIndex >= (uint)residencyBitsCpu.Length)
                return;
            dirtyResidencyWordsByTable[0].Add(wordIndex);
            dirtyResidencyWordsByTable[1].Add(wordIndex);
        }

        void UploadDirtyRanges<T>(ComputeBuffer destination, T[] source, HashSet<int> dirty)
            where T : struct
        {
            if (destination == null || source == null || dirty == null || dirty.Count == 0)
                return;

            dirtyRangeScratch.Clear();
            foreach (int index in dirty)
            {
                if ((uint)index < (uint)source.Length)
                    dirtyRangeScratch.Add(index);
            }
            if (dirtyRangeScratch.Count == 0)
                return;
            dirtyRangeScratch.Sort();

            int runStart = dirtyRangeScratch[0];
            int runEnd = runStart + 1;
            for (int i = 1; i <= dirtyRangeScratch.Count; i++)
            {
                if (i < dirtyRangeScratch.Count && dirtyRangeScratch[i] == runEnd)
                {
                    runEnd++;
                    continue;
                }
                destination.SetData(source, runStart, runStart, runEnd - runStart);
                if (i < dirtyRangeScratch.Count)
                {
                    runStart = dirtyRangeScratch[i];
                    runEnd = runStart + 1;
                }
            }
        }

        void UploadDirtyRanges<T>(GraphicsBuffer destination, T[] source, HashSet<int> dirty)
            where T : struct
        {
            if (destination == null || source == null || dirty == null || dirty.Count == 0)
                return;
            dirtyRangeScratch.Clear();
            foreach (int index in dirty)
                if ((uint)index < (uint)source.Length)
                    dirtyRangeScratch.Add(index);
            if (dirtyRangeScratch.Count == 0)
                return;
            dirtyRangeScratch.Sort();
            int runStart = dirtyRangeScratch[0];
            int runEnd = runStart + 1;
            for (int i = 1; i <= dirtyRangeScratch.Count; i++)
            {
                if (i < dirtyRangeScratch.Count && dirtyRangeScratch[i] == runEnd)
                {
                    runEnd++;
                    continue;
                }
                destination.SetData(source, runStart, runStart, runEnd - runStart);
                if (i < dirtyRangeScratch.Count)
                {
                    runStart = dirtyRangeScratch[i];
                    runEnd = runStart + 1;
                }
            }
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

            if (transcodeTaskCpu.Length < pendingTranscodePageIds.Count)
                Array.Resize(ref transcodeTaskCpu, pendingTranscodePageIds.Count);
            PageTranscodeTask[] tasks = transcodeTaskCpu;
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
                MarkPageDirty(pageId);
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
            if (stats.formatVersion < 1 ||
                stats.formatVersion > NanitePageBinaryCodec.CurrentVersion)
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
                        hash = hash * 31 + (page != null ? page.BinaryPayloadOffset : 0);
                        hash = hash * 31 + (page != null ? page.BinaryPayloadSize : 0);
                    }
                }
                return hash;
            }
        }

    }
}
