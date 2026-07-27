using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Nanite
{
    /// <summary>
    /// GPU culling 第一版后端：Part 粗剔除 + Cluster 精剔除，输出可见 (page, cluster) 列表。
    /// 说明：这是进入光栅化前的 Compute 基线，不含 persistent threads/MPMC 队列。
    /// </summary>
    public sealed class NaniteGpuCullingBackend : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct GpuPartData
        {
            public Vector4 selfSphere;
            public Vector4 parentSphere;
            public float maxParentError;
            public int clusterStart;
            public int clusterCount;
            public int instanceIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuClusterData
        {
            public Vector4 selfSphere;
            public Vector4 parentSphere;
            public float selfError;
            public float parentError;
            public int partIndex;
            public int pageIndex;
            public int clusterIndex;
            public int instanceIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GpuInstanceData
        {
            public Matrix4x4 localToWorld;
            public Vector4 bounds;
            public float maxScale;
            public float lodErrorPixels;
            public uint partOffset;
            public uint partCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GpuVisibleRef
        {
            public uint instanceIndex;
            public uint pageIndex;
            public uint clusterIndex;
        }

        const int kThreadGroupSize = 64;

        ComputeShader shader;
        int kernelPartCull = -1;
        int kernelClusterCull = -1;

        ComputeBuffer partsBuffer;
        ComputeBuffer clustersBuffer;
        ComputeBuffer partVisibleBuffer;
        ComputeBuffer visibleClusterAppendBuffer;
        ComputeBuffer visibleCountBuffer;
        ComputeBuffer clusterCandidateBuffer;
        ComputeBuffer instanceDataBuffer;
        ComputeBuffer instanceVisibleBuffer;
        ComputeBuffer fallbackUintBuffer;
        ComputeBuffer fallbackCullStatsBuffer;
        ComputeBuffer fallbackVirtualRefBuffer;
        ComputeBuffer fallbackVisiblePartAppendBuffer;
        ComputeBuffer fallbackVisibleDrawAppendBuffer;
        RenderTexture fallbackHzbTexture;

        int partCount;
        int clusterCount;
        int clusterCandidateCount;
        NaniteMesh sourceMesh;
        GpuPartData[] partDataCpu;
        uint[] partVisibleCpu;
        uint[] clusterCandidatesCpu;
        int[] pagePartBase;
        int[] pagePartCount;
        readonly List<int> bvhStack = new List<int>(256);

        readonly Vector4[] frustumPlanes = new Vector4[6];
        uint[] visibleCountCpu = new uint[1];
        readonly GpuInstanceData[] singleInstanceData = new GpuInstanceData[1];

        public bool IsReady =>
            shader != null &&
            kernelPartCull >= 0 &&
            kernelClusterCull >= 0 &&
            partsBuffer != null &&
            clustersBuffer != null &&
            partVisibleBuffer != null &&
            visibleClusterAppendBuffer != null &&
            visibleCountBuffer != null &&
            clusterCandidateBuffer != null &&
            instanceDataBuffer != null &&
            instanceVisibleBuffer != null &&
            fallbackVisiblePartAppendBuffer != null &&
            fallbackVisibleDrawAppendBuffer != null;

        public void Dispose()
        {
            ReleaseBuffers();
            shader = null;
            kernelPartCull = -1;
            kernelClusterCull = -1;
            partCount = 0;
            clusterCount = 0;
            clusterCandidateCount = 0;
            ReleaseFallbackHzbTexture();
            sourceMesh = null;
            partDataCpu = null;
            partVisibleCpu = null;
            clusterCandidatesCpu = null;
            pagePartBase = null;
            pagePartCount = null;
        }

        public bool Initialize(NaniteMesh mesh, ComputeShader cullingShader)
        {
            Dispose();
            if (mesh == null || mesh.pageArray == null || cullingShader == null)
                return false;

            shader = cullingShader;
            if (!TryFindKernels())
            {
                Dispose();
                return false;
            }

            sourceMesh = mesh;
            BuildGpuData(mesh, out var parts, out var clusters, out pagePartBase, out pagePartCount);
            partCount = parts.Count;
            clusterCount = clusters.Count;
            if (partCount == 0 || clusterCount == 0)
            {
                Dispose();
                return false;
            }

            partsBuffer = new ComputeBuffer(partCount, Marshal.SizeOf<GpuPartData>());
            clustersBuffer = new ComputeBuffer(clusterCount, Marshal.SizeOf<GpuClusterData>());
            partVisibleBuffer = new ComputeBuffer(partCount, sizeof(uint));
            visibleClusterAppendBuffer = new ComputeBuffer(clusterCount, Marshal.SizeOf<GpuVisibleRef>(), ComputeBufferType.Append);
            visibleCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
            clusterCandidateBuffer = new ComputeBuffer(clusterCount, sizeof(uint));
            instanceDataBuffer = new ComputeBuffer(1, Marshal.SizeOf<GpuInstanceData>());
            instanceVisibleBuffer = new ComputeBuffer(1, sizeof(uint));
            fallbackVisiblePartAppendBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Append);
            fallbackVisibleDrawAppendBuffer = new ComputeBuffer(1, sizeof(uint) * 3, ComputeBufferType.Append);

            partsBuffer.SetData(parts);
            clustersBuffer.SetData(clusters);
            visibleClusterAppendBuffer.SetCounterValue(0);
            fallbackVisiblePartAppendBuffer.SetCounterValue(0);
            fallbackVisibleDrawAppendBuffer.SetCounterValue(0);
            instanceVisibleBuffer.SetData(new uint[] { 1u });
            partDataCpu = parts.ToArray();
            partVisibleCpu = new uint[partCount];
            clusterCandidatesCpu = new uint[clusterCount];

            return true;
        }

        public bool Run(
            Camera camera,
            float lodErrorPixels,
            Matrix4x4 localToWorld,
            float maxScale,
            List<NaniteVisibleClusterRef> output,
            out NaniteCullingStats stats)
        {
            return Run(
                camera,
                lodErrorPixels,
                localToWorld,
                maxScale,
                output,
                out stats,
                null,
                0,
                false);
        }

        public bool Run(
            Camera camera,
            float lodErrorPixels,
            Matrix4x4 localToWorld,
            float maxScale,
            List<NaniteVisibleClusterRef> output,
            out NaniteCullingStats stats,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb)
        {
            output.Clear();
            stats = default;

            if (!IsReady || camera == null)
                return false;

            shader.SetInt("_UseGpuSceneRefs", 0);
            shader.SetInt("_UseInstanceCull", 0);
            shader.SetInt("_EnableVisibleInstanceQueue", 0);
            shader.SetInt("_EnableVisibleDrawAppend", 0);

            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            for (int i = 0; i < 6; i++)
            {
                var p = planes[i];
                frustumPlanes[i] = new Vector4(p.normal.x, p.normal.y, p.normal.z, p.distance);
            }

            Vector3 cameraPos = camera.transform.position;
            Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            float projectionScale = Mathf.Abs(gpuProj.m11) * 0.5f * Mathf.Max(1, camera.pixelHeight);
            float zNear = Mathf.Max(1e-3f, camera.nearClipPlane);
            Matrix4x4 worldToClip = gpuProj * camera.worldToCameraMatrix;
            bool enableHzb = useHzb && hzbTexture != null && hzbMipCount > 0;
            bool hasCpuCandidates = BuildCpuClusterCandidates(
                planes,
                cameraPos,
                projectionScale,
                zNear,
                lodErrorPixels,
                localToWorld,
                maxScale,
                out int cpuTestedNodes,
                out int cpuTestedParts,
                out clusterCandidateCount);

            UploadSingleInstance(localToWorld, maxScale, lodErrorPixels);

            if (hasCpuCandidates)
            {
                partVisibleBuffer.SetData(partVisibleCpu);
                if (clusterCandidateCount > 0)
                    clusterCandidateBuffer.SetData(clusterCandidatesCpu, 0, 0, clusterCandidateCount);
            }
            else
            {
                SetSharedParams(kernelPartCull, cameraPos, projectionScale, zNear, worldToClip, camera.pixelWidth, camera.pixelHeight, enableHzb);
                shader.SetInt("_PartCount", partCount);
                shader.SetInt("_CullPassMode", 0);
                shader.SetInt("_HasPrevVisible", 0);
                shader.SetInt("_EnableVisiblePartQueue", 0);
                shader.SetBuffer(kernelPartCull, "_Parts", partsBuffer);
                shader.SetBuffer(kernelPartCull, "_PartVisible", partVisibleBuffer);
                shader.SetBuffer(kernelPartCull, "_VisiblePartsOut", fallbackVisiblePartAppendBuffer);
                BindLegacyVirtualRefs(kernelPartCull);
                BindInstanceBuffers(kernelPartCull);
                BindBevyCompatBuffers(kernelPartCull);
                BindHzb(kernelPartCull, hzbTexture, hzbMipCount, enableHzb);

                int partGroups = (partCount + kThreadGroupSize - 1) / kThreadGroupSize;
                shader.Dispatch(kernelPartCull, partGroups, 1, 1);
            }

            visibleClusterAppendBuffer.SetCounterValue(0);

            SetSharedParams(kernelClusterCull, cameraPos, projectionScale, zNear, worldToClip, camera.pixelWidth, camera.pixelHeight, enableHzb);
            shader.SetInt("_PartCount", partCount);
            shader.SetInt("_ClusterCount", clusterCount);
            shader.SetInt("_ClusterCandidateCount", hasCpuCandidates ? clusterCandidateCount : 0);
            shader.SetInt("_UseClusterCandidates", hasCpuCandidates ? 1 : 0);
            shader.SetInt("_EnableVisibleAppend", 1);
            shader.SetInt("_EnableClusterVisibleWrite", 0);
            shader.SetInt("_SceneClusterCount", 1);
            shader.SetInt("_CullPassMode", 0);
            shader.SetInt("_HasPrevVisible", 0);
            shader.SetInt("_EnableCullStats", 0);
            shader.SetBuffer(kernelClusterCull, "_Clusters", clustersBuffer);
            shader.SetBuffer(kernelClusterCull, "_PartVisible", partVisibleBuffer);
            shader.SetBuffer(kernelClusterCull, "_VisibleClusters", visibleClusterAppendBuffer);
            shader.SetBuffer(kernelClusterCull, "_ClusterCandidates", clusterCandidateBuffer);
            BindLegacyVirtualRefs(kernelClusterCull);
            BindInstanceBuffers(kernelClusterCull);
            BindBevyCompatBuffers(kernelClusterCull);
            BindHzb(kernelClusterCull, hzbTexture, hzbMipCount, enableHzb);

            int clusterWorkCount = hasCpuCandidates ? clusterCandidateCount : clusterCount;
            if (clusterWorkCount > 0)
            {
                int clusterGroups = (clusterWorkCount + kThreadGroupSize - 1) / kThreadGroupSize;
                shader.Dispatch(kernelClusterCull, clusterGroups, 1, 1);
            }

            ComputeBuffer.CopyCount(visibleClusterAppendBuffer, visibleCountBuffer, 0);
            visibleCountBuffer.GetData(visibleCountCpu);
            int visibleCount = (int)visibleCountCpu[0];

            if (visibleCount > 0)
            {
                var refsCpu = new GpuVisibleRef[visibleCount];
                visibleClusterAppendBuffer.GetData(refsCpu, 0, 0, visibleCount);
                for (int i = 0; i < refsCpu.Length; i++)
                {
                    output.Add(new NaniteVisibleClusterRef
                    {
                        pageIndex = (int)refsCpu[i].pageIndex,
                        clusterIndex = (int)refsCpu[i].clusterIndex
                    });
                }
            }

            stats.testedNodes = hasCpuCandidates ? cpuTestedNodes : 0;
            stats.testedInstances = 1;
            stats.testedParts = hasCpuCandidates ? cpuTestedParts : partCount;
            stats.testedClusters = hasCpuCandidates ? clusterCandidateCount : clusterCount;
            stats.visibleClusters = output.Count;
            return true;
        }

        void UploadSingleInstance(Matrix4x4 localToWorld, float maxScale, float lodErrorPixels)
        {
            singleInstanceData[0] = new GpuInstanceData
            {
                localToWorld = localToWorld,
                bounds = sourceMesh != null ? sourceMesh.boundingSphere : Vector4.zero,
                maxScale = maxScale,
                lodErrorPixels = lodErrorPixels,
                partOffset = 0u,
                partCount = (uint)Mathf.Max(0, partCount)
            };
            instanceDataBuffer.SetData(singleInstanceData);
        }

        void BindInstanceBuffers(int kernel)
        {
            shader.SetInt("_InstanceCount", 1);
            shader.SetBuffer(kernel, "_Instances", instanceDataBuffer);
            shader.SetBuffer(kernel, "_InstanceVisible", instanceVisibleBuffer);
        }

        void BindLegacyVirtualRefs(int kernel)
        {
            if (fallbackVirtualRefBuffer == null)
                fallbackVirtualRefBuffer = new ComputeBuffer(1, sizeof(uint) * 4, ComputeBufferType.Structured);
            shader.SetBuffer(kernel, "_VirtualParts", fallbackVirtualRefBuffer);
            shader.SetBuffer(kernel, "_VirtualClusters", fallbackVirtualRefBuffer);
        }

        void SetSharedParams(
            int kernel,
            Vector3 cameraPos,
            float projectionScale,
            float zNear,
            Matrix4x4 worldToClip,
            int screenWidth,
            int screenHeight,
            bool useHzb)
        {
            shader.SetVector("_CameraPos", new Vector4(cameraPos.x, cameraPos.y, cameraPos.z, 0f));
            shader.SetFloat("_ProjectionScale", projectionScale);
            shader.SetFloat("_ZNear", zNear);
            shader.SetMatrix("_WorldToClip", worldToClip);
            shader.SetVector("_ScreenSize", new Vector4(Mathf.Max(1, screenWidth), Mathf.Max(1, screenHeight), 1f / Mathf.Max(1, screenWidth), 1f / Mathf.Max(1, screenHeight)));
            shader.SetInt("_UseHzb", useHzb ? 1 : 0);
            shader.SetInt("_ReversedZ", SystemInfo.usesReversedZBuffer ? 1 : 0);
            shader.SetVectorArray("_FrustumPlanes", frustumPlanes);
        }

        bool BuildCpuClusterCandidates(
            Plane[] planes,
            Vector3 cameraPos,
            float projectionScale,
            float zNear,
            float lodErrorPixels,
            Matrix4x4 localToWorld,
            float maxScale,
            out int testedNodes,
            out int testedParts,
            out int candidateCount)
        {
            testedNodes = 0;
            testedParts = 0;
            candidateCount = 0;

            if (sourceMesh == null || sourceMesh.pageArray == null || partDataCpu == null || partVisibleCpu == null || clusterCandidatesCpu == null)
                return false;
            if (pagePartBase == null || pagePartCount == null || pagePartBase.Length != sourceMesh.pageArray.Length)
                return false;

            bool hasAnyBvh = false;
            for (int i = 0; i < sourceMesh.pageArray.Length; i++)
            {
                var page = sourceMesh.pageArray[i];
                if (page != null && page.bvhNodes != null && page.bvhNodes.Length > 0 && page.bvhRoot >= 0)
                {
                    hasAnyBvh = true;
                    break;
                }
            }
            if (!hasAnyBvh)
                return false;

            System.Array.Clear(partVisibleCpu, 0, partVisibleCpu.Length);
            int write = 0;

            for (int pageIndex = 0; pageIndex < sourceMesh.pageArray.Length; pageIndex++)
            {
                var page = sourceMesh.pageArray[pageIndex];
                if (page == null || page.parts == null)
                    continue;

                int partBase = pagePartBase[pageIndex];
                int partCountInPage = pagePartCount[pageIndex];
                if (partBase < 0 || partCountInPage <= 0)
                    continue;

                if (page.bvhNodes != null && page.bvhNodes.Length > 0 && page.bvhRoot >= 0)
                {
                    bvhStack.Clear();
                    bvhStack.Add(page.bvhRoot);

                    while (bvhStack.Count > 0)
                    {
                        int idx = bvhStack[bvhStack.Count - 1];
                        bvhStack.RemoveAt(bvhStack.Count - 1);
                        if (idx < 0 || idx >= page.bvhNodes.Length)
                            continue;

                        ref readonly var node = ref page.bvhNodes[idx];
                        testedNodes++;

                        Vector4 worldNode = TransformSphere(node.sphere, localToWorld, maxScale);
                        if (!SphereVisible(worldNode, planes))
                            continue;

                        Vector4 lodSphereLocal = node.lodSphere.w > 0f ? node.lodSphere : node.sphere;
                        Vector4 worldLod = TransformSphere(lodSphereLocal, localToWorld, maxScale);
                        float nodeError = ProjectedErrorPixels(node.maxParentLodError, worldLod, cameraPos, projectionScale, zNear);
                        if (nodeError <= lodErrorPixels)
                            continue;

                        if (node.partIndex >= 0)
                        {
                            int localPart = node.partIndex;
                            if (localPart < 0 || localPart >= partCountInPage)
                                continue;
                            int globalPart = partBase + localPart;
                            testedParts++;
                            if (!PartVisibleForLod(partDataCpu[globalPart], planes, cameraPos, projectionScale, zNear, lodErrorPixels, localToWorld, maxScale))
                                continue;
                            if (partVisibleCpu[globalPart] == 0)
                            {
                                partVisibleCpu[globalPart] = 1;
                                write = AppendPartClusters(globalPart, write);
                            }
                        }
                        else
                        {
                            if (node.child0 >= 0) bvhStack.Add(node.child0);
                            if (node.child1 >= 0) bvhStack.Add(node.child1);
                            if (node.child2 >= 0) bvhStack.Add(node.child2);
                            if (node.child3 >= 0) bvhStack.Add(node.child3);
                        }
                    }
                }
                else
                {
                    for (int localPart = 0; localPart < partCountInPage; localPart++)
                    {
                        int globalPart = partBase + localPart;
                        testedParts++;
                        if (!PartVisibleForLod(partDataCpu[globalPart], planes, cameraPos, projectionScale, zNear, lodErrorPixels, localToWorld, maxScale))
                            continue;
                        if (partVisibleCpu[globalPart] == 0)
                        {
                            partVisibleCpu[globalPart] = 1;
                            write = AppendPartClusters(globalPart, write);
                        }
                    }
                }
            }

            candidateCount = write;
            return true;
        }

        int AppendPartClusters(int globalPart, int write)
        {
            ref readonly var part = ref partDataCpu[globalPart];
            int start = part.clusterStart;
            int end = start + part.clusterCount;
            for (int i = start; i < end && write < clusterCandidatesCpu.Length; i++)
                clusterCandidatesCpu[write++] = (uint)i;
            return write;
        }

        static bool PartVisibleForLod(
            in GpuPartData part,
            Plane[] planes,
            Vector3 cameraPos,
            float projectionScale,
            float zNear,
            float lodErrorPixels,
            Matrix4x4 localToWorld,
            float maxScale)
        {
            Vector4 worldPart = TransformSphere(part.selfSphere, localToWorld, maxScale);
            if (!SphereVisible(worldPart, planes))
                return false;

            Vector4 lodSphereLocal = part.parentSphere.w > 0f ? part.parentSphere : part.selfSphere;
            Vector4 worldLod = TransformSphere(lodSphereLocal, localToWorld, maxScale);
            float partError = ProjectedErrorPixels(part.maxParentError, worldLod, cameraPos, projectionScale, zNear);
            return partError > lodErrorPixels;
        }

        static Vector4 TransformSphere(in Vector4 localSphere, Matrix4x4 localToWorld, float maxScale)
        {
            Vector3 worldCenter = localToWorld.MultiplyPoint3x4(new Vector3(localSphere.x, localSphere.y, localSphere.z));
            float worldRadius = localSphere.w * Mathf.Max(1e-6f, maxScale);
            return new Vector4(worldCenter.x, worldCenter.y, worldCenter.z, worldRadius);
        }

        static bool SphereVisible(in Vector4 sphere, Plane[] planes)
        {
            var center = new Vector3(sphere.x, sphere.y, sphere.z);
            float radius = sphere.w;
            for (int i = 0; i < planes.Length; i++)
            {
                if (planes[i].GetDistanceToPoint(center) < -radius)
                    return false;
            }
            return true;
        }

        static float ProjectedErrorPixels(float error, in Vector4 sphere, Vector3 cameraPosition, float projectionScale, float zNear)
        {
            if (error >= float.MaxValue * 0.5f)
                return float.PositiveInfinity;
            var center = new Vector3(sphere.x, sphere.y, sphere.z);
            float distance = Vector3.Distance(center, cameraPosition) - sphere.w;
            distance = Mathf.Max(distance, zNear);
            return (2f * error * projectionScale) / distance;
        }

        bool TryFindKernels()
        {
            try
            {
                kernelPartCull = shader.FindKernel("CSPartCull");
                kernelClusterCull = shader.FindKernel("CSClusterCull");
                return true;
            }
            catch
            {
                return false;
            }
        }

        void BuildGpuData(
            NaniteMesh mesh,
            out List<GpuPartData> parts,
            out List<GpuClusterData> clusters,
            out int[] outPagePartBase,
            out int[] outPagePartCount)
        {
            parts = new List<GpuPartData>(2048);
            clusters = new List<GpuClusterData>(16384);
            outPagePartBase = new int[mesh.pageArray.Length];
            outPagePartCount = new int[mesh.pageArray.Length];
            for (int i = 0; i < outPagePartBase.Length; i++)
            {
                outPagePartBase[i] = -1;
                outPagePartCount[i] = 0;
            }

            for (int pageIndex = 0; pageIndex < mesh.pageArray.Length; pageIndex++)
            {
                var page = mesh.pageArray[pageIndex];
                if (page == null || page.parts == null || page.clusterArray == null)
                    continue;

                int partBase = parts.Count;
                int clusterBase = clusters.Count;
                outPagePartBase[pageIndex] = partBase;
                outPagePartCount[pageIndex] = page.parts.Length;

                int[] clusterToPart = new int[page.clusterArray.Length];
                for (int i = 0; i < clusterToPart.Length; i++)
                    clusterToPart[i] = -1;

                for (int pi = 0; pi < page.parts.Length; pi++)
                {
                    var part = page.parts[pi];
                    int start = part.clusterStart;
                    int end = part.clusterStart + part.clusterCount;
                    if (start >= 0 && end <= clusterToPart.Length)
                    {
                        for (int ci = start; ci < end; ci++)
                            clusterToPart[ci] = pi;
                    }

                    parts.Add(new GpuPartData
                    {
                        selfSphere = part.selfSphere,
                        parentSphere = part.parentSphere.w > 0f ? part.parentSphere : part.selfSphere,
                        maxParentError = part.maxParentLodError,
                        clusterStart = clusterBase + part.clusterStart,
                        clusterCount = part.clusterCount,
                        instanceIndex = 0
                    });
                }

                for (int ci = 0; ci < page.clusterArray.Length; ci++)
                {
                    var c = page.clusterArray[ci];
                    int localPart = c.partIndex;
                    if (localPart < 0 || localPart >= page.parts.Length)
                        localPart = clusterToPart[ci];

                    int globalPart = (localPart >= 0) ? (partBase + localPart) : -1;
                    clusters.Add(new GpuClusterData
                    {
                        selfSphere = c.selfSphere,
                        parentSphere = c.parentSphere.w > 0f ? c.parentSphere : c.selfSphere,
                        selfError = c.selfError,
                        parentError = c.parentError,
                        partIndex = globalPart,
                        pageIndex = pageIndex,
                        clusterIndex = ci,
                        instanceIndex = 0
                    });
                }
            }
        }

        void ReleaseBuffers()
        {
            partsBuffer?.Release();
            clustersBuffer?.Release();
            partVisibleBuffer?.Release();
            visibleClusterAppendBuffer?.Release();
            visibleCountBuffer?.Release();
            clusterCandidateBuffer?.Release();
            instanceDataBuffer?.Release();
            instanceVisibleBuffer?.Release();
            fallbackUintBuffer?.Release();
            fallbackCullStatsBuffer?.Release();
            fallbackVirtualRefBuffer?.Release();
            fallbackVisiblePartAppendBuffer?.Release();
            fallbackVisibleDrawAppendBuffer?.Release();

            partsBuffer = null;
            clustersBuffer = null;
            partVisibleBuffer = null;
            visibleClusterAppendBuffer = null;
            visibleCountBuffer = null;
            clusterCandidateBuffer = null;
            instanceDataBuffer = null;
            instanceVisibleBuffer = null;
            fallbackUintBuffer = null;
            fallbackCullStatsBuffer = null;
            fallbackVirtualRefBuffer = null;
            fallbackVisiblePartAppendBuffer = null;
            fallbackVisibleDrawAppendBuffer = null;
        }

        public static void BuildGpuData(
            NaniteMesh mesh,
            int instanceIndex,
            List<GpuPartData> parts,
            List<GpuClusterData> clusters,
            out int[] outPagePartBase,
            out int[] outPagePartCount)
        {
            outPagePartBase = new int[mesh.pageArray.Length];
            outPagePartCount = new int[mesh.pageArray.Length];
            for (int i = 0; i < outPagePartBase.Length; i++)
            {
                outPagePartBase[i] = -1;
                outPagePartCount[i] = 0;
            }

            for (int pageIndex = 0; pageIndex < mesh.pageArray.Length; pageIndex++)
            {
                var page = mesh.pageArray[pageIndex];
                if (page == null || page.parts == null || page.clusterArray == null)
                    continue;

                int partBase = parts.Count;
                int clusterBase = clusters.Count;
                outPagePartBase[pageIndex] = partBase;
                outPagePartCount[pageIndex] = page.parts.Length;

                int[] clusterToPart = new int[page.clusterArray.Length];
                for (int i = 0; i < clusterToPart.Length; i++)
                    clusterToPart[i] = -1;

                for (int pi = 0; pi < page.parts.Length; pi++)
                {
                    var part = page.parts[pi];
                    int start = part.clusterStart;
                    int end = part.clusterStart + part.clusterCount;
                    if (start >= 0 && end <= clusterToPart.Length)
                    {
                        for (int ci = start; ci < end; ci++)
                            clusterToPart[ci] = pi;
                    }

                    parts.Add(new GpuPartData
                    {
                        selfSphere = part.selfSphere,
                        parentSphere = part.parentSphere.w > 0f ? part.parentSphere : part.selfSphere,
                        maxParentError = part.maxParentLodError,
                        clusterStart = clusterBase + part.clusterStart,
                        clusterCount = part.clusterCount,
                        instanceIndex = instanceIndex
                    });
                }

                for (int ci = 0; ci < page.clusterArray.Length; ci++)
                {
                    var c = page.clusterArray[ci];
                    int localPart = c.partIndex;
                    if (localPart < 0 || localPart >= page.parts.Length)
                        localPart = clusterToPart[ci];

                    int globalPart = (localPart >= 0) ? (partBase + localPart) : -1;
                    clusters.Add(new GpuClusterData
                    {
                        selfSphere = c.selfSphere,
                        parentSphere = c.parentSphere.w > 0f ? c.parentSphere : c.selfSphere,
                        selfError = c.selfError,
                        parentError = c.parentError,
                        partIndex = globalPart,
                        pageIndex = pageIndex,
                        clusterIndex = ci,
                        instanceIndex = instanceIndex
                    });
                }
            }
        }

        void BindBevyCompatBuffers(int kernel)
        {
            EnsureFallbackUintBuffers();
            // The shared runtime culling shader declares Page streaming resources for its
            // cluster kernels. This legacy per-proxy path intentionally has no global Page
            // table, but Unity still requires every referenced UAV/SRV to be bound even when
            // _EnablePageRequests is zero.
            shader.SetInt("_PageCount", 0);
            shader.SetInt("_EnablePageRequests", 0);
            shader.SetInt("_TrackPageUsage", 0);
            shader.SetBuffer(kernel, "_PageResidency", fallbackUintBuffer);
            shader.SetBuffer(kernel, "_PageRequests", fallbackUintBuffer);
            shader.SetBuffer(kernel, "_ClusterVisible", fallbackUintBuffer);
            shader.SetBuffer(kernel, "_ClusterSceneIndex", fallbackUintBuffer);
            shader.SetBuffer(kernel, "_PrevClusterVisible", fallbackUintBuffer);
            shader.SetBuffer(kernel, "_SecondPassCandidates", fallbackUintBuffer);
            shader.SetBuffer(kernel, "_Pass2Drawn", fallbackUintBuffer);
            shader.SetBuffer(kernel, "_CullStats", fallbackCullStatsBuffer);
            shader.SetBuffer(kernel, "_VisibleDrawClusters", fallbackVisibleDrawAppendBuffer);
            shader.SetBuffer(kernel, "_SceneClusterFirstTri", fallbackUintBuffer);
            shader.SetBuffer(kernel, "_SceneClusterTriCount", fallbackUintBuffer);
        }

        void EnsureFallbackUintBuffers()
        {
            if (fallbackUintBuffer == null)
            {
                fallbackUintBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);
                fallbackUintBuffer.SetData(new uint[] { 0u });
            }

            if (fallbackCullStatsBuffer == null)
            {
                fallbackCullStatsBuffer = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.Structured);
                fallbackCullStatsBuffer.SetData(new uint[] { 0u, 0u, 0u });
            }
        }

        void BindHzb(int kernel, Texture hzbTexture, int hzbMipCount, bool enableHzb)
        {
            if (enableHzb)
            {
                shader.SetInt("_UseHzb", 1);
                shader.SetInt("_HizMipCount", Mathf.Max(1, hzbMipCount));
                shader.SetTexture(kernel, "_HizTexture", hzbTexture);
                return;
            }

            shader.SetInt("_UseHzb", 0);
            shader.SetInt("_HizMipCount", 1);
            shader.SetTexture(kernel, "_HizTexture", GetFallbackHzbTexture());
        }

        Texture GetFallbackHzbTexture()
        {
            if (fallbackHzbTexture != null)
                return fallbackHzbTexture;

            fallbackHzbTexture = new RenderTexture(1, 1, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = "Nanite_Fallback_HZB",
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = false,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            fallbackHzbTexture.Create();
            return fallbackHzbTexture;
        }

        void ReleaseFallbackHzbTexture()
        {
            if (fallbackHzbTexture == null)
                return;
            fallbackHzbTexture.Release();
            fallbackHzbTexture = null;
        }
    }
}
