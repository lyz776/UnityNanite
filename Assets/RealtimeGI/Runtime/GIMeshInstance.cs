using Nanite;
using UnityEngine;

namespace RealtimeGI
{
    /// <summary>Registers an ordinary MeshFilter/MeshRenderer with the unified GI scene.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    public sealed class GIMeshInstance : MonoBehaviour
    {
        [Tooltip("Avoids registering the same object twice when a NaniteRuntimeProxy owns it.")]
        public bool ignoreWhenNaniteProxyPresent = true;
        [Min(0)] public int geometryRevision;

        MeshFilter meshFilter;
        Renderer sourceRenderer;

        public Mesh SharedMesh
        {
            get
            {
                if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
                return meshFilter != null ? meshFilter.sharedMesh : null;
            }
        }

        public Renderer SourceRenderer
        {
            get
            {
                if (sourceRenderer == null) sourceRenderer = GetComponent<Renderer>();
                return sourceRenderer;
            }
        }

        public bool ShouldGather
        {
            get
            {
                if (!isActiveAndEnabled || SharedMesh == null)
                    return false;
                if (ignoreWhenNaniteProxyPresent && TryGetComponent(out NaniteRuntimeProxy proxy) &&
                    proxy.isActiveAndEnabled && proxy.naniteMesh != null)
                    return false;
                return SourceRenderer == null || SourceRenderer.enabled;
            }
        }

        public GIObjectSettings Settings => GetComponent<GIObjectSettings>();

        public Material[] ResolveMaterials()
        {
            Renderer renderer = SourceRenderer;
            return renderer != null ? renderer.sharedMaterials : System.Array.Empty<Material>();
        }

        void OnEnable()
        {
            meshFilter = GetComponent<MeshFilter>();
            sourceRenderer = GetComponent<Renderer>();
            GISceneRegistry.Register(this);
        }

        void OnDisable()
        {
            GISceneRegistry.Unregister(this);
        }

        void OnDestroy()
        {
            GISceneRegistry.Unregister(this);
        }

        void OnValidate()
        {
            geometryRevision = Mathf.Max(0, geometryRevision);
            meshFilter = GetComponent<MeshFilter>();
            sourceRenderer = GetComponent<Renderer>();
            GISceneRegistry.NotifyChanged();
        }
    }
}
