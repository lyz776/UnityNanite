using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Nanite
{
    /// <summary>单个可见簇的运行时打包信息（可直接用于后续 GPU 上传/光栅化调度）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NaniteVisibleClusterPacket
    {
        public int pageIndex;
        public int clusterIndex;
        public int instanceId;
        public int subMeshId;
        public int mipLevel;
        public int indexOffset;
        public int indexCount;
        public int vertexOffset;
    }

    /// <summary>同一页内可见簇在 packet 数组中的连续范围。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NaniteVisiblePageRange
    {
        public int pageIndex;
        public int start;
        public int count;
    }

    /// <summary>一帧 culling + lod 选择结果。</summary>
    public sealed class NaniteRuntimeSelection
    {
        public readonly List<NaniteVisibleClusterRef> visibleClusters = new List<NaniteVisibleClusterRef>(4096);
        public readonly List<NaniteVisibleClusterPacket> packets = new List<NaniteVisibleClusterPacket>(4096);
        public readonly List<NaniteVisiblePageRange> pageRanges = new List<NaniteVisiblePageRange>(128);
        public Matrix4x4 instanceLocalToWorld = Matrix4x4.identity;
        public NaniteCullingStats stats;

        public void Clear()
        {
            visibleClusters.Clear();
            packets.Clear();
            pageRanges.Clear();
            instanceLocalToWorld = Matrix4x4.identity;
            stats = default;
        }
    }
}
