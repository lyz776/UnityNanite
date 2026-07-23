using System;
using UnityEngine;

namespace Nanite
{
    /// <summary>整 mesh 的 Nanite 离线资源入口，引用多个 Page。</summary>
    [CreateAssetMenu(fileName = "NaniteMesh", menuName = "Nanite/Mesh")]
    public class NaniteMesh : ScriptableObject
    {
        public Vector4 boundingSphere;
        public int subMeshCount;
        public int maxMipLevel;
        public NaniteMeshPage[] pageArray = Array.Empty<NaniteMeshPage>();
    }
}
