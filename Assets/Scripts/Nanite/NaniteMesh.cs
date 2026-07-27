using System;
using UnityEngine;

namespace Nanite
{
    /// <summary>整 mesh 的 Nanite 离线资源入口，引用多个 Page。</summary>
    [CreateAssetMenu(fileName = "NaniteMesh", menuName = "Nanite/Mesh")]
    public class NaniteMesh : ScriptableObject
    {
        [Tooltip("原始 Unity Mesh。用于低复杂度对象自动回退到 SRP Batcher / GPU Instancing。")]
        public Mesh sourceMesh;
        [Min(0)] public int sourceTriangleCount;
        public Vector4 boundingSphere;
        public int subMeshCount;
        public int maxMipLevel;
        public NaniteMeshPage[] pageArray = Array.Empty<NaniteMeshPage>();
        public NanitePageStreamingInfo[] pageStreamingInfo = Array.Empty<NanitePageStreamingInfo>();
        public int hierarchyVersion;
        public NaniteHierarchyGroup[] hierarchyGroups = Array.Empty<NaniteHierarchyGroup>();
        public NaniteHierarchyClusterRef[] hierarchyClusterRefs = Array.Empty<NaniteHierarchyClusterRef>();
        public int[] hierarchyRootGroups = Array.Empty<int>();
    }
}
