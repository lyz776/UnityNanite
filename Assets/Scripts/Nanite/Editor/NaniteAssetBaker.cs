#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Nanite.Editor
{
    /// <summary>
    /// 离线资源打包：SubMesh DAG → Part（粗剔除单元）→ Page（分块加载）。
    /// 简化 UE Nanite 的 BVH + Persistent Threads：两层 Part/Cluster 并行剔除。
    /// </summary>
    public static class NaniteAssetBaker
    {
        public const int TargetPageBytes = 256 * 1024;
        public const int MaxPartPerPage = 128;
        public const int MaxClusterPerPart = 8;
        static bool sCancelRequested;

        public static bool IsBakeRunning { get; private set; }
        public static bool IsCancelRequested => sCancelRequested;

        sealed class BuildPart
        {
            public List<int> clusterIndices = new List<int>();
            public int mip;
            public int subMeshId;
            public int groupOrdinal;
            public Vector3 center;
            public float radius;
            public bool root;
        }

        readonly struct BuildGroupKey : IEquatable<BuildGroupKey>
        {
            public readonly int subMeshId;
            public readonly int groupOrdinal;

            public BuildGroupKey(int subMeshId, int groupOrdinal)
            {
                this.subMeshId = subMeshId;
                this.groupOrdinal = groupOrdinal;
            }

            public bool Equals(BuildGroupKey other) =>
                subMeshId == other.subMeshId && groupOrdinal == other.groupOrdinal;

            public override bool Equals(object obj) => obj is BuildGroupKey other && Equals(other);

            public override int GetHashCode() => unchecked(subMeshId * 397 ^ groupOrdinal);
        }

        sealed class StreamingGroupBlock
        {
            public readonly List<BuildPart> parts = new List<BuildPart>(2);
            public int subMeshId;
            public int groupOrdinal;
            public int sourceOrder;
            public Vector3 center;
            public uint morton;
        }

        struct PageRange
        {
            public int startPart;
            public int partCount;
        }

        sealed class BuiltPage
        {
            public NaniteMeshPage page;
            public byte[] blob;
            public NanitePageBinaryStats stats;
            public int minMip;
            public int maxMip;
            public Vector4 bounds;
            public int indexCount;
            public int clusterCount;
            public int vertexCount;
            public int buildMilliseconds;
            public bool root;
        }

        sealed class HierarchyBuildResult
        {
            public NaniteHierarchyGroup[] groups = Array.Empty<NaniteHierarchyGroup>();
            public NaniteHierarchyClusterRef[] clusterRefs = Array.Empty<NaniteHierarchyClusterRef>();
            public int[] rootGroups = Array.Empty<int>();
            public int geometryClusterCount;
        }

        sealed class HierarchyQualityAudit
        {
            public int clusterCount;
            public int groupCount;
            public int minFanout = int.MaxValue;
            public int maxFanout;
            public long fanoutSum;
            public int singletonGroups;
            public int missingClusterMembership;
            public int duplicateClusterMembership;
            public int groupMipMismatch;
            public int invalidBoundsOrErrors;
            public int errorMonotonicityViolations;
            public int geometrySphereContainmentViolations;
            public int parentSphereContainmentViolations;
            public int groupSphereContainmentViolations;
            public int parentMetadataMismatch;
            public int groupErrorRangeViolations;
            public int triangleReductionViolations;
            public int maxMipMismatch;
            public long partitionVertexIncidences;
            public long partitionUniqueVertices;
            public long[] trianglesPerMip = Array.Empty<long>();

            public bool HasFatalViolation =>
                missingClusterMembership != 0 ||
                duplicateClusterMembership != 0 ||
                groupMipMismatch != 0 ||
                invalidBoundsOrErrors != 0 ||
                errorMonotonicityViolations != 0 ||
                geometrySphereContainmentViolations != 0 ||
                parentSphereContainmentViolations != 0 ||
                groupSphereContainmentViolations != 0 ||
                parentMetadataMismatch != 0 ||
                groupErrorRangeViolations != 0 ||
                triangleReductionViolations != 0 ||
                maxMipMismatch != 0;

            public string Format(int maxMip)
            {
                var mipText = new StringBuilder(128);
                for (int mip = 0; mip < trianglesPerMip.Length; mip++)
                {
                    if (mip > 0)
                        mipText.Append(',');
                    mipText.Append(mip).Append(':').Append(trianglesPerMip[mip]);
                }

                double averageFanout = groupCount > 0 ? fanoutSum / (double)groupCount : 0.0;
                double boundaryDuplication = partitionVertexIncidences > 0
                    ? (partitionVertexIncidences - partitionUniqueVertices) /
                      (double)partitionVertexIncidences
                    : 0.0;
                return
                    $"groups={groupCount}, fanout={Mathf.Max(0, minFanout)}/{averageFanout:F2}/{maxFanout}, " +
                    $"singletons={singletonGroups}, depth={maxMip}, boundaryVertexDup={boundaryDuplication:P2}\n" +
                    $"  DAG contract: membershipMissing={missingClusterMembership}, membershipDuplicate={duplicateClusterMembership}, " +
                    $"mipMismatch={groupMipMismatch}, invalid={invalidBoundsOrErrors}, " +
                    $"errorMonotonic={errorMonotonicityViolations}, geometryContainment={geometrySphereContainmentViolations}, " +
                    $"parentContainment={parentSphereContainmentViolations}, " +
                    $"groupContainment={groupSphereContainmentViolations}, parentMetadata={parentMetadataMismatch}, " +
                    $"groupErrorRange={groupErrorRangeViolations}, triangleReduction={triangleReductionViolations}, " +
                    $"maxMip={maxMipMismatch}\n" +
                    $"  Triangles/mip: {mipText}";
            }
        }

        sealed class PageLocalityAudit
        {
            public int pageCount;
            public int rootPageCount;
            public int groupCount;
            public int splitGroupCount;
            public int maxPagesPerGroup;
            public int mixedMipPages;
            public int maxSubMeshesPerPage;
            public long pageVertexIncidences;
            public long uniqueMipVertices;
            public float minFill = 1f;
            public float maxFill;
            public double fillSum;

            public string Format()
            {
                double boundaryDuplication = pageVertexIncidences > 0
                    ? (pageVertexIncidences - uniqueMipVertices) / (double)pageVertexIncidences
                    : 0.0;
                double averageFill = pageCount > 0 ? fillSum / pageCount : 0.0;
                return
                    $"pages={pageCount}, rootPages={rootPageCount}, fill={minFill:P1}/{averageFill:P1}/{maxFill:P1}, " +
                    $"groups={groupCount}, splitGroups={splitGroupCount}, maxPagesPerGroup={maxPagesPerGroup}, " +
                    $"pageBoundaryVertexDup={boundaryDuplication:P2}, mixedMip={mixedMipPages}, " +
                    $"maxSubMeshesPerPage={maxSubMeshesPerPage}";
            }
        }

        public static void RequestCancel()
        {
            if (!IsBakeRunning || sCancelRequested)
                return;

            sCancelRequested = true;
            Debug.LogWarning("[Nanite] 收到取消请求，将在安全检查点停止…");
        }

        public static void Bake(Mesh mesh)
        {
            if (IsBakeRunning)
            {
                Debug.LogWarning("[Nanite] Bake 已在进行中。");
                return;
            }

            if (mesh == null)
            {
                Debug.LogError("[Nanite] Bake 需要有效的 Mesh。");
                return;
            }

            if (!mesh.isReadable)
            {
                Debug.LogError("[Nanite] Mesh 不可读，请在 Import Settings 勾选 Read/Write Enabled。");
                return;
            }

            IsBakeRunning = true;
            sCancelRequested = false;

            var totalTimer = Stopwatch.StartNew();
            long dagMs = 0, partMs = 0, pageMs = 0, saveMs = 0;

            try
            {
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                var uvs = mesh.uv;
                var tangents = mesh.tangents;
                var buildGeometry = new NaniteBuildGeometry(vertices, normals, uvs, tangents);
                int subMeshCount = mesh.subMeshCount;

                Debug.Log($"[Nanite] Bake 开始: mesh={mesh.name}, vertices={vertices.Length}, subMeshes={subMeshCount}");

                var subMeshList = new List<NaniteSubMesh>(subMeshCount);
                int totalClusterCount = 0;
                int maxMipLevel = 0;
                int sourceTriangleCount = 0;

                var stageTimer = Stopwatch.StartNew();
                for (int i = 0; i < subMeshCount; i++)
                {
                    float p = subMeshCount <= 0 ? 0f : (i / (float)subMeshCount) * 0.4f;
                    UpdateProgress($"Build DAG SubMesh {i + 1}/{subMeshCount}", p);

                    var subTimer = Stopwatch.StartNew();
                    var triangles = mesh.GetTriangles(i);
                    sourceTriangleCount += triangles.Length / 3;
                    var subMesh = NaniteMeshBuilder.Build(
                        buildGeometry,
                        triangles,
                        () => sCancelRequested);
                    subMeshList.Add(subMesh);
                    totalClusterCount += subMesh.clusterList.Count;
                    maxMipLevel = Mathf.Max(maxMipLevel, subMesh.maxMipLevel);

                    Debug.Log(
                        $"[Nanite] SubMesh {i + 1}/{subMeshCount}: tris={triangles.Length / 3}, " +
                        $"clusters={subMesh.clusterList.Count}, groups={subMesh.clusterGroupList.Count}, " +
                        $"maxMip={subMesh.maxMipLevel}, {subTimer.ElapsedMilliseconds} ms");
                }
                dagMs = stageTimer.ElapsedMilliseconds;

                // Coarse LODs may own optimized vertices. Every downstream audit,
                // Page estimate and Page pack must consume the same bake arena;
                // falling back to mesh.vertices here would reinterpret generated
                // indices and manifests as distance-dependent holes/UV corruption.
                vertices = buildGeometry.positions.ToArray();
                normals = buildGeometry.normals.ToArray();
                uvs = buildGeometry.uvs.ToArray();
                tangents = buildGeometry.tangents.ToArray();
                CalculateMeshPositionGrid(
                    vertices,
                    out Vector3 meshPositionMin,
                    out Vector3 meshPositionExtent);

                HierarchyQualityAudit hierarchyAudit = AuditHierarchyQuality(
                    subMeshList,
                    vertices,
                    maxMipLevel);
                if (hierarchyAudit.HasFatalViolation)
                {
                    throw new InvalidDataException(
                        "Nanite hierarchy quality contract failed:\n" +
                        hierarchyAudit.Format(maxMipLevel));
                }
                Debug.Log(
                    "[Nanite][BakeAudit] Hierarchy/partition quality\n  " +
                    hierarchyAudit.Format(maxMipLevel));

                stageTimer.Restart();
                var buildParts = BuildPartsFromGroups(subMeshList);
                buildParts = OrderPartsForStreaming(buildParts);
                int buildPartCount = buildParts.Count;
                var pageRanges = PlanPageRanges(buildParts, subMeshList, uvs, TargetPageBytes);
                int pageCount = pageRanges.Count;
                partMs = stageTimer.ElapsedMilliseconds;

                Debug.Log(
                    $"[Nanite] Part 划分完成: parts={buildPartCount}, estimatedPages={pageCount}, " +
                    $"pageBudget={TargetPageBytes / 1024} KiB, {partMs} ms");

                if (buildPartCount == 0)
                {
                    Debug.LogWarning("[Nanite] 未生成任何 Part，Bake 中止。");
                    return;
                }

                string meshAssetPath = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(meshAssetPath))
                {
                    Debug.LogError("[Nanite] Mesh 必须是项目内的资源（有 Asset 路径）。");
                    return;
                }

                string basePath = meshAssetPath.Replace(Path.GetExtension(meshAssetPath), "");
                string meshPath = basePath + "_mesh.asset";
                var previousGeneratedAssets = CollectReferencedPageAssetPaths(meshPath);
                int totalNaniteVerts = 0;
                long totalLegacyRawBytes = 0;
                long totalBinaryBytes = 0;
                long totalStorageBytes = 0;
                float maxPositionQuantizationError = 0f;
                float maxUvQuantizationError = 0f;
                float maxNormalQuantizationError = 0f;
                float maxTangentQuantizationError = 0f;
                int minPackedPageBytes = int.MaxValue;
                int maxPackedPageBytes = 0;
                int oversizedPageCount = 0;
                int compressedPageCount = 0;
                var builtPages = new List<BuiltPage>(pageCount);

                stageTimer.Restart();
                int rangeIndex = 0;
                while (rangeIndex < pageRanges.Count)
                {
                    float p = 0.4f + (pageRanges.Count <= 0 ? 0f : (rangeIndex / (float)pageRanges.Count) * 0.42f);
                    UpdateProgress($"Pack Page {rangeIndex + 1}/{pageRanges.Count}", p);

                    var pageTimer = Stopwatch.StartNew();
                    PageRange range = pageRanges[rangeIndex];
                    NaniteMeshPage page = BuildPage(
                        range,
                        buildParts,
                        subMeshList,
                        vertices,
                        normals,
                        tangents,
                        uvs);

                    if (!NanitePageBinaryCodec.TryEncode(
                            page,
                            meshPositionMin,
                            meshPositionExtent,
                            out byte[] binaryBlob,
                            out NanitePageBinaryStats binaryStats,
                            out string binaryError))
                    {
                        UnityEngine.Object.DestroyImmediate(page);
                        throw new InvalidDataException($"Page {rangeIndex} binary encode/round-trip failed: {binaryError}");
                    }

                    if (binaryBlob.Length > TargetPageBytes && range.partCount > 1)
                    {
                        UnityEngine.Object.DestroyImmediate(page);
                        int leftCount = FindGroupPreservingSplit(range, buildParts);
                        pageRanges[rangeIndex] = new PageRange
                        {
                            startPart = range.startPart,
                            partCount = leftCount
                        };
                        pageRanges.Insert(rangeIndex + 1, new PageRange
                        {
                            startPart = range.startPart + leftCount,
                            partCount = range.partCount - leftCount
                        });
                        continue;
                    }

                    if (!NanitePageStorageCodec.TryPack(
                            binaryBlob,
                            out byte[] storageBlob,
                            out bool storageCompressed,
                            out string storageError))
                    {
                        UnityEngine.Object.DestroyImmediate(page);
                        throw new InvalidDataException(
                            $"Page {rangeIndex} storage compression failed: {storageError}");
                    }
                    binaryStats.storageBytes = storageBlob.Length;
                    binaryStats.storageCodec = storageCompressed
                        ? NanitePageStorageCodec.CodecLz4Block
                        : 0;
                    if (storageCompressed)
                        compressedPageCount++;
                    binaryStats.storageSizeRatio = binaryBlob.Length > 0
                        ? storageBlob.Length / (float)binaryBlob.Length
                        : 1f;
                    if (!NanitePageStorageCodec.TryUnpack(
                            storageBlob,
                            out byte[] storageRoundTrip,
                            out string unpackError) ||
                        !BytesEqual(binaryBlob, storageRoundTrip))
                    {
                        UnityEngine.Object.DestroyImmediate(page);
                        throw new InvalidDataException(
                            $"Page {rangeIndex} storage round-trip failed: {unpackError}");
                    }

                    GetPageMipRange(page, out int pageMinMip, out int pageMaxMip);
                    var builtPage = new BuiltPage
                    {
                        page = page,
                        blob = storageBlob,
                        stats = binaryStats,
                        minMip = pageMinMip,
                        maxMip = pageMaxMip,
                        bounds = MergePartBounds(page.parts),
                        indexCount = page.indiceArray.Length,
                        clusterCount = page.clusterArray.Length,
                        vertexCount = page.vertexCount,
                        buildMilliseconds = (int)pageTimer.ElapsedMilliseconds,
                        root = RangeContainsRoot(range, buildParts)
                    };
                    builtPages.Add(builtPage);
                    rangeIndex++;

                    totalNaniteVerts += builtPage.vertexCount;
                    totalLegacyRawBytes += binaryStats.legacyRawBytes;
                    totalBinaryBytes += binaryStats.blobBytes;
                    totalStorageBytes += binaryStats.storageBytes;
                    maxPositionQuantizationError = Mathf.Max(maxPositionQuantizationError, binaryStats.maxPositionError);
                    maxUvQuantizationError = Mathf.Max(maxUvQuantizationError, binaryStats.maxUvError);
                    maxNormalQuantizationError = Mathf.Max(maxNormalQuantizationError, binaryStats.maxNormalAngleError);
                    maxTangentQuantizationError = Mathf.Max(maxTangentQuantizationError, binaryStats.maxTangentAngleError);
                    minPackedPageBytes = Mathf.Min(minPackedPageBytes, binaryStats.blobBytes);
                    maxPackedPageBytes = Mathf.Max(maxPackedPageBytes, binaryStats.blobBytes);
                    if (binaryStats.blobBytes > TargetPageBytes)
                    {
                        oversizedPageCount++;
                        Debug.LogWarning(
                            $"[Nanite] Page {builtPages.Count - 1} contains one indivisible oversized Part: " +
                            $"{binaryStats.blobBytes / 1024f:F1} KiB > {TargetPageBytes / 1024} KiB.");
                    }

                    Debug.Log(
                        $"[Nanite] Packed Page {builtPages.Count}: parts={range.partCount}, clusters={builtPage.clusterCount}, " +
                        $"indices={builtPage.indexCount}, verts={builtPage.vertexCount}, bvhNodes={page.bvhNodes.Length}, " +
                        $"packed={binaryStats.blobBytes / 1024f:F1} KiB/storage={binaryStats.storageBytes / 1024f:F1} KiB/" +
                        $"raw={binaryStats.legacyRawBytes / 1024f:F1} KiB " +
                        $"(packed/raw={binaryStats.rawSizeRatio:P1}, storage/packed={binaryStats.storageSizeRatio:P1}), " +
                        $"{builtPage.buildMilliseconds} ms");
                }

                pageCount = builtPages.Count;
                PageLocalityAudit pageLocalityAudit = AuditPageLocality(
                    buildParts,
                    pageRanges,
                    subMeshList,
                    builtPages);
                Debug.Log(
                    "[Nanite][BakeAudit] Page locality/streaming quality\n  " +
                    pageLocalityAudit.Format());

                HierarchyBuildResult hierarchy = BuildHierarchyMetadata(
                    subMeshList,
                    buildParts,
                    pageRanges,
                    builtPages);
                Debug.Log(
                    $"[Nanite][BakeAudit] Cross-Page hierarchy ABI: groups={hierarchy.groups.Length}, " +
                    $"refs={hierarchy.clusterRefs.Length}, roots={hierarchy.rootGroups.Length}, " +
                    $"geometryClusters={hierarchy.geometryClusterCount}.");

                var savedPages = new NaniteMeshPage[pageCount];
                var usedGeneratedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var bulkOffsets = new int[pageCount];
                int bulkByteCount = 0;
                for (int pageIdx = 0; pageIdx < pageCount; pageIdx++)
                {
                    bulkOffsets[pageIdx] = bulkByteCount;
                    bulkByteCount = checked(bulkByteCount + builtPages[pageIdx].blob.Length);
                }
                var bulkPayload = new byte[Mathf.Max(1, bulkByteCount)];
                for (int pageIdx = 0; pageIdx < pageCount; pageIdx++)
                {
                    byte[] pageBlob = builtPages[pageIdx].blob;
                    Buffer.BlockCopy(pageBlob, 0, bulkPayload, bulkOffsets[pageIdx], pageBlob.Length);
                }
                string streamingRelativePath = BuildStreamingRelativePath(mesh, meshAssetPath);
                string bulkBinaryPath = "Assets/StreamingAssets/" + streamingRelativePath;
                WriteBinaryAsset(bulkBinaryPath, bulkPayload);
                usedGeneratedAssets.Add(bulkBinaryPath);

                for (int pageIdx = 0; pageIdx < pageCount; pageIdx++)
                {
                    float p = 0.82f + (pageCount <= 0 ? 0f : (pageIdx / (float)pageCount) * 0.13f);
                    UpdateProgress($"Write Page {pageIdx + 1}/{pageCount}", p);

                    BuiltPage builtPage = builtPages[pageIdx];
                    builtPage.page.SetBinaryPayload(
                        null,
                        builtPage.stats,
                        bulkOffsets[pageIdx],
                        builtPage.blob.Length,
                        streamingRelativePath);
                    if (!builtPage.page.StripLegacyGeometryPayload(out string stripError))
                        throw new InvalidDataException($"Page {pageIdx} legacy geometry strip failed: {stripError}");

                    builtPage.page.name = $"{mesh.name}_Page_{pageIdx:D4}";
                    savedPages[pageIdx] = builtPage.page;
                }
                pageMs = stageTimer.ElapsedMilliseconds;

                stageTimer.Restart();
                UpdateProgress("Finalize assets", 0.97f);

                var naniteMesh = ScriptableObject.CreateInstance<NaniteMesh>();
                naniteMesh.sourceMesh = mesh;
                naniteMesh.sourceTriangleCount = sourceTriangleCount;
                naniteMesh.subMeshCount = subMeshCount;
                naniteMesh.maxMipLevel = maxMipLevel;
                naniteMesh.pageArray = savedPages;
                naniteMesh.hierarchyVersion = NaniteHierarchyGroup.CurrentVersion;
                naniteMesh.hierarchyGroups = hierarchy.groups;
                naniteMesh.hierarchyClusterRefs = hierarchy.clusterRefs;
                naniteMesh.hierarchyRootGroups = hierarchy.rootGroups;
                naniteMesh.pageStreamingInfo = new NanitePageStreamingInfo[pageCount];
                int rootPageCount = 0;
                long rootPackedBytes = 0;
                int mixedMipPageCount = 0;
                for (int i = 0; i < pageCount; i++)
                {
                    BuiltPage page = builtPages[i];
                    bool isRootPage = page.root;
                    if (isRootPage)
                    {
                        rootPageCount++;
                        rootPackedBytes += page.stats.blobBytes;
                    }
                    if (page.minMip != page.maxMip)
                        mixedMipPageCount++;
                    naniteMesh.pageStreamingInfo[i] = new NanitePageStreamingInfo
                    {
                        pageIndex = i,
                        packedBytes = page.stats.blobBytes,
                        storageBytes = page.stats.storageBytes,
                        vertexBytes = page.stats.vertexBytes,
                        indexBytes = page.stats.indexBytes,
                        hierarchyBytes = page.stats.hierarchyBytes,
                        minMip = page.minMip,
                        maxMip = page.maxMip,
                        flags = isRootPage ? 1 : 0,
                        boundingSphere = page.bounds
                    };
                }

                var meshBounds = mesh.bounds;
                naniteMesh.boundingSphere = new Vector4(
                    meshBounds.center.x, meshBounds.center.y, meshBounds.center.z,
                    meshBounds.extents.magnitude);

                SaveNaniteMeshAsset(naniteMesh, meshPath);
                DeleteStaleGeneratedAssets(previousGeneratedAssets, usedGeneratedAssets);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                saveMs = stageTimer.ElapsedMilliseconds;

                totalTimer.Stop();
                Debug.Log(
                    $"[Nanite] Bake 完成\n" +
                    $"  原 mesh 顶点: {vertices.Length}  Nanite 顶点(各 Page 合计): {totalNaniteVerts}\n" +
                    $"  Cluster: {totalClusterCount}  Part: {buildPartCount}  Page: {pageCount}\n" +
                    $"  Page Binary V{NanitePageBinaryCodec.CurrentVersion}: packed={totalBinaryBytes / (1024f * 1024f):F2} MiB, " +
                    $"LZ4 storage={totalStorageBytes / (1024f * 1024f):F2} MiB, raw={totalLegacyRawBytes / (1024f * 1024f):F2} MiB " +
                    $"(storage/raw={(totalLegacyRawBytes > 0 ? totalStorageBytes / (double)totalLegacyRawBytes : 1.0):P1})\n" +
                    $"  Page budget: target={TargetPageBytes / 1024} KiB, min={(minPackedPageBytes == int.MaxValue ? 0 : minPackedPageBytes) / 1024f:F1} KiB, " +
                    $"max={maxPackedPageBytes / 1024f:F1} KiB, oversized={oversizedPageCount}, rootPages={rootPageCount}\n" +
                    $"  Page audit: fill={(pageCount > 0 ? totalBinaryBytes / (double)(pageCount * (long)TargetPageBytes) : 0.0):P1}, " +
                    $"compressed={compressedPageCount}/{pageCount}, mixedMip={mixedMipPageCount}, rootPacked={rootPackedBytes / 1024f:F1} KiB\n" +
                    $"  Page locality: {pageLocalityAudit.Format()}\n" +
                    $"  Hierarchy audit: {hierarchyAudit.Format(maxMipLevel)}\n" +
                    $"  量化最大误差: position={maxPositionQuantizationError:G4}, uv={maxUvQuantizationError:G4}, " +
                    $"normal={maxNormalQuantizationError:G4}°, tangent={maxTangentQuantizationError:G4}°\n" +
                    $"  阶段耗时(ms): DAG={dagMs}, Part={partMs}, Page={pageMs}, Save={saveMs}, Total={totalTimer.ElapsedMilliseconds}\n" +
                    $"  资源: {basePath}_mesh.asset");
            }
            finally
            {
                IsBakeRunning = false;
                sCancelRequested = false;
                EditorUtility.ClearProgressBar();
            }
        }

        static void ThrowIfCancelled()
        {
            if (sCancelRequested)
                throw new OperationCanceledException("Bake cancelled by hotkey.");
        }

        static HierarchyQualityAudit AuditHierarchyQuality(
            List<NaniteSubMesh> subMeshes,
            Vector3[] vertices,
            int maxMipLevel)
        {
            var audit = new HierarchyQualityAudit
            {
                trianglesPerMip = new long[Mathf.Max(1, maxMipLevel + 1)]
            };
            if (subMeshes == null || vertices == null || vertices.Length == 0)
            {
                audit.invalidBoundsOrErrors++;
                return audit;
            }

            var positionRemap = new uint[vertices.Length];
            MeshOptimizerNative.NativeGeneratePositionRemap(
                positionRemap,
                vertices,
                (UIntPtr)(uint)vertices.Length,
                (UIntPtr)(uint)(sizeof(float) * 3));

            for (int subMeshIndex = 0; subMeshIndex < subMeshes.Count; subMeshIndex++)
            {
                NaniteSubMesh subMesh = subMeshes[subMeshIndex];
                if (subMesh == null || subMesh.clusterList == null || subMesh.clusterGroupList == null)
                {
                    audit.invalidBoundsOrErrors++;
                    continue;
                }

                List<Cluster> clusters = subMesh.clusterList;
                List<ClusterGroup> groups = subMesh.clusterGroupList;
                audit.clusterCount += clusters.Count;
                int observedMaxMip = 0;
                var membership = new int[clusters.Count];
                for (int i = 0; i < membership.Length; i++)
                    membership[i] = -1;

                var uniqueVerticesPerMip = new Dictionary<int, HashSet<uint>>();
                for (int clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
                {
                    Cluster cluster = clusters[clusterIndex];
                    int mip = Mathf.Max(0, cluster.mip);
                    observedMaxMip = Mathf.Max(observedMaxMip, mip);
                    if (mip >= audit.trianglesPerMip.Length)
                        Array.Resize(ref audit.trianglesPerMip, mip + 1);
                    audit.trianglesPerMip[mip] += cluster.indices != null
                        ? cluster.indices.Length / 3
                        : 0;

                    if (!IsFiniteNonNegative(cluster.self.radius) ||
                        !IsFiniteNonNegative(cluster.parent.radius) ||
                        !IsFiniteVector(cluster.self.center) ||
                        !IsFiniteVector(cluster.parent.center) ||
                        !IsValidLodError(cluster.self.error) ||
                        !IsValidLodError(cluster.parent.error))
                    {
                        audit.invalidBoundsOrErrors++;
                    }

                    if (cluster.parent.error < float.MaxValue * 0.5f &&
                        cluster.parent.error + 1e-6f < cluster.self.error)
                    {
                        audit.errorMonotonicityViolations++;
                    }

                    float geometryContainmentTolerance = Mathf.Max(1e-4f, cluster.self.radius * 1e-4f);
                    if (Vector3.Distance(cluster.geometry.center, cluster.self.center) + cluster.geometry.radius >
                        cluster.self.radius + geometryContainmentTolerance)
                    {
                        audit.geometrySphereContainmentViolations++;
                    }

                    float containmentTolerance = Mathf.Max(1e-4f, cluster.parent.radius * 1e-4f);
                    if (Vector3.Distance(cluster.self.center, cluster.parent.center) + cluster.self.radius >
                        cluster.parent.radius + containmentTolerance)
                    {
                        audit.parentSphereContainmentViolations++;
                    }
                }

                for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                {
                    ClusterGroup group = groups[groupIndex];
                    int fanout = group?.children != null ? group.children.Count : 0;
                    audit.groupCount++;
                    audit.minFanout = Mathf.Min(audit.minFanout, fanout);
                    audit.maxFanout = Mathf.Max(audit.maxFanout, fanout);
                    audit.fanoutSum += fanout;
                    if (fanout == 1)
                        audit.singletonGroups++;
                    if (fanout <= 0)
                    {
                        audit.invalidBoundsOrErrors++;
                        continue;
                    }

                    if (!IsFiniteVector(group.boundsCenter) ||
                        !IsFiniteNonNegative(group.radius) ||
                        !IsValidLodError(group.minLodError) ||
                        !IsValidLodError(group.maxParentLodError))
                    {
                        audit.invalidBoundsOrErrors++;
                    }
                    if (group.maxParentLodError < float.MaxValue * 0.5f &&
                        group.minLodError > group.maxParentLodError + 1e-6f)
                    {
                        audit.groupErrorRangeViolations++;
                    }

                    var groupVertices = new HashSet<uint>();
                    int expectedMip = -1;
                    for (int childIndex = 0; childIndex < fanout; childIndex++)
                    {
                        int clusterIndex = group.children[childIndex];
                        if ((uint)clusterIndex >= (uint)clusters.Count)
                        {
                            audit.invalidBoundsOrErrors++;
                            continue;
                        }

                        if (membership[clusterIndex] >= 0)
                            audit.duplicateClusterMembership++;
                        else
                            membership[clusterIndex] = groupIndex;

                        Cluster cluster = clusters[clusterIndex];
                        if (expectedMip < 0)
                            expectedMip = cluster.mip;
                        if (cluster.mip != expectedMip || cluster.mip != group.mipLevel)
                            audit.groupMipMismatch++;

                        float groupContainmentTolerance = Mathf.Max(1e-4f, group.radius * 1e-4f);
                        if (Vector3.Distance(cluster.self.center, group.boundsCenter) + cluster.self.radius >
                            group.radius + groupContainmentTolerance)
                        {
                            audit.groupSphereContainmentViolations++;
                        }
                        if (Vector3.Distance(cluster.parent.center, group.boundsCenter) >
                                groupContainmentTolerance ||
                            Mathf.Abs(cluster.parent.radius - group.radius) > groupContainmentTolerance ||
                            !ApproximatelyLodError(cluster.parent.error, group.maxParentLodError))
                        {
                            audit.parentMetadataMismatch++;
                        }

                        if (cluster.indices == null)
                        {
                            audit.invalidBoundsOrErrors++;
                            continue;
                        }
                        for (int index = 0; index < cluster.indices.Length; index++)
                        {
                            int vertexIndex = cluster.indices[index];
                            if ((uint)vertexIndex >= (uint)positionRemap.Length)
                            {
                                audit.invalidBoundsOrErrors++;
                                continue;
                            }
                            groupVertices.Add(positionRemap[vertexIndex]);
                        }
                    }

                    int groupMip = Mathf.Max(0, expectedMip);
                    if (!uniqueVerticesPerMip.TryGetValue(groupMip, out HashSet<uint> mipVertices))
                    {
                        mipVertices = new HashSet<uint>();
                        uniqueVerticesPerMip.Add(groupMip, mipVertices);
                    }
                    audit.partitionVertexIncidences += groupVertices.Count;
                    foreach (uint vertex in groupVertices)
                        mipVertices.Add(vertex);
                }

                for (int i = 0; i < membership.Length; i++)
                {
                    if (membership[i] < 0)
                        audit.missingClusterMembership++;
                }
                foreach (KeyValuePair<int, HashSet<uint>> pair in uniqueVerticesPerMip)
                    audit.partitionUniqueVertices += pair.Value.Count;
                if (observedMaxMip != subMesh.maxMipLevel)
                    audit.maxMipMismatch++;
            }

            for (int mip = 1; mip < audit.trianglesPerMip.Length; mip++)
            {
                if (audit.trianglesPerMip[mip] > audit.trianglesPerMip[mip - 1])
                    audit.triangleReductionViolations++;
            }

            if (audit.groupCount == 0)
                audit.minFanout = 0;
            return audit;
        }

        static bool IsFiniteNonNegative(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;

        static bool IsFiniteVector(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        static void CalculateMeshPositionGrid(
            Vector3[] vertices,
            out Vector3 minimum,
            out Vector3 extent)
        {
            if (vertices == null || vertices.Length == 0)
                throw new InvalidDataException("Cannot quantize an empty Nanite mesh.");
            minimum = vertices[0];
            Vector3 maximum = vertices[0];
            for (int vertexIndex = 1; vertexIndex < vertices.Length; vertexIndex++)
            {
                minimum = Vector3.Min(minimum, vertices[vertexIndex]);
                maximum = Vector3.Max(maximum, vertices[vertexIndex]);
            }
            extent = maximum - minimum;
        }

        static bool IsValidLodError(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;

        static bool ApproximatelyLodError(float a, float b)
        {
            if (a >= float.MaxValue * 0.5f || b >= float.MaxValue * 0.5f)
                return a >= float.MaxValue * 0.5f && b >= float.MaxValue * 0.5f;
            float tolerance = Mathf.Max(1e-6f, Mathf.Max(Mathf.Abs(a), Mathf.Abs(b)) * 1e-4f);
            return Mathf.Abs(a - b) <= tolerance;
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        static List<PageRange> PlanPageRanges(
            List<BuildPart> buildParts,
            List<NaniteSubMesh> subMeshes,
            Vector2[] uvs,
            int targetBytes)
        {
            var ranges = new List<PageRange>();
            var uniqueVertices = new HashSet<int>();
            var addedVertices = new List<int>();
            int startPart = 0;
            int partCount = 0;
            int clusterCount = 0;
            int indexCount = 0;
            int maxMip = 0;
            int floatUvVertexCount = 0;
            int partIndex = 0;

            while (partIndex < buildParts.Count)
            {
                BuildPart candidate = buildParts[partIndex];

                // Root/terminal DAG groups form the permanently resident prefix. Never use
                // spare root Page capacity for streamable leaf data; that would silently grow
                // the pinned working set and can evict useful non-root geometry sooner.
                if (partCount > 0 && buildParts[partIndex - 1].root != candidate.root)
                {
                    ranges.Add(new PageRange { startPart = startPart, partCount = partCount });
                    startPart = partIndex;
                    partCount = 0;
                    clusterCount = 0;
                    indexCount = 0;
                    maxMip = 0;
                    floatUvVertexCount = 0;
                    uniqueVertices.Clear();
                    continue;
                }

                // Treat a ClusterGroup as the preferred streaming atom. A group may contain
                // two Parts because the raster culling unit is capped at eight Clusters, but
                // those Parts should start together on a fresh Page whenever the whole group
                // fits. This avoids requesting two Pages for one DAG parent solely to improve
                // the previous Page's fill ratio.
                bool startsGroup = partIndex == 0 ||
                                   !SameBuildGroup(buildParts[partIndex - 1], candidate);
                if (partCount > 0 && startsGroup)
                {
                    int groupEnd = partIndex + 1;
                    while (groupEnd < buildParts.Count &&
                           SameBuildGroup(candidate, buildParts[groupEnd]))
                    {
                        groupEnd++;
                    }

                    var probeVertices = new HashSet<int>(uniqueVertices);
                    int probeClusters = clusterCount;
                    int probeIndices = indexCount;
                    int probeMaxMip = maxMip;
                    int probeFloatUvVertexCount = floatUvVertexCount;
                    for (int probePartIndex = partIndex; probePartIndex < groupEnd; probePartIndex++)
                    {
                        BuildPart probePart = buildParts[probePartIndex];
                        List<Cluster> probePartClusters = subMeshes[probePart.subMeshId].clusterList;
                        probeMaxMip = Mathf.Max(probeMaxMip, probePart.mip);
                        for (int c = 0; c < probePart.clusterIndices.Count; c++)
                        {
                            Cluster cluster = probePartClusters[probePart.clusterIndices[c]];
                            probeClusters++;
                            probeIndices += cluster.indices.Length;
                            for (int i = 0; i < cluster.indices.Length; i++)
                            {
                                int vertex = cluster.indices[i];
                                if (probeVertices.Add(vertex) && VertexRequiresFloatUv(vertex, uvs))
                                    probeFloatUvVertexCount++;
                            }
                        }
                    }

                    int probePartCount = partCount + groupEnd - partIndex;
                    int probeBytes = EstimatePackedPageBytes(
                        probeVertices.Count,
                        probeIndices,
                        probeClusters,
                        probePartCount,
                        probeMaxMip,
                        probeFloatUvVertexCount > 0);
                    if (probeBytes > targetBytes || probePartCount > MaxPartPerPage)
                    {
                        ranges.Add(new PageRange { startPart = startPart, partCount = partCount });
                        startPart = partIndex;
                        partCount = 0;
                        clusterCount = 0;
                        indexCount = 0;
                        maxMip = 0;
                        floatUvVertexCount = 0;
                        uniqueVertices.Clear();
                        continue;
                    }
                }

                List<Cluster> clusters = subMeshes[candidate.subMeshId].clusterList;
                addedVertices.Clear();
                int addedClusters = 0;
                int addedIndices = 0;
                int addedFloatUvVertices = 0;

                for (int c = 0; c < candidate.clusterIndices.Count; c++)
                {
                    Cluster cluster = clusters[candidate.clusterIndices[c]];
                    addedClusters++;
                    addedIndices += cluster.indices.Length;
                    for (int i = 0; i < cluster.indices.Length; i++)
                    {
                        int vertex = cluster.indices[i];
                        if (uniqueVertices.Add(vertex))
                        {
                            addedVertices.Add(vertex);
                            if (VertexRequiresFloatUv(vertex, uvs))
                                addedFloatUvVertices++;
                        }
                    }
                }

                int tentativeParts = partCount + 1;
                int tentativeClusters = clusterCount + addedClusters;
                int tentativeIndices = indexCount + addedIndices;
                int tentativeMaxMip = Mathf.Max(maxMip, candidate.mip);
                int estimatedBytes = EstimatePackedPageBytes(
                    uniqueVertices.Count,
                    tentativeIndices,
                    tentativeClusters,
                    tentativeParts,
                    tentativeMaxMip,
                    floatUvVertexCount + addedFloatUvVertices > 0);

                bool exceedsBudget = partCount > 0 && estimatedBytes > targetBytes;
                bool exceedsPartLimit = partCount >= MaxPartPerPage;
                if (exceedsBudget || exceedsPartLimit)
                {
                    for (int i = 0; i < addedVertices.Count; i++)
                        uniqueVertices.Remove(addedVertices[i]);
                    ranges.Add(new PageRange { startPart = startPart, partCount = partCount });
                    startPart = partIndex;
                    partCount = 0;
                    clusterCount = 0;
                    indexCount = 0;
                    maxMip = 0;
                    floatUvVertexCount = 0;
                    uniqueVertices.Clear();
                    continue;
                }

                floatUvVertexCount += addedFloatUvVertices;
                partCount = tentativeParts;
                clusterCount = tentativeClusters;
                indexCount = tentativeIndices;
                maxMip = tentativeMaxMip;
                partIndex++;
            }

            if (partCount > 0)
                ranges.Add(new PageRange { startPart = startPart, partCount = partCount });
            return ranges;
        }

        static bool SameBuildGroup(BuildPart a, BuildPart b) =>
            a != null && b != null &&
            a.subMeshId == b.subMeshId &&
            a.groupOrdinal == b.groupOrdinal;

        static bool RangeContainsRoot(PageRange range, List<BuildPart> buildParts)
        {
            int end = Mathf.Min(buildParts.Count, range.startPart + range.partCount);
            for (int i = range.startPart; i < end; i++)
            {
                if (buildParts[i].root)
                    return true;
            }
            return false;
        }

        static int FindGroupPreservingSplit(PageRange range, List<BuildPart> buildParts)
        {
            int midpoint = Mathf.Clamp(range.partCount / 2, 1, range.partCount - 1);
            for (int distance = 0; distance < range.partCount; distance++)
            {
                int left = midpoint - distance;
                if (left > 0 && left < range.partCount &&
                    !SameBuildGroup(
                        buildParts[range.startPart + left - 1],
                        buildParts[range.startPart + left]))
                {
                    return left;
                }

                int right = midpoint + distance;
                if (right > 0 && right < range.partCount && right != left &&
                    !SameBuildGroup(
                        buildParts[range.startPart + right - 1],
                        buildParts[range.startPart + right]))
                {
                    return right;
                }
            }

            // One indivisible ClusterGroup can still exceed the real encoded size because of
            // conservative metadata/alignment estimates. Split Parts only as a last resort.
            return midpoint;
        }

        static PageLocalityAudit AuditPageLocality(
            List<BuildPart> buildParts,
            List<PageRange> pageRanges,
            List<NaniteSubMesh> subMeshes,
            List<BuiltPage> builtPages)
        {
            var audit = new PageLocalityAudit();
            var pagesPerGroup = new Dictionary<BuildGroupKey, HashSet<int>>();
            var globalMipVertices = new HashSet<ulong>();
            int pageCount = Mathf.Min(pageRanges.Count, builtPages.Count);
            audit.pageCount = pageCount;

            for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                PageRange range = pageRanges[pageIndex];
                BuiltPage builtPage = builtPages[pageIndex];
                if (builtPage.root)
                    audit.rootPageCount++;
                float fill = builtPage.stats.blobBytes / (float)TargetPageBytes;
                audit.minFill = Mathf.Min(audit.minFill, fill);
                audit.maxFill = Mathf.Max(audit.maxFill, fill);
                audit.fillSum += fill;
                if (builtPage.minMip != builtPage.maxMip)
                    audit.mixedMipPages++;

                var pageSubMeshes = new HashSet<int>();
                var pageMipVertices = new HashSet<ulong>();
                int endPart = Mathf.Min(buildParts.Count, range.startPart + range.partCount);
                for (int partIndex = range.startPart; partIndex < endPart; partIndex++)
                {
                    BuildPart part = buildParts[partIndex];
                    pageSubMeshes.Add(part.subMeshId);
                    var groupKey = new BuildGroupKey(part.subMeshId, part.groupOrdinal);
                    if (!pagesPerGroup.TryGetValue(groupKey, out HashSet<int> groupPages))
                    {
                        groupPages = new HashSet<int>();
                        pagesPerGroup.Add(groupKey, groupPages);
                    }
                    groupPages.Add(pageIndex);

                    List<Cluster> clusters = subMeshes[part.subMeshId].clusterList;
                    for (int clusterOffset = 0; clusterOffset < part.clusterIndices.Count; clusterOffset++)
                    {
                        Cluster cluster = clusters[part.clusterIndices[clusterOffset]];
                        for (int index = 0; index < cluster.indices.Length; index++)
                        {
                            ulong vertexKey = ((ulong)(uint)Mathf.Max(0, part.mip) << 32) |
                                              (uint)cluster.indices[index];
                            pageMipVertices.Add(vertexKey);
                        }
                    }
                }

                audit.maxSubMeshesPerPage = Mathf.Max(audit.maxSubMeshesPerPage, pageSubMeshes.Count);
                audit.pageVertexIncidences += pageMipVertices.Count;
                foreach (ulong vertexKey in pageMipVertices)
                    globalMipVertices.Add(vertexKey);
            }

            audit.uniqueMipVertices = globalMipVertices.Count;
            audit.groupCount = pagesPerGroup.Count;
            foreach (KeyValuePair<BuildGroupKey, HashSet<int>> pair in pagesPerGroup)
            {
                int groupPageCount = pair.Value.Count;
                audit.maxPagesPerGroup = Mathf.Max(audit.maxPagesPerGroup, groupPageCount);
                if (groupPageCount > 1)
                    audit.splitGroupCount++;
            }
            if (pageCount == 0)
                audit.minFill = 0f;
            return audit;
        }

        static int EstimatePackedPageBytes(
            int vertexCount,
            int indexCount,
            int clusterCount,
            int partCount,
            int maxMip,
            bool floatUv)
        {
            long bytes = NanitePageBinaryCodec.HeaderSize;
            bytes += (long)vertexCount * (floatUv ? 24 : 20);
            bytes += (long)indexCount * (vertexCount <= ushort.MaxValue ? 2 : 4);
            bytes += (long)clusterCount * NanitePageBinaryCodec.ClusterRecordBytes;
            bytes += (long)partCount * 48;
            int conservativeBvhNodes = partCount * 2 + maxMip + 8;
            bytes += (long)conservativeBvhNodes * 60;
            bytes += (long)(maxMip + 1) * sizeof(int);
            bytes += 32; // Section alignment slack.
            return bytes >= int.MaxValue ? int.MaxValue : (int)bytes;
        }

        static bool VertexRequiresFloatUv(int vertexIndex, Vector2[] uvs)
        {
            if (uvs == null || (uint)vertexIndex >= (uint)uvs.Length)
                return false;
            Vector2 uv = uvs[vertexIndex];
            return NanitePageBinaryCodec.UvRequiresFloatStorage(uv.x, uv.y);
        }

        static NaniteMeshPage BuildPage(
            PageRange range,
            List<BuildPart> buildParts,
            List<NaniteSubMesh> subMeshList,
            Vector3[] vertices,
            Vector3[] normals,
            Vector4[] tangents,
            Vector2[] uvs)
        {
            var page = ScriptableObject.CreateInstance<NaniteMeshPage>();
            page.parts = new NaniteMeshPart[range.partCount];
            var pageClusters = new List<NaniteCluster>(range.partCount * MaxClusterPerPart);
            var tempIndices = new List<int>();
            var pageMips = new List<int>();

            for (int localPart = 0; localPart < range.partCount; localPart++)
            {
                if ((localPart & 15) == 0)
                    ThrowIfCancelled();

                BuildPart buildPart = buildParts[range.startPart + localPart];
                List<int> partClusterIndices = buildPart.clusterIndices;
                List<Cluster> clusters = subMeshList[buildPart.subMeshId].clusterList;
                var part = new NaniteMeshPart
                {
                    clusterStart = pageClusters.Count,
                    clusterCount = partClusterIndices.Count,
                    mipLevel = buildPart.mip
                };

                float maxParentError = 0f;
                for (int c = 0; c < partClusterIndices.Count; c++)
                {
                    Cluster cluster = clusters[partClusterIndices[c]];
                    pageMips.Add(cluster.mip);
                    var naniteCluster = new NaniteCluster
                    {
                        indiceIndex = tempIndices.Count,
                        indiceCount = cluster.indices.Length,
                        parentError = cluster.parent.error,
                        parentSphere = SphereToVector4(cluster.parent),
                        selfError = cluster.self.error,
                        selfSphere = SphereToVector4(cluster.self),
                        geometrySphere = SphereToVector4(cluster.geometry),
                        longestEdge = cluster.longestEdge,
                        packedCone = cluster.packedCone,
                        subMeshId = buildPart.subMeshId,
                        partIndex = localPart,
                        vertexOffset = 0
                    };
                    maxParentError = Mathf.Max(maxParentError, naniteCluster.parentError);
                    tempIndices.AddRange(cluster.indices);
                    pageClusters.Add(naniteCluster);
                }

                LODBounds partBounds = NaniteMeshBuilder.MergeClusterGeometryBounds(
                    clusters,
                    partClusterIndices);
                part.selfSphere = SphereToVector4(partBounds);
                part.parentSphere = MergeParentSpheres(clusters, partClusterIndices);
                part.maxParentLodError = maxParentError;
                page.parts[localPart] = part;
            }

            page.clusterArray = pageClusters.ToArray();
            page.indiceArray = tempIndices.ToArray();
            page.clusterMip = pageMips.ToArray();
            BuildPageBvh(page);
            PackPageVertices(page, vertices, normals, tangents, uvs, out _);
            return page;
        }

        static void GetPageMipRange(NaniteMeshPage page, out int minMip, out int maxMip)
        {
            minMip = int.MaxValue;
            maxMip = 0;
            for (int i = 0; i < page.parts.Length; i++)
            {
                minMip = Mathf.Min(minMip, page.parts[i].mipLevel);
                maxMip = Mathf.Max(maxMip, page.parts[i].mipLevel);
            }
            if (minMip == int.MaxValue)
                minMip = 0;
        }

        static Vector4 MergePartBounds(NaniteMeshPart[] parts)
        {
            if (parts == null || parts.Length == 0)
                return Vector4.zero;
            Vector4 bounds = parts[0].selfSphere;
            for (int i = 1; i < parts.Length; i++)
                bounds = MergeSphere(bounds, parts[i].selfSphere);
            return bounds;
        }

        static HashSet<string> CollectReferencedPageAssetPaths(string meshPath)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            NaniteMesh existingMesh = AssetDatabase.LoadAssetAtPath<NaniteMesh>(meshPath);
            if (existingMesh?.pageArray == null)
                return paths;

            for (int i = 0; i < existingMesh.pageArray.Length; i++)
            {
                NaniteMeshPage page = existingMesh.pageArray[i];
                if (page == null)
                    continue;
                string pagePath = AssetDatabase.GetAssetPath(page);
                if (!string.IsNullOrEmpty(pagePath) &&
                    !string.Equals(pagePath, meshPath, StringComparison.OrdinalIgnoreCase))
                    paths.Add(pagePath);
                if (page.BinaryPayload != null)
                {
                    string binaryPath = AssetDatabase.GetAssetPath(page.BinaryPayload);
                    if (!string.IsNullOrEmpty(binaryPath))
                        paths.Add(binaryPath);
                }
                if (page.HasStreamingPayload)
                    paths.Add("Assets/StreamingAssets/" + page.StreamingRelativePath);
            }
            return paths;
        }

        static NaniteMesh SaveNaniteMeshAsset(NaniteMesh source, string assetPath)
        {
            NaniteMesh existing = AssetDatabase.LoadAssetAtPath<NaniteMesh>(assetPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(source, assetPath);
                AddPageSubAssets(source, source.pageArray);
                EditorUtility.SetDirty(source);
                return source;
            }

            RemovePageSubAssets(assetPath);
            AddPageSubAssets(existing, source.pageArray);
            EditorUtility.CopySerialized(source, existing);
            EditorUtility.SetDirty(existing);
            UnityEngine.Object.DestroyImmediate(source);
            return existing;
        }

        static void AddPageSubAssets(NaniteMesh owner, NaniteMeshPage[] pages)
        {
            if (owner == null || pages == null)
                return;
            for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
            {
                NaniteMeshPage page = pages[pageIndex];
                if (page == null)
                    continue;
                page.name = $"{owner.name}_Page_{pageIndex:D4}";
                AssetDatabase.AddObjectToAsset(page, owner);
                EditorUtility.SetDirty(page);
            }
        }

        static void RemovePageSubAssets(string assetPath)
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            for (int assetIndex = 0; assetIndex < assets.Length; assetIndex++)
            {
                if (assets[assetIndex] is NaniteMeshPage page)
                    UnityEngine.Object.DestroyImmediate(page, true);
            }
        }

        static void DeleteStaleGeneratedAssets(HashSet<string> previousAssets, HashSet<string> usedAssets)
        {
            foreach (string path in previousAssets)
            {
                if (usedAssets.Contains(path))
                    continue;
                AssetDatabase.DeleteAsset(path);
            }
        }

        static void WriteBinaryAsset(string assetPath, byte[] bytes)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                throw new DirectoryNotFoundException("Cannot resolve the Unity project root.");

            string normalizedAssetPath = assetPath.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(projectRoot, normalizedAssetPath));
            string fullProjectRoot = Path.GetFullPath(projectRoot + Path.DirectorySeparatorChar);
            if (!fullPath.StartsWith(fullProjectRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Binary Page path escaped the Unity project: {assetPath}");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? fullProjectRoot);
            File.WriteAllBytes(fullPath, bytes);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        static string BuildStreamingRelativePath(Mesh mesh, string meshAssetPath)
        {
            string guid;
            long localId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out guid, out localId) ||
                string.IsNullOrEmpty(guid))
            {
                guid = AssetDatabase.AssetPathToGUID(meshAssetPath);
                localId = mesh != null ? mesh.GetInstanceID() : 0;
            }
            if (string.IsNullOrEmpty(guid))
                guid = Hash128.Compute(meshAssetPath ?? "NaniteMesh").ToString();

            string local = unchecked((ulong)localId).ToString("X16");
            return $"NanitePages/{guid}_{local}.npages";
        }

        static void UpdateProgress(string message, float progress)
        {
            ThrowIfCancelled();

            string text = $"{message}  (按 F8 取消)";
            if (EditorUtility.DisplayCancelableProgressBar("Nanite", text, Mathf.Clamp01(progress)))
                throw new OperationCanceledException("Bake cancelled by user.");

            ThrowIfCancelled();
        }

        static List<BuildPart> BuildPartsFromGroups(List<NaniteSubMesh> subMeshList)
        {
            var buildParts = new List<BuildPart>();

            for (int subMeshIndex = 0; subMeshIndex < subMeshList.Count; subMeshIndex++)
            {
                var subMesh = subMeshList[subMeshIndex];
                var clusters = subMesh.clusterList;
                var groups = subMesh.clusterGroupList;

                for (int g = 0; g < groups.Count; g++)
                {
                    var group = groups[g];
                    bool isRootGroup = group.isRootSet ||
                                       group.maxParentLodError >= float.MaxValue * 0.5f;
                    // A Part is a coarse culling/streaming unit and must not straddle two
                    // independent DAG parent groups. Crossing this boundary weakens both
                    // parent-sphere coherence and Page locality.
                    BuildPart currentPart = null;
                    for (int c = 0; c < group.children.Count; c++)
                    {
                        int clusterIndex = group.children[c];
                        int clusterMip = clusters[clusterIndex].mip;

                        if (currentPart == null
                            || currentPart.clusterIndices.Count >= MaxClusterPerPart
                            || currentPart.mip != clusterMip)
                        {
                            currentPart = new BuildPart
                            {
                                mip = clusterMip,
                                subMeshId = subMeshIndex,
                                groupOrdinal = g,
                                root = isRootGroup
                            };
                            buildParts.Add(currentPart);
                        }

                        currentPart.clusterIndices.Add(clusterIndex);
                    }
                }
            }

            for (int i = 0; i < buildParts.Count; i++)
            {
                BuildPart part = buildParts[i];
                LODBounds bounds = NaniteMeshBuilder.MergeClusterGeometryBounds(
                    subMeshList[part.subMeshId].clusterList,
                    part.clusterIndices);
                part.center = bounds.center;
                part.radius = bounds.radius;
            }

            return buildParts;
        }

        static HierarchyBuildResult BuildHierarchyMetadata(
            List<NaniteSubMesh> subMeshes,
            List<BuildPart> buildParts,
            List<PageRange> pageRanges,
            IReadOnlyList<BuiltPage> builtPages)
        {
            if (subMeshes == null || buildParts == null || pageRanges == null || builtPages == null)
                throw new ArgumentNullException("Cross-Page hierarchy inputs must not be null.");
            if (pageRanges.Count != builtPages.Count)
                throw new InvalidDataException(
                    $"Hierarchy Page mapping mismatch: ranges={pageRanges.Count}, pages={builtPages.Count}.");

            var sourceRefs = new NaniteHierarchyClusterRef[subMeshes.Count][];
            for (int subMeshIndex = 0; subMeshIndex < subMeshes.Count; subMeshIndex++)
            {
                int clusterCount = subMeshes[subMeshIndex]?.clusterList?.Count ?? 0;
                sourceRefs[subMeshIndex] = new NaniteHierarchyClusterRef[clusterCount];
                for (int clusterIndex = 0; clusterIndex < clusterCount; clusterIndex++)
                {
                    sourceRefs[subMeshIndex][clusterIndex] = new NaniteHierarchyClusterRef
                    {
                        geometryClusterIndex = -1,
                        pageIndex = -1,
                        pageClusterIndex = -1,
                        refinementGroupIndex = -1
                    };
                }
            }

            int geometryClusterBase = 0;
            for (int pageIndex = 0; pageIndex < pageRanges.Count; pageIndex++)
            {
                PageRange range = pageRanges[pageIndex];
                int pageClusterIndex = 0;
                int endPart = Mathf.Min(buildParts.Count, range.startPart + range.partCount);
                for (int partIndex = range.startPart; partIndex < endPart; partIndex++)
                {
                    BuildPart part = buildParts[partIndex];
                    if ((uint)part.subMeshId >= (uint)sourceRefs.Length)
                        throw new InvalidDataException($"Hierarchy Part {partIndex} has invalid SubMesh {part.subMeshId}.");

                    NaniteHierarchyClusterRef[] subMeshRefs = sourceRefs[part.subMeshId];
                    for (int clusterOffset = 0; clusterOffset < part.clusterIndices.Count; clusterOffset++)
                    {
                        int sourceClusterIndex = part.clusterIndices[clusterOffset];
                        if ((uint)sourceClusterIndex >= (uint)subMeshRefs.Length)
                            throw new InvalidDataException(
                                $"Hierarchy Part {partIndex} has invalid Cluster {sourceClusterIndex}.");
                        if (subMeshRefs[sourceClusterIndex].geometryClusterIndex >= 0)
                            throw new InvalidDataException(
                                $"Hierarchy Cluster {part.subMeshId}:{sourceClusterIndex} was packed more than once.");

                        subMeshRefs[sourceClusterIndex] = new NaniteHierarchyClusterRef
                        {
                            geometryClusterIndex = geometryClusterBase + pageClusterIndex,
                            pageIndex = pageIndex,
                            pageClusterIndex = pageClusterIndex,
                            refinementGroupIndex = -1
                        };
                        pageClusterIndex++;
                    }
                }

                int encodedClusterCount = builtPages[pageIndex]?.clusterCount ?? 0;
                if (pageClusterIndex != encodedClusterCount)
                {
                    throw new InvalidDataException(
                        $"Hierarchy Page {pageIndex} Cluster order mismatch: mapped={pageClusterIndex}, " +
                        $"encoded={encodedClusterCount}.");
                }
                geometryClusterBase += pageClusterIndex;
            }

            int groupCount = 0;
            for (int subMeshIndex = 0; subMeshIndex < subMeshes.Count; subMeshIndex++)
                groupCount += subMeshes[subMeshIndex]?.clusterGroupList?.Count ?? 0;

            var producerGroupByGeometryCluster = new int[geometryClusterBase];
            var consumerGroupByGeometryCluster = new int[geometryClusterBase];
            for (int i = 0; i < producerGroupByGeometryCluster.Length; i++)
            {
                producerGroupByGeometryCluster[i] = -1;
                consumerGroupByGeometryCluster[i] = -1;
            }

            int globalGroupIndex = 0;
            for (int subMeshIndex = 0; subMeshIndex < subMeshes.Count; subMeshIndex++)
            {
                NaniteSubMesh subMesh = subMeshes[subMeshIndex];
                if (subMesh?.clusterGroupList == null)
                    continue;

                for (int localGroupIndex = 0;
                     localGroupIndex < subMesh.clusterGroupList.Count;
                     localGroupIndex++, globalGroupIndex++)
                {
                    ClusterGroup group = subMesh.clusterGroupList[localGroupIndex];
                    if (group?.children != null)
                    {
                        for (int childIndex = 0; childIndex < group.children.Count; childIndex++)
                        {
                            NaniteHierarchyClusterRef childRef = ResolveSourceClusterRef(
                                sourceRefs,
                                subMeshIndex,
                                group.children[childIndex],
                                $"group {subMeshIndex}:{localGroupIndex} child");
                            int previousConsumer = consumerGroupByGeometryCluster[childRef.geometryClusterIndex];
                            if (previousConsumer >= 0 && previousConsumer != globalGroupIndex)
                            {
                                throw new InvalidDataException(
                                    $"Geometry Cluster {childRef.geometryClusterIndex} has two hierarchy consumers: " +
                                    $"{previousConsumer} and {globalGroupIndex}.");
                            }
                            consumerGroupByGeometryCluster[childRef.geometryClusterIndex] = globalGroupIndex;
                        }
                    }
                    if (group?.parents == null)
                        continue;

                    for (int parentIndex = 0; parentIndex < group.parents.Count; parentIndex++)
                    {
                        NaniteHierarchyClusterRef parentRef = ResolveSourceClusterRef(
                            sourceRefs,
                            subMeshIndex,
                            group.parents[parentIndex],
                            $"group {subMeshIndex}:{localGroupIndex} parent");
                        int previousProducer = producerGroupByGeometryCluster[parentRef.geometryClusterIndex];
                        if (previousProducer >= 0 && previousProducer != globalGroupIndex)
                        {
                            throw new InvalidDataException(
                                $"Geometry Cluster {parentRef.geometryClusterIndex} has two refinement producers: " +
                                $"{previousProducer} and {globalGroupIndex}.");
                        }
                        producerGroupByGeometryCluster[parentRef.geometryClusterIndex] = globalGroupIndex;
                    }
                }
            }
            if (globalGroupIndex != groupCount)
                throw new InvalidDataException($"Hierarchy group count mismatch: mapped={globalGroupIndex}, expected={groupCount}.");

            for (int geometryClusterIndex = 0;
                 geometryClusterIndex < geometryClusterBase;
                 geometryClusterIndex++)
            {
                if (consumerGroupByGeometryCluster[geometryClusterIndex] < 0)
                    throw new InvalidDataException(
                        $"Geometry Cluster {geometryClusterIndex} is not owned by a hierarchy group.");
            }

            var groups = new NaniteHierarchyGroup[groupCount];
            var flatRefs = new List<NaniteHierarchyClusterRef>(geometryClusterBase * 2);
            var rootGroups = new List<int>();
            globalGroupIndex = 0;
            for (int subMeshIndex = 0; subMeshIndex < subMeshes.Count; subMeshIndex++)
            {
                NaniteSubMesh subMesh = subMeshes[subMeshIndex];
                if (subMesh?.clusterGroupList == null)
                    continue;

                for (int localGroupIndex = 0;
                     localGroupIndex < subMesh.clusterGroupList.Count;
                     localGroupIndex++, globalGroupIndex++)
                {
                    ClusterGroup sourceGroup = subMesh.clusterGroupList[localGroupIndex];
                    int fineStart = flatRefs.Count;
                    AppendHierarchyRefs(
                        flatRefs,
                        sourceRefs,
                        producerGroupByGeometryCluster,
                        subMeshIndex,
                        sourceGroup?.children,
                        $"group {subMeshIndex}:{localGroupIndex} child");
                    int fineCount = flatRefs.Count - fineStart;

                    int coarseStart = flatRefs.Count;
                    AppendHierarchyRefs(
                        flatRefs,
                        sourceRefs,
                        producerGroupByGeometryCluster,
                        subMeshIndex,
                        sourceGroup?.parents,
                        $"group {subMeshIndex}:{localGroupIndex} parent");
                    int coarseCount = flatRefs.Count - coarseStart;

                    bool rootSet = coarseCount == 0 &&
                                   sourceGroup != null &&
                                   (sourceGroup.isRootSet ||
                                    sourceGroup.maxParentLodError >= float.MaxValue * 0.5f);
                    if (fineCount <= 0 || (!rootSet && coarseCount <= 0))
                    {
                        throw new InvalidDataException(
                            $"Hierarchy group {subMeshIndex}:{localGroupIndex} is not a valid " +
                            $"refinement edge (fine={fineCount}, coarse={coarseCount}, root={rootSet}).");
                    }

                    if (rootSet)
                    {
                        for (int refIndex = fineStart; refIndex < fineStart + fineCount; refIndex++)
                        {
                            NaniteHierarchyClusterRef rootRef = flatRefs[refIndex];
                            if ((uint)rootRef.pageIndex >= (uint)builtPages.Count ||
                                !builtPages[rootRef.pageIndex].root)
                            {
                                throw new InvalidDataException(
                                    $"Root hierarchy Cluster {rootRef.geometryClusterIndex} is on non-root " +
                                    $"Page {rootRef.pageIndex}.");
                            }
                        }
                        rootGroups.Add(globalGroupIndex);
                    }

                    groups[globalGroupIndex] = new NaniteHierarchyGroup
                    {
                        boundingSphere = sourceGroup != null
                            ? new Vector4(
                                sourceGroup.boundsCenter.x,
                                sourceGroup.boundsCenter.y,
                                sourceGroup.boundsCenter.z,
                                sourceGroup.radius)
                            : Vector4.zero,
                        minLodError = sourceGroup?.minLodError ?? 0f,
                        maxParentLodError = sourceGroup?.maxParentLodError ?? float.MaxValue,
                        fineClusterStart = fineStart,
                        fineClusterCount = fineCount,
                        coarseClusterStart = coarseStart,
                        coarseClusterCount = coarseCount,
                        mipLevel = sourceGroup?.mipLevel ?? 0,
                        flags = (rootSet ? NaniteHierarchyGroup.RootSetFlag : 0) |
                                (sourceGroup != null && sourceGroup.usesRobustUvGate
                                    ? NaniteHierarchyGroup.RobustUvGateFlag
                                    : 0)
                    };
                }
            }

            if (rootGroups.Count == 0)
                throw new InvalidDataException("Cross-Page hierarchy contains no permanently resident root set.");

            return new HierarchyBuildResult
            {
                groups = groups,
                clusterRefs = flatRefs.ToArray(),
                rootGroups = rootGroups.ToArray(),
                geometryClusterCount = geometryClusterBase
            };
        }

        static void AppendHierarchyRefs(
            List<NaniteHierarchyClusterRef> destination,
            NaniteHierarchyClusterRef[][] sourceRefs,
            int[] producerGroupByGeometryCluster,
            int subMeshIndex,
            IReadOnlyList<int> sourceClusterIndices,
            string context)
        {
            if (sourceClusterIndices == null)
                return;

            var uniqueGeometryClusters = new HashSet<int>();
            for (int index = 0; index < sourceClusterIndices.Count; index++)
            {
                NaniteHierarchyClusterRef clusterRef = ResolveSourceClusterRef(
                    sourceRefs,
                    subMeshIndex,
                    sourceClusterIndices[index],
                    context);
                if (!uniqueGeometryClusters.Add(clusterRef.geometryClusterIndex))
                {
                    throw new InvalidDataException(
                        $"Duplicate Geometry Cluster {clusterRef.geometryClusterIndex} in {context}. " +
                        "A hierarchy edge must be a set; duplicate refs can schedule the same producer twice.");
                }
                clusterRef.refinementGroupIndex =
                    producerGroupByGeometryCluster[clusterRef.geometryClusterIndex];
                destination.Add(clusterRef);
            }
        }

        static NaniteHierarchyClusterRef ResolveSourceClusterRef(
            NaniteHierarchyClusterRef[][] sourceRefs,
            int subMeshIndex,
            int sourceClusterIndex,
            string context)
        {
            if ((uint)subMeshIndex >= (uint)sourceRefs.Length ||
                (uint)sourceClusterIndex >= (uint)sourceRefs[subMeshIndex].Length)
            {
                throw new InvalidDataException(
                    $"Invalid {context} Cluster reference {subMeshIndex}:{sourceClusterIndex}.");
            }

            NaniteHierarchyClusterRef clusterRef = sourceRefs[subMeshIndex][sourceClusterIndex];
            if (clusterRef.geometryClusterIndex < 0 || clusterRef.pageIndex < 0 || clusterRef.pageClusterIndex < 0)
            {
                throw new InvalidDataException(
                    $"Unpacked {context} Cluster reference {subMeshIndex}:{sourceClusterIndex}.");
            }
            return clusterRef;
        }

        /// <summary>
        /// Keep spatial/group locality inside a mip, but prevent the highest/root mip from
        /// leaking into every packed Page. Coarse pages are emitted first and form the small
        /// permanently pinned root working set; fine pages can then stream independently.
        /// </summary>
        static List<BuildPart> OrderPartsForStreaming(List<BuildPart> source)
        {
            if (source == null || source.Count <= 1)
                return source ?? new List<BuildPart>();

            int maxMip = 0;
            for (int i = 0; i < source.Count; i++)
                maxMip = Mathf.Max(maxMip, source[i].mip);

            var ordered = new List<BuildPart>(source.Count);
            for (int mip = maxMip; mip >= 0; mip--)
            {
                var blocks = new List<StreamingGroupBlock>();
                var blockByGroup = new Dictionary<BuildGroupKey, StreamingGroupBlock>();
                for (int i = 0; i < source.Count; i++)
                {
                    BuildPart part = source[i];
                    if (part.mip != mip)
                        continue;
                    var key = new BuildGroupKey(part.subMeshId, part.groupOrdinal);
                    if (!blockByGroup.TryGetValue(key, out StreamingGroupBlock block))
                    {
                        block = new StreamingGroupBlock
                        {
                            subMeshId = part.subMeshId,
                            groupOrdinal = part.groupOrdinal,
                            sourceOrder = i
                        };
                        blockByGroup.Add(key, block);
                        blocks.Add(block);
                    }
                    block.parts.Add(part);
                }

                for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
                {
                    StreamingGroupBlock block = blocks[blockIndex];
                    BuildPart first = block.parts[0];
                    Vector4 sphere = new Vector4(first.center.x, first.center.y, first.center.z, first.radius);
                    for (int partIndex = 1; partIndex < block.parts.Count; partIndex++)
                    {
                        BuildPart part = block.parts[partIndex];
                        sphere = MergeSphere(
                            sphere,
                            new Vector4(part.center.x, part.center.y, part.center.z, part.radius));
                    }
                    block.center = new Vector3(sphere.x, sphere.y, sphere.z);
                }

                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int i = 0; i < blocks.Count; i++)
                {
                    min = Vector3.Min(min, blocks[i].center);
                    max = Vector3.Max(max, blocks[i].center);
                }

                var extent = max - min;
                if (extent.x < 1e-5f) extent.x = 1f;
                if (extent.y < 1e-5f) extent.y = 1f;
                if (extent.z < 1e-5f) extent.z = 1f;

                for (int i = 0; i < blocks.Count; i++)
                {
                    StreamingGroupBlock block = blocks[i];
                    Vector3 normalized = new Vector3(
                        Mathf.Clamp01((block.center.x - min.x) / extent.x),
                        Mathf.Clamp01((block.center.y - min.y) / extent.y),
                        Mathf.Clamp01((block.center.z - min.z) / extent.z));
                    block.morton = Morton3D(normalized.x, normalized.y, normalized.z);
                }

                blocks.Sort((a, b) =>
                {
                    int order = a.morton.CompareTo(b.morton);
                    if (order != 0)
                        return order;
                    order = a.subMeshId.CompareTo(b.subMeshId);
                    if (order != 0)
                        return order;
                    order = a.groupOrdinal.CompareTo(b.groupOrdinal);
                    return order != 0 ? order : a.sourceOrder.CompareTo(b.sourceOrder);
                });
                for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
                {
                    StreamingGroupBlock block = blocks[blockIndex];
                    for (int partIndex = 0; partIndex < block.parts.Count; partIndex++)
                        ordered.Add(block.parts[partIndex]);
                }
            }
            // The Page planner enforces a hard root/non-root boundary. Keep every terminal
            // group in a contiguous prefix even when different SubMeshes terminate at
            // different mip levels. This stable partition preserves the existing
            // mip/Morton/group ordering inside both classes.
            var rootPrefix = new List<BuildPart>(source.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].root)
                    rootPrefix.Add(ordered[i]);
            }
            for (int i = 0; i < ordered.Count; i++)
            {
                if (!ordered[i].root)
                    rootPrefix.Add(ordered[i]);
            }
            return rootPrefix;
        }

        static void PackPageVertices(
            NaniteMeshPage page,
            Vector3[] vertices,
            Vector3[] normals,
            Vector4[] tangents,
            Vector2[] uvs,
            out int vertexCount)
        {
            bool hasUv = uvs != null && uvs.Length == vertices.Length;
            bool hasTangents = tangents != null && tangents.Length == vertices.Length;
            var indexMap = new Dictionary<int, int>();
            var tempVerts = new List<Vector3>();
            var tempNormals = new List<Vector3>();
            var tempTangents = new List<Vector4>();
            var tempUvs = new List<Vector2>();

            for (int c = 0; c < page.clusterArray.Length; c++)
            {
                ref NaniteCluster cluster = ref page.clusterArray[c];
                int indexEnd = cluster.indiceIndex + cluster.indiceCount;

                for (int index = cluster.indiceIndex; index < indexEnd; index++)
                {
                    int vertIndex = page.indiceArray[index];
                    if (!indexMap.TryGetValue(vertIndex, out int newIndex))
                    {
                        newIndex = tempVerts.Count;
                        indexMap.Add(vertIndex, newIndex);
                        tempVerts.Add(vertices[vertIndex]);
                        tempNormals.Add(normals != null && vertIndex < normals.Length
                            ? normals[vertIndex]
                            : Vector3.up);
                        tempTangents.Add(hasTangents ? tangents[vertIndex] : new Vector4(1f, 0f, 0f, 1f));
                        tempUvs.Add(hasUv ? uvs[vertIndex] : Vector2.zero);
                    }

                    page.indiceArray[index] = newIndex;
                }

                cluster.vertexOffset = 0;
            }

            // Layout: position.xyz + uv.xy + normal.xyz + tangent.xyzw
            page.vertexStride = 12;
            page.vertexCount = tempVerts.Count;
            page.vertexData = new float[tempVerts.Count * page.vertexStride];

            for (int v = 0; v < tempVerts.Count; v++)
            {
                int o = v * page.vertexStride;
                page.vertexData[o + 0] = tempVerts[v].x;
                page.vertexData[o + 1] = tempVerts[v].y;
                page.vertexData[o + 2] = tempVerts[v].z;
                page.vertexData[o + 3] = tempUvs[v].x;
                page.vertexData[o + 4] = tempUvs[v].y;
                Vector3 n = tempNormals[v].sqrMagnitude > 1e-8f ? tempNormals[v].normalized : Vector3.up;
                page.vertexData[o + 5] = n.x;
                page.vertexData[o + 6] = n.y;
                page.vertexData[o + 7] = n.z;
                Vector4 t = tempTangents[v];
                Vector3 t3 = new Vector3(t.x, t.y, t.z);
                if (t3.sqrMagnitude < 1e-8f)
                    t3 = Vector3.right;
                t3.Normalize();
                page.vertexData[o + 8] = t3.x;
                page.vertexData[o + 9] = t3.y;
                page.vertexData[o + 10] = t3.z;
                page.vertexData[o + 11] = Mathf.Approximately(t.w, 0f) ? 1f : Mathf.Sign(t.w);
            }

            vertexCount = tempVerts.Count;
        }

        struct MortonPart
        {
            public uint morton;
            public int index;
        }

        static void BuildPageBvh(NaniteMeshPage page)
        {
            int partCount = page.parts != null ? page.parts.Length : 0;
            if (partCount == 0)
            {
                page.bvhNodes = System.Array.Empty<NaniteBvhNode>();
                page.mipBvhRoots = System.Array.Empty<int>();
                page.bvhRoot = -1;
                return;
            }

            int maxMip = 0;
            for (int i = 0; i < partCount; i++)
                maxMip = Mathf.Max(maxMip, page.parts[i].mipLevel);

            var perMipParts = new List<int>[maxMip + 1];
            page.mipBvhRoots = new int[maxMip + 1];
            for (int i = 0; i < page.mipBvhRoots.Length; i++)
                page.mipBvhRoots[i] = -1;

            for (int i = 0; i < partCount; i++)
            {
                int mip = Mathf.Max(0, page.parts[i].mipLevel);
                perMipParts[mip] ??= new List<int>();
                perMipParts[mip].Add(i);
            }

            var nodes = new List<NaniteBvhNode>(partCount * 2 + maxMip + 8);
            var lodRoots = new List<int>(maxMip + 1);

            for (int mip = 0; mip < perMipParts.Length; mip++)
            {
                var partIndices = perMipParts[mip];
                if (partIndices == null || partIndices.Count == 0)
                    continue;

                int root = BuildHierarchyFromPartIndices(page, partIndices, nodes);
                page.mipBvhRoots[mip] = root;
                lodRoots.Add(root);
            }

            if (lodRoots.Count == 0)
            {
                page.bvhNodes = System.Array.Empty<NaniteBvhNode>();
                page.bvhRoot = -1;
                return;
            }

            page.bvhRoot = lodRoots.Count == 1 ? lodRoots[0] : BuildHierarchyFromNodeIndices(lodRoots, nodes);
            page.bvhNodes = nodes.ToArray();
        }

        static int BuildHierarchyFromPartIndices(NaniteMeshPage page, List<int> partIndices, List<NaniteBvhNode> nodes)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < partIndices.Count; i++)
            {
                Vector4 s = page.parts[partIndices[i]].selfSphere;
                var c = new Vector3(s.x, s.y, s.z);
                min = Vector3.Min(min, c);
                max = Vector3.Max(max, c);
            }

            var ext = max - min;
            if (ext.x < 1e-5f) ext.x = 1f;
            if (ext.y < 1e-5f) ext.y = 1f;
            if (ext.z < 1e-5f) ext.z = 1f;

            var morton = new MortonPart[partIndices.Count];
            for (int i = 0; i < partIndices.Count; i++)
            {
                Vector4 s = page.parts[partIndices[i]].selfSphere;
                float nx = Mathf.Clamp01((s.x - min.x) / ext.x);
                float ny = Mathf.Clamp01((s.y - min.y) / ext.y);
                float nz = Mathf.Clamp01((s.z - min.z) / ext.z);
                morton[i] = new MortonPart
                {
                    index = partIndices[i],
                    morton = Morton3D(nx, ny, nz)
                };
            }

            System.Array.Sort(morton, (a, b) => a.morton.CompareTo(b.morton));

            var leafNodeIndices = new List<int>(morton.Length);
            for (int i = 0; i < morton.Length; i++)
            {
                int partIndex = morton[i].index;
                var part = page.parts[partIndex];
                Vector4 lodSphere = part.parentSphere.w > 0f ? part.parentSphere : part.selfSphere;

                nodes.Add(new NaniteBvhNode
                {
                    sphere = part.selfSphere,
                    lodSphere = lodSphere,
                    maxParentLodError = part.maxParentLodError,
                    child0 = -1,
                    child1 = -1,
                    child2 = -1,
                    child3 = -1,
                    childCount = 0,
                    partIndex = partIndex
                });
                leafNodeIndices.Add(nodes.Count - 1);
            }

            return BuildHierarchyFromNodeIndices(leafNodeIndices, nodes);
        }

        static int BuildHierarchyFromNodeIndices(List<int> nodeIndices, List<NaniteBvhNode> nodes)
        {
            var current = new List<int>(nodeIndices);
            while (current.Count > 1)
            {
                var next = new List<int>((current.Count + 3) / 4);
                for (int i = 0; i < current.Count; i += 4)
                {
                    int count = Mathf.Min(4, current.Count - i);
                    int c0 = current[i + 0];
                    int c1 = count > 1 ? current[i + 1] : -1;
                    int c2 = count > 2 ? current[i + 2] : -1;
                    int c3 = count > 3 ? current[i + 3] : -1;

                    next.Add(CreateInternalNode(nodes, c0, c1, c2, c3, count));
                }

                current = next;
            }

            return current[0];
        }

        static int CreateInternalNode(List<NaniteBvhNode> nodes, int c0, int c1, int c2, int c3, int count)
        {
            Vector4 sphere = nodes[c0].sphere;
            Vector4 lodSphere = nodes[c0].lodSphere;
            float maxParentError = nodes[c0].maxParentLodError;

            if (c1 >= 0)
            {
                sphere = MergeSphere(sphere, nodes[c1].sphere);
                lodSphere = MergeSphere(lodSphere, nodes[c1].lodSphere);
                maxParentError = Mathf.Max(maxParentError, nodes[c1].maxParentLodError);
            }

            if (c2 >= 0)
            {
                sphere = MergeSphere(sphere, nodes[c2].sphere);
                lodSphere = MergeSphere(lodSphere, nodes[c2].lodSphere);
                maxParentError = Mathf.Max(maxParentError, nodes[c2].maxParentLodError);
            }

            if (c3 >= 0)
            {
                sphere = MergeSphere(sphere, nodes[c3].sphere);
                lodSphere = MergeSphere(lodSphere, nodes[c3].lodSphere);
                maxParentError = Mathf.Max(maxParentError, nodes[c3].maxParentLodError);
            }

            nodes.Add(new NaniteBvhNode
            {
                sphere = sphere,
                lodSphere = lodSphere,
                maxParentLodError = maxParentError,
                child0 = c0,
                child1 = c1,
                child2 = c2,
                child3 = c3,
                childCount = count,
                partIndex = -1
            });

            return nodes.Count - 1;
        }

        static Vector4 MergeParentSpheres(List<Cluster> clusters, List<int> group)
        {
            var centers = new Vector3[group.Count];
            var radii = new float[group.Count];

            for (int j = 0; j < group.Count; j++)
            {
                var b = clusters[group[j]].parent;
                centers[j] = b.center;
                radii[j] = b.radius;
            }

            var merged = MeshOptimizerNative.NativeComputeSphereBounds(
                centers,
                (UIntPtr)(uint)centers.Length,
                (UIntPtr)(uint)(sizeof(float) * 3),
                radii,
                (UIntPtr)(uint)sizeof(float));

            return new Vector4(merged.centerX, merged.centerY, merged.centerZ, merged.radius);
        }

        static Vector4 MergeSphere(Vector4 a, Vector4 b)
        {
            var ca = new Vector3(a.x, a.y, a.z);
            var cb = new Vector3(b.x, b.y, b.z);
            float ra = a.w;
            float rb = b.w;

            var d = cb - ca;
            float dist = d.magnitude;

            if (ra >= dist + rb)
                return a;
            if (rb >= dist + ra)
                return b;

            if (dist < 1e-6f)
            {
                float r = Mathf.Max(ra, rb);
                return new Vector4(ca.x, ca.y, ca.z, r);
            }

            float newR = (dist + ra + rb) * 0.5f;
            Vector3 newC = ca + d * ((newR - ra) / dist);
            return new Vector4(newC.x, newC.y, newC.z, newR);
        }

        static uint Morton3D(float x, float y, float z)
        {
            uint xx = (uint)Mathf.Clamp(Mathf.RoundToInt(x * 1023f), 0, 1023);
            uint yy = (uint)Mathf.Clamp(Mathf.RoundToInt(y * 1023f), 0, 1023);
            uint zz = (uint)Mathf.Clamp(Mathf.RoundToInt(z * 1023f), 0, 1023);
            return (ExpandBits(xx) << 2) | (ExpandBits(yy) << 1) | ExpandBits(zz);
        }

        static uint ExpandBits(uint v)
        {
            v &= 0x000003ffu;
            v = (v | (v << 16)) & 0x030000ffu;
            v = (v | (v << 8)) & 0x0300f00fu;
            v = (v | (v << 4)) & 0x030c30c3u;
            v = (v | (v << 2)) & 0x09249249u;
            return v;
        }

        static Vector4 SphereToVector4(LODBounds bounds) =>
            new Vector4(bounds.center.x, bounds.center.y, bounds.center.z, bounds.radius);
    }
}
#endif
