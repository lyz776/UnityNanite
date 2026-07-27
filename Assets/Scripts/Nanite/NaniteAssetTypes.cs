using System;
using UnityEngine;

namespace Nanite
{
    /// <summary>运行时/GPU 用的紧凑 Cluster 描述（Page 内局部索引）。</summary>
    [Serializable]
    public struct NaniteCluster
    {
        public int indiceIndex;
        public int indiceCount;
        public float selfError;
        public float parentError;
        public Vector4 selfSphere;
        public Vector4 parentSphere;
        public int subMeshId;
        public int partIndex; // Page 内 local part 索引（运行时 GPU culling 用）
        public int vertexOffset;
    }

    /// <summary>剔除单元：最多若干 Cluster 先做一次 Part 级粗剔除。</summary>
    [Serializable]
    public struct NaniteMeshPart
    {
        public int clusterStart;
        public int clusterCount;
        public int mipLevel;
        public Vector4 selfSphere;
        public Vector4 parentSphere; // 由本 Part 内 cluster.parentSphere 合并，供保守 LOD 早停
        public float maxParentLodError;
    }

    /// <summary>4 叉 BVH 节点（仿 UE Nanite 节点/簇剔除结构）。</summary>
    [Serializable]
    public struct NaniteBvhNode
    {
        public Vector4 sphere;    // 用于 frustum（基于 selfSphere 构建）
        public Vector4 lodSphere; // 用于 LOD 早停（基于 parentSphere 构建）
        public float maxParentLodError;
        public int child0;
        public int child1;
        public int child2;
        public int child3;
        public int childCount;
        public int partIndex; // 叶子节点有效，其它为 -1
    }

    /// <summary>常驻的轻量 Page 地址/驻留元数据；几何 payload 保存在独立二进制资源中。</summary>
    [Serializable]
    public struct NanitePageStreamingInfo
    {
        public int pageIndex;
        public int packedBytes;
        public int storageBytes;
        public int vertexBytes;
        public int indexBytes;
        public int hierarchyBytes;
        public int minMip;
        public int maxMip;
        public int flags;
        public Vector4 boundingSphere;

        public bool IsRootPage => (flags & 1) != 0;
    }

    /// <summary>
    /// Stable reference from the mesh-level hierarchy into Page-local geometry. The flattened
    /// geometry index follows Page/cluster order and is also the order used by GPU Scene.
    /// </summary>
    [Serializable]
    public struct NaniteHierarchyClusterRef
    {
        public int geometryClusterIndex;
        public int pageIndex;
        public int pageClusterIndex;
        public int refinementGroupIndex;
    }

    /// <summary>
    /// One DAG refinement edge. Coarse clusters can be replaced atomically by fine clusters
    /// only when the complete fine Page working set is resident. Root-set records have no
    /// coarse range and seed top-down traversal from permanently resident Pages.
    /// </summary>
    [Serializable]
    public struct NaniteHierarchyGroup
    {
        public const int CurrentVersion = 1;
        public const int RootSetFlag = 1;

        public Vector4 boundingSphere;
        public float minLodError;
        public float maxParentLodError;
        public int fineClusterStart;
        public int fineClusterCount;
        public int coarseClusterStart;
        public int coarseClusterCount;
        public int mipLevel;
        public int flags;

        public bool IsRootSet => (flags & RootSetFlag) != 0;
    }
}
