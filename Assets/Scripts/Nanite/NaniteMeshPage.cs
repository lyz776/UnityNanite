using System;
using UnityEngine;

namespace Nanite
{
    /// <summary>一块可流式加载的 Page：若干 Part + 本页 Cluster/索引/顶点数据 + BVH。</summary>
    [CreateAssetMenu(fileName = "NaniteMeshPage", menuName = "Nanite/Mesh Page")]
    public class NaniteMeshPage : ScriptableObject
    {
        public NaniteMeshPart[] parts = Array.Empty<NaniteMeshPart>();
        public NaniteCluster[] clusterArray = Array.Empty<NaniteCluster>();
        public int[] indiceArray = Array.Empty<int>();
        public int[] clusterMip = Array.Empty<int>();

        public NaniteBvhNode[] bvhNodes = Array.Empty<NaniteBvhNode>();
        public int bvhRoot = -1;
        public int[] mipBvhRoots = Array.Empty<int>();

        public float[] vertexData = Array.Empty<float>();
        // Layout: position.xyz + uv.xy + normal.xyz + tangent.xyzw
        public int vertexStride = 12;
        public int vertexCount;
    }
}
