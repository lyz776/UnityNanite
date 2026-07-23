using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Nanite
{
    /// <summary>
    /// 与 meshoptimizer 中 <c>meshopt_Meshlet</c> 内存布局一致（4×uint32）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Meshlet
    {
        public uint vertex_offset;
        public uint triangle_offset;
        public uint vertex_count;
        public uint triangle_count;
    }

    /// <summary>
    /// 簇在屏幕/视角下的包围球与几何误差（后续 Build DAG / LOD 会用到）。
    /// </summary>
    [Serializable]
    public struct LODBounds
    {
        public Vector3 center;
        public float radius;
        public float error;
    }

    /// <summary>
    /// 合并/简化后的一组子 Cluster，构成 DAG 中的一个节点（Cluster Group）。
    /// </summary>
    [Serializable]
    public class ClusterGroup
    {
        /// <summary>本组包含的叶子/子 Cluster 在 <see cref="NaniteSubMesh.clusterList"/> 中的索引。</summary>
        public List<int> children = new List<int>();
        public Vector3 boundsCenter;
        public float radius;
        public float minLodError;
        public float maxParentLodError;
        public int mipLevel;
    }

    /// <summary>离线 Nanite 构建结果：全部 Cluster + 各级 Cluster Group + 最大 Mip。</summary>
    [Serializable]
    public class NaniteSubMesh
    {
        public List<ClusterGroup> clusterGroupList = new List<ClusterGroup>();
        public List<Cluster> clusterList = new List<Cluster>();
        public int maxMipLevel;
    }

    /// <summary>
    /// 单个 Cluster：引用原始网格顶点索引的三角形列表，以及自身/父级边界信息。
    /// </summary>
    [Serializable]
    public class Cluster
    {
        public int mip;
        public int[] indices = Array.Empty<int>();
        public LODBounds self;
        public LODBounds parent;
    }
}
