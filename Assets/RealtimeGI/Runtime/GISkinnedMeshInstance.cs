using UnityEngine;

namespace RealtimeGI
{
    /// <summary>
    /// Budgeted SkinnedMeshRenderer adapter. BakeMesh writes into one persistent Mesh and the
    /// result always enters the Dynamic Overlay; use a simplified shadow/GI renderer for crowds.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SkinnedMeshRenderer))]
    public sealed class GISkinnedMeshInstance : MonoBehaviour
    {
        [Min(1)] public int updateEveryFrames = 2;
        [Min(0f)] public float fullRateDistance = 35f;
        [Min(1)] public int farUpdateMultiplier = 4;
        public bool updateInEditMode;

        [Header("Read-only statistics")]
        [SerializeField, Min(0)] int geometryRevision;
        [SerializeField] int lastBakeFrame = -1;

        SkinnedMeshRenderer sourceRenderer;
        Mesh bakedMesh;
        bool hasValidBake;

        public SkinnedMeshRenderer SourceRenderer
        {
            get
            {
                if (sourceRenderer == null) sourceRenderer = GetComponent<SkinnedMeshRenderer>();
                return sourceRenderer;
            }
        }

        public GIObjectSettings Settings => GetComponent<GIObjectSettings>();
        public int GeometryRevision => geometryRevision;
        public bool ShouldGather => isActiveAndEnabled && SourceRenderer != null &&
                                    SourceRenderer.enabled && SourceRenderer.sharedMesh != null;

        public Material[] ResolveMaterials() => SourceRenderer != null
            ? SourceRenderer.sharedMaterials
            : System.Array.Empty<Material>();

        public bool TryGetBakedMesh(out Mesh mesh)
        {
            mesh = null;
            if (!ShouldGather)
                return false;
            EnsureMesh();
            if (!hasValidBake || ShouldBakeNow())
            {
                SourceRenderer.BakeMesh(bakedMesh, true);
                bakedMesh.RecalculateBounds();
                hasValidBake = bakedMesh.vertexCount > 0;
                lastBakeFrame = Time.frameCount;
                unchecked { geometryRevision++; }
            }
            mesh = hasValidBake ? bakedMesh : null;
            return mesh != null;
        }

        bool ShouldBakeNow()
        {
            if (!Application.isPlaying)
                return updateInEditMode;
            int interval = Mathf.Max(1, updateEveryFrames);
            Camera camera = Camera.main;
            if (camera != null && fullRateDistance > 0f &&
                Vector3.Distance(camera.transform.position, transform.position) > fullRateDistance)
                interval *= Mathf.Max(1, farUpdateMultiplier);
            return lastBakeFrame < 0 || Time.frameCount - lastBakeFrame >= interval;
        }

        void EnsureMesh()
        {
            if (bakedMesh != null)
                return;
            bakedMesh = new Mesh
            {
                name = $"{name} GI Skinned Proxy",
                hideFlags = HideFlags.HideAndDontSave
            };
            bakedMesh.MarkDynamic();
        }

        void OnEnable()
        {
            sourceRenderer = GetComponent<SkinnedMeshRenderer>();
            EnsureMesh();
            GISceneRegistry.Register(this);
        }

        void OnDisable()
        {
            GISceneRegistry.Unregister(this);
            ReleaseMesh();
        }

        void OnDestroy()
        {
            GISceneRegistry.Unregister(this);
            ReleaseMesh();
        }

        void OnValidate()
        {
            updateEveryFrames = Mathf.Max(1, updateEveryFrames);
            farUpdateMultiplier = Mathf.Max(1, farUpdateMultiplier);
            lastBakeFrame = -1;
            GISceneRegistry.NotifyChanged();
        }

        void ReleaseMesh()
        {
            if (bakedMesh == null)
                return;
            if (Application.isPlaying) Destroy(bakedMesh);
            else DestroyImmediate(bakedMesh);
            bakedMesh = null;
            hasValidBake = false;
        }
    }
}
